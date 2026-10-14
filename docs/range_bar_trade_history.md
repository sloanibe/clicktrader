# RangeBar-TradeHistory

`RangeBarTrading.cs` now writes a cumulative trade journal to
`C:\rangebar_entries`. On this WSL workspace, that same folder is
`/mnt/c/rangebar_entries`. Both studies execute in Windows MultiCharts, so the runtime path is the
Windows path. The indicator reads the journal by default; set
`UseSignalHistory = false` to use the earlier Order and Position Tracker view.
The indicator remains read-only and does not submit or cancel orders.

## Installation

1. Replace and compile the full `RangeBarTrading.cs` signal and the full
   `RangeBarTradeHistory.cs` indicator in PowerLanguage .NET Editor. Each file is
   self-contained; no external assembly or companion helper study is needed.
2. The signal records automatically to `C:\rangebar_entries`, using stream
   `default`. These are internal settings; its committed Strategy Properties
   input list is unchanged. In Auto Trading settings, keep **Order Filled**
   enabled so broker confirmations reach the journal.
3. In the indicator, set `UseSignalHistory = true` and use the same directory
   and stream. Set `BrokerProfile` and `AccountFilter` to your intended account,
   especially if several accounts use the folder. The symbol defaults to the
   chart's instrument.
4. Record a new pin or manual trade. Existing tracker history is not backfilled
   or retroactively classified. For old trades, switch `UseSignalHistory` off.
5. Duplicate signals on the same profile/account/instrument/chart role/range
   must use distinct, stable stream names in their signal source's internal
   `TradeHistoryStream` constant. Ask and bid roles already use separate files.
   Keep the stream and chart configuration stable across restarts.

## What the signal records

Each broker-confirmed entry or exit has a unique event ID, a durable trade ID,
its explicit setup (`pin bar`, `manual`, or the EMA setup), profile, account,
instrument, stream, ask/bid chart role, side, quantity, and confirmed price.
The price comes from MultiCharts' `OnBrokerStategyOrderFilled` callback's
`avg_fill_price`; it is a reported average, not an invented chart price.

Entry annotations separately record:

- **Intended setup bar:** the bar on which the signal submitted its entry stop,
  with its timestamp, native tick ID where available, original calculation bar
  number, OHLC snapshot, range size, tick size, and preceding completed bar.
- **Fill observation:** the confirmation receipt time, current chart-bar time,
  calculation bar number, and native tick ID when the callback ran.
- **Prices:** both the submitted entry stop price and broker-confirmed fill price.

The intended setup bar remains the entry annotation even when the confirmation
arrives on the next range bar or the fill price lies outside the setup bar.
Once the bar completes, an additional `bar` event updates its final timestamp
and OHLC without changing the execution price or duplicating the trade.
A preceding completed-bar snapshot lets the reader identify a forming setup bar
after a restart even if its own completion timestamp changed before the update
could be saved. Original bar numbers are diagnostic; they are not used as a
permanent chart identity because loading more history changes them.

The callback supplies no execution timestamp or order ID. Its receipt time and
fill observation bar are explicitly observations, not exchange execution time
or a promise that the fill occurred on that bar. Only fills confirmed to this
strategy are journaled. Orders manually placed outside this signal, backtests,
and earlier trades are not reconstructed. An entry confirmation received
without staged setup context is labeled unknown (`T`), using its observation
bar; an exit with no recorded open entry is reported rather than paired by guess.

## Persistence and pairing

New files are named `rangebar-<scope hash>.txt` and use UTF-8 text with Windows
line endings. Each trade is enclosed in 80-character `=` rules with its trade
ID, setup type, direction, and status. Entry, exit, and bar updates remain
separate labeled blocks inside that trade's group:

```text
================================================================================
TRADE "822270c62ef04cceb9a36c05a3615d0e" | "manual" | LONG
================================================================================

[ENTRY event]
[EXIT event]
[BAR UPDATE event]

================================================================================
END TRADE "822270c62ef04cceb9a36c05a3615d0e" | COMPLETE
================================================================================
```

A live trade says `OPEN`. After its position closes, it says
`CLOSED - BAR UPDATES PENDING` until all recorded entry/exit bars have finalized;
then it says `COMPLETE`. A standalone exit without a recoverable entry says
`ENTRY UNKNOWN`. Late updates stay inside the correct trade's rules, including
when another trade has already started. Rules and trade headings are display
text, not additional execution events. Earlier files without rules are accepted.
Here is an abbreviated example of one event block:

