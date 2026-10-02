#!/usr/bin/env bash
# seatres-net up|down|status
# Routes the seatres public IP to our own Caddy without touching anything else on this VM:
#   - adds the secondary private IP (10.0.0.10/32: no subnet route, so other apps' traffic is unaffected) to eth0, and
#   - redirects TCP 80/443 *destined to that IP only* to our Caddy on 9080/9443.
# senti's Caddy keeps owning 80/443 on the primary IP. Idempotent; "up" also restores rule order.
set -euo pipefail
IP=${SEATRES_PRIVATE_IP:-10.0.0.10}
DEV=${SEATRES_DEV:-eth0}
PAIRS=("80 9080" "443 9443")
# Beyond this many concurrent connections to our Caddy, new ones are refused at once (TCP reset) instead of
# letting the proxy run out of memory and drop every open connection.
MAX_CONNS=${SEATRES_MAX_CONNS:-24000}
cap_rule() { echo "-p tcp --syn --dport 9443 -m connlimit --connlimit-above $MAX_CONNS --connlimit-mask 0 -m comment --comment seatres -j REJECT --reject-with tcp-reset"; }

rule() { echo "-d $IP/32 -p tcp --dport $1 -m comment --comment seatres -j REDIRECT --to-ports $2"; }

remove_cap() {
  # Every INPUT rule seatres ever added (whatever MAX_CONNS it had), identified by its comment.
  iptables -S INPUT | grep -- '--comment seatres' | sed 's/^-A /-D /' | while read -r spec; do
    # shellcheck disable=SC2086
    iptables $spec
  done
}

ensure_cap() {
  # shellcheck disable=SC2046
  if ! iptables -C INPUT $(cap_rule) 2>/dev/null; then
    remove_cap
    iptables -I INPUT 1 $(cap_rule)
  fi
}

remove_rules() {
  for pair in "${PAIRS[@]}"; do
    set -- $pair
    # shellcheck disable=SC2046
    while iptables -t nat -C PREROUTING $(rule "$1" "$2") 2>/dev/null; do
      iptables -t nat -D PREROUTING $(rule "$1" "$2")
    done
  done
}

in_order() {
  # Our two rules must be the first two PREROUTING rules, ahead of Docker's DNAT jump.
  local first second
  first=$(iptables -t nat -S PREROUTING | sed -n '2p')
  second=$(iptables -t nat -S PREROUTING | sed -n '3p')
  [[ "$first" == *"-d $IP/32"*"--dport 443"*"comment seatres"*"--to-ports 9443"* ]] &&
    [[ "$second" == *"-d $IP/32"*"--dport 80"*"comment seatres"*"--to-ports 9080"* ]]
}

up() {
  # Earlier versions added a /24 (which also adds a subnet route); replace it with a /32.
  if ip -4 addr show dev "$DEV" | grep -q "inet $IP/24"; then ip addr add "$IP/32" dev "$DEV"; ip addr del "$IP/24" dev "$DEV"; fi
  ip -4 addr show dev "$DEV" | grep -q "inet $IP/32" || ip addr add "$IP/32" dev "$DEV"
  ensure_cap
  in_order && return 0   # nothing to repair: never churn rules that are already right
  remove_rules
  for pair in "${PAIRS[@]}"; do
    set -- $pair
    # shellcheck disable=SC2046
    iptables -t nat -I PREROUTING 1 $(rule "$1" "$2")
  done
}

down() {
  remove_cap
  remove_rules
  for prefix in 32 24; do
    if ip -4 addr show dev "$DEV" | grep -q "inet $IP/$prefix"; then ip addr del "$IP/$prefix" dev "$DEV"; fi
  done
}

case "${1:-up}" in
  up) up ;;
  down) down ;;
  status) ip -4 addr show dev "$DEV" | grep "inet $IP/" || true; iptables -t nat -S PREROUTING | grep seatres || true
          iptables -S INPUT | grep seatres || true ;;
  *) echo "usage: $0 up|down|status" >&2; exit 2 ;;
esac
