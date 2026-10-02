#!/usr/bin/env bash
# seatres-sysctl raise|restore — raise accept-queue limits for bursts, never lowering a value another app set.
# The previous values are saved and restored at teardown. Runtime only: nothing is written to /etc/sysctl.d.
set -euo pipefail
STATE=/var/lib/seatres/sysctl.prev
KEYS="net.core.somaxconn net.ipv4.tcp_max_syn_backlog net.core.netdev_max_backlog"
WANT=8192
case "${1:-raise}" in
  raise)
    mkdir -p "$(dirname "$STATE")"
    [ -f "$STATE" ] || for k in $KEYS; do echo "$k=$(sysctl -n "$k")"; done > "$STATE"
    for k in $KEYS; do
      cur=$(sysctl -n "$k")
      if [ "$cur" -lt "$WANT" ]; then sysctl -q -w "$k=$WANT"; fi
    done ;;
  restore)
    if [ -f "$STATE" ]; then
      while IFS== read -r k v; do sysctl -q -w "$k=$v"; done < "$STATE"
      rm -f "$STATE"
    fi ;;
  *) echo "usage: $0 raise|restore" >&2; exit 2 ;;
esac
