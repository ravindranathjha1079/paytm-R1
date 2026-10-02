#!/usr/bin/env bash
# Removes everything bootstrap.sh installed, and nothing else.  seatres-teardown [--dry-run]
set -euo pipefail
DRY=${1:-}
DIR=/opt/seatres
run() { if [ "$DRY" = "--dry-run" ]; then echo "would run: $*"; else echo "+ $*"; "$@" || true; fi; }

if [ -f "$DIR/compose.yaml" ]; then
  run docker compose --project-directory "$DIR" -f "$DIR/compose.yaml" -f "$DIR/deploy/compose.prod.yaml" \
    --env-file "$DIR/.env" -p seatres down -v --rmi local --remove-orphans
fi
run docker image rm seatres-api:prod   # our own image only; shared base images (postgres, caddy) stay
run systemctl disable --now seatres-net-check.timer seatres-net.service
run /usr/local/sbin/seatres-net down
run /usr/local/sbin/seatres-sysctl restore
run rm -f /etc/systemd/system/seatres-net.service /etc/systemd/system/seatres-net-check.service \
  /etc/systemd/system/seatres-net-check.timer /etc/sysctl.d/99-seatres.conf /usr/local/sbin/seatres-net   /usr/local/sbin/seatres-sysctl
run rm -rf /var/lib/seatres
run rm -rf "$DIR"
# Last: the teardown units themselves (this script is running from the service, so do not stop it).
run systemctl disable seatres-teardown.timer
run rm -f /etc/systemd/system/seatres-teardown.timer /etc/systemd/system/seatres-teardown.service /usr/local/sbin/seatres-teardown
run systemctl daemon-reload
echo "seatres removed from this VM"
