# Seat Reservation at Scale — Design Spec

Date: 2026-10-02 · Status: draft for review
Source brief: Paytm Money Backend Round-1 take-home ("Seat Reservation at Scale — Deploy & Observe").

## 1. Goal and success criteria

Build, deploy and operate a JSON HTTP service that sells assigned seats for a show and stays
correct under on-sale stampedes. Paytm grades the **running service** by cloning, deploy-checking
and bursting the live URL. Success means, across a ~20,000-request burst (and gracefully beyond,
up to ~200,000 concurrent):

1. No seat is ever confirmed (or actively held) for two users. Hot-seat storm → exactly one 201, rest 409.
2. Zero 5xx across the burst. Every decline is a 4xx domain outcome.
3. `available + held + confirmed == total_seats` per show, to the unit, during and after the burst.
4. Same idempotency key → exactly one reservation; same key + different body → 409.
5. Per-user limit (default 4) holds under concurrency.
6. Identity comes only from the auth token; users can cancel only their own reservations.
7. Live public URL, healthy after cold start; Dockerised; health/readiness; Prometheus metrics that
   reconcile with API state; structured logs with request ids; one-command burst script.

Out of scope (explicitly): UI, category / best-available allocation, alternative-seat suggestions,
multi-instance deployment. These go in WRITEUP.md "what next".

## 2. Constraints and decisions

| Topic | Decision |
|---|---|
| Service stack | **.NET 10 (LTS)**, ASP.NET Core minimal APIs, Npgsql + Dapper (explicit SQL), DbUp migrations, Serilog, prometheus-net. 100% of the service is .NET. |
| Datastore | Single PostgreSQL 16. Postgres is the **only** place a seat decision is made. |
| Tooling | Python (asyncio + aiohttp) **only** for the burst script. |
| Money | `bigint` paise everywhere; never floating point. |
| Partial requests | **All-or-nothing.** A multi-seat request gets every seat or none. |
| Hold model | 5-minute hold → confirm (simulated payment). Late confirm allowed until someone else claims a seat. +1 minute grace only while a payment is processing. Explicit cancel by owner. |
| Hosting | Existing Azure VM `senti-vm` (rg `senti-rg`, Central India, Ubuntu 22.04, B2als_v2) via docker compose; new public IP with DNS label `seatres-paytm.centralindia.cloudapp.azure.com`; existing Caddy terminates TLS. senti must not be disturbed. |
| Lifetime | Live for 10 days from first deploy, then automatic teardown of only our resources. |
| Commits | Incremental, authored by the user; no AI co-author trailers. AI usage disclosed in WRITEUP.md. |

## 3. Domain model

### 3.1 Seat states

Stored `seats.status` ∈ `available | held | payment_pending | confirmed`.

```
available ──reserve──▶ held (hold_expires_at = now()+5m)
held ──confirm starts (pin)──▶ payment_pending (pay_deadline)
payment_pending ──gateway success──▶ confirmed
payment_pending ──gateway declined──▶ payment_pending (retry allowed until pay_deadline)
held / payment_pending ──owner cancel──▶ available
confirmed ──owner cancel──▶ available (+ refund)
held past hold_expires_at, or payment_pending past pay_deadline ──▶ "takeable"
```

**Takeable predicate** (single SQL fragment, used everywhere):

```sql
status = 'available'
OR (status = 'held'            AND hold_expires_at <= now())
OR (status = 'payment_pending' AND pay_deadline    <= now())
```

Nothing releases seats on a timer. An expired hold/pin simply becomes takeable; it stays owned by
its reservation until someone else claims it. This is what makes "late confirm until someone else
reserves" work and means correctness never depends on a background job running on time.

**Effective (reported) status**, used by `GET /shows/{id}` and the seats gauge:

| stored | condition | reported |
|---|---|---|
| available | – | available |
| held | `hold_expires_at > now()` | held |
| payment_pending | `pay_deadline > now()` | held |
| held / payment_pending | expired | available |
| confirmed | – | confirmed |

Each seat is one row with one status, so the three reported counts always sum to `total_seats`.

### 3.2 Timing rules

