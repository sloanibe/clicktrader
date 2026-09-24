#!/usr/bin/env python3
"""Summarize current EMA signals by Pacific/chart-time operating window."""
from __future__ import annotations

import argparse
import csv
from datetime import datetime
from pathlib import Path


def metrics(rows):
    resolved = [row for row in rows if row["outcome"] in ("TARGET", "STOP")]
    wins = sum(row["outcome"] == "TARGET" for row in resolved)
    pnl = sum(5 if row["outcome"] == "TARGET" else
              (-10 if row["outcome"] == "STOP" else 0) for row in rows)
    return [len(rows), len(resolved), wins,
            "%.4f" % (100*wins/len(resolved) if resolved else 0),
            "%.4f" % (pnl/len(rows) if rows else 0)]


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--outcomes", required=True, type=Path)
    p.add_argument("--output", required=True, type=Path)
    p.add_argument("--split", default="2026-08-23 23:59:59")
    a = p.parse_args()
    with a.outcomes.open(newline="") as h:
        rows = []
        for raw in csv.DictReader(h):
            stamp = datetime.fromisoformat(raw["Time"])
            rows.append({"raw": raw, "stamp": stamp,
                         "minute": stamp.hour*60 + stamp.minute +
                         stamp.second/60 + stamp.microsecond/60_000_000,
                         "early": raw["Time"] <= a.split,
                         "outcome": raw["Stop10"]})

    records = []
    windows = [
        ("OPEN_BUCKET", "06:30-06:31", 390, 391),
        ("OPEN_BUCKET", "06:31-06:35", 391, 395),
        ("OPEN_BUCKET", "06:35-06:40", 395, 400),
        ("OPEN_BUCKET", "06:40-06:45", 400, 405),
        ("RTH_BLOCK", "06:30-06:45", 390, 405),
        ("RTH_BLOCK", "06:45-07:00", 405, 420),
        ("RTH_BLOCK", "07:00-08:00", 420, 480),
        ("RTH_BLOCK", "08:00-09:00", 480, 540),
        ("RTH_BLOCK", "09:00-11:00", 540, 660),
        ("RTH_BLOCK", "11:00-13:00", 660, 780),
    ]
    for kind, name, start, end in windows:
        base = [row for row in rows if start <= row["minute"] < end]
        for segment, subset in (("ALL", base),
                                ("DEVELOPMENT", [r for r in base if r["early"]]),
                                ("LATER", [r for r in base if not r["early"]])):
            records.append([kind, name, segment, *metrics(subset)])

    for wait in (0, 1, 2, 3, 5, 7, 10, 15, 20, 30, 45, 60):
        base = [row for row in rows if 390+wait <= row["minute"] < 780]
        for segment, subset in (("ALL", base),
                                ("DEVELOPMENT", [r for r in base if r["early"]]),
                                ("LATER", [r for r in base if not r["early"]])):
            records.append(["RTH_AFTER_WAIT", str(wait), segment,
                            *metrics(subset)])
        first = {}
        for row in sorted(base, key=lambda value: value["stamp"]):
            first.setdefault(row["raw"]["Time"][:10], row)
        selected = list(first.values())
        for segment, subset in (("ALL", selected),
                                ("DEVELOPMENT", [r for r in selected if r["early"]]),
                                ("LATER", [r for r in selected if not r["early"]])):
            records.append(["FIRST_AFTER_WAIT", str(wait), segment,
                            *metrics(subset)])

    schedules = {
        "RTH_BASELINE": lambda m: 390 <= m < 780,
        "ONE_MINUTE_BUFFER": lambda m: 391 <= m < 780,
        "CANDIDATE_BLOCKS": lambda m: ((391 <= m < 405) or
                                         (420 <= m < 480) or
                                         (540 <= m < 780)),
    }
    for name, allowed in schedules.items():
        base = [row for row in rows if allowed(row["minute"])]
        for segment, subset in (("ALL", base),
                                ("DEVELOPMENT", [r for r in base if r["early"]]),
                                ("LATER", [r for r in base if not r["early"]])):
            records.append(["SCHEDULE", name, segment, *metrics(subset)])
        first = {}
        for row in sorted(base, key=lambda value: value["stamp"]):
            first.setdefault(row["raw"]["Time"][:10], row)
        selected = list(first.values())
        for segment, subset in (("ALL", selected),
                                ("DEVELOPMENT", [r for r in selected if r["early"]]),
                                ("LATER", [r for r in selected if not r["early"]])):
            records.append(["FIRST_IN_SCHEDULE", name, segment,
                            *metrics(subset)])

    for hour in range(24):
        base = [row for row in rows if hour*60 <= row["minute"] < (hour+1)*60]
        for segment, subset in (("ALL", base),
                                ("DEVELOPMENT", [r for r in base if r["early"]]),
                                ("LATER", [r for r in base if not r["early"]])):
            records.append(["HOUR", "%02d:00" % hour, segment,
                            *metrics(subset)])

    a.output.parent.mkdir(parents=True, exist_ok=True)
    with a.output.open("w", newline="") as h:
        w = csv.writer(h)
        w.writerow(["Analysis", "Window", "Segment", "Selected", "Resolved",
                    "Targets", "TargetRate", "PnlTicksPerSelected"])
        w.writerows(records)
    print("wrote", a.output)


if __name__ == "__main__":
    main()
