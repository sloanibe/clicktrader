# Shift-click trade projection

In `RangeBarTrading.cs`, Shift+Left Click stages a manual entry one tick beyond the projected range-bar completion price on charts with a range of 4 ticks or less. A 4-tick buy entry is the live low plus 5 ticks; a sell entry is the live high minus 5 ticks. Smaller ranges use their own range size plus one tick. Larger ranges enter at completion unless `ShiftProjectionRequireOneTickConfirmation` is enabled. The entry line stays green; a gray dashed guide shows the protective stop two ranges plus one tick against the entry. When shared session P&L is zero or positive, another gray dashed guide shows a fixed 4-tick profit target in the trade direction. When session P&L is negative, the fixed target guide is removed. Both guides follow the projected entry.

Starting a valid Shift entry explicitly disarms automatic pin-bar and EMA entries, including the four-tick pin-bar watch. The HUD shows `MANUAL | SHIFT ENTRY BUY/SELL`. A second Shift-click cancels the manual order and leaves the chart `UNARMED`; expiration or invalidation also leaves automation unarmed. Control-click is required to arm the four-tick watch again.

Shift and pin-bar trades use the same P&L rule. At entry fill, the signal latches the shared session P&L category from its pre-fill snapshot. Zero or positive P&L receives a 4-tick limit target from the actual average fill; negative P&L receives no fixed profit target and exits on the first later opposing-color bar close. For a long, that means close below open; for a short, close above open. A doji does not trigger an exit. There is no minimum favorable move or extra confirmation tick for this exit, and the protective stop remains active. The rule remains fixed through that trade and is independent of F4's legacy EMA profit setting.

When the entry fills, the projected guides are removed and the solid trade-control lines use the actual average fill price. With `ProtectiveStopLossTicks = 0`, the protective stop is two ranges plus one tick for all setups; a positive value supplies a fixed tick distance.

Verify both long and short entries in MultiCharts with session P&L below, at, and above zero. Confirm moving previews, fill-based stop and target levels, no fixed target in negative P&L, exits only on confirmed opposite-color closes, and preview cleanup on cancellation or fill. `python3 tools/check_four_tick_pinbar.py` exercises the production projection and session-profit helpers with recorded broker/drawing calls.

## Automatic range and stop settings

`RangeSizeTicks` and `ProtectiveStopLossTicks` both default to `0`. Automatic range mode measures completed chart bars, excluding the partial forming bar. Before a valid measurement is available, projections and entries wait and the HUD shows `WAITING FOR RANGE`. A positive `RangeSizeTicks` overrides the measured size; a positive `ProtectiveStopLossTicks` overrides the automatic stop distance. Existing chart instances may retain saved input values: set both to zero to enable automatic mode.

The automatic protective stop is placed `(2 × range ticks) + 1` ticks from the actual average fill price, below a long and above a short. Range sizes of 5, 6, and 8 ticks therefore produce stops of 11, 13, and 17 ticks. The projected stop guide uses the same distance from the projected entry.

Entry remains price-triggered. With the default `ShiftProjectionRequireOneTickConfirmation = false`, a long Shift entry on a 6-tick ask chart is a buy stop six ticks above the forming bar's low. It does not wait for the bar's displayed color to change. Shorts mirror this six ticks below the forming high. Verify these levels, the actual fill-based stop, startup without a completed bar, and explicit overrides in MultiCharts.

## Click feedback on bid and ask charts

Control-click and Shift-click check the event modifier, the live Windows modifier, and the physical key state. Both restore the HUD if it was hidden. Control-click arms four-tick pin-bar watching for the chart's configured side even before a qualifying setup appears; entry lines appear only when a setup qualifies. Shift-click keeps the existing EMA gate: ask buys require EMA8 above EMA24, and bid sells require EMA8 below EMA24. A rejected Shift-click now displays the reason in the HUD for five seconds (EMA gate, unknown range, or an already open position). Control-click with an open position also explains why it was ignored.

For chart verification, confirm that `RangeBarTradingV3` is applied and enabled on each chart, with `IsAskChart = true` for ask and `false` for bid. Confirm the HUD's chart role, Control-click's armed status before qualification, and Shift-click's projection or blocked reason on each side. These checks require MultiCharts; the local harness verifies modifier decoding and accepted/rejected entry paths with broker calls recorded rather than transmitted.
