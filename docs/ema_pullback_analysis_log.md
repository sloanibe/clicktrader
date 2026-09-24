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

## Pass 17 — Signal-bar rejection and momentum shapes

Tools: `tools/ema_bounce_bar_shape_analysis.py`,
`tools/ema_bar_shape_rescue_scan.py`, and
`tools/ema_bar_shape_rescue_tick_audit.py`

The signal bar's directional tail was measured from its open to the extreme
against the pullback direction. A zero-tail trend bar was treated as momentum
evidence; one-tick and long-tail bars were treated as rejection evidence. The
result does not support a universal rule that a longer tail is better. Broad
long-tail populations were weaker; tail shape was useful only when paired
with EMA slope, spacing, penetration, and pullback length.

Four new 8-EMA branches survived the chronological split and raw-tick audit:

- one-bar, no-tail momentum with prior 24 slope >=45 degrees, nonnegative
  current 8 slope, gap >=3 ticks, and 0-4.5 ticks of penetration;
- one-bar, one-tick rejection with prior 24 slope >=20 degrees, current 8
  slope >=15 degrees, gap >=5 ticks, and 1-2.5 ticks of penetration;
- one-bar, >=4-tick rejection tail with prior 24 slope >=20 degrees, current
  8 slope >=15 degrees, gap >=3 ticks, 0-2.5 ticks of penetration, and at
  least one tick of local displacement;
- three-bar, no-tail momentum with prior 24 slope >=20 degrees, nonnegative
  current 8 slope, gap >=3 ticks, 0-4.5 ticks of penetration, and at least
  one tick of local displacement.

The four new 8-EMA groups produced 441 additional diagnostic candidates at
76.42%. The combined orange population increased from 739 to 1,180 candidates
and improved from 74.02% to 74.92%. Combined development/later results were
75.67% / 74.14%. Raw-tick results for the new groups were respectively
75.00% (186/248), 81.11% (73/90), 75.00% (33/44), and 77.14% (27/35).

Two new 24-EMA branches were selected:

- two-bar, one-tick rejection with prior 24 slope >=30 degrees, current 24
  slope >=10 degrees, 8/24 gap >=3 ticks, 24/50 gap >=1.5 ticks, 0-5 ticks
  of penetration, and at least two ticks of recovery;
- four-bar, no-tail momentum with prior 24 slope >=39 degrees, nonnegative
  current 24 slope, both gaps >=1.5 ticks, 0-5 ticks of penetration, and at
  least two ticks of recovery.

These added 63 diagnostic candidates at 85.71%. The combined black population
increased from 431 to 494 candidates and improved from 74.71% to 76.11%; its
development/later results were 75.54% / 76.63%. Raw-tick results were 83.87%
(26/31) for the one-tick rejection and 87.10% (27/31) for the no-tail
four-bar continuation. These are close-reference target/stop observations,
not a cost- or fill-adjusted trading expectancy.

The display retains orange circles for all selected 8-EMA branches and black
circles for all selected 24-EMA branches. Shape changes qualification logic;
it does not introduce another marker color.

## Pass 18 — Independent 50-EMA deep-pullback family

Tools: `tools/ema50_pullback_scan.py` and
`tools/ema50_quality_tick_audit.py`

This pass did not use the old `Ema50Bounce` diagnostic label or the former
`RangeEMA50Bounce` settings. Direction was derived from the current 24/50
relationship, full 8/24/50 order was verified immediately before the pullback,
and each outcome was recalculated in the derived direction from the following
12 range bars. This matters because a deep pullback can flatten or reverse the
fast-average diagnostic direction.

The broad population included one through six consecutive countertrend bars,
a trend-color signal bar closing back on the trend side of the 50 EMA, and a
touch within one tick through penetration up to 20 ticks. The scan compared
prior and current 50 slope, prior 24 slope, 24/50 separation, touch tolerance,
penetration, recovery, pullback length, and directional signal-bar tail.

The selected family contains five complementary branches:

- one-bar no-tail momentum: prior 50 slope >=10 degrees, current 50 slope
  >=-10 degrees, 24/50 gap >=1.5 ticks, touch within one tick, penetration
  <=5 ticks, and nonnegative close recovery;
- one-bar 2-3-tick rejection tail: prior 50 slope >=20 degrees, current 50
  slope >=10 degrees, touch within one tick, penetration <=5 ticks;
- two-bar 2-3-tick rejection tail: the same slope and touch requirements plus
  at least two ticks of close recovery;
- three-bar core: prior 50 slope >=20 degrees, prior 24 slope >=30 degrees,
  nonnegative current 50 slope, 24/50 gap >=1.5 ticks, actual touch through no
  more than five ticks, and at least two ticks of recovery;
- a strict one-bar structural branch: prior/current 50 slopes >=10 degrees,
  24/50 gap >=3 ticks, and actual touch through no more than five ticks. This
  admits otherwise excluded signal shapes only under the stronger EMA fan.

A separate broad long-tail exception was rejected. Although its overlapping
signals looked strong, its genuinely incremental candidates produced only
69.23%, illustrating that bar shape is supporting evidence rather than an
independent reason to trade.

The selected union produced 376 diagnostic candidates, with 298 targets when
unresolved horizons were conservatively counted as non-targets (79.26%). The
development/later results were 80.11% / 78.46%, and long/short results were
80.47% / 78.26%. This is about ten candidates per active chart date.

