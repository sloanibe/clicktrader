#!/usr/bin/env python3
"""Sweep larger fixed targets with a +5-tick break-even activation rule."""
from __future__ import annotations

import argparse
import csv
import heapq
import statistics
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from ema_signal_stop_sweep import TICK, select
from tick_execution_audit import raw_tick_rows, second_key

TARGETS = (5, 10, 15, 20, 25, 30)
HORIZONS = (12, 24, 48)


@dataclass
class State:
    ident: int
    signal_ident: int
    horizon: int
    end: int
    armed: bool = False
    last: float | None = None
    pnl: dict[int, float | None] = field(
        default_factory=lambda: {target: None for target in TARGETS})
    reason: dict[int, str] = field(
        default_factory=lambda: {target: "NONE" for target in TARGETS})


def finish_remaining(state, pnl, reason):
    for target in TARGETS:
        if state.pnl[target] is None:
            state.pnl[target] = pnl
            state.reason[target] = reason


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--diagnostic", required=True, type=Path)
    p.add_argument("--ticks", required=True, type=Path)
    p.add_argument("--output", required=True, type=Path)
    p.add_argument("--from", dest="start", required=True)
    p.add_argument("--to", dest="end", required=True)
    a = p.parse_args()
    start, end = second_key(a.start), second_key(a.end)
    with a.diagnostic.open(newline="", encoding="utf-8-sig") as h:
        rows = list(csv.DictReader(h))
    signals = select(rows, start, end)
    positions = {int(row["BarNumber"]): i for i, row in enumerate(rows)}
    states = []
    for signal in signals:
        index = positions[signal.bar]
        for horizon in HORIZONS:
            if index + horizon < len(rows):
                states.append(State(len(states), signal.ident, horizon,
                                    second_key(rows[index+horizon]["Time"])))

    pending = sorted(states, key=lambda value: signals[value.signal_ident].time)
    pending_index = 0
    active = {}
    expiry = []
    count = 0
    for tick_time, price in raw_tick_rows(a.ticks):
        count += 1
        if count % 10_000_000 == 0:
            print("processed", count // 1_000_000, "million ticks; active",
                  len(active), flush=True)
        if tick_time < start:
            continue
        if tick_time > end:
            break
        while (pending_index < len(pending) and
               signals[pending[pending_index].signal_ident].time < tick_time):
            state = pending[pending_index]
            active[state.ident] = state
            heapq.heappush(expiry, (state.end, state.ident))
            pending_index += 1
        while expiry and expiry[0][0] < tick_time:
            _, ident = heapq.heappop(expiry)
            state = active.pop(ident, None)
            if state is not None and state.last is not None:
                finish_remaining(state, state.last, "TIME")
        for ident, state in list(active.items()):
            signal = signals[state.signal_ident]
            excursion = signal.direction * (price - signal.entry) / TICK
            state.last = excursion
            if not state.armed:
                if excursion <= -10:
                    finish_remaining(state, -10, "HARD_STOP")
                    active.pop(ident, None)
                    continue
                if excursion >= 5:
                    state.armed = True
            if state.armed:
                for target in TARGETS:
                    if state.pnl[target] is None and excursion >= target:
                        state.pnl[target] = target
                        state.reason[target] = "TARGET"
                if excursion <= 0:
                    finish_remaining(state, 0, "BREAK_EVEN")
                    active.pop(ident, None)
                    continue
                if all(state.pnl[target] is not None for target in TARGETS):
                    active.pop(ident, None)

    for state in active.values():
        if state.last is not None:
            finish_remaining(state, state.last, "DATA_END")

    a.output.parent.mkdir(parents=True, exist_ok=True)
    detail = a.output.with_name(a.output.stem + "_trades.csv")
    with detail.open("w", newline="") as h:
        w = csv.writer(h)
        w.writerow(["BarNumber", "Time", "Families", "Direction", "Horizon",
                    "TargetTicks", "ExitReason", "PnlTicks"])
        for state in states:
            signal = signals[state.signal_ident]
            for target in TARGETS:
                w.writerow([signal.bar, signal.time_text, "+".join(signal.families),
                            signal.direction, state.horizon, target,
                            state.reason[target], "" if state.pnl[target] is None
                            else f"{state.pnl[target]:.4f}"])
    with a.output.open("w", newline="") as h:
        w = csv.writer(h)
        w.writerow(["Group", "HorizonBars", "TargetTicks", "Selected", "Completed",
                    "Targets", "BreakEven", "Losses", "TargetRate", "AveragePnlTicks",
                    "MedianPnlTicks"])
        for group in ("ALL", "EMA8", "EMA24", "EMA50", "PIN"):
            for horizon in HORIZONS:
                chosen = [state for state in states if state.horizon == horizon and
                          (group == "ALL" or group in signals[state.signal_ident].families)]
                for target in TARGETS:
                    done = [state for state in chosen if state.pnl[target] is not None]
                    values = [state.pnl[target] for state in done]
                    targets = sum(state.reason[target] == "TARGET" for state in done)
                    flat = sum(value == 0 for value in values)
                    losses = sum(value < 0 for value in values)
                    w.writerow([group, horizon, target, len(chosen), len(done), targets,
                                flat, losses, f"{100*targets/len(done):.4f}" if done else "0",
                                f"{sum(values)/len(done):.4f}" if done else "0",
                                f"{statistics.median(values):.4f}" if done else "0"])
    print("wrote", a.output, "and", detail)


if __name__ == "__main__":
    main()
