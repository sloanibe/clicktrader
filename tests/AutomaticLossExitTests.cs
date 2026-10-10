using System;

// The runner inserts the production methods; native order sends are recorded.
class AutomaticLossExitTests {
    const int MinimumAutomaticLossTicks = 9;
    const int OppositeColorExitMinimumProfitTicks = 5;
    class BarData {
        public double[] High = {100}, Low = {100}, Close = {100};
    }
    class StrategyData { public double AvgEntryPrice = 100; }
    class Order {
        public int Sends;
        public double Price;
        public void Send(double price) { Sends++; Price = price; }
    }
    BarData Bars = new BarData();
    StrategyData StrategyInfo = new StrategyData();
    bool m_SessionProfitRuleActive, m_OppositeColorProfitArmed;
    bool UseOppositeColorExitForProfits = true;
    double m_OppositeColorStopPrice, m_ProtectiveStopPrice, m_ProfitTargetPrice;
    int ProtectiveStopLossTicks;
    double rangeTicks = 4;
    Order m_BuyExitStop = new Order(), m_SellExitStop = new Order();
    Order m_BuyExitLimit = new Order(), m_SellExitLimit = new Order();
    Order m_ColorTrailLongStop = new Order(), m_ColorTrailShortStop = new Order();
    double GetActiveRangeTicks(double tickSize) { return rangeTicks; }
    double RoundToTick(double price, double tickSize) { return Math.Round(price / tickSize) * tickSize; }
    void UpdateSessionProfitExit(int position, double tickSize) { }
    void ClearRecoveryColorStopProjection() { }
    void UpdateRecoveryColorStopProjection(double price, int position) { }

    // PRODUCTION_METHODS

    static int checks;
    static void Check(bool condition, string name) {
        checks++;
        if (!condition) throw new Exception(name);
    }
    static void Main() {
        var settings = new AutomaticLossExitTests();
        foreach (int range in new[] {0, 1, 2, 3, 4, 5, 8}) {
            settings.rangeTicks = range;
            Check(settings.GetProtectiveStopLossTicks(.25) == Math.Max(9, 2 * range + 1),
                  "automatic stop has nine-tick floor");
        }
        foreach (int range in new[] {4, 5, 8}) {
            settings.rangeTicks = range;
            foreach (int requested in new[] {1, 8, 9, 10, 20}) {
                settings.ProtectiveStopLossTicks = requested;
                Check(settings.GetProtectiveStopLossTicks(.25) == Math.Max(2 * range + 1, requested),
                      "explicit stops cannot reduce two ranges plus one tick");
            }
        }
        foreach (int side in new[] {1, -1}) {
            foreach (double tick in new[] {.25, .5, 1.0}) {
                foreach (int resultTicks in new[] {-12, -9, -8, -1, 0, 1, 5}) {
                    double price = 100 + side * resultTicks * tick;
                    double expected = 100 + side *
                        (resultTicks < 0 && resultTicks > -9 ? -9 : resultTicks) * tick;
                    Check(ClampAutomaticLossExitPrice(side, 100, price, tick) == expected,
                          "long/short boundary and profitable exits");
                }
            }
            var trade = new AutomaticLossExitTests();
            trade.rangeTicks = 8;
            trade.Bars.High[0] = side > 0 ? 101.25 : 101;
            trade.Bars.Low[0] = side > 0 ? 99 : 98.75;
            trade.UpdateOppositeColorExitManagement(side, .25);
            Check(trade.m_OppositeColorProfitArmed, "five-tick move arms trail");
            Check(trade.m_OppositeColorStopPrice == 100 - side * 2.25,
                  "initial losing trail moved to nine ticks");
            trade.SubmitActiveExitOrders(side);
            Order trail = side > 0 ? trade.m_ColorTrailLongStop : trade.m_ColorTrailShortStop;
            Check(trail.Sends == 1 && trail.Price == 100 - side * 2.25,
                  "native trailing order receives clamped price");
            trade.Bars.High[0] = side > 0 ? 102 : 101;
            trade.Bars.Low[0] = side > 0 ? 99 : 98;
            trade.UpdateOppositeColorExitManagement(side, .25);
            Check(trade.m_OppositeColorStopPrice == 100 - side * 2.25,
                  "ratchet cannot tighten to a one-tick loss");
            trade.Bars.High[0] = side > 0 ? 102.5 : 101;
            trade.Bars.Low[0] = side > 0 ? 99 : 97.5;
            trade.UpdateOppositeColorExitManagement(side, .25);
            Check(trade.m_OppositeColorStopPrice == 100 + side * .25,
                  "profitable trail can ratchet normally");
            trade.Bars.High[0] = side > 0 ? 102 : 101;
            trade.Bars.Low[0] = side > 0 ? 99 : 98;
            trade.UpdateOppositeColorExitManagement(side, .25);
            Check(trade.m_OppositeColorStopPrice == 100 + side * .25,
                  "profitable stop never loosens");
            trade.SubmitActiveExitOrders(side);
            Check(trail.Price == 100 + side * .25, "native profit trail stays active");
        }
        Console.WriteLine("PASS: " + checks + " automatic loss-exit assertions");
    }
}