The raw-tick overlap contained 372 candidates. Of 354 resolved tick paths, 282
reached +5 ticks before -10 ticks (79.66%); earlier/later results were 79.75% /
79.58%, and long/short results were 81.05% / 78.61%. Every resolved tick path
agreed with the independently recalculated bar outcome, including all 344
non-ambiguous cases. Fifteen of the 18 unresolved cases had no usable ticks in
their audit window.

Only 13 selected 50-EMA candidates overlapped the expanded 8/24 display: none
overlapped the 8 EMA, and 13 overlapped the 24 EMA. The remaining 363 genuinely
new candidates retained a 79.06% diagnostic target rate.

`RangeEMA8QualityBounce` now includes this 50-EMA family as red bar-indexed
circles six ticks beyond the signal bar. Orange 8-EMA markers remain at two
ticks and black 24-EMA markers at four ticks. The 50 family has its own display
toggle and direction logic; it is not gated by the current 8/24 order. This is
visual-study integration only and does not add strategy or order behavior.

## Pass 19 — Low-close-recovery 24-EMA rejection rescue

Tools: `tools/ema24_rejection_rescue_scan.py` and
`tools/ema24_rejection_rescue_tick_audit.py`

A visually strong two-bar 24-EMA rejection at 2026-09-10 17:34:35 was excluded
because its close equaled the preceding close, giving zero ticks of the two
ticks of recovery required by the selected two-bar tier. It otherwise had a
47.1-degree recent 24 slope, 28.0-degree current slope, 5.41/6.57-tick EMA
gaps, only 0.91 tick of 24-EMA penetration, a four-tick directional tail, and
a trend-side close at the bar extreme.

The rescue scan started only with otherwise-qualified two-bar candidates that
failed the two-tick close-recovery gate. It compared tail, body, finishing
wick, close beyond the EMA, signal-bar and full-sequence extreme recovery, and
penetration depth. A generic four-tick-tail exception was rejected: the 12
closest visual analogues produced only 66.67%, with a severe long/short split.

The stable alternative was a controlled shallow probe: retain all existing
two-bar slope, order, spacing, color, and trend-side-close requirements, allow
less than two ticks of close recovery, but require deepest 24-EMA penetration
between zero and one tick. This produced 21 targets from 27 candidates
(77.78%), split 80.00% development and 75.00% later, and 81.25% long versus
72.73% short. All 27 raw-tick paths resolved with the same 21/27 result; all
25 non-ambiguous paths agreed with the completed-bar outcome. The motivating
example was non-ambiguous and reached its target.

This would add 27 non-overlapping black-marker candidates. On the diagnostic
sample, the combined 24-EMA family would move from 494 candidates at 76.11%
to 521 at 76.20%. The finding supports a small shallow-probe exception, not a
broad long-tail exception. `RangeEMA8QualityBounce` now includes the rescue as
another black-circle 24-EMA branch.

## Pass 20 — EMA-fan momentum pin bars

Tools: `tools/ema_momentum_pin_scan.py` and
`tools/ema_momentum_pin_tick_audit.py`

This pass defined a separate momentum family rather than relaxing an EMA-touch
rule. The motivating bar at 2026-09-11 08:06:17.844 had a four-tick directional
tail, one-tick trend body, full 8/24/50 order, slopes of 73.1/63.0/48.8 degrees,
and its entire range was almost six ticks beyond the 8 EMA. The existing
compact-PB and EMA-bounce labels were not used to select the research universe,
and forward outcomes were independently recalculated in the EMA-order direction.

The broad universe required a completed five-tick range bar, full EMA order,
trend color, a three-to-five-tick pullback-side tail, and a body entirely clear
of the 8 EMA. The scan compared all three current slopes, both EMA gaps, body
and full-bar distance from the 8 EMA, close extension beyond the prior close,
and prior trend-bar streak.

The selected momentum-pin profile is:

- directional 8/24/50 slopes >=60/45/39 degrees;
- 8/24 gap >=1.5 ticks and 24/50 gap >=3 ticks;
- three-to-five-tick directional tail with trend-colored body;
- the entire bar at least four ticks beyond the 8 EMA; and
- the close at least two ticks beyond the preceding close in the trend
  direction.

The distance and close-extension requirements were important. Steep slopes
without adequate separation from the 8 EMA produced a materially weaker broad
population. Three-tick and four-plus-tick tails performed nearly identically,
supporting the full three-to-five-tick definition rather than one optimized
tail length.

The selected profile produced 206 diagnostic candidates and 162 targets
(78.64%), split 79.31% development / 78.15% later and 78.22% long / 79.38%
short. Raw-tick replay resolved 198 candidates, with 156 targets (78.79%);
earlier/later results were 78.48% / 78.99% and long/short results were 78.22% /
79.38%. All 185 non-ambiguous resolved paths matched the independently
recalculated bar outcome. The motivating example was non-ambiguous and reached
its five-tick target.

The signals are structurally non-overlapping with EMA bounces because the
entire pin bar must remain at least four ticks beyond the 8 EMA; in a fully
ordered fan it therefore cannot touch the 8, 24, or 50 EMA. A conservative
12-bar lockout retained 156 candidates at a 78.21% bar-level rate, showing that
the result is not solely repeated markers inside the same momentum burst.

The evidence supports adding the momentum pin as a separately identified trade
family. The combined display now uses one direction-colored drawing arrow for
any qualifying family: a lime up arrow or red down arrow two ticks beyond the
signal bar. It is not part of order logic.

## Pass 21 — Zero-body momentum-pin relaxation audit

Tool: `tools/ema_momentum_pin_relaxation_tick_audit.py`

