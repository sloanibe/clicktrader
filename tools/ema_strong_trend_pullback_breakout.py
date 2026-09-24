#!/usr/bin/env python3
"""Tick-replay strong-trend pullback breakouts above seven-bar-clear pivots.

For a long, a trend-colored anchor bar must make a high above the preceding
clearance window in a strongly ordered EMA fan. After the first completed red
bar, a buy stop is armed two ticks above the anchor high. Shorts are mirrored.
The first qualifying ask tick is the reference fill. Outcomes use +5/-10 ticks
and a 12-completed-bar horizon from the trigger.

This remains an ask-only, whole-second price-path proxy, not a fill simulator.
"""
from __future__ import annotations

import argparse
import bisect
import csv
import heapq
import sys
from collections import Counter
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from tick_execution_audit import raw_tick_rows, second_key


TICK = 0.25
SPLIT = datetime.fromisoformat("2026-08-23 23:59:59")


def f(row, key):
    return float(row[key])


def direction(row):
    e8, e24, e50 = f(row, "EMA8"), f(row, "EMA24"), f(row, "EMA50")
    if e8 > e24 > e50:
        return 1
    if e8 < e24 < e50:
        return -1
    return 0


def trend_bar(row, side):
    return f(row, "Close") >= f(row, "Open") if side > 0 else f(row, "Close") <= f(row, "Open")


def counter_bar(row, side):
    return f(row, "Close") < f(row, "Open") if side > 0 else f(row, "Close") > f(row, "Open")


def in_prime_time(text):
    when = datetime.fromisoformat(text)
    seconds = when.hour * 3600 + when.minute * 60 + when.second
    return ((6 * 3600 + 31 * 60) <= seconds < (6 * 3600 + 45 * 60) or
            7 * 3600 <= seconds < 8 * 3600 or
            11 * 3600 <= seconds < 13 * 3600)


def in_prime_key(value):
    seconds = value % 86400
    return ((6 * 3600 + 31 * 60) <= seconds < (6 * 3600 + 45 * 60) or
            7 * 3600 <= seconds < 8 * 3600 or
            11 * 3600 <= seconds < 13 * 3600)


def pivot_clear_depth(rows, index, side, maximum=10):
    level = f(rows[index], "High" if side > 0 else "Low")
    for back in range(1, maximum + 1):
        prior = rows[index - back]
        blocked = (f(prior, "High") >= level - 1e-9 if side > 0
                   else f(prior, "Low") <= level + 1e-9)
        if blocked:
            return back - 1
    return maximum


@dataclass
class Setup:
    ident: int
    anchor_index: int
    arm_index: int
    side: int
    anchor_time: str
    arm_time: str
    arm_key: int
    trigger_deadline_key: int
    trigger_price: float
    clear_depth: int
    strong_slopes: bool
    strong_gaps: bool
    moderate_slopes: bool
    moderate_gaps: bool
    triggered: bool = False
    trigger_key: int | None = None
    trigger_bar_index: int | None = None
    trigger_lag_bars: int | None = None
    fill_price: float | None = None
    outcome_end_key: int | None = None
    outcome: str = "NOT_TRIGGERED"
    outcome_key: int | None = None
    outcome_price: float | None = None
    ticks_seen: int = 0
    second_ambiguous: bool = False


