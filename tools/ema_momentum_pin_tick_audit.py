#!/usr/bin/env python3
"""Raw-tick audit for the selected EMA-fan continuation pin family."""
import argparse
import csv
import sys
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from tick_execution_audit import Candidate, DiagnosticRow, audit, write_results


TICK = .25


def key(value):
    day = date(int(value[:4]), int(value[5:7]), int(value[8:10]))
    return (day.toordinal() * 86400 + int(value[11:13]) * 3600 +
            int(value[14:16]) * 60 + int(value[17:19]))


def bar_outcome(rows, index, direction):
    entry = float(rows[index]["Close"])
    target = entry + direction * 5 * TICK
    stop = entry - direction * 10 * TICK
    for future in rows[index+1:index+13]:
        target_hit = (float(future["High"]) >= target if direction > 0
                      else float(future["Low"]) <= target)
        stop_hit = (float(future["Low"]) <= stop if direction > 0
                    else float(future["High"]) >= stop)
        if stop_hit:
            return "STOP"
        if target_hit:
            return "TARGET"
    return "NONE"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--diagnostic", type=Path, required=True)
    parser.add_argument("--ticks", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--from", dest="start", required=True)
    parser.add_argument("--to", dest="end", required=True)
    args = parser.parse_args()
    start_time, end_time = key(args.start), key(args.end)
    with args.diagnostic.open(newline="", encoding="utf-8-sig") as source:
        rows = list(csv.DictReader(source))

    items = []
    for i, row in enumerate(rows):
        if (i < 12 or i + 12 >= len(rows) or
                not start_time <= key(row["Time"]) <= end_time or
                row["OutcomeComplete"] != "True" or
                float(row["RangeTicks"]) != 5):
            continue
        ema8, ema24, ema50 = (float(row["EMA8"]), float(row["EMA24"]),
                              float(row["EMA50"]))
        if ema8 > ema24 > ema50:
            direction = 1
        elif ema8 < ema24 < ema50:
            direction = -1
        else:
            continue
        open_price, high, low, close = (
            float(row["Open"]), float(row["High"]), float(row["Low"]),
            float(row["Close"]))
        trend_color = close >= open_price if direction > 0 else close <= open_price
        if not trend_color:
            continue
        tail = ((open_price - low) / TICK if direction > 0
                else (high - open_price) / TICK)
        if tail < 2.9 or tail > 5.1:
            continue
        body_near = min(open_price, close) if direction > 0 else max(open_price, close)
        body_distance = direction * (body_near - ema8) / TICK
        if body_distance <= 0:
            continue
        bar_extreme = low if direction > 0 else high
        bar_distance = direction * (bar_extreme - ema8) / TICK
        slope8 = direction * float(row["Slope8"])
        slope24 = direction * float(row["Slope24"])
        slope50 = direction * float(row["Slope50"])
        gap824 = direction * (ema8 - ema24) / TICK
        gap2450 = direction * (ema24 - ema50) / TICK
        extension = direction * (close - float(rows[i-1]["Close"])) / TICK
        if (slope8 < 60 or slope24 < 45 or slope50 < 39 or gap824 < 1.5 or
                gap2450 < 3 or bar_distance < 4 or extension < 2):
            continue

        diagnostic = DiagnosticRow(
            int(row["BarNumber"]), key(row["Time"]), row["Time"], direction,
            close, 12, bar_outcome(rows, i, direction))
        same_second = any(key(rows[i+j]["Time"]) == key(row["Time"])
                          for j in range(1, 13))
        items.append(Candidate(len(items), diagnostic, key(rows[i+12]["Time"]),
                               same_second))

    print("loaded", len(items), "EMA momentum pins", flush=True)
    audit(items, args.ticks, start_time, end_time, 5, 10, TICK)
    write_results(args.output, items)


if __name__ == "__main__":
    main()
