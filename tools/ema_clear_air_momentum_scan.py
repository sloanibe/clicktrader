#!/usr/bin/env python3
"""Compare tail shapes for strong-fan, seven-bar-clear momentum bars.

This is an exploratory completed-bar scan. It preserves the established
signal-close entry, +5/-10 tick outcome, 12-bar horizon, stop-first same-bar
convention, and prime-time windows used by the EMA research log.
"""
from __future__ import annotations

import argparse
import csv
from collections import Counter
from datetime import datetime
from pathlib import Path


TICK = 0.25
SPLIT = datetime.fromisoformat("2026-08-23 23:59:59")


def f(row, key):
    return float(row[key])


def direction(row):
    ema8, ema24, ema50 = f(row, "EMA8"), f(row, "EMA24"), f(row, "EMA50")
    if ema8 > ema24 > ema50:
        return 1
    if ema8 < ema24 < ema50:
        return -1
    return 0


def in_prime_time(when):
    seconds = when.hour * 3600 + when.minute * 60 + when.second
    return ((6 * 3600 + 31 * 60) <= seconds < (6 * 3600 + 45 * 60) or
            7 * 3600 <= seconds < 8 * 3600 or
            11 * 3600 <= seconds < 13 * 3600)


def clear_depth(rows, index, side, level, maximum=10):
    """Consecutive prior bars whose trend-side extreme does not reach level."""
    for back in range(1, maximum + 1):
        prior = rows[index - back]
        blocked = (f(prior, "High") >= level - 1e-9 if side > 0
                   else f(prior, "Low") <= level + 1e-9)
        if blocked:
            return back - 1
    return maximum


def diagnostic_outcome(rows, index, side):
    entry = f(rows[index], "Close")
    target = entry + side * 5 * TICK
    stop = entry - side * 10 * TICK
    for future in rows[index + 1:index + 13]:
        hit_target = (f(future, "High") >= target if side > 0
                      else f(future, "Low") <= target)
        hit_stop = (f(future, "Low") <= stop if side > 0
                    else f(future, "High") >= stop)
        if hit_stop:
            return "STOP"
        if hit_target:
            return "TARGET"
    return "NONE"


