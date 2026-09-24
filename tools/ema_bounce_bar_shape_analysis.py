#!/usr/bin/env python3
"""Analyze directional body/tail shape for selected and nearby EMA bounces."""
import argparse
import csv
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


def pullback_length(rows, index, direction, maximum):
    for length in range(1, maximum + 1):
        if (all(counter_bar(rows[index-j], direction)
                for j in range(1, length + 1)) and
                trend_bar(rows[index-length-1], direction)):
            return length
    return 0


def shape(row, direction):
    tail = ((row["o"] - row["l"]) / TICK if direction > 0
            else (row["h"] - row["o"]) / TICK)
    body = direction * (row["c"] - row["o"]) / TICK
    finish_wick = ((row["h"] - row["c"]) / TICK if direction > 0
                   else (row["c"] - row["l"]) / TICK)
    if tail <= .1:
        category = "no_tail"
    elif tail >= 2:
        category = "long_tail"
    else:
        category = "one_tick_tail"
    return round(tail, 3), round(body, 3), round(finish_wick, 3), category


def prior_slope(rows, index, field, direction):
    return max(direction * rows[index-j][field] for j in range(1, 7))


def selected_8(rows, index):
    row = rows[index]
    direction = row["d"]
    length = pullback_length(rows, index, direction, 2)
    if not length:
        return 0
    penetration = -row["lo8"] if direction > 0 else row["hi8"]
    reference = (min(rows[index-1]["l"], rows[index-2]["l"])
                 if direction > 0 else
                 max(rows[index-1]["h"], rows[index-2]["h"]))
    displacement = ((reference - row["l"]) / TICK if direction > 0
                    else (row["h"] - reference) / TICK)
    close_side = row["cl8"] >= 0 if direction > 0 else row["cl8"] <= 0
    return length if (
        row["x8"] and close_side and trend_bar(row, direction) and
        row["g824"] >= 5 and direction * row["s8"] >= 15 and
        prior_slope(rows, index, "s24", direction) >= (20 if length == 1 else 39) and
        1 <= penetration <= (4.5 if length == 1 else 2.5) and
        displacement >= 1) else 0


def selected_24(rows, index):
    row = rows[index]
    direction = row["d"]
    length = pullback_length(rows, index, direction, 3)
    if not length:
        return 0
    settings = {
        1: (45, 10, 3, 3, 0),
        2: (30, 15, 1.5, 3, 2),
        3: (39, 0, 1.5, 1.5, 1),
    }[length]
    prior_min, current_min, gap824_min, gap2450_min, recovery_min = settings
    deepest = max(
        -rows[index-j]["lo24"] if direction > 0 else rows[index-j]["hi24"]
        for j in range(length + 1))
    recovery = direction * (row["c"] - rows[index-1]["c"]) / TICK
    close_side = row["cl24"] >= 0 if direction > 0 else row["cl24"] <= 0
    return length if (
        close_side and trend_bar(row, direction) and
        prior_slope(rows, index, "s24", direction) >= prior_min and
        direction * row["s24"] >= current_min and
        row["g824"] >= gap824_min and direction * row["g2450"] >= gap2450_min and
        recovery >= recovery_min and 0 <= deepest <= 5) else 0


