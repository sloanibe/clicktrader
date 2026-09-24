# Heikin Ashi Pullback/Expansion — Initial Exploration

This is exploratory research, not an approved strategy specification.

## Data and scoring

- HA diagnostic: `HeikinAshiDiagnostic_2026-09-14_224625.csv`
- HA coverage: 2026-08-05 06:30:02 through 2026-09-14 22:44:30
- Comparable five-tick range-bar diagnostic coverage used for outcomes:
  2026-08-02 15:00:00.336 through 2026-09-11 13:36:43.992
- Chronological split: 2026-08-24
- Target/stop: +5/-10 MES ticks through the next 12 completed range bars
- A 12-range-bar lockout prevents repeated two-second signals from counting as
  independent opportunities.

Signals are detected only from HA bars. The range-bar proxy enters at the
first completed range-bar close after the HA signal. A separate raw ask-tick
audit instead uses the first ask tick in the next clock second as its reference
entry. HA OHLC values are synthetic and are never treated as executable fills.
Short results remain ask-only proxies until matching bid ticks are available.

## Pattern families

All profiles require the 8/24/50 HA EMA fan to remain correctly ordered across
the pullback and trigger. The first pass varied:

- the minimum directional slopes and 8/24/50 separations;
- one-to-five countertrend/doji pullback bars;
- absolute and recent-relative pullback-body size;
- doji evidence and progressive body contraction; and
- trigger body, body/range ratio, close location, and pullback clearance.

Two-second HA slope and body scales are much smaller than the corresponding
five-tick range-bar measurements. Reusing the range-bar thresholds generated
only five loose candidates, so HA thresholds were calibrated across the
observed HA distributions before the fixed profile comparison.

## Range-bar outcome proxy

| HA profile | Locked signals | Resolved target rate | Development | Holdout |
|---|---:|---:|---:|---:|
| Ordered/rising + any pullback + expansion | 924 | 69.57% | 70.07% | 69.19% |
| Ordered/rising + doji pullback + expansion | 798 | 69.91% | 72.40% | 68.06% |
| Moderate fan + doji pullback + expansion | 553 | 70.57% | 72.22% | 69.33% |
| Strong fan + doji pullback + expansion | 334 | 69.39% | 72.14% | 67.37% |
| Very strong fan + compact pullback + expansion | 161 | 72.78% | 74.60% | 71.58% |
| Very strong fan + doji pullback + expansion | 184 | 72.93% | 75.68% | 71.03% |
| Very strong fan + shrinking-doji pullback + expansion | 109 | 70.37% | 71.79% | 69.57% |

The best adequately populated variants used the strongest EMA fan. Compact
and doji pullbacks were effectively tied. Requiring an explicitly shrinking
multi-bar sequence did not improve the result.

## Raw ask-tick proxy

| HA profile | Signals | Target / stop / none | Resolved rate | Development | Holdout | Gross ticks/signal |
|---|---:|---:|---:|---:|---:|---:|
| Ordered/rising doji expansion | 798 | 538 / 256 / 4 | 67.76% | 68.55% | 67.18% | +0.16 |
| Very strong compact expansion | 161 | 111 / 49 / 1 | 69.38% | 73.02% | 67.01% | +0.40 |
| Very strong doji expansion | 184 | 129 / 54 / 1 | 70.49% | 74.32% | 67.89% | +0.57 |
| Very strong doji + strong pullback clearance | 15 | 12 / 3 / 0 | 80.00% | 80.00% | 80.00% | +2.00 |

Gross ticks assign +5 to targets, -10 to stops, and zero to unresolved paths;
they exclude costs. The 15-signal clearance result has an approximate 95%
Wilson interval of 54.8%-93.0%, so it is a forward-collection hypothesis, not
evidence for implementation.

For the populated very-strong-doji profile, longs reached 75.68% in the
range-bar proxy while shorts reached 71.03%. This directional difference was
not used to construct a long-only rule. Raw-tick performance declined from
74.32% development to 67.89% holdout, another reason not to promote the rule.

Restricting that profile to the 06:30-13:00 Pacific cash session retained 61
signals at 72.13% (74.07% development / 70.59% holdout). The 06:30-09:00 cell
was only 7/16 targets. These cells are too small to define another clock rule,
but they do not reproduce the previously favorable range-bar opening windows.

## Comparison with the range-bar momentum pin

The selected range-bar momentum-pin family previously produced 156 targets in
198 resolved raw-tick paths (78.79%) and approximately +1.82 gross ticks per
resolved signal. Its development/later rates were 78.48%/78.99%. A locked
range-bar version remained near 78.5%.

The populated HA-only result therefore does not improve the existing
range-bar momentum pin:

- very-strong HA doji expansion: 70.49%, +0.57 gross ticks/signal;
- selected range-bar momentum pin: 78.79%, +1.82 gross ticks/resolved signal.

The loose HA definition creates about 24 locked signals per active day but is
only slightly above the 66.67% gross break-even target rate. The very-strong
version falls to about 5.8 signals per active day, so it does not deliver a
large opportunity increase over the locked range-bar momentum family.

## Initial conclusion

The intuitive picture is present in the data, but the obvious broad HA rule is
not stronger than the range-bar momentum pin. The useful observations are:

1. Strong EMA separation and sustained slope matter more than an elaborate
   shrinking-body definition.
2. One or more compact/doji pullback bars add modest value.
3. A stronger expansion body alone does not improve results consistently.
4. Closing through the entire pullback may matter, but the current strict
   sample is far too small.
5. HA signals are best treated as a possible confirmation or complementary
   timing layer, not a replacement for the range-bar context at this stage.

## Reproducibility

- `tools/heikin_ashi_pullback_expansion_scan.py`
- `tools/heikin_ashi_pullback_expansion_tick_audit.py`
- `analysis_output/heikin_ashi_pullback_expansion_scan.csv`
- `analysis_output/heikin_ashi_pullback_expansion_tick_audit.csv`
