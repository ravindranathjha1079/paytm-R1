# SeatRes — seat reservation at scale

A .NET 10 JSON API that sells assigned seats for a show and stays correct under on-sale stampedes:
no seat is ever sold twice, a user never exceeds their limit, and a retried request never reserves or
charges twice. One Postgres is the only place a seat decision is made.

| | |
|---|---|
| **Live API** | https://seatres-paytm.centralindia.cloudapp.azure.com |
| **Dashboard** (anonymous, read-only) | https://seatres-paytm.centralindia.cloudapp.azure.com/grafana/ |
| **Live logs** (Dozzle, basic auth — credentials in the submission email) | https://seatres-paytm.centralindia.cloudapp.azure.com/logs/ |
| **Metrics** (Prometheus text) | https://seatres-paytm.centralindia.cloudapp.azure.com/metrics |
| **Health** | `/health/live` (process) · `/health/ready` (DB reachable + migrated, fails closed with 503) |
| **OpenAPI** | https://seatres-paytm.centralindia.cloudapp.azure.com/openapi/v1.json |
| **Evidence** | [`docs/evidence/`](docs/evidence/): raw outputs of the live runs below |
| Design | [`WRITEUP.md`](WRITEUP.md) · spec and plan in [`docs/superpowers/`](docs/superpowers/) |

The deployment lives for 10 days (until 2026-10-11 22:00 UTC) and then removes itself.

**Measured live** (see [`docs/evidence/`](docs/evidence/)):

| Run | Requests | Result |
|---|---|---|
| 6 GitHub runners at once, about 10,500 concurrent TLS connections | 192,739 | **0 × 5xx, 0 dropped**; each 500-user hot seat → exactly one 201; all invariants reconcile |
| API container restarted 30 s into a 42k-request burst | 41,987 | **0 × 5xx, 0 dropped**; graceful drain + edge retries on idempotent routes |

## Run it locally

Requirements: Docker. (For the tests: .NET 10 SDK.)

```bash
docker compose up -d --build          # api, postgres, prometheus, grafana, dozzle
curl localhost:8080/health/ready      # {"status":"ready"}
./burst.sh http://localhost:8080      # the on-sale stampede, ~1 minute
```

Grafana: http://127.0.0.1:3000/grafana/ · Logs: http://127.0.0.1:8081/logs/ · default admin key `local-admin-key`.

Tests (real Postgres via Testcontainers, so Docker must be running):

```bash
dotnet test SeatRes.slnx
```

## The burst script

```bash
ADMIN_KEY=<admin key> ./burst.sh https://seatres-paytm.centralindia.cloudapp.azure.com
```

It creates a fresh 1,000-seat show (rows A–T × 50, ₹250, limit 4) and runs:

| Scenario | What it fires | What must hold |
|---|---|---|
| hot-seat | 500 users at seat A1 at once | exactly one 201, 499 × 409 `seat_taken` |
| group-overlap | two users want `[B1,B2,B3]`, a third wants `[B2]`, ×16 | one winner per trial; a group is never split |
| stampede | ~20,000 reserves, 70% aimed at 10 "good" seats, 5% sent twice with the same key | zero 5xx; no seat granted to two reservations |
| idempotency | the same key 50× concurrently, then with other seats | one 201 + 49 × 200 replay; then 409 `idempotency_mismatch` |
| per-user-limit | one user, 10 parallel reserves, limit 4 | at most 4 held |
| spoof | `user_id` in the body; another user cancels/reads/confirms | acts as the token's user; others get 404 |
| payments | confirm, replayed confirm, hold-and-pay, cancel→refund, declined payment, retry | exactly one charge each; refunds recorded |
| `--overload N` | N extra requests at maximum concurrency | excess shed with 429, still zero 5xx |

It prints the outcome distribution (2xx / 409 by reason / 402 / 429 / **5xx**), p50/p99 latency, and a
final reconciliation — `available + held + confirmed == total_seats`, ledger `charges − refunds ==
price × confirmed seats`, and `/metrics` gauges equal to the API — and exits non-zero on any violation.

Options: `--hot`, `--stampede`, `--concurrency`, `--workers N` (multi-process client for very large runs),
`--overload N`, `--only a,b`, `--quick`. A single laptop is usually the bottleneck above a few thousand
requests per second; `.github/workflows/live-burst.yml` runs the same script from a GitHub runner.

## API

All JSON, snake_case, money in integer paise. Every response has `X-Request-Id`; errors are
`{ "error": "<code>", "message": "…", "request_id": "…" }`.

```bash
BASE=https://seatres-paytm.centralindia.cloudapp.azure.com
ADMIN=$(curl -s -X POST $BASE/auth/admin-token -H "X-Admin-Key: $ADMIN_KEY" | jq -r .token)
SHOW=$(curl -s -X POST $BASE/shows -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' \
  -d '{"name":"friday-night","seats":["A1","A2","A3"],"price_paise":25000}' | jq -r .id)
TOKEN=$(curl -s -X POST $BASE/auth/token -H 'Content-Type: application/json' -d '{"user_id":"alice"}' | jq -r .token)

curl -s -X POST $BASE/shows/$SHOW/reserve -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"seats":["A2"],"idempotency_key":"k-1"}'           # 201 {"status":"held","expires_at":…}
curl -s -X POST $BASE/reservations/<id>/confirm -H "Authorization: Bearer $TOKEN" -H 'Idempotency-Key: pay-1'   # 200 confirmed
```

