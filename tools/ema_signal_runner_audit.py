#!/usr/bin/env python3
"""Raw-tick audit of a break-even plus one-range-bar trailing exit.

Entry signals are unchanged. The initial stop is ten ticks. At five ticks of
favorable excursion the trade is protected at entry; thereafter the stop is
five ticks behind the best favorable tick. Open trades are marked to the last
observed tick at the selected 12/24/48-bar horizon.
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
    end: int
    armed: bool = False
    best: float = 0.0
    last: float | None = None
    pnl: float | None = None
    reason: str = "NONE"


def summarize(states, signals, group, horizon):
    chosen = [state for state in states if state.horizon == horizon and
              (group == "ALL" or group in signals[state.signal_ident].families)]
    done = [state for state in chosen if state.pnl is not None]
    pnls = [state.pnl for state in done]
    positive = sum(value > 0 for value in pnls)
    flat = sum(abs(value) < .001 for value in pnls)
    negative = sum(value < 0 for value in pnls)
    five_plus = sum(value >= 5 for value in pnls)
    return [group, horizon, len(chosen), len(done), positive, flat, negative,
            five_plus, f"{100*positive/len(done):.4f}" if done else "0",
            f"{100*flat/len(done):.4f}" if done else "0",
            f"{100*negative/len(done):.4f}" if done else "0",
            f"{100*five_plus/len(done):.4f}" if done else "0",
            f"{sum(pnls)/len(done):.4f}" if done else "0",
            f"{statistics.median(pnls):.4f}" if done else "0"]


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
            if index + horizon >= len(rows):
                continue
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
                state.pnl = state.last
                state.reason = "TIME"

        for ident, state in list(active.items()):
            signal = signals[state.signal_ident]
            excursion = signal.direction * (price - signal.entry) / TICK
            state.last = excursion
            if not state.armed:
                if excursion <= -10:
                    state.pnl = -10
                    state.reason = "HARD_STOP"
                    active.pop(ident, None)
                    continue
                if excursion >= 5:
                    state.armed = True
                    state.best = excursion
                continue
            state.best = max(state.best, excursion)
            trailing_stop = max(0.0, state.best - 5.0)
            if excursion <= trailing_stop:
                state.pnl = trailing_stop
                state.reason = "TRAIL"
                active.pop(ident, None)

    # End-of-file liquidation for states whose chart horizon extends to or
    # beyond the final available tick.
    for state in active.values():
        if state.last is not None:
            state.pnl = state.last
            state.reason = "DATA_END"

    a.output.parent.mkdir(parents=True, exist_ok=True)
    detail = a.output.with_name(a.output.stem + "_trades.csv")
    with detail.open("w", newline="") as h:
        w = csv.writer(h)
        w.writerow(["BarNumber", "Time", "Families", "Direction", "Horizon",
                    "ArmedAtFive", "BestFavorableTicks", "ExitReason", "PnlTicks"])
        for state in states:
            signal = signals[state.signal_ident]
            w.writerow([signal.bar, signal.time_text, "+".join(signal.families),
                        signal.direction, state.horizon, state.armed,
                        f"{state.best:.4f}", state.reason,
                        "" if state.pnl is None else f"{state.pnl:.4f}"])
    with a.output.open("w", newline="") as h:
        w = csv.writer(h)
        w.writerow(["Group", "HorizonBars", "Selected", "Completed", "Positive",
                    "BreakEven", "Negative", "FivePlus", "PositiveRate",
                    "BreakEvenRate", "NegativeRate", "FivePlusRate",
                    "AveragePnlTicks", "MedianPnlTicks"])
        for group in ("ALL", "EMA8", "EMA24", "EMA50", "PIN"):
            for horizon in HORIZONS:
                w.writerow(summarize(states, signals, group, horizon))
    print("wrote", a.output, "and", detail)


if __name__ == "__main__":
    main()
