#!/usr/bin/env python3
"""Raw-tick audit for the independently selected 50-EMA pullback family."""
import argparse
import csv
import math
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


def angle(current, previous, bars=3):
    return math.degrees(math.atan2(current - previous, bars * TICK))


def best(rows, index, field, direction):
    return max(direction * angle(float(rows[index-j][field]),
                                 float(rows[index-j-3][field]))
               for j in range(1, 9))


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
    tiers = {}
    for i, row in enumerate(rows):
        if (i < 20 or i + 12 >= len(rows) or
                not start_time <= key(row["Time"]) <= end_time or
                row["OutcomeComplete"] != "True" or
                float(row["RangeTicks"]) != 5):
            continue
        gap2450_signed = float(row["Separation24_50Ticks"])
        if abs(gap2450_signed) < .001:
            continue
        direction = 1 if gap2450_signed > 0 else -1
        if not trend(row, direction):
            continue
        close_side = (float(row["CloseToEMA50Ticks"]) >= 0 if direction > 0
                      else float(row["CloseToEMA50Ticks"]) <= 0)
        if not close_side:
            continue
        length = 0
        for candidate_length in range(1, 7):
            if (all(counter(rows[i-j], direction)
                    for j in range(1, candidate_length + 1)) and
                    trend(rows[i-candidate_length-1], direction)):
                length = candidate_length
                break
        if not length:
            continue
        start = rows[i-length-1]
        start_order = (
            direction * (float(start["EMA8"]) - float(start["EMA24"])) > 0 and
            direction * (float(start["EMA24"]) - float(start["EMA50"])) > 0)
        if not start_order:
            continue

        penetration = max(
            -float(rows[i-j]["LowToEMA50Ticks"]) if direction > 0
            else float(rows[i-j]["HighToEMA50Ticks"])
            for j in range(length + 1))
        recovery = direction * (float(row["Close"]) -
                                float(rows[i-1]["Close"])) / TICK
        prior50 = best(rows, i, "EMA50", direction)
        prior24 = best(rows, i, "EMA24", direction)
        current50 = direction * angle(float(row["EMA50"]),
                                      float(rows[i-3]["EMA50"]))
        gap2450 = direction * gap2450_signed
        tail = ((float(row["Open"]) - float(row["Low"])) / TICK
                if direction > 0 else
                (float(row["High"]) - float(row["Open"])) / TICK)
        no_tail = tail <= .1
        tail_2_3 = 2 - .1 <= tail <= 3 + .1

        tier = ""
        if (length == 1 and no_tail and prior50 >= 10 and current50 >= -10 and
                gap2450 >= 1.5 and -1 <= penetration <= 5 and recovery >= 0):
            tier = "ONE_BAR_NO_TAIL"
        elif (length == 1 and tail_2_3 and prior50 >= 20 and
              current50 >= 10 and gap2450 >= 0 and
              -1 <= penetration <= 5 and recovery >= 0):
            tier = "ONE_BAR_TAIL_2_3"
        elif (length == 2 and tail_2_3 and prior50 >= 20 and
              current50 >= 10 and gap2450 >= 0 and
              -1 <= penetration <= 5 and recovery >= 2):
            tier = "TWO_BAR_TAIL_2_3"
        elif (length == 3 and prior50 >= 20 and current50 >= 0 and
              prior24 >= 30 and gap2450 >= 1.5 and
              0 <= penetration <= 5 and recovery >= 2):
            tier = "THREE_BAR_CORE"
        elif (length == 1 and prior50 >= 10 and current50 >= 10 and
              gap2450 >= 3 and 0 <= penetration <= 5 and recovery >= 0):
            tier = "ONE_BAR_STRICT_OTHER"
        if not tier:
            continue

        diagnostic = DiagnosticRow(
            int(row["BarNumber"]), key(row["Time"]), row["Time"], direction,
            float(row["Close"]), 12, bar_outcome(rows, i, direction))
        same_second = any(key(rows[i+j]["Time"]) == key(row["Time"])
                          for j in range(1, 13))
        candidate = Candidate(len(items), diagnostic, key(rows[i+12]["Time"]),
                              same_second)
        items.append(candidate)
        tiers[candidate.candidate_id] = tier

    print("loaded", len(items), "quality EMA50 candidates", flush=True)
    audit(items, args.ticks, start_time, end_time, 5, 10, TICK)
    write_results(args.output, items)
    sidecar = args.output.with_name(args.output.stem + "_tiers.csv")
    with sidecar.open("w", newline="") as target:
        writer = csv.writer(target)
        writer.writerow(["CandidateIndex", "Tier"])
        writer.writerows(sorted(tiers.items()))


if __name__ == "__main__":
    main()
