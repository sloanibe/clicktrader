using System;
using System.Collections.Generic;
using PowerLanguage.Indicator;

// Runs the signal's actual observer methods; the real background writer owns all file I/O and trade recovery.
class RangeBarSignalJournalTests {
    enum EEntrySetup { None, PinBar, ShiftProjection }
    enum EBarState { Inside, Close }
    class Instrument { public string Name = "MESZ26"; public int MinMove = 1, PriceScale = 4; }
    class BarData {
        public int CurrentBar = 10;
        public uint TickIDValue = 0x80000002;
        public EBarState Status = EBarState.Inside;
        public Instrument Info = new Instrument();
        public DateTime[] Time = {new DateTime(2026, 10, 14, 9, 0, 1), new DateTime(2026, 10, 14, 9, 0, 0)};
        public double[] Open = {100, 99}, High = {101, 100}, Low = {100, 99}, Close = {101, 100};
    }
    class EnvironmentData { public bool IsRealTimeCalc = true; }
    class Logger { public List<string> Errors = new List<string>(); public void WriteLine(string text) { Errors.Add(text); } }
    const int OrderQuantity = 1;
    string Profile = "Paper", Account = "A", TradeHistoryStream = "default", TradeHistoryDirectory;
    bool IsAskChart = true, WriteTradeHistory = true;
    int RangeSizeTicks;
    double m_AutoRangeTicks = 4, m_StopPrice = 101.25;
    EEntrySetup m_ActiveEntrySetup = EEntrySetup.PinBar;
    BarData Bars = new BarData(); EnvironmentData Environment = new EnvironmentData(); Logger Output = new Logger();
    RangeBarJournalRecord m_HistoryIntent;
    readonly List<RangeBarJournalRecord> m_HistoryBars = new List<RangeBarJournalRecord>();
    RangeBarJournalWriter m_HistoryWriter = new RangeBarJournalWriter();
    DateTime m_HistoryErrorTime;
    // PRODUCTION_JOURNAL_METHODS
    static int checks;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    string PathForHistory() {
        return RangeBarJournal.FileName(TradeHistoryDirectory, "Paper|A|MESZ26|default|" + (IsAskChart ? "ask" : "bid") + "|4");
    }
    void Idle() { Check(m_HistoryWriter.WaitForIdle(8000), "background writer drains without study-thread waits in production"); }
    public static void Run() {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rangebar-signal-tests-" + Guid.NewGuid().ToString("N"));
        try {
            var signal = new RangeBarSignalJournalTests { TradeHistoryDirectory = folder };
            signal.CaptureTradeHistoryIntent(1);
            Check(!System.IO.Directory.Exists(folder), "staging an entry never reads or creates the journal folder");
            signal.Bars.CurrentBar = 11;
            signal.Bars.Time = new[] {signal.Bars.Time[0].AddSeconds(1), signal.Bars.Time[0], signal.Bars.Time[1]};
            signal.Bars.Open = new[] {101d, 100d, 99d}; signal.Bars.High = new[] {102d, 101d, 100d};
            signal.Bars.Low = new[] {101d, 100d, 99d}; signal.Bars.Close = new[] {102d, 101d, 100d};
            signal.OnBrokerStategyOrderFilled(true, 1, 101.25); signal.Idle();
            string path = signal.PathForHistory();
            var records = RangeBarJournal.Fills(RangeBarJournal.Read(path));
            Check(records.Count == 1 && records[0].Setup == "pin bar" && records[0].Kind == "entry", "confirmed pin source persisted by worker");
            Check(records[0].BarNumber == 10 && records[0].FillBarNumber == 11 && records[0].High < records[0].Price,
                "intended pin bar remains distinct from next-bar confirmation price");
            Check(records[0].FinalBar, "next-bar fill finalizes setup snapshot");
            string tradeId = records[0].TradeId;
            var restarted = new RangeBarSignalJournalTests {TradeHistoryDirectory = folder};
            restarted.OnBrokerStategyOrderFilled(false, 1, 102); restarted.Idle();
            records = RangeBarJournal.Fills(RangeBarJournal.Read(path));
            Check(records.Count == 2 && records[1].Kind == "exit" && records[1].TradeId == tradeId && records[1].Setup == "pin bar",
                "worker restores durable entry before correlating a post-restart exit");
            restarted.m_ActiveEntrySetup = EEntrySetup.ShiftProjection;
            restarted.CaptureTradeHistoryIntent(1); restarted.OnBrokerStategyOrderFilled(true, 1, 101.5); restarted.Idle();
            records = RangeBarJournal.Fills(RangeBarJournal.Read(path));
            Check(records[2].Setup == "manual" && records[2].TradeId != tradeId, "manual entry has explicit source and new durable ID");
            restarted.Bars.Status = EBarState.Close; restarted.Bars.Time[0] = restarted.Bars.Time[0].AddSeconds(2);
            restarted.MaintainTradeHistory(); restarted.Idle();
            records = RangeBarJournal.Fills(RangeBarJournal.Read(path));
            Check(records.Count == 3 && records[2].FinalBar && records[2].BarTime == restarted.Bars.Time[0], "bar finalization resolves through original event ID");
            var historical = new RangeBarSignalJournalTests {TradeHistoryDirectory = folder};
            historical.Environment.IsRealTimeCalc = false; historical.CaptureTradeHistoryIntent(1);
            Check(historical.m_HistoryIntent == null, "historical calculation cannot stage a journal intent");
            var shortSignal = new RangeBarSignalJournalTests {TradeHistoryDirectory = folder, IsAskChart = false};
            shortSignal.CaptureTradeHistoryIntent(-1); shortSignal.OnBrokerStategyOrderFilled(false, 1, 99.75);
            shortSignal.OnBrokerStategyOrderFilled(true, 1, 99.25); shortSignal.Idle();
            records = RangeBarJournal.Fills(RangeBarJournal.Read(shortSignal.PathForHistory()));
            Check(records.Count == 2 && records[0].Side == -1 && records[1].Kind == "exit" && records[0].TradeId == records[1].TradeId,
                "back-to-back short callbacks are processed in order without waiting");
            var orphan = new RangeBarSignalJournalTests {TradeHistoryDirectory = folder, TradeHistoryStream = "empty"};
            orphan.OnBrokerStategyOrderFilled(false, 1, 101); orphan.Idle(); orphan.MaintainTradeHistory();
            string orphanPath = RangeBarJournal.FileName(folder, "Paper|A|MESZ26|empty|ask|4");
            var orphanFills = RangeBarJournal.Fills(RangeBarJournal.Read(orphanPath));
            Check(orphan.Output.Errors.Count == 0 && orphanFills.Count == 1 && orphanFills[0].Kind == "exit" && orphanFills[0].Setup == "unknown",
                "missing history on restart records an unknown exit in a new file without an error");
            Check(HistoryMatching.BuildJournal(orphanFills, orphanFills[0].Observed.Date).Pairs.Count == 0,
                "unknown exit never invents an entry connection");
            var pure = new RangeBarSignalJournalTests {TradeHistoryDirectory = folder, m_AutoRangeTicks = 0};
            pure.CaptureTradeHistoryIntent(1);
            Check(pure.m_AutoRangeTicks == 0 && pure.m_StopPrice == 101.25 && pure.m_ActiveEntrySetup == EEntrySetup.PinBar,
                "history snapshot never mutates range measurement, price, or active setup");
            pure.RangeSizeTicks = 8; pure.CaptureTradeHistoryIntent(1);
            Check(pure.m_HistoryIntent.RangeTicks == 8, "history reads configured range override");

            // File deletion during an open trade recovers its recorded context on the worker.
            var deleted = new RangeBarSignalJournalTests {TradeHistoryDirectory = folder, TradeHistoryStream = "delete"};
            string deletedPath = RangeBarJournal.FileName(folder, "Paper|A|MESZ26|delete|ask|4");
            deleted.CaptureTradeHistoryIntent(1); deleted.OnBrokerStategyOrderFilled(true, 1, 101.25); deleted.Idle();
            var beforeDeletion = RangeBarJournal.Fills(RangeBarJournal.Read(deletedPath));
            System.IO.File.Delete(deletedPath);
            deleted.OnBrokerStategyOrderFilled(false, 1, 102); deleted.Idle(); deleted.MaintainTradeHistory();
            var afterDeletion = RangeBarJournal.Fills(RangeBarJournal.Read(deletedPath));
            Check(afterDeletion.Count == 2 && afterDeletion[0].TradeId == beforeDeletion[0].TradeId &&
                afterDeletion[1].TradeId == afterDeletion[0].TradeId && deleted.Output.Errors.Count == 0,
                "deleted file recreates the open entry and correlated exit without an error");
            Check(HistoryMatching.BuildJournal(afterDeletion, afterDeletion[0].Observed.Date).Pairs.Count == 1,
                "indicator still draws the matched trade after file recreation");
            System.IO.File.Delete(deletedPath);
            deleted.CaptureTradeHistoryIntent(1); deleted.OnBrokerStategyOrderFilled(true, 1, 101.75); deleted.Idle();
            afterDeletion = RangeBarJournal.Fills(RangeBarJournal.Read(deletedPath));
            Check(afterDeletion.Count == 1 && afterDeletion[0].Price == 101.75 && deleted.m_HistoryWriter.TakeError() == null,
                "deleting a closed trade history starts a new file without replaying closed trades");
            System.IO.File.Delete(deletedPath);
            deleted.Bars.Status = EBarState.Close; deleted.Bars.High[0] = 101.5;
            deleted.MaintainTradeHistory(); deleted.Idle();
            afterDeletion = RangeBarJournal.Fills(RangeBarJournal.Read(deletedPath));
            Check(afterDeletion.Count == 1 && afterDeletion[0].FinalBar && afterDeletion[0].High == 101.5,
                "bar-finalization append recreates deleted history with its entry target intact");
            // Folder removal is also recovered by the normal off-thread directory creation.
            string removedFolder = System.IO.Path.Combine(folder, "removed");
            var removed = new RangeBarSignalJournalTests {TradeHistoryDirectory = removedFolder};
            removed.CaptureTradeHistoryIntent(1); removed.OnBrokerStategyOrderFilled(true, 1, 101.25); removed.Idle();
            System.IO.Directory.Delete(removedFolder, true);
            removed.OnBrokerStategyOrderFilled(false, 1, 102); removed.Idle();
            Check(RangeBarJournal.Fills(RangeBarJournal.Read(removed.PathForHistory())).Count == 2 && removed.m_HistoryWriter.TakeError() == null,
                "deleted folder and history are recreated off-thread without reporting an error");
            // Partial exits must be retained too, or restart would restore the wrong quantity.
            var partial = new RangeBarSignalJournalTests {TradeHistoryDirectory = folder, TradeHistoryStream = "partial delete"};
            string partialPath = RangeBarJournal.FileName(folder, "Paper|A|MESZ26|partial%20delete|ask|4");
            partial.CaptureTradeHistoryIntent(1); partial.OnBrokerStategyOrderFilled(true, 3, 101.25);
            partial.OnBrokerStategyOrderFilled(false, 1, 101.5); partial.Idle();
            System.IO.File.Delete(partialPath);
            partial.OnBrokerStategyOrderFilled(false, 1, 101.75); partial.Idle();
            int partialRemaining;
            var openPartial = RangeBarJournal.OpenTrade(RangeBarJournal.Read(partialPath), out partialRemaining);
            Check(openPartial != null && partialRemaining == 1 && RangeBarJournal.Fills(RangeBarJournal.Read(partialPath)).Count == 3,
                "recreated history retains partial closes and correct remaining quantity");
            var partialRestart = new RangeBarSignalJournalTests {TradeHistoryDirectory = folder, TradeHistoryStream = "partial delete"};
            partialRestart.OnBrokerStategyOrderFilled(false, 1, 102); partialRestart.Idle();
            Check(RangeBarJournal.OpenTrade(RangeBarJournal.Read(partialPath), out partialRemaining) == null && partialRemaining == 0,
                "restart after file deletion recovers partial quantity and closes the correct trade");

            // Block the actual sink. Broker callbacks and bar calculations must still return.
            var entered = new System.Threading.ManualResetEvent(false);
            var release = new System.Threading.ManualResetEvent(false);
            int studyThread = System.Threading.Thread.CurrentThread.ManagedThreadId, workerThread = studyThread;
            var blocked = new RangeBarSignalJournalTests {TradeHistoryDirectory = folder, TradeHistoryStream = "blocked"};
            blocked.m_HistoryWriter = new RangeBarJournalWriter(delegate(string file, RangeBarJournalRecord record) {
                workerThread = System.Threading.Thread.CurrentThread.ManagedThreadId; entered.Set();
                if (!release.WaitOne(8000)) throw new Exception("test failed to release disk writer");
                RangeBarJournal.Append(file, record);
            });
            try {
                blocked.CaptureTradeHistoryIntent(1); blocked.OnBrokerStategyOrderFilled(true, 1, 101.25);
                Check(entered.WaitOne(2000), "disk sink reached on worker");
                blocked.OnBrokerStategyOrderFilled(false, 1, 102); blocked.MaintainTradeHistory();
                Check(workerThread != studyThread && blocked.m_HistoryWriter.PendingCount >= 2,
                    "blocked disk cannot block callback or chart calculation; later events remain queued");
            } finally { release.Set(); blocked.Idle(); entered.Dispose(); release.Dispose(); }
            // Fail after a durable append: idempotent retry must not consume position quantity twice.
            int calls = 0;
            var retrySignal = new RangeBarSignalJournalTests {TradeHistoryDirectory = folder, TradeHistoryStream = "retry"};
            retrySignal.m_HistoryWriter = new RangeBarJournalWriter(delegate(string file, RangeBarJournalRecord record) {
                RangeBarJournal.Append(file, record);
                if (++calls == 1) throw new System.IO.IOException("simulated interrupted flush");
            });
            retrySignal.CaptureTradeHistoryIntent(1); retrySignal.OnBrokerStategyOrderFilled(true, 1, 101.25);
            retrySignal.OnBrokerStategyOrderFilled(false, 1, 102); retrySignal.Idle();
            string retryPath = RangeBarJournal.FileName(folder, "Paper|A|MESZ26|retry|ask|4");
            records = RangeBarJournal.Fills(RangeBarJournal.Read(retryPath));
            int remaining;
            Check(records.Count == 2 && RangeBarJournal.OpenTrade(RangeBarJournal.Read(retryPath), out remaining) == null && remaining == 0,
                "off-thread retry after interrupted flush neither duplicates fills nor double-counts quantity");
        } finally { if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true); }
        Console.WriteLine("PASS: " + checks + " production signal-journal lifecycle assertions");
    }
}
