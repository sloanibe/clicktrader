using System;

// Production stop promotion and submission; no broker connection.
class RangeBoundaryBreakEvenTests {
    class BarData {
        public bool LastBarOnChart = true;
        public double[] Low = {100}, High = {101}, Close = {101};
    }
    class StrategyData { public double AvgEntryPrice; }
    class Order {
        public int Sends; public double Price;
        public void Send(double price) { Sends++; Price = price; }
    }
    BarData Bars = new BarData();
    StrategyData StrategyInfo = new StrategyData();
    bool AutoProtectiveStopOn1BarProfit = true, m_SessionProfitRuleActive = true, m_KillModeActive;
    bool m_OppositeColorProfitArmed;
    double m_ProtectiveStopPrice, m_ProfitTargetPrice, m_OppositeColorStopPrice;
    double rangeTicks = 4;
    Order m_BuyExitStop = new Order(), m_SellExitStop = new Order();
    Order m_BuyExitLimit = new Order(), m_SellExitLimit = new Order();
    Order m_ColorTrailLongStop = new Order(), m_ColorTrailShortStop = new Order();
    double GetActiveRangeTicks(double tick) { return rangeTicks; }
    double RoundToTick(double price, double tick) { return Math.Round(price / tick) * tick; }

    // PRODUCTION_METHODS

    static int checks;
    static void Check(bool condition, string message) {
        checks++; if (!condition) throw new Exception(message);
    }
    static void Main() {
        foreach (int side in new[] {1, -1}) foreach (int range in new[] {4, 6, 8})
        foreach (double tick in new[] {.25, .5}) {
            var trade = new RangeBoundaryBreakEvenTests(); trade.rangeTicks = range;
            double boundary = 100 + side * range * tick;
            trade.Bars.Low[0] = side > 0 ? 100 : boundary;
            trade.Bars.High[0] = side > 0 ? boundary : 100;
            // Only one tick of trade profit at the boundary: trigger is bar-relative.
            double entry = boundary - side * tick;
            trade.StrategyInfo.AvgEntryPrice = entry;
            double originalStop = entry - side * 10 * tick;
            trade.m_ProtectiveStopPrice = originalStop;
            trade.m_ProfitTargetPrice = entry + side * 4 * tick;
            double target = trade.m_ProfitTargetPrice;
            trade.Bars.Close[0] = boundary - side * tick;
            trade.UpdateRangeBoundaryBreakEven(side, tick);
            Check(trade.m_ProtectiveStopPrice == originalStop,
                  "earlier chart extreme at boundary cannot trigger before live price reaches it");
            trade.Bars.Close[0] = boundary;
            trade.UpdateRangeBoundaryBreakEven(side, tick);
            trade.SubmitActiveExitOrders(side);
            Order stop = side > 0 ? trade.m_BuyExitStop : trade.m_SellExitStop;
            Check(trade.m_ProtectiveStopPrice == entry && stop.Sends == 1 && stop.Price == entry,
                  "boundary touch immediately submits stop at exact entry without extra tick");
            Check(trade.m_ProfitTargetPrice == target, "target stays active and unchanged");
            trade.Bars.Close[0] = boundary - side * 2 * tick;
            trade.UpdateRangeBoundaryBreakEven(side, tick);
            Check(trade.m_ProtectiveStopPrice == entry, "retracement cannot undo break-even");
            trade.m_ProtectiveStopPrice = entry + side * tick;
            trade.Bars.Close[0] = boundary;
            trade.UpdateRangeBoundaryBreakEven(side, tick);
            Check(trade.m_ProtectiveStopPrice == entry + side * tick, "never loosen a tighter stop");
            foreach (string gate in new[] {"disabled", "flat", "legacy", "kill", "historical", "range", "entry", "not favorable"}) {
                trade.m_ProtectiveStopPrice = originalStop;
                trade.AutoProtectiveStopOn1BarProfit = gate != "disabled";
                trade.m_SessionProfitRuleActive = gate != "legacy";
                trade.m_KillModeActive = gate == "kill";
                trade.Bars.LastBarOnChart = gate != "historical";
                trade.rangeTicks = gate == "range" ? 0 : range;
                trade.StrategyInfo.AvgEntryPrice = gate == "entry" ? 0 : gate == "not favorable" ? boundary : entry;
                trade.UpdateRangeBoundaryBreakEven(gate == "flat" ? 0 : side, tick);
                Check(trade.m_ProtectiveStopPrice == originalStop, gate + " suppresses promotion");
            }
        }
        Console.WriteLine("PASS: " + checks + " range-boundary break-even assertions");
    }
}