The unmarked bearish zero-body bar at 2026-09-11 12:09:52.700 was examined as
a prospective relaxation of the selected momentum-pin family. It had a
five-tick rejection tail, closed at its low, a four-bar directional streak,
60+/45+ degree 8/24 slopes, and wide 6.42/5.77-tick EMA gaps. It failed three
selected gates: the 50 slope was 36.11 rather than 39 degrees, the full bar was
only 1.91 rather than four ticks clear of the 8 EMA, and close extension was
one rather than two ticks. The example itself reached the five-tick target on
the raw ask-price path at 12:10:25 without first reaching the ten-tick stop;
its path was not second-precision ambiguous.

A blanket relaxation to 60/45/30-degree slopes, one-tick full-bar clearance,
and one-tick extension produced 692 bar candidates at 73.41%, compared with
206 at 78.64% for the selected family. Its 486 incremental candidates produced
71.19%, split 68.53% development / 73.62% later. The raw-tick overlap confirmed
the result: 670 resolved union candidates reached 73.43%, while the 472
resolved incremental candidates reached 71.19%. The combined raw proxy
expectancy at +5/-10 ticks fell from +1.82 to +1.01 ticks per resolved signal,
before costs.

Restricting the relaxation to exact zero-body, five-tick-tail bars closing at
the trend-side extreme was not stable. The raw overlap's 104 resolved additions
reached 71.15%, but split 60.38% development / 82.35% later and 63.46% long /
78.85% short. A direction-specific short rule was not selected because that
would be a post-hoc response to this example and requires independent data.

The narrowest plausible exploratory exception also required both EMA gaps to
be at least five ticks and a four-bar directional streak. Its full diagnostic
sample added 49 candidates at 73.47%; its 48 raw-tick-overlap candidates
produced 35 targets (72.92%), split 60.00% development / 82.14% later and
66.67% long / 80.95% short. Adding it to the current raw family changed the
resolved rate from 156/198 (78.79%) to 191/246 (77.64%). With a 12-bar lockout,
the comparison was 117/149 (78.52%) versus 138/177 (77.97%). First selected
pin per active date was 25/31 (80.65%) versus 26/32 (81.25%), but those daily
samples are too small to establish improvement.

Therefore the broad relaxation is rejected. The narrow exception reduces the
combined percentage by only 1.15 points, but its own severe chronological and
directional instability prevents treating it as validated. It may be displayed
as a separately identifiable experimental signal for forward collection, but
it should not silently alter the selected pin family or production trade logic.

## Pass 22 — Fixed-stop and continuation-entry audit

Tools: `tools/ema_signal_stop_sweep.py` and
`tools/ema_signal_breakout_stop_sweep.py`

All entry rules in the current display study were reconstructed without
changing their qualifications. Within raw-tick coverage this produced 2,183
unique signals: 1,130 EMA8, 508 EMA24, 358 EMA50, and 200 momentum-pin family
members, with only 13 multi-family overlaps. The family counts closely match
the independent audits, providing a cross-check on the reconstruction.

Each signal was replayed once through the chronological ask ticks from the
completed signal-bar close, beginning with the next whole-second tick and
ending after the same 12-bar horizon. A five-tick target was held constant and
hard stops from one through ten ticks were evaluated on the identical path.
The principal combined results were:

| Stop | Resolved target rate | P&L ticks per selected signal |
|---:|---:|---:|
| 4 | 52.90% | +0.76 |
| 5 | 59.26% | +0.92 |
| 6 | 63.59% | +0.99 |
| 7 | 67.42% | +1.08 |
| 8 | 70.88% | +1.21 |
| 9 | 73.15% | +1.23 |
| 10 | 76.30% | +1.43 |

Ten ticks had the highest expectancy for the combined population and for each
individual family. Its development/later rates were 76.35% / 76.25%, versus
66.70% / 68.06% at seven ticks. A 12-bar signal lockout gave 76.33% at ten
ticks versus 66.17% at seven, so clustered signals do not explain the result.
Among the 1,645 ten-tick winners, 182 (11.06%) first moved far enough against
the entry to be stopped by seven ticks. Tightening nominal risk by 30% reduced
aggregate losing ticks only from 5,110 to 4,949 because it created 196 more
stops, while eliminating 910 ticks of winning targets. Net sample ticks fell
from 3,115 to 2,366, approximately 24%.

A signal-bar structural stop does not create meaningful adaptation on these
five-tick range bars. Every selected trend-color signal closed at its
directional extreme, making a stop one tick beyond the signal bar exactly six
ticks for every candidate. That rule produced 63.59% and +0.99 tick per
selected signal.

Simple pre-entry partitions by family, direction, session, signal tail,
current slopes, EMA gaps, and close recovery did not reveal a stable
seven-tick subgroup. A 50-EMA/gap>=5 subset appeared favorable at exactly one
gap threshold, but adjacent thresholds failed to confirm it; it was rejected
as a narrow historical peak.

A separate continuation-entry experiment kept the markers unchanged but
required entry one tick beyond the signal close, with the order expiring when
the next range bar completed. It triggered 1,943 of 2,183 signals. With a
seven-tick stop it reached the five-tick target on only 61.49% of resolved
trades and returned +0.38 tick per triggered trade. The ten-tick stop remained
better even after the altered entry. A two-tick trigger was weaker still.

