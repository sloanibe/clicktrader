using System;
using System.Drawing;

class SessionProfitRuleTests {
    const int SessionProfitTargetTicks = 4, OneBarProfitTargetTicks = 5;
    enum EBarState { Open, Close }
    enum ETLStyle { ToolDashed }
    class ITrendLineObject {
        public double Price;
        public bool Deleted;
        public void Delete() { Deleted = true; }
    }
    class BarData {
        public int CurrentBar = 11;
        public bool LastBarOnChart = true;
        public EBarState Status = EBarState.Open;
        public double[] Open = {100}, Close = {99.75};
    }
    class StrategyData { public double OpenEquity; }
    class CloseOrder { public int Sends; public void Send() { Sends++; } }
    BarData Bars = new BarData();
    StrategyData StrategyInfo = new StrategyData();
    CloseOrder m_CloseLongNextBar = new CloseOrder(), m_CloseShortNextBar = new CloseOrder();
    bool m_SessionProfitRuleActive = true, m_RecoveryModeActive = true;
    bool UseOppositeColorExitForProfits, m_ColorCloseExitRequested;
    int m_ProfitRuleEntryBar = 10;
    double m_OppositeColorStopPrice = 100;
    ITrendLineObject m_PinBarStopLine, m_PinBarTargetLine;
    ITrendLineObject m_PinBarTailLine, m_PinBarCompletionLine, m_PinBarLabel;
    double UpdateAndGetGlobalPnL(double value) { return value; }
    int GetProtectiveStopLossTicks(double tickSize) { return 9; }
    void ClearRecoveryColorStopProjection() { }
    double RoundToTick(double price, double tickSize) { return Math.Round(price / tickSize) * tickSize; }
    void UpdateShiftProjectionLine(ref ITrendLineObject line, double price,
                                  Color color, ETLStyle style, int size) {
        if (line == null) line = new ITrendLineObject();
        line.Price = price;
    }

    // PRODUCTION_METHODS

    static int checks;
    static void Check(bool condition, string name) {
        checks++;
        if (!condition) throw new Exception(name);
    }
    static void Main() {
        var trade = new SessionProfitRuleTests();
        foreach (bool globalMode in new[] {false, true}) {
            trade.UseOppositeColorExitForProfits = globalMode;
            Check(trade.GetProfitTargetTicksForNewTrade(0, .25) == 4, "zero P&L has 4T target");
            Check(trade.GetProfitTargetTicksForNewTrade(100, .25) == 4, "positive P&L has 4T target");
            Check(trade.GetProfitTargetTicksForNewTrade(-.01, .25) == 0, "negative P&L has no target");
        }
        trade.m_SessionProfitRuleActive = false;
        trade.UseOppositeColorExitForProfits = false;
        Check(trade.GetProfitTargetTicksForNewTrade(-100, .25) == 5, "legacy fixed mode retained");
        trade.UseOppositeColorExitForProfits = true;
        Check(trade.GetProfitTargetTicksForNewTrade(100, .25) == 0, "legacy run mode retained");

        foreach (int side in new[] {1, -1}) {
            ITrendLineObject stop = null, target = null;
            trade.StrategyInfo.OpenEquity = 0;
            trade.UpdateSessionExitProjectionLines(ref stop, ref target, 100, side, .25);
            Check(stop.Price == 100 - side * 2.25, "protective preview matches 9T distance");
            Check(target.Price == 100 + side, "4T target preview on correct side");
            trade.UpdateSessionExitProjectionLines(ref stop, ref target, 100.25, side, .25);
            Check(target.Price == 100.25 + side, "target follows entry");
            var oldTarget = target;
            trade.StrategyInfo.OpenEquity = -.01;
            trade.UpdateSessionExitProjectionLines(ref stop, ref target, 100, side, .25);
            Check(target == null && oldTarget.Deleted, "negative P&L removes fixed target preview");
            Check(stop != null && !stop.Deleted, "protective preview retained in recovery");
            trade.StrategyInfo.OpenEquity = 1;
            trade.UpdateSessionExitProjectionLines(ref stop, ref target, 100, side, .25);
            Check(target != null, "nonnegative preview recreates target");

            var recovery = new SessionProfitRuleTests();
            recovery.Bars.Close[0] = 100 - side * .25;
            recovery.UpdateSessionProfitExit(side);
            Check(recovery.m_CloseLongNextBar.Sends + recovery.m_CloseShortNextBar.Sends == 0,
                  "forming opposite color cannot exit");
            recovery.Bars.Status = EBarState.Close;
            recovery.Bars.Close[0] = 100;
            recovery.UpdateSessionProfitExit(side);
            Check(!recovery.m_ColorCloseExitRequested, "doji cannot exit");
            Check(recovery.m_OppositeColorStopPrice == 0, "no legacy reversal stop");
            recovery.Bars.Close[0] = 100 + side * .25;
            recovery.UpdateSessionProfitExit(side);
            Check(!recovery.m_ColorCloseExitRequested, "same color cannot exit");
            recovery.Bars.Close[0] = 100 - side * .25;
            recovery.Bars.CurrentBar = recovery.m_ProfitRuleEntryBar;
            recovery.UpdateSessionProfitExit(side);
            Check(!recovery.m_ColorCloseExitRequested, "entry bar is not a later reversal");
            recovery.Bars.CurrentBar++;
            recovery.UpdateSessionProfitExit(side);
            Check(recovery.m_ColorCloseExitRequested, "opposite close exits without minimum profit");
            Check((side > 0 ? recovery.m_CloseLongNextBar.Sends : recovery.m_CloseShortNextBar.Sends) == 1,
                  "correct close order submitted");
            recovery.Bars.Status = EBarState.Open;
            recovery.UpdateSessionProfitExit(side);
            Check((side > 0 ? recovery.m_CloseLongNextBar.Sends : recovery.m_CloseShortNextBar.Sends) == 2,
                  "exit request persists until fill");
            recovery.UpdateSessionProfitExit(0);
            Check(recovery.m_CloseLongNextBar.Sends + recovery.m_CloseShortNextBar.Sends == 2,
                  "flat position sends no close");
            var profitable = new SessionProfitRuleTests();
            profitable.m_RecoveryModeActive = false;
            profitable.Bars.Status = EBarState.Close;
            profitable.Bars.Close[0] = 100 - side * .25;
            profitable.UpdateSessionProfitExit(side);
            Check(!profitable.m_ColorCloseExitRequested, "fixed-target trade ignores opposite close");
        }
        var guides = new SessionProfitRuleTests();
        var stopGuide = guides.m_PinBarStopLine = new ITrendLineObject();
        var targetGuide = guides.m_PinBarTargetLine = new ITrendLineObject();
        var tailGuide = guides.m_PinBarTailLine = new ITrendLineObject();
        var entryGuide = guides.m_PinBarCompletionLine = new ITrendLineObject();
        var label = guides.m_PinBarLabel = new ITrendLineObject();
        guides.ClearPinBarProjectionLines();
        Check(stopGuide.Deleted && targetGuide.Deleted && tailGuide.Deleted &&
              entryGuide.Deleted && label.Deleted, "all pending pin guides deleted");
        Check(guides.m_PinBarStopLine == null && guides.m_PinBarTargetLine == null &&
              guides.m_PinBarCompletionLine == null, "guide references cleared");
        guides.ClearPinBarProjectionLines();
        Check(true, "repeated cleanup is safe");
        Console.WriteLine("PASS: " + checks + " session-profit assertions");
    }
}
