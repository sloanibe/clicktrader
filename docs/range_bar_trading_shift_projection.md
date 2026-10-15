# Shift-click trade projection

In `RangeBarTrading.cs`, Shift+Left Click stages a manual entry at the projected range-bar completion price, matching pin-bar entries. A 4-tick buy entry is the live low plus 4 ticks; a sell entry is the live high minus 4 ticks. `ShiftProjectionRequireOneTickConfirmation` defaults to `false` and applies to both manual and pin-bar entries on all range sizes. When `true`, buy entries add one tick above the boundary and sell entries subtract one tick below it. Entry guides and projected stop/target guides use the resulting entry price. The entry line stays green; a gray dashed guide shows the protective stop two ranges plus one tick against the entry. When shared session P&L is zero or positive, another gray dashed guide shows a fixed 4-tick profit target in the trade direction. When session P&L is negative, the fixed target guide is removed. Both guides follow the projected entry.

Starting a valid Shift entry explicitly disarms automatic pin-bar and EMA entries, including the four-tick pin-bar watch. The HUD shows `MANUAL | SHIFT ENTRY BUY/SELL`. A second Shift-click cancels the manual order and leaves the chart `UNARMED`; expiration or invalidation also leaves automation unarmed. Control-click is required to arm the four-tick watch again.

Shift entry stop orders use the recorded name `manual`; pin-bar entries use
`pin bar`. `RangeBar-TradeHistory` uses the entry's recorded name for `M` or `P`
labels on both endpoints. Existing unnamed/shared-name history keeps `T` labels.

Shift and pin-bar trades use the same P&L rule. At entry fill, the signal latches the shared session P&L category from its pre-fill snapshot. Zero or positive P&L receives a 4-tick limit target from the actual average fill; negative P&L receives no fixed profit target and uses a color-change trailing stop, with the profitable opposing-color close exit retained as a fallback. For a long, that means close below open; for a short, close above open. A doji does not trigger an exit. There is no minimum favorable move or extra confirmation tick for this exit, and the protective stop remains active. The rule remains fixed through that trade and is independent of F4's legacy EMA profit setting.

During a negative-session trade, a blue dashed `PROJECTED OPPOSITE CLOSE` guide shows the forming bar's reversal completion: the live high minus one range for a long, or the live low plus one range for a short. It updates as the bar's extremes move, including before the trade becomes profitable. Once the projected completion would exit at a profit, a native stop is armed at that exact level and the guide reads `COLOR CHANGE TRAILING STOP`. The stop and its guide ratchet upward for longs or downward for shorts and never loosen on a retracement or a new bar. There is no five-tick arming threshold or extra confirmation tick in recovery. The stop can execute when its price is reached without waiting for a bar-close calculation; the profitable opposite-color close exit remains as a fallback and the original protective stop stays active. The guide and trail state clear when the position becomes flat.

When the entry fills, the projected guides are removed and the solid trade-control lines use the actual average fill price. With `ProtectiveStopLossTicks = 0`, the protective stop is two ranges plus one tick for all setups; a positive value supplies a fixed tick distance.

Verify both long and short entries in MultiCharts with session P&L below, at, and above zero. Confirm moving previews, fill-based stop and target levels, no fixed target in negative P&L, exits only on confirmed opposite-color closes, and preview cleanup on cancellation or fill. `python3 tools/check_four_tick_pinbar.py` exercises the production projection and session-profit helpers with recorded broker/drawing calls.

## Automatic range and stop settings

`RangeSizeTicks` and `ProtectiveStopLossTicks` both default to `0`. Automatic range mode measures completed chart bars, excluding the partial forming bar. Before a valid measurement is available, projections and entries wait and the HUD shows `WAITING FOR RANGE`. A positive `RangeSizeTicks` overrides the measured size; a positive `ProtectiveStopLossTicks` overrides the automatic stop distance. Existing chart instances may retain saved input values: set both to zero to enable automatic mode.

The automatic protective stop is placed `(2 × range ticks) + 1` ticks from the actual average fill price, below a long and above a short. Range sizes of 5, 6, and 8 ticks therefore produce stops of 11, 13, and 17 ticks. The projected stop guide uses the same distance from the projected entry.

