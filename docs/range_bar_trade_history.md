# RangeBar-TradeHistory

The signal writes one persistent, readable record per trade in
`C:\rangebar_entries` (`/mnt/c/rangebar_entries` from this WSL workspace).
The indicator reads that same file and plots the entry, correlated exits, and
intended entry bar. File operations run on a background worker.
`RangeBarTrading` no longer draws its older filled-trade arrows or trade-type
labels; execution annotations come from this indicator. Setup previews,
stop/target control lines, and the signal HUD remain available.

## Installation and migration

1. Replace and compile both full source files, `RangeBarTrading.cs` and
   `RangeBarTradeHistory.cs`, in PowerLanguage .NET Editor. Both are self-contained.
2. Reload the updated signal and indicator. Install them together: earlier
   versions cannot read the compact `RBJ4` closed-trade format. Do not leave an
   older signal writing to the same file after upgrading it.
3. The signal records automatically to `C:\rangebar_entries`, stream `default`.
   These are internal settings; the committed Strategy Properties input list
   remains unchanged. Keep the strategy's **Order Filled** event enabled in
   Auto Trading settings so broker confirmations reach the recorder.
4. In the indicator, set `UseSignalHistory = true`, using the same directory and
   stream. Filter `BrokerProfile` and `AccountFilter` if needed. The instrument
   defaults to the chart's symbol.
5. Keep `IsAskChart = false` on the bid chart and true on the ask chart. Pin/manual
   orders use fixed buy or sell-short objects selected at submission time, named
   `pin bar buy`, `pin bar sell`, `manual buy`, and `manual sell`.

On its first real-time calculation, the updated signal queues cleanup of its
existing history stream. Cleanup runs on the worker even if no new fill occurs.
It reads earlier `RBJ1` TSV, `RBJ2` events, and `RBJ3` trade records, folds bar updates,
and saves each known trade once. A history with an entry and several updates but
no exit stays **OPEN**. No closing price, exit time, or execution is inferred.
Absent files are left alone until a real record arrives.

For manual conversion of a folder, after stopping older signal writers, run:

```sh
python3 tools/make_range_bar_history_readable.py /mnt/c/rangebar_entries
```

The tool uses the production parser and writer to consolidate existing `.txt`
files and convert legacy `.rbj` histories. Original legacy files remain available
for compatibility. New code writes only the consolidated `.txt` file; there is
no separate persistent machine-format journal. Actual earlier tracker trades
that were never recorded by the signal cannot be backfilled or classified from
candle shapes.

Ask and bid chart roles use different files. Duplicate signal instances on the
same profile/account/instrument/role/range need distinct stable stream names in
the internal `TradeHistoryStream` constant. Keep the stream, range configuration,
and quote side stable across reloads.

## Completed trades: short summaries

Once the position is fully closed, its record changes to compact `RBJ4` format.
It shows the trade type, position, entry/exit dates and times with their prices,
and signed realized profit in ticks. For the reported manual short:

```text
================================================================================
BEGIN RANGE BAR TRADE (RBJ4)
  Trade: Manual
  Position: SHORT - 1 MESZ26
  Entry: 2026-10-14 13:09:40.679 -07:00 @ 7766.50
  Exit: 2026-10-14 13:12:24.774 -07:00 @ 7766.00
  Profit: +2 ticks (before fees)
  Chart metadata: ...compact scope, identity and chart anchors...
END RANGE BAR TRADE
================================================================================
```

The metadata line is abbreviated here. The actual record retains the durable
trade ID, profile/account/symbol/stream/role, tick/range sizes, exact confirmed
fill times, directions, quantities and prices, and annotation bar identities.
This is the same file the indicator reads; no separate machine-version file is
created. There are no previous-bar, fill-observation, update-history, or multiple
exit detail paragraphs in the human summary.

Each finalized closed bar needs only its timestamp, normalized tick ID, OHLC,
and original calculation number for chart identification. Previous-bar snapshots
are removed once finalized, along with fill-observation and submitted-price
diagnostics no longer needed to draw that completed trade. A still-forming bar's
preceding-bar identity stays in the compact metadata until its final update
arrives, ensuring reliable placement during that interval. Applied update IDs
remain compact metadata so retries cannot reapply or duplicate an update.

