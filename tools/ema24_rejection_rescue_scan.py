#!/usr/bin/env python3
"""Test rejection geometry as a rescue for low-recovery 24-EMA pullbacks."""
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


def counter(row, direction):
    return row["c"] < row["o"] if direction > 0 else row["c"] > row["o"]


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
                "d": int(raw["TrendDirection"]),
                "s24": float(raw["Slope24"]),
                "g824": float(raw["Separation8_24Ticks"]),
                "g2450": float(raw["Separation24_50Ticks"]),
                "lo24": float(raw["LowToEMA24Ticks"]),
                "hi24": float(raw["HighToEMA24Ticks"]),
                "cl24": float(raw["CloseToEMA24Ticks"]),
                "complete": raw["OutcomeComplete"] == "True",
                "won": raw["TargetStopResult"].strip() == "TARGET",
            })

    candidates = []
    for i, row in enumerate(rows):
        if (i < 12 or i + 12 >= len(rows) or row["range"] != 5 or
                not row["complete"] or not row["d"]):
            continue
        direction = row["d"]
        ordered = row["g824"] > 0 and direction * row["g2450"] > 0
        two_bar = (counter(rows[i-1], direction) and
                   counter(rows[i-2], direction) and
                   trend(rows[i-3], direction))
        close_side = row["cl24"] >= 0 if direction > 0 else row["cl24"] <= 0
        if not ordered or not two_bar or not close_side or not trend(row, direction):
            continue
        prior24 = max(direction * rows[i-j]["s24"] for j in range(1, 7))
        current24 = direction * row["s24"]
        gap2450 = direction * row["g2450"]
        penetration = max(
            -rows[i-j]["lo24"] if direction > 0 else rows[i-j]["hi24"]
            for j in range(3))
        recovery = direction * (row["c"] - rows[i-1]["c"]) / TICK
        if (prior24 < 30 or current24 < 15 or row["g824"] < 1.5 or
                gap2450 < 3 or not 0 <= penetration <= 5 or recovery >= 2):
            continue

        tail = ((row["o"] - row["l"]) / TICK if direction > 0
                else (row["h"] - row["o"]) / TICK)
        body = direction * (row["c"] - row["o"]) / TICK
        finish = ((row["h"] - row["c"]) / TICK if direction > 0
                  else (row["c"] - row["l"]) / TICK)
        extreme_recovery = ((row["c"] - row["l"]) / TICK if direction > 0
                            else (row["h"] - row["c"]) / TICK)
        pullback_extreme = (min(rows[i-j]["l"] for j in range(3))
                            if direction > 0 else
                            max(rows[i-j]["h"] for j in range(3)))
        sequence_recovery = (direction * (row["c"] - pullback_extreme) / TICK)
        candidates.append({
            "bar": row["bar"], "time": row["time"],
            "text_time": row["text_time"], "direction": direction,
            "early": row["time"] <= split, "won": row["won"],
            "prior24": prior24, "current24": current24,
            "gap824": row["g824"], "gap2450": gap2450,
            "penetration": penetration, "recovery": recovery,
            "close_beyond24": direction * row["cl24"], "tail": tail,
            "body": body, "finish": finish,
            "extreme_recovery": extreme_recovery,
            "sequence_recovery": sequence_recovery,
        })

    output = []
    configs = itertools.product(
        (30, 39, 45), (15, 20, 30), (1.5, 3, 5), (3, 5),
        (1, 2.5, 5), (0, 1.5, 3, 4), (0, 1, 2, 3, 4),
        (0, 1, 2, 4), (0, 1), (3, 4, 5), (3, 4, 5))
    for cfg in configs:
        (prior_min, current_min, gap824_min, gap2450_min, pen_max,
         close_min, tail_min, body_min, finish_max, extreme_min,
         sequence_min) = cfg
        totals = [[0, 0], [0, 0]]
        for item in candidates:
            if (item["prior24"] < prior_min or
                    item["current24"] < current_min or
                    item["gap824"] < gap824_min or
                    item["gap2450"] < gap2450_min or
                    item["penetration"] > pen_max or
                    item["close_beyond24"] < close_min or
                    item["tail"] < tail_min or item["body"] < body_min or
                    item["finish"] > finish_max or
                    item["extreme_recovery"] < extreme_min or
                    item["sequence_recovery"] < sequence_min):
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
            "Prior24Min", "Current24Min", "Gap824Min", "Gap2450Min",
            "PenMax", "CloseBeyond24Min", "TailMin", "BodyMin",
            "FinishMax", "ExtremeRecoveryMin", "SequenceRecoveryMin",
            "DevN", "DevTarget", "DevRate", "HoldN", "HoldTarget",
            "HoldRate"])
        writer.writerows(output)

    candidate_path = args.output.with_name(args.output.stem + "_candidates.csv")
    with candidate_path.open("w", newline="") as target:
        fields = list(candidates[0]) if candidates else []
        writer = csv.DictWriter(target, fieldnames=fields)
        writer.writeheader()
        writer.writerows(candidates)

    print("low-recovery candidates", len(candidates))
    example = [x for x in candidates
               if x["text_time"] == "2026-09-10 17:34:35.000"]
    print("selected example", example)
    eligible = [x for x in output if x[11] >= 12 and x[14] >= 12]
    for row in sorted(eligible,
                      key=lambda x: (-min(x[13], x[16]),
                                     -(x[11] + x[14])))[:40]:
        print("TOP", ",".join(map(str, row)))


if __name__ == "__main__":
    main()
