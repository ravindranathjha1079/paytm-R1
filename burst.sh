#!/usr/bin/env bash
# One-command on-sale stampede against a running service.
#   ADMIN_KEY=... ./burst.sh https://seatres-paytm.centralindia.cloudapp.azure.com [--overload 50000] [--workers 4]
set -euo pipefail
BASE_URL="${1:?usage: ./burst.sh <BASE_URL> [burst.py options]}"
shift
DIR="$(cd "$(dirname "$0")/tools/burst" && pwd)"
PY="${PYTHON:-python3}"
command -v "$PY" >/dev/null 2>&1 || PY=python
if [ ! -d "$DIR/.venv" ]; then
  "$PY" -m venv "$DIR/.venv"
fi
if [ -f "$DIR/.venv/bin/activate" ]; then . "$DIR/.venv/bin/activate"; else . "$DIR/.venv/Scripts/activate"; fi
pip install -q -r "$DIR/requirements.txt"
exec python "$DIR/burst.py" "$BASE_URL" "$@"
