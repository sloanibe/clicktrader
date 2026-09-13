# EMA Pullback Analysis Log

This is a running research log. Preliminary results are observations, not
approved strategy settings.

## Data source

- Diagnostic CSV: `/mnt/c/rangebar_diagnostics/RangeBarDiagnostics_2026-09-13_122732.csv`
- Contract: MESU26
- Diagnostic timestamp coverage: 2026-08-02 15:00:00.336 through
  2026-09-11 13:36:43.992
- Completed range bars: 52,890 (52,869 are 5-tick bars; 21 1-4-tick edge bars)
- Forward-label configuration: signal-bar-close reference entry; next 12
  completed bars; 5-tick target; 10-tick stop; same-bar target/stop is
  stop-first.

These forward labels are a diagnostic research proxy. They do not yet model
the live projected-breakout order, spread/slippage, commissions, a trading
session boundary, or the daily recovery policy.

## Pass 1 — Existing diagnostic labels

Only rows with complete 12-bar forward outcomes were used.

| Setup label | Candidates | Target | Stop/both | Other | Target rate |
|---|---:|---:|---:|---:|---:|
| 8 EMA bounce | 460 | 327 | 129 | 4 | 71.09% |
| 24 EMA bounce | 1,278 | 898 | 363 | 17 | 70.27% |
| 50 EMA bounce | 1,160 | 810 | 339 | 11 | 69.83% |
| General PB | 459 | 340 | 116 | 3 | 74.07% |

The 8-EMA figures are encouraging relative to the 66.7% gross break-even
rate of a 5-tick target versus a 10-tick stop, but they are **not evidence of
an edge yet**. The existing labels, fixed forward horizon, overlapping
candidates, and absence of transaction costs can materially bias this result.

## Pass 2 — Current versus pre-pullback 8-EMA slope

For 8-EMA candidates, the current three-bar slope was compared with the best
directional slope in the preceding six diagnostic bars. This directly tests
the stated hypothesis that a good pullback can flatten the 8 EMA.

| Current 8-EMA directional slope | Candidates | Target rate |
|---|---:|---:|
| Reversed briefly | 12 | 83.33% |
| 0° to 20° | 63 | 68.25% |
| 20° to 39° | 169 | 71.60% |
| At least 39° | 216 | 70.83% |
| Below 20° now, but prior slope at least 39° | 75 | 70.67% |

Initial conclusion: do not impose a hard current-slope gate that would reject
a temporarily flattened 8 EMA. The reversed subset is far too small to justify
a positive rule; it merely shows that a current-slope-only filter would discard
some successful examples.

## Pass 3 — 8-EMA penetration depth

Penetration is measured from the relevant low/high through the 8 EMA in ticks.

| Penetration bucket | Candidates | Target rate |
|---|---:|---:|
| Touch to 0.5 ticks | 27 | 66.67% |
| 0.5 to 1.5 ticks | 101 | 71.29% |
| 1.5 to 2.5 ticks | 120 | 77.50% |
| 2.5 to 3.5 ticks | 117 | 64.10% |
| 3.5 to 4.5 ticks | 95 | 72.63% |

The apparent 1.5-2.5-tick strength and 2.5-3.5-tick weakness require
date/direction and holdout testing before they can influence a rule. They must
not be treated as optimized thresholds.

## Pass 4 — Chronological and directional stability

The same existing 8-EMA label was divided into three chronological segments.
These are diagnostic checks, not a formal development/holdout split because no
settings have yet been selected.

## Pass 8 — Broad bounce-location walk-forward exploration

Tool: `tools/ema_bounce_walkforward.py`

This pass expands beyond the original 8-EMA label. It tests fast, middle, and
slow EMA touch/rejection locations across fast lengths 5/8/11/13/16, middle
lengths 18/24/30/36/42, and slow lengths 40/50/60/75. It uses recent trend
slopes, rather than a hard current-slope gate, so a pullback may flatten the
target EMA.

