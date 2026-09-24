#!/usr/bin/env python3
"""Replay current display signals against several fixed stops using ask ticks.

This is a price-path research proxy, not a fill simulator.  Entry is the
completed signal-bar close and evaluation begins with the next whole-second
tick because the export has second precision while bars have milliseconds.
"""
from __future__ import annotations

import argparse
import csv
import heapq
import math
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from tick_execution_audit import raw_tick_rows, second_key

TICK = 0.25
STOPS = tuple(range(1, 11))


def f(row, name):
    return float(row[name])


def trend(row, direction):
    return f(row, "Close") >= f(row, "Open") if direction > 0 else f(row, "Close") <= f(row, "Open")


def counter(row, direction):
    return f(row, "Close") < f(row, "Open") if direction > 0 else f(row, "Close") > f(row, "Open")


def pullback(rows, i, direction, maximum):
    for length in range(1, maximum + 1):
        if (all(counter(rows[i-j], direction) for j in range(1, length + 1)) and
                trend(rows[i-length-1], direction)):
            return length
    return 0


def angle(current, previous, bars=3):
    return math.degrees(math.atan2(current - previous, bars * TICK))


def current_slope(rows, i, ema, direction):
    return direction * angle(f(rows[i], ema), f(rows[i-3], ema))


def best_slope(rows, i, ema, direction, lookback):
    return max(direction * angle(f(rows[i-j], ema), f(rows[i-j-3], ema))
               for j in range(1, lookback + 1))


def ordered_direction(row):
    e8, e24, e50 = f(row, "EMA8"), f(row, "EMA24"), f(row, "EMA50")
    if e8 > e24 > e50:
        return 1
    if e8 < e24 < e50:
        return -1
    return 0


def ema50_direction(row):
    gap = f(row, "EMA24") - f(row, "EMA50")
    return 1 if gap > 0 else (-1 if gap < 0 else 0)


def quality8(rows, i, d):
    x = rows[i]
    n = pullback(rows, i, d, 3)
    if not n or not trend(x, d):
        return False
    e8 = f(x, "EMA8")
    if not (f(x, "Low") <= e8 <= f(x, "High")):
        return False
    if (f(x, "Close") < e8 if d > 0 else f(x, "Close") > e8):
        return False
    sep = abs(e8 - f(x, "EMA24")) / TICK
    prior24 = best_slope(rows, i, "EMA24", d, 6)
    slope8 = current_slope(rows, i, "EMA8", d)
    pen = (e8 - f(x, "Low")) / TICK if d > 0 else (f(x, "High") - e8) / TICK
    ref = (min(f(rows[i-1], "Low"), f(rows[i-2], "Low")) if d > 0
           else max(f(rows[i-1], "High"), f(rows[i-2], "High")))
    disp = ((ref - f(x, "Low")) / TICK if d > 0
            else (f(x, "High") - ref) / TICK)
    tail = ((f(x, "Open") - f(x, "Low")) / TICK if d > 0
            else (f(x, "High") - f(x, "Open")) / TICK)
    base = (n in (1, 2) and sep >= 5 and slope8 >= 15 and
            prior24 >= (20 if n == 1 else 39) and 1 <= pen <= (4.5 if n == 1 else 2.5) and
            disp >= 1)
    no_tail = tail <= .1
    one_tail = abs(tail - 1) <= .1
    long_tail = tail >= 4
    return (base or
            (n == 1 and no_tail and prior24 >= 45 and slope8 >= 0 and sep >= 3 and 0 <= pen <= 4.5) or
            (n == 1 and one_tail and prior24 >= 20 and slope8 >= 15 and sep >= 5 and 1 <= pen <= 2.5) or
            (n == 1 and long_tail and prior24 >= 20 and slope8 >= 15 and sep >= 3 and 0 <= pen <= 2.5 and disp >= 1) or
            (n == 3 and no_tail and prior24 >= 20 and slope8 >= 0 and sep >= 3 and 0 <= pen <= 4.5 and disp >= 1))


