# RangeBar-TradeHistory

`RangeBarTradeHistory.cs` is a standalone, read-only MultiCharts .NET indicator.
It plots order records returned by MultiCharts' Order and Position Tracker API,
independently of `RangeBarTrading` and its entry/exit tracking. It does not place,
modify, or cancel orders, or write a separate history file.

## What appears on the chart

- Fat black horizontal arrowheads with no shafts: entries point right and touch
  the entry candle's left edge; exits point left and touch the exit candle's right
  edge, for both longs and shorts. The vertical position is the reported average
  execution price. The timestamp identifies the containing bar rather than
  displacing the arrow across its horizontal slot.
- Black vertical entry-bar arrows: buys point up at the bar's low, and sells point
  down at its high. These use the actual chart bar's extreme whether it has a tail
  or moves straight from its open. Multiple entries in the same bar and direction
  share one extreme arrow; their horizontal markers and trade numbers remain.
  The vertical locator checks the recorded entry price against the chosen
  candle's range. If that price is outside its range and exactly one adjacent
  candle contains it, the vertical arrow uses that adjacent candle. If the
  selected candle is already compatible, or the alternatives are ambiguous,
  it keeps the timestamp assignment. This check changes only the vertical
  marker, preserving horizontal arrows, labels and connectors.
- Short black labels share the entry type and trade number: `P1 in 100` and
  `P1 out 101` for a recorded `pin bar` entry; `M2 in 102` and `M2 out 101` for a
  recorded `manual` entry. The exit inherits the entry's prefix. Older or other
  entry names keep `T`, since their type cannot be determined reliably.
  Solid connecting lines are green for a zero or positive price result and red
  for a loss. Results account for the long/short direction and matched quantity;
  fees and commissions are not supplied by this order interface.
- Partial exits retain the entry's trade number. A reversal can show both a close
  and a new entry on the same order. Scale-ins can produce several connections to
  one closing order.
- Labels show only the trade number, `in`/`out`, and price. Entry labels sit
  immediately left of their arrowheads, centered vertically on the execution
  price. Exit labels mirror this on the right. Labels remain attached instead of
  shifting above or below the arrow to avoid collisions.
- Trade labels have transparent backgrounds. The informational heading is hidden
  by default; `ShowInfoOverlay = true` restores its day, instrument, record counts,
  and data limits.

Dense clusters can still overlap, especially when several entries share a bar
and price. Their labels stay aligned with their arrows; zooming in or narrowing
the selected account helps review crowded regions.

## Recorded data and precision

The installed API exposes `Order.FilledContracts`, `Order.ExecPrice` (an order's
average execution price), and `Order.FinalDT` (last update time). It does **not**
expose an individual execution tape through this interface. The optional information
overlay identifies these as order averages and last-update times, including partially filled or cancelled orders
that have a nonzero filled quantity. A cancellation/update timestamp is not
necessarily the earlier execution timestamp. Missing execution prices or last
update times are reported and skipped; the order placement time, limit price,
stop price, and chart bar opening/closing price are never substituted.

The tracker reports BUY/SELL orders without native entry-to-exit trade links.
Connections therefore use FIFO within each **broker profile + account +
instrument**. These are derived correlations of reported records, not native
MultiCharts trade IDs or a reproduction of the Positions History panel's rows.
Simultaneous opposite-side records are ordered by order ID and flagged in the
optional information overlay as uncertain. Missing history can also prevent reliable pairing.

Range bars have discrete horizontal slots rather than a continuous time axis.
Timestamps select the containing bar. Duplicate timestamps are flagged in the
optional overlay because bar assignment can be ambiguous. The arrows attach to
the candle edges, and do not reconstruct the price path inside the candle.
Overlapping executions with the same role, bar and price share an arrow-tip
location. Their labels identify the separate trades.

The default candle-body width is estimated from actual bar-center spacing using
`CandleBodyWidthPercent = 70`, and follows chart zoom. The API's
`ChartBarCloseDistance` is not used as a candle half-width: doing that displaced
the markers away from narrow candles. Adjust the percentage to match your chart
style, or set `CandleEdgeOffsetPixels` to the measured candle half-width in pixels
for precise attachment at the current zoom. A pixel override must be adjusted
when the candle width changes.

The updated `RangeBarTrading.cs` submits pin-bar entries as `pin bar` and
Shift-click manual entries as `manual`. Its cancellation and emergency order
recognition include both names; EMA entries retain their existing names. Reload
both the strategy and this indicator to use the new names. Orders already
recorded as `RangeBuy`/`RangeSell` cannot be retroactively classified from their
names.

This implements a view of **what the order tracker actually reports**. Exact
individual-fill timestamps/prices and broker-native trade pairing would require
an execution-level source not supplied by the documented tracker order interface.

## Installation

1. In PowerLanguage .NET Editor, create a C# **indicator** named
   `RangeBarTradeHistory` (the C# identifier cannot contain a hyphen; its chart
   heading reads `RangeBar-TradeHistory`).
2. Replace its contents with the entire `RangeBarTradeHistory.cs` file and compile.
3. Insert it on the desired range chart, in the symbol subchart. Keep update on
   every tick enabled. No companion signal or strategy is required.
