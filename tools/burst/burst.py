#!/usr/bin/env python3
"""
On-sale stampede generator for the seat reservation service.

Creates a fresh show, then runs scenarios that mirror the take-home's correctness bar:
  hot-seat storm, group overlap, ~20k stampede (with retries), idempotency, per-user limit,
  identity spoofing, payments/cancel, and (optionally) an overload ramp.
Prints the outcome distribution, latency, and a final reconciliation against GET /shows/{id}
and /metrics. Exits 1 if any correctness rule is violated.

  ADMIN_KEY=... python burst.py https://seatres-paytm.centralindia.cloudapp.azure.com
"""
from __future__ import annotations

import argparse
import asyncio
import collections
import concurrent.futures
import datetime
import json
import os
import random
import statistics
import string
import sys
import time
import uuid

import aiohttp

ROWS = string.ascii_uppercase[:20]  # A..T
PER_ROW = 50                        # 1,000 seats


# --------------------------------------------------------------------------- plumbing

class Tally:
    def __init__(self) -> None:
        self.counts: collections.Counter[str] = collections.Counter()
        self.latencies: list[float] = []
        self.errors: collections.Counter[str] = collections.Counter()

    def add(self, status: int, body: dict, seconds: float) -> None:
        if status == 0:
            key = "transport-error"
            self.errors[str(body.get("message", ""))[:120]] += 1
        elif status >= 400 and isinstance(body, dict) and body.get("error"):
            key = f"{status} {body['error']}"
        else:
            key = str(status)
        self.counts[key] += 1
        self.latencies.append(seconds)

    def merge(self, other: "Tally") -> None:
        self.counts.update(other.counts)
        self.latencies.extend(other.latencies)
        self.errors.update(other.errors)

    @property
    def server_errors(self) -> int:
        return sum(n for k, n in self.counts.items() if k[:1] == "5")

    def to_dict(self) -> dict:
        return {"counts": dict(self.counts), "latencies": self.latencies, "errors": dict(self.errors)}

    @staticmethod
    def from_dict(d: dict) -> "Tally":
        t = Tally()
        t.counts.update(d["counts"])
        t.latencies.extend(d["latencies"])
        t.errors.update(d.get("errors", {}))
        return t


class Api:
    def __init__(self, base: str, session: aiohttp.ClientSession, concurrency: int) -> None:
        self.base = base.rstrip("/")
        self.session = session
        self.sem = asyncio.Semaphore(concurrency)

    async def call(self, method: str, path: str, token: str | None = None, body: dict | None = None,
                   headers: dict | None = None, tally: Tally | None = None) -> tuple[int, dict]:
        h = {"Content-Type": "application/json", "X-Request-Id": "burst-" + uuid.uuid4().hex[:16]}
        if token:
            h["Authorization"] = f"Bearer {token}"
        h.update(headers or {})
        async with self.sem:
            start = time.perf_counter()
            try:
                async with self.session.request(method, self.base + path, json=body, headers=h) as r:
                    text = await r.text()
                    status = r.status
                try:
                    data = json.loads(text) if text else {}
                except ValueError:
                    data = {"raw": text[:200]}
            except (aiohttp.ClientError, asyncio.TimeoutError) as e:
                status, data = 0, {"error": "transport", "message": str(e)}
            elapsed = time.perf_counter() - start
        if tally is not None:
            tally.add(status, data, elapsed)
        return status, data

    async def token(self, user_id: str) -> str:
        # Setup, not the thing under test: retry a few times before giving up.
        for attempt in range(4):
            status, body = await self.call("POST", "/auth/token", body={"user_id": user_id})
            if status == 200:
                return body["token"]
            await asyncio.sleep(0.5 * (attempt + 1))
        raise RuntimeError(f"token mint failed for {user_id}: {status} {body}")

    async def tokens(self, prefix: str, n: int) -> list[str]:
        return await asyncio.gather(*(self.token(f"{prefix}-{i}") for i in range(n)))

    async def reserve(self, token: str, show: str, seats: list[str], key: str | None = None,
                      confirm: bool | None = None, extra: dict | None = None, headers: dict | None = None,
                      tally: Tally | None = None) -> tuple[int, dict]:
        body = {"seats": seats, "idempotency_key": key or uuid.uuid4().hex}
        if confirm is not None:
            body["confirm"] = confirm
        body.update(extra or {})
        return await self.call("POST", f"/shows/{show}/reserve", token, body, headers, tally)


