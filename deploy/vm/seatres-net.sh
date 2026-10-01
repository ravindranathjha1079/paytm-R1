#!/usr/bin/env bash
# seatres-net up|down|status
# Routes the seatres public IP to our own Caddy without touching anything else on this VM:
#   - adds the secondary private IP (10.0.0.10) to eth0, and
#   - redirects TCP 80/443 *destined to that IP only* to our Caddy on 9080/9443.
# senti's Caddy keeps owning 80/443 on the primary IP. Idempotent; "up" also restores rule order.
set -euo pipefail
IP=${SEATRES_PRIVATE_IP:-10.0.0.10}
DEV=${SEATRES_DEV:-eth0}
PAIRS=("80 9080" "443 9443")

rule() { echo "-d $IP/32 -p tcp --dport $1 -m comment --comment seatres -j REDIRECT --to-ports $2"; }

remove_rules() {
  for pair in "${PAIRS[@]}"; do
    set -- $pair
    # shellcheck disable=SC2046
    while iptables -t nat -C PREROUTING $(rule "$1" "$2") 2>/dev/null; do
      iptables -t nat -D PREROUTING $(rule "$1" "$2")
    done
  done
}

up() {
  ip -4 addr show dev "$DEV" | grep -q "inet $IP/" || ip addr add "$IP/24" dev "$DEV"
  # Re-insert at the top every time so Docker's own DNAT rules can never shadow ours.
  remove_rules
  for pair in "${PAIRS[@]}"; do
    set -- $pair
    # shellcheck disable=SC2046
    iptables -t nat -I PREROUTING 1 $(rule "$1" "$2")
  done
}

down() {
  remove_rules
  if ip -4 addr show dev "$DEV" | grep -q "inet $IP/"; then ip addr del "$IP/24" dev "$DEV"; fi
}

case "${1:-up}" in
  up) up ;;
  down) down ;;
  status) ip -4 addr show dev "$DEV" | grep "inet $IP/" || true; iptables -t nat -S PREROUTING | grep seatres || true ;;
  *) echo "usage: $0 up|down|status" >&2; exit 2 ;;
esac
