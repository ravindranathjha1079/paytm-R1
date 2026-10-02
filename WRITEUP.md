# Write-up — Seat Reservation at Scale

Live: https://seatres-paytm.centralindia.cloudapp.azure.com · dashboard `/grafana/` · logs `/logs/`

## 1. The atomic decision

**Mechanism.** Every seat is one row in `seats` (`PRIMARY KEY (show_id, seat_no)`) holding exactly one
`status`. A reserve is one READ COMMITTED transaction (`Data/ReserveTx.cs`):

1. insert the idempotency key (see §2);
2. take the per-user lock row (`user_show_locks`, `INSERT … ON CONFLICT DO UPDATE`);
3. count the user's live seats; over the limit → 409 `per_user_limit`;
4. `SELECT … FROM seats WHERE show_id = $1 AND seat_no = ANY($2) ORDER BY seat_no FOR UPDATE`;
5. if any locked row is not *takeable* → roll back to a savepoint, 409 `seat_taken`;
6. `UPDATE seats SET status='held', reservation_id=$r … WHERE … AND <takeable>` and assert every row changed;
7. insert the reservation.

**Why it is race-free.** The takeability check (5) and the claim (6) happen while this transaction holds
the row locks from (4). A concurrent claimer of the same seat blocks on that row lock; when the winner
commits, Postgres hands the waiter the *latest committed* row version (READ COMMITTED re-evaluation under
`FOR UPDATE`), the waiter sees it is no longer takeable, and declines. There is no read-then-write window.
Underneath, `CHECK ((status = 'available') = (reservation_id IS NULL))` makes a seat with two owners
unrepresentable, and the conditional `UPDATE … WHERE <takeable>` would still refuse even if the lock
logic were wrong.

**Multi-seat and deadlocks.** Requests are all-or-nothing. Every transaction takes locks in one global
order — user lock → seats by `seat_no` (an integer assigned at show creation; never the label text, whose
collation order differs between systems: "A10" < "A2") → reservation rows → payment rows. With one total
order a wait-for cycle cannot form, so `[A1,A2,A3]` vs `[A2]` vs `[A3,A1]` all resolve without deadlock
(tested repeatedly with random overlap). As a backstop, `40P01`/`40001`/`55P03` are retried once and then
answered with 429 — never a 5xx.

**Per-user limit under concurrency.** The `user_show_locks` row serialises one user's writes for one show,
and the count in step 3 is of *live* seats (held-and-unexpired, payment-pinned, confirmed), so ten parallel
requests from one user on a limit-4 show yield exactly four holds.

