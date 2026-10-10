using System;
using System.Collections.Generic;

class SignalExitRules {
    const int SessionProfitTargetTicks = 4, MinimumAutomaticLossTicks = 9;
    public int ProtectiveStopLossTicks;
    public double RangeTicks;
    double GetActiveRangeTicks(double tickSize) { return RangeTicks; }
    // SIGNAL_METHODS
    public int Target(double pnl) { return GetSessionProfitTargetTicks(pnl); }
    public int Stop() { return GetProtectiveStopLossTicks(.25); }
    public bool Opposing(int side, bool closed, double open, double close) {
        return IsOpposingColorClose(side, closed, open, close);
    }
}

class ManualTradingExitTests {
    const int SessionProfitTargetTicks = 4, MinimumAutomaticLossTicks = 9;
    int ProtectiveStopLossTicks;
    double m_TickSize = .25;
    enum EBarState { Inside, Close }
    class BarValue {
        public int Number;
        public DateTime Time;
        public double Open, High, Low, Close;
    }
    class ManualTrade {
        public int EntryBar = 10, Direction, RangeTicks, LastClosedBarProcessed = 10;
        public double EntryPrice = 100, TargetPrice, StopPrice, ExitPrice;
        public bool UseProfitTarget, IsClosed;
    }
    class Cursor { public int Current = 12; }
    class BarData {
        public Cursor FullSymbolData = new Cursor();
        public EBarState Status = EBarState.Inside;
        public double[] Open = {100}, High = {100}, Low = {100}, Close = {100};
        public DateTime[] Time = {DateTime.Today};
    }
    BarData Bars = new BarData();
    Dictionary<int, BarValue> m_CompletedBars = new Dictionary<int, BarValue>();
    int m_FirstCompletedBar = Int32.MaxValue, m_LastCompletedBar = -1;
    ManualTrade m_ActiveTrade;
    int exits;
    void CloseTrade(ManualTrade trade, int barNumber, DateTime time, double price, string reason) {
        Check(!trade.IsClosed, "trade closes only once");
        trade.IsClosed = true;
        trade.ExitPrice = price;
        if (m_ActiveTrade == trade) m_ActiveTrade = null;
        exits++;
    }
    // MANUAL_METHODS

