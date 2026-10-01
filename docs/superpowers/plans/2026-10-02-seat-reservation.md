# Seat Reservation at Scale — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A .NET 10 JSON API that sells assigned seats with zero double-sells, zero 5xx under bursts, exactly-once payments, full observability, deployed on `senti-vm` with a 10-day auto-teardown.

**Architecture:** ASP.NET Core minimal APIs over a single Postgres. Every seat decision is a short READ COMMITTED transaction that takes row locks in one global order (user lock → seats by `seat_no` → reservations → attempts) and claims seats with a conditional `UPDATE … WHERE <takeable>`. In-memory taken-seat cache + striped per-seat gate + admission limiter only decline/delay. Payments are a pin → gateway → finalize saga with ledger and compensation.

**Tech Stack:** .NET 10, Npgsql, Dapper, Serilog (compact JSON, async console), prometheus-net, JWT bearer (HS256), xUnit 2.9 + Testcontainers.PostgreSql + Microsoft.AspNetCore.Mvc.Testing + Microsoft.Extensions.TimeProvider.Testing, Postgres 16, Prometheus, Grafana, Dozzle, Caddy, Python 3.11+ aiohttp (burst only), GitHub Actions, GHCR, Azure CLI.

**Spec:** `docs/superpowers/specs/2026-10-02-seat-reservation-design.md`

## Global Constraints

- Service code is 100% .NET; Python only in `tools/burst/`.
- Money is `long`/`bigint` paise. JSON numbers with fractions for money are rejected (400).
- All SQL takes current time as `@now` from `TimeProvider`; never `now()` in domain SQL.
- Lock order everywhere: idempotency insert (own key only) → `user_show_locks` → `seats ORDER BY seat_no` → `reservations ORDER BY id` → `payment_attempts`/`payments`.
- Hold = 300 s; payment grace = 60 s; `pay_deadline = greatest(hold_expires_at, now) + grace` unless a running deadline exists.
- Npgsql `Maximum Pool Size=30`; `lock_timeout 2s`, `statement_timeout 5s` per transaction.
- JSON is snake_case; errors are `{error, message, request_id, …}`; every response has `X-Request-Id`.
- No 5xx for domain outcomes; transient DB contention → 429 `overloaded`; DB unreachable → 503 `db_unavailable`.
- Commits authored by the user's identity; **no AI co-author trailers**.
- Public hostname `seatres-paytm.centralindia.cloudapp.azure.com`; never modify or remove senti resources.

## Review Focus

1. Retry of a *winning* idempotency key while the fast path is on must replay 200, not 409 — test in Task 9.
2. Client disconnects while the gateway is processing: payment must still finalize (confirmed or refunded) — test in Task 6.
3. Seat labels are case-sensitive exact matches (`a1` ≠ `A1` → 400 `unknown_seats`) — test in Task 4.
4. Oversized reserve payloads (> 64 labels) → fast 400, never a slow DB scan — test in Task 4.
5. A hold expiring exactly at `now` is takeable (`<=`), and a confirm at that instant still pins if unclaimed — unit test in Task 2, integration in Task 5.

---

### Task 0: Toolchain

- [ ] Install .NET 10 SDK and GitHub CLI (`winget install Microsoft.DotNet.SDK.10`, `winget install GitHub.cli`).
- [ ] Start Docker Desktop; verify `docker info` succeeds (Testcontainers needs it).
- [ ] Verify `dotnet --list-sdks` shows `10.0.x`.

### Task 1: Solution scaffold and cross-cutting plumbing

**Files:**
- Create: `global.json`, `SeatRes.slnx`, `Directory.Build.props`
- Create: `src/SeatRes.Api/SeatRes.Api.csproj`, `Program.cs`, `ServiceRegistration.cs`, `Options.cs`, `HealthProbe.cs`
- Create: `src/SeatRes.Api/Domain/{ApiResult.cs, ErrorCodes.cs, Json.cs}`
- Create: `src/SeatRes.Api/Observability/{RequestIdMiddleware.cs, ErrorHandlingMiddleware.cs, LoggingSetup.cs, SeatResMetrics.cs}`
- Create: `tests/SeatRes.UnitTests/*`, `tests/SeatRes.IntegrationTests/{Infrastructure/ApiFactory.cs, HealthTests.cs}`

