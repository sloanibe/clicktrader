# Manual Trading indicator

`ManualTrading.cs` is a MultiCharts .NET chart-side manual trade simulator. It draws and tallies hypothetical trades; it does **not** send orders to a broker.

Hold the Up arrow key and left-click a completed range bar to mark a long. Hold Down and left-click a completed bar to mark a short. The selected bar's **close** is the simulated entry price in both directions. A blue up arrow appears two ticks below the long entry bar's low; a red down arrow appears two ticks above the short entry bar's high.

The indicator keeps a realized net dollar total for each trading session. Its session boundary defaults to 06:30 chart time, matching `RangeSessionNavigator`; navigating to another day displays that day's own labels and zero-based total, without changing earlier days. A label six ticks outside each entry bar is added after that trade exits, showing the cumulative total for its entry session. `RoundTripCostDollars` defaults to zero and can be set to an all-in per-trade cost to make the displayed total net of that amount.

When you click an older completed bar, the study replays the completed bars already loaded on the chart to find the exit and tally the result immediately. When you click the latest completed bar and no exit has occurred yet, the arrow appears but there is no P&L label until the trade closes. Full recalculation/reload clears manual marks and totals; re-enter them in chronological order afterward.

The range size is calibrated automatically at each entry from the most common positive high-to-low range, measured in symbol ticks, among the selected bar and up to 19 preceding completed bars. Ties favor the most recently observed size. Forming bars and bars after the entry are excluded. With fewer bars available, the study uses those available; if none has a usable positive range, the entry is rejected. The inferred size is locked for that trade. This is intended for fixed range-bar charts.

The exit policy matches the session-P&L rule for Shift and pin entries in `RangeBarTrading.cs` (not the legacy EMA profit-mode switch). The profit target is **four ticks**, independent of range size. The protective stop is **two ranges plus one tick, with a nine-tick minimum**: a 4-tick chart uses +4/-9 ticks, a 5-tick chart uses +4/-11, an 8-tick chart uses +4/-17, and a 12-tick chart uses +4/-25. `ProtectiveStopLossTicks` defaults to zero (automatic); a larger value widens the stop, but a smaller value cannot reduce the automatic distance. Set this input to match the signal if its stop has been widened.

The exit mode is chosen from that session's realized net total **at entry**:

- At zero or above: a price touch at +4 ticks takes profit, or a price touch at the protective stop exits the trade, whichever occurs first. An opposing-color bar alone does not exit this mode.
- Below zero: the fixed target is disabled. The trade exits at the first later completed opposing-color bar whose close is profitable relative to entry, or at a price touch of the protective stop, whichever comes first. Losing and break-even opposing closes are ignored, even if the trade was previously profitable. For longs, an opposing bar closes below its open; for shorts, above its open. Dojis do not count. Profitability uses the same price-based tolerance as the signal (greater than one millionth of a tick), before costs.

The target and stop are directional mirrors for shorts. The configured protective stop is an intended maximum, not a fill guarantee: if a completed bar opens beyond it, the simulator uses that worse open. On historical bars where both target and stop are touched, the stop is assumed first because OHLC alone cannot recover their order. On a forming live bar, the study checks the latest price on each tick; color exits require a completed bar. The manual simulator books a qualifying color exit at that bar's close; the signal submits a market exit, so its actual fill can differ. The manual session total is local to this indicator, while the signal uses its shared session P&L. Real fills, fees, and slippage can differ from these chart calculations.

There is no automatic session-end flatten because no such exit was specified. If a trade spans a session boundary, it remains the one open position and its eventual result is attributed to its entry session.

Only one simulated position may be open at a time. Within a session, click entry bars in chronological order; a duplicate, overlapping, or out-of-order click is rejected with a chart message. Clicks on an incomplete bar are rejected. Entries and drawings are held in the study instance, so removing or reloading the study does not persist manual trade history.

Suggested chart checks after compiling in MultiCharts:

1. On a 4-tick chart with zero session P&L, verify a +4-tick target and a -9-tick stop. On 5-, 8-, and 12-tick charts, verify the same +4-tick target with stops at -11, -17, and -25 ticks.
2. After a loss, enter another trade. Verify opposing-color closes at a loss or break-even leave it open. A later profitable opposing close should exit; a same-color close or doji should not.
3. In recovery, verify price can pass +4 ticks without taking a fixed target, and that the protective stop remains active throughout. A trade that first becomes profitable and then has a losing opposing close must stay open unless its stop is touched.
4. Repeat for longs and shorts, using both historical clicks and new completed bars. An open trade should have an arrow but no total label until it closes.
5. Navigate to another session and verify its first trade uses the fixed target. A cross-session trade should retain its entry mode and tally to its entry session.
6. Set `ProtectiveStopLossTicks` above the automatic distance and verify the wider stop; values below the automatic distance should not tighten it.
