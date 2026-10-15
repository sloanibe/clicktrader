using System;

class ShiftManualEntryTests {
    const int OrderQuantity = 1;
    class PositionData { public int MarketPosition; }
    PositionData StrategyInfo = new PositionData();
    bool m_AutoEntryArmed, m_FourTickPinBarMode, m_KillModeActive, m_FlattenRequested;
    int m_ArmedDirection, m_EmaBounceOrderBar, m_PinBarOrderBar;
    int cancellations;
    void ResetAutomaticEntryCandidates() { }
    void ClearEma8BounceProjectionLines() { }
    string feedback;
    void SetClickFeedback(string message) { feedback = message; }
    void ClearEmergencyIndicator() { }
    void ClearPinBarProjectionLines() { }
    void ClearEmaBounceProjectionLines() { }
    void UpdateProjectedEntryLine() { }
    enum EEntrySetup { None, PinBar, ShiftProjection }
    class BarData {
        public int CurrentBar = 10;
        public bool LastBarOnChart = true;
        public double[] Low = {100}, High = {101};
    }
    class Order {
        public double Price;
        public int Sends;
        public void Send(double price, int quantity) { Price = price; Sends++; }
    }
    BarData Bars = new BarData();
    double[] m_FastEMA = {100}, m_SlowEMA = {99};
    bool m_ShiftProjectionActive = true, ShiftProjectionRequireOneTickConfirmation = false;
    int m_ShiftProjectionBar = 10;
    bool m_BuyOrderActive, m_SellOrderActive;
    EEntrySetup m_ActiveEntrySetup;
    double m_StopPrice, m_LastSentPrice, rangeTicks = 4, drawnEntry;
    bool IsAskChart = true;
    Order m_BuyStop = new Order(), m_SellStop = new Order();
    Order m_PinBuyStop = new Order(), m_PinSellStop = new Order();
    Order m_ManualBuyStop = new Order(), m_ManualSellStop = new Order();
    Order ManualOrder { get { return IsAskChart ? m_ManualBuyStop : m_ManualSellStop; } }
    bool IsEntryDirectionAllowed(int direction) { return direction == (IsAskChart ? 1 : -1); }
    double GetActiveRangeTicks(double tickSize) { return rangeTicks; }
    double RoundToTick(double price, double tickSize) { return Math.Round(price / tickSize) * tickSize; }
    void CancelWorkingEntryOrders() { cancellations++; }
    void ClearPendingEntry() {
        m_ActiveEntrySetup = EEntrySetup.None;
        m_BuyOrderActive = m_SellOrderActive = false;
        m_StopPrice = m_LastSentPrice = drawnEntry = 0;
    }
    void UpdateShiftProjectionLines(double tailPrice, double entryPrice, int direction, double tickSize) {
        drawnEntry = entryPrice;
    }

    void CaptureTradeHistoryIntent(int direction) { }

    // PRODUCTION_METHODS

