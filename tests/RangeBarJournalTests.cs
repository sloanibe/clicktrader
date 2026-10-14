// Exercises the production codec/store and explicit trade-ID matching in a temporary directory.
static class RangeBarJournalTests {
    static int checks;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    internal static RangeBarJournalRecord Record(string trade, string kind, int side, double price, DateTime time) {
        return new RangeBarJournalRecord {TradeId = trade, Kind = kind, Setup = "pin bar", Profile = "Paper", Account = "A",
            Symbol = "MESZ26", Stream = "test", Role = "ask", Side = side, Quantity = 1, Price = price,
            Observed = time, BarTime = time.AddSeconds(-2), FillBarTime = time, FillBarNumber = 11, BarNumber = 10,
            SubmittedPrice = price - .25, TickSize = .25, RangeTicks = 4, Open = 100, High = 101, Low = 100, Close = 101};
    }
    public static void Run() {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rangebar-journal-tests-" + Guid.NewGuid().ToString("N"));
        try {
            var day = new DateTime(2026, 10, 14);
            var entry = Record("pin", "entry", 1, 101.25, day.AddHours(9));
            entry.Account = "A\tB\nC%|λ";
            var copy = RangeBarJournal.Decode(RangeBarJournal.Encode(entry));
            Check(copy.Account == entry.Account && copy.Price == 101.25 && copy.BarNumber == 10 && copy.FillBarNumber == 11,
                "escaped strings and intended/fill bars round trip independently");
            Check(copy.BarTime != copy.FillBarTime && copy.SubmittedPrice != copy.Price, "actual confirmation price independent from submission and bar");
            string readable = RangeBarJournal.Encode(entry);
            Check(readable.Contains("Trade type: \"pin bar\"") && readable.Contains("Action: BUY") &&
                readable.Contains("Confirmed price: 101.25") && readable.Contains("Annotation bar number: 10"),
                "text contains understandable labeled trade type, side, price and intended bar");
            var escaped = entry.Copy(); escaped.Stream = "quotes \" and slash \\ and unicode λ\nEND RANGE BAR EVENT";
            Check(RangeBarJournal.Decode(RangeBarJournal.Encode(escaped)).Stream == escaped.Stream,
                "literal unicode and escaped quotes/newlines cannot corrupt block boundaries");
            string path = RangeBarJournal.FileName(directory, "scope");
            Check(path.EndsWith(".txt"), "new histories have a text-editor-friendly extension");
            RangeBarJournal.Append(path, entry); RangeBarJournal.Append(path, entry);
            string contents = System.IO.File.ReadAllText(path);
            Check(contents.StartsWith("RangeBar trade history") && contents.Contains("Status: OPEN"),
                "saved file has a title and consolidated open trade");
            var encodeLegacy = typeof(RangeBarJournal).GetMethod("EncodeLegacy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            string migration = RangeBarJournal.FileName(directory, "migration");
            string oldFile = System.IO.Path.ChangeExtension(migration, ".rbj");
            string oldRow = (string)encodeLegacy.Invoke(null, new object[] {entry}) + "\n";
            System.IO.File.WriteAllText(oldFile, oldRow);
            Check(RangeBarJournal.Read(migration).Count == 1, "new path restores legacy history before first append");
            RangeBarJournal.Append(migration, entry);
            Check(System.IO.File.ReadAllText(migration).Contains("Trade type: \"pin bar\"") &&
                System.IO.File.ReadAllText(oldFile) == oldRow && RangeBarJournal.Read(migration).Count == 1,
                "conversion creates a readable copy without altering old rows or duplicating events");
            Check(RangeBarJournal.HistoryFiles(directory).Length == 2, "paired legacy file is not separately enumerated");
            var laterLegacy = entry.Copy(); laterLegacy.EventId = Guid.NewGuid().ToString("N"); laterLegacy.TradeId = "legacy arrived later";
            System.IO.File.AppendAllText(oldFile, (string)encodeLegacy.Invoke(null, new object[] {laterLegacy}) + "\n");
            RangeBarJournal.Append(migration, laterLegacy);
            Check(System.IO.File.ReadAllText(migration).Contains(laterLegacy.EventId) && RangeBarJournal.Read(migration).Count == 2,
                "converter can copy later legacy appends into existing readable history without duplicates");
            var dayTwo = entry.Copy(); dayTwo.EventId = Guid.NewGuid().ToString("N"); dayTwo.TradeId = "next day";
            dayTwo.Observed = dayTwo.Observed.AddDays(1);
            RangeBarJournal.Append(migration, dayTwo);
            Check(RangeBarJournal.Read(migration).Count == 3 && System.IO.File.ReadAllText(migration).Contains("2026-10-15"),
                "readable file retains earlier days when new days are appended");
            Check(RangeBarJournal.Read(path).Count == 1, "retry of durable event ID is idempotent");
            System.IO.File.AppendAllText(path, "RBJ1\tbroken");
            Check(RangeBarJournal.Read(path).Count == 1, "incomplete tail ignored");
            System.IO.File.AppendAllText(path, "\nBEGIN RANGE BAR EVENT (RBJ2)\n  Event: ENTRY\n");
            Check(RangeBarJournal.Read(path).Count == 1, "incomplete labeled block is ignored");
            var manual = Record("manual", "entry", -1, 105, day.AddHours(9).AddSeconds(1)); manual.Setup = "manual";
            RangeBarJournal.Append(path, manual);
            Check(RangeBarJournal.Read(path).Count == 2, "new append survives torn tail without overwriting history");
            var closeManual = Record("manual", "exit", 1, 104, day.AddHours(9).AddSeconds(2)); closeManual.Setup = "manual";
            var closePin = Record("pin", "exit", -1, 102, day.AddHours(9).AddSeconds(3)); closePin.FinalBar = true;
            RangeBarJournal.Append(path, closeManual); RangeBarJournal.Append(path, closePin);
            var events = RangeBarJournal.Read(path);
            var view = HistoryMatching.BuildJournal(RangeBarJournal.Fills(events), day);
            Check(view.Pairs.Count == 2 && view.Pairs[0].Entry.Scope == "manual" && view.Pairs[1].Entry.Scope == "pin",
                "overlapping trades pair by explicit trade IDs instead of FIFO");
            Check(view.Markers[0].Label == "P1 in 101.25" && view.Markers[1].Label == "M2 in 105", "explicit source selects P/M labels");
            var update = entry.Copy(); update.EventId = Guid.NewGuid().ToString("N"); update.Kind = "bar";
            update.Target = entry.EventId; update.FinalBar = true; update.BarTime = day.AddHours(9).AddSeconds(5); update.High = 102;
            RangeBarJournal.Append(path, update);
            var finalized = RangeBarJournal.Fills(RangeBarJournal.Read(path));
            Check(finalized.Count == 4 && finalized[0].FinalBar && finalized[0].High == 102 && finalized[0].Price == 101.25,
                "bar finalization changes annotation without changing fill price or duplicating execution");
            string grouped = System.IO.File.ReadAllText(path);
            string rule = new string('=', 80);
            string pinStart = "id=pin;";
            string pinEnd = "Profit: +3 ticks";
            string manualStart = "id=manual;";
            Check(grouped.Contains(rule + "\r\nBEGIN RANGE BAR TRADE (RBJ4)") && grouped.Contains(pinEnd),
                "equal-sign rules mark each single consolidated trade record");
            int pinBegin = grouped.IndexOf(pinStart), pinFinish = grouped.IndexOf("END RANGE BAR TRADE", pinBegin);
            int manualBegin = grouped.IndexOf(manualStart);
            Check(pinBegin < grouped.IndexOf(entry.EventId) && grouped.IndexOf(update.EventId) < pinFinish && pinFinish < manualBegin,
                "late pin update is folded into its trade's metadata before the next trade");
            Check(grouped.IndexOf(closePin.EventId) < pinFinish && grouped.IndexOf(closeManual.EventId) > manualBegin,
                "entry and correlated exit fill details are inside one trade record");
            Check(!grouped.Contains("BEGIN RANGE BAR EVENT") && !grouped.Contains("Event: BAR UPDATE"),
                "consolidated file contains no separate entry, exit or update events");
            var parsedGroups = RangeBarJournal.Read(path);
            Check(parsedGroups.Count == 4 && HistoryMatching.BuildJournal(RangeBarJournal.Fills(parsedGroups), day).Pairs.Count == 2,
                "trade separators add no executions and do not change indicator matching");
            Check(System.IO.Directory.GetFiles(directory, "*.tmp").Length == 0, "atomic group save cleans up its temporary file");
            string openText = System.IO.File.ReadAllText(migration);
            Check(openText.Contains("Trade ID: \"next day\"\r\n  Status: OPEN"), "an open trade is not marked complete");
            string pendingFile = RangeBarJournal.FileName(directory, "pending group");
            var pendingEntry = Record("pending", "entry", 1, 100, day); pendingEntry.FinalBar = true;
            var pendingExit = Record("pending", "exit", -1, 101, day.AddSeconds(1));
            RangeBarJournal.Append(pendingFile, pendingEntry); RangeBarJournal.Append(pendingFile, pendingExit);
            Check(System.IO.File.ReadAllText(pendingFile).Contains("BEGIN RANGE BAR TRADE (RBJ4)") && !RangeBarJournal.Read(pendingFile)[1].FinalBar,
                "closed trade remains pending until its forming exit bar is finalized");
            var pendingUpdate = pendingExit.Copy(); pendingUpdate.Kind = "bar"; pendingUpdate.Target = pendingExit.EventId;
            pendingUpdate.EventId = Guid.NewGuid().ToString("N"); pendingUpdate.FinalBar = true;
            RangeBarJournal.Append(pendingFile, pendingUpdate);
            Check(System.IO.File.ReadAllText(pendingFile).Contains("BEGIN RANGE BAR TRADE (RBJ4)") && RangeBarJournal.Read(pendingFile)[1].FinalBar,
                "last bar update completes the trade group without adding another execution");
            using (var heldReader = new System.IO.FileStream(pendingFile, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete)) {
                string previousText = System.IO.File.ReadAllText(pendingFile);
                var lastUpdate = pendingUpdate.Copy(); lastUpdate.EventId = Guid.NewGuid().ToString("N");
                RangeBarJournal.Append(pendingFile, lastUpdate);
                using (var reader = new System.IO.StreamReader(heldReader))
                    Check(reader.ReadToEnd() == previousText && System.IO.File.ReadAllText(pendingFile).Contains(lastUpdate.EventId),
                        "atomic grouping preserves the prior snapshot for an open reader while publishing the complete new one");
            }
            int remaining;
            Check(RangeBarJournal.OpenTrade(RangeBarJournal.Read(path), out remaining) == null && remaining == 0, "closed trades remain closed after restart");
            string second = RangeBarJournal.FileName(directory, "other scope");
            entry.Quantity = 3; RangeBarJournal.Append(second, entry);
            closePin.Quantity = 1; RangeBarJournal.Append(second, closePin);
            Check(RangeBarJournal.OpenTrade(RangeBarJournal.Read(second), out remaining).TradeId == "pin" && remaining == 2,
                "restart recovers partially closed trade ID and remaining quantity");
            RangeBarJournal.Append(second, manual);
            bool ambiguous = false; try { RangeBarJournal.OpenTrade(RangeBarJournal.Read(second), out remaining); }
            catch (InvalidOperationException) { ambiguous = true; }
            Check(ambiguous, "ambiguous stream restoration fails instead of assigning an exit to a guessed trade");
            var threads = new List<System.Threading.Thread>();
            string concurrent = RangeBarJournal.FileName(directory, "concurrent");
            Exception failure = null;
            for (int i = 0; i < 4; i++) {
                var thread = new System.Threading.Thread(delegate() {
                    try { for (int n = 0; n < 8; n++) RangeBarJournal.Append(concurrent, Record(Guid.NewGuid().ToString("N"), "entry", 1, 100, day)); }
                    catch (Exception ex) { failure = ex; }
                }); threads.Add(thread); thread.Start();
            }
            foreach (var thread in threads) thread.Join();
            Check(failure == null && RangeBarJournal.Read(concurrent).Count == 32, "concurrent writers preserve every complete event");
        } finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
        Console.WriteLine("PASS: " + checks + " persistent trade-journal assertions");
    }
}
