using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Historical/display-only 24 EMA bounce visualizer. It has no trading,
    // projections, or order-management code; mouse input is diagnostics only.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    [MouseEvents(true)]
    public class RangeEMA24Bounce : IndicatorObject
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        private const int FastEmaLength = 8;
        private const int SlowEmaLength = 24;
        private const int SlopeBars = 3;
        private const int SlopeLookbackBars = 6;
        private const double MinimumSlopeDegrees = 20.0;
        private const double EmaTouchToleranceTicks = 1.25;
        private const double MinimumEmaSeparationTicks = 2.0;
        private const double MinimumCurrentEmaSeparationTicks = 1.5;
        // Treat each displayed arrow as a completed virtual entry.  A new
        // arrow cannot be printed until that virtual trade has reached one
        // of these exits on a later completed bar.
        private const int ReentryProfitTargetTicks = 5;
        private const int ReentryStopLossTicks = 10;

        [Input] public bool ShowDisplay { get; set; }

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private readonly List<IDrawObject> m_DisplayDrawings =
            new List<IDrawObject>();
        private readonly Dictionary<DateTime, BounceDiagnostic> m_DiagnosticsByTime =
            new Dictionary<DateTime, BounceDiagnostic>();
        private bool m_VirtualTradeActive;
        private int m_VirtualTradeDirection;
        private double m_VirtualTradeEntryPrice;

        private class BounceDiagnostic
        {
            public int Direction;
            public double FastEma;
            public double SlowEma;
            public double SeparationTicks;
            public double BestSeparationTicks;
            public double RequiredSeparationTicks;
            public double BestSlopeDegrees;
            public double EmaRangeGapTicks;
            public bool SeparationPass;
            public bool CurrentSeparationPass;
            public bool SlopePass;
            public bool TouchPass;
            public bool ClosePass;
            public bool BarColorPass;
        }

        public RangeEMA24Bounce(object ctx) : base(ctx)
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

            // Mark only a completed bar; a live bar is not a historical
            // signal and must not create an early or repeated arrow.
            if (Bars.Status != EBarState.Close ||
                Bars.CurrentBar < SlopeBars + SlopeLookbackBars) return;

            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;

            // This is deliberately evaluated before looking for a new setup.
            // It also consumes the bar that completes the virtual trade, so
            // that one bar cannot both close a trade and open another one.
            if (m_VirtualTradeActive)
            {
                UpdateVirtualTrade(tickSize);
                return;
            }

            BounceDiagnostic diagnostic = BuildDiagnostic(tickSize);
            m_DiagnosticsByTime[Bars.Time[0]] = diagnostic;
            if (!diagnostic.SeparationPass || !diagnostic.SlopePass ||
                !diagnostic.TouchPass || !diagnostic.ClosePass ||
                !diagnostic.BarColorPass)
                return;

            DrawBounceArrow(diagnostic.Direction, tickSize);
            StartVirtualTrade(diagnostic.Direction, Bars.Close[0]);
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

            // A completed range bar provides only its high and low, not the
            // intrabar path.  If both exits are touched, use stop-first
            // handling so historical arrows never assume an unavailable fill.
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
            // Alt+D + left-click any historical bar to see the exact values and
            // pass/fail gates used for that bar. This remains display-only.
            if (arg.buttons != MouseButtons.Left || !IsAltHeld(arg.keys) ||
                !IsDHeld(arg.keys))
                return;

            BounceDiagnostic diagnostic;
            if (!m_DiagnosticsByTime.TryGetValue(arg.point.Time, out diagnostic))
                return;
            ShowDiagnosticPopup(arg.point.Time, diagnostic);
        }

        private bool IsDHeld(Keys eventKeys)
        {
            if ((eventKeys & Keys.KeyCode) == Keys.D) return true;
            try { return (GetAsyncKeyState((int)Keys.D) & 0x8000) != 0; }
            catch { return false; }
        }

        private bool IsAltHeld(Keys eventKeys)
        {
            if ((eventKeys & Keys.Alt) == Keys.Alt) return true;
            try { return (GetAsyncKeyState((int)Keys.Menu) & 0x8000) != 0; }
            catch { return false; }
        }

        private int GetTrendDirection()
        {
            if (m_FastEMA[0] > m_SlowEMA[0]) return 1;
            if (m_FastEMA[0] < m_SlowEMA[0]) return -1;
            return 0;
        }

        private BounceDiagnostic BuildDiagnostic(double tickSize)
        {
            BounceDiagnostic result = new BounceDiagnostic();
            result.Direction = GetTrendDirection();
            result.FastEma = m_FastEMA[0];
            result.SlowEma = m_SlowEMA[0];
            result.RequiredSeparationTicks = MinimumEmaSeparationTicks;
            result.SeparationTicks = result.Direction > 0
                ? m_FastEMA[0] - m_SlowEMA[0]
                : m_SlowEMA[0] - m_FastEMA[0];
            result.SeparationTicks /= tickSize;
            result.BestSeparationTicks = GetBestDirectionalSeparation(
                result.Direction, tickSize);
            result.CurrentSeparationPass = result.Direction != 0 &&
                result.SeparationTicks >= MinimumCurrentEmaSeparationTicks;
            result.SeparationPass = result.CurrentSeparationPass &&
                result.BestSeparationTicks >= result.RequiredSeparationTicks;

            result.BestSlopeDegrees = GetBestDirectionalSlope(
                result.Direction, tickSize);
            result.SlopePass = result.Direction > 0
                ? result.BestSlopeDegrees >= MinimumSlopeDegrees
                : result.Direction < 0 &&
                  result.BestSlopeDegrees <= -MinimumSlopeDegrees;

            if (m_SlowEMA[0] < Bars.Low[0])
                result.EmaRangeGapTicks = (Bars.Low[0] - m_SlowEMA[0]) / tickSize;
            else if (m_SlowEMA[0] > Bars.High[0])
                result.EmaRangeGapTicks = (m_SlowEMA[0] - Bars.High[0]) / tickSize;
            else
                result.EmaRangeGapTicks = 0;
            result.TouchPass = result.EmaRangeGapTicks <= EmaTouchToleranceTicks;
            result.ClosePass = result.Direction > 0
                ? Bars.Close[0] > m_SlowEMA[0]
                : result.Direction < 0 && Bars.Close[0] < m_SlowEMA[0];
            // A zero-body range bar has no chart color.  In a bearish 24 EMA
            // bounce it represents the completed rejection at the EMA, so
            // classify it as bearish rather than rejecting the setup merely
            // because Open and Close are equal.
            result.BarColorPass = result.Direction > 0
                ? Bars.Close[0] > Bars.Open[0]
                : result.Direction < 0 && Bars.Close[0] <= Bars.Open[0];
            return result;
        }

        private double GetBestDirectionalSeparation(int direction, double tickSize)
        {
            if (direction == 0) return 0;
            double best = Double.NegativeInfinity;
            for (int barsBack = 0; barsBack <= SlopeLookbackBars; barsBack++)
            {
                double separation = direction > 0
                    ? m_FastEMA[barsBack] - m_SlowEMA[barsBack]
                    : m_SlowEMA[barsBack] - m_FastEMA[barsBack];
                best = Math.Max(best, separation / tickSize);
            }
            return best;
        }

        private double GetBestDirectionalSlope(int direction, double tickSize)
        {
            // Use the strategy's normalized angle: EMA price rise over a run
            // of one minimum price increment per bar.  A pullback can flatten
            // today's reading, so any qualifying directional slope from this
            // bar through six bars ago validates the setup.
            double best = direction > 0 ? Double.NegativeInfinity : Double.PositiveInfinity;
            if (direction == 0) return 0;
            for (int barsBack = 0; barsBack <= SlopeLookbackBars; barsBack++)
            {
                double angle = GetAngle(m_SlowEMA[barsBack],
                                        m_SlowEMA[barsBack + SlopeBars],
                                        SlopeBars, tickSize);
                if (direction > 0) best = Math.Max(best, angle);
                else best = Math.Min(best, angle);
            }
            return best;
        }

        private void ShowDiagnosticPopup(DateTime time, BounceDiagnostic diagnostic)
        {
            string direction = diagnostic.Direction > 0 ? "LONG" :
                diagnostic.Direction < 0 ? "SHORT" : "FLAT";
            string text = string.Format(
                "24 EMA BOUNCE DIAGNOSTIC ({0})\n" +
                "8/24: {1:F2} / {2:F2}\n" +
                "8/24 separation now: {3:F2}t / {4:F2}t [{5}]\n" +
                "Best separation (0-6): {6:F2}t / {7:F2}t [{8}]\n" +
                "Best 24 slope (0-6): {9:F1} degrees [{10}]\n" +
                "24 EMA-to-bar gap: {11:F2}t / {12:F2}t [{13}]\n" +
                "Close on trend side: [{14}]\n" +
                "Bar color matches direction: [{15}]",
                direction, diagnostic.FastEma, diagnostic.SlowEma,
                diagnostic.SeparationTicks, MinimumCurrentEmaSeparationTicks,
                diagnostic.CurrentSeparationPass ? "PASS" : "FAIL",
                diagnostic.BestSeparationTicks, diagnostic.RequiredSeparationTicks,
                diagnostic.SeparationPass ? "PASS" : "FAIL",
                diagnostic.BestSlopeDegrees,
                diagnostic.SlopePass ? "PASS" : "FAIL",
                diagnostic.EmaRangeGapTicks,
                EmaTouchToleranceTicks,
                diagnostic.TouchPass ? "PASS" : "FAIL",
                diagnostic.ClosePass ? "PASS" : "FAIL",
                diagnostic.BarColorPass ? "PASS" : "FAIL");
            try
            {
                MessageBox.Show(text, "24 EMA Bounce: " + time.ToString("yyyy-MM-dd HH:mm:ss"),
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
        }

        private void DrawBounceArrow(int direction, double tickSize)
        {
            // Purple identifies the 24-EMA bounce family in either direction;
            // DrwArrow uses false for an up arrow and true for a down arrow.
            double price = direction > 0
                ? Bars.Low[0] - (2 * tickSize)
                : Bars.High[0] + (2 * tickSize);
            IArrowObject arrow = DrwArrow.Create(
                new ChartPoint(Bars.Time[0], price), direction < 0);
            if (arrow != null)
            {
                arrow.Color = Color.DarkViolet;
                arrow.Size = 4;
                m_DisplayDrawings.Add(arrow);
            }

            double labelPrice = direction > 0
                ? Bars.Low[0] - (4 * tickSize)
                : Bars.High[0] + (4 * tickSize);
            ITextObject label = DrwText.Create(
                new ChartPoint(Bars.Time[0], labelPrice), "24E");
            if (label == null) return;

            label.Color = Color.DarkViolet;
            label.Size = 9;
            label.HStyle = ETextStyleH.Right;
            label.VStyle = direction > 0 ? ETextStyleV.Below : ETextStyleV.Above;
            m_DisplayDrawings.Add(label);
        }

        private double GetAngle(double valueCurrent, double valueOld,
                                int barsBack, double tickSize)
        {
            double rise = valueCurrent - valueOld;
            double run = barsBack * tickSize;
            return Math.Atan2(rise, run) * (180.0 / Math.PI);
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

        protected override void Destroy()
        {
            ClearDisplayDrawings();
            m_DiagnosticsByTime.Clear();
            ResetVirtualTrade();
        }
    }
}
