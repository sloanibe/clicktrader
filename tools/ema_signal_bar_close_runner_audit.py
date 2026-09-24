#!/usr/bin/env python3
"""Audit a break-even runner trailed from completed range-bar closes.

The ten-tick initial stop and post-activation break-even floor use raw ticks.
After +5 ticks is reached, the favorable high-water mark is updated only by
completed range-bar closes; a close five ticks below that mark exits the trade.
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
from ema_signal_stop_sweep import TICK, select
from tick_execution_audit import raw_tick_rows, second_key

HORIZONS = (12, 24, 48)


@dataclass
class State:
    ident: int
    signal_ident: int
    horizon: int
    end_bar: int
    end_time: int
    armed: bool = False
    best_close: float = 0.0
    pnl: float | None = None
    reason: str = "NONE"


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--diagnostic", required=True, type=Path)
    p.add_argument("--ticks", required=True, type=Path)
    p.add_argument("--output", required=True, type=Path)
    p.add_argument("--from", dest="start", required=True)
    p.add_argument("--to", dest="end", required=True)
    p.add_argument("--pin-clearance", type=float, default=4.0)
    a = p.parse_args()
    start, end = second_key(a.start), second_key(a.end)
    with a.diagnostic.open(newline="", encoding="utf-8-sig") as h:
        rows = list(csv.DictReader(h))
    signals = select(rows, start, end, pin_clearance=a.pin_clearance)
    positions = {int(row["BarNumber"]): i for i, row in enumerate(rows)}
    states = []
    for signal in signals:
        index = positions[signal.bar]
        for horizon in HORIZONS:
            if index + horizon < len(rows):
                states.append(State(len(states), signal.ident, horizon,
                                    int(rows[index+horizon]["BarNumber"]),
                                    second_key(rows[index+horizon]["Time"])))

    pending = sorted(states, key=lambda value: signals[value.signal_ident].time)
    pending_index = 0
    active = {}
    bar_index = 0
    count = 0

    def process_bar(row):
        bar_number = int(row["BarNumber"])
        close = float(row["Close"])
        for ident, state in list(active.items()):
            signal = signals[state.signal_ident]
            excursion = signal.direction * (close - signal.entry) / TICK
            if state.armed:
                state.best_close = max(state.best_close, excursion)
                if state.best_close - excursion >= 5:
                    state.pnl = max(0.0, excursion)
                    state.reason = "BAR_CLOSE_TRAIL"
                    active.pop(ident, None)
                    continue
            if bar_number >= state.end_bar:
                state.pnl = excursion
                state.reason = "TIME"
                active.pop(ident, None)

    for tick_time, price in raw_tick_rows(a.ticks):
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
            # Whole-second ticks cannot observe a horizon that completed in
            # the signal second (or before the next exported tick).
            if state.end_time >= tick_time:
                active[state.ident] = state
            else:
                state.reason = "NO_TICK_WINDOW"
            pending_index += 1
        for ident, state in list(active.items()):
            signal = signals[state.signal_ident]
            excursion = signal.direction * (price - signal.entry) / TICK
            if not state.armed:
                if excursion <= -10:
                    state.pnl = -10
                    state.reason = "HARD_STOP"
                    active.pop(ident, None)
                elif excursion >= 5:
                    state.armed = True
                    state.best_close = 5
                continue
            if excursion <= 0:
                state.pnl = 0
                state.reason = "BREAK_EVEN"
                active.pop(ident, None)

    for state in active.values():
        state.reason = "DATA_END"

    a.output.parent.mkdir(parents=True, exist_ok=True)
    detail = a.output.with_name(a.output.stem + "_trades.csv")
    with detail.open("w", newline="") as h:
        w = csv.writer(h)
        w.writerow(["BarNumber", "Time", "Families", "Direction", "Horizon",
                    "ArmedAtFive", "BestCloseTicks", "ExitReason", "PnlTicks"])
        for state in states:
            signal = signals[state.signal_ident]
            w.writerow([signal.bar, signal.time_text, "+".join(signal.families),
                        signal.direction, state.horizon, state.armed,
                        f"{state.best_close:.4f}", state.reason,
                        "" if state.pnl is None else f"{state.pnl:.4f}"])
    with a.output.open("w", newline="") as h:
        w = csv.writer(h)
        w.writerow(["Group", "HorizonBars", "Selected", "Completed", "Positive",
                    "BreakEven", "Negative", "FivePlus", "PositiveRate",
                    "BreakEvenRate", "NegativeRate", "FivePlusRate",
                    "AveragePnlTicks", "MedianPnlTicks"])
        for group in ("ALL", "EMA8", "EMA24", "EMA50", "PIN"):
            for horizon in HORIZONS:
                chosen = [state for state in states if state.horizon == horizon and
                          (group == "ALL" or group in signals[state.signal_ident].families)]
                done = [state for state in chosen if state.pnl is not None]
                values = [state.pnl for state in done]
                pos = sum(v > 0 for v in values); flat = sum(v == 0 for v in values)
                neg = sum(v < 0 for v in values); five = sum(v >= 5 for v in values)
                n = len(done)
                w.writerow([group, horizon, len(chosen), n, pos, flat, neg, five,
                            f"{100*pos/n:.4f}" if n else "0",
                            f"{100*flat/n:.4f}" if n else "0",
                            f"{100*neg/n:.4f}" if n else "0",
                            f"{100*five/n:.4f}" if n else "0",
                            f"{sum(values)/n:.4f}" if n else "0",
                            f"{statistics.median(values):.4f}" if n else "0"])
    print("wrote", a.output, "and", detail)


if __name__ == "__main__":
    main()