Entry remains price-triggered. A long Shift entry on a 6-tick ask chart is a buy stop six ticks above the forming bar's low. It does not wait for the bar's displayed color to change. Shorts mirror this six ticks below the forming high. Verify these levels, the actual fill-based stop, startup without a completed bar, and explicit overrides in MultiCharts.

## Click feedback on bid and ask charts

Control-click and Shift-click check the event modifier, the live Windows modifier, and the physical key state. Both restore the HUD if it was hidden. Control-click arms four-tick pin-bar watching for the chart's configured side even before a qualifying setup appears; entry lines appear only when a setup qualifies. Shift-click keeps the existing EMA gate: ask buys require EMA8 above EMA24, and bid sells require EMA8 below EMA24. A rejected Shift-click now displays the reason in the HUD for five seconds (EMA gate, unknown range, or an already open position). Control-click with an open position also explains why it was ignored.

For chart verification, confirm that `RangeBarTradingV3` is applied and enabled on each chart, with `IsAskChart = true` for ask and `false` for bid. Confirm the HUD's chart role, Control-click's armed status before qualification, and Shift-click's projection or blocked reason on each side. These checks require MultiCharts; the local harness verifies modifier decoding and accepted/rejected entry paths with broker calls recorded rather than transmitted.

## Pin-bar invalidation

Both pin-bar modes reject a buy pin whose live or projected low falls below the
immediately preceding bar's low, and reject a sell pin whose live or projected
high exceeds the preceding bar's high. Equal extremes are allowed. The legacy
mode also rejects unsupported pin shapes and completed bars that did not close
at the projected reversal boundary. Each evaluation clears the old candidate
before checking the current setup. Invalidation cancels a pending pin entry and
clears its preview while preserving the armed direction. The next bar is checked
against its own immediately preceding bar, including an invalidated pin.

## Automatic break-even from the actual fill

`AutoProtectiveStopOn1BarProfit` is enabled by default for manual and pin-bar
trades. While the position remains open, the current chart price must move one
full chart range in favor from the actual average entry price. On a four-tick
chart, longs trigger at entry plus four ticks and shorts at entry minus four
ticks. Adverse excursions and new bars never change that reference: recovering
four ticks from an adverse extreme, returning to entry, or reaching a bar boundary
with less than four ticks of profit cannot promote the stop on a four-tick chart.
At the trigger, the protective stop moves to the actual average entry price,
without requiring bar close or another confirmation tick. Tighter stops remain
tighter; a subsequent retracement cannot undo promotion. Current chart price is
used, so an earlier high/low before the fill cannot trigger promotion. Existing
targets and color-change management remain active. Turning the setting off
disables automatic promotion; clicking the stop retains its separate manual
behavior of entry with one tick of permitted loss. Existing charts retain saved
input values, so check this setting after reloading the signal.

## Persisted day P&L and the live position

The HUD's `Day PnL` is today's history-based realized profit plus live open
profit. Realized P&L is matched from confirmed entry/exit fills by durable trade
ID, including quantity-weighted partial exits, and assigned to each exit's local
calendar date. Earlier daily files are included to find overnight entries.
Unmatched exits do not invent profits. The calculation deduplicates fills and
combines ask/bid and range scopes for the same broker profile, account, instrument,
and history stream. Different accounts or instruments remain separate.
Price-point results are converted to currency using the chart instrument's
`BigPointValue`; history-based realized totals are before fees. The `Open` amount
comes from MultiCharts' current position `OpenProfit`, refreshed on live chart
calculations. The same realized base is counted once across chart instances;
duplicate instances of one quote-side/range scope do not double-count open P&L.
The HUD shows `Day PnL`, `Realized`, and `Open` separately.

Signal reloads restore the saved realized total. The background worker loads
and refreshes the history snapshot (normally every 500 ms, also invalidated by
fills); chart/order threads perform no file I/O or waits. Until the initial or
post-fill snapshot is current, the HUD shows `LOADING HISTORY` alongside the
live open amount, and a new entry waits rather than assuming zero day P&L.
The freshly loaded total is latched before entry submission so existing positive
versus negative day-P&L trade-management rules use the persisted base. An active
trade keeps its entry-time category as before. At midnight a new local-calendar
day starts; overnight exits are credited when realized, and live open profit
continues to update. Deleting history removes the realized baseline it contained;
only recorded fills can restore that baseline.