def quality24(rows, i, d):
    x = rows[i]
    n = pullback(rows, i, d, 4)
    if not n or not trend(x, d):
        return False
    e24 = f(x, "EMA24")
    if (f(x, "Close") < e24 if d > 0 else f(x, "Close") > e24):
        return False
    p24 = best_slope(rows, i, "EMA24", d, 6)
    c24 = current_slope(rows, i, "EMA24", d)
    g8 = abs(f(x, "EMA8") - e24) / TICK
    g50 = abs(e24 - f(x, "EMA50")) / TICK
    rec = d * (f(x, "Close") - f(rows[i-1], "Close")) / TICK
    deep = max(((f(rows[i-j], "EMA24") - f(rows[i-j], "Low")) / TICK if d > 0
                else (f(rows[i-j], "High") - f(rows[i-j], "EMA24")) / TICK)
               for j in range(n + 1))
    if not 0 <= deep <= 5:
        return False
    base = ((n == 1 and p24 >= 45 and c24 >= 10 and g8 >= 3 and g50 >= 3 and rec >= 0) or
            (n == 2 and p24 >= 30 and c24 >= 15 and g8 >= 1.5 and g50 >= 3 and rec >= 2) or
            (n == 3 and p24 >= 39 and c24 >= 0 and g8 >= 1.5 and g50 >= 1.5 and rec >= 1))
    tail = ((f(x, "Open") - f(x, "Low")) / TICK if d > 0
            else (f(x, "High") - f(x, "Open")) / TICK)
    return (base or
            (n == 2 and abs(tail-1) <= .1 and p24 >= 30 and c24 >= 10 and g8 >= 3 and g50 >= 1.5 and rec >= 2) or
            (n == 2 and p24 >= 30 and c24 >= 15 and g8 >= 1.5 and g50 >= 3 and rec < 2 and deep <= 1) or
            (n == 4 and tail <= .1 and p24 >= 39 and c24 >= 0 and g8 >= 1.5 and g50 >= 1.5 and rec >= 2))


def quality50(rows, i, d):
    x = rows[i]
    if not trend(x, d):
        return False
    e50 = f(x, "EMA50")
    if (f(x, "Close") < e50 if d > 0 else f(x, "Close") > e50):
        return False
    n = pullback(rows, i, d, 3)
    if not n:
        return False
    pre = rows[i-n-1]
    if not (d * (f(pre, "EMA8") - f(pre, "EMA24")) > 0 and
            d * (f(pre, "EMA24") - f(pre, "EMA50")) > 0):
        return False
    p50 = best_slope(rows, i, "EMA50", d, 8)
    p24 = best_slope(rows, i, "EMA24", d, 8)
    c50 = current_slope(rows, i, "EMA50", d)
    gap = d * (f(x, "EMA24") - e50) / TICK
    if gap < .001:
        return False
    rec = d * (f(x, "Close") - f(rows[i-1], "Close")) / TICK
    deep = max(((f(rows[i-j], "EMA50") - f(rows[i-j], "Low")) / TICK if d > 0
                else (f(rows[i-j], "High") - f(rows[i-j], "EMA50")) / TICK)
               for j in range(n + 1))
    tail = ((f(x, "Open") - f(x, "Low")) / TICK if d > 0
            else (f(x, "High") - f(x, "Open")) / TICK)
    tail23 = 1.9 <= tail <= 3.1
    return ((n == 1 and tail <= .1 and p50 >= 10 and c50 >= -10 and gap >= 1.5 and -1 <= deep <= 5 and rec >= 0) or
            (n == 1 and tail23 and p50 >= 20 and c50 >= 10 and gap >= 0 and -1 <= deep <= 5 and rec >= 0) or
            (n == 2 and tail23 and p50 >= 20 and c50 >= 10 and gap >= 0 and -1 <= deep <= 5 and rec >= 2) or
            (n == 3 and p50 >= 20 and p24 >= 30 and c50 >= 0 and gap >= 1.5 and 0 <= deep <= 5 and rec >= 2) or
            (n == 1 and p50 >= 10 and c50 >= 10 and gap >= 3 and 0 <= deep <= 5 and rec >= 0))


def quality_pin(rows, i, d, minimum_clearance=4):
    x = rows[i]
    if not trend(x, d):
        return False
    tail = ((f(x, "Open") - f(x, "Low")) / TICK if d > 0
            else (f(x, "High") - f(x, "Open")) / TICK)
    if not 2.9 <= tail <= 5.1:
        return False
    e8 = f(x, "EMA8")
    body_near = min(f(x, "Open"), f(x, "Close")) if d > 0 else max(f(x, "Open"), f(x, "Close"))
    extreme = f(x, "Low") if d > 0 else f(x, "High")
    if d * (body_near - e8) / TICK <= 0:
        return False
    return (current_slope(rows, i, "EMA8", d) >= 60 and
            current_slope(rows, i, "EMA24", d) >= 45 and
            current_slope(rows, i, "EMA50", d) >= 39 and
            abs(f(x, "EMA8") - f(x, "EMA24")) / TICK >= 1.5 and
            abs(f(x, "EMA24") - f(x, "EMA50")) / TICK >= 3 and
            d * (extreme - e8) / TICK >= minimum_clearance and
            d * (f(x, "Close") - f(rows[i-1], "Close")) / TICK >= 2)


@dataclass
class Signal:
    ident: int
    bar: int
    time: int
    time_text: str
    end: int
    direction: int
    entry: float
    families: tuple[str, ...]
    signal_risk: int
    same_second: bool
    outcomes: dict[int, str] = field(default_factory=lambda: {s: "NONE" for s in STOPS})
    ticks_seen: int = 0