4. Set `BrokerProfile` and `AccountFilter` to the exact tracker values for the
   account you want. Blank selects all returned profiles/accounts, which remain
   separate during matching.
5. Compare the arrows against the tracker **Orders** tab, including its average
   filled price and filled/cancelled update time.

## Inputs

| Input | Default | Behavior |
| --- | --- | --- |
| `BrokerProfile` | blank | Optional exact broker-profile filter. |
| `AccountFilter` | blank | Optional exact account filter. |
| `InstrumentOverride` | blank | Defaults to the chart's instrument; override with an exact MC or broker symbol if mapping differs. No prefix/continuous-contract matching. |
| `HistoryDate` | blank | Today in the configured chart time; or `yyyy-MM-dd` for another day. |
| `HistoryLookbackDays` | 7 | Request preceding history for entry/exit pairing, up to 365 days. Only the selected day's records and their matched prior-day entries are drawn. |
| `TrackerToChartOffsetMinutes` | 0 | Add this offset to tracker timestamps to match the chart. Use the same time zone for both if possible. |
| `BarTimestampIsClose` | true | Use completion timestamps so fills map to the actual containing range bar. Existing instances may retain the former false value; explicitly set true after updating. False is available for a data source that stamps bar starts. |
| `ShowConnections` | true | Draw green/red entry/exit links based on the price result. |
| `ShowLabels` | true | Show short black trade-number, in/out, and price labels. |
| `ShowEntryBarArrows` | true | Show upward buy-entry arrows at bar lows and downward sell-entry arrows at bar highs. |
| `ShowInfoOverlay` | false | Restore the informational status box when needed. |
| `ArrowLengthPixels` | 22 | Vertical arrow length and horizontal arrowhead scale; changing it does not move the tip. |
| `CandleEdgeOffsetPixels` | 0 | Zero estimates body width from bar spacing; positive values override the candle half-width for precise edge attachment. |
| `CandleBodyWidthPercent` | 70 | Candle width as a percentage of bar-center spacing, used when the pixel override is zero. |
| `LabelSize` | 9 | Text size. |
| `RefreshMilliseconds` | 500 | Refresh interval, minimum 100 ms; scheduled recalculation also refreshes without incoming ticks. |

Tracker collection history loads asynchronously, and availability depends on
MultiCharts and its broker connection. Initial emptiness is not proof that no
trades occurred. The indicator requests all order states in its study's tracker
collection and applies account/symbol filters locally. It reads the tracker only
during real-time calculation. Recalculation refreshes the source snapshot.

Bar completion timestamps are the default. With start-time interpretation, a
fill between two range-bar completion times was mapped one bar early, moving
both the vertical extreme arrow and horizontal entry marker to the previous bar.
Both markers now share the corrected completion-time assignment. A live fill
after the forming bar's latest timestamp stays on that forming bar until it closes.

The renderer also preserves each selected bar's original native chart index.
It does not look that bar up a second time by timestamp when placing an arrow;
a closest-left lookup or duplicate range-bar timestamp could otherwise move
the screen position to a neighboring bar. Each drawing uses the preserved native
index of its selected candle. The vertical-only price check can select an
adjacent candle without changing horizontal markers or connectors.

When a current broker position is available, an opening quantity not explained
by the returned history is treated as an unknown carry-in position. A close of
that quantity reads `T? out <price>` and gets no fabricated
entry connector. For older selected dates, the current broker position cannot
establish the historical opening quantity; load enough preceding order history.

## Validation

Run `python3 tools/check_range_bar_trade_history.py`. It tests the production FIFO
and time-positioning code, compiles the complete indicator against the installed
MultiCharts SDK, and exercises the production custom drawer on a bitmap with real
SDK interfaces. The fixture verifies shaft-free arrowheads touching opposite
candle edges at the reported prices, labels centered beside the entry head,
green/red result lines, and upward/downward entry-bar extreme arrows.
Regression fixtures also make timestamp lookup return the previous bar and
verify that PB1, full-tail pins, and manual entries still mark the selected bar.
A further fixture uses the reported first entry at `7825.25`, timestamped
`06:31:27.094`, with synthetic neighboring candle data to verify the vertical-only
range correction and unchanged horizontal marker. These invented candles are
not a reconstruction of the user's live chart. The tracker table alone does not
contain chart-bar identities; overlapping ranges, quote-side differences and
order-average prices can prevent a unique price-based bar identification.
`analysis_output/range_bar_trade_history_preview.png` is a synthetic renderer
fixture, not a screenshot or a record of live trades.

Verify in MultiCharts with a single-contract long and short, a same-bar round
trip, partial exits, account filtering, recalculation, and an entry preceding the
selected day. Confirm timestamp mode and offset before judging horizontal
placement. Runtime broker/history retrieval requires the running application;
the local checks do not connect to a broker.

References: [MultiCharts TradingData API](https://www.multicharts.com/trading-software/index.php?title=Receiving_Data._TradingData),
[Order and Position Tracker](https://www.multicharts.com/trading-software/index.php?title=Order_and_Position_Tracker),
[Bar time documentation](https://www.multicharts.com/trading-software/index.php?title=Symbol_Time),
and the installed SDK's `PLTypes.xml` / `PLStudiesProxy.xml` and assembly metadata.
