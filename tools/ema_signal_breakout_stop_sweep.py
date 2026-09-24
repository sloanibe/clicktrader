#!/usr/bin/env python3
"""Test continuation-trigger entries for the unchanged EMA display signals.

An entry stop is placed one or two ticks beyond the completed signal-bar close
and remains valid only until the next range bar completes. Outcomes use a
five-tick target from the triggered entry and fixed 4-10 tick stops. Ask-only,
whole-second data makes short fills and bars sharing a second research proxies.
"""
from __future__ import annotations

import argparse
import csv
import heapq
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from ema_signal_stop_sweep import STOPS, TICK, select
from tick_execution_audit import raw_tick_rows, second_key

TRIGGERS = (1, 2)


@dataclass
class TriggerState:
    trigger_ticks: int
    trigger_end: int
    triggered: bool = False
    entry: float = 0.0
    outcomes: dict[int, str] = field(
        default_factory=lambda: {stop: "NONE" for stop in STOPS})


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
    states = {
        signal.ident: {
            trigger: TriggerState(
                trigger, second_key(rows[positions[signal.bar] + 1]["Time"]))
            for trigger in TRIGGERS
        }
        for signal in signals
    }

    pending = sorted(signals, key=lambda value: value.time)
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
        while pending_index < len(pending) and pending[pending_index].time < tick_time:
            signal = pending[pending_index]
            active[signal.ident] = signal
            heapq.heappush(expiry, (signal.end, signal.ident))
            pending_index += 1
        while expiry and expiry[0][0] < tick_time:
            _, ident = heapq.heappop(expiry)
            active.pop(ident, None)

        for ident, signal in list(active.items()):
            finished = True
            for trigger, state in states[ident].items():
                if not state.triggered:
                    if tick_time > state.trigger_end:
                        continue
                    trigger_price = signal.entry + signal.direction * trigger * TICK
                    hit = price >= trigger_price if signal.direction > 0 else price <= trigger_price
                    if not hit:
                        finished = False
                        continue
                    state.triggered = True
                    state.entry = trigger_price
                    finished = False
                    continue
                if all(value != "NONE" for value in state.outcomes.values()):
                    continue
                finished = False
                target = (price >= state.entry + 5*TICK if signal.direction > 0
                          else price <= state.entry - 5*TICK)
                for stop in STOPS:
                    if state.outcomes[stop] != "NONE":
                        continue
                    stopped = (price <= state.entry - stop*TICK if signal.direction > 0
                               else price >= state.entry + stop*TICK)
                    if stopped:
                        state.outcomes[stop] = "STOP"
                    elif target:
                        state.outcomes[stop] = "TARGET"
            if finished:
                active.pop(ident, None)

    a.output.parent.mkdir(parents=True, exist_ok=True)
    with a.output.open("w", newline="") as h:
        w = csv.writer(h)
        w.writerow(["Group", "TriggerTicks", "StopTicks", "Signals",
                    "Triggered", "Resolved", "Targets", "Stops", "TargetRate",
                    "ExpectancyPerTrade", "ExpectancyPerSignal"])
        for group in ("ALL", "EMA8", "EMA24", "EMA50", "PIN"):
            sample = (signals if group == "ALL" else
                      [signal for signal in signals if group in signal.families])
            for trigger in TRIGGERS:
                triggered = [signal for signal in sample
                             if states[signal.ident][trigger].triggered]
                for stop in STOPS:
                    outcomes = [states[signal.ident][trigger].outcomes[stop]
                                for signal in triggered]
                    wins, losses = outcomes.count("TARGET"), outcomes.count("STOP")
                    resolved = wins + losses
                    rate = wins / resolved if resolved else 0
                    pnl = wins*5 - losses*stop
                    w.writerow([group, trigger, stop, len(sample), len(triggered),
                                resolved, wins, losses, f"{100*rate:.4f}",
                                f"{pnl/resolved:.4f}" if resolved else "0",
                                f"{pnl/len(sample):.4f}" if sample else "0"])
    print("wrote", a.output)


if __name__ == "__main__":
    main()
