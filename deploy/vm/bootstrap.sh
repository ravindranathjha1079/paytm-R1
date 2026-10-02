#!/usr/bin/env bash
# Installs or updates seatres on the VM. Run as root:
#   REPO_URL=https://github.com/<owner>/seatres.git EXPIRES_AT="2026-10-12 06:30:00 UTC" bash bootstrap.sh
# Creates only: /opt/seatres, the seatres compose project, /usr/local/sbin/seatres-net,
# /etc/systemd/system/seatres-*, /etc/sysctl.d/99-seatres.conf. Records them in /opt/seatres/.manifest.
set -euo pipefail
REPO_URL=${REPO_URL:?REPO_URL is required}
BRANCH=${BRANCH:-main}
DIR=/opt/seatres
FQDN=${SEATRES_FQDN:-seatres-paytm.centralindia.cloudapp.azure.com}

if [ -d "$DIR/.git" ]; then
  git -C "$DIR" fetch -q origin "$BRANCH"
  git -C "$DIR" reset -q --hard "origin/$BRANCH"
else
  git clone -q --branch "$BRANCH" "$REPO_URL" "$DIR"
fi
cd "$DIR"

if [ ! -f .env ]; then
  rand() { openssl rand -hex "$1"; }
  LOGS_PASSWORD=$(rand 12)
  LOGS_HASH=$(docker run --rm caddy:2 caddy hash-password --plaintext "$LOGS_PASSWORD")
  umask 077
  cat > .env <<ENV
SEATRES_FQDN=$FQDN
POSTGRES_PASSWORD=$(rand 16)
JWT_KEY=$(rand 32)
ADMIN_KEY=$(rand 16)
GRAFANA_ADMIN_PASSWORD=$(rand 12)
LOGS_USER=paytm
LOGS_PASSWORD=$LOGS_PASSWORD
LOGS_HASH='$LOGS_HASH'
ENV
fi

install -m 0755 deploy/vm/seatres-net.sh /usr/local/sbin/seatres-net
install -m 0755 deploy/vm/teardown.sh /usr/local/sbin/seatres-teardown
install -m 0644 deploy/vm/seatres-net.service deploy/vm/seatres-net-check.service deploy/vm/seatres-net-check.timer \
  deploy/vm/seatres-teardown.service /etc/systemd/system/
install -m 0755 deploy/vm/seatres-sysctl.sh /usr/local/sbin/seatres-sysctl
rm -f /etc/sysctl.d/99-seatres.conf   # earlier versions wrote a fixed file; values are now raise-only at runtime
/usr/local/sbin/seatres-sysctl raise

# One-shot teardown, fixed at the first install (never pushed back by re-deploys).
if [ ! -f /etc/systemd/system/seatres-teardown.timer ]; then
  EXPIRES_AT=${EXPIRES_AT:?EXPIRES_AT is required on first install, e.g. "2026-10-12 06:30:00 UTC"}
  sed "s|@EXPIRES_AT@|$EXPIRES_AT|" deploy/vm/seatres-teardown.timer.tmpl > /etc/systemd/system/seatres-teardown.timer
fi

systemctl daemon-reload
systemctl enable --now seatres-net.service seatres-net-check.timer seatres-teardown.timer >/dev/null
systemctl restart seatres-net.service

# The network's subnet is fixed in compose.prod.yaml; an older network without it must be recreated.
if docker network inspect seatres_default >/dev/null 2>&1 &&    [ "$(docker network inspect seatres_default --format '{{range .IPAM.Config}}{{.Subnet}}{{end}}')" != "172.31.250.0/24" ]; then
  docker compose -f compose.yaml -f deploy/compose.prod.yaml --env-file .env down
fi
docker compose -f compose.yaml -f deploy/compose.prod.yaml --env-file .env up -d --build --remove-orphans
docker image prune -f --filter "label=com.docker.compose.project=seatres" >/dev/null || true

for _ in $(seq 1 60); do
  if curl -fsS http://127.0.0.1:8080/health/ready >/dev/null; then break; fi
  sleep 2
done

cat > .manifest <<MANIFEST
dir=$DIR
compose_project=seatres
files=/usr/local/sbin/seatres-net /usr/local/sbin/seatres-teardown /usr/local/sbin/seatres-sysctl /var/lib/seatres
units=seatres-net.service seatres-net-check.service seatres-net-check.timer seatres-teardown.service seatres-teardown.timer
teardown_at=$(systemctl show seatres-teardown.timer -p TimersCalendar --value)
MANIFEST

echo "--- seatres status"
docker compose -f compose.yaml -f deploy/compose.prod.yaml --env-file .env ps --format '{{.Service}} {{.Status}}'
/usr/local/sbin/seatres-net status
systemctl list-timers seatres-teardown.timer --no-pager | head -3
curl -s http://127.0.0.1:8080/health/ready; echo