With entries and target fixed, a tighter stop cannot mathematically increase
the hit rate; it can only leave a path unchanged or turn a former winner into
a stop. The raw paths also show no compensating expectancy advantage at seven
ticks or less. Retain ten ticks as the current hard catastrophe stop. If loss
reduction remains a priority, the next distinct research question should be
an early-failure exit or favorable-excursion management rule while preserving
the ten-tick emergency stop, evaluated sequentially and with actual exit P&L.

## Pass 23 — Break-even runners and larger profit targets

Tools: `tools/ema_signal_runner_audit.py`,
`tools/ema_signal_bar_close_runner_audit.py`, and
`tools/ema_signal_breakeven_target_sweep.py`

The entry population and ten-tick initial stop remained unchanged. Once raw
price reached five favorable ticks, the experimental exits protected entry at
break-even. Two interpretations of a one-range-bar trailing exit were tested:
a tick-by-tick five-tick retracement from the best favorable tick, and a
completed-bar version whose favorable high-water mark and adverse exit were
updated only by range-bar closes. Fixed targets of 5/10/15/20/25/30 ticks were
also compared under the same break-even rule. Twelve-, 24-, and 48-bar maximum
holding windows prevented a horizon designed for the original five-tick target
from mechanically excluding runners.

The fixed five-tick exit remained best for the combined population. At a
12-bar horizon it returned +1.42 ticks per completed signal. Fixed targets of
10/15/20/25/30 ticks returned approximately +0.90/+0.76/+0.74/+0.70/+0.70.
Extending the horizon did not reverse this result. EMA8, EMA24, and EMA50
bounces each independently favored the original five-tick target.

The raw-tick five-tick trail was also weaker: +0.79 tick per resolved signal
over 12 bars, with 62.12% positive, 14.01% break-even, and 23.87% negative.
Only 25.44% banked at least five ticks. Twenty-four and 48 bars did not improve
the result materially because the tight trail usually resolved first.

The completed-range-bar-close trail was slightly better overall but still
inferior to the fixed target: +0.76/+0.78/+0.81 ticks at 12/24/48 bars. It did,
however, reveal a distinct momentum-pin behavior. On the 198 pins with an
observable 24-bar raw-tick window, the completed-bar runner averaged +2.61
ticks versus +1.82 for the fixed five-tick target. Development/later averages
were +2.82/+2.46, compared with +1.77/+1.85 for the fixed exit. The runner
finished 31.31% positive, 47.47% break-even, and 21.21% negative; 26.77%
banked at least five ticks. Its median result was zero, so the improvement came
from the intended positively skewed tail of larger trends.

Fixed larger targets corroborated only part of that finding. Momentum-pin
targets of 15 and 20 ticks averaged +2.05 and +2.53 ticks over 24 bars, but the
20-tick improvement was concentrated more strongly in development (+3.92)
than later data (+1.60, below the later fixed-five result of +1.85). The
completed-bar runner was more balanced across periods and adapted to the trend
rather than selecting one optimized large target.

The pin-runner advantage remains uncertain. Its paired improvement over the
fixed-five result was +0.79 tick per signal, but a 95% bootstrap interval was
approximately -0.48 to +2.21 ticks and included no improvement. Large winners
are legitimate to a trend-following exit, but the sample of 198 pins is not
large enough to establish the right-tail estimate precisely. Moreover, these
are independent per-signal paths; a 24-bar runner may occupy the position and
block later recovery signals. Before changing exit logic, run a chronological
one-position-at-a-time simulation with costs, signal priority, and daily state.

Current conclusion: retain the five-tick target for EMA8/24/50 bounce entries.
Treat a completed-range-bar-close runner, armed at +5 with a break-even floor,
as a promising momentum-pin-only research candidate. Do not apply the runner
indiscriminately to all signals and do not yet replace production exit logic.

## Pass 24 — Near-8-EMA four-tail pin rejection

The visually appealing bullish bar at 2026-08-05 06:31:06.756 had a four-tick
lower tail, one-tick body, 62.4/47.5/41.4-degree directional slopes, 6.78/9.36-
tick EMA gaps, a four-bar directional streak, and two ticks of close extension.
It failed only the selected momentum-pin clearance gate: its low was 2.53 ticks
above the 8 EMA rather than at least four ticks above it. The bar reached the
five-tick target on the raw path at 06:31:13 without ambiguity. The selected-
range report renumbered it 2545; its identity in the full-contract diagnostic
and research outputs is bar 4490.

Relaxing only full-bar clearance to 2.5 ticks added 157 diagnostic candidates
at 68.79%. The raw overlap contained 155 additions at 68.39%, split 65.28%
development / 71.08% later and 67.53% long / 69.23% short. The combined strict
plus relaxed raw family fell from 78.79% to 74.22%.

The example's exact four-tail/one-body near-EMA morphology was worse: 38 of 61
raw paths reached the fixed five-tick target (62.30%), below the 66.67% pre-cost
break-even rate for +5/-10 ticks. Requiring both gaps >=5 ticks and a four-bar
streak retained 29 cases at 65.52%, still below break-even and without stable
directional or chronological improvement.

The momentum-pin runner did not rescue the subtype. With break-even armed at
+5 and the completed-range-bar-close trail, all 155 near-clearance additions
averaged only +0.19 tick, while the exact 61 four-tail cases averaged -1.74
ticks (development -1.44 / later -1.97). The 29 strong-gap/streak cases averaged
-1.66 ticks. The pictured trade itself reached +5 and a best completed-close
excursion of +10 but subsequently touched break-even, producing zero under the
runner.