- Hold: `hold_expires_at = now() + 5 min` at reserve (server clock; client clocks are never used).
- Pin on confirm start: `pay_deadline = greatest(hold_expires_at, now()) + 1 min`.
  - Started inside the hold → seat is pinned until hold expiry + 1 minute.
  - Started after hold expiry but seat not yet claimed by anyone → pinned until now + 1 minute.
- `confirm: true` on reserve (hold and pay in one call): no hold phase; `pay_deadline = now() + 1 min`.
- Retries of a declined payment are allowed until `pay_deadline`; retries never move the deadline.
- If `pay_deadline` passes with the seats still unclaimed, the owner may start confirm again (it
  re-pins with `now() + 1 min`) — same "until someone else claims" rule.
- Responses carry `expires_at` / `pay_deadline` and `server_time` (UTC) so clients can render a
  countdown independent of their clock.

### 3.3 Tables

```sql
shows(
  id uuid PK, name text NOT NULL, price_paise bigint NOT NULL CHECK (price_paise >= 0),
  per_user_limit int NOT NULL DEFAULT 4 CHECK (per_user_limit > 0),
  total_seats int NOT NULL, created_at timestamptz NOT NULL DEFAULT now())

seats(
  show_id uuid REFERENCES shows, seat_no int NOT NULL,          -- creation order; THE lock order
  label text NOT NULL,
  status text NOT NULL CHECK (status IN ('available','held','payment_pending','confirmed')),
  reservation_id uuid NULL, holder_user_id text NULL,
  hold_expires_at timestamptz NULL, pay_deadline timestamptz NULL,
  PRIMARY KEY (show_id, seat_no),
  UNIQUE (show_id, label),
  CHECK ((status = 'available') = (reservation_id IS NULL)),
  CHECK ((reservation_id IS NULL) = (holder_user_id IS NULL)))
INDEX seats_holder (show_id, holder_user_id) WHERE reservation_id IS NOT NULL
INDEX seats_reservation (reservation_id)

reservations(
  id uuid PK, show_id uuid, user_id text, seat_nos int[], labels text[],
  amount_paise bigint NOT NULL,
  status text CHECK (status IN ('held','payment_pending','confirmed','cancelled',
                                'expired','refund_pending','refunded')),
  hold_expires_at timestamptz, pay_deadline timestamptz,
  created_at timestamptz, updated_at timestamptz)

user_show_locks(user_id text, show_id uuid, PRIMARY KEY (user_id, show_id))   -- lock row only

idempotency_keys(
  user_id text, scope text CHECK (scope IN ('reserve','confirm')), key text,
  request_hash bytea NOT NULL, reservation_id uuid NULL,
  response_status int NULL, response_body jsonb NULL,   -- NULL while in progress
  created_at timestamptz, PRIMARY KEY (user_id, scope, key))

payment_attempts(
  id uuid PK, reservation_id uuid, attempt_no int, idem_key text,
  gateway_key text UNIQUE,                               -- "{reservation_id}:{attempt_no}"
  status text CHECK (status IN ('pending','succeeded','declined','unknown')),
  created_at timestamptz, updated_at timestamptz,
  UNIQUE (reservation_id, attempt_no))
UNIQUE INDEX one_inflight_attempt ON payment_attempts(reservation_id)
  WHERE status IN ('pending','unknown')

payments(                                                 -- our money ledger
  id uuid PK, reservation_id uuid, attempt_id uuid NULL,
  kind text CHECK (kind IN ('charge','refund')), amount_paise bigint CHECK (amount_paise > 0),
  gateway_ref text NOT NULL, reason text NULL, created_at timestamptz,
  UNIQUE (gateway_ref, kind))

gateway_charges(                                          -- the simulated external provider's book
  gateway_key text PK, amount_paise bigint, status text, refunded boolean, created_at timestamptz)
```

`holder_user_id` is denormalised onto `seats` so the per-user live-seat count is one indexed query.

## 4. Concurrency design (the atomic decision)

### 4.1 Global lock order (deadlock freedom)

Every transaction that takes more than one lock takes them in this order, and only this order:

1. `user_show_locks` row of the acting user (exactly one per transaction),
2. `seats` rows `ORDER BY seat_no` (`SELECT … FOR UPDATE`),
3. `reservations` rows `ORDER BY id`,
4. `idempotency_keys` / `payment_attempts` / `payments` rows (inserted or updated last).

