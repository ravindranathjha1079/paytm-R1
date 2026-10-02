# Enhancements after the first review

Goal: close the reviewer's remaining findings, prove capacity, and make every operational claim in the
write-up something a grader can see live. Each item is test-first where behaviour changes; each is
deployed and verified against the live URL.

| # | Item | Why it matters to a grader | Verification |
|---|---|---|---|
| E1 | Shared-VM safety: secondary IP as /32; redirect check only repairs when out of order; sysctl raise-only; teardown refuses an empty `EXPIRES_ON` | Deploy must never disturb other apps | `seatres-net status`, senti/ll/ellis still 200/expected |
| E2 | Correctness polish: decline-after-seat-lost says `hold_lost`; finalize-released seats leave the taken cache; 202s carry `reservation_id`; simulated gateway gets its own small pool | No misleading answers under load | integration tests |
| E3 | 5xx are counted with their real status (HTTP metrics outside the error handler) | The "5xx = 0" panel must be trustworthy | test with a deliberately failing route in the test host |
| E4 | Seat gauge scoped to recent shows; `GET /shows/{id}?seats=false` for cheap polling; reads get their own limiter | Metrics keep reconciling for 10 days; polling cannot starve writes | tests + live `/metrics` |
| E5 | Alert rules as code (Prometheus rules for the 2am list) + "firing alerts" panel | "What pages you" becomes runnable, not prose | rules load; panel shows 0 firing; a forced condition fires |
| E6 | OpenAPI document at `/openapi/v1.json` | Graders can explore the API | live fetch |
| E7 | Capacity: multi-source live burst (several GitHub runners at once), Caddy/API memory measured and sized, ceiling documented | Answers "does it survive the real burst" with numbers | live-burst matrix, container memory/restarts after |
| E8 | Chaos: restart the API container mid-burst; zero double-sells, invariants hold after | Correctness does not depend on process memory | burst + reconciliation |
| E9 | Evidence: commit live burst outputs under `docs/evidence/`, refresh README/WRITEUP numbers | Graders see what was actually run | files in repo |