The diagnostic's `CompactLongPB` pass is a legacy diagnostic label and is not
an input to the self-contained quality-bounce display. The evidence supports
retaining the four-tick full-bar clearance gate and not adding this near-EMA
pin subtype despite the individual example's attractive appearance.

## Pass 25 — Opening delay and time-of-day analysis

Tool: `tools/ema_signal_time_window_analysis.py`

This pass retained all current entry qualifications, the fixed five-tick
target, and the ten-tick stop. It treated diagnostic timestamps as Pacific
chart time and the cash-session opening as 06:30. Results use the existing raw-
tick outcome table; `NONE` paths contribute zero to average sample ticks.

The data does not support a conventional 5/10/15-minute opening blackout. The
first eligible RTH signal after waits of 0/5/10/15 minutes produced respectively
20/29 (68.97%), 21/29 (72.41%), 19/29 (65.52%), and 20/29 (68.97%). A ten-minute
wait was slightly negative under the +5/-10 payoff. By contrast, a one-minute
wait produced 23/29 targets (79.31%), split 78.57% development / 80.00% later.
Its Wilson interval was wide (approximately 61.6%-90.2%) because only 29 days
were available, so this is a provisional operational rule rather than a proven
optimum. A seven-minute cutoff happened to produce the same aggregate, but
nearby cutoffs were erratic and do not establish seven minutes as structural.

The opening buckets explain the result. Signals from 06:30-06:31 produced
19/28 (67.86%) and only 7/12 (58.33%) later. From 06:31-06:35, 43/52 reached
target (82.69%); 06:35-06:40 produced 27/34 (79.41%); and 06:40-06:45 produced
32/42 (76.19%). Thus a ten-minute wait removes many good signals rather than
merely filtering opening noise.

Broad RTH blocks showed additional structure:

| Pacific/chart window | Resolved target rate | Ticks per selected signal |
|---|---:|---:|
| 06:30-06:45 | 77.56% | +1.62 |
| 06:45-07:00 | 67.89% | +0.18 |
| 07:00-08:00 | 79.83% | +1.97 |
| 08:00-09:00 | 69.80% | +0.46 |
| 09:00-11:00 | 76.86% | +1.50 |
| 11:00-13:00 | 79.61% | +1.94 |

The weak blocks were not uniform across families. For example, EMA24 and pin
signals remained respectable during 08:00-09:00 while EMA8 and EMA50 drove the
aggregate weakness. Family-specific clock rules would create many small cells
and were not selected from this single contract month.

As an exploratory general schedule, allowing 06:31-06:45, 07:00-08:00, and
09:00-13:00 retained 939 signals. Its 933 resolved paths produced 737 targets
(78.99%), split 78.15% development / 79.79% later, versus 76.34% for all RTH
signals. The first eligible signal inside those blocks was 24/29 (82.76%),
split 78.57% / 86.67%. This schedule was derived after inspecting the same
data and therefore needs forward validation; it is not yet a production time
filter.

Current conclusion: reject a five-, ten-, or fifteen-minute mandatory delay.
The most defensible opening hypothesis is to skip only 06:30:00-06:30:59 and
begin considering qualified signals at 06:31. Preserve the broader candidate
blocks for forward measurement, and evaluate them in a chronological one-
position-at-a-time daily simulation before promoting the filter into an
order-submitting strategy.

Display integration: `RangeEMA8QualityBounce` now has a
`UsePrimeTradingHours` input, enabled by default. The stricter display schedule
retains 06:31-06:45, 07:00-08:00, and 11:00-13:00 only. Boundaries are
start-inclusive and end-exclusive and use the bar's chart timestamp directly;
the indicator performs no timezone conversion. This filters visual signal
arrows only and does not submit orders.

## Pass 26 — Momentum-pin horizontal left clearance

Tools: `tools/ema_momentum_pin_left_clearance.py` and
`tools/ema_momentum_pin_left_clearance_tick_audit.py`

This pass tested the trader's hypothesis that horizontal open space can be
more revealing than distance from the 8 EMA. Two definitions were measured
over the prior ten completed bars: clearance beyond the signal close and the
stricter absence of a prior bar reaching the pullback-side edge of the pin's
body. The signal bar still required full EMA order, trend color, a three-to-
five-tick directional tail, and its body completely on the trend side of the
8 EMA. Outcomes retained the +5/-10 tick, 12-bar proxy.

Close-level clearance was mostly redundant with the existing two-tick close-
extension rule. Body-zone clearance was materially more discriminating. Among
prime-time pins whose full bar was zero to less than two ticks beyond the 8
EMA, the bar-level target rate rose monotonically as the required body-clear
lookback increased: 72.8% with no requirement, 75.3% at two bars, 77.2% at
three, 79.1% at five, 81.3% at seven, and 83.3% at ten. The ten-bar group had
97 candidates: 80 targets, 16 stops, and one unresolved diagnostic path. Its
development/later rates were 77.1% / 87.8% by selected signal.

Raw ask-tick replay resolved 91 of the 97 ten-bar candidates and produced 76
targets and 15 stops (83.52%), or +2.37 ticks per selected signal before costs.
Restricting to the user's exact one-to-two-tick bodies retained 72 candidates;
68 resolved paths produced 56 targets and 12 stops (82.35%), split 77.14% in
development and 87.88% later. Zero-body pins were also strong but are not
necessary to support the stated one-to-two-tick-body hypothesis.