def new_session(concurrency: int, insecure: bool, keepalive: float = 60) -> aiohttp.ClientSession:
    connector = aiohttp.TCPConnector(limit=concurrency, limit_per_host=concurrency, ssl=False if insecure else None,
                                     keepalive_timeout=keepalive)
    return aiohttp.ClientSession(connector=connector, timeout=aiohttp.ClientTimeout(total=60))


class Report:
    def __init__(self) -> None:
        self.sections: list[tuple[str, Tally, list[str]]] = []
        self.violations: list[str] = []

    def add(self, name: str, tally: Tally, notes: list[str], violations: list[str]) -> None:
        if tally.server_errors:
            violations.append(f"{tally.server_errors} server errors (5xx)")
        dropped = tally.counts.get("transport-error", 0)
        if dropped:
            violations.append(f"{dropped} requests got no HTTP response (reset/timeout) - "
                              "check whether the client or the service dropped them")
        self.sections.append((name, tally, notes))
        self.violations.extend(f"[{name}] {v}" for v in violations)
        mark = "FAIL" if violations else "ok"
        print(f"  {name:<18} {mark:<4} {sum(tally.counts.values()):>7} requests  " +
              ", ".join(f"{k}: {v}" for k, v in sorted(tally.counts.items())))
        for n in notes:
            print(f"  {'':<18}      {n}")
        for v in violations:
            print(f"  {'':<18}      !! {v}")


def pct(values: list[float], p: float) -> float:
    if not values:
        return 0.0
    values = sorted(values)
    return values[min(len(values) - 1, int(round(p / 100 * (len(values) - 1))))] * 1000


# --------------------------------------------------------------------------- scenarios

async def hot_seat(api: Api, show: str, users: int, run: str) -> tuple[Tally, list[str], list[str]]:
    tally, notes, bad = Tally(), [], []
    tokens = await api.tokens(f"{run}-hot", users)
    results = await asyncio.gather(*(api.reserve(t, show, ["A1"], tally=tally) for t in tokens))
    wins = sum(1 for s, _ in results if s == 201)
    takens = sum(1 for s, b in results if s == 409 and b.get("error") == "seat_taken")
    notes.append(f"{users} users -> seat A1: {wins} x 201, {takens} x 409 seat_taken")
    if wins != 1:
        bad.append(f"expected exactly one winner for A1, got {wins}")
    if takens != users - 1:
        bad.append(f"expected {users - 1} clean seat_taken declines, got {takens}")
    return tally, notes, bad


async def group_overlap(api: Api, show: str, trials: int, run: str) -> tuple[Tally, list[str], list[str]]:
    tally, notes, bad = Tally(), [], []
    split = 0
    for trial in range(trials):
        base = 1 + 3 * trial
        triple = [f"B{base}", f"B{base + 1}", f"B{base + 2}"]
        t1, t2, t3 = await asyncio.gather(*(api.token(f"{run}-grp{trial}-{i}") for i in range(3)))
        results = await asyncio.gather(
            api.reserve(t1, show, triple, tally=tally),
            api.reserve(t2, show, triple, tally=tally),
            api.reserve(t3, show, [triple[1]], tally=tally))
        winners = [b for s, b in results if s == 201]
        if len(winners) != 1:
            bad.append(f"trial {trial}: {len(winners)} winners (expected 1)")
            continue
        _, state = await api.call("GET", f"/shows/{show}")
        held = sorted(s["label"] for s in state["seats"] if s["label"] in triple and s["status"] != "available")
        if held != sorted(winners[0]["seats"]):
            split += 1
            bad.append(f"trial {trial}: owned {held} but winner holds {winners[0]['seats']}")
    notes.append(f"{trials} trials of [3-seat group x2] vs [1 seat]: {split} split groups")
    return tally, notes, bad