The period was evaluated in two chronological splits. A notable result is that
the broad **slow-EMA bounce** family is more stable than the fast family under
this simple close-entry outcome proxy. Examples with both early and late
samples include:

| EMA triple / bounce location | Early candidates / target rate | Late candidates / target rate |
|---|---:|---:|
| 5 / 24 / 50, slow | 33 / 78.79% | 42 / 78.57% |
| 5 / 18 / 50, slow | 41 / 78.05% | 44 / 77.27% |
| 8 / 24 / 50, slow | 34 / 82.35% | 44 / 77.27% |
| 5 / 18 / 40, slow | 67 / 77.61% | 58 / 75.86% |

This does not establish an optimal EMA or a tradable 50-EMA system. The slow
template is intentionally broad, the samples are modest, and the result uses
the same diagnostic close-reference 5/10 outcome proxy. The finding does
justify a dedicated next pass: define slow-EMA pullback/rejection quality and
test its actual entry path, costs, and daily first-trade behavior.

## Pass 9 — 5/24/50 slow-bounce first-trade and tick audit

Tool: `tools/custom_slow_bounce_tick_audit.py`

The leading broad profile was isolated as a concrete research candidate:
5/24/50 EMA order; recent pre-pullback trend slopes; two counter-trend bars
preceded by two trend bars; touch/rejection of the 50 EMA; trend-side close;
and controlled penetration up to 10 ticks. It is deliberately distinct from
the existing `RangeEMA50Bounce` code and should not replace it without further
validation.

Using a provisional 15:00 futures-session boundary, the first qualifying setup
appeared in 28 of 29 sessions with chart data. Under the same 5-tick target /
10-tick stop, 12-bar close-reference proxy, those first setups were 24 target
and 4 stop (85.71%). The session boundary remains an explicit parameter, not a
settled rule.

The custom model produced 101 candidates in the tick-overlap window. Its
tick-sequence audit found 95 non-ambiguous candidates, all of which agreed
with the bar-level result. The remaining six timestamp-ambiguous candidates
also agreed under the strict next-second convention. This supports the
diagnostic outcome calculation but still does not model executable bid/ask
fills, spread, slippage, commissions, or the projected-breakout entry.

## Pass 10 — Combined first-trade and recovery opportunity simulation

Tool: `tools/daily_recovery_sim.py`

Provisional model: take the first 5/24/50 slow bounce of a 15:00-based session
as primary. If it stops, take the earliest subsequent non-overlapping generic
fast, middle, or slow bounce. Every trade is conservatively locked out for its
full 12-bar outcome horizon, even if target/stop would have occurred sooner.
Wins add 5 ticks; stops subtract 10; the daily goal is +5 ticks.

| Maximum recovery trades | First-loss sessions recovered | Recovery rate |
|---:|---:|---:|
| 1 | 0 / 4 | 0% |
| 2 | 0 / 4 | 0% |
| 3 | 2 / 4 | 50% |
| 4 | 2 / 4 | 50% |

The primary setup won 24 of 28 evaluated sessions. Of the four first-trade
losses, two recovered to +5 after three successful recovery trades; the other
two became -20 and -35 ticks under a four-trade cap. The sample is far too
small to approve recovery rules, but it demonstrates the central trade-off:
the broader pool creates opportunity, while unconstrained recovery can amplify
losses. Any production design needs a hard daily loss limit and a limited
recovery count.

| Period | All candidates / target rate | Long candidates / target rate | Short candidates / target rate |
|---|---:|---:|---:|
| Aug 02–15 | 139 / 67.63% | 80 / 70.00% | 59 / 64.41% |
| Aug 16–29 | 158 / 72.78% | 86 / 77.91% | 72 / 66.67% |
| Aug 30–Sep 11 | 163 / 72.39% | 86 / 72.09% | 77 / 72.73% |