```text
BEGIN RANGE BAR EVENT (RBJ2)
  Event ID: "<unique event ID>"
  Trade ID: "<shared trade ID>"
  Event: ENTRY
  Annotation target ID: ""
  Trade type: "pin bar"
  Broker profile: "Paper Trader"
  Account: "SIM 001"
  Instrument: "MESZ26"
  Stream: "default"
  Chart: "ask"
  Confirmation received: 2026-10-14T09:00:01.0000000
  Annotation bar time: 2026-10-14T09:00:00.0000000
  Action: BUY
  Quantity: 1
  Confirmed price: 7825.25
  Annotation bar number: 123
  ...remaining labeled bar fields...
END RANGE BAR EVENT
```

This excerpt illustrates the layout; actual blocks include every bar field,
native tick ID, submitted entry stop price, and preceding bar snapshot. The
price remains invariant and timestamps retain full precision. Spaces and Unicode
in names are literal; quotes, backslashes, tabs, and newlines within names are
escaped so they cannot split a record. Bar-finalization records say
`Event: BAR UPDATE`, and their annotation target ID references the earlier fill.

Earlier `RBJ1` TSV rows and `.rbj` files remain readable. On the first append to
a new `.txt` stream, its existing `.rbj` history is copied into labeled text
blocks; the original is retained. Readers combine both files and deduplicate
event IDs, including any legacy fills received before updating the signal.
To create or refresh readable copies immediately from WSL, run:

```sh
python3 tools/make_range_bar_history_readable.py /mnt/c/rangebar_entries
```

The converter uses the production parser and writer, preserves originals, and
adds only existing recorded events. It can be run again to copy later legacy
appends. The codec lives in `shared/RangeBarJournal.cs`; run
`python3 tools/sync_range_bar_journal.py` after changing it to update both
self-contained study files.

The signal captures chart snapshots in memory and enqueues confirmed fills;
all journal reading, recovery, named-mutex waits, appends and flushes run on a
background worker. That worker never reads MultiCharts objects, sends orders,
or changes the signal's execution state. Existing inputs and trade management match commit `f470557`, with an explicit
repair to named entry routing: buy and sell-short orders are created separately
and selected at submission time. Their names are `pin bar buy`, `pin bar sell`,
`manual buy`, and `manual sell`; earlier unsuffixed names remain recognized for
cancellation and tracker labels. The callback also preserves the SDK's
base callback behavior. Snapshot capture does not call the strategy's mutating
range-measurement fallback.

Adding records is serialized by a named mutex and deduplicated by event ID.
To keep each trade together, the background writer preserves all recorded events,
groups them by trade ID, and saves a flushed temporary file in the same folder.
An atomic replacement publishes the complete new version. Readers permit file
replacement and retain a complete old snapshot if they already have it open.
The file remains cumulative across days; earlier valid events are retained.
This grouping rewrites the readable journal on the worker rather than inserting
late updates on the order execution thread. Incomplete/malformed events are
ignored when rebuilding the valid history.
The worker restores a uniquely open trade and remaining quantity before pairing
an exit after restart; partial entries/exits use the same durable trade ID.
Quantity state advances only after a successful write, so retrying an interrupted
flush cannot double-count a fill. Ambiguous entry associations are reported
rather than guessed.

Deleting the history file during recording is handled as normal recovery:
the next record recreates the file on the background worker, without reporting
file removal as an error. If a trade is open, its confirmed entries, partial
exits, and latest bar snapshot are retained in memory and written into the new
file first, preserving exit pairing and restart recovery. Closed trades are not
replayed from the writer's memory. A removed directory is also recreated.
Readers tolerate a file disappearing between discovery and opening.
If the file is gone and the signal has restarted, there may be no recoverable
entry context; the real exit is then recorded as unknown without fabricating an
entry connection. Data absent from both memory and retained legacy files cannot
be reconstructed.

File failures retain the queued event and retry off the chart thread once per
second, even without another market tick. Errors are reported through MultiCharts'
output on a subsequent real-time chart calculation. Journal problems cannot stop
the signal's order processing. Recalculation keeps the writer and queued events;
removing the signal does not wait for disk writes, and its background queue can
finish while MultiCharts remains running. Closing the application before an
asynchronous append completes can lose unsaved events. Successfully flushed
records survive recompilation, chart closure and application restart. Files are
never automatically deleted or rotated.

## Chart annotations

- Fat black horizontal arrowheads have no shafts. Entries point right at the
  intended candle's left edge and confirmed entry price. Exits point left at
  the observed exit candle's right edge and confirmed exit price.
- Black vertical arrows point up at the intended entry bar's low for buys or
  down at its high for sells, regardless of tail length.
- Black labels read `P1 in 100`, `P1 out 101`, or `M2 in 102`, `M2 out 101`.
  Entry labels sit immediately left of the arrowhead, centered on its price;
  exit labels mirror them on the right. Other setup types use `T`.
- Connecting lines use the saved trade ID. Green means zero or positive price
  result; red means a loss, accounting for direction and quantity before fees.
- Informational boxes stay hidden unless `ShowInfoOverlay = true`.

