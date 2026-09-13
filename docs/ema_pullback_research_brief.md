# EMA Pullback System — Research Brief

## Purpose

Build and validate a range-bar trading system that finds a high-quality
continuation trade early in the session. The initial business objective is not
maximum trade frequency: it is to give the day's *first* trade a strong chance
of producing at least one profitable range bar.

The system is currently a research hypothesis. Its EMA lengths, thresholds,
entry mechanics, and recovery rules are **not** assumed to be optimal.

## Market idea

The desired trade is a brief pullback within an established, steep directional
move:

- Several moving averages are ordered and moving in the trade direction.
- Price has moved away from the averages rather than remaining compressed.
- A short, controlled counter-trend pullback reaches a chosen moving average.
- Price rejects the average and resumes the original trend.
- The entry is intended to participate in that resumption, not to buy/sell an
  arbitrary moving-average touch.

A pullback can temporarily flatten, or even slightly reverse, the current
reading of the fast EMA. That alone must not disqualify an otherwise good
fast-EMA bounce. Research should retain separate measures for **pre-pullback
momentum** (for example, the strongest directional 8-EMA slope in a recent
window before the pullback) and **current slope**. The former establishes the
move that is being resumed; the latter helps identify when the trend has truly
failed rather than merely paused.

The supplied chart is the visual reference for a long setup: a steeply rising
yellow 8 EMA, black 24 EMA, and green 50 EMA; a shallow pullback into the 8;
then a bullish rejection and continuation. The mirror image applies to shorts.

## EMA role hypothesis

The current 8 / 24 / 50 EMA set is a starting point only.

| Role | Initial interpretation | Research question |
|---|---|---|
| Fast EMA (currently 8) | Shallow momentum-pullback location | Which fast length best identifies the first controlled retracement in a strong move? |
| Intermediate EMA (currently 24) | Normal corrective-pullback location and trend confirmation | Does it improve selection, and what separation/slope is useful? |
| Slow EMA (currently 50) | Higher-level trend and deep-pullback context | Does it filter weak regimes or provide a distinct, viable bounce trade? |

The research must compare a limited, sensible neighborhood of EMA lengths and
must prefer stable regions of settings over a single historical winner.

### Current research priority

Although slow-EMA bounces currently look promising as selective first-trade
candidates, the next optimization focus is the **fast/8-EMA bounce family**.
It generates materially more intraday opportunities and matches the desired
market behavior. The aim is to improve its quality with context—not replace it
with a low-frequency slow-EMA-only system. Key filters to test are the 24/50
trend profile, pre-pullback 8-EMA momentum, controlled penetration, rejection,
time/session context, and a defined recovery role.

The primary display now also includes a separately optimized **24-EMA bounce
family**. It treats the slower average as a deeper corrective location, allows
one-, two-, or three-bar pullbacks when their slope/recovery profile supports
them, and displays those candidates as black circles. This does not replace
the higher-frequency orange 8-EMA family.

The current protective-loss research assumption is **10 ticks**, equal to two
5-tick range bars (not 10 full MES price points). For the present phase,
optimize high-probability, numerous EMA opportunities first; do not optimize
or constrain the system around a recovery policy until the opportunity model
has been established.

## Daily trading objective and state model

### Initial daily state: seek one quality trade

At the beginning of each trading day, the strategy seeks only a qualifying
high-quality continuation setup. A successful first trade is defined initially
as reaching at least **one range bar of profit**. Exact tick/bar size and order
fill assumptions must be fixed per experiment.

If that profit objective is reached, the day is considered profitable and the
default policy should be to stop initiating new trades for that day. Whether an
exception is worthwhile is a later research question, not a default rule.

### Recovery state: first trade failed

If the first trade fails, the strategy enters recovery mode. Recovery trades
must recover the realized loss and then finish at least one range bar ahead for
the day before trading stops.

This is a *daily P&L objective*, not permission to loosen the entry quality
filter. Recovery needs its own risk limits so that a failed first trade cannot
produce uncontrolled trade count or loss. The research must evaluate at least:

- maximum recovery trades per day;
- maximum daily loss / hard stop;
- whether recovery uses the same setups or a stricter subset;
- whether recovery is allowed after a time cutoff;
- whether a losing day is preferable to forcing a recovery trade.

No martingale or size escalation is assumed. Position sizing and recovery
targets must be evaluated explicitly with costs and realistic fills.

## Candidate setup families

Each family trades only in the direction supported by the EMA profile. The
current code's 8/24/50 bounce and PB diagnostics are useful starting labels,
not final definitions.

1. **Fast-EMA bounce:** shallow pullback to the fast EMA in a strong, separated
   trend, followed by rejection/continuation.
2. **Intermediate-EMA bounce:** a deeper but still controlled correction to the
   intermediate EMA, with the slow EMA preserving the higher-level trend.
3. **Slow-EMA bounce:** a deeper retracement to the slow EMA; likely less
   frequent and subject to stronger context and confirmation requirements.
4. **Range PB continuation:** pin-bar/pullback behavior around an EMA profile,
   evaluated as a potentially separate entry expression of the same trend idea.

When more than one family is valid, a documented priority rule is required.

## Data and research assets

- MultiCharts range-bar chart and its exact construction/session settings.
- `RangeBarDiagnostic` selected-range CSV: completed-bar OHLC; 8/24/50 values,
  slopes, separations, distances and crosses; current gate labels; and forward
  research outcomes.
- MESU26 ask tick data under `/home/mark/tick_data`, including the large
  `MESU26-Tick-Ask-2.csv` file covering approximately 2026-08-03 through
  2026-09-11.
- Chart screenshots, used as visual gold-standard examples and near-misses.

Ask-only tick data supports price-path analysis but does not provide full
bid/ask fill or spread information. Any execution result must state its
conservative bid/spread/slippage assumption. Bid data is preferred if it
becomes available.

## Research workflow

1. **Reproduce and validate data.** Verify the range-bar construction, EMA
   values, timestamps, and session handling against MultiCharts diagnostic
   output before judging any settings.
2. **Profile before optimizing.** Examine all candidate pullbacks and their
   outcomes by EMA touched, depth, current and pre-pullback slope, separation,
   trend order, time of day, direction, and volatility/volume context.
3. **Form constrained hypotheses.** Test only sensible parameter ranges. Do
   not run an unconstrained search for the best historical combination.
4. **Model entries and exits.** Compare confirmed-bar versus intrabar breakout
   entries using tick sequence where possible. Include costs, slippage, and
   conservative same-bar stop/target handling.
5. **Evaluate the daily state model.** First-trade quality comes first; test
   recovery only after the base setup has demonstrated an edge.
6. **Hold out data.** Reserve dates that are not used for setting selection.
   Favor parameter areas that remain effective on holdout data and across
   long/short directions rather than a narrow peak.
7. **Visually audit finalists.** Review representative winners, losers, and
   near-misses against screenshots to ensure the selected rules match the
   intended market behavior.

## Primary evaluation measures

- First-trade win rate and expectancy by day.
- Probability of reaching one profitable range bar on the first trade.
- Average favorable/adverse excursion and stop/target sequencing.
- Net daily P&L after costs; drawdown; losing-day distribution.
- Recovery success rate, recovery trade count, and added drawdown.
- Trade count, parameter stability, and holdout performance.
- Performance broken down by long/short, session period, trend regime, and
  EMA setup family.

## Open decisions before the first formal pass

- Exact range-bar specification and session/timezone.
- Definition of one bar of profit in ticks and whether it is a target, a
  trailing threshold, or an exit rule.
- Entry approach: confirmed completed bar or intrabar breakout projection.
- Initial stop, target, exit, time cutoff, and cost assumptions.
- Recovery risk limits and whether recovery may use all setup families.
- Development versus untouched holdout date ranges.

## Guiding principle

The goal is a simple, explainable system whose first daily trade is selective
and repeatable. More trades, more parameters, or a more aggressive recovery
rule are improvements only if they survive out-of-sample and tick-aware
validation while preserving that principle.