Positive results show `+2 ticks`, losses show `-2 ticks`, and break-even shows
`0 ticks`, before fees. With partial fills, entry/exit summary prices are
quantity-weighted averages and the exit time is the final close confirmation.
The reader retains individual fill prices and anchors internally for exact
partial-exit annotations; the human summary still shows one exit line. New compact
metadata also retains separate recorded fill-bar anchors (`at=`) so horizontal
and vertical entry markers can use different candles after reload. Profit
in ticks accumulates the realized movement across matched units, not an average
per contract. The current signal submits one contract.

## Open and unfinished trades

An **OPEN** trade stays in the detailed consolidated `RBJ3` format, with pending
exit fields. **PARTIALLY CLOSED** records retain remaining quantity and individual
partial exits for recovery. When all entered quantity has confirmed matching
exits, the same trade is written as the short closed summary. A missing exit is
never inferred from bar updates or apparent price movement.

Unknown-entry exits and quantity inconsistencies retain detailed records rather
than a verified-complete summary. `Bar details` remains `PENDING` or `FINAL` for
unfinished records. Updates modify existing bar snapshots without generating
separate event blocks. Successfully saved unfinished records recover the exact
trade ID and remaining quantity on restart.

## Consolidation and precision

Cleanup matches observations by their explicit annotation target and durable
trade ID. It can resolve a bar update even if it appears before the target fill
in the input. Older/non-final observations cannot roll back a newer finalized
bar. Duplicate fills and applied updates do not increase quantity or create
additional trades. Repeated cleanup leaves the same trade facts intact, including previously compacted closed records.
Updates change only annotation snapshots, preserving confirmed prices,
quantities, trade type, direction and confirmation times.

The price comes from MultiCharts' `OnBrokerStategyOrderFilled` callback's
`avg_fill_price`. The callback supplies no exchange execution timestamp or order
ID. Recorded entry/exit times are explicitly **confirmation receipt times**;
fill-observation bars are the bars present when the callback ran. These do not
prove the exchange execution's timestamp or bar.

Only broker confirmations belonging to the signal are recorded. Manual trades
submitted through this signal are included; orders manually placed elsewhere,
backtests, and previously unrecorded trades are not reconstructed. If setup
context is unavailable, the entry type remains unknown rather than inferred.

The intended entry bar is captured when the signal submits the stop. It remains
independent of the confirmation bar and actual confirmed price, even if that
price is outside the setup bar. Once the bar closes, its final shape and time
are folded into the trade. A preceding completed-bar snapshot helps identify a
forming setup after reload. Original calculation bar numbers are diagnostic;
loading more history can change them.

## Background writing and persistence

The signal captures chart state in memory and queues copies. Reading history,
cleanup, recovery, mutex waits, serialization, writes, flushes, and retries run
on the background worker. It never accesses MultiCharts study objects, sends
orders, or modifies trading state. The existing signal inputs and trade-management
code remain as committed, apart from the explicit named-entry direction repair.
Snapshot capture does not call the strategy's mutating range fallback.

Each save retains all valid trade data across days, writes and flushes a temporary
file in the same folder, then atomically replaces the readable journal. Readers
can retain a complete old snapshot while the new one is published. Temporary
files are removed after the save. No persistent second version is maintained.
Malformed or incomplete records are ignored without hiding later valid records.
Successfully flushed open records recover the exact trade ID and remaining
quantity after restarting the signal; exits complete those same records.

Deleting a file recreates it on the next record, on the worker. An open trade's
confirmed entries and partial exits are recovered from memory first, preserving
pairing and quantity. A removed directory is recreated too. Closed trades are
not replayed from memory. If the file and the signal's memory are both lost,
its entry cannot be reconstructed; a subsequent confirmed exit is saved as
unknown without fabricating a connector.

File failures keep the queued work and retry once per second off the execution
thread. The signal reports errors through MultiCharts output on a later chart
calculation. Removing the signal does not wait for disk access; queued work can
finish while MultiCharts remains running. Closing the application before a save
finishes can lose unsaved work. Successfully saved records survive reloads and
application restarts. Files are never automatically deleted or rotated.

## Chart annotations and inputs

White horizontal arrowheads have no shafts: entries point right at the recorded
fill candle's left edge, exits point left at the recorded exit candle's right edge,
at their confirmed prices. White vertical arrows point up with their tips one tick below buy-entry lows,
or down with their tips one tick above sell-entry highs, regardless of tail
length. The spacing uses the chart instrument’s tick size. Black labels read `P1 in 100` /
`P1 out 101` or `M2 in 102` / `M2 out 101`; other types use `T`. Green connectors
mean zero/positive price result; red means loss, before fees. Labels stay attached
beside their arrows; dense clusters can overlap. Informational boxes stay hidden
unless enabled.