**Interfaces (produced):**
- `ApiResult(int StatusCode, JsonObject? Body, string? Reason = null, bool Replayed = false) : IResult` with `Ok(object dto, int status)`, `Fail(int status, string code, string message, IDictionary<string, object?>? extra = null)`, `WriteAsync(HttpContext)`.
- `Json.Options` (snake_case, ignore nulls), `Json.ToNode<T>(T)`.
- `ErrorHandlingMiddleware.Classify(Exception) → (int status, string code)` (internal, unit-tested).
- `SeatResMetrics` static counters/gauges (full catalogue from spec §6.1).
- `ServiceRegistration.AddSeatRes(IServiceCollection, IConfiguration)`, `UseSeatResPipeline(WebApplication)`, `MapSeatResEndpoints(WebApplication)`.

- [ ] Write unit tests for `Classify`: `PostgresException` 40P01/40001/55P03/57014 → 429 `overloaded`; pool-exhausted `NpgsqlException` → 429; other `NpgsqlException` → 503 `db_unavailable`; `BadHttpRequestException`/`JsonException` → 400; anything else → 500 `internal`.
- [ ] Write integration tests: `GET /health/live` → 200; response carries generated `X-Request-Id`; a valid incoming `X-Request-Id` is echoed; an invalid one (`bad id!`) is replaced; `GET /metrics` contains `seatres_unhandled_errors_total`.
- [ ] Run → fail. Implement. Run → pass.
- [ ] Commit `Scaffold API with request ids, JSON errors, logging and metrics`.

### Task 2: Domain rules

**Files:** `src/SeatRes.Api/Domain/{SeatRules.cs, RequestHash.cs, SeatLabel.cs, IdempotencyKey.cs}`, `tests/SeatRes.UnitTests/{SeatRulesTests.cs, RequestHashTests.cs, IdempotencyKeyTests.cs}`

**Interfaces (produced):**
- `SeatRules.TakeableSql`, `SeatRules.EffectiveStatusSql` (both use `@now`).
- `SeatRules.IsTakeable(string status, DateTime? holdExpiresAt, DateTime? payDeadline, DateTime now) : bool`
- `SeatRules.Effective(...) : string` (`available|held|confirmed`)
- `SeatRules.PayDeadline(string reservationStatus, DateTime? holdExpiresAt, DateTime? currentDeadline, DateTime now, TimeSpan grace) : DateTime`
- `RequestHash.ForReserve(Guid showId, IEnumerable<string> labels, bool confirm) : byte[]` (order-insensitive), `RequestHash.ForConfirm(Guid reservationId) : byte[]`
- `SeatLabel.IsValid(string)` (`^[A-Za-z0-9-]{1,16}$`)
- `IdempotencyKey.Resolve(string? header, string? body) : (string? Key, ApiResult? Error)` (both present & different → 400; missing → 400; > 128 chars or non-printable → 400).

- [ ] Tests: takeable boundaries (expiry == now → takeable; now − 1 tick → not), payment_pending vs deadline, confirmed never takeable; effective mapping table from spec §3.1; PayDeadline cases (inside hold → hold+60 s; after expiry → now+60 s; running deadline unchanged; expired running deadline → now+60 s); hash order-insensitivity and confirm-flag sensitivity; key resolution cases.
- [ ] Fail → implement → pass → commit `Add seat state, deadline and idempotency rules`.

### Task 3: Database, migrations, readiness

**Files:** `src/SeatRes.Api/Data/{Db.cs, Migrator.cs, MigrationHostedService.cs, MigrationState.cs, Migrations/0001_init.sql}`, `Endpoints/HealthEndpoints.cs`; tests `Infrastructure/{PostgresFixture.cs, TestClients.cs}`, `ReadinessTests.cs`.

**Interfaces (produced):**
- `Db.InTx<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct)` — sets `lock_timeout`/`statement_timeout`, retries once on 40P01/40001/55P03, counts `seatres_db_retries_total`.
- `Db.DataSource` (NpgsqlDataSource).
- `MigrationState.Applied` (volatile bool).
- Schema exactly as spec §3.3 plus `payment_attempts(idem_user, idem_scope, idem_key, amount_paise, refund_reason)` and attempt statuses `pending|succeeded|declined|unknown|refund_pending|refunded`.
- `/health/ready`: 503 `migrations_pending` / 503 `db_unreachable` (1 s budget) / 200.