The left-clear near-EMA set was disjoint from the current four-tick-clear pin
family. During prime hours, the current family produced 50 targets and 11
stops among 61 resolved paths (81.97%). Combining it with the new set produced
126 targets and 26 stops among 152 resolved paths (82.89%) while increasing
selected opportunities from 67 to 164. The new set was directionally uneven:
46/49 resolved longs reached target (93.88%), versus 30/42 shorts (71.43%).

Current conclusion: horizontal body-zone clearance is a useful and testable
feature, and ten clear bars identifies a promising near-EMA momentum-pin
family that the old distance gate excluded. Because the rule and prime-time
interaction were discovered on this same contract month, and because of the
large long/short asymmetry, retain it as a separately identifiable forward-
validation candidate before promoting it into the display or trade logic.

Display experiment: at the trader's request, the display uses the less
restrictive seven-clear-bar version for visual evaluation. The default-on
`ShowSevenBarClearPins` input adds a cyan direction arrow when the pin has a
three-to-five-tick directional tail, its body is fully beyond the 8 EMA, its
full range is zero to less than two ticks beyond the 8 EMA, and none of the
preceding seven bars reaches the body's near edge. The global prime-hours
filter continues to apply. This does not change the selected four-tick-clear
pin family and remains display-only experimental logic.

The exact seven-bar display rule selected 124 prime-time candidates. Raw ask-
tick replay resolved 115: 94 targets and 21 stops (81.74%), with nine paths
reported as `NONE`; eight had no tick coverage and one covered path reached
neither boundary during the diagnostic horizon.
The one-to-two-tick-body subset produced 74 targets and 16 stops among 90
resolved paths (82.22%). Seven-bar development/later results were 78.18% /
85.00%; long/short results remained asymmetric at 91.04% / 68.75%.

Expanded display experiment: `ShowExtendedSevenBarClearPins`, enabled by
default, displays the otherwise identical 2-to-less-than-4-tick EMA-distance
tier with magenta direction arrows. The original 0-to-less-than-2-tick tier
remains cyan and independently switchable. Across the diagnostic month,
adding the expanded tier increased prime-time seven-clear candidates from 124
to 310 and reduced the combined bar-level resolved target rate from 81.30% to
76.05%. The expanded tier alone produced 135 targets and 51 stops (72.58%).

## Pass 27 — Current-signal break-even comparison

Tool: `tools/ema_current_signal_breakeven_comparison.py`

This paired raw-tick comparison used the current prime-time display population,
including both seven-bar-clear pin tiers. Every entry retained the ten-tick
hard stop and armed the completed-range-bar-close runner after reaching +5.
The only experimental difference was whether the stop immediately moved to
entry after arming or remained at -10 until the completed-bar trail exited.

At the 24-bar horizon, 993 paired paths completed. The break-even version
averaged +1.20 ticks per path versus +1.62 without break-even, a -0.42-tick
paired difference. Results were directionally consistent by chronological
segment: break-even underperformed by 0.46 tick in development and 0.38 tick
later. A paired bootstrap resampling complete trading dates placed the 95%
interval for the difference at approximately -0.72 to -0.15 tick.

Break-even improved 184 individual paths and harmed 123, but the total ticks
saved by the improvements were +425 versus -842 ticks forfeited on interrupted
recoveries and runners. It therefore reduced the frequency of negative exits
while also cutting off fewer but materially larger eventual winners. At 24
bars the break-even version had 286 positive, 492 flat, and 215 negative exits;
without break-even the counts were 409 positive, 185 flat, and 399 negative.

The family result was also informative. Removing break-even improved EMA-bounce
expectancy by 0.49 tick and seven-clear-pin expectancy by 0.37 tick. The strict
momentum-pin family showed a negligible +0.08-tick advantage from break-even,
with a wide day-bootstrap interval spanning -0.36 to +0.49. Thus there is no
reliable strict-pin exception on this sample.

The first eligible signal of each of the 29 active dates showed the same
expectancy tradeoff. Break-even averaged +0.62 tick with 10 positive, 12 flat,
and seven negative days; no break-even averaged +1.38 ticks with 13 positive,
seven flat, and nine negative days. Thus removing the floor improved the first-
trade average but created two additional negative first-trade outcomes.

Current conclusion: a +5 break-even floor hurts average P&L for the current
completed-bar runner, although it substantially reduces the percentage of
trades reported as losses. If the system exits at a fixed +5 target instead,
the question is moot because the target and break-even activation occur at the
same price. Costs, sequential position blocking, and actual bid/ask fills
remain outside this independent-path comparison.

## Pass 28 — 50 EMA filter ablation

Tools: `tools/ema50_filter_ablation.py` and
`tools/ema50_filter_ablation_tick_audit.py`

This pass separated two meanings of the 50 EMA filter on the current prime-
time, +5/-10, 12-bar population: requiring the 50 EMA to remain on the proper
side of the 8/24 trend, and retaining all setup-specific 50 slope and 24/50
gap gates. The 50 EMA bounce itself was not ablated because the 50 EMA defines
that setup.

Across the non-50-bounce families, current filtering selected 895 signals and
858 raw-tick-resolved paths, with a 78.09% target rate and +1.64 ticks per
selected signal. Removing only 50 order selected 1,056 signals and reduced the
resolved rate to 77.13% and expectancy to +1.50 ticks. Removing all 50-related
gates selected 1,166 signals, with a 76.63% rate and +1.43 ticks. Thus the full
filter removed 271 candidates and improved probability by 1.46 percentage
points and the raw price-path proxy by 0.21 tick per selected signal.

