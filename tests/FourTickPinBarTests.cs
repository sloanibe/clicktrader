// The runner inserts the production projection methods into this harness.
// Drawing and broker calls are recorded rather than sent to MultiCharts.
using System;

class FourTickPinBarTests {
    enum EEntrySetup { None, PinBar }
    enum EBarState { Open, Close }
    class BarData {
        public int CurrentBar = 10;
        public EBarState Status = EBarState.Open;
        public double[] Open = {100, 100}, High = {100, 101};
        public double[] Low = {99.25, 99}, Close = {99.25, 100};
    }
    BarData Bars = new BarData();
    double[] m_FastEMA = {99}, m_SlowEMA = {98.75}, m_ProfileEMA = {98.5};
    bool m_AutoEntryArmed = true, m_FourTickPinBarMode = true;
    bool m_ShiftProjectionActive = false, m_KillModeActive = false;
    int m_ArmedDirection = 1, m_PinProjectionBar, m_PinProjectionDirection;
    int m_PinProjectionTailTicks, m_PinEntryCandidateDirection, m_PinEntryCandidateBodyTicks;
    bool m_PinEntryCandidateValid;
    double m_PinEntryCandidatePrice;
    EEntrySetup m_ActiveEntrySetup;
    double rangeTicks = 4, drawnEntry;
    bool askChart = true, drawingVisible;
    int cancellations;
    object m_PinBarCompletionLine, m_PinBarStopLine, m_PinBarTargetLine;
    bool IsEntryDirectionAllowed(int direction) { return direction == (askChart ? 1 : -1); }
    double GetActiveRangeTicks(double tickSize) { return rangeTicks; }
    void ClearPinBarProjectionLines() { drawingVisible = false; }
    void CancelWorkingEntryOrders() { cancellations++; }
    void ClearPendingEntry() { m_ActiveEntrySetup = EEntrySetup.None; }
    void UpdatePinBarTailProjectionLine(double price) { }
    void UpdatePinBarProjectionLine(ref object line, double price, bool active) {
        drawingVisible = true; drawnEntry = price;
    }
    void UpdatePinBarProjectionLabel(double price, int direction, bool active, int bodyTicks) { }

    void UpdateSessionExitProjectionLines(ref object stopLine, ref object targetLine,
        double entryPrice, int direction, double tickSize) { }

    // PRODUCTION_METHODS

