# Write-up — Seat Reservation at Scale

Live: https://seatres-paytm.centralindia.cloudapp.azure.com · `/grafana/` · `/logs/` · evidence in `docs/evidence/`

## 1. The atomic decision

- **One row per seat is the source of truth.** `seats(show_id, seat_no)` holds a single status, so
  `available + held + confirmed == total` holds by construction.
- **Check and claim happen in one READ COMMITTED transaction.** `SELECT … FOR UPDATE`, then a conditional
  `UPDATE … WHERE <takeable>`, so there's no read-then-write window.
- **Losers fail cleanly.** After the winner commits, Postgres has each waiter re-check the latest row
  (READ COMMITTED re-check); it sees the seat taken and returns 409, never a 500.
- **Defence in depth.** `CHECK ((status='available') = (reservation_id IS NULL))` makes a seat with two
  owners impossible to store, even if the code were wrong.
- **One global lock order makes deadlocks impossible:** user lock, then seats by integer `seat_no`, then
  reservation, then payment rows. Never sorted by label ("A10" < "A2").
- **Multi-seat is all-or-nothing.** On a conflict, `ROLLBACK TO SAVEPOINT`, so a group is never split
  (tested with `[A1,A2,A3]×2` against `[A2]` under random timing).
- **Per-user limit under concurrency.** A `user_show_locks` row makes one user's writes go one at a time,
  and the limit counts only *live* seats, so expired holds free quota instantly.
- **Contention is a retry, not an error.** `40P01`/`40001`/`55P03` are retried once, then sent as 429 +
  `Retry-After`. Domain outcomes are never 5xx.

## 2. Idempotency

- **`(user_id, scope, key)` is inserted inside the deciding transaction.** A concurrent retry blocks on the
  uncommitted unique-index entry, then replays the committed outcome.
- **The response is stored in the same commit as the effect,** so key and reservation can never disagree.
  Declines are stored too, so retries are deterministic.
- **A replay is 200 + `Idempotent-Replayed: true`, never 201,** so a storm has exactly one 201. Reusing a
  key with a different body (SHA-256 fingerprint) → 409 `idempotency_mismatch`.
- **Memory-only declines are exact.** A never-accepted key can't be a replay, so a taken seat plus an
  unseen key costs zero DB reads. Seen keys (24 h) are reloaded before readiness turns green.

## 3. Holds, expiry and payment

- **Expiry is lazy, never timer-driven.** A hold becomes takeable at `hold_expires_at <= now`, so
  correctness never waits on a background job.
- **Late confirm succeeds until someone else claims the seat.** It checks ownership, not the clock,
  which is fair to users on flaky connections.
- **Payment is a saga: pin → charge → finalize.** The pin holds seats until `greatest(hold, now) + 60 s`,
  and only while a payment is in flight. No gateway call runs inside a transaction.
- **Exactly-once money.** Charges are keyed `reservation:attempt`, the ledger has `UNIQUE(gateway_ref, kind)`,
  and a partial unique index allows one in-flight attempt per reservation.
- **Built-in compensation.** A charge landing after the seat was legitimately re-granted is auto-refunded
  and alerted on. Unknown outcomes are settled by a recovery job, by key.
- **Cancel releases only `WHERE reservation_id = $r`,** so it can never resurrect a seat now owned by
  someone else. Cancelling a confirmed booking releases the seat and refunds atomically.

## 4. Consistency vs availability

- **CP by design.** With Postgres unreachable, readiness fails closed (503) and writes are refused,
  because an oversell costs more than a minute of refusals.
- **In-memory layers may only decline or delay, never sell.** Remove them and it's still correct, and the
  integration suite proves the DB guarantee with them switched off.
- **Gateway partitions are absorbed by keys.** The pin protects the seat, recovery reconciles by key, and
  the ledger makes any double movement detectable.

## 5. Scale: designed, then measured