def select_setups(rows, start, end, trigger_wait_bars):
    keys = [second_key(row["Time"]) for row in rows]
    setups = []
    for i in range(10, len(rows) - 13):
        anchor, first_pullback = rows[i], rows[i + 1]
        side = direction(anchor)
        if (not side or not trend_bar(anchor, side) or
                not counter_bar(first_pullback, side) or
                not in_prime_time(first_pullback["Time"])):
            continue
        arm_key = keys[i + 1]
        if arm_key < start or arm_key > end:
            continue
        clear_depth = pivot_clear_depth(rows, i, side)
        if clear_depth < 7:
            continue

        s8 = side * f(anchor, "Slope8")
        s24 = side * f(anchor, "Slope24")
        s50 = side * f(anchor, "Slope50")
        gap824 = abs(f(anchor, "EMA8") - f(anchor, "EMA24")) / TICK
        gap2450 = abs(f(anchor, "EMA24") - f(anchor, "EMA50")) / TICK
        moderate_slopes = s8 >= 45 and s24 >= 30 and s50 >= 20
        if not moderate_slopes:
            continue
        strong_slopes = s8 >= 60 and s24 >= 45 and s50 >= 39
        deadline_index = min(i + 1 + trigger_wait_bars, len(rows) - 13)
        pivot = f(anchor, "High" if side > 0 else "Low")
        setups.append(Setup(
            len(setups), i, i + 1, side, anchor["Time"],
            first_pullback["Time"], arm_key, keys[deadline_index],
            pivot + side * 2 * TICK, clear_depth,
            strong_slopes, gap824 >= 1.5 and gap2450 >= 3,
            moderate_slopes, gap824 >= 1.5 and gap2450 >= 1.5))
    return setups, keys


def replay(setups, keys, tick_path, start, end):
    pending = sorted(setups, key=lambda x: x.arm_key)
    pending_index = 0
    active = {}
    expiry = []
    count = 0
    for tick_key, price in raw_tick_rows(tick_path):
        count += 1
        if count % 10_000_000 == 0:
            print("processed", count // 1_000_000, "million ticks; active",
                  len(active), flush=True)
        if tick_key < start:
            continue
        if tick_key > end:
            break
        while pending_index < len(pending) and pending[pending_index].arm_key < tick_key:
            setup = pending[pending_index]
            active[setup.ident] = setup
            heapq.heappush(expiry, (setup.trigger_deadline_key, setup.ident, "TRIGGER"))
            pending_index += 1
        while expiry and expiry[0][0] < tick_key:
            expiry_key, ident, phase = heapq.heappop(expiry)
            setup = active.get(ident)
            if setup is None:
                continue
            if phase == "TRIGGER" and not setup.triggered:
                active.pop(ident, None)
            elif phase == "OUTCOME" and setup.triggered and setup.outcome == "OPEN":
                setup.outcome = "NONE"
                active.pop(ident, None)

        for ident, setup in list(active.items()):
            setup.ticks_seen += 1
            if not setup.triggered:
                hit = (price >= setup.trigger_price if setup.side > 0
                       else price <= setup.trigger_price)
                if not hit or not in_prime_key(tick_key):
                    continue
                setup.triggered = True
                setup.trigger_key = tick_key
                setup.fill_price = price
                # Whole-second timestamps can contain multiple completed bars.
                # Use the last bar known at this second and flag that ambiguity.
                bar_index = bisect.bisect_left(keys, tick_key) - 1
                bar_index = max(setup.arm_index, bar_index)
                setup.trigger_bar_index = bar_index
                setup.trigger_lag_bars = bar_index - setup.arm_index
                horizon_index = min(bar_index + 12, len(keys) - 1)
                setup.outcome_end_key = keys[horizon_index]
                setup.outcome = "OPEN"
                setup.second_ambiguous = (keys.count(tick_key) > 1)
                heapq.heappush(expiry, (setup.outcome_end_key, ident, "OUTCOME"))
                continue

            target = setup.fill_price + setup.side * 5 * TICK
            stop = setup.fill_price - setup.side * 10 * TICK
            hit_target = price >= target if setup.side > 0 else price <= target
            hit_stop = price <= stop if setup.side > 0 else price >= stop
            if not hit_target and not hit_stop:
                continue
            setup.outcome = "STOP" if hit_stop else "TARGET"
            setup.outcome_key = tick_key
            setup.outcome_price = price
            active.pop(ident, None)

    for setup in setups:
        if setup.outcome == "OPEN":
            setup.outcome = "NONE"


