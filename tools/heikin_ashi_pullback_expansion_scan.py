#!/usr/bin/env python3
"""Explore HA trend/pullback/expansion signals with range-bar proxy outcomes."""
import argparse
import bisect
import csv
import itertools
from collections import Counter, deque
from datetime import datetime
from pathlib import Path


TICK = 0.25


def f(row, name):
    return float(row[name])


def read_range_bars(path):
    rows = []
    with path.open(newline="", encoding="utf-8-sig") as source:
        for raw in csv.DictReader(source):
            rows.append({
                "time": raw["Time"],
                "day": raw["Time"][:10],
                "close": f(raw, "Close"),
                "high": f(raw, "High"),
                "low": f(raw, "Low"),
            })
    return rows


def range_outcome(rows, index, direction):
    if index + 12 >= len(rows):
        return "INCOMPLETE"
    entry = rows[index]["close"]
    target = entry + direction * 5 * TICK
    stop = entry - direction * 10 * TICK
    for row in rows[index + 1:index + 13]:
        target_hit = row["high"] >= target if direction > 0 else row["low"] <= target
        stop_hit = row["low"] <= stop if direction > 0 else row["high"] >= stop
        # This matches the conservative same-range-bar convention used before.
        if stop_hit:
            return "STOP"
        if target_hit:
            return "TARGET"
    return "NONE"


def compact_row(raw):
    direction = int(raw["HADirection"])
    return {
        "time": raw["Time"], "day": raw["Time"][:10], "dir": direction,
        "o": f(raw, "HAOpen"), "h": f(raw, "HAHigh"),
        "l": f(raw, "HALow"), "c": f(raw, "HAClose"),
        "body": f(raw, "HABodyTicks"),
        "btr": f(raw, "HABodyToRangeRatio"),
        "body_rel": f(raw, "HABodyVsPriorAverage"),
        "close_loc": f(raw, "HACloseLocation"),
        "s8": f(raw, "HASlope8"), "s24": f(raw, "HASlope24"),
        "s50": f(raw, "HASlope50"),
        "g824": f(raw, "HAGap8_24Ticks"),
        "g2450": f(raw, "HAGap24_50Ticks"),
        "bull": raw["HABullishOrder"] == "True",
        "bear": raw["HABearishOrder"] == "True",
    }


def scan_candidates(path):
    candidates = []
    recent = deque(maxlen=7)
    with path.open(newline="", encoding="utf-8-sig") as source:
        for raw in csv.DictReader(source):
            row = compact_row(raw)
            direction = 1 if row["bull"] else -1 if row["bear"] else 0
            if direction and row["dir"] == direction and recent:
                pullback = []
                for prior in reversed(recent):
                    if prior["dir"] == direction:
                        break
                    pullback.append(prior)
                    if len(pullback) == 6:
                        break
                pullback.reverse()
                if (1 <= len(pullback) <= 5 and
                        any(x["dir"] == -direction for x in pullback)):
                    sequence = pullback + [row]
                    directional_close = (row["close_loc"] if direction > 0
                                         else 1.0 - row["close_loc"])
                    pull_extreme = (max(x["h"] for x in pullback) if direction > 0
                                    else min(x["l"] for x in pullback))
                    clears = (row["c"] > pull_extreme if direction > 0
                              else row["c"] < pull_extreme)
                    bodies = [x["body"] for x in pullback]
                    btrs = [x["btr"] for x in pullback]
                    rels = [x["body_rel"] for x in pullback]
                    candidates.append({
                        "time": row["time"], "day": row["day"],
                        "direction": direction, "pullback_len": len(pullback),
                        "pb_avg_body": sum(bodies) / len(bodies),
                        "pb_max_body": max(bodies), "pb_last_body": bodies[-1],
                        "pb_shrink_ratio": (bodies[-1] / bodies[0]
                                            if bodies[0] > 0 else 99.0),
                        "pb_max_btr": max(btrs),
                        "pb_avg_rel": sum(rels) / len(rels),
                        "pb_doji25": sum(x <= .25 for x in btrs),
                        "pb_doji35": sum(x <= .35 for x in btrs),
                        "trigger_body": row["body"], "trigger_btr": row["btr"],
                        "trigger_rel": row["body_rel"],
                        "trigger_close": directional_close,
                        "clears_pullback": clears,
                        "min_s8": min(direction * x["s8"] for x in sequence),
                        "min_s24": min(direction * x["s24"] for x in sequence),
                        "min_s50": min(direction * x["s50"] for x in sequence),
                        "min_g824": min(x["g824"] for x in sequence),
                        "min_g2450": min(x["g2450"] for x in sequence),
                        "order_all": all(x["bull"] if direction > 0 else x["bear"]
                                         for x in sequence),
                    })
            recent.append(row)
    return candidates


TRENDS = {
    # Two-second HA slopes are structurally much smaller than five-tick
    # range-bar slopes. These levels span the observed HA distribution.
    "T1_ordered_rising": (0, 0, 0, .25, .25),
    "T2_moderate": (2, 2, 1, .5, .5),
    "T3_strong": (4, 4, 2, .75, .75),
    "T4_very_strong": (6, 6, 3, 1, 1),
}

