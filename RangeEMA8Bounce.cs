using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Visual-only 8 EMA bounce detector. Its thresholds are provisional and
    // can be refined from the Alt+W diagnostics.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    [MouseEvents(true)]
    public class RangeEMA8Bounce : IndicatorObject
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        private const int FastEmaLength = 8;
        private const int SlowEmaLength = 24;
        private const int SlopeBars = 3;
        private const int SlopeLookbackBars = 6;
        private const int DisplacementLookbackBars = 2;
        private const double MinimumSeparationTicks = 4.5;
        private const double MinimumFastSlopeDegrees = 45.0;
        private const double MinimumSlowSlopeDegrees = 40.0;
        private const double MinimumFastSlopeLeadDegrees = 2.5;
        private const double MinimumPenetrationTicks = 1.0;
        private const double MaximumPenetrationTicks = 3.5;
        // EMA values are fractional-price values while range-bar highs/lows
        // are tick prices.  Compare the measured penetration at the same
        // half-tick precision as the 3.5-tick rule so an EMA interpolation
        // artifact (for example, 3.54t) does not reject a 3.5t pullback.
        private const double PenetrationComparisonIncrementTicks = 0.5;
        private const double MinimumLocalDisplacementTicks = 1.0;
        // Treat each displayed arrow as a completed virtual entry.  A new
        // arrow cannot be printed until that virtual trade has reached one
        // of these exits on a later completed bar.
        private const int ReentryProfitTargetTicks = 5;
        private const int ReentryStopLossTicks = 10;

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private readonly List<IDrawObject> m_DisplayDrawings =
            new List<IDrawObject>();
        private readonly Dictionary<DateTime, BounceDiagnostic> m_DiagnosticsByTime =
            new Dictionary<DateTime, BounceDiagnostic>();
        private bool m_VirtualTradeActive;
        private int m_VirtualTradeDirection;
        private double m_VirtualTradeEntryPrice;

        [Input] public bool ShowDisplay { get; set; }

        private class BounceDiagnostic
        {
            public int Direction;
            public double FastEma;
            public double SlowEma;
            public double SeparationTicks;
            public double BestSeparationTicks;
            public double FastSlopeNow;
            public double SlowSlopeNow;
            public double BestFastSlope;
            public double BestSlowSlope;
            public double SlopeLeadDegrees;
            public double PenetrationTicks;
            public double LocalDisplacementTicks;
            public double Open;
            public double High;
            public double Low;
            public double Close;
            public double LowToFastTicks;
            public double HighToFastTicks;
            public double CloseToFastTicks;
            public bool RangeCrossesFast;
            public bool CloseOnTrendSide;
            public string Body;
            public bool SeparationPass;
            public bool FastSlopePass;
            public bool SlowSlopePass;
            public bool SlopeLeadPass;
            public bool PenetrationPass;
            public bool BarColorPass;
            public bool LocalDisplacementPass;
            public bool SignalPass;
        }

        public RangeEMA8Bounce(object ctx) : base(ctx)
        {
            ShowDisplay = true;
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this);
            m_SlowEMA = new XAverage(this);
        }

        protected override void StartCalc()
        {
            ClearDisplayDrawings();
            m_DiagnosticsByTime.Clear();
            ResetVirtualTrade();
            m_FastEMA.Length = FastEmaLength;
            m_FastEMA.Price = Bars.Close;
            m_SlowEMA.Length = SlowEmaLength;
            m_SlowEMA.Price = Bars.Close;
        }

        protected override void CalcBar()
        {
            if (!ShowDisplay)
            {
                ClearDisplayDrawings();
                return;
            }

            if (Bars.Status != EBarState.Close ||
                Bars.CurrentBar < SlopeBars + SlopeLookbackBars)
                return;

            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;

            // Evaluate any open virtual trade before looking for a new setup.
            // The exit bar is consumed, so it cannot also be a new entry.
            if (m_VirtualTradeActive)
            {
                UpdateVirtualTrade(tickSize);
                return;
            }

            BounceDiagnostic diagnostic = BuildDiagnostic(tickSize);
            m_DiagnosticsByTime[Bars.Time[0]] = diagnostic;
            if (diagnostic.SignalPass)
            {
                DrawBounceArrow(diagnostic.Direction, tickSize);
                StartVirtualTrade(diagnostic.Direction, Bars.Close[0]);
            }
        }

        private void StartVirtualTrade(int direction, double entryPrice)
        {
            m_VirtualTradeActive = direction != 0;
            m_VirtualTradeDirection = direction;
            m_VirtualTradeEntryPrice = entryPrice;
        }

        private void UpdateVirtualTrade(double tickSize)
        {
            double profitTarget = m_VirtualTradeDirection > 0
                ? m_VirtualTradeEntryPrice + (ReentryProfitTargetTicks * tickSize)
                : m_VirtualTradeEntryPrice - (ReentryProfitTargetTicks * tickSize);
            double stopPrice = m_VirtualTradeDirection > 0
                ? m_VirtualTradeEntryPrice - (ReentryStopLossTicks * tickSize)
                : m_VirtualTradeEntryPrice + (ReentryStopLossTicks * tickSize);

            bool targetReached = m_VirtualTradeDirection > 0
                ? Bars.High[0] >= profitTarget
                : Bars.Low[0] <= profitTarget;
            bool stoppedOut = m_VirtualTradeDirection > 0
                ? Bars.Low[0] <= stopPrice
                : Bars.High[0] >= stopPrice;

            // Range-bar OHLC has no intrabar sequence.  When both are touched,
            // apply stop-first handling instead of assuming a target fill.
            if (stoppedOut || targetReached)
                ResetVirtualTrade();
        }

        private void ResetVirtualTrade()
        {
            m_VirtualTradeActive = false;
            m_VirtualTradeDirection = 0;
            m_VirtualTradeEntryPrice = 0;
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            // Alt+W + left-click a completed range bar to inspect the 8 EMA
            // bounce geometry and trend relationship for that exact bar.
            if (arg.buttons != MouseButtons.Left || !IsAltHeld(arg.keys) ||
                !IsWHeld(arg.keys))
                return;

            BounceDiagnostic diagnostic;
            if (!m_DiagnosticsByTime.TryGetValue(arg.point.Time, out diagnostic))
                return;
            ShowDiagnosticPopup(arg.point.Time, diagnostic);
        }

        private bool IsWHeld(Keys eventKeys)
        {
            if ((eventKeys & Keys.KeyCode) == Keys.W) return true;
            try { return (GetAsyncKeyState((int)Keys.W) & 0x8000) != 0; }
            catch { return false; }
        }

        private bool IsAltHeld(Keys eventKeys)
        {
            if ((eventKeys & Keys.Alt) == Keys.Alt) return true;
            try { return (GetAsyncKeyState((int)Keys.Menu) & 0x8000) != 0; }
            catch { return false; }
        }

        private BounceDiagnostic BuildDiagnostic(double tickSize)
        {
            BounceDiagnostic result = new BounceDiagnostic();
            result.FastEma = m_FastEMA[0];
            result.SlowEma = m_SlowEMA[0];
            result.Direction = result.FastEma > result.SlowEma ? 1 :
                               result.FastEma < result.SlowEma ? -1 : 0;
            result.SeparationTicks = Math.Abs(result.FastEma - result.SlowEma) /
                                     tickSize;
            result.BestSeparationTicks = GetBestSeparation(tickSize);

            result.FastSlopeNow = GetAngle(m_FastEMA[0], m_FastEMA[SlopeBars],
                                            SlopeBars, tickSize);
            result.SlowSlopeNow = GetAngle(m_SlowEMA[0], m_SlowEMA[SlopeBars],
                                            SlopeBars, tickSize);
            result.BestFastSlope = GetBestDirectionalSlope(m_FastEMA,
                                                            result.Direction, tickSize);
            result.BestSlowSlope = GetBestDirectionalSlope(m_SlowEMA,
                                                            result.Direction, tickSize);
            // A pullback can soften the current 3-bar reading.  Use the same
            // best-directional values shown in the diagnostic so the visual
            // detector matches the examples being tuned.
            result.SlopeLeadDegrees = result.Direction > 0
                ? result.BestFastSlope - result.BestSlowSlope
                : result.Direction < 0
                    ? Math.Abs(result.BestFastSlope) - Math.Abs(result.BestSlowSlope)
                    : 0;

            result.Open = Bars.Open[0];
            result.High = Bars.High[0];
            result.Low = Bars.Low[0];
            result.Close = Bars.Close[0];
            result.LowToFastTicks = (result.Low - result.FastEma) / tickSize;
            result.HighToFastTicks = (result.High - result.FastEma) / tickSize;
            result.CloseToFastTicks = (result.Close - result.FastEma) / tickSize;
            result.RangeCrossesFast = result.Low <= result.FastEma &&
                                      result.High >= result.FastEma;
            result.CloseOnTrendSide = result.Direction > 0
                ? result.Close >= result.FastEma
                : result.Direction < 0 && result.Close <= result.FastEma;
            result.Body = result.Close > result.Open ? "BULLISH" :
                          result.Close < result.Open ? "BEARISH" : "ZERO-BODY";
            result.PenetrationTicks = result.Direction > 0
                ? -result.LowToFastTicks
                : result.Direction < 0 ? result.HighToFastTicks : 0;
            result.LocalDisplacementTicks = GetLocalDisplacementTicks(
                result.Direction, tickSize);

            // The initial values are from the supplied positive examples.
            // The pullback itself can soften the current three-bar slope, so
            // use the best directional slope in the recent 0-6 bar window.
            result.SeparationPass = result.Direction != 0 &&
                                    result.SeparationTicks >= MinimumSeparationTicks;
            result.FastSlopePass = result.Direction > 0
                ? result.BestFastSlope >= MinimumFastSlopeDegrees
                : result.Direction < 0 &&
                  result.BestFastSlope <= -MinimumFastSlopeDegrees;
            result.SlowSlopePass = result.Direction > 0
                ? result.BestSlowSlope >= MinimumSlowSlopeDegrees
                : result.Direction < 0 &&
                  result.BestSlowSlope <= -MinimumSlowSlopeDegrees;
            result.SlopeLeadPass = result.SlopeLeadDegrees >=
                                   MinimumFastSlopeLeadDegrees;
            double comparablePenetration = RoundToIncrement(
                result.PenetrationTicks, PenetrationComparisonIncrementTicks);
            result.PenetrationPass = result.RangeCrossesFast &&
                comparablePenetration >= MinimumPenetrationTicks &&
                comparablePenetration <= MaximumPenetrationTicks;
            result.BarColorPass = result.Direction > 0
                ? result.Close > result.Open
                : result.Direction < 0 && result.Close <= result.Open;
            result.LocalDisplacementPass = result.LocalDisplacementTicks >=
                                           MinimumLocalDisplacementTicks;
            result.SignalPass = result.SeparationPass && result.FastSlopePass &&
                result.SlowSlopePass && result.SlopeLeadPass &&
                result.PenetrationPass && result.CloseOnTrendSide &&
                result.BarColorPass && result.LocalDisplacementPass;
            return result;
        }

        private double GetBestSeparation(double tickSize)
        {
            double best = 0;
            for (int barsBack = 0; barsBack <= SlopeLookbackBars; barsBack++)
                best = Math.Max(best, Math.Abs(m_FastEMA[barsBack] -
                                                m_SlowEMA[barsBack]) / tickSize);
            return best;
        }

        private double RoundToIncrement(double value, double increment)
        {
            if (increment <= 0) return value;
            return Math.Round(value / increment) * increment;
        }

        private double GetLocalDisplacementTicks(int direction, double tickSize)
        {
            if (direction == 0 || Bars.CurrentBar < DisplacementLookbackBars)
                return 0;

            double reference = direction > 0 ? Double.PositiveInfinity :
                                                Double.NegativeInfinity;
            for (int barsBack = 1; barsBack <= DisplacementLookbackBars;
                 barsBack++)
            {
                reference = direction > 0
                    ? Math.Min(reference, Bars.Low[barsBack])
                    : Math.Max(reference, Bars.High[barsBack]);
            }
            return direction > 0
                ? (reference - Bars.Low[0]) / tickSize
                : (Bars.High[0] - reference) / tickSize;
        }

        private double GetBestDirectionalSlope(XAverage ema, int direction,
                                               double tickSize)
        {
            if (direction == 0) return 0;
            double best = direction > 0 ? Double.NegativeInfinity :
                                          Double.PositiveInfinity;
            for (int barsBack = 0; barsBack <= SlopeLookbackBars; barsBack++)
            {
                double angle = GetAngle(ema[barsBack], ema[barsBack + SlopeBars],
                                        SlopeBars, tickSize);
                best = direction > 0 ? Math.Max(best, angle) : Math.Min(best, angle);
            }
            return best;
        }

        private void ShowDiagnosticPopup(DateTime time, BounceDiagnostic d)
        {
            string direction = d.Direction > 0 ? "LONG CONTEXT" :
                               d.Direction < 0 ? "SHORT CONTEXT" : "FLAT";
            string text = string.Format(
                "8 EMA BOUNCE DIAGNOSTIC ({0})\n" +
                "8 / 24 EMA: {1:F2} / {2:F2}  [{3}]\n" +
                "8/24 separation now: {4:F2}t | best (0-6): {5:F2}t\n" +
                "8 slope now / best: {6:F1}\u00B0 / {7:F1}\u00B0\n" +
                "24 slope now / best: {8:F1}\u00B0 / {9:F1}\u00B0\n" +
                "8 slope lead over 24 (best): {10:F1}\u00B0\n" +
                "OHLC: {11:F2} / {12:F2} / {13:F2} / {14:F2} [{15}]\n" +
                "Bar vs 8 EMA -- low: {16:F2}t, high: {17:F2}t, close: {18:F2}t\n" +
                "Pierces / penetration: [{19}] / {20:F2}t [{21}]\n" +
                "Close on trend side: [{22}] | Bar color: [{23}]\n" +
                "PROVISIONAL 8 EMA BOUNCE: [{24}]\n" +
                "Rules -- separation >= 4.5t [{25}], 8 slope >= 45\u00B0 [{26}], " +
                "24 slope >= 40\u00B0 [{27}], 8 lead >= 2.5\u00B0 [{28}], " +
                "penetration 1-3.5t.\n" +
                "Local displacement vs prior 2 bars: {29:F2}t [{30}] (min 1t).",
                time.ToString("yyyy-MM-dd HH:mm:ss"), d.FastEma, d.SlowEma,
                direction, d.SeparationTicks, d.BestSeparationTicks,
                d.FastSlopeNow, d.BestFastSlope, d.SlowSlopeNow, d.BestSlowSlope,
                d.SlopeLeadDegrees, d.Open, d.High, d.Low, d.Close, d.Body,
                d.LowToFastTicks, d.HighToFastTicks, d.CloseToFastTicks,
                d.RangeCrossesFast ? "YES" : "NO",
                d.PenetrationTicks, d.PenetrationPass ? "PASS" : "FAIL",
                d.CloseOnTrendSide ? "PASS" : "FAIL",
                d.BarColorPass ? "PASS" : "FAIL",
                d.SignalPass ? "PASS" : "FAIL",
                d.SeparationPass ? "PASS" : "FAIL",
                d.FastSlopePass ? "PASS" : "FAIL",
                d.SlowSlopePass ? "PASS" : "FAIL",
                d.SlopeLeadPass ? "PASS" : "FAIL",
                d.LocalDisplacementTicks,
                d.LocalDisplacementPass ? "PASS" : "FAIL");
            try
            {
                MessageBox.Show(text, "Range 8 EMA Bounce: " +
                                time.ToString("yyyy-MM-dd HH:mm:ss"),
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
        }

        private double GetAngle(double valueCurrent, double valueOld,
                                int barsBack, double tickSize)
        {
            return Math.Atan2(valueCurrent - valueOld, barsBack * tickSize) *
                   (180.0 / Math.PI);
        }

        private void DrawBounceArrow(int direction, double tickSize)
        {
            // Do not rely on the small arrow alone.  Some chart themes render
            // an arrow almost indistinguishably from a range-bar wick, and an
            // unavailable arrow object used to make a passed setup appear to
            // have no signal at all.  Keep a text marker as the definitive
            // historical indication of every qualifying 8-EMA bounce.
            double price = direction > 0
                ? Bars.Low[0] - (2 * tickSize)
                : Bars.High[0] + (2 * tickSize);
            IArrowObject arrow = DrwArrow.Create(
                new ChartPoint(Bars.Time[0], price), direction < 0);
            if (arrow != null)
            {
                // Green identifies the 8-EMA bounce family in either
                // direction; arrow orientation carries the trade direction.
                arrow.Color = Color.MediumSeaGreen;
                arrow.Size = 4;
                m_DisplayDrawings.Add(arrow);
            }

            double labelPrice = direction > 0
                ? Bars.Low[0] - (4 * tickSize)
                : Bars.High[0] + (4 * tickSize);
            ITextObject label = DrwText.Create(
                new ChartPoint(Bars.Time[0], labelPrice), "8E");
            if (label == null) return;

            label.Color = Color.MediumSeaGreen;
            label.Size = 9;
            label.HStyle = ETextStyleH.Right;
            label.VStyle = direction > 0 ? ETextStyleV.Below : ETextStyleV.Above;
            m_DisplayDrawings.Add(label);
        }

        private void ClearDisplayDrawings()
        {
            foreach (IDrawObject drawing in m_DisplayDrawings)
            {
                if (drawing == null) continue;
                try { drawing.Delete(); }
                catch { }
            }
            m_DisplayDrawings.Clear();
        }
    }
}
