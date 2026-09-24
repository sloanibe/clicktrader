#!/usr/bin/env python3
"""Compare completed-bar runners with and without a +5 break-even floor.

Uses the current prime-time EMA display population, including strict momentum
pins and both seven-bar-clear pin distance tiers. Both modes retain the ten-
tick hard stop and arm the completed-range-bar-close trail after +5 ticks.
"""
from __future__ import annotations

import argparse
import csv
import heapq
import statistics
import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from ema_signal_stop_sweep import Signal, TICK, f, select
from tick_execution_audit import raw_tick_rows, second_key


HORIZONS = (12, 24, 48)
MODES = ("BREAK_EVEN", "NO_BREAK_EVEN")


def prime_time(timestamp):
    hour, minute, second = (int(timestamp[11:13]), int(timestamp[14:16]),
                            int(timestamp[17:19]))
    value = hour * 3600 + minute * 60 + second
    return ((6 * 3600 + 31 * 60) <= value < (6 * 3600 + 45 * 60) or
            7 * 3600 <= value < 8 * 3600 or
            11 * 3600 <= value < 13 * 3600)


def current_signals(rows, left_candidates, start, end):
    signals = [signal for signal in select(rows, start, end)
               if prime_time(signal.time_text)]
    by_bar = {signal.bar: signal for signal in signals}
    positions = {int(row["BarNumber"]): index for index, row in enumerate(rows)}

    with left_candidates.open(newline="") as source:
        for candidate in csv.DictReader(source):
            if (candidate["prime"] != "True" or
                    int(candidate["body_depth"]) < 7 or
                    not 0 <= float(candidate["bar_distance8"]) < 4):
                continue
            bar = int(candidate["bar"])
            if bar in by_bar:
                signal = by_bar[bar]
                if "PIN7" not in signal.families:
                    signal.families = signal.families + ("PIN7",)
                continue
            index = positions[bar]
            row = rows[index]
            when = second_key(row["Time"])
            if not start <= when <= end or index + 12 >= len(rows):
                continue
            side = int(candidate["direction"])
            entry = f(row, "Close")
            adverse = ((entry - f(row, "Low")) / TICK if side > 0
                       else (f(row, "High") - entry) / TICK)
            signal = Signal(
                0, bar, when, row["Time"], second_key(rows[index + 12]["Time"]),
                side, entry, ("PIN7",), int(round(adverse)) + 1,
                any(second_key(rows[index + step]["Time"]) == when
                    for step in range(1, 13)))
            signals.append(signal)
            by_bar[bar] = signal

    signals.sort(key=lambda signal: signal.time)
    for ident, signal in enumerate(signals):
        signal.ident = ident
    return signals


@dataclass
class State:
    ident: int
    signal_ident: int
    horizon: int
    mode: str
    end_bar: int
    end_time: int
    armed: bool = False
    best_close: float = 0.0
    pnl: float | None = None
    reason: str = "NONE"


def group_for(signal):
    if "PIN7" in signal.families:
        return "PIN7"
    if "PIN" in signal.families:
        return "PIN_STRICT"
    return "EMA_BOUNCE"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--diagnostic", type=Path, required=True)
    parser.add_argument("--left-candidates", type=Path, required=True)
    parser.add_argument("--ticks", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--from", dest="start", required=True)
    parser.add_argument("--to", dest="end", required=True)
    args = parser.parse_args()
    start, end = second_key(args.start), second_key(args.end)
    with args.diagnostic.open(newline="", encoding="utf-8-sig") as source:
        rows = list(csv.DictReader(source))
    positions = {int(row["BarNumber"]): index for index, row in enumerate(rows)}
    signals = current_signals(rows, args.left_candidates, start, end)

    states = []
    for signal in signals:
        index = positions[signal.bar]
        for horizon in HORIZONS:
            if index + horizon >= len(rows):
                continue
            for mode in MODES:
                states.append(State(
                    len(states), signal.ident, horizon, mode,
                    int(rows[index + horizon]["BarNumber"]),
                    second_key(rows[index + horizon]["Time"])))

    pending = sorted(states, key=lambda state: signals[state.signal_ident].time)
    pending_index = 0
    active = {}
    bar_index = 0

    def process_bar(row):
        bar = int(row["BarNumber"])
        close = f(row, "Close")
        for ident, state in list(active.items()):
            signal = signals[state.signal_ident]
            excursion = signal.direction * (close - signal.entry) / TICK
            if state.armed:
                state.best_close = max(state.best_close, excursion)
                if state.best_close - excursion >= 5:
                    state.pnl = (max(0.0, excursion)
                                 if state.mode == "BREAK_EVEN" else excursion)
                    state.reason = "BAR_CLOSE_TRAIL"
                    active.pop(ident, None)
                    continue
            if bar >= state.end_bar:
                state.pnl = excursion
                state.reason = "TIME"
                active.pop(ident, None)

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
        while bar_index < len(rows) and second_key(rows[bar_index]["Time"]) < tick_time:
            process_bar(rows[bar_index])
            bar_index += 1
        while (pending_index < len(pending) and
               signals[pending[pending_index].signal_ident].time < tick_time):
            state = pending[pending_index]
            if state.end_time >= tick_time:
                active[state.ident] = state
            else:
                state.reason = "NO_TICK_WINDOW"
            pending_index += 1
        for ident, state in list(active.items()):
            signal = signals[state.signal_ident]
            excursion = signal.direction * (price - signal.entry) / TICK
            if excursion <= -10:
                state.pnl = -10
                state.reason = "HARD_STOP"
                active.pop(ident, None)
                continue
            if not state.armed and excursion >= 5:
                state.armed = True
                state.best_close = 5
            if (state.armed and state.mode == "BREAK_EVEN" and
                    excursion <= 0):
                state.pnl = 0
                state.reason = "BREAK_EVEN"
                active.pop(ident, None)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    detail = args.output.with_name(args.output.stem + "_trades.csv")
    with detail.open("w", newline="") as target:
        writer = csv.writer(target)
        writer.writerow(["BarNumber", "Time", "Group", "Families", "Direction",
                         "Horizon", "Mode", "Armed", "ExitReason", "PnlTicks"])
        for state in states:
            signal = signals[state.signal_ident]
            writer.writerow([
                signal.bar, signal.time_text, group_for(signal),
                "+".join(signal.families), signal.direction, state.horizon,
                state.mode, state.armed, state.reason,
                "" if state.pnl is None else f"{state.pnl:.4f}"])

    with args.output.open("w", newline="") as target:
        writer = csv.writer(target)
        writer.writerow(["Group", "Horizon", "Mode", "Selected", "Completed",
                         "Positive", "BreakEven", "Negative", "AveragePnlTicks",
                         "MedianPnlTicks"])
        for group in ("ALL", "EMA_BOUNCE", "PIN_STRICT", "PIN7"):
            for horizon in HORIZONS:
                for mode in MODES:
                    chosen = [state for state in states
                              if state.horizon == horizon and state.mode == mode and
                              (group == "ALL" or
                               group_for(signals[state.signal_ident]) == group)]
                    done = [state for state in chosen if state.pnl is not None]
                    values = [state.pnl for state in done]
                    writer.writerow([
                        group, horizon, mode, len(chosen), len(done),
                        sum(value > 0 for value in values),
                        sum(value == 0 for value in values),
                        sum(value < 0 for value in values),
                        f"{sum(values)/len(values):.4f}" if values else "",
                        f"{statistics.median(values):.4f}" if values else ""])
    print("selected current prime-time signals", len(signals))
    print("wrote", args.output, "and", detail)


if __name__ == "__main__":
    main()
