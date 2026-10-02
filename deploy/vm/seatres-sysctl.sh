#!/usr/bin/env bash
# seatres-sysctl raise|restore — raise accept-queue limits for bursts, never lowering a value another app set.
# Runtime only (nothing in /etc/sysctl.d); seatres-net.service re-runs "raise" at boot.
# restore puts back the value seen before seatres raised it, but only if the value is still the one seatres
# set: if another app changed it meanwhile, it is left alone.
set -euo pipefail
STATE=/var/lib/seatres/sysctl.prev
KEYS="net.core.somaxconn net.ipv4.tcp_max_syn_backlog net.core.netdev_max_backlog net.netfilter.nf_conntrack_max"
# Sized so a whole 20k-connection wave fits in the accept queue (8192 overflowed ~68k times in testing).
want() {
  case "$1" in
    net.netfilter.nf_conntrack_max) echo 262144 ;;
    net.core.netdev_max_backlog) echo 16384 ;;
    *) echo 65535 ;;
  esac
}
case "${1:-raise}" in
  raise)
    mkdir -p "$(dirname "$STATE")"
    if [ ! -f "$STATE" ]; then
      for k in $KEYS; do echo "$k=$(sysctl -n "$k")"; done > "$STATE"
    fi
    for k in $KEYS; do
      cur=$(sysctl -n "$k")
      if [ "$cur" -lt "$(want "$k")" ]; then sysctl -q -w "$k=$(want "$k")"; fi
    done ;;
  restore)
    if [ -f "$STATE" ]; then
      while IFS== read -r k v; do
        if [ "$(sysctl -n "$k")" = "$(want "$k")" ] && [ "$v" != "$(want "$k")" ]; then sysctl -q -w "$k=$v"; fi
      done < "$STATE"
      rm -f "$STATE"
    fi ;;
  *) echo "usage: $0 raise|restore" >&2; exit 2 ;;
esac
