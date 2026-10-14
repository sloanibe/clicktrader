// Verifies legacy cleanup, unfinished trades, folded bar snapshots and restart-safe consolidated records.
static class RangeBarTradeConsolidationTests {
    static int checks;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    static int Count(string text, string value) { return (text.Length - text.Replace(value, "").Length) / value.Length; }
    public static void Run() {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rangebar-consolidation-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try {
            var time = new DateTime(2026, 10, 14, 9, 0, 0);
            string path = RangeBarJournal.FileName(directory, "unfinished");
            var entry = RangeBarJournalTests.Record("unfinished", "entry", 1, 100, time);
            var first = entry.Copy(); first.Kind = "bar"; first.Target = entry.EventId; first.EventId = Guid.NewGuid().ToString("N");
            first.BarTime = time.AddSeconds(1); first.High = 102;
            var final = first.Copy(); final.EventId = Guid.NewGuid().ToString("N"); final.FinalBar = true;
            final.BarTime = time.AddSeconds(2); final.High = 103;
            var stale = first.Copy(); stale.EventId = Guid.NewGuid().ToString("N"); stale.High = 999;
            // A target can be encountered after an update; stale rows must not roll back a final bar.
            System.IO.File.WriteAllText(path, RangeBarJournal.Encode(first) + "\n" + RangeBarJournal.Encode(entry) + "\n" +
                RangeBarJournal.Encode(final) + "\n" + RangeBarJournal.Encode(stale) + "\n");
            RangeBarJournal.Consolidate(path);
            string text = System.IO.File.ReadAllText(path);
            var fills = RangeBarJournal.Read(path);
            int remaining;
            Check(Count(text, "BEGIN RANGE BAR TRADE (RBJ3)") == 1 && !text.Contains("BEGIN RANGE BAR EVENT"),
                "legacy entry and series of updates become one trade record without event blocks");
            Check(text.Contains("Status: OPEN") && text.Contains("Exit time: pending") && text.Contains("Exit quantity: 0"),
                "cleanup never invents an exit for an unfinished trade");
            Check(fills.Count == 1 && fills[0].High == 103 && fills[0].FinalBar && fills[0].Price == 100 && fills[0].Observed == time,
                "update-before-entry and stale observations retain the latest finalized shape without changing fill facts");
            Check(RangeBarJournal.OpenTrade(fills, out remaining).TradeId == entry.TradeId && remaining == 1,
                "one open record retains exact trade identity and quantity for restart");
            Check(fills[0].AppliedUpdates.Contains(first.EventId) && fills[0].AppliedUpdates.Contains(final.EventId) && fills[0].AppliedUpdates.Contains(stale.EventId),
                "folded update identities remain available for retry deduplication");
            RangeBarJournal.Append(path, stale);
            Check(System.IO.File.ReadAllText(path) == text, "replayed folded update is idempotent after reload");
            RangeBarJournal.Consolidate(path);
            Check(System.IO.File.ReadAllText(path) == text, "repeated consolidation does not change existing trade facts");
            string unfinishedText = text;
            var close = RangeBarJournalTests.Record("unfinished", "exit", -1, 101, time.AddSeconds(5)); close.FinalBar = true;
            RangeBarJournal.Append(path, close);
            text = System.IO.File.ReadAllText(path); fills = RangeBarJournal.Read(path);
            Check(Count(text, "BEGIN RANGE BAR TRADE (RBJ4)") == 1 && fills[0].FinalBar && fills[1].FinalBar,
                "exit completes the existing trade rather than creating another event or trade");
            Check(text.Contains(" @ 100.00") && text.Contains(" @ 101.00") &&
                text.Contains(close.Observed.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", System.Globalization.CultureInfo.InvariantCulture)) &&
                text.Contains("Profit: +4 ticks"), "consolidated summary exposes entry/exit prices, final confirmation time and realized ticks");
            Check(text.Contains("Trade: Pin Bar") && text.Contains("Position: LONG - 1 MESZ26") &&
                !text.Contains("Entry 1 ") && !text.Contains("Exit 1 ") && !text.Contains("Previous bar") &&
                !text.Contains("Fill observation") && !text.Contains("Realized result (price points)"),
                "fully closed trade shows a compact human summary without previous-bar or repeated fill detail rows");
            Check(fills[0].EventId == entry.EventId && fills[1].EventId == close.EventId && fills[0].BarTime == final.BarTime &&
                fills[0].PreviousBarTime == DateTime.MinValue && fills[0].AppliedUpdates.Contains(final.EventId),
                "compact chart metadata retains exact identity and final anchors while dropping unnecessary finalized diagnostics");
            string oldClosed = RangeBarJournal.FileName(directory, "old closed");
            System.IO.File.WriteAllText(oldClosed, unfinishedText + RangeBarJournal.Encode(close) + "\n");
            RangeBarJournal.Consolidate(oldClosed);
            Check(System.IO.File.ReadAllText(oldClosed).Contains("BEGIN RANGE BAR TRADE (RBJ4)") &&
                RangeBarJournal.Read(oldClosed).Count == 2, "earlier RBJ3 records with later exit events upgrade to compact closed summaries");
            var view = HistoryMatching.BuildJournal(fills, time.Date);
            Check(view.Pairs.Count == 1 && view.Markers.Count == 2 && view.Pairs[0].Entry.JournalBar.High == 103,
                "indicator gets the same paired fills and explicit finalized entry bar from the consolidated record");
            // Additional legacy rows must not replace authoritative snapshots saved in the .txt record.
            string old = System.IO.Path.ChangeExtension(path, ".rbj");
            System.IO.File.WriteAllText(old, RangeBarJournal.Encode(entry) + "\n" + RangeBarJournal.Encode(first) + "\n");
            fills = RangeBarJournal.Fills(RangeBarJournal.Read(path));
            Check(fills.Count == 2 && fills[0].High == 103 && fills[0].FinalBar, "retained legacy history cannot regress a consolidated bar");

            string partial = RangeBarJournal.FileName(directory, "partial");
            var scaleEntry = RangeBarJournalTests.Record("partial", "entry", -1, 105, time); scaleEntry.Quantity = 3; scaleEntry.FinalBar = true;
            var partialExit = RangeBarJournalTests.Record("partial", "exit", 1, 104, time.AddSeconds(1)); partialExit.FinalBar = true;
            RangeBarJournal.Append(partial, scaleEntry); RangeBarJournal.Append(partial, partialExit);
            text = System.IO.File.ReadAllText(partial);
            Check(text.Contains("Status: PARTIALLY CLOSED") && text.Contains("Remaining quantity: 2") && text.Contains("Exit time: pending"),
                "partial exit leaves a single unfinished record and no fabricated final exit time");
            Check(RangeBarJournal.OpenTrade(RangeBarJournal.Read(partial), out remaining) != null && remaining == 2,
                "consolidated partial close recovers remaining quantity");
            var lastExit = RangeBarJournalTests.Record("partial", "exit", 1, 103, time.AddSeconds(2)); lastExit.Quantity = 2; lastExit.FinalBar = true;
            RangeBarJournal.Append(partial, lastExit);
            text = System.IO.File.ReadAllText(partial);
            Check(Count(text, "BEGIN RANGE BAR TRADE (RBJ4)") == 1 && Count(text, ";out=") == 2 &&
                text.Contains("Profit: +20 ticks"), "partial fills remain losslessly inside one closed short trade with correct realized result");
            Check(RangeBarJournal.Read(partial).Count == 3 && HistoryMatching.BuildJournal(RangeBarJournal.Read(partial), time.Date).Pairs.Count == 2,
                "consolidation preserves individual partial fill prices and chart connections");
            string malformed = RangeBarJournal.FileName(directory, "malformed");
            System.IO.File.WriteAllText(malformed, "BEGIN RANGE BAR TRADE (RBJ3)\n  Trade ID: \"incomplete\"\nEND RANGE BAR TRADE\n" + text);
            Check(RangeBarJournal.Read(malformed).Count == 3, "malformed consolidated record does not hide later valid trades");
            System.IO.File.WriteAllText(malformed, text.Substring(0, text.IndexOf("END RANGE BAR TRADE")));
            Check(RangeBarJournal.Read(malformed).Count == 0, "incomplete trade record is not mistaken for committed entry or exit fills");
            foreach (int side in new[] {1, -1}) foreach (int change in new[] {-1, 0, 1}) {
                string resultPath = RangeBarJournal.FileName(directory, "result " + side + " " + change);
                var opening = RangeBarJournalTests.Record("result", "entry", side, 100, time); opening.Setup = "manual"; opening.FinalBar = true;
                var closing = RangeBarJournalTests.Record("result", "exit", -side, 100 + side * change * .5, time.AddSeconds(1)); closing.Setup = "manual"; closing.FinalBar = true;
                RangeBarJournal.Append(resultPath, opening); RangeBarJournal.Append(resultPath, closing);
                string signed = change > 0 ? "+2" : change < 0 ? "-2" : "0";
                string resultText = System.IO.File.ReadAllText(resultPath);
                Check(resultText.Contains("Profit: " + signed + " ticks (before fees)") && resultText.Contains("Trade: Manual"),
                    "compact long/short summaries report positive, negative and break-even tick results");
                var resultView = HistoryMatching.BuildJournal(RangeBarJournal.Read(resultPath), time.Date);
                Check(resultView.Pairs.Count == 1 && HistoryMatching.PriceResult(resultView.Pairs[0]) == change * .5,
                    "indicator keeps original entry/exit direction and result from compact summaries");
            }
            string formingPath = RangeBarJournal.FileName(directory, "forming closed");
            var formingEntry = RangeBarJournalTests.Record("forming", "entry", 1, 100, time);
            formingEntry.PreviousBarTime = time.AddSeconds(-5); formingEntry.PreviousOpen = 98;
            formingEntry.PreviousHigh = 100; formingEntry.PreviousLow = 98; formingEntry.PreviousClose = 100;
            var formingExit = RangeBarJournalTests.Record("forming", "exit", -1, 100.5, time.AddSeconds(1));
            RangeBarJournal.Append(formingPath, formingEntry); RangeBarJournal.Append(formingPath, formingExit);
            var formingFills = RangeBarJournal.Read(formingPath);
            Check(System.IO.File.ReadAllText(formingPath).Contains("BEGIN RANGE BAR TRADE (RBJ4)") &&
                !formingFills[0].FinalBar && formingFills[0].PreviousBarTime == formingEntry.PreviousBarTime &&
                formingFills[0].PreviousOpen == 98, "compact fully closed trade retains forming-bar identity until its final update arrives");
            var formingUpdate = formingEntry.Copy(); formingUpdate.Kind = "bar"; formingUpdate.Target = formingEntry.EventId;
            formingUpdate.EventId = Guid.NewGuid().ToString("N"); formingUpdate.FinalBar = true; formingUpdate.BarTime = time.AddSeconds(2);
            RangeBarJournal.Append(formingPath, formingUpdate);
            formingFills = RangeBarJournal.Read(formingPath);
            Check(formingFills[0].FinalBar && formingFills[0].BarTime == formingUpdate.BarTime &&
                formingFills[0].PreviousBarTime == DateTime.MinValue && formingFills[1].EventId == formingExit.EventId,
                "final update removes obsolete forming identity without changing the other compact fill");
            string beforeReplay = System.IO.File.ReadAllText(formingPath);
            RangeBarJournal.Append(formingPath, formingUpdate);
            Check(System.IO.File.ReadAllText(formingPath) == beforeReplay, "compact applied update metadata deduplicates retries after restart");
            var shortSnapshot = RangeBarJournal.Read(partial);
            Check(Count(System.IO.File.ReadAllText(partial), "  Exit:") == 1 && shortSnapshot.Count == 3,
                "multiple partial exits have one human exit summary while indicator preserves all actual fill prices");
            string anchored = RangeBarJournal.FileName(directory, "separate anchors");
            var staged = RangeBarJournalTests.Record("separate", "entry", 1, 101.25, time); staged.FinalBar = true;
            staged.BarNumber = 10; staged.BarTime = time; staged.High = 101;
            staged.FillAnchor = new RangeBarJournalRecord {BarNumber = 11, BarTime = time.AddSeconds(1),
                Open = 101, High = 102, Low = 101, Close = 102, FinalBar = true, TickId = 12};
            var decodedLegacy = RangeBarJournal.Decode(RangeBarJournal.Encode(staged));
            Check(decodedLegacy.FillAnchor != null && decodedLegacy.FillAnchor.BarNumber == 11 && decodedLegacy.BarNumber == 10,
                "labeled event codec retains both setup and fill-bar identities");
            RangeBarJournal.Append(anchored, staged);
            var openAnchors = RangeBarJournal.Read(anchored);
            Check(openAnchors.Count == 1 && openAnchors[0].FillAnchor.TickId == 12 && openAnchors[0].FillAnchor.High == 102,
                "open detailed record preserves actual fill-bar timestamp, normalized tick ID and shape");
            var anchorClose = RangeBarJournalTests.Record("separate", "exit", -1, 102, time.AddSeconds(2)); anchorClose.FinalBar = true;
            anchorClose.FillAnchor = new RangeBarJournalRecord {BarNumber = 12, BarTime = time.AddSeconds(3),
                Open = 102, High = 103, Low = 102, Close = 103, FinalBar = true, TickId = 4};
            RangeBarJournal.Append(anchored, anchorClose);
            var compactAnchors = RangeBarJournal.Read(anchored);
            Check(System.IO.File.ReadAllText(anchored).Contains("BEGIN RANGE BAR TRADE (RBJ4)") && compactAnchors.Count == 2 &&
                compactAnchors[0].FillAnchor.BarTime == staged.FillAnchor.BarTime && compactAnchors[0].FillAnchor.TickId == 12 &&
                compactAnchors[1].FillAnchor.BarNumber == 12 && compactAnchors[0].BarNumber == 10,
                "closed compact record preserves independent entry and exit fill anchors for the indicator after reload");
            var clone = compactAnchors[0].Copy(); clone.FillAnchor.High = 999;
            Check(compactAnchors[0].FillAnchor.High == 102, "queued bar anchors are independent snapshots, not shared mutable objects");
            string absent = RangeBarJournal.FileName(directory, "absent"); RangeBarJournal.Consolidate(absent);
            Check(!System.IO.File.Exists(absent), "cleanup of an absent history never creates a fake trade file");
        } finally { System.IO.Directory.Delete(directory, true); }
        Console.WriteLine("PASS: " + checks + " single-record trade consolidation assertions");
    }
}
