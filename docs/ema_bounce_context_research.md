# EMA bounce context recommendations and test plan

Recorded 2026-09-19. Research hypotheses; no change to chart signals or orders.

## Recommendation

Add context beyond the existing EMA ordering, separation, slope, and rejection
rules. Investigate three independently measurable questions before combining
them into a custom indicator:

1. **Room to target:** how far is the proposed entry from an established
   opposing swing level? A five-tick target may behave differently when an
   unbroken prior high is three ticks above a long entry versus nine ticks.
   Treat levels as possible obstacles, not guaranteed barriers. Eventually
   compare repeated rejection zones and session levels separately.
2. **Pullback pressure:** compare the depth and speed of the correction with
   the immediately preceding impulse. Test whether a shallow, slower correction
   behaves better than a fast adverse move. Range bars conceal elapsed time,
   so use actual timestamps as well as bar counts. Later examine diminishing
   progress on successive attempts and the speed of recovery at the EMA.
3. **Volume effort versus price response:** distinguish fading aggressive
   countertrend volume (possible exhaustion) from substantial aggressive volume
   with little price progress (possible absorption). Do not impose a generic
   high-volume or positive-delta rule. This needs executed trade size and
   reliable bid/ask classification; separate bid/ask price charts alone do not
   establish it.

A possible future display would show room to target, pullback pressure, and
rejection response for each existing signal. Test each input independently
before choosing weights or imposing a combined score.

