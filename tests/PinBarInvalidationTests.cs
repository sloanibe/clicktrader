using System;

// Production setup evaluation and cleanup run with recorded order cancellation.
class PinBarInvalidationTests {
    enum EEntrySetup { None, PinBar, Ema24Bounce }
    class BarData { public int CurrentBar = 10; }
    class StrategyData { public int MarketPosition; }
    BarData Bars = new BarData();
    StrategyData StrategyInfo = new StrategyData();
    bool m_FourTickPinBarMode, m_ShiftProjectionActive;
    bool EnablePinBarTrading = true, m_AutoEntryArmed = true;
    double[] m_FastEMA = {101}, m_SlowEMA = {100};
    int m_ArmedDirection = 1, m_PinProjectionBar = -1, m_PinProjectionDirection;
    int m_PinProjectionTailTicks, m_PinEntryCandidateDirection, m_PinEntryCandidateBodyTicks;
    int m_PinBarOrderBar = -1;
    bool m_PinProjectionTailReached, m_PinProjectionBroken, m_PinProjectionQualified;
    bool m_PinProjectionOpenAligned, m_PinEntryCandidateValid;
    double m_PinEntryCandidatePrice;
    object m_PinBarCompletionLine;
    EEntrySetup m_ActiveEntrySetup;
    bool workingOrder, drawingVisible;
    int cancellations;
    bool shapeValid = true, emaValid = true, trendValid = true, contextValid = true;
    bool tailReached = true;
    double rangeTicks = 4;
    int bodyTicks = 2;
    void UpdateFourTickPinBarProjection(double tick, int position) { }
    double GetActiveRangeTicks(double tick) { return rangeTicks; }
    void ClearPinBarProjectionLines() { drawingVisible = false; }
    void CancelWorkingEntryOrders() {
        if (m_ActiveEntrySetup != EEntrySetup.PinBar) throw new Exception("cancel before clear");
        cancellations++; workingOrder = false;
    }
    void ClearPendingEntry() { m_ActiveEntrySetup = EEntrySetup.None; }
    int GetUnarmedPinBarProjectionDirection(double tick) { return 1; }
    int GetInitialPinBarTailTicks() { return 4 - bodyTicks; }
    bool IsPinBarOpenOnCorrectEmaSide(int direction, double tick) { return true; }
    bool IsEntryDirectionAllowed(int direction) { return direction == m_ArmedDirection; }
    int GetActivePinBarRangeTicks() { return 4; }
    int GetPinBarPB1BodyTicks(int range) { return 1; }
    void GetPinBarProjectionPrices(int direction, int tail, int body, double tick,
                                   out double low, out double high) { low = 99; high = 100; }
    void UpdatePinBarFormationState(int direction, double low, double high, double tick) {
        m_PinProjectionTailReached = tailReached;
        m_PinProjectionBroken = !shapeValid;
    }
    bool CanStillFormPinBar(int direction, int tail, int body, double tick) { return shapeValid; }
    bool IsSupportedPinBarShape(int range, int body) { return true; }
    bool HasReachedPinBarTail(int direction, double low, double high, double tick) { return tailReached; }
    double RoundToTick(double price, double tick) { return Math.Round(price / tick) * tick; }
    void UpdatePinBarTailProjectionLine(double price) { drawingVisible = true; }
    void UpdatePinBarProjectionLine(ref object line, double price, bool active) { drawingVisible = true; }
    void UpdatePinBarProjectionLabel(double price, int direction, bool active, int body) { }
    bool HasThreeBarPinBarCloseBreakout(int direction) { return true; }
    bool HasTwoPriorPinBarTrendCloses(int direction) { return true; }
    bool HasCompactPinBarPriorTrendConfirmation(int direction, double tick) { return true; }
    bool HasPinBarPriorBarDirection(int direction) { return true; }
    bool HasRequiredEmaFan(int direction, double tick) { return emaValid; }
    bool HasDirectionalFastEmaSlopeForThreeBars(int direction) { return true; }
    bool IsPinBarTailOnFastEmaSide(int direction, double low, double high) { return true; }
    bool HasSharplyMovingFastEma(int direction, double tick) { return trendValid; }
    bool IsPinBarTrendFilterValid(int direction, double tick) { return trendValid; }
    bool IsCompactPinBarContinuationValid(int direction, double low, double high, double tick) { return contextValid; }
    bool HasPinBarLeadInStructure(int direction) { return contextValid; }
    bool IsStrongPinBarContinuationValid(int direction, double low, double high, double tick) { return contextValid; }