The all-direction result is not concentrated in one period, but the early
short subset is weaker and samples remain modest. Any depth/slope modification
must be evaluated within these segments and directions before it is considered
robust.

## Pass 5 — Tick-file alignment and usable window

The MESU26 ask-tick file was checked against diagnostic timestamps and prices:

- At the end of the range, diagnostic bar timestamp
  `2026-09-11 13:36:43.992` aligns with ask ticks at `9/11/2026 13:36:43`;
  both show the 7662.25/7662.00 price sequence.
- The tick file begins at `2026-08-03 15:00:00` near 7631.25. The diagnostic
  matches that opening sequence beginning at `2026-08-03 15:00:01.108`.
- The 1,925 earlier diagnostic bars are retained as EMA warm-up history but
  excluded from any tick-aware execution analysis because this tick file does
  not cover them.

The resulting tick-overlap window has 50,965 diagnostic bars, 50,953 complete
forward outcomes, and 446 complete 8-EMA candidates. The baseline 8-EMA
target rate in this window is 71.75% (320 target, 122 stop/both, 4 neither),
which is consistent with the full diagnostic range.

Timestamp matching is adequate for the next pass. Because tick records are
second-granular while diagnostic bar timestamps include milliseconds, the
tick-sequence simulator must state how it treats ticks within the bar-close
second and must not overstate fill precision.

## Pass 6 — Tick-sequence audit of 8-EMA labels

Tool: `tools/tick_execution_audit.py`

The tool streams the ask-tick file and compares each completed diagnostic
8-EMA candidate with the price path through its 12-bar diagnostic horizon. It
uses the diagnostic close as a reference entry and does not allow a tick from
the signal's clock second to trigger the result. This is intentionally
conservative because raw ticks contain seconds but no milliseconds.

- 446 candidates were audited using unique diagnostic bar number, not
  timestamp, because multiple range bars can share an exported timestamp.
- 437 candidates had no timing ambiguity and all 437 matched the diagnostic
  target/stop/none outcome exactly.
- Nine candidates are timestamp-ambiguous: a later range bar shares the
  signal's second, or the strict tick result occurs in the horizon-ending
  second. Seven have the same result under the strict rule; two differ.

The two differences cannot be classified as diagnostic errors: tick ordering
inside the relevant second is unavailable. The audit CSV marks these rows as
`SecondPrecisionAmbiguous=True`.

This validates the diagnostic's bar-level outcome calculation for this use,
but it does **not** validate a live fill model. It remains a close-reference,
ask-price proxy without bid/ask spread, slippage, commissions, or projected
breakout entry mechanics.

## Pass 7 — Constrained EMA-length template sweep

Tool: `tools/ema_template_sweep.py`

The tool recalculates EMAs from the diagnostic close series and applies a fixed
version of the current fast-EMA pullback template. It tests only these length
families: fast 5/8/11/13, intermediate 18/24/30/36, and slow 40/50/60. The
entry/outcome proxy and all other gates remain unchanged.

The reconstructed 8/24/50 template produced 427 candidates at a 71.90% target
rate, close to the diagnostic's 446 candidates and 71.75% rate. The count
difference is expected from independent EMA initialization and the deliberately
normalized template, while the close rate provides a useful validation check.

Some alternatives are competitive (for example 5/30/40: 324 candidates,
73.46%; 5/36/60: 294, 73.47%). The highest raw rate, 13/36/40 at 75.58%, has
only 86 candidates. None of these is a selected setting: all were evaluated
on the same data and must next be compared by chronological development versus
holdout periods, direction, and tick-aware execution assumptions.

## Next checks

1. Break 8-EMA results down by long/short and non-overlapping date segments.
2. Define the actual trading-session boundary before evaluating first trade of
   day and recovery behavior.
3. Validate diagnostic timestamps/range-bar construction against the ask tick
   file before making tick-level execution claims.