- **DB work scales with seats, not requests.** A taken-seat cache plus a striped per-seat gate collapse a
  500-buyer hot seat into about one transaction.
- **Admission control.** Writes are bounded, with excess shed as 429. Reads get their own budget, so
  polling can't starve reservations of connections.
- **Edge sized from measurement.** At ~57 KB per TLS connection: a 1.8 GB proxy, a 24k connection cap and a
  65,535 accept queue. The edge dials the container directly (Docker's userland proxy failed at ~10k).
- **Zero-downtime restarts.** Kestrel drains on SIGTERM, and the edge re-dials and retries idempotent routes
  only. A mid-burst API restart showed 0 errors.
- **63,000 reserves in flight at once** (3 shows, 12 machines, 0.1 s spread, HTTP/2): 0 × 5xx, 0 double
  grants, every show reconciled.
- **10.5k concurrent connections / 192k requests: 0 errors.** For 20k brand-new HTTP/1.1 connections in one
  second, 98% were answered within 30 s; that's the 2-vCPU TLS-handshake ceiling.
- **Path to 200k:** a waiting room, TLS at a load balancer, stateless replicas plus PgBouncer (Postgres still
  decides), seat-map push, and sharding by `show_id`.

## 6. Observability — what pages me at 2am

- **The metrics reconcile with the API by design.** Seat gauges are read from the DB at scrape time with the
  same rule as `GET /shows/{id}`, alongside confirmed and declined-by-reason counters.
- **An independent reconciler runs every 15 s.** It checks that seat rows equal capacity, that ownership is
  consistent, and that `charges − refunds == price × confirmed`.
- **Alert rules as code** (`deploy/prometheus/alerts.yml`, checked by `promtool` in CI). Firing alerts show
  on the dashboard, and a DB outage was verified to fire.
- **Pages:** 5xx or unhandled errors · reconciliation failure · seat-lost refund · payment stuck > 2 min ·
  DB down · pool saturated. Sustained 429s are a capacity ticket.
- **Logs:** one structured JSON decision line per request, with a `request_id`. Polling endpoints' access
  lines are dropped, halving log volume under a burst.

## 7. Operating on a shared VM

- **The other apps on the VM aren't touched.** We use our own `/32` public IP, an `iptables` REDIRECT
  matching only that IP, and our own Caddy; senti/ll/ellis config is never edited.
- **Good-neighbour defaults.** Kernel limits are raise-only and restored at teardown. Our CPU weight is
  below the other apps', and config is hot-reloaded on deploy.
- **The 10-day lifetime enforces itself.** A systemd timer removes the stack, and an OIDC GitHub workflow
  scoped to one NIC and one IP removes the address.

## 8. AI usage — directed vs decided

- **I owned the architecture and every trade-off:** the stack, the CP stance, lock order, ledger, the
  hold → late-confirm → payment-grace model, the co-tenant deployment and edge capacity.
- **I stress-tested the design before code existed.** Pay-after-expiry races, "20k requests ≠ 20k
  connections", 200k users and overlapping groups shaped the pin, admission control and lock order.
- **Claude Code (Anthropic) was my implementation and review accelerator.** It wrote code, tests and scripts
  test-first under my direction, and independent AI review passes surfaced defects that were then fixed.
- **Nothing was taken on trust.** Every claim is backed by 170 tests on real Postgres, live bursts up to
  63,000 concurrent requests, and direct SQL checks.

## 9. What I would do next

- **Category / best-available allocation** (`{"category":"deluxe","quantity":3}`): row-filling and
  group-preserving, as one locked query over `seat_no`.
- **Suggested alternative seats in the 409 body,** computed from the same seat map.
- **Multiple replicas:** a shared seen-key set plus PgBouncer, with the database still the only decider.
- **A virtual waiting room** in front of the on-sale moment.
- **Alert routing:** the existing rules through Alertmanager to Slack or PagerDuty.