`seat_no` is an integer assigned in request order at show creation; label text is never used for
ordering (collation-dependent, "A10" < "A2"). Backstop: SQLSTATE `40P01` (deadlock) / `40001`
(serialization) are retried once in-process, then mapped to 429 — never 5xx.

### 4.2 Reserve — `POST /shows/{id}/reserve`

Request `{ "seats": ["A12","A13"], "idempotency_key": "…", "confirm": false }`; the key may also
come from the `Idempotency-Key` header (header wins; if both present and different → 400).
`user_id` in the body is ignored and logged as `spoof_attempt`.

Validation: empty seat list, duplicate labels within the request, unknown labels, or missing
idempotency key → 400; show missing → 404. Requesting more seats than `per_user_limit` is a
domain decline (409 `per_user_limit`), not a validation error.

Processing pipeline:

1. **Idempotency lookup** (before any fast path, so a retry of a successful request is replayed and
   not mis-declined): in-memory LRU of completed keys → else PK read of `idempotency_keys`.
   Found + completed → same hash: replay stored response (original 201 is replayed as **200** with
   header `Idempotent-Replayed: true`; stored declines replay as the same 409); different hash →
   409 `idempotency_mismatch`. Found + in progress → wait on it (falls into step 4's insert, which
   blocks on the uncommitted unique entry).