def select(rows, start, end, pin_clearance=4):
    result = []
    for i, x in enumerate(rows):
        when = second_key(x["Time"])
        if (i < 20 or i + 12 >= len(rows) or not start <= when <= end or
                x["OutcomeComplete"] != "True" or abs(f(x, "RangeTicks") - 5) > .001):
            continue
        families = []
        od = ordered_direction(x)
        if od:
            if quality8(rows, i, od): families.append("EMA8")
            if quality24(rows, i, od): families.append("EMA24")
            if quality_pin(rows, i, od, pin_clearance): families.append("PIN")
        d50 = ema50_direction(x)
        if d50 and quality50(rows, i, d50): families.append("EMA50")
        if not families:
            continue
        # The chart draws only one arrow. In the rare conflicting case, its
        # ordered 8/24/50 direction takes precedence over the 50-only direction.
        direction = od if any(v in families for v in ("EMA8", "EMA24", "PIN")) else d50
        entry = f(x, "Close")
        adverse = (entry - f(x, "Low")) / TICK if direction > 0 else (f(x, "High") - entry) / TICK
        result.append(Signal(len(result), int(x["BarNumber"]), when, x["Time"],
                             second_key(rows[i+12]["Time"]), direction, entry,
                             tuple(families), int(round(adverse)) + 1,
                             any(second_key(rows[i+j]["Time"]) == when for j in range(1, 13))))
    return result


def replay(signals, ticks, start, end):
    pending = sorted(signals, key=lambda x: x.time)
    pi = 0
    active = {}
    expiry = []
    count = 0
    for tick_time, price in raw_tick_rows(ticks):
        count += 1
        if count % 10_000_000 == 0:
            print("processed", count // 1_000_000, "million ticks; active", len(active), flush=True)
        if tick_time < start:
            continue
        if tick_time > end:
            break
        while pi < len(pending) and pending[pi].time < tick_time:
            s = pending[pi]; active[s.ident] = s; heapq.heappush(expiry, (s.end, s.ident)); pi += 1
        while expiry and expiry[0][0] < tick_time:
            _, ident = heapq.heappop(expiry); active.pop(ident, None)
        for ident, s in list(active.items()):
            s.ticks_seen += 1
            target = price >= s.entry + 5*TICK if s.direction > 0 else price <= s.entry - 5*TICK
            for stop in STOPS:
                if s.outcomes[stop] != "NONE":
                    continue
                stopped = price <= s.entry - stop*TICK if s.direction > 0 else price >= s.entry + stop*TICK
                if stopped:
                    s.outcomes[stop] = "STOP"
                elif target:
                    s.outcomes[stop] = "TARGET"
            if all(v != "NONE" for v in s.outcomes.values()):
                active.pop(ident, None)


def write(signals, output):
    output.parent.mkdir(parents=True, exist_ok=True)
    detail = output.with_name(output.stem + "_outcomes.csv")
    with detail.open("w", newline="") as h:
        w = csv.writer(h)
        w.writerow(["BarNumber", "Time", "Direction", "Entry", "Families",
                    "SignalBarStopPlus1", "TicksSeen", "SameSecondAmbiguous", *[f"Stop{s}" for s in STOPS]])
        for x in signals:
            w.writerow([x.bar, x.time_text, x.direction, x.entry, "+".join(x.families),
                        x.signal_risk, x.ticks_seen, x.same_second, *[x.outcomes[s] for s in STOPS]])
    groups = ["ALL", "EMA8", "EMA24", "EMA50", "PIN"]
    with output.open("w", newline="") as h:
        w = csv.writer(h)
        w.writerow(["Group", "StopTicks", "Selected", "Resolved", "Targets", "Stops", "TargetRate", "ExpectancyTicks"])
        for group in groups:
            sample = signals if group == "ALL" else [x for x in signals if group in x.families]
            for stop in STOPS:
                outcomes = [x.outcomes[stop] for x in sample]
                wins, losses = outcomes.count("TARGET"), outcomes.count("STOP")
                n = wins + losses
                rate = wins / n if n else 0
                w.writerow([group, stop, len(sample), n, wins, losses,
                            f"{100*rate:.4f}", f"{rate*5-(1-rate)*stop:.4f}"])
    print("wrote", output, "and", detail)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--diagnostic", required=True, type=Path)
    p.add_argument("--ticks", required=True, type=Path)
    p.add_argument("--output", required=True, type=Path)
    p.add_argument("--from", dest="start", required=True)
    p.add_argument("--to", dest="end", required=True)
    a = p.parse_args()
    start, end = second_key(a.start), second_key(a.end)
    with a.diagnostic.open(newline="", encoding="utf-8-sig") as h:
        rows = list(csv.DictReader(h))
    signals = select(rows, start, end)
    print("selected", len(signals), "unique current display signals", flush=True)
    replay(signals, a.ticks, start, end)
    write(signals, a.output)


if __name__ == "__main__":
    main()
