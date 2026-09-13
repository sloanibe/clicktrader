using System;
using System.Drawing;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Visual-only study for selected 8- and 24-EMA pullbacks. Orange points
    // mark 8-EMA entries; black points mark 24-EMA entries.
    // It does not stage or transmit orders.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    public class RangeEMA8QualityBounce : IndicatorObject
    {
        private const int FastEmaLength = 8;
        private const int SlowEmaLength = 24;
        private const int TrendEmaLength = 50;
        private const int SlopeBars = 3;
        private const int PriorSlopeLookbackBars = 6;
        private const double OneBarMinimum24PriorSlopeDegrees = 20.0;
        private const double TwoBarMinimum24PriorSlopeDegrees = 39.0;
        private const double Minimum824SeparationTicks = 5.0;
        private const double MinimumCurrent8SlopeDegrees = 15.0;
        private const double MinimumPenetrationTicks = 1.0;
        private const double OneBarMaximumPenetrationTicks = 4.5;
        private const double TwoBarMaximumPenetrationTicks = 2.5;
        private const double MinimumLocalDisplacementTicks = 1.0;
        private const double Ema24MaximumPenetrationTicks = 5.0;

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_TrendEMA;
        private IPlotObject m_SignalPlot;
        private IPlotObject m_Ema24SignalPlot;

        [Input] public bool ShowDisplay { get; set; }
        [Input] public bool Show24EMABounces { get; set; }

        public RangeEMA8QualityBounce(object ctx) : base(ctx)
        {
            ShowDisplay = true;
            Show24EMABounces = true;
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this);
            m_SlowEMA = new XAverage(this);
            m_TrendEMA = new XAverage(this);
            m_SignalPlot = AddPlot(new PlotAttributes("Quality 8 EMA Bounce",
                EPlotShapes.Point, Color.Orange, Color.Empty, 12, 0, true));
            m_Ema24SignalPlot = AddPlot(new PlotAttributes("Quality 24 EMA Bounce",
                EPlotShapes.Point, Color.Black, Color.Empty, 12, 0, true));
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
            m_SignalPlot.Set(Double.NaN);
            m_Ema24SignalPlot.Set(Double.NaN);
            if (!ShowDisplay || Bars.Status != EBarState.Close ||
                Bars.CurrentBar < PriorSlopeLookbackBars + SlopeBars)
                return;

            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;
            int direction = GetDirection();
            if (direction == 0) return;
            if (IsQuality8Bounce(direction, tickSize))
                Plot8EntryMarker(direction, tickSize);
            if (Show24EMABounces && IsQuality24Bounce(direction, tickSize))
                Plot24EntryMarker(direction, tickSize);
        }

        private int GetDirection()
        {
            if (m_FastEMA[0] > m_SlowEMA[0] && m_SlowEMA[0] > m_TrendEMA[0])
                return 1;
            if (m_FastEMA[0] < m_SlowEMA[0] && m_SlowEMA[0] < m_TrendEMA[0])
                return -1;
            return 0;
        }

        private bool IsQuality8Bounce(int direction, double tickSize)
        {
            double rangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            // The analyzed population excluded partial session/startup bars.
            if (Math.Abs(rangeTicks - 5.0) > 0.001) return false;

            double separation = Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) / tickSize;
            if (separation < Minimum824SeparationTicks) return false;
            double prior24Slope = GetBestPriorDirectionalSlope(
                m_SlowEMA, direction, tickSize);
            double current8Slope = direction * GetAngle(
                m_FastEMA[0], m_FastEMA[SlopeBars], SlopeBars, tickSize);
            if (current8Slope < MinimumCurrent8SlopeDegrees) return false;
            int pullbackBars = GetPullbackLength(direction, 2);
            if (pullbackBars == 0) return false;

            bool crossesFast = Bars.Low[0] <= m_FastEMA[0] &&
                               Bars.High[0] >= m_FastEMA[0];
            double penetration = direction > 0
                ? (m_FastEMA[0] - Bars.Low[0]) / tickSize
                : (Bars.High[0] - m_FastEMA[0]) / tickSize;
            bool closeOnTrendSide = direction > 0
                ? Bars.Close[0] >= m_FastEMA[0]
                : Bars.Close[0] <= m_FastEMA[0];
            bool trendColor = direction > 0
                ? Bars.Close[0] >= Bars.Open[0]
                : Bars.Close[0] <= Bars.Open[0];
            double reference = direction > 0
                ? Math.Min(Bars.Low[1], Bars.Low[2])
                : Math.Max(Bars.High[1], Bars.High[2]);
            double displacement = direction > 0
                ? (reference - Bars.Low[0]) / tickSize
                : (Bars.High[0] - reference) / tickSize;

            if (!crossesFast) return false;
            double maximumPenetration = pullbackBars == 1
                ? OneBarMaximumPenetrationTicks
                : TwoBarMaximumPenetrationTicks;
            double minimum24Slope = pullbackBars == 1
                ? OneBarMinimum24PriorSlopeDegrees
                : TwoBarMinimum24PriorSlopeDegrees;
            if (prior24Slope < minimum24Slope ||
                penetration < MinimumPenetrationTicks ||
                penetration > maximumPenetration ||
                !closeOnTrendSide || !trendColor ||
                displacement < MinimumLocalDisplacementTicks)
                return false;
            return true;
        }

        private bool IsQuality24Bounce(int direction, double tickSize)
        {
            double rangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            if (Math.Abs(rangeTicks - 5.0) > 0.001) return false;

            int pullbackBars = GetPullbackLength(direction, 3);
            if (pullbackBars == 0) return false;

            double prior24Slope = GetBestPriorDirectionalSlope(
                m_SlowEMA, direction, tickSize);
            double current24Slope = direction * GetAngle(
                m_SlowEMA[0], m_SlowEMA[SlopeBars], SlopeBars, tickSize);
            double gap824 = Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) / tickSize;
            double gap2450 = Math.Abs(m_SlowEMA[0] - m_TrendEMA[0]) / tickSize;
            double recovery = direction * (Bars.Close[0] - Bars.Close[1]) / tickSize;

            double priorSlopeMinimum;
            double currentSlopeMinimum;
            double gap824Minimum;
            double gap2450Minimum;
            double recoveryMinimum;
            if (pullbackBars == 1)
            {
                priorSlopeMinimum = 45.0;
                currentSlopeMinimum = 10.0;
                gap824Minimum = 3.0;
                gap2450Minimum = 3.0;
                recoveryMinimum = 0.0;
            }
            else if (pullbackBars == 2)
            {
                priorSlopeMinimum = 30.0;
                currentSlopeMinimum = 15.0;
                gap824Minimum = 1.5;
                gap2450Minimum = 3.0;
                recoveryMinimum = 2.0;
            }
            else
            {
                // A three-bar pullback may flatten the current 24 EMA, so
                // require a stronger recent slope instead of current slope.
                priorSlopeMinimum = 39.0;
                currentSlopeMinimum = 0.0;
                gap824Minimum = 1.5;
                gap2450Minimum = 1.5;
                recoveryMinimum = 1.0;
            }
            if (prior24Slope < priorSlopeMinimum ||
                current24Slope < currentSlopeMinimum ||
                gap824 < gap824Minimum || gap2450 < gap2450Minimum ||
                recovery < recoveryMinimum)
                return false;

            bool closeOnTrendSide = direction > 0
                ? Bars.Close[0] >= m_SlowEMA[0]
                : Bars.Close[0] <= m_SlowEMA[0];
            bool trendColor = direction > 0
                ? Bars.Close[0] >= Bars.Open[0]
                : Bars.Close[0] <= Bars.Open[0];
            if (!closeOnTrendSide || !trendColor) return false;

            // The touch may occur on the rejection bar or anywhere within
            // the immediately preceding countertrend pullback sequence.
            double deepestPenetration = Double.NegativeInfinity;
            for (int barsBack = 0; barsBack <= pullbackBars; barsBack++)
            {
                double penetration = direction > 0
                    ? (m_SlowEMA[barsBack] - Bars.Low[barsBack]) / tickSize
                    : (Bars.High[barsBack] - m_SlowEMA[barsBack]) / tickSize;
                deepestPenetration = Math.Max(deepestPenetration, penetration);
            }
            return deepestPenetration >= 0.0 &&
                   deepestPenetration <= Ema24MaximumPenetrationTicks;
        }

        private int GetPullbackLength(int direction, int maximumBars)
        {
            for (int length = 1; length <= maximumBars; length++)
            {
                bool allCountertrend = true;
                for (int barsBack = 1; barsBack <= length; barsBack++)
                {
                    bool countertrend = direction > 0
                        ? Bars.Close[barsBack] < Bars.Open[barsBack]
                        : Bars.Close[barsBack] > Bars.Open[barsBack];
                    if (!countertrend)
                    {
                        allCountertrend = false;
                        break;
                    }
                }
                if (!allCountertrend) continue;
                bool precedingWithTrend = direction > 0
                    ? Bars.Close[length + 1] >= Bars.Open[length + 1]
                    : Bars.Close[length + 1] <= Bars.Open[length + 1];
                if (precedingWithTrend) return length;
            }
            return 0;
        }

        private double GetBestPriorDirectionalSlope(XAverage ema, int direction,
                                                     double tickSize)
        {
            double best = Double.NegativeInfinity;
            for (int barsBack = 1; barsBack <= PriorSlopeLookbackBars; barsBack++)
            {
                double angle = GetAngle(ema[barsBack], ema[barsBack + SlopeBars],
                                        SlopeBars, tickSize);
                best = Math.Max(best, direction * angle);
            }
            return best;
        }

        private double GetAngle(double current, double previous, int barsBack,
                                double tickSize)
        {
            return Math.Atan2(current - previous, barsBack * tickSize) *
                   (180.0 / Math.PI);
        }

        private void Plot8EntryMarker(int direction, double tickSize)
        {
            double price = direction > 0 ? Bars.Low[0] - 2 * tickSize :
                                           Bars.High[0] + 2 * tickSize;
            m_SignalPlot.Set(price);
        }

        private void Plot24EntryMarker(int direction, double tickSize)
        {
            double price = direction > 0 ? Bars.Low[0] - 4 * tickSize :
                                           Bars.High[0] + 4 * tickSize;
            m_Ema24SignalPlot.Set(price);
        }

    }
}
