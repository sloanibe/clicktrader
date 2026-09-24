# Manual Trading indicator

`ManualTrading.cs` is a MultiCharts .NET chart-side manual trade simulator. It draws and tallies hypothetical trades; it does **not** send orders to a broker.

Hold the Up arrow key and left-click a completed range bar to mark a long. Hold Down and left-click a completed bar to mark a short. The selected bar's **close** is the simulated entry price in both directions. A blue up arrow appears two ticks below the long entry bar's low; a red down arrow appears two ticks above the short entry bar's high.

The indicator keeps a realized net dollar total for each trading session. Its session boundary defaults to 06:30 chart time, matching `RangeSessionNavigator`; navigating to another day displays that day's own labels and zero-based total, without changing earlier days. A label six ticks outside each entry bar is added after that trade exits, showing the cumulative total for its entry session. `RoundTripCostDollars` defaults to zero and can be set to an all-in per-trade cost to make the displayed total net of that amount.

When you click an older completed bar, the study replays the completed bars already loaded on the chart to find the exit and tally the result immediately. When you click the latest completed bar and no exit has occurred yet, the arrow appears but there is no P&L label until the trade closes. Full recalculation/reload clears manual marks and totals; re-enter them in chronological order afterward.

The exit mode is chosen from that session's realized net total **at entry**:

- At zero or above: a price touch at +8 ticks takes profit, or a price touch at -16 ticks stops the trade, whichever occurs first. An opposing-color bar alone does not exit this mode.
- Below zero: the +8 target is disabled. The trade exits at the first completed opposing-color bar's close, or at a -16-tick price touch, whichever comes first. An opposing close may therefore realize a smaller loss or a profit.

The target and stop are directional mirrors for shorts. The -16 stop is an intended maximum, not a fill guarantee: if a completed bar opens beyond it, the simulator uses that worse open. On historical bars where both target and stop are touched, the stop is assumed first because OHLC alone cannot recover their order. On a forming live bar, the study checks the latest price on each tick. Real fills, fees, and slippage can differ from these chart calculations.

There is no automatic session-end flatten because no such exit was specified. If a trade spans a session boundary, it remains the one open position and its eventual result is attributed to its entry session.

Only one simulated position may be open at a time. Within a session, click entry bars in chronological order; a duplicate, overlapping, or out-of-order click is rejected with a chart message. Clicks on an incomplete bar are rejected. Entries and drawings are held in the study instance, so removing or reloading the study does not persist manual trade history.

Suggested chart checks after compiling in MultiCharts:

1. Click an older completed bar whose next bars visibly reached +8 ticks or -16 ticks; verify the cumulative dollar label appears immediately under/over the arrow. A click on the latest completed bar should remain open until an exit occurs.
2. On a new day, click a completed bar with Up held; verify the blue arrow is below the low and a touch of entry +8 exits at +8 ticks and shows the day's cumulative dollar total below the arrow.
3. On a separate new day, let the first trade touch entry -16. The next trade that day should ignore +8 and exit on an opposing-color close or its own -16 stop.
4. Navigate to a different day using `RangeSessionNavigator`; its first trade should choose the zero-or-positive +8/-16 mode regardless of the earlier day's result, while the earlier arrows and labels remain unchanged.