def shape_expansion_8(rows, index):
    row = rows[index]
    direction = row["d"]
    length = pullback_length(rows, index, direction, 3)
    if not length:
        return 0
    penetration = -row["lo8"] if direction > 0 else row["hi8"]
    reference = (min(rows[index-1]["l"], rows[index-2]["l"])
                 if direction > 0 else
                 max(rows[index-1]["h"], rows[index-2]["h"]))
    displacement = ((reference - row["l"]) / TICK if direction > 0
                    else (row["h"] - reference) / TICK)
    tail, _, _, _ = shape(row, direction)
    common = (row["x8"] and trend_bar(row, direction) and
              (row["cl8"] >= 0 if direction > 0 else row["cl8"] <= 0))
    if not common:
        return 0
    prior24 = prior_slope(rows, index, "s24", direction)
    current8 = direction * row["s8"]
    if (length == 1 and tail <= .1 and prior24 >= 45 and current8 >= 0 and
            row["g824"] >= 3 and 0 <= penetration <= 4.5):
        return length
    if (length == 1 and abs(tail - 1) <= .1 and prior24 >= 20 and
            current8 >= 15 and row["g824"] >= 5 and
            1 <= penetration <= 2.5):
        return length
    if (length == 1 and tail >= 4 and prior24 >= 20 and current8 >= 15 and
            row["g824"] >= 3 and 0 <= penetration <= 2.5 and
            displacement >= 1):
        return length
    if (length == 3 and tail <= .1 and prior24 >= 20 and current8 >= 0 and
            row["g824"] >= 3 and 0 <= penetration <= 4.5 and
            displacement >= 1):
        return length
    return 0


def shape_expansion_24(rows, index):
    row = rows[index]
    direction = row["d"]
    length = pullback_length(rows, index, direction, 4)
    if not length or not trend_bar(row, direction):
        return 0
    close_side = row["cl24"] >= 0 if direction > 0 else row["cl24"] <= 0
    if not close_side:
        return 0
    deepest = max(
        -rows[index-j]["lo24"] if direction > 0 else rows[index-j]["hi24"]
        for j in range(length + 1))
    if not 0 <= deepest <= 5:
        return 0
    recovery = direction * (row["c"] - rows[index-1]["c"]) / TICK
    prior24 = prior_slope(rows, index, "s24", direction)
    current24 = direction * row["s24"]
    gap2450 = direction * row["g2450"]
    tail, _, _, _ = shape(row, direction)
    if (length == 2 and abs(tail - 1) <= .1 and prior24 >= 30 and
            current24 >= 10 and row["g824"] >= 3 and gap2450 >= 1.5 and
            recovery >= 2):
        return length
    if (length == 4 and tail <= .1 and prior24 >= 39 and current24 >= 0 and
            row["g824"] >= 1.5 and gap2450 >= 1.5 and recovery >= 2):
        return length
    return 0


def nearby_8(rows, index):
    row = rows[index]
    direction = row["d"]
    length = pullback_length(rows, index, direction, 3)
    if not length:
        return 0
    penetration = -row["lo8"] if direction > 0 else row["hi8"]
    close_side = row["cl8"] >= 0 if direction > 0 else row["cl8"] <= 0
    return length if (
        row["x8"] and close_side and trend_bar(row, direction) and
        row["g824"] >= 3 and direction * row["s8"] >= 0 and
        prior_slope(rows, index, "s24", direction) >= 20 and
        0 <= penetration <= 6) else 0


def nearby_24(rows, index):
    row = rows[index]
    direction = row["d"]
    length = pullback_length(rows, index, direction, 4)
    if not length:
        return 0
    deepest = max(
        -rows[index-j]["lo24"] if direction > 0 else rows[index-j]["hi24"]
        for j in range(length + 1))
    close_side = row["cl24"] >= 0 if direction > 0 else row["cl24"] <= 0
    recovery = direction * (row["c"] - rows[index-1]["c"]) / TICK
    return length if (
        close_side and trend_bar(row, direction) and
        prior_slope(rows, index, "s24", direction) >= 20 and
        direction * row["s24"] >= 0 and row["g824"] >= 1.5 and
        direction * row["g2450"] >= 1.5 and recovery >= 0 and
        -1 <= deepest <= 7) else 0


