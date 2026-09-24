# Heikin Ashi Diagnostic

`HeikinAshiDiagnostic` exports short-interval Heikin Ashi bars for timestamp
alignment with `RangeBarDiagnostic`. It is a read-only research indicator and
does not place orders or draw trade signals.

## Recommended setup

1. Open the same contract used by the range-bar diagnostic (for example,
   MESU26) with the same loaded date range.
2. Create the desired short-interval chart, initially two seconds.
3. If the chart itself uses Heikin Ashi bars, leave
   `ChartBarsAreHeikinAshi = true`. If it uses ordinary time bars and the
   diagnostic should calculate HA values, set it to `false`.
4. Leave `ExpectedBarSeconds = 2` for the proposed two-second study. This does
   not change the chart interval; it records the expectation and warns in the
   report when fewer than 95% of measured intervals match it.
5. Apply `HeikinAshiDiagnostic` and allow the chart to recalculate.
6. Hold Alt+D and click the first desired bar, then Alt+D-click the last bar.
   The selected interval exports automatically to `ExportDirectory`, which
   defaults to `C:\rangebar_diagnostics`. The first click displays a blue
   "Range 1 selected" label and prompts for the second HA bar. The second
   click replaces it with a green completion/export label.
7. Use the same beginning and ending timestamps as the corresponding
   `RangeBarDiagnostic` selection. Exact row counts will differ because the
   two charts have different bar construction.

The export creates `HeikinAshiDiagnostic_yyyy-MM-dd_HHmmss.csv` and a matching
Markdown summary. After the second selection, a modal results window displays
the selected-range summary and both export paths, matching the range-bar
diagnostic workflow. MultiCharts output also logs the full path.

`BodyComparisonLookbackBars = 3` compares each HA body with the preceding
three HA bodies and measures consecutive body contraction. On a two-second
chart, that represents approximately six seconds.
`VolumeComparisonLookbackBars = 20` compares the current bar's volume with the
preceding 20 bars, or approximately 40 seconds on a two-second chart.

## Exported measurements

The CSV includes chart OHLC, HA OHLC, body and wick sizes, body/range ratio,
close location, direction and direction streaks, body contraction, prior
seven-bar highs and lows, breaks of those levels, 8/24/50 HA EMAs and slopes,
EMA gaps/order, bar volume, up/down ticks, delta, relative volume, and bar
duration.

When `ChartBarsAreHeikinAshi` is true, `ChartOpen` through `ChartClose` are the
values supplied by the HA chart and are not an independent underlying-price
series. HA values are synthetic and must not be treated as executable prices.
Entry and exit studies should synchronize the export with ask ticks for long
entries/short exits and bid ticks for short entries/long exits.
