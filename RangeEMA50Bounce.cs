using System;
using System.Collections.Generic;
using System.Drawing;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Visual-only 50 EMA bounce detector. A deeper pullback may flatten the
    // fast EMAs on the bounce bar, so only the 50 EMA's prior trend strength
    // is required.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    public class RangeEMA50Bounce : IndicatorObject
    {
        private const int FastEmaLength = 8;
        private const int SlowEmaLength = 24;
        private const int TrendEmaLength = 50;
        private const int SlopeBars = 3;
        private const int TrendSlopeLookbackBars = 3;
        private const double TouchToleranceTicks = 0.75;
        private const double MinimumRecentTrendSlopeDegrees = 12.0;
        // Keep the 24/50 stack nearly half a range bar apart while allowing
        // fractional EMA values to qualify a 3.87-tick gap on an 8-tick bar.
        private const double MinimumSlowTrendSeparationBarFraction = 0.48;

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_TrendEMA;
        private readonly List<IDrawObject> m_DisplayDrawings =
            new List<IDrawObject>();

        [Input] public bool ShowDisplay { get; set; }

        public RangeEMA50Bounce(object ctx) : base(ctx)
        {
            ShowDisplay = true;
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this);
            m_SlowEMA = new XAverage(this);
            m_TrendEMA = new XAverage(this);
        }

        protected override void StartCalc()
        {
            ClearDisplayDrawings();
            m_FastEMA.Length = FastEmaLength;
            m_FastEMA.Price = Bars.Close;
            m_SlowEMA.Length = SlowEmaLength;
            m_SlowEMA.Price = Bars.Close;
            m_TrendEMA.Length = TrendEmaLength;
            m_TrendEMA.Price = Bars.Close;
        }

        protected override void CalcBar()
        {
            if (!ShowDisplay)
            {
                ClearDisplayDrawings();
                return;
            }
            if (Bars.Status != EBarState.Close ||
                Bars.CurrentBar < SlopeBars)
                return;

            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;
            int direction = GetBounceDirection();
            if (IsFiftyEmaBounce(direction, tickSize))
                DrawBounceArrow(direction, tickSize);
        }

        private int GetBounceDirection()
        {
            // A deep 50-EMA pullback may put the 8 EMA through the 24 EMA.
            // The 24/50 relationship is the directional context for 50E.
            return m_SlowEMA[0] > m_TrendEMA[0]
                ? 1
                : m_SlowEMA[0] < m_TrendEMA[0]
                    ? -1 : 0;
        }

        private bool IsFiftyEmaBounce(int direction, double tickSize)
        {
            if (direction == 0) return false;
            double gap = m_TrendEMA[0] < Bars.Low[0]
                ? (Bars.Low[0] - m_TrendEMA[0]) / tickSize
                : m_TrendEMA[0] > Bars.High[0]
                    ? (m_TrendEMA[0] - Bars.High[0]) / tickSize : 0;
            bool touchesTrend = gap <= TouchToleranceTicks;
            bool closeAndColor = direction > 0
                ? Bars.Close[0] >= Bars.Open[0] && Bars.Close[0] > m_TrendEMA[0]
                : Bars.Close[0] <= Bars.Open[0] && Bars.Close[0] < m_TrendEMA[0];
            double rangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            bool slowTrendSeparation = Math.Abs(m_SlowEMA[0] - m_TrendEMA[0]) /
                                       tickSize >= rangeTicks * MinimumSlowTrendSeparationBarFraction;
            return touchesTrend && closeAndColor && slowTrendSeparation &&
                   HasRecentDirectionalTrendSlope(direction, tickSize);
        }

        private bool HasRecentDirectionalTrendSlope(int direction, double tickSize)
        {
            // The bounce may briefly flatten the 50 EMA.  Retain its trend
            // only when one of the three prior completed bars was at least
            // 12 degrees in the signal direction.
            for (int barsBack = 1; barsBack <= TrendSlopeLookbackBars; barsBack++)
            {
                double trendSlope = GetAngle(m_TrendEMA[barsBack],
                    m_TrendEMA[barsBack + SlopeBars], SlopeBars, tickSize);
                if (direction > 0 && trendSlope >= MinimumRecentTrendSlopeDegrees)
                    return true;
                if (direction < 0 && trendSlope <= -MinimumRecentTrendSlopeDegrees)
                    return true;
            }
            return false;
        }

        private double GetAngle(double current, double old, int barsBack,
                                double tickSize)
        {
            return Math.Atan2(current - old, barsBack * tickSize) *
                   (180.0 / Math.PI);
        }

        private void DrawBounceArrow(int direction, double tickSize)
        {
            double arrowPrice = direction > 0
                ? Bars.Low[0] - (2 * tickSize) : Bars.High[0] + (2 * tickSize);
            IArrowObject arrow = DrwArrow.Create(
                new ChartPoint(Bars.Time[0], arrowPrice), direction < 0);
            if (arrow != null)
            {
                arrow.Color = Color.Red;
                arrow.Size = 4;
                m_DisplayDrawings.Add(arrow);
            }
            double labelPrice = direction > 0
                ? Bars.Low[0] - (4 * tickSize) : Bars.High[0] + (4 * tickSize);
            ITextObject label = DrwText.Create(
                new ChartPoint(Bars.Time[0], labelPrice), "50");
            if (label == null) return;
            label.Color = Color.Red;
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