- [ ] Tests: ready → 200 after startup; migrations apply twice without error; with a dedicated container stopped, `/health/ready` → 503 within 3 s and `/health/live` still 200.
- [ ] Fail → implement → pass → commit `Add Postgres schema, migrations and readiness`.

### Task 4: Auth, shows, catalogue

**Files:** `Auth/{TokenService.cs, CurrentUser.cs}`, `Endpoints/{AuthEndpoints.cs, ShowEndpoints.cs}`, `Data/{ShowStore.cs, ShowCatalog.cs}`, `Domain/ShowDtos.cs`; tests `AuthTests.cs`, `ShowTests.cs`.

**Interfaces (produced):**
- `POST /auth/token {user_id}` → `{token, user_id, expires_at}`; `POST /auth/admin-token` (`X-Admin-Key`) → `{token}`; `GET /me` → `{user_id, role}`.
- `ClaimsPrincipal.UserId()`.
- `ShowInfo(Guid Id, string Name, long PricePaise, int PerUserLimit, int TotalSeats, string[] Labels)` with `SeatNoByLabel` (1-based).
- `ShowCatalog.GetAsync(Guid, CancellationToken) : Task<ShowInfo?>` (immutable cache).
- `ShowStore.GetStateAsync(Guid, DateTime now)` → counts, seats, ledger, `reconciled`.

- [ ] Tests: token mint/validation; invalid user id 400; bad admin key 401; `POST /shows` without token 401, user token 403; create → 201 all available, labels in order; duplicates/invalid/empty/too many → 400; fractional `price_paise` → 400; `GET` unknown → 404; counts sum to total.
- [ ] Fail → implement → pass → commit `Add token auth and show creation/state`.

### Task 5: Reserve (the atomic decision) + reservation read

**Files:** `Data/{IdempotencyStore.cs, UserLock.cs, Rows.cs, ReserveTx.cs, ReservationStore.cs}`, `Domain/ReservationDtos.cs`, `Services/{ReservationService.cs, OutcomeRecorder.cs}`, `Endpoints/ReservationEndpoints.cs`; tests `ReserveConcurrencyTests.cs`, `ReserveIdempotencyTests.cs`, `ReserveValidationTests.cs`, `HoldExpiryTests.cs`, `Infrastructure/Invariants.cs`.

**Interfaces (produced):**
- `ReserveCommand(ShowInfo Show, string UserId, string IdemKey, byte[] RequestHash, int[] SeatNos, bool Confirm)`
- `ReserveTx.ExecuteAsync(ReserveCommand, CancellationToken) : Task<ReserveTxResult>` where `ReserveTxResult(ApiResult Response, Guid? ReservationId, int[] ClaimedSeatNos, (int SeatNo, DateTime? Until)[] TakenSeats, PinnedPayment? Pinned)`
- `IdempotencyStore.{TryInsertAsync, GetAsync, StoreAsync, FindAsync, Replay}`
- `ReservationService.ReserveAsync(Guid showId, string userId, ReserveRequest req, string? headerKey, string? gatewayHint, CancellationToken)`
- `GET /reservations/{id}` (owner only, 404 otherwise).

Algorithm exactly as spec §4.2 step 4 (savepoint after idempotency insert; declines stored).

- [ ] Tests (fast path off): single reserve 201 held with `expires_at = now+5m`; 500 users on one seat → exactly 1×201 + 499×409 `seat_taken`; 10 users on the same 2 seats; group `[A1,A2,A3]`×2 vs `[A2]` repeated 20× (one owner per seat, losers own nothing, A1/A3 free when single wins); 50 users random overlapping sets on 6 seats (no 5xx, held == Σ winner seats); one user 10 parallel distinct seats on limit 4 → exactly 4×201; 5 seats in one request → 409 `per_user_limit`; same key 20× concurrent → 1×201 + 19×200 replay same id; same key other seats → 409 `idempotency_mismatch`; declined request retried → same 409; body `user_id` spoof ignored; validation 400s (empty, duplicates, unknown, case-mismatch `a1`, > 64 labels, missing key, header≠body key); 401/404.
- [ ] Expiry tests (fake clock): after 5:00 another user claims → 201; original reservation becomes `expired`; `GET /shows` shows expired hold as available before any claim; expired holds stop counting toward the limit; hold expiring exactly at `now` is claimable.
- [ ] Fail → implement → pass → commit `Add atomic seat reservation with idempotency and per-user limit`.