PULLBACKS = {
    "P1_any_1to5": lambda x: 1 <= x["pullback_len"] <= 5,
    "P2_compact": lambda x: (1 <= x["pullback_len"] <= 3 and
                              x["pb_max_body"] <= .5 and x["pb_max_btr"] <= .60),
    "P3_doji": lambda x: (1 <= x["pullback_len"] <= 5 and
                           x["pb_avg_body"] <= .4 and x["pb_doji35"] >= 1),
    "P4_shrink_doji": lambda x: (2 <= x["pullback_len"] <= 5 and
                                  x["pb_avg_body"] <= .5 and
                                  x["pb_last_body"] <= .25 and
                                  x["pb_shrink_ratio"] <= .75 and
                                  x["pb_doji35"] >= 1),
    "P5_relative_weak": lambda x: (1 <= x["pullback_len"] <= 4 and
                                    x["pb_avg_rel"] <= .75 and
                                    x["pb_max_btr"] <= .60),
}

TRIGGERS = {
    "E1_expand": (.4, .50, 1.0, .70, False),
    "E2_strong": (.6, .60, 1.25, .75, False),
    "E3_strong_clear": (.6, .60, 1.25, .75, True),
    "E4_very_strong_clear": (.8, .70, 1.50, .80, True),
}


def qualifies(x, trend, pullback_fn, trigger):
    s8, s24, s50, g824, g2450 = trend
    body, btr, rel, close_loc, clear = trigger
    return (x["order_all"] and x["min_s8"] >= s8 and
            x["min_s24"] >= s24 and x["min_s50"] >= s50 and
            x["min_g824"] >= g824 and x["min_g2450"] >= g2450 and
            pullback_fn(x) and x["trigger_body"] >= body and
            x["trigger_btr"] >= btr and x["trigger_rel"] >= rel and
            x["trigger_close"] >= close_loc and
            (not clear or x["clears_pullback"]))


def summarize(selected, split, lockout=True):
    kept = []
    last_range_index = -1000
    for x in selected:
        if lockout and x["range_index"] <= last_range_index + 12:
            continue
        kept.append(x)
        last_range_index = x["range_index"]
    counts = Counter(x["outcome"] for x in kept)
    dev = [x for x in kept if x["time"] < split]
    hold = [x for x in kept if x["time"] >= split]
    longs = [x for x in kept if x["direction"] > 0]
    shorts = [x for x in kept if x["direction"] < 0]
    def stats(items):
        resolved = sum(x["outcome"] in ("TARGET", "STOP") for x in items)
        wins = sum(x["outcome"] == "TARGET" for x in items)
        return len(items), resolved, wins, 100 * wins / resolved if resolved else 0
    return kept, counts, stats(dev), stats(hold), stats(longs), stats(shorts)


def percentile(values, fraction):
    if not values:
        return 0.0
    ordered = sorted(values)
    return ordered[int((len(ordered) - 1) * fraction)]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--ha", type=Path, required=True)
    parser.add_argument("--range", dest="range_path", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--split", default="2026-08-24 00:00:00.000")
    args = parser.parse_args()

    range_rows = read_range_bars(args.range_path)
    range_times = [x["time"] for x in range_rows]
    candidates = scan_candidates(args.ha)
    mapped = []
    for x in candidates:
        index = bisect.bisect_left(range_times, x["time"])
        if index >= len(range_rows) or index + 12 >= len(range_rows):
            continue
        x["range_index"] = index
        x["entry_time"] = range_rows[index]["time"]
        x["delay_seconds"] = (datetime.fromisoformat(x["entry_time"]) -
                              datetime.fromisoformat(x["time"])).total_seconds()
        x["outcome"] = range_outcome(range_rows, index, x["direction"])
        mapped.append(x)

    output = []
    selected_by_name = {}
    for trend_name, pullback_name, trigger_name in itertools.product(
            TRENDS, PULLBACKS, TRIGGERS):
        chosen = [x for x in mapped if qualifies(
            x, TRENDS[trend_name], PULLBACKS[pullback_name],
            TRIGGERS[trigger_name])]
        kept, counts, dev, hold, longs, shorts = summarize(chosen, args.split)
        resolved = counts["TARGET"] + counts["STOP"]
        output.append({
            "profile": trend_name + "+" + pullback_name + "+" + trigger_name,
            "trend": trend_name, "pullback": pullback_name,
            "trigger": trigger_name, "raw_signals": len(chosen),
            "signals": len(kept),
            "days": len(set(x["day"] for x in kept)),
            "targets": counts["TARGET"], "stops": counts["STOP"],
            "none": counts["NONE"], "resolved_rate":
                round(100 * counts["TARGET"] / resolved, 2) if resolved else 0,
            "dev_n": dev[0], "dev_rate": round(dev[3], 2),
            "hold_n": hold[0], "hold_rate": round(hold[3], 2),
            "long_n": longs[0], "long_rate": round(longs[3], 2),
            "short_n": shorts[0], "short_rate": round(shorts[3], 2),
            "median_delay_seconds": round(percentile(
                [x["delay_seconds"] for x in kept], .5), 3),
            "p90_delay_seconds": round(percentile(
                [x["delay_seconds"] for x in kept], .9), 3),
        })
        selected_by_name[output[-1]["profile"]] = kept

    output.sort(key=lambda x: (-min(x["dev_rate"], x["hold_rate"]),
                               -x["signals"]))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="") as target:
        writer = csv.DictWriter(target, fieldnames=list(output[0]))
        writer.writeheader(); writer.writerows(output)

    eligible = [x for x in output if x["dev_n"] >= 30 and x["hold_n"] >= 30]
    print("base candidates", len(candidates), "mapped", len(mapped))
    print("range", range_times[0], "to", range_times[-1], "split", args.split)
    for row in eligible[:20]:
        print("TOP", row)


if __name__ == "__main__":
    main()
