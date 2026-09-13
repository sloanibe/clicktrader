#!/usr/bin/env python3
"""Audit diagnostic EMA signals against chronological ask-tick prices.

This is a research tool, not a broker-fill simulator. It uses the diagnostic
bar close as a reference entry and starts looking at the *next* tick second,
because the tick file is second-granular while MultiCharts bar timestamps carry
milliseconds. Long and short outcomes are both price-path proxies when only
ask data is available; short fills, spread, slippage, and commissions must be
modeled separately before treating results as tradable performance.
"""

from __future__ import annotations

import argparse
import csv
import heapq
from collections import Counter
from dataclasses import dataclass
from datetime import date, datetime
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Tuple


@dataclass
class DiagnosticRow:
    bar_number: int
    time_key: int
    time_text: str
    direction: int
    entry_price: float
    horizon_bars: int
    expected: str


@dataclass
class Candidate:
    candidate_id: int
    row: DiagnosticRow
    end_key: int
    same_second_future_bar: bool
    result: str = "NONE"
    trigger_key: Optional[int] = None
    trigger_price: Optional[float] = None
    ticks_seen: int = 0


def second_key(timestamp: str) -> int:
    """Convert YYYY-MM-DD HH:MM:SS[.fff] to an ordinal second key."""
    day = date(int(timestamp[0:4]), int(timestamp[5:7]), int(timestamp[8:10]))
    hour = int(timestamp[11:13])
    minute = int(timestamp[14:16])
    second = int(timestamp[17:19])
    return day.toordinal() * 86400 + hour * 3600 + minute * 60 + second


def key_to_text(value: int) -> str:
    ordinal, seconds = divmod(value, 86400)
    day = date.fromordinal(ordinal)
    return "%04d-%02d-%02d %02d:%02d:%02d" % (
        day.year, day.month, day.day, seconds // 3600,
        (seconds % 3600) // 60, seconds % 60,
    )


def read_diagnostic(path: Path, signal_column: str, start_key: int,
                    end_key: int) -> List[Candidate]:
    rows: List[DiagnosticRow] = []
    with path.open("r", newline="", encoding="utf-8-sig") as handle:
        reader = csv.DictReader(handle)
        required = {
            "BarNumber", "Time", "OutcomeDirection", "OutcomeEntryPrice",
            "OutcomeHorizonBars", "OutcomeComplete", "TargetStopResult",
            signal_column,
        }
        missing = required.difference(reader.fieldnames or [])
        if missing:
            raise ValueError("Diagnostic CSV is missing: " + ", ".join(sorted(missing)))
        for record in reader:
            if record[signal_column] != "True" or record["OutcomeComplete"] != "True":
                continue
            row_key = second_key(record["Time"])
            if row_key < start_key or row_key > end_key:
                continue
            rows.append(DiagnosticRow(
                bar_number=int(record["BarNumber"]),
                time_key=row_key,
                time_text=record["Time"],
                direction=int(record["OutcomeDirection"]),
                entry_price=float(record["OutcomeEntryPrice"]),
                horizon_bars=int(record["OutcomeHorizonBars"]),
                expected=record["TargetStopResult"].strip(),
            ))

    # The horizon is expressed in diagnostic bars. Re-read the complete series
    # to map each selected row to the timestamp of its horizon-ending bar.
    all_rows: List[Tuple[int, str, int]] = []
    with path.open("r", newline="", encoding="utf-8-sig") as handle:
        reader = csv.DictReader(handle)
        for record in reader:
            all_rows.append((second_key(record["Time"]), record["Time"],
                             int(record["BarNumber"])))

    # Range bars may share an exchange timestamp, including milliseconds. Bar
    # number is the diagnostic's unique identity and must be used for joining.
    candidates: List[Candidate] = []
    selected = {row.bar_number: row for row in rows}
    for index, value in enumerate(all_rows):
        row = selected.get(value[2])
        if row is None:
            continue
        # The exported rows already guarantee a complete horizon; this guard
        # keeps the tool safe when called on a hand-edited diagnostic CSV.
        if index + row.horizon_bars >= len(all_rows):
            continue
        same_second_future_bar = any(
            all_rows[index + step][0] == row.time_key
            for step in range(1, row.horizon_bars + 1)
        )
        candidates.append(Candidate(len(candidates), row,
                                    all_rows[index + row.horizon_bars][0],
                                    same_second_future_bar))
    return candidates


def raw_tick_rows(path: Path) -> Iterable[Tuple[int, float]]:
    """Yield chronological (second_key, ask_price) records without pandas."""
    date_cache: Dict[bytes, int] = {}
    with path.open("rb") as handle:
        next(handle)  # header
        for line in handle:
            fields = line.rstrip(b"\r\n").split(b",", 3)
            if len(fields) != 4:
                continue
            day_key = date_cache.get(fields[0])
            if day_key is None:
                month, day_value, year = (int(value) for value in fields[0].split(b"/"))
                day_key = date(year, month, day_value).toordinal() * 86400
                date_cache[fields[0]] = day_key
            time_value = fields[1]
            tick_key = day_key + int(time_value[0:2]) * 3600 + \
                int(time_value[3:5]) * 60 + int(time_value[6:8])
            yield tick_key, float(fields[2])


