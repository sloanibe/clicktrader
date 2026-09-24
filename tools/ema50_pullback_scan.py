#!/usr/bin/env python3
"""Independent chronological scan for deep pullbacks to the 50 EMA.

Direction comes from the 24/50 relationship, not the diagnostic's existing
bounce labels. Outcomes are recalculated from future bars in that direction.
"""
import argparse
import csv
import itertools
import math
from collections import defaultdict
from datetime import date
from pathlib import Path


TICK = .25


def time_key(value):
    day = date(int(value[:4]), int(value[5:7]), int(value[8:10]))
    return (day.toordinal() * 86400 + int(value[11:13]) * 3600 +
            int(value[14:16]) * 60 + int(value[17:19]))


def trend_bar(row, direction):
    return row["c"] >= row["o"] if direction > 0 else row["c"] <= row["o"]


def counter_bar(row, direction):
    return row["c"] < row["o"] if direction > 0 else row["c"] > row["o"]


def angle(current, previous, bars):
    return math.degrees(math.atan2(current - previous, bars * TICK))


def best_slope(rows, index, field, direction, lookback=8, slope_bars=3):
    return max(direction * angle(rows[index-j][field],
                                 rows[index-j-slope_bars][field], slope_bars)
               for j in range(1, lookback + 1))


def classify_tail(row, direction):
    tail = ((row["o"] - row["l"]) / TICK if direction > 0
            else (row["h"] - row["o"]) / TICK)
    if tail <= .1:
        return tail, "no_tail"
    if abs(tail - 1) <= .1:
        return tail, "tail_1"
    if tail >= 4:
        return tail, "tail_ge4"
    return tail, "tail_2_3"


def forward_outcome(rows, index, direction, horizon=12):
    entry = rows[index]["c"]
    target = entry + direction * 5 * TICK
    stop = entry - direction * 10 * TICK
    for future in rows[index+1:index+horizon+1]:
        target_hit = future["h"] >= target if direction > 0 else future["l"] <= target
        stop_hit = future["l"] <= stop if direction > 0 else future["h"] >= stop
        if stop_hit:
            return False
        if target_hit:
            return True
    return False


