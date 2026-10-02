#!/usr/bin/env python3
"""
Synchronised flood: many runners fire all their reserve requests at the same instant, over HTTP/2, so tens
of thousands of requests are in flight at once without needing tens of thousands of TLS connections.

  flood.py setup  BASE_URL --shows 3                   -> prints {"shows": [...], "start_at": epoch}
  flood.py fire   BASE_URL --show ID --requests 5000 --start-at EPOCH --out shard.json
  flood.py verify BASE_URL shard-*.json                -> merges shards, checks every show, exit 1 on violation

Each fire shard: mints its users' tokens, waits for start_at, then launches every request at once
(70% at ten "good" seats, 30% spread over the hall, 5% sent twice with the same idempotency key) and records
each outcome. verify then checks, per show: no seat granted to two reservations whose holds overlap,
zero 5xx, zero requests without a response, and GET /shows/{id} reconciles (counts and ledger).
"""
from __future__ import annotations

import argparse
import asyncio
import collections
import datetime
import json
import os
import random
import string
import sys
import time
import uuid

import httpx

ROWS = string.ascii_uppercase[:20]
PER_ROW = 50
SEATS = [f"{r}{i}" for r in ROWS for i in range(1, PER_ROW + 1)]
HOT = [f"C{i}" for i in range(1, 11)]


def client(base: str, connections: int) -> httpx.AsyncClient:
    return httpx.AsyncClient(
        base_url=base.rstrip("/"), http2=True,
        limits=httpx.Limits(max_connections=connections, max_keepalive_connections=connections),
        timeout=httpx.Timeout(connect=30, read=240, write=60, pool=None))


async def setup(args) -> int:
    admin_key = os.environ.get("ADMIN_KEY", "local-admin-key")
    async with client(args.base_url, 4) as c:
        tok = (await c.post("/auth/admin-token", headers={"X-Admin-Key": admin_key})).json()["token"]
        shows = []
        for i in range(args.shows):
            r = await c.post("/shows", headers={"Authorization": f"Bearer {tok}"},
                             json={"name": f"flood-{uuid.uuid4().hex[:6]}-{i}", "seats": SEATS, "price_paise": 25000})
            r.raise_for_status()
            shows.append(r.json()["id"])
    print(json.dumps({"shows": shows, "start_at": int(time.time()) + args.lead}))
    return 0