    ManualTrade Trade(int side, double pnl, int range) {
        var trade = new ManualTrade {Direction = side, RangeTicks = range};
        ConfigureTradeExits(trade, pnl);
        return trade;
    }
    static BarValue Bar(int number, int side, double resultTicks, double openTicks,
                        double lowResultTicks, double highResultTicks) {
        double a = 100 + side * lowResultTicks * .25;
        double b = 100 + side * highResultTicks * .25;
        return new BarValue {Number = number, Time = DateTime.Today,
            Open = 100 + side * openTicks * .25, Close = 100 + side * resultTicks * .25,
            Low = Math.Min(a, b), High = Math.Max(a, b)};
    }
    static int checks;
    static void Check(bool condition, string name) {
        checks++;
        if (!condition) throw new Exception(name);
    }
    static void Main() {
        foreach (int side in new[] {1, -1}) {
            var sim = new ManualTradingExitTests();
            var signal = new SignalExitRules();
            foreach (int range in new[] {1, 2, 3, 4, 5, 8, 12}) {
                foreach (int stop in new[] {0, 1, 9, 20}) {
                    sim.ProtectiveStopLossTicks = signal.ProtectiveStopLossTicks = stop;
                    signal.RangeTicks = range;
                    foreach (double pnl in new[] {-100.0, -.01, 0, 100}) {
                        var trade = sim.Trade(side, pnl, range);
                        Check(trade.UseProfitTarget == (signal.Target(pnl) > 0), "signal target mode parity");
                        Check(!trade.UseProfitTarget || trade.TargetPrice == 100 + side * signal.Target(pnl) * .25,
                              "signal fixed target parity across range sizes");
                        Check(trade.StopPrice == 100 - side * signal.Stop() * .25, "signal protective stop parity");
                    }
                }
            }
            sim.ProtectiveStopLossTicks = 0;
            foreach (double result in new[] {-8.0, -1, 0, .0000005, 1, 4, 8}) {
                var trade = sim.Trade(side, -10, 4);
                var bar = Bar(11, side, result, result + 1, Math.Min(result, -8), result + 1);
                Check(IsOpposingColorClose(side, true, bar.Open, bar.Close) ==
                      signal.Opposing(side, true, bar.Open, bar.Close), "signal color parity");
                sim.ProcessCompletedBar(trade, bar);
                Check(trade.IsClosed == (result > 1e-6), "recovery closes only at profitable opposing close");
                if (trade.IsClosed) Check(trade.ExitPrice == bar.Close, "color exit fills at close");
            }
            foreach (bool doji in new[] {false, true}) {
                var trade = sim.Trade(side, -10, 4);
                sim.ProcessCompletedBar(trade, Bar(11, side, 8, doji ? 8 : 7, 0, 8));
                Check(!trade.IsClosed, "profitable same color and doji run beyond fixed target");
            }
            var runner = sim.Trade(side, -10, 4);
            sim.ProcessCompletedBar(runner, Bar(11, side, 8, 0, 0, 8));
            sim.ProcessCompletedBar(runner, Bar(12, side, -1, 0, -8, 0));
            Check(!runner.IsClosed, "earlier profit does not arm a losing color exit");
            sim.ProcessCompletedBar(runner, Bar(13, side, 0, 1, -8, 1));
            Check(!runner.IsClosed, "break-even reversal remains open");
            sim.ProcessCompletedBar(runner, Bar(14, side, 1, 2, 0, 2));
            Check(runner.IsClosed && runner.ExitPrice == 100 + side * .25, "later profitable reversal exits replay");

            foreach (double pnl in new[] {-10.0, 0}) {
                var stopTrade = sim.Trade(side, pnl, 4);
                sim.ProcessCompletedBar(stopTrade, Bar(11, side, 1, 2, -9, 4));
                Check(stopTrade.IsClosed && stopTrade.ExitPrice == stopTrade.StopPrice,
                      "stop precedes target or profitable color on ambiguous historical bar");
                var gap = sim.Trade(side, pnl, 4);
                sim.ProcessCompletedBar(gap, Bar(11, side, -10, -12, -12, -10));
                Check(gap.ExitPrice == 100 - side * 3, "stop gaps use worse open");
            }
            var fixedTrade = sim.Trade(side, 0, 8);
            sim.ProcessCompletedBar(fixedTrade, Bar(11, side, 1, 2, 0, 2));
            Check(!fixedTrade.IsClosed, "fixed mode ignores profitable opposing close");
            sim.ProcessCompletedBar(fixedTrade, Bar(12, side, 4, 1, 1, 4));
            Check(fixedTrade.ExitPrice == 100 + side, "four-tick target on eight-tick range");

            var partial = sim.Trade(side, -10, 4);
            sim.ProcessPartialBar(partial, Bar(11, side, 1, 2, 0, 8));
            Check(!partial.IsClosed, "click replay cannot color-exit forming bar or target recovery");
            sim.ProcessPartialBar(partial, Bar(11, side, 1, 2, -9, 8));
            Check(partial.ExitPrice == partial.StopPrice, "partial replay retains stop");

            sim.m_ActiveTrade = sim.Trade(side, -10, 4);
            var live = sim.m_ActiveTrade;
            sim.Bars.Close[0] = 100 + side * 2;
            sim.CalcBar();
            Check(!live.IsClosed, "live recovery ignores four-tick target");
            sim.Bars.Status = EBarState.Close;
            sim.Bars.Open[0] = 100;
            sim.Bars.Close[0] = 100 - side * .25;
            sim.Bars.High[0] = 100.25;
            sim.Bars.Low[0] = 99.75;
            sim.CalcBar();
            Check(!live.IsClosed, "live completed losing reversal leaves trade open");
            sim.Bars.FullSymbolData.Current++;
            sim.Bars.Open[0] = 100 + side * .5;
            sim.Bars.Close[0] = 100 + side * .25;
            sim.Bars.High[0] = Math.Max(100, sim.Bars.Open[0]);
            sim.Bars.Low[0] = Math.Min(100, sim.Bars.Open[0]);
            sim.CalcBar();
            Check(live.IsClosed && live.ExitPrice == sim.Bars.Close[0], "live profitable reversal closes");
            int exits = sim.exits;
            sim.CalcBar();
            Check(sim.exits == exits, "repeated bar update cannot double exit");
            sim.Bars.Status = EBarState.Inside;
            sim.m_ActiveTrade = sim.Trade(side, -10, 4);
            var liveStop = sim.m_ActiveTrade;
            sim.Bars.Close[0] = 100 - side * 2.5;
            sim.CalcBar();
            Check(liveStop.ExitPrice == sim.Bars.Close[0], "live stop overshoot keeps worse fill");
        }
        Console.WriteLine("PASS: " + checks + " manual exit and signal-parity assertions");
    }
}