### Task 6: Payments — gateway, confirm, finalize, refunds

**Files:** `Payments/{IPaymentGateway.cs, SimulatedGateway.cs}`, `Data/{PaymentTx.cs, ReservationLocks.cs}`, `Services/PaymentService.cs`, endpoint `POST /reservations/{id}/confirm`; tests `Infrastructure/ScriptedGateway.cs`, `ConfirmTests.cs`.

**Interfaces (produced):**
- `enum GatewayStatus { Succeeded, Declined, Unknown, NotFound }`
- `IPaymentGateway.ChargeAsync(string key, long amountPaise, string? hint, CancellationToken)`, `QueryAsync(string key, CancellationToken)`, `RefundAsync(string key, CancellationToken) : Task<bool>`
- `PinnedPayment(Guid AttemptId, string GatewayKey, long AmountPaise, string? Hint)`
- `PaymentTx.PinAsync(Guid reservationId, string caller, string key, byte[] hash, string? hint, CancellationToken) : Task<PinResult>`
- `PaymentTx.FinalizeAsync(Guid attemptId, GatewayStatus outcome, CancellationToken) : Task<FinalizeResult(ApiResult Response, Guid? RefundAttemptId)>`
- `PaymentTx.MarkRefundedAsync(Guid attemptId, CancellationToken)`
- `PaymentService.ConfirmAsync(...)`, `RunAsync(PinnedPayment)` (uses `CancellationToken.None` once pinned), `RefundAsync(Guid attemptId) : Task<bool>`.

- [ ] Tests: hold→confirm 200 confirmed, ledger charged once; replay same key → 200, gateway charged once; non-owner 404; decline → 402 `retry_until = hold+60s`, other user still 409, retry with new key → confirmed, one charge; blocked gateway while clock passes 5:30 → other user 409 (pinned), release → 200; late confirm at 7:00 unclaimed → 200; reclaimed then confirm → 409 `hold_lost`, no charge; pinned at 4:59, clock to 6:01, other user claims, gateway succeeds → 409 `hold_lost` `refunded:true`, ledger net 0, reservation `refunded`; concurrent confirm different key → 409 `payment_in_progress`, same key → 202; late confirm over limit → 409 `per_user_limit`; client aborts the confirm request while gateway is blocked → after release the reservation is confirmed.
- [ ] Fail → implement → pass → commit `Add payment pin/charge/finalize with ledger and compensation`.

### Task 7: Hold-and-pay in one call, and cancel

**Files:** modify `Data/ReserveTx.cs`, `Services/ReservationService.cs`; create `Data/CancelTx.cs`, endpoint `POST /reservations/{id}/cancel`; tests `CancelTests.cs`, `ReserveConfirmTests.cs`.

- [ ] Tests: `confirm:true` → 201 confirmed in one call; decline → 402 and seats available; replay → 200 confirmed; in-flight replay → 202; cancel held → seats available and re-bookable; non-owner cancel → 404 and seat untouched; cancel confirmed → 200 refunded, ledger net 0; expired-and-reclaimed cancel → 409 `reservation_closed`, new owner keeps seat; cancel twice → 200 both.
- [ ] Fail → implement → pass → commit `Add hold-and-pay shortcut and owner cancel with refunds`.

### Task 8: Background jobs, gauges, metrics reconciliation

**Files:** `Background/{PaymentRecovery.cs, Reconciler.cs, PeriodicWorker.cs, SeatGauge.cs}`; tests `RecoveryTests.cs`, `ReconcilerTests.cs`, `MetricsTests.cs`.