4. Add a true entry/exit simulation for the chosen order model and costs.

## Later passes — Broad EMA and recovery findings

Broader walk-forward exploration found that slow-EMA bounce locations were more
stable than the original fast-EMA-only family under the close-reference 5/10
outcome proxy. The leading examples included 5/24/50 and 5/18/50 slow bounces.
Tick-path audits agreed with the bar-level result for every non-ambiguous
candidate tested; second-level timestamp ambiguity remains explicitly flagged.

The 5/18/50 slow profile is currently the more stable primary-first-trade
candidate: 84.62% (11/13) in the early period and 86.67% (13/15) in the later
period, with 119 total candidates. 5/24/50 was less stable (76.92% / 93.33%)
but had a slightly better result in the small recovery experiment.

With a provisional 15:00 session boundary and a conservative 12-bar lockout,
the first 5/24/50 slow trade won 24 of 28 sessions. Among four first-trade
losses, a broad fast/middle/slow recovery pool recovered two with a
three-trade cap; 5/18/50 recovered one of four. This difference is far too
small to select a recovery engine. The evidence supports keeping both primary
profiles and testing a predefined combined recovery pool on additional data.

## Pass 12 — 8-EMA quality-tier optimization

Tool: `tools/ema8_filter_scan.py`

A broader structural universe of 991 8-EMA pullbacks was built from EMA order,
two counter-trend bars preceded by two trend bars, 8-EMA touch, and a
trend-side rejection. A constrained chronological filter scan found a useful,
interpretable quality tier:

- recent directional 24-EMA slope at least 39 degrees;
- 8/24 EMA separation at least 5 ticks;
- 8-EMA penetration between 1 and 2.5 ticks;
- trend-side close, trend-colored rejection, and at least one tick of local
  displacement.

The tier scored 80.68% in the development segment (71/88) and 74.53% in the
later segment (79/106) under the bar-level proxy. A hard current 8-EMA slope
filter did not improve this result, supporting the requirement to allow the
8 EMA to flatten during a valid pullback.

The tick-audit implementation selected 198 candidates in the tick-overlap
window. All 194 non-ambiguous candidates agreed with the range-bar outcome;
four were timestamp-ambiguous. Tick-proxy results were 80.23% early (69/86)
and 74.11% late (83/112), with about six candidates per calendar day. This is
a credible high-frequency recovery-pool candidate, pending first-trade and
sequential-recovery testing under explicit daily risk limits.

## Pass 13 — Broad 8-EMA core opportunity tier

The strict 8-EMA tier was relaxed into a predefined core tier: 24-EMA prior
directional slope at least 20 degrees, 8/24 separation at least 1.5 ticks, and
1-4.5 ticks of 8-EMA penetration. Three-EMA alignment, pullback structure,
trend-side rejection, and local displacement remain required.

The chronological scan reported 71.60% development and 71.65% later target
rates. Its raw-tick audit selected 783 candidates, with 71.30% early and
70.78% late results. All 763 non-ambiguous candidates matched the tick-path
outcome; 20 were second-precision ambiguous. This core tier yields about 24
candidates per calendar day, versus roughly six for the strict tier.

## Pass 14 — Independent pullback-length comparison

Tool: `tools/ema8_pullback_length_scan.py`

One-, two-, and three-bar pullbacks were compared without carrying forward the
old two-bar assumption. The leading one-bar profile requires one counter-trend
bar followed by the rejection bar, a preceding trend-color bar, prior 24-EMA
slope at least 20 degrees, current directional 8-EMA slope at least 15
degrees, 8/24 separation at least 5 ticks, and 1-4.5 ticks of penetration.

| Pullback length | Development candidates / rate | Later candidates / rate |
|---|---:|---:|
| One bar | 255 / 72.94% | 237 / 73.00% |
| Two bars (best tier) | 112 / 75.89% | 134 / 76.12% |
| Three bars (best tier) | 75 / 70.67% | 78 / 70.51% |

