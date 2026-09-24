#!/usr/bin/env python3
"""Raw-tick audit for the broad relaxation around a momentum-pin example.

The audited superset lowers only the three selected-pin gates that the
2026-09-11 12:09:52.700 zero-body example fails.  The output can therefore be
joined by BarNumber to the momentum-pin candidate table to evaluate the
current family, the incremental relaxed population, and narrower zero-body
exceptions from one chronological pass through the large tick file.
"""
import argparse
import csv
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from ema_momentum_pin_tick_audit import TICK, bar_outcome, key
from tick_execution_audit import Candidate, DiagnosticRow, audit, write_results


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
        if not (close >= open_price if direction > 0 else close <= open_price):
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

        # Broadest rounded thresholds that admit the motivating example.
        if (slope8 < 60 or slope24 < 45 or slope50 < 30 or
                gap824 < 1.5 or gap2450 < 3 or bar_distance < 1 or
                extension < 1):
            continue
        diagnostic = DiagnosticRow(
            int(row["BarNumber"]), key(row["Time"]), row["Time"], direction,
            close, 12, bar_outcome(rows, i, direction))
        same_second = any(key(rows[i+j]["Time"]) == key(row["Time"])
                          for j in range(1, 13))
        items.append(Candidate(len(items), diagnostic, key(rows[i+12]["Time"]),
                               same_second))

    print("loaded", len(items), "broad relaxed momentum pins", flush=True)
    audit(items, args.ticks, start_time, end_time, 5, 10, TICK)
    write_results(args.output, items)


if __name__ == "__main__":
    main()