The effect varied materially by family. Raw resolved rates for current versus
all-50-gates-removed were 77.14% versus 76.61% for EMA8, 83.11% versus 81.68%
for EMA24, 81.97% versus 75.41% for strict pins, and 75.92% versus 74.58% for
seven-clear pins. For EMA24, removing 50 order alone was neutral/slightly
positive (83.33%), while removing its 24/50 spacing conditions caused the
decline. For strict pins, order itself was redundant with the remaining strong
fan conditions; the directional 50 slope and 24/50 gap produced the material
benefit.

Chronological stability was mixed. Current filtering scored 74.94% in
development versus 75.59% with all gates removed, but 81.21% later versus
77.64%. The apparent aggregate improvement is therefore promising rather than
conclusive on one contract month. The existing 50 EMA bounce family itself
remained strong at approximately 82-83% in the prime-time raw/bar proxies.

Current conclusion: retain the present 50 EMA roles. They improve combined
probability modestly, protect strict momentum-pin quality materially, and do
not impose a meaningful penalty on EMA8/24 quality. Do not add a universal
extra 50-slope threshold to families that currently use only order; this pass
tested removal of existing gates, not invention of a new common filter.

## Pass 29 — Normalized 4-tick versus 5-tick range bars

Tools: `tools/range_size_normalized_signal_comparison.py` and
`tools/range_size_normalized_tick_audit.py`

The 4-tick diagnostic at
`/mnt/c/rangebar_diagnostics/RangeBarDiagnostics_2026-09-13_231440.csv`
contained 75,247 rows, including 75,232 complete four-tick bars. The comparison
used the common full-session date span 2026-08-04 through 2026-09-11 13:00 and
the existing prime-time windows. Four-tick outcomes used a +4 target and -8
stop; five-tick outcomes retained +5/-10. Price-distance and slope-displacement
thresholds were proportionally normalized, while EMA lengths, pullback counts,
the seven-bar look-left, and the 12-bar outcome horizon remained bar-count
based.

Two 4-tick pin geometries were evaluated: the strict proportional three-to-
four-tick tail and a broader two-to-four-tick visual interpretation. Raw ask-
tick replay produced:

| Configuration | Selected | Resolved target rate | Ticks per selected signal |
|---|---:|---:|---:|
| 4-tick, 3-4 tail | 1,233 | 72.10% | +0.65 |
| 4-tick, 2-4 tail | 1,630 | 72.30% | +0.67 |
| 5-tick current | 993 | 78.59% | +1.78 |

Thus the strict 4-tick version increased opportunities by about 24%, and the
broad version by about 64%, but both reduced the resolved rate by roughly 6.3-
6.5 percentage points. A paired-date bootstrap placed the 95% interval for the
4-minus-5 difference at approximately -10.9 to -2.2 percentage points for
both shape variants. Four-tick development/later rates were 71.48% / 72.73%
for the strict shape and 71.50% / 73.11% for the broad shape; the five-tick
comparison was 75.67% / 81.39%.

Every major family was weaker on the normalized 4-tick chart: approximately
73.3% EMA8, 69.3% EMA24, 74.9% EMA50, 65.7% strict pin, and 72.2% seven-clear
pin under the strict shape, versus 77.1%, 83.1%, 82.4%, 82.0%, and 75.9% on
five ticks. Interestingly, the first eligible signal of each of 29 active
dates was 23/29 (79.31%) for both strict 4-tick and current 5-tick bars; the
broad 4-tick pin shape reduced this to 22/29 (75.86%).

Current conclusion: direct structural normalization to four-tick bars does
not improve winning percentage. It creates materially more recovery
opportunities, but at substantially lower per-signal quality and expectancy.
The result does not prove that no independently optimized 4-tick rule can
work; it shows that the current five-tick setup family does not transfer more
profitably merely by proportional scaling.

## Pass 30 — Clear-air no-tail momentum bars

Tools: `tools/ema_clear_air_momentum_scan.py` and
`tools/ema_clear_air_momentum_tick_audit.py`

This pass tested whether the seven-bar-clear concept identifies continuation
bars without requiring the existing three-to-five-tick rejection tail. The
candidate retained full 8/24/50 order, a body on the trend side of the 8 EMA,
the established prime-time windows, and the +5/-10, 12-bar close-entry proxy.
The primary version required the existing strict momentum slopes (8 >= 60,
24 >= 45, and 50 >= 39 directional degrees), a full five-tick trend body with
no tail on either side, and a close beyond every trend-side extreme from the
preceding seven bars. Existing 8/24 and 24/50 gap gates were retained for the
tick-audited version.

The current strict pin and combined zero-to-four-tick seven-clear family were
67 candidates at 80.60% and 310 candidates at 76.05% respectively in the
same bar-level proxy. Their disjoint union was 377 candidates, 289 targets,
87 stops, and one unresolved path: 76.86% resolved and +1.53 ticks per
selected signal before costs.

The strict-fan, full-body no-tail close-clear rule added 75 entirely new
candidates. Its bar proxy produced 59 targets and 16 stops (78.67%, +1.80
ticks per selected). Raw ask-tick replay produced 56 targets, 16 stops, and
three paths with no usable subsequent tick: 77.78% resolved and +1.60 ticks
per selected candidate. Tick-resolved development/later rates were 72.50% /
84.38%, and long/short rates were 75.00% / 82.14%. Twenty-six paths carry the
usual second-precision ambiguity flag, so these results remain a price-path
proxy rather than a live fill model.

