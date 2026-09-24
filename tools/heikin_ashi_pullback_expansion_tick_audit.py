#!/usr/bin/env python3
"""Ask-tick audit of selected HA pullback/expansion profiles."""
import argparse
import bisect
import csv
import heapq
import sys
from collections import Counter
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import heikin_ashi_pullback_expansion_scan as scan
from tick_execution_audit import raw_tick_rows


PROFILES = {
    "ordered_doji_expand": ("T1_ordered_rising", "P3_doji", "E1_expand"),
    "very_strong_compact_expand": ("T4_very_strong", "P2_compact", "E1_expand"),
    "very_strong_doji_expand": ("T4_very_strong", "P3_doji", "E1_expand"),
    "very_strong_doji_clear": ("T4_very_strong", "P3_doji", "E3_strong_clear"),
}


def second_key(value):
    day = date(int(value[:4]), int(value[5:7]), int(value[8:10]))
    return (day.toordinal() * 86400 + int(value[11:13]) * 3600 +
            int(value[14:16]) * 60 + int(value[17:19]))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--ha", type=Path, required=True)
    parser.add_argument("--range", dest="range_path", type=Path, required=True)
    parser.add_argument("--ticks", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--split", default="2026-08-24 00:00:00.000")
    args = parser.parse_args()

    range_rows = scan.read_range_bars(args.range_path)
    range_times = [x["time"] for x in range_rows]
    base = scan.scan_candidates(args.ha)
    selected = []
    for profile, (trend, pullback, trigger) in PROFILES.items():
        possible = [x for x in base if scan.qualifies(
            x, scan.TRENDS[trend], scan.PULLBACKS[pullback],
            scan.TRIGGERS[trigger])]
        last_index = -1000
        for x in possible:
            index = bisect.bisect_left(range_times, x["time"])
            if (index >= len(range_rows) or index + 12 >= len(range_rows) or
                    index <= last_index + 12):
                continue
            last_index = index
            selected.append({
                "id": len(selected), "profile": profile, "time": x["time"],
                "signal_key": second_key(x["time"]),
                "end_key": second_key(range_rows[index + 12]["time"]),
                "direction": x["direction"], "entry": None,
                "entry_key": None, "result": "NONE", "result_key": None,
                "result_price": None, "ticks_seen": 0,
                "period": "development" if x["time"] < args.split else "holdout",
            })
    pending = sorted(selected, key=lambda x: x["signal_key"])
    cursor = 0
    active = {}
    expiry = []
    tick_count = 0
    for tick_key, price in raw_tick_rows(args.ticks):
        tick_count += 1
        if tick_count % 10_000_000 == 0:
            print("processed", tick_count // 1_000_000, "million ticks; active",
                  len(active), flush=True)
        while cursor < len(pending) and pending[cursor]["signal_key"] < tick_key:
            item = pending[cursor]
            active[item["id"]] = item
            heapq.heappush(expiry, (item["end_key"], item["id"]))
            cursor += 1
        while expiry and expiry[0][0] < tick_key:
            _, item_id = heapq.heappop(expiry)
            active.pop(item_id, None)
        for item_id, item in list(active.items()):
            item["ticks_seen"] += 1
            if item["entry"] is None:
                item["entry"] = price
                item["entry_key"] = tick_key
                continue
            direction = item["direction"]
            target = item["entry"] + direction * 5 * scan.TICK
            stop = item["entry"] - direction * 10 * scan.TICK
            hit_target = price >= target if direction > 0 else price <= target
            hit_stop = price <= stop if direction > 0 else price >= stop
            if not hit_target and not hit_stop:
                continue
            item["result"] = "STOP" if hit_stop else "TARGET"
            item["result_key"] = tick_key
            item["result_price"] = price
            active.pop(item_id, None)
        if cursor >= len(pending) and not active:
            break

    args.output.parent.mkdir(parents=True, exist_ok=True)
    fields = ["profile", "time", "direction", "period", "entry", "entry_key",
              "end_key", "result", "result_key", "result_price", "ticks_seen"]
    with args.output.open("w", newline="") as target:
        writer = csv.DictWriter(target, fieldnames=fields, extrasaction="ignore")
        writer.writeheader(); writer.writerows(selected)
    for profile in PROFILES:
        items = [x for x in selected if x["profile"] == profile]
        counts = Counter(x["result"] for x in items)
        print(profile, len(items), dict(counts))
        for group in ("development", "holdout"):
            part = [x for x in items if x["period"] == group]
            resolved = sum(x["result"] in ("TARGET", "STOP") for x in part)
            wins = sum(x["result"] == "TARGET" for x in part)
            print(" ", group, len(part),
                  round(100 * wins / resolved, 2) if resolved else 0)


if __name__ == "__main__":
    main()