def summarize(name, items, baseline_ids):
    counts = Counter(item["outcome"] for item in items)
    resolved = counts["TARGET"] + counts["STOP"]
    rate = 100 * counts["TARGET"] / resolved if resolved else 0.0
    expectancy = ((5 * counts["TARGET"] - 10 * counts["STOP"]) /
                  len(items) if items else 0.0)

    def rate_for(predicate):
        subset = [item for item in items if predicate(item)]
        results = Counter(item["outcome"] for item in subset)
        n = results["TARGET"] + results["STOP"]
        return len(subset), (100 * results["TARGET"] / n if n else 0.0)

    dev_n, dev_rate = rate_for(lambda item: item["segment"] == "DEV")
    hold_n, hold_rate = rate_for(lambda item: item["segment"] == "HOLD")
    long_n, long_rate = rate_for(lambda item: item["direction"] > 0)
    short_n, short_rate = rate_for(lambda item: item["direction"] < 0)
    identifiers = {item["bar"] for item in items}
    overlap = len(identifiers & baseline_ids)
    return {
        "name": name, "selected": len(items), "resolved": resolved,
        "target": counts["TARGET"], "stop": counts["STOP"],
        "none": counts["NONE"], "target_rate": round(rate, 4),
        "ticks_per_selected": round(expectancy, 4),
        "dev_n": dev_n, "dev_rate": round(dev_rate, 4),
        "hold_n": hold_n, "hold_rate": round(hold_rate, 4),
        "long_n": long_n, "long_rate": round(long_rate, 4),
        "short_n": short_n, "short_rate": round(short_rate, 4),
        "overlap_current": overlap,
        "new_vs_current": len(identifiers - baseline_ids),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--diagnostic", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    with args.diagnostic.open(newline="", encoding="utf-8-sig") as source:
        rows = list(csv.DictReader(source))

    candidates = []
    for index, row in enumerate(rows):
        if (index < 10 or index + 12 >= len(rows) or
                row["OutcomeComplete"] != "True" or
                abs(f(row, "RangeTicks") - 5.0) > 0.001):
            continue
        side = direction(row)
        when = datetime.fromisoformat(row["Time"])
        if not side or not in_prime_time(when):
            continue
        trend_color = (f(row, "Close") >= f(row, "Open") if side > 0
                       else f(row, "Close") <= f(row, "Open"))
        if not trend_color:
            continue

        open_price, high, low, close = (f(row, "Open"), f(row, "High"),
                                        f(row, "Low"), f(row, "Close"))
        body_near = min(open_price, close) if side > 0 else max(open_price, close)
        body_beyond_8 = side * (body_near - f(row, "EMA8")) / TICK
        if body_beyond_8 <= 0:
            continue
        tail = ((open_price - low) / TICK if side > 0
                else (high - open_price) / TICK)
        bar_extreme = low if side > 0 else high
        distance8 = side * (bar_extreme - f(row, "EMA8")) / TICK
        close_extension = side * (close - f(rows[index - 1], "Close")) / TICK
        gap824 = abs(f(row, "EMA8") - f(row, "EMA24")) / TICK
        gap2450 = abs(f(row, "EMA24") - f(row, "EMA50")) / TICK
        slope8 = side * f(row, "Slope8")
        slope24 = side * f(row, "Slope24")
        slope50 = side * f(row, "Slope50")
        strong_fan = (slope8 >= 60 and slope24 >= 45 and slope50 >= 39 and
                      gap824 >= 1.5 and gap2450 >= 3)
        moderate_fan = (slope8 >= 45 and slope24 >= 30 and slope50 >= 20 and
                        gap824 >= 1.5 and gap2450 >= 1.5)
        candidates.append({
            "bar": int(row["BarNumber"]), "time": row["Time"],
            "direction": side, "tail": tail,
            "body_ticks": abs(close - open_price) / TICK,
            "distance8": distance8,
            "close_extension": close_extension,
            "slope8": slope8, "slope24": slope24, "slope50": slope50,
            "gap824": gap824, "gap2450": gap2450,
            "body_depth": clear_depth(rows, index, side, body_near),
            "close_depth": clear_depth(rows, index, side, close),
            "strong_fan": strong_fan, "moderate_fan": moderate_fan,
            "segment": "DEV" if when <= SPLIT else "HOLD",
            "outcome": diagnostic_outcome(rows, index, side),
        })

    def select(test):
        return [item for item in candidates if test(item)]

    current_strict = select(
        lambda x: x["strong_fan"] and 2.9 <= x["tail"] <= 5.1 and
        x["distance8"] >= 4 and x["close_extension"] >= 2)
    current_clear = select(
        lambda x: 2.9 <= x["tail"] <= 5.1 and x["body_depth"] >= 7 and
        0 <= x["distance8"] < 4)
    current_union_ids = {x["bar"] for x in current_strict + current_clear}

    variants = [
        ("CURRENT_STRICT_PIN", current_strict),
        ("CURRENT_7_CLEAR_0_4", current_clear),
        ("CURRENT_MOMENTUM_UNION", select(lambda x: x["bar"] in current_union_ids)),
    ]
    for fan_name, fan_test in (
            ("STRONG", lambda x: x["strong_fan"]),
            ("MODERATE", lambda x: x["moderate_fan"])):
        for clear_name, clear_test in (
                ("BODY7", lambda x: x["body_depth"] >= 7),
                ("CLOSE7", lambda x: x["close_depth"] >= 7)):
            variants.append((
                f"{fan_name}_{clear_name}_FULL_BODY_NO_TAIL",
                select(lambda x, ft=fan_test, ct=clear_test:
                       ft(x) and ct(x) and x["tail"] <= 0.1 and
                       x["body_ticks"] >= 4.9)))
            for tail_name, tail_test in (
                    ("TAIL_0", lambda x: x["tail"] <= 0.1),
                    ("TAIL_0_1", lambda x: x["tail"] <= 1.1),
                    ("TAIL_1_3", lambda x: 0.9 <= x["tail"] < 2.9),
                    ("TAIL_3_5", lambda x: 2.9 <= x["tail"] <= 5.1),
                    ("ANY_TAIL", lambda x: 0 <= x["tail"] <= 5.1)):
                variants.append((
                    f"{fan_name}_{clear_name}_{tail_name}",
                    select(lambda x, ft=fan_test, ct=clear_test, tt=tail_test:
                           ft(x) and ct(x) and tt(x))))

    summaries = [summarize(name, items, current_union_ids)
                 for name, items in variants]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="") as target:
        writer = csv.DictWriter(target, fieldnames=list(summaries[0]))
        writer.writeheader()
        writer.writerows(summaries)

    candidate_output = args.output.with_name(args.output.stem + "_candidates.csv")
    with candidate_output.open("w", newline="") as target:
        writer = csv.DictWriter(target, fieldnames=list(candidates[0]))
        writer.writeheader()
        writer.writerows(candidates)

    for summary in summaries:
        print(summary)


if __name__ == "__main__":
    main()
