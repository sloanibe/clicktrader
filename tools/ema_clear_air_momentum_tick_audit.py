#!/usr/bin/env python3
"""Raw ask-tick replay for exact-no-tail, seven-bar-clear momentum bars."""
from __future__ import annotations

import argparse
import csv
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from tick_execution_audit import (Candidate, DiagnosticRow, audit, second_key,
                                  write_results)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--diagnostic", required=True, type=Path)
    parser.add_argument("--candidates", required=True, type=Path)
    parser.add_argument("--ticks", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--from", dest="start", required=True)
    parser.add_argument("--to", dest="end", required=True)
    parser.add_argument("--fan", choices=("strong", "moderate"),
                        default="moderate")
    parser.add_argument("--clear-mode", choices=("body", "close"),
                        default="body")
    parser.add_argument("--clear-bars", type=int, default=7)
    parser.add_argument("--minimum-body-ticks", type=float, default=4.9)
    args = parser.parse_args()

    selected = set()
    fan_column = args.fan + "_fan"
    depth_column = args.clear_mode + "_depth"
    with args.candidates.open(newline="") as source:
        for row in csv.DictReader(source):
            if (row[fan_column] == "True" and
                    int(row[depth_column]) >= args.clear_bars and
                    float(row["body_ticks"]) >= args.minimum_body_ticks and
                    float(row["tail"]) <= 0.1):
                selected.add(int(row["bar"]))

    with args.diagnostic.open(newline="", encoding="utf-8-sig") as source:
        rows = list(csv.DictReader(source))
    positions = {int(row["BarNumber"]): index for index, row in enumerate(rows)}
    items = []
    for bar in sorted(selected):
        index = positions[bar]
        row = rows[index]
        side = (1 if float(row["EMA8"]) > float(row["EMA24"]) else -1)
        diagnostic = DiagnosticRow(
            bar, second_key(row["Time"]), row["Time"], side,
            float(row["Close"]), 12, row["TargetStopResult"].strip())
        same_second = any(
            second_key(rows[index + step]["Time"]) == diagnostic.time_key
            for step in range(1, 13))
        items.append(Candidate(
            len(items), diagnostic, second_key(rows[index + 12]["Time"]),
            same_second))

    start, end = second_key(args.start), second_key(args.end)
    print("loaded", len(items), args.fan, "exact-no-tail",
          args.clear_mode.upper() + str(args.clear_bars), "candidates", flush=True)
    audit(items, args.ticks, start, end, 5, 10, 0.25)
    matrix = write_results(args.output, items)
    outcomes = Counter(item.result for item in items)
    resolved = outcomes["TARGET"] + outcomes["STOP"]
    rate = 100 * outcomes["TARGET"] / resolved if resolved else 0
    expectancy = ((5 * outcomes["TARGET"] - 10 * outcomes["STOP"]) /
                  len(items) if items else 0)
    print("tick outcomes", dict(outcomes))
    print("resolved target rate %.2f%%; ticks/selected %+.3f" %
          (rate, expectancy))
    print("diagnostic/tick matrix", dict(matrix))


if __name__ == "__main__":
    main()