async def fire(args) -> int:
    rng = random.Random(f"{args.show}-{args.shard}")
    users = max(1, args.requests // 4)
    plan = []
    for _ in range(args.requests):
        seats = [rng.choice(HOT)] if rng.random() < 0.7 else rng.sample(SEATS, rng.choice([1, 1, 2]))
        plan.append((rng.randrange(users), seats, uuid.uuid4().hex))
    # 5% are retried concurrently with the same key, like a client that timed out and resent.
    plan += [plan[i] for i in rng.sample(range(len(plan)), len(plan) // 20)]

    results: list[dict] = []
    inflight = peak = 0
    async with client(args.base_url, args.connections) as c:
        async def mint(u: int) -> str:
            for attempt in range(5):
                try:
                    r = await c.post("/auth/token", json={"user_id": f"f{args.shard}-{u}-{args.show[:8]}"})
                    if r.status_code == 200:
                        return r.json()["token"]
                except httpx.HTTPError:
                    pass
                await asyncio.sleep(0.5 * (attempt + 1))
            raise RuntimeError("token mint failed")
        tokens = await asyncio.gather(*(mint(u) for u in range(users)))
        await c.get("/health/ready")  # warm the HTTP/2 connections before the start

        wait = args.start_at - time.time()
        print(f"shard {args.shard}: {len(plan)} requests for show {args.show}, firing in {wait:.1f}s", flush=True)
        if wait > 0:
            await asyncio.sleep(wait)

        async def one(user: int, seats: list[str], key: str) -> None:
            nonlocal inflight, peak
            inflight += 1
            peak = max(peak, inflight)
            t0 = time.perf_counter()
            entry = {"seats": seats, "key": key}
            try:
                r = await c.post(f"/shows/{args.show}/reserve", headers={"Authorization": f"Bearer {tokens[user]}"},
                                 json={"seats": seats, "idempotency_key": key})
                body = r.json() if r.content else {}
                entry.update(status=r.status_code, error=body.get("error"))
                if r.status_code in (200, 201):
                    entry.update(rid=body["reservation_id"], won=body["seats"], at=body.get("server_time"),
                                 until=body.get("expires_at"))
            except (httpx.HTTPError, ValueError) as e:
                entry.update(status=0, error=type(e).__name__)
            finally:
                inflight -= 1
            entry["ms"] = round((time.perf_counter() - t0) * 1000)
            results.append(entry)

        started = time.time()
        await asyncio.gather(*(one(u, s, k) for u, s, k in plan))
        elapsed = time.time() - started

    out = {"show": args.show, "shard": args.shard, "fired_at": started, "elapsed": elapsed,
           "peak_inflight": peak, "results": results}
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f)
    counts = collections.Counter(f"{r['status']} {r['error'] or ''}".strip() for r in results)
    print(f"shard {args.shard}: peak in flight {peak}, done in {elapsed:.1f}s: {dict(counts)}")
    return 0


def ts(v: str | None) -> datetime.datetime:
    if not v:
        return datetime.datetime.max
    v = v.rstrip("Z")
    if "." in v:
        head, frac = v.split(".", 1)
        v = f"{head}.{frac[:6]}"
    return datetime.datetime.fromisoformat(v)


async def verify(args) -> int:
    shards = [json.load(open(p, encoding="utf-8")) for p in args.files]
    by_show: dict[str, list[dict]] = collections.defaultdict(list)
    for s in shards:
        by_show[s["show"]].append(s)
    bad: list[str] = []
    total = 0
    fired = [s["fired_at"] for s in shards]
    peak_total = sum(s["peak_inflight"] for s in shards)
    print(f"{len(shards)} shards, {len(by_show)} shows; start spread {max(fired) - min(fired):.2f}s; "
          f"sum of per-shard peak in-flight requests: {peak_total}")
    async with client(args.base_url, 4) as c:
        for show, parts in by_show.items():
            res = [r for p in parts for r in p["results"]]
            total += len(res)
            counts = collections.Counter(f"{r['status']} {r['error'] or ''}".strip() for r in res)
            five = sum(n for k, n in counts.items() if k.startswith("5"))
            dropped = sum(n for k, n in counts.items() if k.startswith("0"))
            grants: dict[str, dict[str, tuple]] = collections.defaultdict(dict)
            for r in res:
                for seat in r.get("won", []):
                    grants[seat][r["rid"]] = (ts(r.get("at")), ts(r.get("until")))
            overlaps = [seat for seat, g in grants.items()
                        if any(b[0] < a[1] for a, b in zip(sorted(g.values()), sorted(g.values())[1:]))]
            state = (await c.get(f"/shows/{show}?seats=false")).json()
            cnt = state["counts"]
            ms = sorted(r["ms"] for r in res)
            print(f"\nshow {show}: {len(res)} requests from {len(parts)} shards (peak in flight "
                  f"{sum(p['peak_inflight'] for p in parts)})")
            print(f"  outcomes: {dict(sorted(counts.items()))}")
            print(f"  seats won {len(grants)}, overlapping grants {len(overlaps)}; "
                  f"available {cnt['available']} + held {cnt['held']} + confirmed {cnt['confirmed']} = "
                  f"{cnt['available'] + cnt['held'] + cnt['confirmed']} / {state['total_seats']}, reconciled {state['reconciled']}")
            print(f"  latency p50 {ms[len(ms) // 2]} ms, p99 {ms[int(len(ms) * 0.99)]} ms, max {ms[-1]} ms")
            if five: bad.append(f"show {show}: {five} x 5xx")
            if dropped: bad.append(f"show {show}: {dropped} requests got no response")
            if overlaps: bad.append(f"show {show}: seats granted to overlapping reservations: {overlaps[:5]}")
            if not state["reconciled"] or len(grants) != cnt["held"] + cnt["confirmed"]:
                bad.append(f"show {show}: reconciliation mismatch (won {len(grants)} vs held+confirmed "
                           f"{cnt['held'] + cnt['confirmed']})")
    print(f"\ntotal {total} requests")
    if bad:
        print("VIOLATIONS"); [print(" ", b) for b in bad]
        return 1
    print("ALL CHECKS PASSED")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("setup"); s.add_argument("base_url"); s.add_argument("--shows", type=int, default=3)
    s.add_argument("--lead", type=int, default=150, help="seconds from now until the synchronised start")
    f = sub.add_parser("fire"); f.add_argument("base_url"); f.add_argument("--show", required=True)
    f.add_argument("--shard", type=int, default=0); f.add_argument("--requests", type=int, default=5000)
    f.add_argument("--connections", type=int, default=60, help="HTTP/2 connections (each carries many streams)")
    f.add_argument("--start-at", type=float, default=0); f.add_argument("--out", default="shard.json")
    v = sub.add_parser("verify"); v.add_argument("base_url"); v.add_argument("files", nargs="+")
    args = p.parse_args()
    return asyncio.run({"setup": setup, "fire": fire, "verify": verify}[args.cmd](args))


if __name__ == "__main__":
    sys.exit(main())
