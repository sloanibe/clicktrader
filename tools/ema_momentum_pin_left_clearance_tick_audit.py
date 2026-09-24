#!/usr/bin/env python3
"""Raw ask-tick audit for an exploratory body-clear pin subset."""
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
    parser.add_argument("--diagnostic", type=Path, required=True)
    parser.add_argument("--candidates", type=Path, required=True)
    parser.add_argument("--ticks", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--from", dest="start", required=True)
    parser.add_argument("--to", dest="end", required=True)
    parser.add_argument("--clear-bars", type=int, default=10)
    args = parser.parse_args()

    selected = set()
    with args.candidates.open(newline="") as source:
        for row in csv.DictReader(source):
            if (row["prime"] == "True" and
                    int(row["body_depth"]) >= args.clear_bars and
                    0 <= float(row["bar_distance8"]) < 2):
                selected.add(int(row["bar"]))

    with args.diagnostic.open(newline="", encoding="utf-8-sig") as source:
        rows = list(csv.DictReader(source))
    positions = {int(row["BarNumber"]): index for index, row in enumerate(rows)}
    items = []
    for bar in sorted(selected):
        index = positions[bar]
        row = rows[index]
        side = (1 if float(row["EMA8"]) > float(row["EMA24"]) else -1)
        expected = row["TargetStopResult"].strip()
        diagnostic = DiagnosticRow(
            bar, second_key(row["Time"]), row["Time"], side,
            float(row["Close"]), 12, expected)
        same_second = any(
            second_key(rows[index + step]["Time"]) == diagnostic.time_key
            for step in range(1, 13))
        items.append(Candidate(
            len(items), diagnostic,
            second_key(rows[index + 12]["Time"]), same_second))

    start, end = second_key(args.start), second_key(args.end)
    print("loaded", len(items), str(args.clear_bars) + "-bar body-clear",
          "prime-time pins", flush=True)
    audit(items, args.ticks, start, end, 5, 10, 0.25)
    matrix = write_results(args.output, items)
    resolved = Counter()
    for item in items:
        resolved[item.result] += 1
    print("tick outcomes", dict(resolved))
    print("diagnostic/tick matrix", dict(matrix))


if __name__ == "__main__":
    main()
