using System;

// Production setup evaluation and cleanup run with recorded order cancellation.
class PinBarInvalidationTests {
    enum EEntrySetup { None, PinBar, Ema24Bounce }
    enum EBarState { Open, Close }
    class BarData {
        public int CurrentBar = 10;
        public EBarState Status = EBarState.Open;
        public double[] Low = {99, 99}, High = {100, 100}, Close = {100, 100};
    }
    class StrategyData { public int MarketPosition; }
    BarData Bars = new BarData();
    StrategyData StrategyInfo = new StrategyData();
    bool m_FourTickPinBarMode, m_ShiftProjectionActive, m_KillModeActive;
    bool EnablePinBarTrading = true, m_AutoEntryArmed = true;
    double[] m_FastEMA = {101}, m_SlowEMA = {100};
    int m_ArmedDirection = 1, m_PinProjectionBar = -1, m_PinProjectionDirection;
    int m_PinProjectionTailTicks, m_PinEntryCandidateDirection, m_PinEntryCandidateBodyTicks;
    int m_PinBarOrderBar = -1;
    bool m_PinProjectionTailReached, m_PinProjectionBroken, m_PinProjectionQualified;
    bool m_PinProjectionOpenAligned, m_PinEntryCandidateValid;
    double m_PinEntryCandidatePrice, drawnEntry;
    object m_PinBarCompletionLine;
    EEntrySetup m_ActiveEntrySetup;
    bool workingOrder, drawingVisible;
    int cancellations;
    bool shapeValid = true, emaValid = true, trendValid = true, contextValid = true;
    bool tailReached = true;
    bool supportedShape = true, allowedDirection = true;
    double projectedLow = 99, projectedHigh = 100;
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
    bool IsEntryDirectionAllowed(int direction) { return allowedDirection && direction == m_ArmedDirection; }
    int GetActivePinBarRangeTicks() { return 4; }
    int GetPinBarPB1BodyTicks(int range) { return 1; }
    void GetPinBarProjectionPrices(int direction, int tail, int body, double tick,
                                   out double low, out double high) { low = projectedLow; high = projectedHigh; }
    void UpdatePinBarFormationState(int direction, double low, double high, double tick) {
        m_PinProjectionTailReached = tailReached;
        m_PinProjectionBroken = !shapeValid;
    }
    bool CanStillFormPinBar(int direction, int tail, int body, double tick) { return shapeValid; }
    bool IsSupportedPinBarShape(int range, int body) { return supportedShape; }
    bool HasReachedPinBarTail(int direction, double low, double high, double tick) { return tailReached; }
    double RoundToTick(double price, double tick) { return Math.Round(price / tick) * tick; }
    void UpdatePinBarTailProjectionLine(double price) { drawingVisible = true; }
    void UpdatePinBarProjectionLine(ref object line, double price, bool active) { drawingVisible = true; drawnEntry = price; }
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

    bool ShiftProjectionRequireOneTickConfirmation = false;
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
        foreach (int side in new[] {1, -1}) foreach (bool confirm in new[] {false, true}) {
            var entry = new PinBarInvalidationTests(); entry.m_ArmedDirection = side;
            entry.ShiftProjectionRequireOneTickConfirmation = confirm;
            entry.Tick();
            double expected = (side > 0 ? 100 : 99) + (confirm ? side * .25 : 0);
            Check(entry.m_PinEntryCandidateValid && entry.m_PinEntryCandidatePrice == expected &&
                  entry.drawnEntry == expected, "standard pin candidate and guide honor shared confirmation");
        }

        foreach (int side in new[] {1, -1}) foreach (bool confirm in new[] {false, true}) {
            var entry = new PinBarInvalidationTests(); entry.m_ArmedDirection = side;
            entry.ShiftProjectionRequireOneTickConfirmation = confirm;
            entry.Tick(); entry.Stage();
            if (side > 0) entry.Bars.Low[0] = 98.75;
            else entry.Bars.High[0] = 100.25;
            entry.UpdatePinBarProjection(.25, 0);
            Check(!entry.workingOrder && !entry.m_PinEntryCandidateValid && entry.m_AutoEntryArmed,
                  "legacy pin loses entry stop on previous extreme break");
            entry.Bars.CurrentBar++; entry.bodyTicks = 1;
            entry.Bars.Low[1] = entry.Bars.Low[0]; entry.Bars.High[1] = entry.Bars.High[0];
            if (side > 0) entry.Bars.Low[0] = entry.projectedLow = 98.5;
            else entry.Bars.High[0] = entry.projectedHigh = 100.5;
            entry.UpdatePinBarProjection(.25, 0);
            Check(!entry.m_PinEntryCandidateValid && !entry.workingOrder && entry.m_AutoEntryArmed,
                  "legacy next pin cannot qualify beyond preceding invalidated pin extreme");
        }
        foreach (int direction in new[] {1, -1}) {
            foreach (int body in new[] {0, 1, 2}) {
                foreach (string failure in new[] {"shape", "ema", "trend", "context", "range", "functions", "previous extreme", "projected extreme", "wrong close", "kill", "unsupported", "direction", "disabled", "tail"}) {
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
                        case "previous extreme":
                            if (direction > 0) signal.Bars.Low[0] = 98.75;
                            else signal.Bars.High[0] = 100.25;
                            break;
                        case "projected extreme":
                            if (direction > 0) signal.Bars.Low[1] = 99.25;
                            else signal.Bars.High[1] = 99.75;
                            break;
                        case "wrong close":
                            signal.Bars.Status = EBarState.Close;
                            signal.Bars.Close[0] = direction > 0 ? 99 : 100;
                            break;
                        case "kill": signal.m_KillModeActive = true; break;
                        case "unsupported": signal.supportedShape = false; break;
                        case "direction": signal.allowedDirection = false; break;
                        case "disabled": signal.EnablePinBarTrading = false; break;
                        case "tail": signal.tailReached = signal.m_PinProjectionTailReached = false; break;
                    }
                    // Exercise the evaluator directly with a stale candidate: it must clear it itself.
                    signal.UpdatePinBarProjection(.25, 0);
                    Check(!signal.workingOrder && signal.cancellations == 1,
                          failure + " cancels previously qualified pin order");
                    Check(!signal.drawingVisible && !signal.m_PinEntryCandidateValid &&
                          signal.m_ActiveEntrySetup == EEntrySetup.None && signal.m_PinBarOrderBar == -1,
                          failure + " clears setup and projections");
                    Check(signal.m_AutoEntryArmed && signal.m_ArmedDirection == direction,
                          failure + " preserves arming and direction");
                    signal.shapeValid = signal.emaValid = signal.trendValid = signal.contextValid = true;
                    signal.rangeTicks = 4; signal.m_FastEMA = new[] {101.0};
                    signal.Bars.Low[0] = signal.Bars.Low[1] = 99;
                    signal.Bars.High[0] = signal.Bars.High[1] = 100;
                    signal.Bars.Status = EBarState.Open; signal.m_KillModeActive = false;
                    signal.supportedShape = signal.allowedDirection = signal.EnablePinBarTrading = true;
                    signal.tailReached = true;
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
