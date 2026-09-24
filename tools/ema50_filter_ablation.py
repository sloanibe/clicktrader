#!/usr/bin/env python3
"""Ablate 50-EMA order, slope, and gap gates from current prime-time setups."""
from __future__ import annotations

import argparse
import csv
from collections import Counter
from datetime import datetime
from pathlib import Path

from ema_signal_stop_sweep import (TICK, best_slope, current_slope, f,
                                   pullback, quality24, quality50, quality8,
                                   quality_pin, trend)


SPLIT = "2026-08-23 23:59:59"


def full_direction(row):
    e8, e24, e50 = f(row, "EMA8"), f(row, "EMA24"), f(row, "EMA50")
    if e8 > e24 > e50:
        return 1
    if e8 < e24 < e50:
        return -1
    return 0


def direction824(row):
    gap = f(row, "EMA8") - f(row, "EMA24")
    return 1 if gap > 0 else (-1 if gap < 0 else 0)


def prime_time(timestamp):
    when = datetime.fromisoformat(timestamp)
    value = when.hour * 3600 + when.minute * 60 + when.second
    return ((6 * 3600 + 31 * 60) <= value < (6 * 3600 + 45 * 60) or
            7 * 3600 <= value < 8 * 3600 or
            11 * 3600 <= value < 13 * 3600)


def outcome(rows, index, side):
    entry = f(rows[index], "Close")
    target, stop = entry + side * 5 * TICK, entry - side * 10 * TICK
    for future in rows[index + 1:index + 13]:
        hit_target = f(future, "High") >= target if side > 0 else f(future, "Low") <= target
        hit_stop = f(future, "Low") <= stop if side > 0 else f(future, "High") >= stop
        if hit_stop:
            return "STOP"
        if hit_target:
            return "TARGET"
    return "NONE"


def quality24_no50(rows, index, side):
    row = rows[index]
    length = pullback(rows, index, side, 4)
    if not length or not trend(row, side):
        return False
    ema24 = f(row, "EMA24")
    if f(row, "Close") < ema24 if side > 0 else f(row, "Close") > ema24:
        return False
    prior24 = best_slope(rows, index, "EMA24", side, 6)
    slope24 = current_slope(rows, index, "EMA24", side)
    gap8 = abs(f(row, "EMA8") - ema24) / TICK
    recovery = side * (f(row, "Close") - f(rows[index - 1], "Close")) / TICK
    deepest = max(
        ((f(rows[index-j], "EMA24") - f(rows[index-j], "Low")) / TICK
         if side > 0 else
         (f(rows[index-j], "High") - f(rows[index-j], "EMA24")) / TICK)
        for j in range(length + 1))
    if not 0 <= deepest <= 5:
        return False
    tail = ((f(row, "Open") - f(row, "Low")) / TICK if side > 0
            else (f(row, "High") - f(row, "Open")) / TICK)
    base = ((length == 1 and prior24 >= 45 and slope24 >= 10 and gap8 >= 3 and recovery >= 0) or
            (length == 2 and prior24 >= 30 and slope24 >= 15 and gap8 >= 1.5 and recovery >= 2) or
            (length == 3 and prior24 >= 39 and slope24 >= 0 and gap8 >= 1.5 and recovery >= 1))
    return (base or
            (length == 2 and abs(tail - 1) <= .1 and prior24 >= 30 and
             slope24 >= 10 and gap8 >= 3 and recovery >= 2) or
            (length == 2 and prior24 >= 30 and slope24 >= 15 and gap8 >= 1.5 and
             recovery < 2 and deepest <= 1) or
            (length == 4 and tail <= .1 and prior24 >= 39 and slope24 >= 0 and
             gap8 >= 1.5 and recovery >= 2))


def quality_pin_no50(rows, index, side):
    row = rows[index]
    if not trend(row, side):
        return False
    tail = ((f(row, "Open") - f(row, "Low")) / TICK if side > 0
            else (f(row, "High") - f(row, "Open")) / TICK)
    if not 2.9 <= tail <= 5.1:
        return False
    ema8 = f(row, "EMA8")
    body_near = min(f(row, "Open"), f(row, "Close")) if side > 0 else max(f(row, "Open"), f(row, "Close"))
    extreme = f(row, "Low") if side > 0 else f(row, "High")
    return (side * (body_near - ema8) / TICK > 0 and
            current_slope(rows, index, "EMA8", side) >= 60 and
            current_slope(rows, index, "EMA24", side) >= 45 and
            abs(ema8 - f(row, "EMA24")) / TICK >= 1.5 and
            side * (extreme - ema8) / TICK >= 4 and
            side * (f(row, "Close") - f(rows[index-1], "Close")) / TICK >= 2)