def rate(wins, count):
    return round(100 * wins / count, 2) if count else 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--diagnostic", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--split", required=True)
    args = parser.parse_args()
    split = time_key(args.split)

    rows = []
    with args.diagnostic.open(newline="", encoding="utf-8-sig") as source:
        for raw in csv.DictReader(source):
            rows.append({
                "bar": int(raw["BarNumber"]), "time": time_key(raw["Time"]),
                "text_time": raw["Time"], "o": float(raw["Open"]),
                "h": float(raw["High"]), "l": float(raw["Low"]),
                "c": float(raw["Close"]), "range": float(raw["RangeTicks"]),
                "e8": float(raw["EMA8"]), "e24": float(raw["EMA24"]),
                "e50": float(raw["EMA50"]),
                "g824": float(raw["Separation8_24Ticks"]),
                "g850": float(raw["Separation8_50Ticks"]),
                "g2450": float(raw["Separation24_50Ticks"]),
                "lo50": float(raw["LowToEMA50Ticks"]),
                "hi50": float(raw["HighToEMA50Ticks"]),
                "cl50": float(raw["CloseToEMA50Ticks"]),
                "complete": raw["OutcomeComplete"] == "True",
            })

    candidates = []
    descriptive = defaultdict(lambda: [[0, 0], [0, 0]])
    for i, row in enumerate(rows):
        if (i < 20 or i + 12 >= len(rows) or row["range"] != 5 or
                not row["complete"] or abs(row["g2450"]) < .001):
            continue
        direction = 1 if row["g2450"] > 0 else -1
        if not trend_bar(row, direction):
            continue
        close_side = row["cl50"] >= 0 if direction > 0 else row["cl50"] <= 0
        if not close_side:
            continue
        length = 0
        for n in range(1, 7):
            if (all(counter_bar(rows[i-j], direction) for j in range(1, n+1)) and
                    trend_bar(rows[i-n-1], direction)):
                length = n
                break
        if not length:
            continue

        start = rows[i-length-1]
        start_order = (direction * (start["e8"] - start["e24"]) > 0 and
                       direction * (start["e24"] - start["e50"]) > 0)
        penetration = max(
            -rows[i-j]["lo50"] if direction > 0 else rows[i-j]["hi50"]
            for j in range(length + 1))
        if penetration < -1 or penetration > 20:
            continue
        recovery = direction * (row["c"] - rows[i-1]["c"]) / TICK
        prior50 = best_slope(rows, i, "e50", direction)
        prior24 = best_slope(rows, i, "e24", direction)
        current50 = direction * angle(row["e50"], rows[i-3]["e50"], 3)
        gap2450 = direction * row["g2450"]
        tail, shape = classify_tail(row, direction)
        won = forward_outcome(rows, i, direction)
        early = row["time"] <= split
        item = {
            "bar": row["bar"], "time": row["time"],
            "text_time": row["text_time"], "direction": direction,
            "entry": row["c"], "length": length, "start_order": start_order,
            "prior50": prior50, "prior24": prior24,
            "current50": current50, "gap2450": gap2450,
            "penetration": penetration, "recovery": recovery,
            "tail": tail, "shape": shape, "early": early, "won": won,
        }
        candidates.append(item)
        for group in ((length, "all"), (length, shape), (0, shape), (0, "all")):
            bucket = descriptive[group][0 if early else 1]
            bucket[0] += 1
            bucket[1] += won

    configs = itertools.product(
        range(1, 7), (10, 20, 30, 39), (-10, 0, 10), (0, 20, 30),
        (0, 1.5, 3), (0, 1), (5, 10, 15), (0, 2),
        ("any", "no_tail", "tail_1", "tail_2_3", "tail_ge4"),
        (False, True))
    output = []
    by_length = defaultdict(list)
    for item in candidates:
        by_length[item["length"]].append(item)
    for cfg in configs:
        (length, prior50_min, current50_min, prior24_min, gap_min,
         touch_tolerance, pen_max, recovery_min, shape, require_order) = cfg
        totals = [[0, 0], [0, 0]]
        for item in by_length[length]:
            if (item["prior50"] < prior50_min or
                    item["current50"] < current50_min or
                    item["prior24"] < prior24_min or
                    item["gap2450"] < gap_min or
                    item["penetration"] < -touch_tolerance or
                    item["penetration"] > pen_max or
                    item["recovery"] < recovery_min or
                    (shape != "any" and item["shape"] != shape) or
                    (require_order and not item["start_order"])):
                continue
            bucket = totals[0 if item["early"] else 1]
            bucket[0] += 1
            bucket[1] += item["won"]
        if totals[0][0] and totals[1][0]:
            output.append([*cfg, totals[0][0], totals[0][1],
                           rate(totals[0][1], totals[0][0]), totals[1][0],
                           totals[1][1], rate(totals[1][1], totals[1][0])])

    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="") as target:
        writer = csv.writer(target)
        writer.writerow([
            "PullbackBars", "Prior50Min", "Current50Min", "Prior24Min",
            "Gap2450Min", "TouchTolerance", "PenMax", "RecoveryMin",
            "Shape", "RequireStartOrder", "DevN", "DevTarget", "DevRate",
            "HoldN", "HoldTarget", "HoldRate"])
        writer.writerows(output)

    candidate_path = args.output.with_name(args.output.stem + "_candidates.csv")
    with candidate_path.open("w", newline="") as target:
        fields = list(candidates[0]) if candidates else []
        writer = csv.DictWriter(target, fieldnames=fields)
        writer.writeheader()
        writer.writerows(candidates)

    print("broad candidates", len(candidates))
    for (length, shape), values in sorted(descriptive.items()):
        if length and shape == "all":
            print("BASE", length, *values[0], rate(values[0][1], values[0][0]),
                  *values[1], rate(values[1][1], values[1][0]))
    eligible = [row for row in output if row[10] >= 20 and row[13] >= 20]
    for row in sorted(eligible,
                      key=lambda x: (-min(x[12], x[15]),
                                     -(x[10] + x[13])))[:40]:
        print("TOP", ",".join(map(str, row)))


if __name__ == "__main__":
    main()