Concept references: [CME support and resistance](https://www.cmegroup.com/education/courses/technical-analysis/support-and-resistance)
and [Sierra Chart bid/ask volume documentation](https://www.sierrachart.com/index.php?l=doc%2FNumbersBars.php).
These explain the concepts, not evidence that these filters improve this system.

## First experiment, specified before examining feature results

- Use the established expanded quality-display EMA8/EMA24/EMA50 populations
  from `tools/ema_signal_stop_sweep.py` and their existing raw ask-tick outcome
  table. Exclude pin-only signals; retain family membership and deduplicate the
  combined population. These are **not** the live strategy's earlier detectors.
- Source: MESU26 five-tick diagnostic
  `/mnt/c/rangebar_diagnostics/RangeBarDiagnostics_2026-09-13_122732.csv`;
  outcomes: `analysis_output/ema_signal_stop_sweep_outcomes.csv`.
- Preserve close-reference entry, +5 target, -10 stop, and 12-bar horizon.
  Validate outcome identities against freshly reconstructed signal membership.
- Primary operating window: existing display prime hours, 06:31–06:45,
  07:00–08:00, 11:00–13:00, Pacific/chart time. Also report all available hours.
- Keep the existing split after 2026-08-23, report directions and each EMA
  separately. Call the second segment LATER: this extensively reused month is
  not an untouched holdout.
- Define a swing using two strictly lower highs / higher lows on each side.
  Both confirmation bars must have closed **before** the signal bar. Search
  the preceding 50 bars, within the same provisional 15:00 futures session;
  discard levels already breached before the signal. Measure the nearest
  opposing level at or ahead of the entry. No detected level is a separate
  category, not proof of unlimited room.
- Test one fixed room rule: no detected obstacle within five ticks, equivalent
  to at least six ticks for tick-aligned prices. Report absent levels separately.
- Identify consecutive countertrend bars immediately before the signal and
  the consecutive trend-colored impulse immediately before them. Measure
  impulse net close advance, pullback extreme depth relative to the anchor
  close, and pullback net close speed divided by impulse net close speed.
  Exclude the signal bar from these pressure measurements. Zero duration,
  nonpositive impulse, or a session crossing is missing data, never imputed.
- Test two fixed pressure rules individually: depth <= 50% of impulse;
  pullback speed <= impulse speed. No threshold optimization in this pass.
- Report retained counts, unresolved paths, resolved target rate, total proxy
  ticks, and proxy ticks per selected signal. Missing-feature cases get their
  own counts; pressure filters are compared with their eligible baseline.
  Use day-cluster bootstrap intervals for the change in mean proxy ticks and
  repeat comparisons without the existing same-second ambiguity flags.
- Include illustrative 0.5/1/1.5 tick round-trip cost sensitivities; these are
  assumptions, not measured fees or fills. Unresolved paths score zero gross
  for compatibility with earlier research, so the result is a proxy.

## Limits and next steps

Same-second tick ambiguity, ask-only paths, overlapping signals, and reference
entries remain inherited limitations. Any favorable association must survive
fresh dates, actual projected-entry replay, bid/ask execution costs, and a
one-position-at-a-time test before it becomes a trading rule. A filter must
justify both its quality improvement and the opportunities it removes.

Volume testing is deferred until a suitable executed-trade dataset is verified.
Do not infer volume or delta from the existing OHLC/EMA diagnostic. Repeated
rejection zones, session levels, and intrabar recovery timing are follow-ups,
not claims covered by this initial swing/pressure experiment.

## First experiment results — 2026-09-19

Tool: [`tools/ema_bounce_context_scan.py`](../tools/ema_bounce_context_scan.py).
Artifacts: [`analysis_output/ema_bounce_context/`](../analysis_output/ema_bounce_context/).
`features.csv` contains each signal and its measurements; `summary.csv` contains
baseline/eligible/kept/dropped/missing populations; `comparisons.csv` contains
paired day-bootstrap intervals; `buckets.csv` contains descriptive ranges;
`manifest.json` records input hashes and experiment parameters. Bucket numbers
count lower boundaries satisfied (for room: 1 = 0–2 ticks, 2 = 3–5,
3 = 6–10, 4 = 11+); absent levels are separate.

Reproduce from the repository root:

```bash
python3 tools/ema_bounce_context_scan.py \
  --diagnostic /mnt/c/rangebar_diagnostics/RangeBarDiagnostics_2026-09-13_122732.csv \
  --outcomes analysis_output/ema_signal_stop_sweep_outcomes.csv \
  --output analysis_output/ema_bounce_context
```

All 2,183 cached signal identities, directions, entries, family memberships,
and ambiguity flags matched a fresh reconstruction using the existing selector.
This pass reused those previously computed raw-tick outcomes; it did not replay
the raw tick file again. Removing pin-only events left 1,983 unique EMA signals,
632 of them in the existing prime windows. Family populations overlap.

### Primary prime-hours comparison

Target rates exclude unresolved paths; mean and total proxy ticks include them
at zero gross. Each pressure rule is compared with its measurable population,
not with a different baseline containing missing feature values.

| Rule | Eligible → retained | Target rate before → after | Gross proxy ticks/signal before → after | 95% day-bootstrap interval for change |
|---|---:|---:|---:|---:|
| No swing obstacle within five ticks | 632 → 529 | 79.52% → 80.08% | +1.922 → +2.004 | -0.212 to +0.364 |
| Pullback depth <= half the prior impulse | 583 → 184 | 79.69% → 72.68% | +1.947 → +0.897 | -1.720 to -0.280 |
| Pullback speed <= prior impulse speed | 580 → 289 | 79.62% → 81.31% | +1.940 → +2.197 | -0.272 to +0.745 |

There were 49 signals without a valid positive, same-session impulse for the
depth ratio, and 52 without a usable speed ratio. The overall baseline had
501 targets, 129 stops, and two unresolved paths. Retained room/depth/speed
populations had two/one/zero unresolved paths respectively.

**Room:** the aggregate improvement was small and uncertain. Earlier/later
rates changed from 78.76%/80.25% to 79.76%/80.36%. It removed 103 signals
(16.3%), reducing total gross proxy ticks from 1,215 to 1,060. The excluded
signals themselves remained gross-positive. No-level cases were 205 signals
at 78.54%, so absence of a level did not identify especially strong trades.
The room filter did not improve EMA8 overall (77.14% → 77.13%), and hurt its
later segment (76.02% → 74.05%). It excluded no EMA24 signals under this narrow
swing definition. This does not test all support/resistance concepts.

**Depth:** reject this particular universal <=50% rule. It substantially
reduced prime-time quality in both chronological segments and both directions.
It retained only 10 EMA24 and nine EMA50 signals, illustrating how a short
consecutive-bar impulse is an unsuitable universal reference for deeper
corrections. EMA8 alone also deteriorated (76.85% → 71.34%), so the aggregate
decline is not solely a change in family composition. Do not invert the rule
or optimize its cutoff retrospectively based on this result.

**Speed:** keep as a follow-up hypothesis, not a selected filter. It retained
49.8% of measurable signals and increased gross proxy mean by 0.258 ticks,
but the interval includes no improvement. The earlier rate improved from
79.37% to 82.64%; the later rate barely changed, 79.86% to 80.00%. Long rates
changed from 81.00% to 84.21%, short rates from 78.14% to 78.85%. Total gross
proxy ticks fell from 1,125 to 635 because about half the opportunities were
removed; these overlapping signal totals are not portfolio returns.

| Family, speed rule | Eligible → retained | Target rate before → after | Mean proxy tick change |
|---|---:|---:|---:|
| EMA8 | 337 → 180 | 76.79% → 79.44% | +0.403 |
| EMA24 | 134 → 70 | 83.58% → 82.86% | -0.109 |
| EMA50 | 112 → 41 | 83.93% → 87.80% | +0.581 |

All three family-level speed intervals include zero. The apparent EMA50
improvement is based on only 41 retained signals and is concentrated in the
earlier segment: later rates were 82.26% → 82.61%.

### Sensitivity and interpretation

Removing same-second-ambiguous paths leaves essentially the same comparison:
room +0.082, depth -1.091, speed +0.251 proxy ticks per signal. Across all
hours, improvements are smaller: room +0.116 and speed +0.102; depth is -0.155.
Prime-time speed therefore has no demonstrated broad, stable effect yet.

At an illustrative one-tick round-trip cost, eligible/retained prime-time
means are room +0.922/+1.004, depth +0.947/-0.103, and speed +0.940/+1.197.
These merely subtract a fixed cost from the proxy: they do not establish
executable profits. Bootstrap intervals use 2,000 paired chart-date resamples,
seed 190926; they are descriptive, not adjusted for the many historical
experiments already performed on this month.

Feature checks passed for a hand-calculated impulse/pullback, independence
from appended future bars, mirrored long/short behavior, zero-duration
handling, delayed swing confirmation, and retirement of breached levels.
Features use no future bars, but they are measured at the completed signal
close; transferring them to a forming-bar stop entry requires separate replay.

**Decision:** do not change the live strategy or display. Preserve speed as
the leading next hypothesis, especially for EMA8, with the current fixed
definition and threshold. Seek fresh dates before tuning or combining it.
Session-level/repeated-zone resistance and true intrabar rejection response
remain untested. Volume confirmation requires additional suitable data.