def summarize(name, setups, family_test, maximum_lag):
    selected = [x for x in setups if family_test(x)]
    triggered = [x for x in selected if x.triggered and
                 x.trigger_lag_bars <= maximum_lag]
    outcomes = Counter(x.outcome for x in triggered)
    resolved = outcomes["TARGET"] + outcomes["STOP"]
    rate = 100 * outcomes["TARGET"] / resolved if resolved else 0
    pnl = 5 * outcomes["TARGET"] - 10 * outcomes["STOP"]

    def split_rate(test):
        subset = [x for x in triggered if test(x)]
        counts = Counter(x.outcome for x in subset)
        n = counts["TARGET"] + counts["STOP"]
        return len(subset), (100 * counts["TARGET"] / n if n else 0)

    dev_n, dev_rate = split_rate(
        lambda x: datetime.fromisoformat(x.arm_time) <= SPLIT)
    hold_n, hold_rate = split_rate(
        lambda x: datetime.fromisoformat(x.arm_time) > SPLIT)
    long_n, long_rate = split_rate(lambda x: x.side > 0)
    short_n, short_rate = split_rate(lambda x: x.side < 0)
    return {
        "name": name, "setups": len(selected), "triggered": len(triggered),
        "trigger_rate": round(100 * len(triggered) / len(selected), 4)
        if selected else 0,
        "resolved": resolved, "targets": outcomes["TARGET"],
        "stops": outcomes["STOP"], "none": outcomes["NONE"],
        "target_rate": round(rate, 4),
        "ticks_per_trigger": round(pnl / len(triggered), 4) if triggered else 0,
        "ticks_per_setup": round(pnl / len(selected), 4) if selected else 0,
        "dev_n": dev_n, "dev_rate": round(dev_rate, 4),
        "hold_n": hold_n, "hold_rate": round(hold_rate, 4),
        "long_n": long_n, "long_rate": round(long_rate, 4),
        "short_n": short_n, "short_rate": round(short_rate, 4),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--diagnostic", required=True, type=Path)
    parser.add_argument("--ticks", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--from", dest="start", required=True)
    parser.add_argument("--to", dest="end", required=True)
    parser.add_argument("--maximum-trigger-bars", type=int, default=6)
    args = parser.parse_args()
    start, end = second_key(args.start), second_key(args.end)
    with args.diagnostic.open(newline="", encoding="utf-8-sig") as source:
        rows = list(csv.DictReader(source))
    setups, keys = select_setups(rows, start, end, args.maximum_trigger_bars)
    print("loaded", len(setups), "moderate-or-strong seven-clear pullbacks",
          flush=True)
    replay(setups, keys, args.ticks, start, end)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    detail_path = args.output.with_name(args.output.stem + "_trades.csv")
    with detail_path.open("w", newline="") as target:
        fields = list(Setup.__dataclass_fields__)
        writer = csv.DictWriter(target, fieldnames=fields)
        writer.writeheader()
        for setup in setups:
            writer.writerow({key: getattr(setup, key) for key in fields})

    summaries = []
    families = (
        ("STRONG_GAPS", lambda x: x.strong_slopes and x.strong_gaps),
        ("STRONG_SLOPES", lambda x: x.strong_slopes),
        ("MODERATE_GAPS", lambda x: x.moderate_slopes and x.moderate_gaps),
        ("MODERATE_SLOPES", lambda x: x.moderate_slopes),
    )
    for family_name, family_test in families:
        for lag in (1, 2, 3, 4, 6):
            summaries.append(summarize(
                family_name + "_TRIGGER_WITHIN_" + str(lag), setups,
                family_test, lag))
    with args.output.open("w", newline="") as target:
        writer = csv.DictWriter(target, fieldnames=list(summaries[0]))
        writer.writeheader()
        writer.writerows(summaries)
    for row in summaries:
        print(row)


if __name__ == "__main__":
    main()