    static int checks;
    static void Check(bool condition, string name) {
        checks++;
        if (!condition) throw new Exception(name);
    }
    static void Shape(int direction, double tailTicks, double bodySideTicks,
                      double previousTailTicks, double emaTicks, bool closed,
                      double closeTicks, bool expected, int expectedBody, string name) {
        const double tick = 0.25;
        double extreme = 100 - direction * tailTicks * tick;
        double bodySide = 100 + direction * bodySideTicks * tick;
        double high = direction > 0 ? bodySide : extreme;
        double low = direction > 0 ? extreme : bodySide;
        double tail, entry;
        int body;
        bool valid = TryGetFourTickPinBarProjection(direction, tick, 100, high, low,
            100 + direction * closeTicks * tick,
            100 - direction * previousTailTicks * tick,
            100 - direction * emaTicks * tick, closed, out tail, out entry, out body);
        Check(valid == expected, name);
        if (expected) {
            Check(body == expectedBody, name + " body");
            Check(tail == extreme, name + " tail");
            Check(entry == 100 + direction * expectedBody * tick, name + " completion");
        } else {
            Check(tail == 0 && entry == 0, name + " cleared outputs");
        }
    }
    static void Main() {
        foreach (int direction in new[] {1, -1}) {
            string side = direction > 0 ? "buy " : "sell ";
            Shape(direction, 2, 0, 4, 4, false, -2, false, 0, side + "tail too short");
            Shape(direction, 3, 0, 4, 4, false, -3, true, 1, side + "forming PB1");
            Shape(direction, 4, 0, 4, 4, false, -4, true, 0, side + "forming PB0 exact touch");
            Shape(direction, 5, 0, 6, 6, false, -5, false, 0, side + "tail too long");
            Shape(direction, 3, 0, 2, 4, false, -3, false, 0, side + "previous tail broken");
            Shape(direction, 3, 0, 3, 3, false, -3, true, 1, side + "both tails touched");
            Shape(direction, 3, 0, 4, 2.99, false, -3, false, 0, side + "fractional EMA penetration");
            Shape(direction, 3, 0, 2.99, 4, false, -3, false, 0, side + "previous extreme penetration");
            Shape(direction, 3, 2, 4, 4, false, -3, false, 0, side + "opposite extreme exceeded");
            Shape(direction, 4, 1, 4, 4, false, -4, false, 0, side + "PB0 has opposite wick");
            Shape(direction, 3, 1, 4, 4, true, 1, true, 1, side + "completed PB1");
            Shape(direction, 4, 0, 4, 4, true, 0, true, 0, side + "completed PB0");
            Shape(direction, 3, 1, 4, 4, true, -3, false, 0, side + "closed at tail");
        }

        foreach (int side in new[] {1, -1}) {
            double ema8 = 100 + side * .5, ema24 = 100 + side * .25, ema50 = 100;
            Check(IsFourTickPinBarContextValid(side, ema8, ema24, ema50, 100, 100 + side * .25),
                  "ordered EMAs with matching prior color");
            Check(IsFourTickPinBarContextValid(side, ema8, ema24, ema50, 100, 100),
                  "ordered EMAs allow prior doji");
            Check(!IsFourTickPinBarContextValid(side, ema8, ema24, ema50, 100, 100 - side * .25),
                  "opposite prior color rejected");
            Check(!IsFourTickPinBarContextValid(side, ema24, ema24, ema50, 100, 100),
                  "8 equals 24 rejected");
            Check(!IsFourTickPinBarContextValid(side, ema8, ema24, ema24, 100, 100),
                  "24 equals 50 rejected");
            Check(!IsFourTickPinBarContextValid(side, ema50, ema24, ema50, 100, 100),
                  "8 on wrong side rejected");
            Check(!IsFourTickPinBarContextValid(side, ema8, ema24, ema8, 100, 100),
                  "50 on wrong side rejected");
        }
        Check(!IsFourTickPinBarContextValid(0, 100, 99, 98, 100, 100), "no direction rejected");

        var signal = new FourTickPinBarTests();
        signal.UpdateFourTickPinBarProjection(.25, 0);
        Check(signal.drawingVisible && signal.drawnEntry == 100.25 &&
              signal.m_PinEntryCandidateValid && signal.m_PinEntryCandidateBodyTicks == 1,
              "first live projection");
        signal.m_ActiveEntrySetup = EEntrySetup.PinBar;
        signal.Bars.Low[0] = signal.Bars.Close[0] = 99;
        signal.UpdateFourTickPinBarProjection(.25, 0);
        Check(signal.drawnEntry == 100 && signal.m_PinEntryCandidateBodyTicks == 0,
              "entry glides from PB1 to PB0");
        signal.m_PinEntryCandidateValid = false; // CalcBar resets candidates each tick.
        signal.m_FastEMA[0] = 99.01;
        signal.UpdateFourTickPinBarProjection(.25, 0);
        Check(!signal.drawingVisible && !signal.m_PinEntryCandidateValid &&
              signal.cancellations == 1 && signal.m_ActiveEntrySetup == EEntrySetup.None,
              "EMA failure removes drawing and cancels staged order");
        Check(signal.m_AutoEntryArmed && signal.m_FourTickPinBarMode,
              "failure leaves watch armed");
        signal.Bars.CurrentBar++;
        signal.Bars.Low[0] = 99.25;
        signal.m_FastEMA[0] = 99;
        signal.UpdateFourTickPinBarProjection(.25, 0);
        Check(signal.drawingVisible && signal.drawnEntry == 100.25, "fresh bar qualifies");
        signal.rangeTicks = 5;
        signal.UpdateFourTickPinBarProjection(.25, 0);
        Check(!signal.drawingVisible && signal.m_AutoEntryArmed, "wrong range hides projection");
        signal.rangeTicks = 4;
        signal.askChart = false;
        signal.UpdateFourTickPinBarProjection(.25, 0);
        Check(!signal.drawingVisible, "chart direction gate");
        signal.askChart = true;
        signal.UpdateFourTickPinBarProjection(.25, 1);
        Check(!signal.drawingVisible, "filled position hides projection");
        signal.m_ShiftProjectionActive = true;
        signal.UpdateFourTickPinBarProjection(.25, 0);
        Check(!signal.drawingVisible, "manual projection owns drawing");
        signal.m_ShiftProjectionActive = false;
        signal.m_KillModeActive = true;
        signal.UpdateFourTickPinBarProjection(.25, 0);
        Check(!signal.drawingVisible, "kill mode suppresses projection");

        var sell = new FourTickPinBarTests();
        sell.askChart = false;
        sell.m_ArmedDirection = -1;
        sell.Bars.High[0] = 100.75;
        sell.Bars.Low[0] = 100;
        sell.Bars.High[1] = 101;
        sell.m_FastEMA[0] = 101;
        sell.m_SlowEMA[0] = 101.25;
        sell.m_ProfileEMA[0] = 101.5;
        sell.UpdateFourTickPinBarProjection(.25, 0);
        Check(sell.drawingVisible && sell.drawnEntry == 99.75 &&
              sell.m_PinEntryCandidateDirection == -1, "live sell projection");
        sell.Bars.High[0] = 101;
        sell.UpdateFourTickPinBarProjection(.25, 0);
        Check(sell.drawnEntry == 100 && sell.m_PinEntryCandidateBodyTicks == 0,
              "sell entry glides to PB0");
        sell.m_ActiveEntrySetup = EEntrySetup.PinBar;
        sell.Bars.High[1] = 100.75;
        sell.UpdateFourTickPinBarProjection(.25, 0);
        Check(!sell.drawingVisible && sell.cancellations == 1 && sell.m_AutoEntryArmed,
              "previous high failure cancels sell while staying armed");
        foreach (int side in new[] {1, -1}) {
            var watch = new FourTickPinBarTests();
            watch.askChart = side > 0;
            watch.m_ArmedDirection = side;
            watch.m_FastEMA[0] = side > 0 ? 99 : 101;
            watch.m_SlowEMA[0] = watch.m_FastEMA[0] - side * .25;
            watch.m_ProfileEMA[0] = watch.m_FastEMA[0] - side * .5;
            watch.Bars.Low[0] = side > 0 ? 99.25 : 100;
            watch.Bars.High[0] = side > 0 ? 100 : 100.75;
            watch.Bars.Close[1] = 100 + side * .25;
            watch.UpdateFourTickPinBarProjection(.25, 0);
            Check(watch.drawingVisible, "valid trend and prior color draws setup");
            watch.m_ActiveEntrySetup = EEntrySetup.PinBar;
            watch.m_SlowEMA[0] = watch.m_FastEMA[0];
            watch.m_PinEntryCandidateValid = false;
            watch.UpdateFourTickPinBarProjection(.25, 0);
            Check(!watch.drawingVisible && !watch.m_PinEntryCandidateValid && watch.cancellations == 1 &&
                  watch.m_ActiveEntrySetup == EEntrySetup.None && watch.m_AutoEntryArmed,
                  "live EMA equality cancels setup while remaining armed");
            watch.m_SlowEMA[0] = watch.m_FastEMA[0] - side * .25;
            watch.UpdateFourTickPinBarProjection(.25, 0);
            Check(watch.drawingVisible, "restored stack requalifies setup");
            watch.m_ActiveEntrySetup = EEntrySetup.PinBar;
            watch.Bars.Close[1] = 100 - side * .25;
            watch.m_PinEntryCandidateValid = false;
            watch.UpdateFourTickPinBarProjection(.25, 0);
            Check(!watch.drawingVisible && !watch.m_PinEntryCandidateValid && watch.cancellations == 2 &&
                  watch.m_AutoEntryArmed, "immediately preceding opposite color blocks setup");
            watch.Bars.Close[1] = watch.Bars.Open[1];
            watch.UpdateFourTickPinBarProjection(.25, 0);
            Check(watch.drawingVisible, "preceding doji qualifies");
            watch.m_ActiveEntrySetup = EEntrySetup.PinBar;
            watch.m_ProfileEMA = null;
            watch.UpdateFourTickPinBarProjection(.25, 0);
            Check(!watch.drawingVisible && watch.cancellations == 3 && watch.m_AutoEntryArmed,
                  "missing EMA clears pending setup safely");
        }
        Console.WriteLine("PASS: " + checks + " pin-bar assertions");
    }
}