- [ ] Tests: unknown→settled-succeeded attempt is confirmed by `RunOnceAsync`; unknown→never-charged older than 2 min becomes declined; failed refund is retried until refunded; reconciler OK on healthy data and flags a tampered ledger row; `/metrics` `seatres_seats{state="held"}` equals `GET /shows` held count; confirmed counter delta equals confirmed reservations.
- [ ] Fail → implement → pass → commit `Add payment recovery, reconciler and DB-backed gauges`.

### Task 9: Fast path and admission control

**Files:** `Concurrency/{TakenSeatCache.cs, SeatGate.cs}`, modify `Services/ReservationService.cs`, `ServiceRegistration.cs`; tests `TakenSeatCacheTests.cs`, `SeatGateTests.cs` (unit), `FastPathTests.cs`, `AdmissionTests.cs`.

- [ ] Unit: cache TTL and `notAfter` bound; gate stripes sorted/deduplicated; gate exclusive; gate timeout returns empty lease.
- [ ] Integration (fast path on): 500 on one seat → exactly 1×201, fast-path declines > 0; winning key retried → 200 replay; after cancel the seat is re-bookable within 1 s; admission limit 1/queue 0 with blocked gateway → second write 429 with `Retry-After`.
- [ ] Fail → implement → pass → commit `Add taken-seat cache, per-seat gate and admission limiter`.

### Task 10: Container, compose, dashboards

**Files:** `Dockerfile`, `.dockerignore`, `compose.yaml`, `.env.example`, `deploy/prometheus.yml`, `deploy/grafana/provisioning/{datasources,dashboards}/*.yml`, `deploy/grafana/dashboards/onsale.json`, `Makefile`.

- [ ] `docker compose up -d --build` from clean checkout → api healthy, `/health/ready` 200, Grafana dashboard loads, Dozzle shows only `seatres` containers; stop postgres → ready 503; start → 200.
- [ ] Commit `Add Dockerfile, compose stack, Prometheus and Grafana dashboard`.

### Task 11: Burst script

**Files:** `tools/burst/{burst.py, requirements.txt}`, `burst.sh`.

- [ ] Run against local compose; scenarios: hot-seat, group overlap, stampede (20k), idempotency, per-user limit, spoof, payments, `--overload`; prints distribution, p50/p99, reconciliation vs `/metrics`; exit 1 on any violation.
- [ ] Commit `Add one-command burst script`.

### Task 12: CI

**Files:** `.github/workflows/ci.yml` — build, unit + integration tests, Docker build, compose up + `burst.sh --quick`.

- [ ] Commit `Add CI workflow`.

### Task 13: Azure network, VM bootstrap, Caddy, deploy

**Files:** `deploy/azure/network-up.sh`, `deploy/vm/{bootstrap.sh, seatres-secondary-ip.service, 99-seatres.conf (sysctl), seatres.caddy.tmpl}`, `deploy/compose.prod.yaml`, `.github/workflows/deploy.yml`.

- [ ] Create `seatres-paytm-ip` (Standard, static, DNS label `seatres-paytm`) and NIC ipconfig `seatres-ipconfig` at `10.0.0.10`.
- [ ] Inspect senti's Caddy (systemd vs container) and add our site block via `import` without altering senti's blocks (backup first).
- [ ] Deploy; verify `https://seatres-paytm.centralindia.cloudapp.azure.com/health/ready` 200 and senti's URL unchanged.
- [ ] Commit `Add Azure network setup, VM bootstrap and deploy workflow`.

### Task 14: Teardown

**Files:** `deploy/vm/{teardown.sh, seatres-teardown.service, seatres-teardown.timer.tmpl}`, `deploy/azure/{network-down.sh, oidc-setup.sh}`, `.github/workflows/teardown.yml`.

- [ ] `teardown.sh --dry-run` lists only our artefacts; timer installed for deploy date + 10 days; workflow deletes ipconfig + IP on/after `EXPIRES_ON`.
- [ ] Commit `Add 10-day automatic teardown`.

### Task 15: README, WRITEUP, live verification

- [ ] README: run locally, deploy, burst usage, URLs (API, Grafana, logs), admin key handling.
- [ ] WRITEUP.md per spec §12.
- [ ] Run `./burst.sh https://seatres-paytm…` against live; record results in README.
- [ ] Commit `Add README and write-up`.
