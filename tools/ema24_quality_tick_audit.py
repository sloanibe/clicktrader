#!/usr/bin/env python3
"""Raw-tick audit for the selected 8/24/50 quality 24-EMA tiers."""
import argparse
import csv
import sys
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from tick_execution_audit import Candidate, DiagnosticRow, audit, write_results


def key(value):
    day = date(int(value[:4]), int(value[5:7]), int(value[8:10]))
    return (day.toordinal() * 86400 + int(value[11:13]) * 3600 +
            int(value[14:16]) * 60 + int(value[17:19]))


def trend_color(row, direction):
    return (float(row["Close"]) >= float(row["Open"]) if direction > 0
            else float(row["Close"]) <= float(row["Open"]))


def counter_color(row, direction):
    return (float(row["Close"]) < float(row["Open"]) if direction > 0
            else float(row["Close"]) > float(row["Open"]))


def tier_settings(length):
    return {
        1: (45, 10, 3, 3, 5, 0),
        2: (30, 15, 1.5, 3, 5, 2),
        3: (39, 0, 1.5, 1.5, 5, 1),
    }[length]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--diagnostic", type=Path, required=True)
    parser.add_argument("--ticks", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--from", dest="start", required=True)
    parser.add_argument("--to", dest="end", required=True)
    args = parser.parse_args()
    start, end = key(args.start), key(args.end)
    with args.diagnostic.open(newline="", encoding="utf-8-sig") as source:
        rows = list(csv.DictReader(source))

    items = []
    lengths = {}
    for i, row in enumerate(rows):
        if (i < 12 or i + 12 >= len(rows) or
                not start <= key(row["Time"]) <= end or
                row["OutcomeComplete"] != "True" or
                float(row["RangeTicks"]) != 5):
            continue
        direction = int(row["TrendDirection"])
        if direction == 0 or not trend_color(row, direction):
            continue
        ordered = (float(row["Separation8_24Ticks"]) > 0 and
                   direction * float(row["Separation24_50Ticks"]) > 0)
        close_side = (float(row["CloseToEMA24Ticks"]) >= 0
                      if direction > 0
                      else float(row["CloseToEMA24Ticks"]) <= 0)
        if not ordered or not close_side:
            continue

        length = 0
        for candidate_length in (1, 2, 3):
            if (all(counter_color(rows[i-j], direction)
                    for j in range(1, candidate_length + 1)) and
                    trend_color(rows[i-candidate_length-1], direction)):
                length = candidate_length
                break
        if length == 0:
            continue

        prior_min, current_min, gap824_min, gap2450_min, pen_max, recovery_min = tier_settings(length)
        prior24 = max(direction * float(rows[i-j]["Slope24"])
                      for j in range(1, 7))
        current24 = direction * float(row["Slope24"])
        gap824 = float(row["Separation8_24Ticks"])
        gap2450 = direction * float(row["Separation24_50Ticks"])
        recovery = direction * (float(row["Close"]) -
                                float(rows[i-1]["Close"])) / .25
        penetration = max(
            -float(rows[i-j]["LowToEMA24Ticks"]) if direction > 0
            else float(rows[i-j]["HighToEMA24Ticks"])
            for j in range(0, length + 1))
        if (prior24 < prior_min or current24 < current_min or
                gap824 < gap824_min or gap2450 < gap2450_min or
                penetration < 0 or penetration > pen_max or
                recovery < recovery_min):
            continue

        diagnostic = DiagnosticRow(
            int(row["BarNumber"]), key(row["Time"]), row["Time"], direction,
            float(row["Close"]), 12, row["TargetStopResult"].strip())
        same_second = any(key(rows[i+j]["Time"]) == key(row["Time"])
                          for j in range(1, 13))
        candidate = Candidate(len(items), diagnostic, key(rows[i+12]["Time"]),
                              same_second)
        items.append(candidate)
        lengths[candidate.candidate_id] = length

    print("loaded", len(items), "quality EMA24 candidates", flush=True)
    audit(items, args.ticks, start, end, 5, 10, .25)
    write_results(args.output, items)
    sidecar = args.output.with_name(args.output.stem + "_lengths.csv")
    with sidecar.open("w", newline="") as target:
        writer = csv.writer(target)
        writer.writerow(["CandidateIndex", "PullbackBars"])
        writer.writerows(sorted(lengths.items()))


if __name__ == "__main__":
    main()