def summarize(items, split):
    groups = defaultdict(lambda: [[0, 0], [0, 0]])
    for item in items:
        period = 0 if item["time"] <= split else 1
        keys = [
            (item["ema"], item["pool"], "all", 0),
            (item["ema"], item["pool"], item["shape"], 0),
            (item["ema"], item["pool"], item["shape"], item["length"]),
            (item["ema"], item["pool"], "tail_%s" % item["tail"],
             item["length"]),
        ]
        for group in keys:
            groups[group][period][0] += 1
            groups[group][period][1] += item["won"]
    output = []
    for key, values in groups.items():
        dev_rate = 100 * values[0][1] / values[0][0] if values[0][0] else 0
        hold_rate = 100 * values[1][1] / values[1][0] if values[1][0] else 0
        output.append([*key, *values[0], round(dev_rate, 2),
                       *values[1], round(hold_rate, 2)])
    return output


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
                "time": time_key(raw["Time"]), "o": float(raw["Open"]),
                "h": float(raw["High"]), "l": float(raw["Low"]),
                "c": float(raw["Close"]), "range": float(raw["RangeTicks"]),
                "d": int(raw["TrendDirection"]), "s8": float(raw["Slope8"]),
                "s24": float(raw["Slope24"]), "s50": float(raw["Slope50"]),
                "g824": float(raw["Separation8_24Ticks"]),
                "g2450": float(raw["Separation24_50Ticks"]),
                "lo8": float(raw["LowToEMA8Ticks"]),
                "hi8": float(raw["HighToEMA8Ticks"]),
                "cl8": float(raw["CloseToEMA8Ticks"]),
                "lo24": float(raw["LowToEMA24Ticks"]),
                "hi24": float(raw["HighToEMA24Ticks"]),
                "cl24": float(raw["CloseToEMA24Ticks"]),
                "x8": raw["CrossesEMA8"] == "True",
                "complete": raw["OutcomeComplete"] == "True",
                "result": raw["TargetStopResult"].strip(),
            })

    items = []
    for i, row in enumerate(rows):
        if (i < 12 or i + 12 >= len(rows) or row["range"] != 5 or
                not row["complete"] or not row["d"]):
            continue
        if row["g824"] <= 0 or row["d"] * row["g2450"] <= 0:
            continue
        tail, body, finish, category = shape(row, row["d"])
        for ema, selected, expansion, nearby in (
                (8, selected_8(rows, i), shape_expansion_8(rows, i),
                 nearby_8(rows, i)),
                (24, selected_24(rows, i), shape_expansion_24(rows, i),
                 nearby_24(rows, i))):
            if selected:
                items.append({"ema": ema, "pool": "selected", "length": selected,
                              "tail": tail, "body": body, "finish": finish,
                              "shape": category, "time": row["time"],
                              "won": row["result"] == "TARGET"})
            if expansion and not selected:
                items.append({"ema": ema, "pool": "shape_addition",
                              "length": expansion, "tail": tail, "body": body,
                              "finish": finish, "shape": category,
                              "time": row["time"],
                              "won": row["result"] == "TARGET"})
            if selected or expansion:
                items.append({"ema": ema, "pool": "expanded_total",
                              "length": selected or expansion, "tail": tail,
                              "body": body, "finish": finish,
                              "shape": category, "time": row["time"],
                              "won": row["result"] == "TARGET"})
            if nearby and not selected:
                items.append({"ema": ema, "pool": "nearby_miss", "length": nearby,
                              "tail": tail, "body": body, "finish": finish,
                              "shape": category, "time": row["time"],
                              "won": row["result"] == "TARGET"})

    output = summarize(items, split)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="") as target:
        writer = csv.writer(target)
        writer.writerow(["EMA", "Pool", "Shape", "PullbackBars", "DevN",
                         "DevTarget", "DevRate", "HoldN", "HoldTarget",
                         "HoldRate"])
        writer.writerows(sorted(output))

    for ema in (8, 24):
        print("EMA", ema)
        for row in sorted(output):
            if row[0] == ema and (row[2] == "all" or
                                  row[2] in ("no_tail", "long_tail",
                                             "one_tick_tail")):
                print(",".join(map(str, row)))


if __name__ == "__main__":
    main()
