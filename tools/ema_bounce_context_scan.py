#!/usr/bin/env python3
"""Causal context features joined to established EMA close-entry tick outcomes.

Research only. Rules are fixed in docs/ema_bounce_context_research.md.
NONE scores zero gross; cost scenarios charge every selected signal.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import random
from collections import defaultdict
from datetime import datetime, timedelta
from pathlib import Path

from ema_signal_stop_sweep import TICK, select
from tick_execution_audit import second_key

FAMILIES = {"EMA8", "EMA24", "EMA50"}
SPLIT = "2026-08-23 23:59:59"


def read_csv(path):
    with path.open(newline="", encoding="utf-8-sig") as source:
        return list(csv.DictReader(source))


def session(stamp):
    return (stamp - timedelta(hours=15)).date()


def prime(stamp):
    minute = stamp.hour * 60 + stamp.minute
    return 391 <= minute < 405 or 420 <= minute < 480 or 660 <= minute < 780


def features(bars, i, direction):
    """Only bars <= i are accessed; swing confirmation ends at i-1."""
    x = bars[i]
    same_session = lambda j: bars[j]["session"] == x["session"]
    extreme = "High" if direction > 0 else "Low"
    levels = []
    for pivot in range(max(2, i - 50), i - 2):
        if not same_session(pivot - 2):
            continue
        level = bars[pivot][extreme]
        if not all(direction * (level - bars[j][extreme]) > 0
                   for j in (pivot-2, pivot-1, pivot+1, pivot+2)):
            continue
        if any(direction * (bars[j][extreme] - level) > 0
               for j in range(pivot+3, i)):
            continue
        distance = direction * (level - x["Close"]) / TICK
        if distance >= 0:
            levels.append(distance)
    room = min(levels) if levels else None
    result = {"RoomTicks": room, "PullbackBars": 0, "ImpulseBars": 0,
              "DepthRatio": None, "SpeedRatio": None,
              "ImpulseTicks": None, "PullbackDepthTicks": None,
              "ImpulseSeconds": None, "PullbackSeconds": None}
    j = i - 1
    while j >= 0 and same_session(j) and direction * (bars[j]["Close"] - bars[j]["Open"]) < 0:
        j -= 1
    anchor = j
    length = i - 1 - anchor
    result["PullbackBars"] = length
    if length == 0 or anchor < 1 or not same_session(anchor):
        return result
    while j >= 0 and same_session(j) and direction * (bars[j]["Close"] - bars[j]["Open"]) >= 0:
        j -= 1
    first = j + 1
    if first > anchor or j < 0 or not same_session(j):
        return result
    result["ImpulseBars"] = anchor - first + 1
    impulse = direction * (bars[anchor]["Close"] - bars[j]["Close"]) / TICK
    if impulse <= 0:
        return result
    adverse = min(bars[k]["Low"] for k in range(anchor+1, i)) if direction > 0 else max(
        bars[k]["High"] for k in range(anchor+1, i))
    depth = direction * (bars[anchor]["Close"] - adverse) / TICK
    net_pullback = direction * (bars[anchor]["Close"] - bars[i-1]["Close"]) / TICK
    impulse_seconds = (bars[anchor]["stamp"] - bars[j]["stamp"]).total_seconds()
    pullback_seconds = (bars[i-1]["stamp"] - bars[anchor]["stamp"]).total_seconds()
    result.update(DepthRatio=max(0, depth) / impulse, ImpulseTicks=impulse,
                  PullbackDepthTicks=depth, ImpulseSeconds=impulse_seconds,
                  PullbackSeconds=pullback_seconds)
    if impulse_seconds > 0 and pullback_seconds > 0 and net_pullback >= 0:
        result["SpeedRatio"] = (net_pullback / pullback_seconds) / (impulse / impulse_seconds)
    return result


def metrics(rows):
    n = len(rows)
    targets = sum(r["Outcome"] == "TARGET" for r in rows)
    stops = sum(r["Outcome"] == "STOP" for r in rows)
    gross = targets * 5 - stops * 10
    mean = gross / n if n else None
    return dict(Selected=n, Targets=targets, Stops=stops,
                Unresolved=n-targets-stops,
                TargetRate=100*targets/(targets+stops) if targets+stops else None,
                TotalProxyTicks=gross, ProxyTicksPerSelected=mean,
                NetAtHalfTick=mean-.5 if n else None,
                NetAtOneTick=mean-1 if n else None,
                NetAtOneHalfTicks=mean-1.5 if n else None)


RULES = {
    "ROOM": (lambda r: True, lambda r: r["RoomTicks"] is None or r["RoomTicks"] >= 6),
    "DEPTH": (lambda r: r["DepthRatio"] is not None, lambda r: r["DepthRatio"] <= .5),
    "SPEED": (lambda r: r["SpeedRatio"] is not None, lambda r: r["SpeedRatio"] <= 1),
}


def bootstrap(base, kept, repetitions):
    """Paired resampling of complete chart dates, retaining intraday clusters."""
    aggregates = defaultdict(lambda: [0, 0, 0, 0])
    for rows, offset in ((base, 0), (kept, 2)):
        for row in rows:
            a = aggregates[row["Time"][:10]]
            a[offset] += 1
            a[offset+1] += 5 if row["Outcome"] == "TARGET" else -10 if row["Outcome"] == "STOP" else 0
    days = list(aggregates.values())
    if not days or not kept:
        return None, None
    rng = random.Random(190926)
    deltas = []
    for _ in range(repetitions):
        sample = [rng.choice(days) for _ in days]
        bn, bp, kn, kp = [sum(day[j] for day in sample) for j in range(4)]
        if kn and bn:
            deltas.append(kp/kn - bp/bn)
    deltas.sort()
    return (deltas[int(.025*(len(deltas)-1))], deltas[int(.975*(len(deltas)-1))]) if deltas else (None, None)


def write_csv(path, records):
    with path.open("w", newline="") as dest:
        writer = csv.DictWriter(dest, fieldnames=list(records[0]))
        writer.writeheader()
        writer.writerows(records)


def run(args):
    raw = read_csv(args.diagnostic)
    cached = read_csv(args.outcomes)
    if not raw or not cached:
        raise ValueError("Empty input")
    if len({r["BarNumber"] for r in raw}) != len(raw):
        raise ValueError("Duplicate diagnostic bar IDs")
    if len({r["BarNumber"] for r in cached}) != len(cached):
        raise ValueError("Duplicate outcome bar IDs")
    stamps = [datetime.fromisoformat(r["Time"]) for r in raw]
    if stamps != sorted(stamps):
        raise ValueError("Diagnostic timestamps are not chronological")
    if {r["Contract"] for r in raw} != {"MESU26"}:
        raise ValueError("This experiment is fixed to the established MESU26 sample")
    signals = select(raw, second_key(min(r["Time"] for r in cached)),
                     second_key(max(r["Time"] for r in cached)))
    by_bar = {s.bar: s for s in signals}
    if set(by_bar) != {int(r["BarNumber"]) for r in cached}:
        raise ValueError("Cached signal population does not match reconstructed population")
    bars = [dict({k: float(r[k]) for k in ("Open", "High", "Low", "Close")},
                 stamp=t, session=session(t)) for r, t in zip(raw, stamps)]
    positions = {int(r["BarNumber"]): i for i, r in enumerate(raw)}
    records = []
    for r in cached:
        signal = by_bar[int(r["BarNumber"])]
        if (signal.time_text != r["Time"] or signal.direction != int(r["Direction"])
                or signal.entry != float(r["Entry"])
                or set(signal.families) != set(r["Families"].split("+"))
                or signal.same_second != (r["SameSecondAmbiguous"] == "True")):
            raise ValueError("Cached identity mismatch: " + r["BarNumber"])
        if r["Stop10"] not in {"TARGET", "STOP", "NONE"}:
            raise ValueError("Unknown outcome")
        families = FAMILIES.intersection(signal.families)
        if not families:
            continue
        i = positions[signal.bar]
        records.append(dict(BarNumber=signal.bar, Time=r["Time"], Direction=signal.direction,
                            Families="+".join(sorted(families)), Entry=signal.entry,
                            Segment="DEV" if r["Time"] <= SPLIT else "LATER",
                            Prime=prime(stamps[i]), Ambiguous=signal.same_second,
                            Outcome=r["Stop10"], **features(bars, i, signal.direction)))
    summaries, comparisons, buckets = [], [], []
    for scope in ("ALL_HOURS", "PRIME"):
        scoped = [r for r in records if scope == "ALL_HOURS" or r["Prime"]]
        for family in ("COMBINED", "EMA8", "EMA24", "EMA50"):
            population = [r for r in scoped if family == "COMBINED" or family in r["Families"].split("+")]
            for segment in ("ALL", "DEV", "LATER", "LONG", "SHORT", "UNAMBIGUOUS"):
                base = [r for r in population if segment == "ALL" or r["Segment"] == segment
                        or segment == "LONG" and r["Direction"] == 1
                        or segment == "SHORT" and r["Direction"] == -1
                        or segment == "UNAMBIGUOUS" and not r["Ambiguous"]]
                tags = dict(Scope=scope, Family=family, Segment=segment)
                summaries.append(dict(**tags, Rule="BASELINE", Selection="ALL", **metrics(base)))
                for rule, (eligible, accept) in RULES.items():
                    known = [r for r in base if eligible(r)]
                    kept = [r for r in known if accept(r)]
                    dropped = [r for r in known if not accept(r)]
                    missing = [r for r in base if not eligible(r)]
                    for label, subset in (("ELIGIBLE", known), ("KEPT", kept),
                                          ("DROPPED", dropped), ("MISSING", missing)):
                        summaries.append(dict(**tags, Rule=rule, Selection=label, **metrics(subset)))
                    if scope == "PRIME" and segment in ("ALL", "UNAMBIGUOUS"):
                        lo, hi = bootstrap(known, kept, args.bootstrap)
                        b, k = metrics(known), metrics(kept)
                        comparisons.append(dict(**tags, Rule=rule, Eligible=len(known),
                            Kept=len(kept), RetainedPct=100*len(kept)/len(known) if known else None,
                            DeltaProxyTicks=k["ProxyTicksPerSelected"]-b["ProxyTicksPerSelected"] if kept and known else None,
                            DayBootstrapLower=lo, DayBootstrapUpper=hi))
                for feature, boundaries in (("RoomTicks", (0, 3, 6, 11)),
                                            ("DepthRatio", (0, .5, 1)),
                                            ("SpeedRatio", (0, .5, 1, 2))):
                    groups = defaultdict(list)
                    for row in base:
                        value = row[feature]
                        label = "MISSING_OR_NO_LEVEL" if value is None else str(sum(value >= b for b in boundaries))
                        groups[label].append(row)
                    for label, subset in sorted(groups.items()):
                        buckets.append(dict(**tags, Feature=feature, Bucket=label,
                                            Boundaries=str(boundaries), **metrics(subset)))
    args.output.mkdir(parents=True, exist_ok=True)
    write_csv(args.output / "features.csv", records)
    write_csv(args.output / "summary.csv", summaries)
    write_csv(args.output / "comparisons.csv", comparisons)
    write_csv(args.output / "buckets.csv", buckets)
    manifest = dict(diagnostic=str(args.diagnostic.resolve()), outcomes=str(args.outcomes.resolve()),
                    diagnostic_sha256=hashlib.sha256(args.diagnostic.read_bytes()).hexdigest(),
                    outcomes_sha256=hashlib.sha256(args.outcomes.read_bytes()).hexdigest(),
                    script_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                    bootstrap=args.bootstrap, seed=190926, split=SPLIT,
                    validated_cached_signals=len(cached), ema_signals=len(records),
                    first=records[0]["Time"], last=records[-1]["Time"],
                    caveat="Previously used sample; ask-only close-entry paths; overlapping signals; NONE=0 gross")
    (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(json.dumps(manifest, indent=2))
    for row in summaries:
        if row["Scope"] == "PRIME" and row["Family"] == "COMBINED" and row["Segment"] in ("ALL", "DEV", "LATER") and row["Selection"] in ("ALL", "KEPT", "ELIGIBLE"):
            print(row)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--diagnostic", required=True, type=Path)
    parser.add_argument("--outcomes", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--bootstrap", type=int, default=2000)
    run(parser.parse_args())