def audit(candidates: List[Candidate], tick_path: Path, start_key: int,
          end_key: int, target_ticks: float, stop_ticks: float,
          tick_size: float) -> None:
    pending = sorted(candidates, key=lambda value: value.row.time_key)
    pending_index = 0
    active: Dict[int, Candidate] = {}
    expiry_heap: List[Tuple[int, int]] = []
    tick_count = 0

    for tick_key, price in raw_tick_rows(tick_path):
        tick_count += 1
        if tick_count % 10000000 == 0:
            print("processed %d million ticks; active candidates: %d" %
                  (tick_count // 1000000, len(active)), flush=True)
        if tick_key < start_key:
            continue
        if tick_key > end_key:
            break

        while pending_index < len(pending) and \
                pending[pending_index].row.time_key < tick_key:
            candidate = pending[pending_index]
            active[candidate.candidate_id] = candidate
            heapq.heappush(expiry_heap, (candidate.end_key, candidate.candidate_id))
            pending_index += 1

        while expiry_heap and expiry_heap[0][0] < tick_key:
            _, candidate_id = heapq.heappop(expiry_heap)
            active.pop(candidate_id, None)

        for candidate_id, candidate in list(active.items()):
            candidate.ticks_seen += 1
            if candidate.row.direction > 0:
                hit_target = price >= candidate.row.entry_price + target_ticks * tick_size
                hit_stop = price <= candidate.row.entry_price - stop_ticks * tick_size
            else:
                hit_target = price <= candidate.row.entry_price - target_ticks * tick_size
                hit_stop = price >= candidate.row.entry_price + stop_ticks * tick_size
            if not hit_target and not hit_stop:
                continue
            candidate.result = "TARGET" if hit_target else "STOP"
            candidate.trigger_key = tick_key
            candidate.trigger_price = price
            active.pop(candidate_id, None)

    for candidate in pending[pending_index:]:
        candidate.result = "NO_TICK_WINDOW"
    for candidate in active.values():
        if candidate.ticks_seen == 0:
            candidate.result = "NO_TICK_WINDOW"


def write_results(path: Path, candidates: List[Candidate]) -> Counter:
    path.parent.mkdir(parents=True, exist_ok=True)
    matrix: Counter = Counter()
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow([
            "BarNumber", "SignalTime", "Direction", "ReferenceEntryPrice",
            "DiagnosticOutcome", "StrictNextSecondTickOutcome",
            "TickOutcomeTime", "TickOutcomePrice", "TicksSeen",
            "SecondPrecisionAmbiguous",
        ])
        for candidate in candidates:
            actual = candidate.result
            matrix[(candidate.row.expected, actual)] += 1
            writer.writerow([
                candidate.row.bar_number,
                candidate.row.time_text,
                "LONG" if candidate.row.direction > 0 else "SHORT",
                "%.6f" % candidate.row.entry_price,
                candidate.row.expected,
                actual,
                "" if candidate.trigger_key is None else key_to_text(candidate.trigger_key),
                "" if candidate.trigger_price is None else "%.6f" % candidate.trigger_price,
                candidate.ticks_seen,
                candidate.same_second_future_bar or
                    (candidate.trigger_key is not None and
                     candidate.trigger_key == candidate.end_key),
            ])
    return matrix


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--diagnostic", required=True, type=Path)
    parser.add_argument("--ticks", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--from", dest="start", required=True,
                        help="YYYY-MM-DD HH:MM:SS; inclusive tick window")
    parser.add_argument("--to", dest="end", required=True,
                        help="YYYY-MM-DD HH:MM:SS; inclusive tick window")
    parser.add_argument("--signal-column", default="Ema8Bounce")
    parser.add_argument("--target-ticks", type=float, default=5.0)
    parser.add_argument("--stop-ticks", type=float, default=10.0)
    parser.add_argument("--tick-size", type=float, default=0.25)
    args = parser.parse_args()

    start_key = second_key(args.start)
    end_key = second_key(args.end)
    candidates = read_diagnostic(args.diagnostic, args.signal_column, start_key, end_key)
    print("loaded %d complete %s candidates" % (len(candidates), args.signal_column), flush=True)
    audit(candidates, args.ticks, start_key, end_key, args.target_ticks,
          args.stop_ticks, args.tick_size)
    matrix = write_results(args.output, candidates)
    print("wrote %s" % args.output)
    print("diagnostic_outcome,tick_outcome,count")
    for (expected, actual), count in sorted(matrix.items()):
        print("%s,%s,%d" % (expected, actual, count))


if __name__ == "__main__":
    main()