    static int checks;
    static void Check(bool condition, string name) {
        checks++;
        if (!condition) throw new Exception(name);
    }
    static void Main() {
        foreach (int side in new[] {1, -1}) {
            foreach (int range in new[] {1, 2, 3, 4, 5, 6, 8}) {
                foreach (bool option in new[] {false, true}) {
                    var entry = new ShiftManualEntryTests();
                    entry.IsAskChart = side > 0;
                    entry.m_FastEMA[0] = 99 + side;
                    entry.rangeTicks = range;
                    entry.ShiftProjectionRequireOneTickConfirmation = option;
                    double extreme = side > 0 ? entry.Bars.Low[0] : entry.Bars.High[0];
                    int distance = range + (option ? 1 : 0);
                    double expected = extreme + side * distance * .25;
                    entry.UpdateShiftProjectionEntry(.25, 0);
                    Check(entry.m_StopPrice == expected && entry.drawnEntry == expected,
                          "order and projection honor shared confirmation option for range " + range);
                    entry.SubmitShiftProjectionEntryNow();
                    Order order = entry.ManualOrder;
                    Check(order.Sends == 1 && order.Price == expected && entry.m_LastSentPrice == expected,
                          "correct native stop submitted");
                    if (side > 0) entry.Bars.Low[0] -= .25;
                    else entry.Bars.High[0] += .25;
                    entry.UpdateShiftProjectionEntry(.25, 0);
                    entry.SubmitShiftProjectionEntryNow();
                    Check(entry.drawnEntry == expected - side * .25 && order.Price == entry.drawnEntry,
                          "entry order and line follow extreme");
                }
            }
        }
        foreach (bool ask in new[] {true, false}) {
            var blocked = new ShiftManualEntryTests();
            blocked.IsAskChart = ask;
            blocked.m_ShiftProjectionActive = false;
            blocked.m_FastEMA[0] = ask ? 98 : 100;
            blocked.StartShiftProjectionEntry(.25);
            Check(!blocked.m_ShiftProjectionActive && !blocked.m_AutoEntryArmed &&
                  blocked.ManualOrder.Sends == 0 &&
                  blocked.feedback.Contains(ask ? "ASK REQUIRES" : "BID REQUIRES"),
                  "opposite EMA direction explains rejection without sending");
            blocked.m_FastEMA[0] = 99;
            blocked.StartShiftProjectionEntry(.25);
            Check(!blocked.m_ShiftProjectionActive && blocked.feedback != null,
                  "equal EMAs reject visibly");
            blocked.rangeTicks = 0;
            blocked.StartShiftProjectionEntry(.25);
            Check(blocked.feedback.Contains("WAITING FOR RANGE"), "unknown range explained");
            blocked.StrategyInfo.MarketPosition = ask ? 1 : -1;
            blocked.StartShiftProjectionEntry(.25);
            Check(blocked.feedback.Contains("POSITION ALREADY OPEN"), "open position explained");

            var accepted = new ShiftManualEntryTests();
            accepted.IsAskChart = ask;
            accepted.m_FastEMA[0] = ask ? 100 : 98;
            accepted.StartShiftProjectionEntry(.25);
            Check(accepted.feedback == null && !accepted.m_AutoEntryArmed &&
                  accepted.ManualOrder.Sends == 1,
                  "permitted click submits on both chart sides");
        }
        foreach (bool ask in new[] {true, false}) {
            foreach (bool previouslyArmed in new[] {false, true}) {
                foreach (bool pinMode in new[] {false, true}) {
                    var transition = new ShiftManualEntryTests();
                    transition.IsAskChart = ask;
                    transition.m_FastEMA[0] = ask ? 100 : 98;
                    transition.m_ShiftProjectionActive = false;
                    transition.m_AutoEntryArmed = previouslyArmed;
                    transition.m_FourTickPinBarMode = pinMode;
                    transition.m_ArmedDirection = previouslyArmed ? (ask ? 1 : -1) : 0;
                    transition.StartShiftProjectionEntry(.25);
                    Check(transition.m_ShiftProjectionActive &&
                          !transition.m_AutoEntryArmed && !transition.m_FourTickPinBarMode &&
                          transition.m_ArmedDirection == 0,
                          "manual start clears previous automation");
                    int sends = transition.ManualOrder.Sends;
                    transition.ClearShiftProjectionEntry();
                    Check(!transition.m_ShiftProjectionActive &&
                          !transition.m_AutoEntryArmed && !transition.m_FourTickPinBarMode &&
                          transition.m_ArmedDirection == 0 &&
                          !transition.m_BuyOrderActive && !transition.m_SellOrderActive &&
                          transition.m_ActiveEntrySetup == EEntrySetup.None &&
                          transition.drawnEntry == 0 && transition.cancellations == 2,
                          "manual cancel cancels order and stays unarmed");
                    transition.UpdateShiftProjectionEntry(.25, 0);
                    transition.SubmitShiftProjectionEntryNow();
                    Check(transition.ManualOrder.Sends == sends,
                          "cancelled manual entry cannot resubmit");
                    transition.StartShiftProjectionEntry(.25);
                    transition.Bars.CurrentBar++;
                    transition.UpdateShiftProjectionEntry(.25, 0);
                    Check(!transition.m_ShiftProjectionActive && !transition.m_AutoEntryArmed &&
                          !transition.m_FourTickPinBarMode,
                          "expired manual entry stays unarmed");
                }
            }
        }
        Console.WriteLine("PASS: " + checks + " manual-entry assertions");
    }
}
