using System;
using System.Collections.Generic;
using System.Drawing;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Visual companion for the Range Bar Breakout Strategy. It identifies a
    // one- or two-bar counter-trend pullback inside an extreme EMA trend and
    // marks the one-tick breakout entry beyond the final pullback bar.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    public class RangeBarBreakoutDisplay : IndicatorObject
    {
        private const int FastEmaLength = 8;
        private const int SlowEmaLength = 24;
        private const int TrendEmaLength = 50;
        private const int SlopeBars = 3;
        private const double MinimumFastSlopeDegrees = 45.0;
        private const double MinimumSlowSlopeDegrees = 35.0;
        private const double MinimumTrendSlopeDegrees = 25.0;
        private const double MinimumFastSlowSeparationTicks = 6.0;
        private const double MinimumSlowTrendSeparationTicks = 4.0;
        private const double MinimumFastTrendSeparationTicks = 10.0;

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_TrendEMA;
        private IPlotObject m_FastEMAPlot;
        private IPlotObject m_SlowEMAPlot;
        private IPlotObject m_TrendEMAPlot;
        private readonly List<IDrawObject> m_DisplayDrawings =
            new List<IDrawObject>();
        private readonly HashSet<DateTime> m_MarkedBars = new HashSet<DateTime>();

        [Input] public bool ShowDisplay { get; set; }
        [Input] public bool ShowEMAs { get; set; }

        public RangeBarBreakoutDisplay(object ctx) : base(ctx)
        {
            ShowDisplay = true;
            ShowEMAs = true;
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this);
            m_SlowEMA = new XAverage(this);
            m_TrendEMA = new XAverage(this);
            m_FastEMAPlot = AddPlot(new PlotAttributes("Breakout 8 EMA",
                EPlotShapes.Line, Color.Yellow, Color.Empty, 2, 0, true));
            m_SlowEMAPlot = AddPlot(new PlotAttributes("Breakout 24 EMA",
                EPlotShapes.Line, Color.Black, Color.Empty, 2, 0, true));
            m_TrendEMAPlot = AddPlot(new PlotAttributes("Breakout 50 EMA",
                EPlotShapes.Line, Color.Green, Color.Empty, 2, 0, true));
        }

        protected override void StartCalc()
        {
            ClearDisplayDrawings();
            m_MarkedBars.Clear();
            m_FastEMA.Length = FastEmaLength;
            m_FastEMA.Price = Bars.Close;
            m_SlowEMA.Length = SlowEmaLength;
            m_SlowEMA.Price = Bars.Close;
            m_TrendEMA.Length = TrendEmaLength;
            m_TrendEMA.Price = Bars.Close;
        }

        protected override void CalcBar()
        {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;
            if (ShowEMAs)
            {
                m_FastEMAPlot.Set(m_FastEMA[0], Color.Yellow);
                m_SlowEMAPlot.Set(m_SlowEMA[0], Color.Black);
                m_TrendEMAPlot.Set(m_TrendEMA[0], Color.Green);
            }

            if (!ShowDisplay || Bars.Status != EBarState.Close ||
                Bars.CurrentBar < SlopeBars + 3)
                return;
            if (m_MarkedBars.Contains(Bars.Time[0])) return;

            int direction = GetExtremeBiasDirection(tickSize);
            if (direction == 0 || !IsFinalPullbackBar(direction)) return;

            int pullbackBars = IsPullbackBar(direction, 1) ? 2 : 1;
            // A third counter-trend bar makes the pullback too deep.
            if (pullbackBars == 2 && IsPullbackBar(direction, 2)) return;
            if (pullbackBars == 2 && !IsBodyOnTrendSide(direction, 1)) return;

            double breakoutPrice = direction < 0
                ? Bars.Low[0] - tickSize : Bars.High[0] + tickSize;
            DrawBreakoutMarker(direction, breakoutPrice);
            m_MarkedBars.Add(Bars.Time[0]);
        }

        private int GetExtremeBiasDirection(double tickSize)
        {
            double fast = m_FastEMA[0];
            double slow = m_SlowEMA[0];
            double trend = m_TrendEMA[0];
            int direction = fast < slow && slow < trend ? -1 :
                            fast > slow && slow > trend ? 1 : 0;
            if (direction == 0) return 0;
            if (Math.Abs(fast - slow) / tickSize < MinimumFastSlowSeparationTicks ||
                Math.Abs(slow - trend) / tickSize < MinimumSlowTrendSeparationTicks ||
                Math.Abs(fast - trend) / tickSize < MinimumFastTrendSeparationTicks)
                return 0;
            double fastSlope = GetAngle(m_FastEMA[0], m_FastEMA[SlopeBars],
                                        SlopeBars, tickSize);
            double slowSlope = GetAngle(m_SlowEMA[0], m_SlowEMA[SlopeBars],
                                        SlopeBars, tickSize);
            double trendSlope = GetAngle(m_TrendEMA[0], m_TrendEMA[SlopeBars],
                                         SlopeBars, tickSize);
            if (direction < 0)
                return fastSlope <= -MinimumFastSlopeDegrees &&
                       slowSlope <= -MinimumSlowSlopeDegrees &&
                       trendSlope <= -MinimumTrendSlopeDegrees ? -1 : 0;
            return fastSlope >= MinimumFastSlopeDegrees &&
                   slowSlope >= MinimumSlowSlopeDegrees &&
                   trendSlope >= MinimumTrendSlopeDegrees ? 1 : 0;
        }

        private bool IsFinalPullbackBar(int direction)
        {
            return IsPullbackBar(direction, 0) && IsBodyOnTrendSide(direction, 0);
        }

        private bool IsPullbackBar(int direction, int barsBack)
        {
            return direction < 0
                ? Bars.Close[barsBack] > Bars.Open[barsBack]
                : Bars.Close[barsBack] < Bars.Open[barsBack];
        }

        private bool IsBodyOnTrendSide(int direction, int barsBack)
        {
            double bodyHigh = Math.Max(Bars.Open[barsBack], Bars.Close[barsBack]);
            double bodyLow = Math.Min(Bars.Open[barsBack], Bars.Close[barsBack]);
            return direction < 0 ? bodyHigh <= m_FastEMA[barsBack] :
                                  bodyLow >= m_FastEMA[barsBack];
        }

        private double GetAngle(double current, double old, int barsBack,
                                double tickSize)
        {
            return Math.Atan2(current - old, barsBack * tickSize) *
                   (180.0 / Math.PI);
        }

        private void DrawBreakoutMarker(int direction, double breakoutPrice)
        {
            TimeSpan barSpan = Bars.Time[0] - Bars.Time[1];
            if (barSpan <= TimeSpan.Zero || barSpan > TimeSpan.FromMinutes(5))
                barSpan = TimeSpan.FromSeconds(1);
            long halfWidthTicks = Math.Max(1, barSpan.Ticks / 3);
            ChartPoint begin = new ChartPoint(Bars.Time[0].AddTicks(-halfWidthTicks),
                                              breakoutPrice);
            ChartPoint end = new ChartPoint(Bars.Time[0].AddTicks(halfWidthTicks),
                                            breakoutPrice);
            ITrendLineObject marker = DrwTrendLine.Create(begin, end);
            if (marker == null) return;
            marker.ExtRight = false;
            marker.Color = direction < 0 ? Color.Red : Color.Blue;
            marker.Style = ETLStyle.ToolSolid;
            marker.Size = 3;
            m_DisplayDrawings.Add(marker);
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
