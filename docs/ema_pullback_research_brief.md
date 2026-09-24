# EMA Pullback System — Research Brief

## Purpose

For the September 19 recommendations on nearby swing levels, pullback speed
and depth, and eventual volume confirmation, see
[EMA bounce context research](ema_bounce_context_research.md).

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
one- through four-bar pullbacks when their slope/recovery and signal-bar shape
support them. The 8-EMA family
also uses separate rejection-tail and no-tail momentum branches, including a
selective three-bar continuation. Shape is corroborating evidence rather than
a stand-alone signal: broad long-tail populations were not inherently better.
This does not replace the higher-frequency 8-EMA family.

An independently derived **50-EMA deep-pullback family** is also supported by
the current contract sample. It uses the 24/50 relationship for direction,
requires full EMA order before the correction, and applies different
shape/slope logic to one-, two-, and three-bar pullbacks. It contributed 363
setups not already present in the expanded 8/24 display, at a 79.06% bar-level
target rate. Raw-tick-resolved results were 79.66% with stable chronological
and directional splits, but it has not been incorporated into trading/order
logic.

A narrowly defined 24-EMA shallow-probe rescue is also supported. For an
otherwise-qualified two-bar pullback that fails the two-tick close-recovery
gate, penetration of no more than one tick produced 21/27 targets with stable
chronological results. The evidence did not support a generic long-tail
exception.

A separate **EMA-fan momentum pin** family is supported for bars that do not
touch an EMA. It requires a three-to-five-tick directional tail, slopes of at
least 60/45/39 degrees across the 8/24/50 fan, adequate EMA separation, the
entire bar at least four ticks beyond the 8 EMA, and at least two ticks of close
extension. It produced 206 candidates at 78.64%, with 78.79% across resolved
raw-tick paths and stable time/direction splits. Because it cannot overlap an
EMA touch by construction, it is a genuinely additive momentum opportunity.

The combined quality-bounce study presents every qualifying family as a single
directional arrow two ticks beyond the signal bar: lime up arrows for long
signals and red down arrows for short signals. It is display-only and remains
outside trading/order logic.

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

## Current momentum-pin caution

The selected EMA-fan momentum-pin family requires the entire signal bar to be
at least four ticks clear of the 8 EMA. A September 11 zero-body bearish example
showed why visually compelling exceptions must be evaluated as populations.
Reducing full-bar clearance to admit it broadly lowered the raw-tick hit rate
from 78.79% to 73.43%. A tightly compensated zero-body exception lowered the
combined rate only to 77.64%, but its incremental population split 60.00%
development / 82.14% later. Keep that exception experimental and separately
identified until it succeeds on new forward data; do not use the example's
winning outcome to justify a retrospective production rule.

## Current stop-loss conclusion

A raw-tick sweep of all 2,183 current-display signals found no support for
reducing the fixed ten-tick stop to seven ticks or less while retaining the
five-tick target. Ten ticks produced 76.30% targets and +1.43 proxy ticks per
selected signal; seven ticks produced 67.42% and +1.08. Results were stable
across the development/later split and after a 12-bar signal lockout, and ten
ticks remained best within every EMA family. A one-tick continuation entry and
a signal-bar structural stop also failed. Keep ten ticks as the hard emergency
stop unless new independent data overturns this result. Research early-failure
exits and post-entry trade management separately rather than disguising them
as a tighter fixed stop.

## Current profit-runner conclusion

Moving all entries from the fixed five-tick target to larger targets or a
one-range-bar trail reduced average profit. EMA8, EMA24, and EMA50 bounces
should retain the five-tick exit. Momentum pins are a plausible exception: a
break-even floor armed at +5 followed by a completed-range-bar-close trail
averaged +2.61 ticks over a 24-bar window versus +1.82 at the fixed target,
with improvement in both chronological segments. The median was zero and the
paired confidence interval included no improvement, so keep this pin-specific
runner experimental. Its next test must be a chronological one-position-at-a-
time simulation; independent signal paths do not account for runners blocking
later entries or recovery opportunities.

## Current time-of-day conclusion

The available month does not support waiting five, ten, or fifteen minutes
after the 06:30 Pacific/chart-time opening. In particular, the first signal
after a ten-minute wait produced only 19 targets in 29 days, while signals in
the first ten minutes were generally strong. The initial 06:30-06:31 minute
was weaker and unstable; skipping only that minute produced 23/29 first-trade
targets with a balanced development/later split. Treat a 06:31 start as the
leading opening hypothesis, not a finalized optimum. Exploratory operating
blocks of 06:31-06:45, 07:00-08:00, and 09:00-13:00 improved the historical
population to 78.99%, but were selected from this sample and require forward
and one-position-at-a-time validation before implementation.