**The in-memory layer only declines or delays.** With 500 buyers on one seat, 499 row-lock waiters would
each hold a pooled connection and starve every other seat. So, in front of the transaction:
- a *taken-seat cache* remembers for ≤ 1 s (never past the seat's own expiry) that a seat was just taken
  and answers 409 from memory;
- a *per-seat gate* (4,096 striped semaphores, acquired in stripe order, released before any further DB
  work) lets one request per seat reach the database while the rest wait in memory, not on a connection.

Delete both and the system is still correct — just slower; with several replicas each has its own cache and
Postgres still picks the single winner. The whole decision path is tested with the in-memory layer *off*.

## 2. Idempotency

- **Where:** `idempotency_keys (user_id, scope, key)` primary key, with the request fingerprint
  (SHA-256 of show + sorted seats + confirm flag), the final status and the response body (jsonb).
  Keys are scoped per user, so one user can never collide with or replay another's.
- **Exactly once:** the key row is inserted *inside* the deciding transaction, before any seat lock. A
  concurrent retry's insert blocks on the uncommitted unique-index entry, then sees the committed outcome
  and replays it. The response is stored in the same commit as the reservation, so "reserved but the key
  says otherwise" cannot happen.
- **Replays** return the original body with **200** (not 201) and `Idempotent-Replayed: true`, so a storm
  still has exactly one 201 per seat. Declines are stored too: retrying a declined request returns the same
  409 rather than being re-decided.
- **Same key, different body** → 409 `idempotency_mismatch` (seat order does not matter).
- **Subtle case the burst caught:** with the cache on, a retry whose original was still in flight missed the
  key lookup, then hit the cache ("seat taken") and was wrongly declined. Fix: a fast decline consults the
  now-committed key row first. Test: `Concurrent_retries_of_the_winning_key_all_replay_…`.
- Confirm (payment) has its own scope; one key = one payment attempt.

## 3. Holds, expiry and payment

- Reserve creates a **5-minute hold** (server clock; responses carry `expires_at` and `server_time` so a
  client can show a countdown without trusting its own clock).
- **Nothing releases seats on a timer.** An expired hold simply becomes *takeable*
  (`status='held' AND hold_expires_at <= now`). `GET /shows/{id}` and the gauges report it as available
  immediately, and correctness never depends on a background job being on time.
- **Late confirm is allowed until someone else claims the seat**: confirm checks ownership
  (`reservation_id = $r`), not the clock. If the seat was reclaimed → 409 `hold_lost`, no charge.
- **Payment is a saga** (`Data/PaymentTx.cs`): *pin* (seats → `payment_pending`, deadline
  `greatest(hold_expires_at, now) + 1 min` — the extra minute exists only while a payment is processing and
  is never extended by retries) → *charge* (outside any transaction, keyed by `reservation:attempt`) →
  *finalize* (confirm, or keep the pin for a retry after a decline, or compensate).
- If the gateway answers after the pin lapsed and someone else claimed the seat, the payment is **refunded
  automatically** and recorded in the ledger (`seatres_refunds_total{reason="seat_lost"}`).
- Unknown outcomes (timeouts, crashes between charge and finalize) answer 202 and are settled by a recovery
  job that asks the gateway by key. Payment finalization ignores client cancellation: once money may move,
  the outcome is recorded even if the client disconnects.
- Owner cancel releases only `WHERE reservation_id = $r`, so it can never resurrect a seat now owned by
  someone else; cancelling a confirmed booking refunds it.
- Ledger invariant, checked every 15 s and shown on `GET /shows/{id}`:
  `charges − refunds == price × confirmed seats`.

## 4. Consistency vs availability under a partition

This is a CP system by choice. Selling a seat twice or charging twice is worse than refusing a sale for a
minute. If the API cannot reach Postgres, `/health/ready` fails closed (503 `db_unreachable`) and writes
are refused (503) rather than guessed — there is no local write path, no queue-and-reconcile-later. The
in-memory cache may still answer "seat taken" for ≤ 1 s, which is a safe refusal, never a sale.

Across a payment-gateway partition we stay consistent by keying every charge and refund: the pin keeps
the seat while we do not know, recovery asks the gateway later, and the ledger makes any double movement
detectable and compensated.

## 5. Scaling past 20k (the 200k question)

- DB work grows with **seats**, not requests: the cache and gate collapse a hot-seat storm to about one
  transaction, and a seat can only be won once.
- **Admission control**: a concurrency limiter on write routes (512 in flight, bounded queue); beyond it
  requests get an immediate **429 + Retry-After** — a 4xx, never a 5xx or a dropped connection.
- On one 2-vCPU VM the edge breaks before the database does, and I measured it rather than guessing:
  - Caddy needs about 57 KB per open TLS connection. It was OOM-killed at 128 MB (about 2k connections) and
    at 384 MB (about 6k). It now has 896 MB with `GOMEMLIMIT` (about 13k).
  - A kernel `connlimit` at 12k refuses connections beyond that with an immediate reset, rather than the
    proxy dying and dropping everyone.
  - Docker's userland port proxy timed out dialling at about 10k connections (502s). The edge now dials the
    API container directly, with a bounded upstream pool, so excess requests queue at the edge, not in
    the kernel.
  - Measured: 6 runners at once, about 10,500 concurrent TLS connections, 192,681 requests, **0 × 5xx and
    0 dropped**. Throughput is CPU-bound at about 900–1,000 req/s on this shared burstable VM; latency
    under that much concurrency is queueing, not failure.
- **Restarts are invisible.** Kestrel drains in-flight requests on SIGTERM. The edge re-dials while the
  process is down and retries cut-off requests, but only on routes that are idempotent by design.
  Restarting the API 30 s into a live 42k-request burst gave 0 × 5xx and 0 dropped connections. In an
  earlier version without those fixes, the restart cut off ~1,600 requests, yet no seat was double-granted
  and the ledger stayed exact: correctness never lived in process memory.
- Next steps for real 200k: a virtual waiting room in front of the on-sale; TLS at the edge (Front Door /
  App Gateway) with keep-alive and HTTP/2; N stateless replicas behind a load balancer with PgBouncer
  (correctness unchanged — Postgres still decides); push seat-map changes to clients so most never ask for
  a taken seat; shard by `show_id` when many shows sell at once.

## 6. Observability — what pages me at 2am

Metrics, structured logs with request ids, Grafana dashboard and Dozzle logs are described in the README.
The list below is not just prose: it is [`deploy/prometheus/alerts.yml`](deploy/prometheus/alerts.yml),
evaluated live by Prometheus and shown as "alerts firing" on the dashboard. Stopping Postgres fires
`SeatResDatabaseDown` within a minute (tested). I would page on:

1. any 5xx or `seatres_unhandled_errors_total` increase;
2. `seatres_reconciliation_ok == 0` (seat counts or ledger do not add up);
3. any `seatres_refunds_total{reason="seat_lost"}` (a customer paid for a seat they did not get);
4. `seatres_payment_pending` attempts older than 2 minutes (gateway or recovery stuck);
5. `/health/ready` failing for more than a minute;
6. DB pool saturated for more than 30 s.

Sustained 429s are a capacity-planning signal, not a page.

## 7. Measured

| Run | Requests | Result |
|---|---|---|
| Integration suite (real Postgres, in-memory layer off) | 150 tests | all pass; includes 500-on-one-seat, overlapping groups ×20, limit storms, payment races |
| Live, GitHub runner, `--workers 4 --concurrency 200 --stampede 40000 --overload 30000` | 72,600+ | 0 × 5xx, 0 transport errors; hot seat 1 × 201 + 499 × 409; reconciliation, ledger and `/metrics` all match; p50 650 ms, p99 1.45 s (client is ~200 ms RTT away) |
| Live, 6 runners at once (~10,500 concurrent TLS connections) | 192,681 | 0 × 5xx, 0 dropped; every shard's hot seat had exactly one winner |
| Live, API container restarted mid-burst | 41,987 | 0 × 5xx, 0 dropped; invariants exact |

Single-source bursts top out around 700–900 req/s because of the client side (one source IP's NAT ports
and TLS), not the service: during those runs the API used under 0.7 CPU and Postgres under 0.8.

## 8. AI usage — directed vs decided

I used Claude Code (Anthropic) throughout. Honest split:

**I decided** (and pushed back on the AI where it proposed otherwise):
- .NET for the entire service; Python only for the burst script.
- Deploy on my existing Azure VM in the existing resource group, under a new Azure DNS name, live for 10 days
  and then torn down automatically.
- The hold model: a 5-minute hold; confirmation allowed *after* expiry as long as nobody else has reserved
  the seat; plus an extra minute **only** while a payment is actually processing, so a user on a slow
  connection or a flaky gateway does not lose a genuine chance.
- That payment reversal had to be atomic/compensated, and the scenario I asked it to walk through: A holds at
  5:05, payment clears at 5:12, B sees the seat free at 5:11 — which led to pinning at payment start and the
  seat-lost refund.
- I challenged the design on connection counts ("won't 20,000 requests open 20,000 connections?"), on
  200,000 concurrent requests, and on concurrent overlapping seat requests (U1/U2 on A1–A3 vs U3 on A2).
  Those questions produced the per-seat gate, admission control, and the explicit lock-order argument.
- Scope: exactly what the brief asks — no UI, no category/best-available allocation.

**The AI did** (directed by me, reviewed by me):
- Proposed the core mechanism (row locks in `seat_no` order + conditional update + CHECK constraints) and
  the idempotency table design; I accepted after the walkthroughs above.
- Wrote the code, tests, burst script, Docker/Grafana/Caddy configuration and deploy scripts, test-first.
- Found and fixed issues during testing: Dapper not binding positional records with array columns; the
  idempotency/cache race above; a metrics gauge that never refreshed (integer overflow); the edge proxy
  OOM; GitHub's new OIDC subject format breaking the teardown login.
- An independent AI review pass found a real 500: a free show (price 0) crashed confirm, because the ledger
  rejects zero-amount rows. It also found counters incremented inside retryable transactions, and three
  ways the burst script could report a false pass. All fixed with tests.
- Load testing found that the burst checker flagged legal re-grants after an expired hold as
  double-sells. I had it verify against the database (0 overlapping live grants) before changing the
  checker to compare hold windows, not reservation ids.
- Chose not to modify the other apps' reverse proxy on the shared VM (its config is overwritten by that
  app's own deploys) and routed the new IP to a separate proxy instead.

I can explain and extend every part of it; the places I would extend first are below.

## 9. What I would do next

1. Category / best-available allocation (`{ "category": "deluxe", "quantity": 3 }`) that fills seats row by
   row and keeps groups together — the schema keeps `seat_no` order to make this a single locked query.
2. Suggested alternative seats in the 409 body.
3. Multiple replicas behind a load balancer with PgBouncer, and a shared (or no) taken-seat cache.
4. A real payment gateway behind `IPaymentGateway`, with webhooks feeding the same finalize step.
5. A virtual waiting room for the on-sale moment.
6. Alert rules shipped as code (Prometheus/Grafana alerting) rather than only documented.
