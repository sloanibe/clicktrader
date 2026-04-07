# Coding Brief: Dynamic Renko Brick Size Indicator
## For: MultiCharts .NET (PowerLanguage .NET, C#)
## Prepared for: Claude Sonnet 4.6

---

## What We Are Building

A MultiCharts .NET indicator that runs on a 1-minute MES (Micro E-mini S&P 500) chart and
continuously calculates the optimal Renko brick size based on the volatility of the last 10
minutes. The output is a recommended brick size (integer, 3–10 ticks) that the trader uses
to decide which Renko chart to focus on.

---

## The Problem Being Solved

Renko brick size is a noise filter. If the brick size is too small relative to current
volatility, the chart produces whipsaw bricks with no meaningful signal. If it is too large,
the chart is too slow and the trader misses tradeable structure. MES volatility varies
significantly across the session — quiet midday periods may produce 4–6 tick average swings,
while open/close or news-driven periods may produce 12–18 tick swings. A fixed brick size
cannot handle both regimes well. This indicator solves that by recommending the right brick
size for the current volatility regime in real time.

---

## Core Formula

```
True Range (per bar) = Max(High - Low, Abs(High - Previous Close), Abs(Low - Previous Close))

ATR = Simple Moving Average of True Range over the last 10 bars (1-minute bars = 10 minutes)

Raw Brick Size = ATR x ATR_Multiplier (default 0.5)

Recommended Brick Size = Clamp(Round(Raw Brick Size), Min_Brick, Max_Brick)
```

All values are in ticks. MES tick size = 0.25 points, so 1 tick = 0.25.

---

## Why SMA, Not Wilder's Smoothing

The built-in MultiCharts ATR function uses Wilder's smoothing by default, which has long
memory — bars from 20 or 30 periods ago still influence the current value. For this
indicator we want equal weight on only the last 10 minutes, so the ATR must be calculated
manually using a Simple Moving Average of True Range values. Do not use the built-in ATR
function.

---

## User-Adjustable Inputs

| Input Name      | Type   | Default | Description                                      |
|-----------------|--------|---------|--------------------------------------------------|
| ATR_Length      | int    | 10      | Number of 1-minute bars in the lookback          |
| ATR_Multiplier  | double | 0.5     | Fraction of ATR used to derive brick size        |
| Min_Brick       | int    | 3       | Minimum allowable recommended brick size (ticks) |
| Max_Brick       | int    | 10      | Maximum allowable recommended brick size (ticks) |
| Enable_Alert    | bool   | true    | Fire an alert when recommended brick size changes|

All inputs must be exposed via [Input] attributes so they appear in the MultiCharts
indicator settings dialog.

---

## Calculation Steps (bar by bar)

1. Calculate True Range for the current bar:
   TR = Max(Bars.High[0] - Bars.Low[0],
            Math.Abs(Bars.High[0] - Bars.Close[1]),
            Math.Abs(Bars.Low[0] - Bars.Close[1]))

2. Store TR values in a rolling array or IndicatorSeries of length ATR_Length.

3. Compute ATR as the simple average of the last ATR_Length TR values.

4. Convert ATR from points to ticks:
   ATR_ticks = ATR / Bars.Info.MinMove   (MinMove = 0.25 for MES)

5. Calculate raw brick size:
   Raw = ATR_ticks * ATR_Multiplier

6. Round and clamp:
   RecommendedBrick = Clamp(Round(Raw), Min_Brick, Max_Brick)

7. Compare RecommendedBrick to previous bar's value. If different and Enable_Alert is true,
   fire a chart alert.

---

## Outputs / Plots

| Plot   | Name                  | Description                                           |
|--------|-----------------------|-------------------------------------------------------|
| Plot1  | RecommendedBrickSize  | Primary output — integer step plot, displayed large   |
| Plot2  | RawATR_ticks          | Secondary — raw ATR in ticks, for trader reference    |

Both plots go in a sub-panel (not overlaid on price bars).
Plot1 should use a step style (not interpolated line) since it is an integer value.
Use a distinctive color for Plot1 (e.g. yellow or cyan) so it is easy to read at a glance.

---

## Alert Behavior

When RecommendedBrickSize changes from the previous bar:
- Fire a MultiCharts alert (Alert() function)
- Message format: "Brick size changed to X ticks (ATR = Y ticks)"
- Only fire once per change, not on every subsequent bar

---

## Volatility-to-Brick-Size Reference Table (default parameters)

| 10-min ATR (ticks) | x 0.5 | Recommended Brick |
|--------------------|-------|-------------------|
| <= 6               | <= 3  | 3  (floor)        |
| 8                  | 4.0   | 4                 |
| 10                 | 5.0   | 5                 |
| 12                 | 6.0   | 6                 |
| 14                 | 7.0   | 7                 |
| 16                 | 8.0   | 8                 |
| 18                 | 9.0   | 9                 |
| >= 20              | >= 10 | 10 (ceiling)      |

---

## Platform / Environment Notes

- Language: C# within the PowerLanguage .NET framework
- Class must inherit from IndicatorObject
- Use the standard override methods: Create(), StartCalc(), CalcBar()
- Data access: Bars.High[0], Bars.Low[0], Bars.Close[1] (previous close = index 1)
- Tick size access: Bars.Info.MinMove (returns 0.25 for MES)
- For the rolling TR series, use an IndicatorSeries<double> or a manual circular buffer
  of length ATR_Length
- The indicator is applied to a 1-minute chart of MES, running alongside (not on) the
  Renko chart

---

## What the Trader Does With the Output

The trader keeps multiple Renko charts open simultaneously — for example a 3-tick, 5-tick,
7-tick, and 10-tick chart. This indicator tells them which chart to focus on at any given
moment. When the alert fires, the trader shifts attention to the Renko chart that matches
the new recommended brick size. No mid-session chart rebuilding is required.

---

## Suggested Class Name

DynamicRenkoBrickSize

---

## Out of Scope for v1

- Automatic switching of Renko chart brick size (not supported natively by MultiCharts)
- Hysteresis / dampening to prevent rapid oscillation of the recommendation
- Multi-timeframe data series (keep it simple: single 1-minute series)

Hysteresis can be added in v2 if the recommendation proves to oscillate too frequently
in practice.