In journal mode, an explicit bar is matched by its saved timestamp and OHLC,
with native tick IDs disambiguating equal timestamps where available. A still
forming record is located immediately after its uniquely identified preceding
completed bar. Missing or ambiguous identities are not moved to a neighboring
bar by a price heuristic. Reload the same range configuration and quote side
for reliable bar matching. Price alone does not determine the setup bar.
Dense clusters can still overlap; labels stay attached rather than shifting.

Candle edges use `CandleBodyWidthPercent = 70` of native bar-center spacing,
following zoom. Adjust that percentage for your chart style, or set
`CandleEdgeOffsetPixels` to the measured candle half-width at your current zoom.

## Indicator inputs

| Input | Default | Behavior |
| --- | --- | --- |
| `UseSignalHistory` | true | Read durable signal annotations; false selects legacy tracker mode. |
| `TradeHistoryDirectory` | `C:\rangebar_entries` | Shared Windows journal folder. |
| `TradeHistoryStream` | `default` | Stable signal stream to display. |
| `BrokerProfile`, `AccountFilter` | blank | Optional exact scope filters. |
| `InstrumentOverride` | blank | Defaults to the chart's instrument. |
| `HistoryDate` | blank | Today by confirmation receipt time in journal mode; or `yyyy-MM-dd`. |
| `HistoryLookbackDays` | 7 | Legacy tracker lookback only. Journal mode retains all entry links. |
| `TrackerToChartOffsetMinutes` | 0 | Legacy tracker timestamp offset only; journal bar timestamps come directly from the chart. |
| `BarTimestampIsClose` | true | Legacy tracker completion-time interpretation only. |
| `ShowConnections`, `ShowLabels`, `ShowEntryBarArrows` | true | Enable the corresponding drawings. |
| `ShowInfoOverlay` | false | Show the informational status box. |
| `ArrowLengthPixels` | 22 | Vertical arrow length and horizontal head scale. |
| `CandleEdgeOffsetPixels` | 0 | Positive values override candle half-width in pixels. |
| `CandleBodyWidthPercent` | 70 | Estimated candle body width as percentage of center spacing. |
| `LabelSize` | 9 | Label text size. |
| `RefreshMilliseconds` | 500 | Scheduled refresh interval, minimum 100 ms. |

## Legacy tracker mode

With `UseSignalHistory = false`, records come from MultiCharts' tracker:
`FilledContracts`, `ExecPrice` (order average), and `FinalDT` (last update).
A final update can occur after the fill; it is not an execution timestamp.
FIFO connections are derived within profile/account/instrument and cannot prove
native trade pairing. Missing price/time values are skipped. Native chart
indices are preserved, completion timestamps select containing bars, and a
unique neighboring candle containing the entry price can correct placement.
That price heuristic is confined to legacy mode. Old `RangeBuy`/`RangeSell`
records retain `T`; their source is not inferred from candle shapes. Both old
`pin bar`/`manual` and new buy/sell suffixed names retain the P/M label prefixes.
Separate fixed-action order objects avoid using the constructor's default ask
setting to choose a short order's action before the saved bid input is applied.
Recompile and reload the signal to reconstruct these objects, keeping
`IsAskChart = false` on the bid chart and true on the ask chart.

## Validation

Run `python3 tools/check_signal_history_isolation.py`,
`python3 tools/check_range_bar_trade_history.py`, and
`python3 tools/check_four_tick_pinbar.py`. The isolation check verifies that the
committed input list and all other trading code remain unchanged except for
three observer hooks and the explicit named-entry direction repair. These test the production journal
codec/store, labeled-text round trips, legacy conversion, concurrent writes, retry after an I/O failure, restart recovery,
partial closes, trade-ID pairing, actual signal callback lifecycle methods,
blocked disk writes, off-thread retries, file/folder removal during open and
closed trades, partial-quantity recovery after deletion, trade grouping with late updates,
atomic replacement while readers are open, and the GDI+ renderer. A journal drawing fixture verifies the intended entry
bar wins when the confirmation time and price belong to the next bar. Other
fixtures verify legacy placement, candle-edge contact, centered labels,
entry-bar extremes, and result colors. Both complete studies compile against
the installed MultiCharts SDK.

Tests use temporary directories and simulated callbacks; they do not write
fake trades to `C:\rangebar_entries`, connect to a broker, or activate trading.
The synthetic renderer preview is not a screenshot or a live trade record.
Verify one new pin/manual long and short in running MultiCharts, then reload
the indicator and restart the signal to check persistence with your chart data.

Reference: [MultiCharts .NET Programming Guide, broker fill events](https://download.multicharts.com/downloads/MultiCharts.NET-ProgrammingGuide-v1.1.pdf)
and the installed `PLTypes.xml` / `PLStudiesProxy.xml` API documentation.
