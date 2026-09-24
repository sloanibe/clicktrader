#!/usr/bin/env python3
"""Raw-tick replay of normalized range-size signal candidate files."""
import argparse
import csv
import heapq
import sys
from collections import Counter
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from tick_execution_audit import raw_tick_rows, second_key


@dataclass
class State:
    ident: int
    config: str
    bar: int
    time: int
    time_text: str
    end: int
    direction: int
    entry: float
    target: int
    stop: int
    families: str
    segment: str
    result: str = "NONE"
    ticks_seen: int = 0


def load(config, candidates, diagnostic, bar_size, states):
    with diagnostic.open(newline="", encoding="utf-8-sig") as source:
        rows = list(csv.DictReader(source))
    positions = {int(row["BarNumber"]): index for index, row in enumerate(rows)}
    with candidates.open(newline="") as source:
        for item in csv.DictReader(source):
            index = positions[int(item["bar"])]
            states.append(State(
                len(states), config, int(item["bar"]), second_key(item["time"]),
                item["time"], second_key(rows[index + 12]["Time"]),
                int(item["direction"]), float(item["entry"]), bar_size,
                2 * bar_size, item["families"], item["segment"]))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--four-diagnostic", type=Path, required=True)
    parser.add_argument("--five-diagnostic", type=Path, required=True)
    parser.add_argument("--four-strict", type=Path, required=True)
    parser.add_argument("--four-broad", type=Path, required=True)
    parser.add_argument("--five", type=Path, required=True)
    parser.add_argument("--ticks", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--from", dest="start", required=True)
    parser.add_argument("--to", dest="end", required=True)
    args = parser.parse_args()

    states = []
    load("R4_TAIL3_4", args.four_strict, args.four_diagnostic, 4, states)
    load("R4_TAIL2_4", args.four_broad, args.four_diagnostic, 4, states)
    load("R5_CURRENT", args.five, args.five_diagnostic, 5, states)
    pending = sorted(states, key=lambda state: state.time)
    pending_index, active, expiry = 0, {}, []
    start, end = second_key(args.start), second_key(args.end)

    count = 0
    for tick_time, price in raw_tick_rows(args.ticks):
        count += 1
        if count % 10_000_000 == 0:
            print("processed", count // 1_000_000, "million ticks; active",
                  len(active), flush=True)
        if tick_time < start:
            continue
        if tick_time > end:
            break
        while pending_index < len(pending) and pending[pending_index].time < tick_time:
            state = pending[pending_index]
            active[state.ident] = state
            heapq.heappush(expiry, (state.end, state.ident))
            pending_index += 1
        while expiry and expiry[0][0] < tick_time:
            _, ident = heapq.heappop(expiry)
            active.pop(ident, None)
        for ident, state in list(active.items()):
            state.ticks_seen += 1
            excursion = state.direction * (price - state.entry) / .25
            if excursion <= -state.stop:
                state.result = "STOP"
                active.pop(ident, None)
            elif excursion >= state.target:
                state.result = "TARGET"
                active.pop(ident, None)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    detail = args.output.with_name(args.output.stem + "_trades.csv")
    with detail.open("w", newline="") as target:
        writer = csv.writer(target)
        writer.writerow(["Configuration", "BarNumber", "Time", "Direction",
                         "Families", "Segment", "Outcome", "TicksSeen"])
        for state in states:
            writer.writerow([state.config, state.bar, state.time_text,
                             state.direction, state.families, state.segment,
                             state.result, state.ticks_seen])
    with args.output.open("w", newline="") as target:
        writer = csv.writer(target)
        writer.writerow(["Configuration", "Segment", "Selected", "Resolved",
                         "Target", "Stop", "None", "TargetRate",
                         "TicksPerSelected"])
        for config in ("R4_TAIL3_4", "R4_TAIL2_4", "R5_CURRENT"):
            base = [state for state in states if state.config == config]
            for segment in ("ALL", "DEV", "HOLD"):
                chosen = base if segment == "ALL" else [state for state in base
                                                        if state.segment == segment]
                counts = Counter(state.result for state in chosen)
                resolved = counts["TARGET"] + counts["STOP"]
                rate = 100 * counts["TARGET"] / resolved if resolved else 0
                bar_size = chosen[0].target if chosen else 0
                pnl = ((bar_size * counts["TARGET"] - 2 * bar_size * counts["STOP"]) /
                       len(chosen) if chosen else 0)
                writer.writerow([config, segment, len(chosen), resolved,
                                 counts["TARGET"], counts["STOP"], counts["NONE"],
                                 f"{rate:.4f}", f"{pnl:.4f}"])
                print(config, segment, len(chosen), resolved,
                      f"{rate:.2f}%", f"{pnl:+.3f} ticks")


if __name__ == "__main__":
    main()
