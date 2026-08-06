using System;
using System.Drawing;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true)]
    [UpdateOnEveryTick(true)]
    public class RangeBarTradingEMAs : IndicatorObject
    {
        private const int FastEmaLength = 8;
        private const int SlowEmaLength = 24;
        private const int TrendEmaLength = 50;
        private const int MinimumSeparationTicks = 3;
        private const int SlopeLookbackBars = 3;
        private const double MinimumSlopeDegrees = 20.0;

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_TrendEMA;
        private IPlotObject m_FastEMAPlot;
        private IPlotObject m_SlowEMAPlot;
        private IPlotObject m_TrendEMAPlot;

        [Input] public bool Show50EMA { get; set; }

        public RangeBarTradingEMAs(object ctx) : base(ctx)
        {
            Show50EMA = true;
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this);
            m_SlowEMA = new XAverage(this);
            m_TrendEMA = new XAverage(this);
            m_FastEMAPlot = AddPlot(new PlotAttributes(
                "Range Bar Trading 8 EMA", EPlotShapes.Line,
                Color.DarkGray, Color.Empty, 2, 0, true));
            m_SlowEMAPlot = AddPlot(new PlotAttributes(
                "Range Bar Trading 24 EMA", EPlotShapes.Line,
                Color.Black, Color.Empty, 2, 0, true));
            m_TrendEMAPlot = AddPlot(new PlotAttributes(
                "Range Bar Trading 50 EMA", EPlotShapes.Line,
                Color.Green, Color.Empty, 2, 0, true));
        }

        protected override void StartCalc()
        {
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

            Color color = IsQualified(tickSize) ? Color.Yellow : Color.DarkGray;
            m_FastEMAPlot.Set(m_FastEMA[0], color);
            m_SlowEMAPlot.Set(m_SlowEMA[0], Color.Black);
            // Color.Empty falls back to the plot's configured green color in
            // MultiCharts. Use an empty numeric value and transparency so all
            // historical 50-EMA plot points are actually removed on recalc.
            m_TrendEMAPlot.Set(Show50EMA ? m_TrendEMA[0] : Double.NaN,
                Show50EMA ? Color.Green : Color.Transparent);
        }

        private bool IsQualified(double tickSize)
        {
            if (Bars.CurrentBar < SlopeLookbackBars) return false;

            double minimumSeparation = MinimumSeparationTicks * tickSize;
            double slope = GetAngle(m_FastEMA[0],
                                    m_FastEMA[SlopeLookbackBars],
                                    SlopeLookbackBars,
                                    tickSize);

            bool bullish = m_FastEMA[0] - m_SlowEMA[0] >= minimumSeparation &&
                           slope > MinimumSlopeDegrees;
            bool bearish = m_SlowEMA[0] - m_FastEMA[0] >= minimumSeparation &&
                           slope < -MinimumSlopeDegrees;
            return bullish || bearish;
        }

        private double GetAngle(double currentValue, double oldValue,
                                int barsBack, double tickSize)
        {
            double rise = currentValue - oldValue;
            double run = barsBack * tickSize;
            return Math.Atan2(rise, run) * (180.0 / Math.PI);
        }
    }
}