2. **Taken-seat cache** (optimisation only): if any requested seat is known taken within the last
   ≤1 s (never beyond that seat's own expiry), decline 409 `seat_taken` immediately. Declines-only,
   so it can never cause an oversell; these fast declines are not persisted (a retry is
   re-evaluated; a key can still never reserve twice).
3. **Per-seat gate** (optimisation only): acquire in-process semaphores for the requested seats from
   a fixed stripe array (4096 stripes, hashed by `(show_id, seat_no)`), acquired **in stripe-index
   order, de-duplicated** — deadlock-free. Waiters wait in memory, not on a DB connection.
   Re-check the taken cache after acquiring. Gate wait timeout → proceed to DB anyway (DB decides).
   All gates are acquired before the DB transaction opens and released after it ends.
4. **One DB transaction** (READ COMMITTED, `lock_timeout 2s`, `statement_timeout 5s`):
   1. `INSERT INTO idempotency_keys … ON CONFLICT DO NOTHING RETURNING` → conflict ⇒ go to step 1
      semantics with the now-committed row.
   2. `INSERT INTO user_show_locks … ON CONFLICT DO NOTHING; SELECT … FOR UPDATE` (the user's lock).
   3. Live-seat count for this user/show (`holder_user_id = $u AND NOT takeable`).
      `count + n > per_user_limit` ⇒ decline `per_user_limit`.
   4. `SELECT seat_no, status, reservation_id, hold_expires_at, pay_deadline FROM seats
      WHERE show_id=$s AND seat_no = ANY($nos) ORDER BY seat_no FOR UPDATE`.
   5. If any row is not takeable ⇒ decline `seat_taken` (response lists the taken labels;
      `held_by_you: true` when they are the caller's own).
   6. Collect previous owners of takeable-but-owned rows; `UPDATE seats SET status='held',
      reservation_id=$r, holder_user_id=$u, hold_expires_at=now()+5m, pay_deadline=NULL
      WHERE show_id=$s AND seat_no = ANY($nos) AND (<takeable>)`; assert affected == n
      (otherwise decline `seat_taken`; cannot happen while rows are locked — defence in depth).
   7. `INSERT reservations (…status 'held'…)`; mark previous owners' reservations `expired`
      (`UPDATE reservations SET status='expired' WHERE id = ANY($prev) AND status IN
      ('held','payment_pending')`, ids sorted). Their other, unclaimed seats stay as-is: already
      takeable and reported available; the owner can no longer confirm an `expired` reservation.
   8. Store the response on the idempotency row; COMMIT.
   Declines: `ROLLBACK TO SAVEPOINT` taken after step 1, write the 409 response onto the
   idempotency row, COMMIT — so retries of a declined request replay the same decline.
5. Update taken cache (claimed seats → taken), metrics, structured log.
6. If `confirm: true`: steps 4.3–4.4 run inside the same request; the reserve transaction already
   sets `status='payment_pending'` and `pay_deadline = now() + 1 min` instead of `held`
   (the seat is never in a takeable hold). Success → **201 `confirmed`**; gateway declined →
   seats released in the finalize transaction and **402 `payment_declined`**; gateway outcome
   unknown → **202 `payment_processing`**.

Why race-free: the takeability check (4.5) and the claim (4.6) happen while holding the seat row
locks; a concurrent claimer blocks on the same rows and, after the winner commits, re-reads the
latest committed version (READ COMMITTED re-evaluation under `FOR UPDATE` / `UPDATE … WHERE`),
sees it not takeable, and declines. The CHECK constraints make a seat with two owners
unrepresentable. The in-memory layers only ever decline or delay; deleting them leaves the
system correct.

### 4.3 Confirm — `POST /reservations/{id}/confirm` (Idempotency-Key required)

**TX1 — pin** (lock order: owner's user lock → seats of the reservation by seat_no → reservation):

- Not the caller's reservation → 404. Status `confirmed` → 200 (already done). Status
  `cancelled/expired/refunded/refund_pending` → 409 `hold_lost` or `reservation_closed`.
- Any seat whose `reservation_id ≠ $r` → mark reservation `expired`, 409 `hold_lost`.
- If an attempt is in flight (`pending`/`unknown`): same idem key → 202 `payment_processing`
  (no new charge); different key → 409 `payment_in_progress`.
- If the reservation is no longer protected (hold expired / pay_deadline passed but seats still
  ours): re-check the per-user limit excluding this reservation; over → 409 `per_user_limit`.
- Set seats + reservation `payment_pending`, `pay_deadline` per §3.2 (unchanged if a deadline
  is still running), insert `payment_attempts(status 'pending', gateway_key r:attempt_no)`. COMMIT.

**Gateway call** (outside any transaction): `IPaymentGateway.Charge(gateway_key, amount_paise)`,
idempotent per `gateway_key`, with a client timeout.

**TX2 — finalize** (same lock order):

- Success and all seats still owned by `$r` and no existing kept charge → insert `payments` charge
  row, seats + reservation `confirmed`, attempt `succeeded` → **200 `confirmed`**. This holds even
  if `pay_deadline` has passed, as long as nobody claimed the seats.
- Success but seats lost (claimed after `pay_deadline`) → record charge, call `Refund`, record
  refund (reason `seat_lost`), reservation `refunded` → 409 `hold_lost` with `refunded: true`.
  Metric `seatres_refunds_total{reason="seat_lost"}` (paging alert).
- Success but a charge for this reservation already kept (duplicate attempt) → refund this
  attempt (reason `duplicate_charge`).
- Declined → attempt `declined`; seats stay `payment_pending` until `pay_deadline` →
  **402 `payment_declined`** (`retry_until = pay_deadline`).
- Timeout / unknown → attempt `unknown` → **202 `payment_processing`**; resolved by recovery.

Idempotency: the confirm key is stored in `idempotency_keys` (scope `confirm`) with the final
response; a retry with the same key after a lost response replays it — the customer is never
charged twice because the charge is keyed by `gateway_key` and the ledger has
`UNIQUE (gateway_ref, kind)`.

### 4.4 Cancel — `POST /reservations/{id}/cancel`

Lock order as above. Not owner → 404. Attempt in flight → 409 `payment_in_progress`.

- `held` / `payment_pending` (no money taken) → `UPDATE seats SET status='available', … WHERE
  reservation_id = $r` (never touches seats now owned by others), reservation `cancelled` → 200.
- `confirmed` → in one TX: release seats `WHERE reservation_id=$r`, reservation `refund_pending`;
  then gateway `Refund(gateway_key)`; then TX: insert refund ledger row, reservation `refunded` →
  200 `refunded`. If the refund call fails, the reservation stays `refund_pending` and recovery
  retries; response 202 `refund_processing`.
- Already final → 409 `reservation_closed`.
- Taken-seat cache entries for released seats are invalidated.

### 4.5 Background services (.NET `BackgroundService`)

| Service | Interval | Job |
|---|---|---|
| PaymentRecovery | 10 s | Attempts `pending`/`unknown` older than 30 s → query gateway by `gateway_key` → run TX2 logic. Reservations `refund_pending` → retry refund. `FOR UPDATE SKIP LOCKED`. |
| Reconciler | 15 s | Per show: reported available+held+confirmed == total_seats. Ledger: for every settled reservation, `sum(charge) − sum(refund) == (confirmed ? amount : 0)`. Exports `seatres_reconciliation_ok{check}`; violations logged at Error. |
| SeatGauge | on scrape (1 s cache) | Effective-status counts per show for `seatres_seats`. |

No service releases or reassigns seats — correctness never waits on a timer.

### 4.6 Capacity and overload (the 200k case)

- Npgsql pool capped at **30**; Postgres `max_connections` 100. Transactions never wait on I/O
  other than Postgres (the gateway call is outside), so they last ~2–5 ms.
- DB work scales with seats, not requests: the taken cache + per-seat gate collapse a hot-seat
  storm to ~1 transaction.
- **Admission control** (ASP.NET Core concurrency limiter on reservation/confirm/cancel routes):
  bounded in-flight permits + bounded queue sized for the VM; beyond it → immediate
  **429 `overloaded` + `Retry-After`** (4xx, never 5xx, never a dropped connection).
  Health and metrics routes are exempt.
- Kestrel limits, Caddy upstream keep-alive, and VM tuning (`nofile`, `somaxconn`,
  `tcp_max_syn_backlog`) are part of the deploy.
- Unhandled exceptions are caught by a last-resort handler, logged at Error with the request id,
  and counted in `seatres_unhandled_errors_total`; the burst script fails if this is non-zero.

## 5. API contract

All responses JSON. Errors: `{ "error": "<code>", "message": "…", "request_id": "…" }`.
Every response carries `X-Request-Id`.

| Method & path | Auth | Success | Declines / errors |
|---|---|---|---|
| `POST /auth/token` `{user_id}` | none (demo identity provider; `user_id` matches `^[A-Za-z0-9_-]{1,64}$`) | 200 `{token, user_id, expires_at}` (HS256 JWT, `sub`, `role=user`, 24 h) | 400 |
| `POST /auth/admin-token` header `X-Admin-Key` | admin key (env secret, shared in submission) | 200 `{token}` (`role=admin`) | 401 |
| `POST /shows` `{name, seats[], price_paise, per_user_limit?}` | admin | 201 show with every seat `available` | 400 (empty/duplicate/invalid labels, > 20,000 seats, negative price), 401, 403 |
| `GET /shows/{id}` | none | 200 `{id, name, price_paise, per_user_limit, total_seats, counts{available,held,confirmed}, reconciled, seats[{label,status}], ledger{charged_paise,refunded_paise,net_paise}, server_time}` | 404 |
| `POST /shows/{id}/reserve` | user | 201 `{reservation_id, show_id, user_id, seats, amount_paise, status:"held", expires_at, server_time}`; 201 `status:"confirmed"` with `confirm:true`; 200 replay | 409 `seat_taken`, `per_user_limit`, `idempotency_mismatch`; 402 `payment_declined`; 202 `payment_processing`; 429 `overloaded`; 400; 401; 404 |
| `POST /reservations/{id}/confirm` | owner | 200 `status:"confirmed"` | 409 `hold_lost`, `payment_in_progress`, `per_user_limit`, `reservation_closed`, `idempotency_mismatch`; 402 `payment_declined` (+`retry_until`); 202 `payment_processing`; 404 |
| `POST /reservations/{id}/cancel` | owner | 200 `cancelled` / `refunded` | 409 `payment_in_progress`, `reservation_closed`; 202 `refund_processing`; 404 |
| `GET /reservations/{id}` | owner | 200 reservation incl. attempts | 404 |
| `GET /health/live` | none | 200 | – |
| `GET /health/ready` | none | 200 when DB answers `SELECT 1` within 1 s and migrations applied | 503 |
| `GET /metrics` | none | Prometheus text | – |

`amount_paise = price_paise × seat count`. Authorisation failures on other users' reservations
return 404 (no existence leak).

## 6. Observability

### 6.1 Metrics (prometheus-net, `/metrics`)

| Metric | Type | Labels |
|---|---|---|
| `seatres_reservations_confirmed_total` | counter | show |
| `seatres_holds_created_total` | counter | show |
| `seatres_reservations_declined_total` | counter | reason: `seat_taken`, `per_user_limit`, `idempotent_replay`, `idempotency_mismatch`, `hold_lost`, `payment_declined`, `payment_in_progress`, `overloaded` |
| `seatres_seats` | gauge (from DB) | show, state (`available`/`held`/`confirmed`) |
| `seatres_holds_reclaimed_total` | counter | show |
| `seatres_payments_total` | counter | result (`succeeded`/`declined`/`unknown`) |
| `seatres_refunds_total` | counter | reason (`cancel`/`seat_lost`/`duplicate_charge`) |
| `seatres_payment_pending` | gauge | – |
| `seatres_reconciliation_ok` | gauge | check (`seats`/`ledger`) |
| `seatres_fast_path_declines_total` | counter | layer (`taken_cache`/`gate`) |
| `seatres_db_retries_total` | counter | sqlstate |
| `seatres_unhandled_errors_total` | counter | – |
| `http_request_duration_seconds` | histogram | route, method, code |
| `seatres_db_pool_busy` / `seatres_db_pool_idle` | gauge | – |

No per-seat or per-user labels (bounded cardinality).

### 6.2 Logs

Serilog compact JSON to stdout. Request-id middleware accepts `X-Request-Id` (validated) or
generates one; it is attached to every log event and echoed in responses. Each domain decision
logs `event` (`reserve.held`, `reserve.declined`, `confirm.pinned`, `payment.succeeded`,
`refund.issued`, `spoof_attempt`, …), `request_id`, `user_id`, `show_id`, `reservation_id`,
`seats`, `outcome`, `reason`, `latency_ms`. Logs are viewable through **Dozzle** (read-only) at
`/logs` behind basic auth; a short screen recording of live logs under load is the fallback.

### 6.3 Dashboards

Prometheus (scrapes `/metrics` every 5 s, 10-day retention) + Grafana at `/grafana`
(anonymous read-only) with a provisioned "On-sale burst" dashboard: request rate, outcomes by
reason, seats by state, 5xx (expected flat 0), reconciliation status, p50/p99 latency, DB pool,
payment pending, refunds.

### 6.4 Alerts (documented as "what pages me at 2am")

Any 5xx / unhandled error; `seatres_reconciliation_ok == 0`; any `seat_lost` refund;
`payment_pending` attempts older than 2 minutes; readiness failing > 1 minute; DB pool
saturated > 30 s; 429 rate sustained (capacity planning, not a page).

## 7. Burst script — `./burst.sh <BASE_URL>` (Python 3.11+, asyncio + aiohttp)

1. Wait for `/health/ready`; mint admin token (`ADMIN_KEY` env) and N user tokens.
2. Create a fresh show (default 1,000 seats, `per_user_limit` 4).
3. Scenarios (selectable, all by default):
   - **hot-seat storm**: 500 users → one seat; expect exactly 1×201, 499×409.
   - **group overlap**: users on `[A1,A2,A3]` vs users on `[A2]`; expect one owner per seat, losers own nothing.
   - **stampede**: ~20,000 reserve requests, skewed so most target a handful of "good" seats.
   - **idempotent retries**: same key resent concurrently → one reservation; same key + other seats → 409.
   - **per-user limit**: one user fires 10 parallel reserves on a limit-4 show → ≤ 4 seats.
   - **spoof**: body `user_id` of another user → acts as token user; cancel others' reservation → 404.
   - **confirm/cancel**: hold → confirm; cancel confirmed → refund; late confirm after expiry.
   - `--overload`: ramp beyond capacity → 429s rise, 5xx stays 0.
4. Print outcome distribution (201 / 200-replay / 409 by reason / 402 / 429 / 5xx / transport
   errors), p50/p99 latency, and final reconciliation: `GET /shows/{id}` counts, sum check,
   ledger check, and comparison with `/metrics` counters. Non-zero exit on any violation.
5. `--workers N` (multi-process) and documented multi-machine runs for ≥ 200k concurrency.

## 8. Testing

- **Unit** (xUnit): request hashing, error mapping, takeable/effective status rules, deadline maths,
  stripe ordering.
- **Integration** (xUnit + Testcontainers Postgres 16, in-memory layer **disabled** so the DB
  guarantee is tested alone): every scenario in §7 plus expired-hold reclaim vs late confirm,
  +1 minute grace, decline-then-retry with one charge, gateway-late success → `seat_lost` refund,
  duplicate attempt refund, recovery of `unknown` attempts, cancel never resurrecting others'
  seats, readiness 503 while Postgres is paused, overlapping-set deadlock freedom (repeated with
  randomised timing). After every test: zero 5xx, seat invariant, ledger invariant.
- Time: all SQL takes the current time as a parameter (`$now`) from .NET `TimeProvider` instead of
  calling `now()`, in production and tests alike (single instance, one clock). Tests inject a fake
  `TimeProvider`, so expiry and grace windows are tested without sleeping. The `now()` shown in
  SQL sketches above stands for `$now`.
- CI: GitHub Actions runs build + all tests on every push.

## 9. Deployment

- **Image**: multi-stage Dockerfile (SDK build → `aspnet` runtime, non-root). Built by GitHub
  Actions, pushed to GHCR.
- **Compose** (`deploy/docker-compose.yml`, same file for local `make up`): `api`, `postgres`
  (bound to 127.0.0.1, named volume), `prometheus`, `grafana`, `dozzle`; CPU/memory limits on
  each so senti keeps headroom; `api` healthcheck on `/health/ready`; restart `unless-stopped`.
- **Azure network**: new Standard public IP `seatres-paytm-ip` (DNS label `seatres-paytm`) as a
  secondary IP configuration on `senti-vmVMNic` with a static secondary private IP; netplan
  drop-in on the VM for that address. NSG already allows 80/443.
- **Caddy**: one site block for `seatres-paytm.centralindia.cloudapp.azure.com` added to senti's
  Caddy (exact integration decided after inspecting the VM): `/` → api, `/grafana*` → grafana,
  `/logs*` → dozzle (basic auth). Automatic TLS.
- **Deploy workflow**: build → push → SSH → `docker compose pull && up -d` → poll `/health/ready`.
- **Cold start**: migrations at startup; readiness stays 503 until DB reachable and migrated.

## 10. Teardown (T + 10 days)

- **VM-local**: `setup` installs a one-shot systemd timer for T+10d running
  `deploy/teardown/teardown.sh`: `docker compose down -v`, remove our Caddy block + reload, remove
  the netplan drop-in. Touches only files/containers recorded in a manifest written at setup.
- **Azure**: scheduled GitHub Actions workflow (OIDC, role scoped to the NIC and our public IP)
  removes the secondary IP configuration and deletes `seatres-paytm-ip` on/after the expiry date.
- Nothing belonging to senti is modified or removed.

## 11. Repository layout

```
seatres/
├─ src/SeatRes.Api/{Endpoints,Domain,Data,Data/Migrations,Concurrency,Payments,Background,Observability}
├─ tests/SeatRes.UnitTests/   tests/SeatRes.IntegrationTests/
├─ tools/burst/ (burst.py, requirements.txt)   burst.sh
├─ deploy/ (docker-compose.yml, Caddyfile.seatres, prometheus.yml, grafana/, teardown/, azure/)
├─ .github/workflows/ (ci.yml, deploy.yml, teardown.yml)
├─ Dockerfile  Makefile  README.md  WRITEUP.md
```

## 12. WRITEUP.md outline

Atomic decision (row locks + conditional update + CHECK constraints; seat_no lock order for
deadlock freedom); idempotency (table, scope, exactly-once, mismatch, stored declines, fast-path
caveat); holds & expiry (lazy takeability, late confirm, +1 minute grace, payment pin, saga +
compensation, recovery); consistency vs availability under partition (CP: if Postgres is
unreachable, readiness fails closed and writes are refused rather than guessed); scaling to 200k
(DB load ∝ seats, admission control, waiting room, edge TLS, replicas + PgBouncer, sharding by
show); observability and paging; AI usage (directed vs decided, specific and honest); what next
(category / best-available allocation, alternatives on 409, multi-instance, real gateway).

## 13. Open items (needed at implementation time, not design decisions)

- SSH access to `senti-vm` to inspect senti's Caddy setup.
- GitHub account/org for the public repo and GHCR.