def quality_pin7(rows, index, side):
    row = rows[index]
    if not trend(row, side):
        return False
    tail = ((f(row, "Open") - f(row, "Low")) / TICK if side > 0
            else (f(row, "High") - f(row, "Open")) / TICK)
    if not 2.9 <= tail <= 5.1:
        return False
    ema8 = f(row, "EMA8")
    body_near = min(f(row, "Open"), f(row, "Close")) if side > 0 else max(f(row, "Open"), f(row, "Close"))
    extreme = f(row, "Low") if side > 0 else f(row, "High")
    distance = side * (extreme - ema8) / TICK
    if side * (body_near - ema8) / TICK <= 0 or not 0 <= distance < 4:
        return False
    for back in range(1, 8):
        blocked = (f(rows[index-back], "High") >= body_near if side > 0
                   else f(rows[index-back], "Low") <= body_near)
        if blocked:
            return False
    return True


def summarize(family, version, records):
    counts = Counter(record["outcome"] for record in records)
    resolved = counts["TARGET"] + counts["STOP"]
    dev = [record for record in records if record["segment"] == "DEV"]
    hold = [record for record in records if record["segment"] == "HOLD"]

    def rate(items):
        count = Counter(record["outcome"] for record in items)
        total = count["TARGET"] + count["STOP"]
        return 100 * count["TARGET"] / total if total else 0

    return [family, version, len(records), resolved, counts["TARGET"],
            counts["STOP"], counts["NONE"], rate(records), len(dev), rate(dev),
            len(hold), rate(hold)]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--diagnostic", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    with args.diagnostic.open(newline="", encoding="utf-8-sig") as source:
        rows = list(csv.DictReader(source))

    records = {family: {version: [] for version in ("CURRENT", "NO_50_ORDER", "NO_50_ALL")}
               for family in ("EMA8", "EMA24", "PIN_STRICT", "PIN7", "EMA50")}
    combined = {version: {} for version in ("CURRENT", "NO_50_ORDER", "NO_50_ALL")}
    detail = []
    for index, row in enumerate(rows):
        if (index < 20 or index + 12 >= len(rows) or
                row["OutcomeComplete"] != "True" or
                abs(f(row, "RangeTicks") - 5) > .001 or
                not prime_time(row["Time"])):
            continue
        directions = {"CURRENT": full_direction(row),
                      "NO_50_ORDER": direction824(row),
                      "NO_50_ALL": direction824(row)}
        for version, side in directions.items():
            if not side:
                continue
            tests = {
                "EMA8": quality8(rows, index, side),
                "EMA24": (quality24_no50(rows, index, side)
                          if version == "NO_50_ALL" else quality24(rows, index, side)),
                "PIN_STRICT": (quality_pin_no50(rows, index, side)
                               if version == "NO_50_ALL" else quality_pin(rows, index, side)),
                "PIN7": quality_pin7(rows, index, side),
            }
            for family, selected in tests.items():
                if not selected:
                    continue
                result = outcome(rows, index, side)
                item = {"bar": int(row["BarNumber"]), "time": row["Time"],
                        "direction": side, "family": family, "version": version,
                        "segment": "DEV" if row["Time"] <= SPLIT else "HOLD",
                        "outcome": result}
                records[family][version].append(item)
                detail.append(item)
                combined[version][(item["bar"], side)] = item

        side50 = 1 if f(row, "EMA24") > f(row, "EMA50") else -1
        if quality50(rows, index, side50):
            result = outcome(rows, index, side50)
            for version in records["EMA50"]:
                records["EMA50"][version].append({
                    "bar": int(row["BarNumber"]), "time": row["Time"],
                    "direction": side50, "family": "EMA50", "version": version,
                    "segment": "DEV" if row["Time"] <= SPLIT else "HOLD",
                    "outcome": result})

    summaries = []
    for family, versions in records.items():
        for version, selected in versions.items():
            summaries.append(summarize(family, version, selected))
    for version, selected in combined.items():
        summaries.append(summarize("NON50_COMBINED", version,
                                   list(selected.values())))

    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="") as target:
        writer = csv.writer(target)
        writer.writerow(["Family", "Version", "Selected", "Resolved", "Target",
                         "Stop", "None", "TargetRate", "DevN", "DevRate",
                         "HoldN", "HoldRate"])
        writer.writerows(summaries)
    detail_path = args.output.with_name(args.output.stem + "_candidates.csv")
    with detail_path.open("w", newline="") as target:
        writer = csv.DictWriter(target, fieldnames=list(detail[0]))
        writer.writeheader()
        writer.writerows(detail)
    print("wrote", args.output, "and", detail_path)
    for row in summaries:
        print(row)


if __name__ == "__main__":
    main()
