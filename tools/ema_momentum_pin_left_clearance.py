#!/usr/bin/env python3
"""Measure horizontal open space to the left of EMA-fan momentum pins.

The study tests two obstacle levels over the preceding ten completed bars:

* close_depth: consecutive prior bars that do not reach the signal's
  trend-side close/extreme;
* body_depth: consecutive prior bars that do not reach the pullback-side edge
  of the signal body.

This is a diagnostic bar-path screen. Outcomes retain the established +5 tick
target, -10 tick stop, 12-bar horizon, and stop-first same-bar convention.
"""
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


def trend_color(row, side):
    return f(row, "Close") >= f(row, "Open") if side > 0 else f(row, "Close") <= f(row, "Open")


def in_prime_time(when):
    seconds = when.hour * 3600 + when.minute * 60 + when.second
    return ((6 * 3600 + 31 * 60) <= seconds < (6 * 3600 + 45 * 60) or
            7 * 3600 <= seconds < 8 * 3600 or
            11 * 3600 <= seconds < 13 * 3600)


def open_depth(rows, index, side, level, maximum=10):
    """Number of immediately preceding bars clear of level, capped at maximum."""
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
        hit_target = f(future, "High") >= target if side > 0 else f(future, "Low") <= target
        hit_stop = f(future, "Low") <= stop if side > 0 else f(future, "High") >= stop
        if hit_stop:
            return "STOP"
        if hit_target:
            return "TARGET"
    return "NONE"


def summarize(name, records):
    results = Counter(item["outcome"] for item in records)
    resolved = results["TARGET"] + results["STOP"]
    rate = 100 * results["TARGET"] / resolved if resolved else 0
    expectancy = (5 * results["TARGET"] - 10 * results["STOP"]) / len(records) if records else 0
    dev = [item for item in records if item["segment"] == "DEV"]
    hold = [item for item in records if item["segment"] == "HOLD"]

    def segment_rate(items):
        counts = Counter(item["outcome"] for item in items)
        n = counts["TARGET"] + counts["STOP"]
        return 100 * counts["TARGET"] / n if n else 0

    return {
        "name": name, "n": len(records), "resolved": resolved,
        "target": results["TARGET"], "stop": results["STOP"],
        "none": results["NONE"], "rate": rate, "expectancy": expectancy,
        "dev_n": len(dev), "dev_rate": segment_rate(dev),
        "hold_n": len(hold), "hold_rate": segment_rate(hold),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--diagnostic", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    with args.diagnostic.open(newline="", encoding="utf-8-sig") as source:
        rows = list(csv.DictReader(source))

    candidates = []
    for index, row in enumerate(rows):
        if (index < 10 or index + 12 >= len(rows) or
                row["OutcomeComplete"] != "True" or
                abs(f(row, "RangeTicks") - 5) > 0.001):
            continue
        side = direction(row)
        if not side or not trend_color(row, side):
            continue

        open_price, high, low, close = (f(row, "Open"), f(row, "High"),
                                        f(row, "Low"), f(row, "Close"))
        tail = ((open_price - low) / TICK if side > 0
                else (high - open_price) / TICK)
        if not 2.9 <= tail <= 5.1:
            continue
        body_near = min(open_price, close) if side > 0 else max(open_price, close)
        if side * (body_near - f(row, "EMA8")) / TICK <= 0:
            continue

        body = abs(close - open_price) / TICK
        bar_extreme = low if side > 0 else high
        bar_distance = side * (bar_extreme - f(row, "EMA8")) / TICK
        extension = side * (close - f(rows[index - 1], "Close")) / TICK
        gap824 = abs(f(row, "EMA8") - f(row, "EMA24")) / TICK
        gap2450 = abs(f(row, "EMA24") - f(row, "EMA50")) / TICK
        core = (side * f(row, "Slope8") >= 60 and
                side * f(row, "Slope24") >= 45 and
                side * f(row, "Slope50") >= 39 and
                gap824 >= 1.5 and gap2450 >= 3 and extension >= 2)
        when = datetime.fromisoformat(row["Time"])
        candidates.append({
            "bar": int(row["BarNumber"]), "time": row["Time"],
            "direction": side, "body": round(body, 4),
            "tail": round(tail, 4), "bar_distance8": round(bar_distance, 4),
            "close_depth": open_depth(rows, index, side, close),
            "body_depth": open_depth(rows, index, side, body_near),
            "core": core, "strict": core and bar_distance >= 4,
            "prime": in_prime_time(when),
            "segment": "DEV" if when <= SPLIT else "HOLD",
            "outcome": diagnostic_outcome(rows, index, side),
        })

    scopes = {
        "ALL": lambda item: True,
        "PRIME": lambda item: item["prime"],
    }
    summaries = []
    for scope_name, scope_test in scopes.items():
        scoped = [item for item in candidates if scope_test(item)]
        core = [item for item in scoped if item["core"]]
        strict = [item for item in core if item["strict"]]
        summaries.append(summarize(f"{scope_name}:CURRENT_STRICT", strict))
        summaries.append(summarize(f"{scope_name}:CORE_NO_DISTANCE", core))
        for body_group, body_test in (
                ("ANY_BODY", lambda item: True),
                ("BODY_1_2", lambda item: 0.9 <= item["body"] <= 2.1)):
            base = [item for item in core if body_test(item)]
            for metric in ("close_depth", "body_depth"):
                for depth in (1, 2, 3, 5, 7, 10):
                    selected = [item for item in base if item[metric] >= depth]
                    summaries.append(summarize(
                        f"{scope_name}:{body_group}:{metric}>={depth}", selected))
                    rescued = [item for item in selected if not item["strict"]]
                    summaries.append(summarize(
                        f"{scope_name}:RESCUE:{body_group}:{metric}>={depth}", rescued))

    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="") as target:
        fields = list(summaries[0])
        writer = csv.DictWriter(target, fieldnames=fields)
        writer.writeheader()
        writer.writerows(summaries)
    candidate_path = args.output.with_name(args.output.stem + "_candidates.csv")
    with candidate_path.open("w", newline="") as target:
        writer = csv.DictWriter(target, fieldnames=list(candidates[0]))
        writer.writeheader()
        writer.writerows(candidates)

    print("broad candidates", len(candidates), "core candidates",
          sum(item["core"] for item in candidates), "strict candidates",
          sum(item["strict"] for item in candidates))
    for row in summaries:
        if ("CURRENT_STRICT" in row["name"] or "CORE_NO_DISTANCE" in row["name"] or
                ("BODY_1_2" in row["name"] and any(
                    token in row["name"] for token in (">=3", ">=5", ">=7", ">=10")))):
            print(row)


if __name__ == "__main__":
    main()
