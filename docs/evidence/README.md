# Evidence: runs against the live service

Raw output of `burst.sh`, kept as produced. Each run creates a fresh 1,000-seat show and checks every
invariant itself. A run exits non-zero on any 5xx, any request that got no HTTP response, any seat granted
to two reservations whose holds overlap, or any reconciliation mismatch.

| File | What was run | Outcome |
|---|---|---|
| `live-6-shards-shard-{1..6}.txt` | 6 GitHub runners at once (`live-burst.yml`, `shards=6`, `--workers 4 --concurrency 350 --stampede 30000`): about 10,500 concurrent TLS connections | 192,739 requests in total, about 1,470 req/s combined; **0 × 5xx, 0 dropped**. Each shard: 500-user hot seat → exactly 1 × 201 + 499 × 409; idempotency, per-user limit, spoofing and payments all pass; counts, ledger and `/metrics` reconcile |
| `live-api-restart-mid-burst.txt` | 42k-request burst from one client; `docker restart seatres-api-1` on the VM 30 s in | **0 × 5xx, 0 dropped** while the API process restarted: Kestrel drains in-flight requests, the edge re-dials and retries idempotent routes; invariants hold |
| `local-chaos-api-restart-before-drain.txt` | Same chaos test locally, *before* graceful drain and edge retries existed | About 1,600 requests cut off by the restart, but **no seat double-granted** and the ledger exact. Correctness never depended on process memory; the later fixes removed the visible errors |
| `live-laptop-burst.txt` | First live run, from a laptop (single process, 800 connections) | All checks pass; 21k requests, 0 × 5xx |

## What the measurements showed (and what changed because of them)

- **Edge memory is the first ceiling.** Caddy needs about 57 KB per open TLS connection. At a 128 MB limit it
  was OOM-killed at about 2k connections, and at 384 MB at about 6k. It now has 896 MB (~13k connections).
  An iptables `connlimit` at 12k refuses connections beyond that with an immediate TCP reset, instead of
  letting the proxy die and drop everyone.
- **Docker's userland port proxy timed out dialling** at about 10k concurrent connections (502s). The edge
  now dials the API container's fixed internal IP, with at most 768 upstream connections; excess requests
  queue at the edge.
- **One client is not a load test.** A single source IP tops out at about 1k connections (NAT/SNAT, TLS in
  Python). Multi-runner shards were needed to reach about 10k.
- **Throughput on this VM is CPU-bound.** It was about 920 req/s, and about 1,470 req/s after the edge fixes and declines for never-used keys stopped touching the database (exact memory-only declines). It is a 2-vCPU burstable machine shared
  with three other apps. The p50 under 8,400 in-flight requests is queueing time, not errors. seatres
  containers run at reduced CPU weight so the other apps win contention.
