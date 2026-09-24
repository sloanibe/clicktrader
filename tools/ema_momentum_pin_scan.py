#!/usr/bin/env python3
"""Independent scan for continuation pin bars clear of the 8 EMA."""
import argparse
import csv
import itertools
from collections import defaultdict
from datetime import date
from pathlib import Path


TICK = .25


def time_key(value):
    day = date(int(value[:4]), int(value[5:7]), int(value[8:10]))
    return (day.toordinal() * 86400 + int(value[11:13]) * 3600 +
            int(value[14:16]) * 60 + int(value[17:19]))


def trend(row, direction):
    return row["c"] >= row["o"] if direction > 0 else row["c"] <= row["o"]


def outcome(rows, index, direction):
    entry = rows[index]["c"]
    target = entry + direction * 5 * TICK
    stop = entry - direction * 10 * TICK
    for future in rows[index+1:index+13]:
        target_hit = future["h"] >= target if direction > 0 else future["l"] <= target
        stop_hit = future["l"] <= stop if direction > 0 else future["h"] >= stop
        if stop_hit:
            return "STOP"
        if target_hit:
            return "TARGET"
    return "NONE"


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
                "e50": float(raw["EMA50"]), "s8": float(raw["Slope8"]),
                "s24": float(raw["Slope24"]), "s50": float(raw["Slope50"]),
                "complete": raw["OutcomeComplete"] == "True",
            })

    candidates = []
    for i, row in enumerate(rows):
        if (i < 12 or i + 12 >= len(rows) or row["range"] != 5 or
                not row["complete"]):
            continue
        if row["e8"] > row["e24"] > row["e50"]:
            direction = 1
        elif row["e8"] < row["e24"] < row["e50"]:
            direction = -1
        else:
            continue
        if not trend(row, direction):
            continue
        tail = ((row["o"] - row["l"]) / TICK if direction > 0
                else (row["h"] - row["o"]) / TICK)
        if tail < 3 - .1 or tail > 5 + .1:
            continue
        body_near = min(row["o"], row["c"]) if direction > 0 else max(row["o"], row["c"])
        body_distance = direction * (body_near - row["e8"]) / TICK
        if body_distance <= 0:
            continue
        bar_extreme = row["l"] if direction > 0 else row["h"]
        bar_distance = direction * (bar_extreme - row["e8"]) / TICK
        body = direction * (row["c"] - row["o"]) / TICK
        finish = ((row["h"] - row["c"]) / TICK if direction > 0
                  else (row["c"] - row["l"]) / TICK)
        gap824 = direction * (row["e8"] - row["e24"]) / TICK
        gap2450 = direction * (row["e24"] - row["e50"]) / TICK
        close_extension = direction * (row["c"] - rows[i-1]["c"]) / TICK
        prior_streak = 0
        for bars_back in range(1, 5):
            if trend(rows[i-bars_back], direction):
                prior_streak += 1
            else:
                break
        result = outcome(rows, i, direction)
        candidates.append({
            "bar": row["bar"], "time": row["time"],
            "text_time": row["text_time"], "direction": direction,
            "entry": row["c"], "early": row["time"] <= split,
            "result": result, "won": result == "TARGET", "tail": tail,
            "body": body, "finish": finish, "s8": direction * row["s8"],
            "s24": direction * row["s24"], "s50": direction * row["s50"],
            "gap824": gap824, "gap2450": gap2450,
            "body_distance8": body_distance, "bar_distance8": bar_distance,
            "close_extension": close_extension, "prior_streak": prior_streak,
        })

    output = []
    configs = itertools.product(
        (3, 4), (20, 30, 39, 45, 60), (20, 30, 39, 45),
        (0, 10, 20, 30, 39), (1.5, 3, 5), (1.5, 3),
        (0, 1, 3, 5), (-5, 0, 1, 3, 5), (0, 1, 2), (0, 1, 2))
    for cfg in configs:
        (tail_min, slope8_min, slope24_min, slope50_min, gap824_min,
         gap2450_min, body_distance_min, bar_distance_min,
         extension_min, streak_min) = cfg
        totals = [[0, 0], [0, 0]]
        for item in candidates:
            if (item["tail"] < tail_min or item["s8"] < slope8_min or
                    item["s24"] < slope24_min or item["s50"] < slope50_min or
                    item["gap824"] < gap824_min or
                    item["gap2450"] < gap2450_min or
                    item["body_distance8"] < body_distance_min or
                    item["bar_distance8"] < bar_distance_min or
                    item["close_extension"] < extension_min or
                    item["prior_streak"] < streak_min):
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
            "TailMin", "Slope8Min", "Slope24Min", "Slope50Min",
            "Gap824Min", "Gap2450Min", "BodyDistance8Min",
            "BarDistance8Min", "CloseExtensionMin", "PriorStreakMin",
            "DevN", "DevTarget", "DevRate", "HoldN", "HoldTarget",
            "HoldRate"])
        writer.writerows(output)
    candidate_path = args.output.with_name(args.output.stem + "_candidates.csv")
    with candidate_path.open("w", newline="") as target:
        fields = list(candidates[0]) if candidates else []
        writer = csv.DictWriter(target, fieldnames=fields)
        writer.writeheader()
        writer.writerows(candidates)

    print("broad momentum pins", len(candidates))
    print("selected example", [x for x in candidates
                               if x["text_time"] == "2026-09-11 08:06:17.844"])
    eligible = [x for x in output if x[10] >= 25 and x[13] >= 25]
    for row in sorted(eligible,
                      key=lambda x: (-min(x[12], x[15]),
                                     -(x[10] + x[13])))[:40]:
        print("TOP", ",".join(map(str, row)))


if __name__ == "__main__":
    main()