def stampede_plan(n: int, seed: int) -> list[tuple[int, list[str], bool]]:
    rng = random.Random(seed)
    hot = [f"C{i}" for i in range(1, 11)]
    others = [f"{r}{i}" for r in ROWS[3:19] for i in range(1, PER_ROW + 1)]  # rows D..S
    plan = []
    for _ in range(n):
        if rng.random() < 0.7:
            seats = [rng.choice(hot)]
        else:
            seats = rng.sample(others, rng.choice([1, 1, 2]))
        plan.append((rng.randrange(max(1, n // 4)), seats, rng.random() < 0.05))
    return plan


async def stampede_part(api: Api, show: str, plan: list[tuple[int, list[str], bool]], run: str,
                        worker: int) -> tuple[Tally, dict[str, list[str]], int]:
    tally = Tally()
    user_ids = sorted({u for u, _, _ in plan})
    tokens = dict(zip(user_ids, await asyncio.gather(*(api.token(f"{run}-s{worker}-{u}") for u in user_ids))))
    # seat -> {reservation_id: (granted_at, live_until)}; a hold is live until expires_at, a confirmation forever.
    owners: dict[str, dict[str, tuple[str, str]]] = collections.defaultdict(dict)
    retries = 0

    async def one(user: int, seats: list[str], retry: bool) -> None:
        nonlocal retries
        key = uuid.uuid4().hex
        calls = [api.reserve(tokens[user], show, seats, key, tally=tally)]
        if retry:  # the same request sent twice at once, like a client retrying on a timeout
            retries += 1
            calls.append(api.reserve(tokens[user], show, seats, key, tally=tally))
        for status, body in await asyncio.gather(*calls):
            if status in (200, 201) and "reservation_id" in body:
                window = (body.get("server_time", ""), body.get("expires_at") or "9999")
                for seat in body["seats"]:
                    owners[seat][body["reservation_id"]] = window

    await asyncio.gather(*(one(u, s, r) for u, s, r in plan))
    return tally, {k: dict(v) for k, v in owners.items()}, retries


def _stampede_worker(base: str, show: str, plan: list, run: str, worker: int, concurrency: int, insecure: bool) -> dict:
    async def go() -> dict:
        async with new_session(concurrency, insecure) as s:
            tally, owners, retries = await stampede_part(Api(base, s, concurrency), show, plan, run, worker)
            return {"tally": tally.to_dict(), "owners": owners, "retries": retries}
    return asyncio.run(go())


async def stampede(api: Api, args, show: str, run: str) -> tuple[Tally, list[str], list[str]]:
    plan = stampede_plan(args.stampede, seed=hash(run) & 0xFFFF)
    started = time.perf_counter()
    if args.workers <= 1:
        tally, owners, retries = await stampede_part(api, show, plan, run, 0)
    else:
        # Partition by user so each worker mints only its own users' tokens.
        chunks = [[p for p in plan if p[0] % args.workers == i] for i in range(args.workers)]
        loop = asyncio.get_running_loop()
        with concurrent.futures.ProcessPoolExecutor(args.workers) as pool:
            parts = await asyncio.gather(*(loop.run_in_executor(
                pool, _stampede_worker, api.base, show, chunk, run, i, args.concurrency, args.insecure)
                for i, chunk in enumerate(chunks)))
        tally, owners, retries = Tally(), collections.defaultdict(dict), 0
        for p in parts:
            tally.merge(Tally.from_dict(p["tally"]))
            retries += p["retries"]
            for seat, grants in p["owners"].items():
                owners[seat].update({rid: tuple(w) for rid, w in grants.items()})
    elapsed = time.perf_counter() - started
    total = sum(tally.counts.values())
    def ts(value: str) -> datetime.datetime:
        if not value or value.startswith("9999"):
            return datetime.datetime.max
        value = value.rstrip("Z")
        if "." in value:  # .NET emits up to 7 fractional digits; Python parses up to 6
            head, frac = value.split(".", 1)
            value = f"{head}.{frac[:6]}"
        return datetime.datetime.fromisoformat(value)

    def overlapping(grants: dict) -> bool:
        # Re-granting a seat after a hold expired is legal; two grants alive at the same instant are not.
        windows = sorted((ts(a), ts(b)) for a, b in grants.values())
        return any(nxt[0] < cur[1] for cur, nxt in zip(windows, windows[1:]))
    doubled = {seat: grants for seat, grants in owners.items() if overlapping(grants)}
    regranted = sum(1 for grants in owners.values() if len(grants) > 1) - len(doubled)
    notes = [f"{total} requests in {elapsed:.1f}s ({total / max(elapsed, 1e-9):.0f} req/s), "
             f"{retries} concurrent duplicate retries, {len(owners)} seats won, "
             f"{regranted} re-granted after an expired hold"]
    bad = [f"seat {seat} granted to overlapping reservations {sorted(grants)}" for seat, grants in list(doubled.items())[:10]]
    return tally, notes, bad


async def idempotency(api: Api, show: str, run: str) -> tuple[Tally, list[str], list[str]]:
    tally, notes, bad = Tally(), [], []
    token = await api.token(f"{run}-idem")
    key = uuid.uuid4().hex
    results = await asyncio.gather(*(api.reserve(token, show, ["T1"], key, tally=tally) for _ in range(50)))
    created = sum(1 for s, _ in results if s == 201)
    replays = sum(1 for s, _ in results if s == 200)
    ids = {b.get("reservation_id") for s, b in results if s in (200, 201)}
    notes.append(f"same key x50 concurrently: {created} x 201, {replays} x 200 replay, {len(ids)} distinct reservation")
    if created != 1 or replays != 49 or len(ids) != 1:
        bad.append("same idempotency key must reserve exactly once and replay the original")
    status, body = await api.reserve(token, show, ["T2"], key, tally=tally)
    notes.append(f"same key, different seats: {status} {body.get('error')}")
    if status != 409 or body.get("error") != "idempotency_mismatch":
        bad.append("same key with a different body must be 409 idempotency_mismatch")
    return tally, notes, bad


async def per_user_limit(api: Api, show: str, limit: int, run: str) -> tuple[Tally, list[str], list[str]]:
    tally, notes, bad = Tally(), [], []
    token = await api.token(f"{run}-greedy")
    seats = [f"T{i}" for i in range(41, 51)]
    results = await asyncio.gather(*(api.reserve(token, show, [s], tally=tally) for s in seats))
    won = sum(1 for s, _ in results if s == 201)
    limited = sum(1 for s, b in results if s == 409 and b.get("error") == "per_user_limit")
    notes.append(f"1 user, 10 parallel reserves, limit {limit}: {won} held, {limited} x 409 per_user_limit")
    if won > limit:
        bad.append(f"user holds {won} seats, limit is {limit}")
    return tally, notes, bad


async def spoofing(api: Api, show: str, run: str) -> tuple[Tally, list[str], list[str]]:
    tally, notes, bad = Tally(), [], []
    me, other = await asyncio.gather(api.token(f"{run}-me"), api.token(f"{run}-other"))
    status, body = await api.reserve(me, show, ["T20"], extra={"user_id": "victim"}, tally=tally)
    if status != 201 or body.get("user_id") != f"{run}-me":
        bad.append(f"body user_id must be ignored; got {status} user_id={body.get('user_id')}")
    rid = body.get("reservation_id")
    s_cancel, _ = await api.call("POST", f"/reservations/{rid}/cancel", other, tally=tally)
    s_read, _ = await api.call("GET", f"/reservations/{rid}", other, tally=tally)
    s_conf, _ = await api.call("POST", f"/reservations/{rid}/confirm", other, headers={"Idempotency-Key": uuid.uuid4().hex}, tally=tally)
    notes.append(f"spoofed body -> acted as token user; other user cancel/read/confirm: {s_cancel}/{s_read}/{s_conf}")
    if (s_cancel, s_read, s_conf) != (404, 404, 404):
        bad.append("another user's reservation must be invisible (404) to cancel/read/confirm")
    _, after = await api.call("GET", f"/reservations/{rid}", me)
    if after.get("status") != "held":
        bad.append("someone else's cancel attempt changed the hold")
    return tally, notes, bad


async def payments(api: Api, show: str, run: str) -> tuple[Tally, list[str], list[str], int]:
    tally, notes, bad = Tally(), [], []
    confirmed = 0
    t1, t2, t3 = await asyncio.gather(*(api.token(f"{run}-pay{i}") for i in range(3)))

    _, hold = await api.reserve(t1, show, ["T30"], tally=tally)
    key = uuid.uuid4().hex
    s1, c1 = await api.call("POST", f"/reservations/{hold['reservation_id']}/confirm", t1, headers={"Idempotency-Key": key}, tally=tally)
    s1b, _ = await api.call("POST", f"/reservations/{hold['reservation_id']}/confirm", t1, headers={"Idempotency-Key": key}, tally=tally)
    confirmed += s1 == 200
    if s1 != 200 or c1.get("status") != "confirmed" or s1b != 200:
        bad.append(f"hold -> confirm should be 200 confirmed (and replay 200); got {s1}/{s1b}")

    s2, c2 = await api.reserve(t2, show, ["T31", "T32"], confirm=True, tally=tally)
    confirmed += s2 == 201
    if s2 != 201 or c2.get("status") != "confirmed":
        bad.append(f"hold-and-pay should be 201 confirmed; got {s2}")
    s3, c3 = await api.call("POST", f"/reservations/{c2.get('reservation_id')}/cancel", t2, tally=tally)
    if s3 != 200 or c3.get("status") != "refunded":
        bad.append(f"cancel of a confirmed booking should refund; got {s3} {c3.get('status')}")

    _, hold3 = await api.reserve(t3, show, ["T33"], tally=tally)
    s4, c4 = await api.call("POST", f"/reservations/{hold3['reservation_id']}/confirm", t3,
                            headers={"Idempotency-Key": uuid.uuid4().hex, "X-Sim-Gateway": "decline"}, tally=tally)
    if s4 != 402:
        bad.append(f"declined payment should be 402; got {s4}")
    s5, _ = await api.call("POST", f"/reservations/{hold3['reservation_id']}/confirm", t3,
                           headers={"Idempotency-Key": uuid.uuid4().hex}, tally=tally)
    confirmed += s5 == 200
    if s5 != 200:
        bad.append(f"retry after a decline (inside the pin) should confirm; got {s5}")
    notes.append(f"confirm {s1}, replay {s1b}, hold+pay {s2}, cancel->refund {s3}, decline {s4}, retry {s5}")
    return tally, notes, bad, confirmed


async def overload(api: Api, show: str, n: int, run: str) -> tuple[Tally, list[str], list[str]]:
    tally = Tally()
    tokens = await api.tokens(f"{run}-ovl", 200)
    seats = [f"{r}{i}" for r in ROWS for i in range(1, PER_ROW + 1)]
    started = time.perf_counter()
    await asyncio.gather(*(api.reserve(random.choice(tokens), show, [random.choice(seats)], tally=tally) for _ in range(n)))
    elapsed = time.perf_counter() - started
    shed = sum(v for k, v in tally.counts.items() if k.startswith("429"))
    return tally, [f"{n} requests in {elapsed:.1f}s; {shed} shed with 429 (expected under overload)"], []


# --------------------------------------------------------------------------- reconciliation

def parse_metrics(text: str) -> dict[str, float]:
    out = {}
    for line in text.splitlines():
        if line and not line.startswith("#"):
            name, _, value = line.rpartition(" ")
            try:
                out[name] = float(value)
            except ValueError:
                pass
    return out


async def scrape(api: Api) -> dict[str, float]:
    async with api.session.get(api.base + "/metrics") as r:
        return parse_metrics(await r.text())


async def reconcile(api: Api, show: str, confirmations: int, unhandled_before: float) -> tuple[list[str], list[str]]:
    notes, bad = [], []
    await asyncio.sleep(1.5)  # the seat gauge refreshes at most once a second
    _, state = await api.call("GET", f"/shows/{show}")
    c = state["counts"]
    total = state["total_seats"]
    notes.append(f"available {c['available']} + held {c['held']} + confirmed {c['confirmed']} = "
                 f"{c['available'] + c['held'] + c['confirmed']} (total_seats {total})")
    if c["available"] + c["held"] + c["confirmed"] != total or not state["reconciled"]:
        bad.append("reconciliation invariant violated")
    ledger = state["ledger"]
    notes.append(f"ledger: charged {ledger['charged_paise']} - refunded {ledger['refunded_paise']} = {ledger['net_paise']} paise "
                 f"(expected {ledger['expected_net_paise']})")
    if ledger["net_paise"] != ledger["expected_net_paise"]:
        bad.append("ledger does not match confirmed seats")

    m = await scrape(api)
    for st in ("available", "held", "confirmed"):
        metric = m.get(f'seatres_seats{{show="{show}",state="{st}"}}')
        if metric is None or int(metric) != c[st]:
            bad.append(f"/metrics seatres_seats[{st}]={metric} but API says {c[st]}")
    counter = m.get(f'seatres_reservations_confirmed_total{{show="{show}"}}', 0)
    notes.append(f"/metrics: seats gauge matches API; confirmed counter {int(counter)} (this run confirmed {confirmations})")
    if int(counter) != confirmations:
        bad.append(f"confirmed counter {counter} != confirmations observed {confirmations}")
    unhandled = m.get("seatres_unhandled_errors_total", 0) - unhandled_before
    if unhandled < 0:
        notes.append("/metrics: the API restarted during the run (counters reset); unhandled-error delta unknown")
        unhandled = m.get("seatres_unhandled_errors_total", 0)
    notes.append(f"/metrics: unhandled errors during this run: {int(unhandled)}")
    if unhandled > 0:
        bad.append(f"service recorded {int(unhandled)} unhandled errors during the run")
    for check in ("seats", "ledger"):
        ok = m.get(f'seatres_reconciliation_ok{{check="{check}"}}')
        if ok is not None and ok < 1:
            bad.append(f"service reconciler reports {check} invariant violated")
    return notes, bad


# --------------------------------------------------------------------------- main

async def main(args) -> int:
    run = "b" + uuid.uuid4().hex[:6]
    # The coordinating session lets idle connections go quickly, so they don't sit alongside the stampede
    # workers' own connections and inflate the concurrent-connection count.
    async with new_session(args.concurrency, args.insecure, keepalive=2) as session:
        api = Api(args.base_url, session, args.concurrency)
        print(f"seatres burst {run} -> {api.base}")

        for _ in range(60):
            status, _ = await api.call("GET", "/health/ready")
            if status == 200:
                break
            await asyncio.sleep(2)
        else:
            print("service never became ready")
            return 1

        status, body = await api.call("POST", "/auth/admin-token", headers={"X-Admin-Key": args.admin_key})
        if status != 200:
            print(f"admin token failed ({status}); set ADMIN_KEY")
            return 1
        admin = body["token"]
        seats = [f"{r}{i}" for r in ROWS for i in range(1, PER_ROW + 1)]
        status, show_body = await api.call("POST", "/shows", admin,
                                           {"name": f"burst-{run}", "seats": seats, "price_paise": 25000, "per_user_limit": 4})
        if status != 201:
            print(f"create show failed: {status} {show_body}")
            return 1
        show = show_body["id"]
        unhandled_before = (await scrape(api)).get("seatres_unhandled_errors_total", 0)
        print(f"show {show}: {len(seats)} seats, price 25000 paise, per_user_limit 4\n")

        report = Report()
        only = set(args.only.split(",")) if args.only else None
        want = lambda name: only is None or name in only
        confirmations = 0

        if want("hot-seat"):
            report.add("hot-seat", *await hot_seat(api, show, args.hot, run))
        if want("group-overlap"):
            report.add("group-overlap", *await group_overlap(api, show, 10 if args.quick else 16, run))
        if want("stampede"):
            report.add("stampede", *await stampede(api, args, show, run))
        if want("idempotency"):
            report.add("idempotency", *await idempotency(api, show, run))
        if want("per-user-limit"):
            report.add("per-user-limit", *await per_user_limit(api, show, 4, run))
        if want("spoof"):
            report.add("spoof", *await spoofing(api, show, run))
        if want("payments"):
            tally, notes, bad, confirmations = await payments(api, show, run)
            report.add("payments", tally, notes, bad)
        if args.overload:
            report.add("overload", *await overload(api, show, args.overload, run))

        notes, bad = await reconcile(api, show, confirmations, unhandled_before)
        print("\nreconciliation")
        for n in notes:
            print(f"  {n}")
        report.violations.extend(f"[reconcile] {b}" for b in bad)

        everything = Tally()
        for _, t, _ in report.sections:
            everything.merge(t)
        lat = everything.latencies
        print("\noutcome distribution (all scenarios)")
        for k, v in sorted(everything.counts.items()):
            print(f"  {k:<34} {v:>8}")
        print(f"  {'5xx':<34} {everything.server_errors:>8}")
        if everything.errors:
            print()
            print("client-side transport errors (never reached a response; often the load generator itself)")
            for msg, n in everything.errors.most_common(3):
                print(f"  {n:>8}  {msg or '(timeout)'}")
        if lat:
            print(f"\nlatency  p50 {pct(lat, 50):.0f} ms   p99 {pct(lat, 99):.0f} ms   max {max(lat) * 1000:.0f} ms   "
                  f"mean {statistics.mean(lat) * 1000:.0f} ms")

        if report.violations:
            print("\nVIOLATIONS")
            for v in report.violations:
                print(f"  {v}")
            return 1
        print("\nALL CHECKS PASSED")
        return 0


def parse_args(argv: list[str]):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("base_url")
    p.add_argument("--admin-key", default=os.environ.get("ADMIN_KEY", "local-admin-key"))
    p.add_argument("--hot", type=int, default=500, help="users storming one seat (default 500)")
    p.add_argument("--stampede", type=int, default=20000, help="stampede requests (default 20000)")
    p.add_argument("--concurrency", type=int, default=1000, help="in-flight requests per process (default 1000)")
    p.add_argument("--workers", type=int, default=1, help="processes for the stampede (for very large runs)")
    p.add_argument("--overload", type=int, default=0, help="extra requests fired to push past capacity (expect 429s)")
    p.add_argument("--only", help="comma list: hot-seat,group-overlap,stampede,idempotency,per-user-limit,spoof,payments")
    p.add_argument("--quick", action="store_true", help="small run for CI: 100 hot users, 2000 stampede")
    p.add_argument("--insecure", action="store_true", help="skip TLS verification")
    args = p.parse_args(argv)
    if args.quick:
        args.hot, args.stampede = min(args.hot, 100), min(args.stampede, 2000)
    return args


if __name__ == "__main__":
    sys.exit(asyncio.run(main(parse_args(sys.argv[1:]))))
