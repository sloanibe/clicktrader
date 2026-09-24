#!/usr/bin/env python3
"""Raw ask-tick replay for the prime-time 50-EMA filter ablation sets."""
import argparse
import csv
import sys
from collections import Counter, defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from tick_execution_audit import Candidate, DiagnosticRow, audit, second_key


VERSIONS = ("CURRENT", "NO_50_ORDER", "NO_50_ALL")
FAMILIES = {"EMA8", "EMA24", "PIN_STRICT", "PIN7"}
SPLIT = "2026-08-23 23:59:59"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--diagnostic", type=Path, required=True)
    parser.add_argument("--candidates", type=Path, required=True)
    parser.add_argument("--ticks", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--from", dest="start", required=True)
    parser.add_argument("--to", dest="end", required=True)
    args = parser.parse_args()

    selected = defaultdict(lambda: defaultdict(set))
    metadata = {}
    with args.candidates.open(newline="") as source:
        for row in csv.DictReader(source):
            if row["family"] not in FAMILIES:
                continue
            key = (int(row["bar"]), int(row["direction"]))
            selected[row["version"]][key].add(row["family"])
            metadata[key] = row

    with args.diagnostic.open(newline="", encoding="utf-8-sig") as source:
        rows = list(csv.DictReader(source))
    positions = {int(row["BarNumber"]): index for index, row in enumerate(rows)}
    candidates = []
    labels = {}
    for version in VERSIONS:
        for (bar, side), families in selected[version].items():
            index = positions[bar]
            row = rows[index]
            diagnostic = DiagnosticRow(
                bar, second_key(row["Time"]), row["Time"], side,
                float(row["Close"]), 12, metadata[(bar, side)]["outcome"])
            same_second = any(
                second_key(rows[index + step]["Time"]) == diagnostic.time_key
                for step in range(1, 13))
            candidate = Candidate(
                len(candidates), diagnostic,
                second_key(rows[index + 12]["Time"]), same_second)
            candidates.append(candidate)
            labels[candidate.candidate_id] = (version, "+".join(sorted(families)))

    start, end = second_key(args.start), second_key(args.end)
    print("loaded", len(candidates), "versioned candidates", flush=True)
    audit(candidates, args.ticks, start, end, 5, 10, .25)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    detail = args.output.with_name(args.output.stem + "_trades.csv")
    with detail.open("w", newline="") as target:
        writer = csv.writer(target)
        writer.writerow(["Version", "Families", "BarNumber", "Time", "Direction",
                         "Segment", "TickOutcome", "TicksSeen"])
        for candidate in candidates:
            version, families = labels[candidate.candidate_id]
            writer.writerow([
                version, families, candidate.row.bar_number,
                candidate.row.time_text, candidate.row.direction,
                "DEV" if candidate.row.time_text <= SPLIT else "HOLD",
                candidate.result, candidate.ticks_seen])

    with args.output.open("w", newline="") as target:
        writer = csv.writer(target)
        writer.writerow(["Version", "Segment", "Selected", "Resolved", "Target",
                         "Stop", "None", "TargetRate", "TicksPerSelected"])
        for version in VERSIONS:
            base = [candidate for candidate in candidates
                    if labels[candidate.candidate_id][0] == version]
            for segment in ("ALL", "DEV", "HOLD"):
                chosen = base if segment == "ALL" else [
                    candidate for candidate in base
                    if (candidate.row.time_text <= SPLIT) == (segment == "DEV")]
                counts = Counter(candidate.result for candidate in chosen)
                resolved = counts["TARGET"] + counts["STOP"]
                rate = 100 * counts["TARGET"] / resolved if resolved else 0
                expectancy = ((5 * counts["TARGET"] - 10 * counts["STOP"]) /
                              len(chosen) if chosen else 0)
                writer.writerow([
                    version, segment, len(chosen), resolved, counts["TARGET"],
                    counts["STOP"], counts["NONE"], f"{rate:.4f}",
                    f"{expectancy:.4f}"])
                print(version, segment, len(chosen), resolved,
                      f"{rate:.2f}%", f"{expectancy:+.3f} ticks")


if __name__ == "__main__":
    main()