| Endpoint | Auth | Success | Declines |
|---|---|---|---|
| `POST /auth/token {user_id}` | — | 200 `{token}` (stand-in identity provider) | 400 |
| `POST /auth/admin-token` + `X-Admin-Key` | admin key | 200 `{token}` | 401 |
| `POST /shows {name, seats[], price_paise, per_user_limit?}` | admin | 201, every seat `available` | 400, 401, 403 |
| `GET /shows/{id}` (`?seats=false` for counts only) | — | per-seat status, counts, `reconciled`, ledger | 404 |
| `POST /shows/{id}/reserve {seats[], idempotency_key, confirm?}` | user | 201 `held` (5-minute hold) · 201 `confirmed` with `confirm:true` · 200 replay | 409 `seat_taken` / `per_user_limit` / `idempotency_mismatch`, 402, 429, 400, 404 |
| `POST /reservations/{id}/confirm` + `Idempotency-Key` | owner | 200 `confirmed` | 409 `hold_lost` / `payment_in_progress` / `per_user_limit`, 402 (+`retry_until`), 202 |
| `POST /reservations/{id}/cancel` | owner | 200 `cancelled` / `refunded` | 409, 404 |
| `GET /reservations/{id}` | owner | 200 | 404 |

Behaviour in one paragraph: a reserve holds the seats for 5 minutes (server clock). Multi-seat requests
are **all-or-nothing**. Confirm pins the seats while the (simulated) payment runs, with a 1-minute grace
past the hold; a late confirm still succeeds if nobody has claimed the seats since. An expired hold is not
released by a timer — it simply becomes claimable, so correctness never depends on a background job.
`X-Sim-Gateway: decline | unknown | slow` on a confirm drives the payment failure paths.

## Observability

- Metrics: `seatres_reservations_confirmed_total{show}`, `seatres_holds_created_total{show}`,
  `seatres_reservations_declined_total{reason}` (`seat_taken`, `per_user_limit`, `idempotent_replay`,
  `idempotency_mismatch`, `hold_lost`, `payment_declined`, `payment_in_progress`, `overloaded`, …),
  `seatres_seats{show,state}` (read from the DB at scrape time — the same rule as `GET /shows/{id}`),
  `seatres_reconciliation_ok{check}`, `seatres_payments_total`, `seatres_refunds_total{reason}`,
  `seatres_payment_pending`, `seatres_fast_path_declines_total`, `seatres_db_retries_total`,
  `seatres_unhandled_errors_total`, HTTP histograms, Npgsql pool metrics.
- Logs: one structured JSON line per request and per domain decision (`reserve.outcome`,
  `confirm.outcome`, `gateway.charge`, `refund.issued`, `spoof_attempt`, `reconciliation.violation`),
  all carrying `request_id`.
- Dashboard: "SeatRes — On-sale burst": firing alerts, 5xx, reconciliation, outcomes by reason, seats by
  state, latency, pool, payments.
- Alerts as code: [`deploy/prometheus/alerts.yml`](deploy/prometheus/alerts.yml) holds the "page me" list
  (5xx, unhandled errors, reconciliation failure, seat-lost refund, stuck payments, DB down, pool saturated),
  evaluated by Prometheus and shown on the dashboard. CI validates the rules with `promtool`.

## Deployment

The service runs on an existing Azure VM shared with other apps, without touching them:

- `deploy/azure/network-up.sh` adds a second public IP (`seatres-paytm`) as a secondary NIC ip-config.
- `deploy/vm/bootstrap.sh` clones this repo to `/opt/seatres`, generates secrets into `.env`, installs a
  small systemd unit that redirects 80/443 **for that IP only** to our own Caddy (9080/9443, automatic
  TLS), and runs `docker compose -f compose.yaml -f deploy/compose.prod.yaml up -d --build`.
- Edge: Caddy dials the API container directly (fixed internal IP, at most 768 upstream connections). It
  re-dials while the API restarts, and retries cut-off requests on idempotent routes only. A 12k
  connection cap refuses excess connections instead of running out of memory. Restarts and redeploys
  don't drop requests.
- Shared-VM manners: secondary IP as /32, kernel limits only ever raised (restored at teardown if still ours), lower
  CPU weight than the other apps, nothing of theirs touched.
- Teardown: a systemd timer on the VM runs `deploy/vm/teardown.sh` at the expiry, and
  `.github/workflows/teardown.yml` (GitHub OIDC, Network Contributor on that IP and NIC only) removes the
  IP afterwards.

## Repository layout

```
src/SeatRes.Api/        Endpoints · Services · Data (all SQL) · Concurrency · Payments · Background · Observability
tests/                  unit tests and integration tests (real Postgres)
tools/burst/            burst.py (the only Python in the repo)
deploy/                 compose overlay, Caddy, Prometheus, Grafana, VM and Azure scripts
docs/superpowers/       design spec and implementation plan
```