The one-bar model offers the best frequency/quality balance. Its raw-tick
audit selected 469 candidates after tick-window boundary handling: 71.00%
early and 71.85% later. Of 446 non-ambiguous candidates, 445 matched the
bar-level outcome. The visual study was updated to mark this one-bar profile.

## Pass 15 — Unified one- and two-bar 8-EMA bounce display

The pullback-length scan was rerun after a visually strong two-bar pullback at
2026-09-11 08:26:12.836 was found to pass every trend, touch, rejection, and
displacement condition but fail the display's one-bar-only structure rule.

The selected display now treats pullback length as a setup feature:

- One-bar tier: current directional 8 slope at least 15 degrees, best prior
  24 slope at least 20 degrees, 8/24 gap at least 5 ticks, and 1-4.5 ticks of
  penetration.
- Two-bar tier: current directional 8 slope at least 15 degrees, best prior
  24 slope at least 39 degrees, 8/24 gap at least 5 ticks, and a shallower
  1-2.5 ticks of penetration.

The two-bar tier produced 112 development candidates at 75.89% and 134 later
candidates at 76.12% in the bar-level split. A fresh raw-tick replay found 237
overlap candidates. Of 234 resolved outcomes, 180 reached the 5-tick target
before the 10-tick stop (76.92%); early and later rates were 77.45% and 76.52%.
All 231 non-ambiguous cases agreed with the completed-bar label.

Three-bar setups remained near 70.5% in both periods. At a 5-tick target and
10-tick stop, that leaves only a thin pre-cost expectancy margin, so they are
not included in the primary orange display. This supersedes Pass 14's
one-bar-only visual-study decision while retaining its measured results.

## Pass 16 — Dedicated quality 24-EMA pullback analysis

Tools: `tools/ema24_pullback_scan.py` and
`tools/ema24_quality_tick_audit.py`

The 24 EMA was analyzed as a distinct slower pullback location. A touch could
occur on the rejection bar or on any immediately preceding countertrend bar;
the signal bar had to close back on the trend side of the 24 EMA with trend
color. The scan varied pullback length, recent/current 24 slope, 8/24 and
24/50 spacing, penetration, and rejection recovery across the same
development/later split.

Selected tiers:

- One bar: prior 24 slope >=45 degrees, current 24 slope >=10 degrees, 8/24
  and 24/50 gaps >=3 ticks, penetration 0-5 ticks.
- Two bars: prior 24 slope >=30 degrees, current 24 slope >=15 degrees, 8/24
  gap >=1.5 ticks, 24/50 gap >=3 ticks, penetration 0-5 ticks, and at least
  2 ticks of close recovery versus the prior bar.
- Three bars: prior 24 slope >=39 degrees, nonnegative current 24 slope,
  8/24 and 24/50 gaps >=1.5 ticks, penetration 0-5 ticks, and at least 1 tick
  of close recovery. This allows the deeper pullback to flatten—but not
  reverse—the 24 EMA.

The full diagnostic contained 93 one-bar, 130 two-bar, and 208 three-bar
selected candidates. Four-bar results were unstable across periods and the
five-bar sample was too small, so neither was selected.

The raw-tick overlap contained 419 candidates. Of 413 resolved outcomes, 310
reached the 5-tick target before the 10-tick stop (75.06%). Early/later rates
were 74.73% / 75.33%, long/short rates were 74.26% / 75.83%, and one/two/three
bar rates were 77.01% / 73.60% / 75.12%. All 399 non-ambiguous candidates
matched the completed-bar label. These remain close-reference research
outcomes without costs or fill simulation.

`RangeEMA8QualityBounce` now adds the selected 24-EMA candidates as black
bar-indexed circles four ticks beyond the signal bar. Existing 8-EMA signals
remain orange at a two-tick offset. The selected samples had no same-bar
overlap in this dataset.