Adding that bar-level family to the current momentum union increased the set
from 377 to 452 candidates (about 20%) while slightly increasing the resolved
rate from 76.86% to 77.16% and ticks per selected from +1.53 to +1.57. Removing
only the two gap gates added two further candidates, both bar-level targets;
that tiny difference does not justify selecting the looser rule yet.

Shape separation mattered. Allowing zero-to-one-tick directional tails as one
cumulative group reduced the strict body-clear bar-level rate to 69.46%; the
one-tick members, rather than the exact no-tail bars, caused the decline.
Broad one-to-three-tick and unrestricted-tail variants likewise increased
frequency but fell near 67-71%, close to the break-even probability for the
asymmetric +5/-10 payoff. A moderate slope fan created more opportunities,
but the full-body BODY7 raw-tick subset resolved only 68 targets and 25 stops
(73.12%) and was weaker than the strict-slope version.

Current conclusion: the exact full-body no-tail, strict-fan, seven-bar close-
clear setup is a promising separately identifiable display candidate. It adds
non-overlapping opportunities without degrading the observed aggregate
momentum population. Do not generalize it to one-tick or arbitrary tails;
those shapes behaved as different, materially weaker populations. Forward
validation is still required because the rule was developed on the same
single-contract sample.

Display integration: the default-on `ShowSevenBarClearNoTailBars` input adds
gold direction arrows for the strict-fan, full-body, seven-bar close-clear
rule. It is independently switchable from both seven-clear pin tiers and the
older strict momentum-pin family.

## Pass 31 — Strong-trend pullback breakout stop entry

Tool: `tools/ema_strong_trend_pullback_breakout.py`

This pass replaced signal-bar-close entry with the proposed structural event.
A trend-colored anchor bar had to establish a pivot beyond the preceding seven
bars in a fully ordered EMA fan. After the first completed countertrend bar,
an order was armed two ticks beyond the anchor extreme for up to six completed
bars. Only prime-time triggers were accepted. The first qualifying raw ask
tick supplied the reference fill; the outcome was +5 ticks before -10 ticks
within the next 12 completed bars. Shorts remain an ask-only proxy, and bars
sharing a whole-second timestamp remain timing-ambiguous.

The strict existing momentum context (8/24/50 directional slopes >= 60/45/39
degrees and the existing 1.5/3-tick fan gaps) produced 251 setups. Of those,
156 triggered within six bars: 96 targets, 58 stops, and two unresolved paths,
or 62.34% resolved and -0.64 tick per trigger before costs. Development/later
rates were 62.50% / 62.20%; long/short rates were 66.67% / 57.14%. Removing
the gap gates or using the broader moderate-slope fan did not rescue the rule.
The moderate-gap version triggered 382 times and resolved at 63.30%, also
negative under the +5/-10 payoff.

A retrospective pullback-length split showed that the broad failure was
concentrated in one-bar pullbacks. Strict-fan two-completed-bar pullbacks were
24 targets and 10 stops (70.59%, +0.59 tick per trigger), stable across the
development/later split at 71.43% / 70.00%, but asymmetric at 82.35% long and
58.82% short. The moderate-gap two-bar subset was 52/21 (71.23%, +0.69 tick),
with development/later rates of 66.67% / 75.68% and long/short rates of
76.32% / 65.71%. Only four strict three-bar cases triggered, which is too
small to interpret.

Current conclusion: reject the unrestricted "any pullback" breakout as a
trade rule under the established stop and horizon. It creates useful-looking
frequency but does not reach the 66.67% break-even target rate required by the
+5/-10 payoff. The two-bar subset is a possible follow-up hypothesis, not a
selected rule: it is much smaller, only modestly positive before costs, and
its direction split is unstable.

## Pass 32 — Swing obstacles and pullback pressure

Recorded 2026-09-19. Recommendations, definitions, reproduction command,
results, and limitations are in
[EMA bounce context research](ema_bounce_context_research.md).
Tool: `tools/ema_bounce_context_scan.py`.

The experiment reused the established expanded quality-display EMA signals
and cached +5/-10, 12-bar raw ask-tick close-entry outcomes. A fresh selector
reconstruction matched all 2,183 cached signal identities; the EMA-only union
contained 1,983 events, including 632 in the existing prime windows. This is
the research population, not the earlier live 8/24 detectors. The previously
used chronological split is a consistency check, not a fresh holdout.

Three independently fixed rules were tested. Excluding an unbroken confirmed
swing within five ticks retained 529/632 signals and moved target rate from
79.52% to 80.08%, with only +0.081 proxy ticks per signal and an uncertainty
interval spanning zero. Requiring pullback depth <=50% of the immediately
preceding consecutive-bar impulse retained 184/583 measurable cases and
reduced rate from 79.69% to 72.68%; reject that universal rule.

Requiring correction speed no greater than impulse speed retained 289/580
measurable cases and increased rate from 79.62% to 81.31%, or +0.258 gross
proxy ticks per signal. Its 95% paired day-bootstrap interval was -0.272 to
+0.745 ticks. Improvement concentrated in the earlier segment: 79.37% to
82.64%, versus 79.86% to 80.00% later. EMA8 and EMA50 had positive aggregate
associations, EMA24 a slightly negative one; all family intervals included
zero. Excluding ambiguous timestamps did not materially change the result.

Current conclusion: no new trading filter is justified. Keep relative
pullback speed as a fixed follow-up hypothesis for fresh data, especially
EMA8. Its small uncertain quality improvement currently sacrifices roughly
half of measurable opportunities. Room-to-target needs broader independent
level definitions before further claims; rejection response and volume remain
untested. No strategy or indicator source was changed by this pass.
