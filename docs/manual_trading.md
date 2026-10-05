# Manual Trading indicator

`ManualTrading.cs` is a MultiCharts .NET chart-side manual trade simulator. It draws and tallies hypothetical trades; it does **not** send orders to a broker.

Hold the Up arrow key and left-click a completed range bar to mark a long. Hold Down and left-click a completed bar to mark a short. The selected bar's **close** is the simulated entry price in both directions. A blue up arrow appears two ticks below the long entry bar's low; a red down arrow appears two ticks above the short entry bar's high.

The indicator keeps a realized net dollar total for each trading session. Its session boundary defaults to 06:30 chart time, matching `RangeSessionNavigator`; navigating to another day displays that day's own labels and zero-based total, without changing earlier days. A label six ticks outside each entry bar is added after that trade exits, showing the cumulative total for its entry session. `RoundTripCostDollars` defaults to zero and can be set to an all-in per-trade cost to make the displayed total net of that amount.

When you click an older completed bar, the study replays the completed bars already loaded on the chart to find the exit and tally the result immediately. When you click the latest completed bar and no exit has occurred yet, the arrow appears but there is no P&L label until the trade closes. Full recalculation/reload clears manual marks and totals; re-enter them in chronological order afterward.

The range size is calibrated automatically at each entry from the most common positive high-to-low range, measured in symbol ticks, among the selected bar and up to 19 preceding completed bars. Ties favor the most recently observed size. Forming bars and bars after the entry are excluded. With fewer bars available, the study uses those available; if none has a usable positive range, the entry is rejected. The inferred size is locked for that trade. This is intended for fixed range-bar charts.

The profit target is **one range** and the protective stop is **two ranges plus one tick**: a 5-tick chart uses +5/-11 ticks, an 8-tick chart uses +8/-17, and a 12-tick chart uses +12/-25. The fixed `ProfitTargetTicks` and `MaximumLossTicks` inputs have been removed.

The exit mode is chosen from that session's realized net total **at entry**:

- At zero or above: a price touch at +1 range takes profit, or a price touch at -(2 ranges + 1 tick) stops the trade, whichever occurs first. An opposing-color bar alone does not exit this mode.
- Below zero: the one-range target is disabled. The trade exits at the first completed opposing-color bar's close, or at a price touch two ranges plus one tick against the trade, whichever comes first. An opposing close may therefore realize a smaller loss or a profit.

The target and stop are directional mirrors for shorts. The two-range-plus-one-tick stop is an intended maximum, not a fill guarantee: if a completed bar opens beyond it, the simulator uses that worse open. On historical bars where both target and stop are touched, the stop is assumed first because OHLC alone cannot recover their order. On a forming live bar, the study checks the latest price on each tick. Real fills, fees, and slippage can differ from these chart calculations.

There is no automatic session-end flatten because no such exit was specified. If a trade spans a session boundary, it remains the one open position and its eventual result is attributed to its entry session.

Only one simulated position may be open at a time. Within a session, click entry bars in chronological order; a duplicate, overlapping, or out-of-order click is rejected with a chart message. Clicks on an incomplete bar are rejected. Entries and drawings are held in the study instance, so removing or reloading the study does not persist manual trade history.

Suggested chart checks after compiling in MultiCharts:

1. Click an older completed bar whose next bars visibly reached +1 range or -(2 ranges + 1 tick); verify the cumulative dollar label appears immediately under/over the arrow. A click on the latest completed bar should remain open until an exit occurs.
2. On a new day, click a completed bar with Up held; verify the blue arrow is below the low and a touch of entry + one range exits at +1 range and shows the day's cumulative dollar total below the arrow.
3. On a separate new day, let the first trade touch the protective stop (two ranges plus one tick from entry). The next trade that day should ignore the one-range target and exit on an opposing-color close or its own two-range-plus-one-tick stop.
4. Navigate to a different day using `RangeSessionNavigator`; its first trade should choose the zero-or-positive one-range target/two-range-plus-one-tick stop mode regardless of the earlier day's result, while the earlier arrows and labels remain unchanged.
5. Repeat on 5-, 8-, and 12-tick range charts and verify target/stop distances of 5/11, 8/17, and 12/25 ticks respectively. A historical entry must use only completed bars at or before that entry; later bars must not affect its calibration.