    // PRODUCTION_METHODS

    static int checks;
    static void Check(bool condition, string name) {
        checks++;
        if (!condition) throw new Exception(name);
    }
    void Tick() {
        m_PinEntryCandidateValid = false; // CalcBar resets candidates each update.
        UpdatePinBarProjection(.25, StrategyInfo.MarketPosition);
    }
    void Stage() {
        Check(m_PinEntryCandidateValid, "valid pin stages a candidate");
        m_ActiveEntrySetup = EEntrySetup.PinBar;
        m_PinBarOrderBar = Bars.CurrentBar;
        workingOrder = true;
    }
    static void Main() {
        foreach (int direction in new[] {1, -1}) {
            foreach (int body in new[] {0, 1, 2}) {
                foreach (string failure in new[] {"shape", "ema", "trend", "context", "range", "functions"}) {
                    var signal = new PinBarInvalidationTests();
                    signal.m_ArmedDirection = direction;
                    signal.bodyTicks = body;
                    signal.Tick();
                    // PB0 inherits a reached tail from the earlier pin shape.
                    signal.m_PinProjectionTailReached = true;
                    signal.Tick();
                    signal.Stage();
                    Check(signal.m_PinProjectionQualified && signal.drawingVisible, "qualified pin visible");
                    switch (failure) {
                        case "shape": signal.shapeValid = false; break;
                        case "ema": signal.emaValid = false; break;
                        case "trend": signal.trendValid = false; break;
                        case "context": signal.contextValid = false; break;
                        case "range": signal.rangeTicks = 0; break;
                        case "functions": signal.m_FastEMA = null; break;
                    }
                    signal.Tick();
                    Check(!signal.workingOrder && signal.cancellations == 1,
                          failure + " cancels previously qualified pin order");
                    Check(!signal.drawingVisible && !signal.m_PinEntryCandidateValid &&
                          signal.m_ActiveEntrySetup == EEntrySetup.None && signal.m_PinBarOrderBar == -1,
                          failure + " clears setup and projections");
                    Check(signal.m_AutoEntryArmed && signal.m_ArmedDirection == direction,
                          failure + " preserves arming and direction");
                    signal.shapeValid = signal.emaValid = signal.trendValid = signal.contextValid = true;
                    signal.rangeTicks = 4; signal.m_FastEMA = new[] {101.0};
                    // A physically broken bar is replaced by a fresh bar.
                    if (failure == "shape") signal.Bars.CurrentBar++;
                    signal.Tick();
                    signal.m_PinProjectionTailReached = true;
                    signal.Tick();
                    Check(signal.m_PinEntryCandidateValid && signal.drawingVisible,
                          "fresh valid setup appears without rearming");
                    signal.Stage();
                    signal.StrategyInfo.MarketPosition = direction;
                    signal.ClearPinBarEntryIfActive();
                    Check(signal.workingOrder && signal.cancellations == 1, "filled trade is not cancelled");
                }
            }
        }
        var other = new PinBarInvalidationTests();
        other.m_ActiveEntrySetup = EEntrySetup.Ema24Bounce;
        other.workingOrder = true;
        other.ClearPinBarEntryIfActive();
        Check(other.workingOrder && other.cancellations == 0, "other setup is not cancelled");
        Console.WriteLine("PASS: " + checks + " pin-bar invalidation assertions");
    }
}
