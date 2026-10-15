// Runs actual HUD P&L methods with platform position data stubbed; real files/worker/cache.
class RangeBarDailyPnlTests {
    class Instrument { public string Name = "MESZ26"; public double BigPointValue = 5; }
    class BarData { public Instrument Info = new Instrument(); }
    class StrategyData { public int MarketPosition; }
    class Position { public double OpenProfit; }
    static readonly RangeBarPnlBook s_DayPnl = new RangeBarPnlBook();
    bool m_DayPnlReady;
    bool m_HasFlatSessionPnlSnapshot;
    double m_FlatSessionPnlSnapshot;
    double m_DayRealizedPnl, m_DayOpenPnl, m_AutoRangeTicks = 4;
    int RangeSizeTicks;
    bool IsAskChart = true;
    string TradeHistoryDirectory, Profile = "Paper", Account = "A", TradeHistoryStream = "test", feedback;
    BarData Bars = new BarData(); StrategyData StrategyInfo = new StrategyData(); Position CurrentPosition = new Position();
    RangeBarJournalWriter m_HistoryWriter = new RangeBarJournalWriter();
    void SetClickFeedback(string text) { feedback = text; }
    // PRODUCTION_PNL_METHODS
    static int checks;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    void Loaded() {
        UpdateAndGetGlobalPnL();
        Check(m_HistoryWriter.WaitForIdle(8000), "P&L worker finishes without blocking a production study call");
        UpdateAndGetGlobalPnL();
    }
    public static void Run() {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rangebar-pnl-" + Guid.NewGuid().ToString("N"));
        var signals = new List<RangeBarDailyPnlTests>();
        try {
            DateTime day = DateTime.Now.Date;
            var entry = RangeBarJournalTests.Record("overnight-pnl", "entry", 1, 100, day.AddMinutes(-2)); entry.Quantity = 3;
            var oldExit = RangeBarJournalTests.Record(entry.TradeId, "exit", -1, 101, day.AddMinutes(-1));
            var partial = RangeBarJournalTests.Record(entry.TradeId, "exit", -1, 99.5, day.AddHours(1));
            var shortEntry = RangeBarJournalTests.Record("short-pnl", "entry", -1, 102, day.AddHours(2));
            shortEntry.Role = "bid"; shortEntry.Quantity = 2;
            var shortExit = shortEntry.Copy(); shortExit.EventId = Guid.NewGuid().ToString("N"); shortExit.Kind = "exit";
            shortExit.Side = 1; shortExit.Price = 101; shortExit.Observed = day.AddHours(3);
            var events = new List<RangeBarJournalRecord> {entry, oldExit, partial, shortEntry, shortExit};
            var unknown = RangeBarJournalTests.Record("unmatched-pnl", "exit", -1, 200, day.AddHours(4)); events.Add(unknown);
            var other = RangeBarJournalTests.Record("other-account", "entry", 1, 100, day.AddHours(1)); other.Account = "B";
            var otherExit = other.Copy(); otherExit.EventId = Guid.NewGuid().ToString("N"); otherExit.Kind = "exit";
            otherExit.Side = -1; otherExit.Price = 200;
            events.Add(other); events.Add(otherExit);
            var filter = entry.Copy(); filter.Observed = day;
            Check(RangeBarJournalWriter.RealizedPoints(events, filter) == 1.5,
                "today counts quantity-weighted partial long loss and short win, excludes yesterday and unrelated account/unknown exit");
            events.Add(entry.Copy()); events.Add(partial.Copy());
            Check(RangeBarJournalWriter.RealizedPoints(events, filter) == 1.5, "duplicate files/fills cannot double-count daily profit");
            foreach (var record in events) {
                string file = RangeBarJournal.DailyFileName(System.IO.Path.Combine(directory, "scope.txt"),
                    record.TradeId == entry.TradeId ? entry.Observed : record.Observed);
                RangeBarJournal.Append(file, record);
            }
            var ask = new RangeBarDailyPnlTests {TradeHistoryDirectory = directory}; signals.Add(ask);
            Check(!ask.IsDailyPnlEntryReady() && ask.feedback.Contains("LOADING DAY PNL"),
                "new entry waits for restored history rather than assuming zero after restart");
            ask.Loaded();
            Check(ask.m_DayPnlReady && ask.m_DayRealizedPnl == 7.5 && ask.m_DayOpenPnl == 0,
                "HUD restores realized points as dollars using instrument point value");
            Check(ask.IsDailyPnlEntryReady() && ask.m_HasFlatSessionPnlSnapshot && ask.m_FlatSessionPnlSnapshot == 7.5,
                "entry-time profit rules latch fresh persisted total after initial background load");
            ask.StrategyInfo.MarketPosition = 1; ask.CurrentPosition.OpenProfit = 2.5;
            Check(ask.UpdateAndGetGlobalPnL() == 10 && ask.m_DayOpenPnl == 2.5, "live position gain updates without disk refresh");
            ask.CurrentPosition.OpenProfit = -5;
            Check(ask.UpdateAndGetGlobalPnL() == 2.5, "live position loss updates immediately");
            var bid = new RangeBarDailyPnlTests {TradeHistoryDirectory = directory, IsAskChart = false}; signals.Add(bid);
            bid.StrategyInfo.MarketPosition = -1; bid.CurrentPosition.OpenProfit = -1.25; bid.Loaded();
            Check(bid.UpdateAndGetGlobalPnL() == 1.25 && bid.m_DayRealizedPnl == 7.5 && bid.m_DayOpenPnl == -6.25,
                "ask and bid open P&L combine while daily realized base is counted once");
            var duplicate = new RangeBarDailyPnlTests {TradeHistoryDirectory = directory}; signals.Add(duplicate);
            duplicate.StrategyInfo.MarketPosition = 1; duplicate.CurrentPosition.OpenProfit = -5; duplicate.Loaded();
            Check(duplicate.UpdateAndGetGlobalPnL() == 1.25, "duplicate chart scope does not double-count open P&L");
            duplicate.RemoveGlobalPnLContributor(); ask.RemoveGlobalPnLContributor();
            var reloaded = new RangeBarDailyPnlTests {TradeHistoryDirectory = directory}; signals.Add(reloaded); reloaded.Loaded();
            Check(reloaded.m_DayRealizedPnl == 7.5, "disabling and reloading a signal never resets persisted realized day P&L");
            var otherSignal = new RangeBarDailyPnlTests {TradeHistoryDirectory = directory, Account = "B"}; signals.Add(otherSignal); otherSignal.Loaded();
            Check(otherSignal.UpdateAndGetGlobalPnL() == 500, "separate accounts keep separate currency totals");
            // A queued close invalidates the old day snapshot until the worker includes it.
            var close = RangeBarJournalTests.Record("unassigned", "exit", -1, 100.5, day.AddHours(5));
            reloaded.m_HistoryWriter.Enqueue(directory, close);
            Check(!reloaded.IsDailyPnlEntryReady(), "new fill cannot reuse a stale P&L snapshot for entry management");
            reloaded.Loaded();
            Check(reloaded.m_DayRealizedPnl == 10, "remaining overnight lot realized today joins the saved base after close");
            var book = new RangeBarPnlBook(); var owner = new object();
            var snapshot = new RangeBarDayPnl {Day = day, RealizedPoints = 2, Version = 2};
            Check(book.Update(owner, "scope", "ask", day, false, 0, 5, snapshot).Total == 10, "book converts realized base once");
            Check(!book.Update(owner, "scope", "ask", day.AddDays(1), false, 0, 5, snapshot).Ready,
                "midnight cannot carry yesterday's realized base into today");
            var tomorrow = new RangeBarDayPnl {Day = day.AddDays(1), RealizedPoints = 0, Version = 3};
            Check(book.Update(owner, "scope", "ask", tomorrow.Day, false, 0, 5, tomorrow).Total == 0,
                "new day begins at its own persisted total");
        } finally {
            foreach (var signal in signals) { signal.m_HistoryWriter.WaitForIdle(8000); signal.RemoveGlobalPnLContributor(); }
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }
        Console.WriteLine("PASS: " + checks + " persisted day P&L / live HUD assertions");
    }
}
