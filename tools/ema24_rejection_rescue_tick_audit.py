#!/usr/bin/env python3
"""Raw-tick audit for shallow, low-close-recovery two-bar 24-EMA probes."""
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


def trend(row, direction):
    return (float(row["Close"]) >= float(row["Open"]) if direction > 0
            else float(row["Close"]) <= float(row["Open"]))


def counter(row, direction):
    return (float(row["Close"]) < float(row["Open"]) if direction > 0
            else float(row["Close"]) > float(row["Open"]))


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
        direction = int(row["TrendDirection"])
        if direction == 0 or not trend(row, direction):
            continue
        ordered = (float(row["Separation8_24Ticks"]) > 0 and
                   direction * float(row["Separation24_50Ticks"]) > 0)
        two_bar = (counter(rows[i-1], direction) and
                   counter(rows[i-2], direction) and
                   trend(rows[i-3], direction))
        close_side = (float(row["CloseToEMA24Ticks"]) >= 0
                      if direction > 0
                      else float(row["CloseToEMA24Ticks"]) <= 0)
        if not ordered or not two_bar or not close_side:
            continue

        prior24 = max(direction * float(rows[i-j]["Slope24"])
                      for j in range(1, 7))
        current24 = direction * float(row["Slope24"])
        gap824 = float(row["Separation8_24Ticks"])
        gap2450 = direction * float(row["Separation24_50Ticks"])
        recovery = direction * (float(row["Close"]) -
                                float(rows[i-1]["Close"])) / TICK
        penetration = max(
            -float(rows[i-j]["LowToEMA24Ticks"]) if direction > 0
            else float(rows[i-j]["HighToEMA24Ticks"])
            for j in range(3))
        if (prior24 < 30 or current24 < 15 or gap824 < 1.5 or
                gap2450 < 3 or not 0 <= penetration <= 1 or recovery >= 2):
            continue

        diagnostic = DiagnosticRow(
            int(row["BarNumber"]), key(row["Time"]), row["Time"], direction,
            float(row["Close"]), 12, row["TargetStopResult"].strip())
        same_second = any(key(rows[i+j]["Time"]) == key(row["Time"])
                          for j in range(1, 13))
        items.append(Candidate(len(items), diagnostic, key(rows[i+12]["Time"]),
                               same_second))

    print("loaded", len(items), "shallow EMA24 rescue candidates", flush=True)
    audit(items, args.ticks, start_time, end_time, 5, 10, TICK)
    write_results(args.output, items)


if __name__ == "__main__":
    main()
