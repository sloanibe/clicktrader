// Production daily writer/store tests use temporary files only.
static class RangeBarDailyJournalTests {
    static int checks;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    static void Send(RangeBarJournalWriter writer, string directory, RangeBarJournalRecord record) {
        writer.Enqueue(directory, record);
        Check(writer.WaitForIdle(8000) && writer.TakeError() == null, "daily writer drains without errors");
    }
    public static void Run() {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rangebar-daily-" + Guid.NewGuid().ToString("N"));
        try {
            DateTime day = new DateTime(2026, 10, 14);
            string path = RangeBarJournal.FileName(directory, "Paper|A|MESZ26|test|ask|4");
            string first = RangeBarJournal.DailyFileName(path, day), second = RangeBarJournal.DailyFileName(path, day.AddDays(1));
            var entry = RangeBarJournalTests.Record("overnight", "entry", 1, 100, day.AddDays(1).AddSeconds(-1));
            var writer = new RangeBarJournalWriter();
            Send(writer, directory, entry);
            Check(System.IO.File.Exists(first) && !System.IO.File.Exists(path) && !System.IO.File.Exists(second),
                "fresh trade creates only its entry-date file");
            var exit = RangeBarJournalTests.Record("unassigned", "exit", -1, 101, day.AddDays(1).AddSeconds(1));
            writer = new RangeBarJournalWriter(); // restart after midnight while still in position
            Send(writer, directory, exit);
            var fills = RangeBarJournal.Fills(RangeBarJournal.Read(first));
            Check(fills.Count == 2 && fills[1].TradeId == entry.TradeId && !System.IO.File.Exists(second),
                "post-midnight exit restores and closes prior-day trade in its original file");
            Check(HistoryMatching.BuildJournal(fills, day.AddDays(1)).Markers.Count > 0,
                "indicator still finds midnight-crossing exit inside preceding-day file");
            var next = RangeBarJournalTests.Record("today", "entry", 1, 102, day.AddDays(1).AddHours(1));
            Send(writer, directory, next);
            Check(System.IO.File.Exists(second) && RangeBarJournal.Read(second).Count == 1, "next trade starts new date file");
            var update = entry.Copy(); update.Kind = "bar"; update.Target = entry.EventId;
            update.EventId = Guid.NewGuid().ToString("N"); update.Observed = day.AddDays(1).AddHours(2);
            update.FinalBar = true; update.BarTime = entry.BarTime.AddSeconds(2); update.High = 103;
            Send(writer, directory, update);
            Check(RangeBarJournal.Read(first)[0].FinalBar && RangeBarJournal.Read(first)[0].High == 103 &&
                RangeBarJournal.Read(second).Count == 1, "late closed-trade bar update returns to original date");
            System.IO.File.Delete(first);
            Send(writer, directory, update);
            Check(RangeBarJournal.Read(first).Count == 0 && RangeBarJournal.Read(second).Count == 1,
                "late deleted closed-trade update never replays another day's open trade into its file");
            // The update alone cannot reconstruct a deleted closed trade; open-trade recovery can.
            System.IO.File.Delete(second);
            var nextExit = RangeBarJournalTests.Record("exit", "exit", -1, 103, day.AddDays(2));
            Send(writer, directory, nextExit);
            Check(RangeBarJournal.Read(second).Count == 2, "deleted open-trade day recreated and paired even next day");
            var allDays = new List<RangeBarJournalRecord>();
            foreach (string file in RangeBarJournal.StreamFiles(path)) allDays.AddRange(RangeBarJournal.Read(file));
            // Restore the deleted closed trade only in this isolated test fixture.
            allDays.Add(entry); exit.TradeId = entry.TradeId; allDays.Add(exit);
            var multiDay = HistoryMatching.BuildJournal(RangeBarJournal.Fills(allDays), DateTime.MinValue, DateTime.MaxValue);
            Check(multiDay.Markers.Count == 4 && multiDay.Pairs.Count == 2,
                "blank-date model includes every day and keeps both cross-midnight trades paired");
            foreach (var pair in multiDay.Pairs)
                Check(pair.Entry.JournalBar.TradeId == pair.Exit.JournalBar.TradeId,
                    "multi-day view never connects unrelated daily trades");
            var singleDay = HistoryMatching.BuildJournal(RangeBarJournal.Fills(allDays), day.AddDays(2));
            Check(singleDay.Pairs.Count == 1 && singleDay.Markers.Count == 2,
                "explicit date still selects its exit and correlated prior-day entry");
            // Migrate a mixed-day legacy file, keeping one midnight-crossing trade together.
            string legacy = RangeBarJournal.FileName(System.IO.Path.Combine(directory, "migration"), "migration");
            RangeBarJournal.Append(legacy, entry);
            exit.TradeId = entry.TradeId; RangeBarJournal.Append(legacy, exit);
            RangeBarJournal.Append(legacy, next);
            RangeBarJournal.SplitDaily(legacy);
            string migratedFirst = RangeBarJournal.DailyFileName(legacy, entry.Observed);
            string migratedSecond = RangeBarJournal.DailyFileName(legacy, next.Observed);
            Check(!System.IO.File.Exists(legacy) && RangeBarJournal.Read(migratedFirst).Count == 2 &&
                RangeBarJournal.Read(migratedSecond).Count == 1, "legacy file split by entry day with all fills preserved");
            string snapshot = System.IO.File.ReadAllText(migratedFirst);
            RangeBarJournal.SplitDaily(legacy);
            Check(System.IO.File.ReadAllText(migratedFirst) == snapshot, "migration is idempotent");
            // Interrupted migration: some daily data exists, but original still present.
            RangeBarJournal.Append(legacy, entry); RangeBarJournal.Append(legacy, exit);
            RangeBarJournal.SplitDaily(legacy);
            Check(RangeBarJournal.Read(migratedFirst).Count == 2, "partial migration replay does not duplicate fills");
            string malformed = RangeBarJournal.FileName(System.IO.Path.Combine(directory, "migration"), "malformed migration");
            string torn = RangeBarJournal.Encode(entry) + "\nBEGIN RANGE BAR EVENT (RBJ2)\n";
            System.IO.File.WriteAllText(malformed, torn);
            bool rejected = false;
            try { RangeBarJournal.SplitDaily(malformed); } catch (System.IO.IOException) { rejected = true; }
            Check(rejected && System.IO.File.ReadAllText(malformed) == torn,
                "incomplete migration source stays intact for recovery");
            string blocked = RangeBarJournal.FileName(System.IO.Path.Combine(directory, "blocked"), "blocked migration");
            RangeBarJournal.Append(blocked, entry); RangeBarJournal.Append(blocked, next);
            string blockedDay = RangeBarJournal.DailyFileName(blocked, next.Observed);
            RangeBarJournal.Append(blockedDay, next);
            string original = System.IO.File.ReadAllText(blocked);
            using (var file = new System.IO.FileStream(blockedDay, System.IO.FileMode.Open,
                System.IO.FileAccess.ReadWrite, System.IO.FileShare.None)) {
                rejected = false;
                try { RangeBarJournal.SplitDaily(blocked); } catch (System.IO.IOException) { rejected = true; }
                Check(rejected && System.IO.File.ReadAllText(blocked) == original,
                    "failed daily save preserves complete original until migration can finish");
            }
            RangeBarJournal.SplitDaily(blocked);
            Check(!System.IO.File.Exists(blocked) && RangeBarJournal.Read(blockedDay).Count == 1 &&
                RangeBarJournal.Read(RangeBarJournal.DailyFileName(blocked, entry.Observed)).Count == 1,
                "retry completes partially saved migration without duplication");
            Check(RangeBarJournal.HistoryFiles(directory).Length == 2 &&
                RangeBarJournal.StreamFiles(path).Length == 2, "dated files discoverable without including temp files");
            string sharedDirectory = System.IO.Path.Combine(directory, "shared");
            var writers = new List<RangeBarJournalWriter>(); var entries = new List<RangeBarJournalRecord>();
            for (int i = 0; i < 6; i++) {
                var opening = RangeBarJournalTests.Record("shared-" + i, "entry", i % 2 == 0 ? 1 : -1, 100, day.AddHours(9));
                if (i == 1) opening.Role = "bid";
                if (i == 2) opening.Account = "B";
                if (i == 3) opening.Profile = "Live";
                if (i == 4) opening.Stream = "other";
                if (i == 5) { opening.Symbol = "NQZ26"; opening.RangeTicks = 8; }
                entries.Add(opening); var worker = new RangeBarJournalWriter(); writers.Add(worker);
                worker.Enqueue(sharedDirectory, opening);
            }
            foreach (var worker in writers) Check(worker.WaitForIdle(8000) && worker.TakeError() == null,
                "concurrent scopes append to shared daily file without errors");
            string sharedPath = RangeBarJournal.DailyFileName(System.IO.Path.Combine(sharedDirectory, "ignored.txt"), day);
            Check(System.IO.Path.GetFileName(sharedPath) == "RangeBar--history-2026-10-14.txt" &&
                RangeBarJournal.HistoryFiles(sharedDirectory).Length == 1 && RangeBarJournal.Read(sharedPath).Count == 6,
                "all chart/account scopes share the requested human-friendly date file");
            var restartedWriter = new RangeBarJournalWriter();
            var ownUpdate = entries[0].Copy(); ownUpdate.Kind = "bar"; ownUpdate.Target = entries[0].EventId;
            ownUpdate.EventId = Guid.NewGuid().ToString("N"); ownUpdate.FinalBar = true;
            Send(restartedWriter, sharedDirectory, ownUpdate);
            writers[0] = restartedWriter;
            Check(RangeBarJournal.Read(sharedPath).Find(delegate(RangeBarJournalRecord r) {
                return r.EventId == entries[0].EventId; }).FinalBar,
                "restart recovers its own open trade while five other scopes also have open positions");
            System.IO.File.Delete(sharedPath);
            for (int i = 0; i < entries.Count; i++) {
                var closing = entries[i].Copy(); closing.Kind = "exit"; closing.Side = -closing.Side;
                closing.EventId = Guid.NewGuid().ToString("N"); closing.TradeId = "callback-unassigned";
                closing.Observed = day.AddHours(10);
                // Each live worker retains only its own open entry after deletion.
                Send(writers[i], sharedDirectory, closing);
            }
            Check(RangeBarJournal.Read(sharedPath).Count == 12,
                "each writer recovers its own missing entry even after another chart recreates the shared file");
            var sharedView = HistoryMatching.BuildJournal(RangeBarJournal.Read(sharedPath), day);
            Check(sharedView.Pairs.Count == 6, "shared-file trades remain paired in indicator");
            foreach (var pair in sharedView.Pairs) Check(
                RangeBarJournal.ScopeKey(pair.Entry.JournalBar) == RangeBarJournal.ScopeKey(pair.Exit.JournalBar),
                "shared history never pairs different accounts or chart sides");
            var legacyEntry = entries[0].Copy(); legacyEntry.Account = "old scope"; legacyEntry.TradeId = "old dated trade";
            legacyEntry.EventId = Guid.NewGuid().ToString("N");
            string oldBase = RangeBarJournal.FileName(sharedDirectory, RangeBarJournal.ScopeKey(legacyEntry));
            string oldDated = System.IO.Path.Combine(sharedDirectory,
                System.IO.Path.GetFileNameWithoutExtension(oldBase) + "-2026-10-14.txt");
            RangeBarJournal.Append(oldDated, legacyEntry);
            var cleanup = legacyEntry.Copy(); cleanup.Kind = "consolidate";
            Send(new RangeBarJournalWriter(), sharedDirectory, cleanup);
            Check(!System.IO.File.Exists(oldDated) && RangeBarJournal.Read(sharedPath).Count == 13,
                "previous hashed daily file merges into friendly daily file without losing other scopes");
            var legacyExit = legacyEntry.Copy(); legacyExit.Kind = "exit"; legacyExit.Side = -legacyExit.Side;
            legacyExit.EventId = Guid.NewGuid().ToString("N");
            Send(new RangeBarJournalWriter(), sharedDirectory, legacyExit);
            Check(RangeBarJournal.Read(sharedPath).FindAll(delegate(RangeBarJournalRecord r) {
                return r.TradeId == legacyEntry.TradeId; }).Count == 2,
                "restart restores only matching scope from shared daily file");
        } finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
        Console.WriteLine("PASS: " + checks + " daily history rotation/migration assertions");
    }
}
