#!/usr/bin/env python3
"""Walk-forward scan for 8/24/50 pullbacks that reject the 24 EMA.

The EMA touch may occur on the signal bar or on one of the immediately
preceding countertrend pullback bars.  The signal bar must close back on the
trend side of the 24 EMA with trend color.
"""
import argparse
import csv
from datetime import date
from pathlib import Path


def time_key(value):
    day = date(int(value[:4]), int(value[5:7]), int(value[8:10]))
    return (day.toordinal() * 86400 + int(value[11:13]) * 3600 +
            int(value[14:16]) * 60 + int(value[17:19]))


def with_trend(row, direction):
    return (row["c"] >= row["o"] if direction > 0
            else row["c"] <= row["o"])


def countertrend(row, direction):
    return (row["c"] < row["o"] if direction > 0
            else row["c"] > row["o"])


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
                "time": time_key(raw["Time"]), "text_time": raw["Time"],
                "o": float(raw["Open"]), "h": float(raw["High"]),
                "l": float(raw["Low"]), "c": float(raw["Close"]),
                "range": float(raw["RangeTicks"]),
                "d": int(raw["TrendDirection"]),
                "s24": float(raw["Slope24"]),
                "s50": float(raw["Slope50"]),
                "g824": float(raw["Separation8_24Ticks"]),
                "g2450": float(raw["Separation24_50Ticks"]),
                "lo24": float(raw["LowToEMA24Ticks"]),
                "hi24": float(raw["HighToEMA24Ticks"]),
                "cl24": float(raw["CloseToEMA24Ticks"]),
                "complete": raw["OutcomeComplete"] == "True",
                "result": raw["TargetStopResult"].strip(),
            })

    base = {length: [] for length in range(1, 6)}
    for i, row in enumerate(rows):
        if (i < 12 or i + 12 >= len(rows) or row["range"] != 5 or
                not row["complete"] or row["d"] == 0):
            continue
        direction = row["d"]
        ordered = (row["g824"] > 0 and
                   direction * row["g2450"] > 0)
        close_side = (row["cl24"] >= 0 if direction > 0
                      else row["cl24"] <= 0)
        if not ordered or not close_side or not with_trend(row, direction):
            continue

        prior24 = max(direction * rows[i-j]["s24"] for j in range(1, 7))
        current24 = direction * row["s24"]
        current50 = direction * row["s50"]
        recovery = (direction * (row["c"] - rows[i-1]["c"]) / .25)
        gap2450 = direction * row["g2450"]

        for length in base:
            pullback = all(countertrend(rows[i-j], direction)
                           for j in range(1, length + 1))
            preceding = with_trend(rows[i-length-1], direction)
            if not pullback or not preceding:
                continue
            penetrations = []
            for bars_back in range(0, length + 1):
                touch_row = rows[i-bars_back]
                penetrations.append(-touch_row["lo24"] if direction > 0
                                    else touch_row["hi24"])
            deepest = max(penetrations)
            base[length].append((
                row["time"] <= split, row["result"] == "TARGET",
                prior24, current24, current50, row["g824"], gap2450,
                deepest, recovery, row["text_time"], direction))

    output = []
    for length, candidates in base.items():
        print("pullback_bars=%d base_candidates=%d" %
              (length, len(candidates)))
        for prior24_min in (20, 30, 39, 45):
            for current24_min in (0, 10, 15, 20):
                for current50_min in (0, 10):
                    for gap824_min in (1.5, 3, 5):
                        for gap2450_min in (1.5, 3, 5):
                            for touch_tolerance in (0, 1):
                                for penetration_max in (2.5, 5, 7.5, 10):
                                    for recovery_min in (0, 1, 2):
                                        totals = [[0, 0], [0, 0]]
                                        for item in candidates:
                                            (early, won, prior24, current24,
                                             current50, gap824, gap2450,
                                             penetration, recovery, _, _) = item
                                            if (prior24 < prior24_min or
                                                    current24 < current24_min or
                                                    current50 < current50_min or
                                                    gap824 < gap824_min or
                                                    gap2450 < gap2450_min or
                                                    penetration < -touch_tolerance or
                                                    penetration > penetration_max or
                                                    recovery < recovery_min):
                                                continue
                                            bucket = totals[0 if early else 1]
                                            bucket[0] += 1
                                            bucket[1] += won
                                        dev_rate = (100 * totals[0][1] /
                                                    totals[0][0]
                                                    if totals[0][0] else 0)
                                        hold_rate = (100 * totals[1][1] /
                                                     totals[1][0]
                                                     if totals[1][0] else 0)
                                        output.append([
                                            length, prior24_min, current24_min,
                                            current50_min, gap824_min,
                                            gap2450_min, touch_tolerance,
                                            penetration_max, recovery_min,
                                            *totals[0], round(dev_rate, 2),
                                            *totals[1], round(hold_rate, 2)])

    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="") as target:
        writer = csv.writer(target)
        writer.writerow([
            "PullbackBars", "Prior24Min", "Current24Min", "Current50Min",
            "Gap824Min", "Gap2450Min", "TouchTolerance", "PenMax",
            "RecoveryMin", "DevN", "DevTarget", "DevRate", "HoldN",
            "HoldTarget", "HoldRate"])
        writer.writerows(output)

    for length in base:
        eligible = [row for row in output if row[0] == length and
                    row[9] >= 35 and row[12] >= 35]
        print("TOP", length)
        for row in sorted(
                eligible,
                key=lambda value: (-min(value[11], value[14]),
                                   -(value[9] + value[12])))[:10]:
            print(",".join(map(str, row)))


if __name__ == "__main__":
    main()