Vertical entry arrows use the intended setup bar; horizontal entry heads use
the separately recorded fill bar. Their labels and connector endpoints use that
same horizontal anchor. Both snapshots are saved and finalized independently,
including when the intended bar closes before the entry confirmation's bar.
Journal bar placement uses saved timestamps and OHLC, with native tick IDs where
available. Forming snapshots use their preceding completed bar. Missing or
ambiguous identities are not shifted to neighboring candles by a price guess.
Reload the same range and quote side for reliable identity matching. Earlier
detailed records retaining setup/fill calculation numbers can use their relative
bar offset. For earlier compact entry records without fill-bar identity, if the
setup bar cannot contain the fill price but the immediate next bar can, the
horizontal arrow uses that next bar. This is a price-based inference, not a
recovered execution timestamp. Otherwise those records retain their prior
placement. Vertical setup arrows and exit markers do not use this fallback.

| Input | Default | Behavior |
| --- | --- | --- |
| `UseSignalHistory` | true | Read the signal journal; false selects legacy tracker mode. |
| `TradeHistoryDirectory` | `C:\rangebar_entries` | Shared Windows history folder. |
| `TradeHistoryStream` | `default` | Stable signal stream to display. |
| `BrokerProfile`, `AccountFilter` | blank | Optional exact scope filters. |
| `InstrumentOverride` | blank | Defaults to the chart's instrument. |
| `HistoryDate` | blank | Today by confirmation receipt time; or `yyyy-MM-dd`. |
| `HistoryLookbackDays` | 7 | Legacy tracker lookback only. Journal links retain prior entries. |
| `TrackerToChartOffsetMinutes` | 0 | Legacy tracker timestamp offset only. |
| `BarTimestampIsClose` | true | Legacy tracker completion-time interpretation only. |
| `ShowConnections`, `ShowLabels`, `ShowEntryBarArrows` | true | Enable the corresponding drawings. |
| `ShowInfoOverlay` | false | Show the informational status box. |
| `ArrowLengthPixels` | 22 | Vertical arrow length and horizontal head scale. |
| `CandleEdgeOffsetPixels` | 0 | Positive values override candle half-width in pixels. |
| `CandleBodyWidthPercent` | 70 | Estimated candle width as percentage of center spacing. |
| `LabelSize` | 9 | Label text size. |
| `RefreshMilliseconds` | 500 | Scheduled refresh, minimum 100 ms. |

Tracker mode uses `FilledContracts`, `ExecPrice` (order average), and `FinalDT`
(last update). FIFO pairing and timestamp/neighbor-price placement are derived,
not native execution pairing. Old `RangeBuy`/`RangeSell` entries use `T`; old
`pin bar`/`manual` and new buy/sell suffixed names retain P/M labels.

## Development and validation

The codec is `shared/RangeBarJournal.cs`, and the worker is
`shared/RangeBarJournalWriter.cs`. After changing shared code, run
`python3 tools/sync_range_bar_journal.py` to update embedded copies in the
self-contained source files.

Run:

```sh
python3 tools/check_signal_history_isolation.py
python3 tools/check_range_bar_trade_history.py
python3 tools/check_four_tick_pinbar.py
```

Tests cover compact closed summaries with signed long/short tick results,
indicator decoding, separate setup/fill anchors and their finalization,
white horizontal heads and vertical arrows with black labels, partial-exit anchors, unfinished-trade preservation,
single-record consolidation with a missing exit, multiple/stale bar updates, legacy compatibility, partial quantities and weighted results, restart
recovery, duplicate retries, file deletion, concurrent/blocked writes, atomic
replacement, chart drawing, and bid-entry routing when the false input arrives
after order construction. Both studies compile against the installed SDK.
Fixtures use temporary directories and simulated broker callbacks, not live
trades or the real journal folder. The isolation check verifies that committed
inputs and other trading logic remain unchanged outside the approved direction
repair, history hooks, and removal of legacy execution annotations. Live verification requires the running application.

Reference: [MultiCharts .NET Programming Guide, broker fill events](https://download.multicharts.com/downloads/MultiCharts.NET-ProgrammingGuide-v1.1.pdf)
and installed `PLTypes.xml` / `PLStudiesProxy.xml` API documentation.
