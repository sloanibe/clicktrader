using System;
using System.Collections.Generic;
using System.Drawing;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Visual-only study for selected 8-, 24-, and 50-EMA pullbacks. Orange
    // points mark 8-EMA entries, black points mark 24-EMA entries, red
    // points mark 50-EMA entries, and cyan points mark EMA-fan momentum pins.
    // Cyan drawing arrows separately identify experimental seven-bar-clear
    // momentum pins 0-2 ticks from the 8 EMA; magenta arrows identify the
    // expanded 2-4-tick opportunity tier. Gold arrows identify full-body,
    // no-tail momentum bars whose close clears the preceding seven bars.
    // Signal-bar shape is used as evidence:
    // directional tails identify rejection, while a no-tail trend bar can
    // identify decisive continuation after a pullback.
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
        private const int Ema50PriorSlopeLookbackBars = 8;
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
        private IPlotObject m_Ema50SignalPlot;
        private IPlotObject m_MomentumPinSignalPlot;
        private readonly List<IDrawObject> m_DisplayDrawings =
            new List<IDrawObject>();

        [Input] public bool ShowDisplay { get; set; }
        [Input] public bool Show8EMABounces { get; set; }
        [Input] public bool Show24EMABounces { get; set; }
        [Input] public bool Show50EMABounces { get; set; }
        [Input] public bool ShowMomentumPins { get; set; }
        [Input] public bool UsePrimeTradingHours { get; set; }
        [Input] public bool ShowSevenBarClearPins { get; set; }
        [Input] public bool ShowExtendedSevenBarClearPins { get; set; }
        [Input] public bool ShowSevenBarClearNoTailBars { get; set; }

        public RangeEMA8QualityBounce(object ctx) : base(ctx)
        {
            ShowDisplay = true;
            Show8EMABounces = true;
            Show24EMABounces = true;
            Show50EMABounces = true;
            ShowMomentumPins = true;
            UsePrimeTradingHours = true;
            ShowSevenBarClearPins = true;
            ShowExtendedSevenBarClearPins = true;
            ShowSevenBarClearNoTailBars = true;
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
            m_Ema50SignalPlot = AddPlot(new PlotAttributes("Quality 50 EMA Bounce",
                EPlotShapes.Point, Color.Red, Color.Empty, 12, 0, true));
            m_MomentumPinSignalPlot = AddPlot(new PlotAttributes(
                "EMA Fan Momentum Pin", EPlotShapes.Point, Color.Cyan,
                Color.Empty, 12, 0, true));
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
            // Retain inert plots for compatibility with existing chart-study
            // instances, but draw direction arrows instead of point markers.
            m_SignalPlot.Set(Double.NaN);
            m_Ema24SignalPlot.Set(Double.NaN);
            m_Ema50SignalPlot.Set(Double.NaN);
            m_MomentumPinSignalPlot.Set(Double.NaN);
            if (!ShowDisplay)
            {
                ClearDisplayDrawings();
                return;
            }
            if (Bars.Status != EBarState.Close ||
                Bars.CurrentBar < Ema50PriorSlopeLookbackBars + SlopeBars)
                return;

            // Diagnostic times are Pacific/chart time. Deliberately use the
            // chart timestamp directly rather than applying a timezone shift.
            if (UsePrimeTradingHours && !IsPrimeTradingTime())
                return;

            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;
            int markerDirection = 0;
            bool sevenBarClearPin = false;
            bool extendedSevenBarClearPin = false;
            bool sevenBarClearNoTailBar = false;
            int orderedDirection = GetDirection();
            if (orderedDirection != 0)
            {
                if (Show8EMABounces &&
                    IsQuality8Bounce(orderedDirection, tickSize))
                    markerDirection = orderedDirection;
                if (Show24EMABounces &&
                    IsQuality24Bounce(orderedDirection, tickSize))
                    markerDirection = orderedDirection;
                if (ShowMomentumPins &&
                    IsQualityMomentumPin(orderedDirection, tickSize))
                    markerDirection = orderedDirection;
                if (ShowSevenBarClearPins &&
                    IsSevenBarClearMomentumPin(
                        orderedDirection, tickSize, 0.0, 2.0))
                {
                    markerDirection = orderedDirection;
                    sevenBarClearPin = true;
                }
                if (ShowExtendedSevenBarClearPins &&
                    IsSevenBarClearMomentumPin(
                        orderedDirection, tickSize, 2.0, 4.0))
                {
                    markerDirection = orderedDirection;
                    extendedSevenBarClearPin = true;
                }
                if (ShowSevenBarClearNoTailBars &&
                    IsSevenBarClearNoTailMomentumBar(
                        orderedDirection, tickSize))
                {
                    markerDirection = orderedDirection;
                    sevenBarClearNoTailBar = true;
                }
            }

            int ema50Direction = GetEma50Direction();
            if (Show50EMABounces && ema50Direction != 0 &&
                IsQuality50Bounce(ema50Direction, tickSize))
                markerDirection = markerDirection != 0
                    ? markerDirection : ema50Direction;

            if (markerDirection != 0)
                DrawSignalArrow(markerDirection, tickSize, sevenBarClearPin,
                                extendedSevenBarClearPin,
                                sevenBarClearNoTailBar);
        }

        private bool IsPrimeTradingTime()
        {
            DateTime barTime = Bars.Time[0];
            int hhmmss = barTime.Hour * 10000 +
                         barTime.Minute * 100 +
                         barTime.Second;

            // Start boundaries are inclusive; end boundaries are exclusive.
            return (hhmmss >= 63100 && hhmmss < 64500) ||
                   (hhmmss >= 70000 && hhmmss < 80000) ||
                   (hhmmss >= 110000 && hhmmss < 130000);
        }

        private int GetDirection()
        {
            if (m_FastEMA[0] > m_SlowEMA[0] && m_SlowEMA[0] > m_TrendEMA[0])
                return 1;
            if (m_FastEMA[0] < m_SlowEMA[0] && m_SlowEMA[0] < m_TrendEMA[0])
                return -1;
            return 0;
        }

        private int GetEma50Direction()
        {
            // A deep correction can temporarily disturb the 8/24 order.
            // The independently analyzed 50-EMA family therefore derives
            // current direction from the still-slower 24/50 relationship.
            if (m_SlowEMA[0] > m_TrendEMA[0]) return 1;
            if (m_SlowEMA[0] < m_TrendEMA[0]) return -1;
            return 0;
        }

        private bool IsQuality8Bounce(int direction, double tickSize)
        {
            double rangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            // The analyzed population excluded partial session/startup bars.
            if (Math.Abs(rangeTicks - 5.0) > 0.001) return false;

            double separation = Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) / tickSize;
            double prior24Slope = GetBestPriorDirectionalSlope(
                m_SlowEMA, direction, tickSize);
            double current8Slope = direction * GetAngle(
                m_FastEMA[0], m_FastEMA[SlopeBars], SlopeBars, tickSize);
            int pullbackBars = GetPullbackLength(direction, 3);
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
            double directionalTail = direction > 0
                ? (Bars.Open[0] - Bars.Low[0]) / tickSize
                : (Bars.High[0] - Bars.Open[0]) / tickSize;

            if (!crossesFast || !closeOnTrendSide || !trendColor) return false;

            // Original shape-neutral population.
            bool baseQuality = false;
            if (pullbackBars == 1 || pullbackBars == 2)
            {
                double maximumPenetration = pullbackBars == 1
                    ? OneBarMaximumPenetrationTicks
                    : TwoBarMaximumPenetrationTicks;
                double minimum24Slope = pullbackBars == 1
                    ? OneBarMinimum24PriorSlopeDegrees
                    : TwoBarMinimum24PriorSlopeDegrees;
                baseQuality = separation >= Minimum824SeparationTicks &&
                    current8Slope >= MinimumCurrent8SlopeDegrees &&
                    prior24Slope >= minimum24Slope &&
                    penetration >= MinimumPenetrationTicks &&
                    penetration <= maximumPenetration &&
                    displacement >= MinimumLocalDisplacementTicks;
            }

            bool noTail = directionalTail <= 0.1;
            bool oneTickTail = Math.Abs(directionalTail - 1.0) <= 0.1;
            bool longTail = directionalTail >= 4.0;

            // A no-tail trend bar is continuation evidence, so it does not
            // need to make a new pullback extreme. A one-tick or long-tail
            // bar instead earns admission as a controlled rejection.
            bool noTailOneBarMomentum = pullbackBars == 1 && noTail &&
                prior24Slope >= 45.0 && current8Slope >= 0.0 &&
                separation >= 3.0 && penetration >= 0.0 &&
                penetration <= 4.5;
            bool oneTickRejection = pullbackBars == 1 && oneTickTail &&
                prior24Slope >= 20.0 && current8Slope >= 15.0 &&
                separation >= 5.0 && penetration >= 1.0 &&
                penetration <= 2.5;
            bool longTailRejection = pullbackBars == 1 && longTail &&
                prior24Slope >= 20.0 && current8Slope >= 15.0 &&
                separation >= 3.0 && penetration >= 0.0 &&
                penetration <= 2.5 &&
                displacement >= MinimumLocalDisplacementTicks;
            bool noTailThreeBarMomentum = pullbackBars == 3 && noTail &&
                prior24Slope >= 20.0 && current8Slope >= 0.0 &&
                separation >= 3.0 && penetration >= 0.0 &&
                penetration <= 4.5 &&
                displacement >= MinimumLocalDisplacementTicks;

            return baseQuality || noTailOneBarMomentum || oneTickRejection ||
                   longTailRejection || noTailThreeBarMomentum;
        }

        private bool IsQuality24Bounce(int direction, double tickSize)
        {
            double rangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            if (Math.Abs(rangeTicks - 5.0) > 0.001) return false;

            int pullbackBars = GetPullbackLength(direction, 4);
            if (pullbackBars == 0) return false;

            double prior24Slope = GetBestPriorDirectionalSlope(
                m_SlowEMA, direction, tickSize);
            double current24Slope = direction * GetAngle(
                m_SlowEMA[0], m_SlowEMA[SlopeBars], SlopeBars, tickSize);
            double gap824 = Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) / tickSize;
            double gap2450 = Math.Abs(m_SlowEMA[0] - m_TrendEMA[0]) / tickSize;
            double recovery = direction * (Bars.Close[0] - Bars.Close[1]) / tickSize;

            bool baseQuality = false;
            if (pullbackBars == 1)
            {
                baseQuality = prior24Slope >= 45.0 && current24Slope >= 10.0 &&
                    gap824 >= 3.0 && gap2450 >= 3.0 && recovery >= 0.0;
            }
            else if (pullbackBars == 2)
            {
                baseQuality = prior24Slope >= 30.0 && current24Slope >= 15.0 &&
                    gap824 >= 1.5 && gap2450 >= 3.0 && recovery >= 2.0;
            }
            else if (pullbackBars == 3)
            {
                // A three-bar pullback may flatten the current 24 EMA, so
                // require a stronger recent slope instead of current slope.
                baseQuality = prior24Slope >= 39.0 && current24Slope >= 0.0 &&
                    gap824 >= 1.5 && gap2450 >= 1.5 && recovery >= 1.0;
            }

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
            if (deepestPenetration < 0.0 ||
                deepestPenetration > Ema24MaximumPenetrationTicks)
                return false;

            double directionalTail = direction > 0
                ? (Bars.Open[0] - Bars.Low[0]) / tickSize
                : (Bars.High[0] - Bars.Open[0]) / tickSize;
            bool noTail = directionalTail <= 0.1;
            bool oneTickTail = Math.Abs(directionalTail - 1.0) <= 0.1;

            // A one-tick rejection supports a slightly less separated 24/50
            // fan after two pullback bars. A no-tail trend bar supplies the
            // continuation evidence needed after a four-bar correction.
            bool oneTickTwoBarRejection = pullbackBars == 2 && oneTickTail &&
                prior24Slope >= 30.0 && current24Slope >= 10.0 &&
                gap824 >= 3.0 && gap2450 >= 1.5 && recovery >= 2.0;
            // Close-to-close recovery can understate a rejection when the
            // pullback only probes fractionally through the 24 EMA. Preserve
            // the normal two-bar structure but allow that controlled shallow
            // test without the two-tick recovery requirement.
            bool shallowTwoBarProbe = pullbackBars == 2 &&
                prior24Slope >= 30.0 && current24Slope >= 15.0 &&
                gap824 >= 1.5 && gap2450 >= 3.0 && recovery < 2.0 &&
                deepestPenetration <= 1.0;
            bool noTailFourBarMomentum = pullbackBars == 4 && noTail &&
                prior24Slope >= 39.0 && current24Slope >= 0.0 &&
                gap824 >= 1.5 && gap2450 >= 1.5 && recovery >= 2.0;

            return baseQuality || oneTickTwoBarRejection || shallowTwoBarProbe ||
                   noTailFourBarMomentum;
        }

        private bool IsQuality50Bounce(int direction, double tickSize)
        {
            double rangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            if (Math.Abs(rangeTicks - 5.0) > 0.001) return false;

            bool trendColor = direction > 0
                ? Bars.Close[0] >= Bars.Open[0]
                : Bars.Close[0] <= Bars.Open[0];
            bool closeOnTrendSide = direction > 0
                ? Bars.Close[0] >= m_TrendEMA[0]
                : Bars.Close[0] <= m_TrendEMA[0];
            if (!trendColor || !closeOnTrendSide) return false;

            int pullbackBars = GetPullbackLength(direction, 3);
            if (pullbackBars == 0) return false;

            // The full EMA fan must have been intact immediately before the
            // countertrend sequence began. It need not remain intact on the
            // signal bar of this deeper pullback.
            int prePullbackBar = pullbackBars + 1;
            bool prePullbackOrder =
                direction * (m_FastEMA[prePullbackBar] -
                             m_SlowEMA[prePullbackBar]) > 0.0 &&
                direction * (m_SlowEMA[prePullbackBar] -
                             m_TrendEMA[prePullbackBar]) > 0.0;
            if (!prePullbackOrder) return false;

            double prior50Slope = GetBestPriorDirectionalSlope(
                m_TrendEMA, direction, tickSize, Ema50PriorSlopeLookbackBars);
            double prior24Slope = GetBestPriorDirectionalSlope(
                m_SlowEMA, direction, tickSize, Ema50PriorSlopeLookbackBars);
            double current50Slope = direction * GetAngle(
                m_TrendEMA[0], m_TrendEMA[SlopeBars], SlopeBars, tickSize);
            double gap2450 = direction *
                (m_SlowEMA[0] - m_TrendEMA[0]) / tickSize;
            if (gap2450 < 0.001) return false;
            double recovery = direction *
                (Bars.Close[0] - Bars.Close[1]) / tickSize;

            double deepestPenetration = Double.NegativeInfinity;
            for (int barsBack = 0; barsBack <= pullbackBars; barsBack++)
            {
                double penetration = direction > 0
                    ? (m_TrendEMA[barsBack] - Bars.Low[barsBack]) / tickSize
                    : (Bars.High[barsBack] - m_TrendEMA[barsBack]) / tickSize;
                deepestPenetration = Math.Max(deepestPenetration, penetration);
            }

            double directionalTail = direction > 0
                ? (Bars.Open[0] - Bars.Low[0]) / tickSize
                : (Bars.High[0] - Bars.Open[0]) / tickSize;
            bool noTail = directionalTail <= 0.1;
            bool twoOrThreeTickTail = directionalTail >= 1.9 &&
                                      directionalTail <= 3.1;

            bool oneBarNoTailMomentum = pullbackBars == 1 && noTail &&
                prior50Slope >= 10.0 && current50Slope >= -10.0 &&
                gap2450 >= 1.5 && deepestPenetration >= -1.0 &&
                deepestPenetration <= 5.0 && recovery >= 0.0;
            bool oneBarRejection = pullbackBars == 1 && twoOrThreeTickTail &&
                prior50Slope >= 20.0 && current50Slope >= 10.0 &&
                gap2450 >= 0.0 && deepestPenetration >= -1.0 &&
                deepestPenetration <= 5.0 && recovery >= 0.0;
            bool twoBarRejection = pullbackBars == 2 && twoOrThreeTickTail &&
                prior50Slope >= 20.0 && current50Slope >= 10.0 &&
                gap2450 >= 0.0 && deepestPenetration >= -1.0 &&
                deepestPenetration <= 5.0 && recovery >= 2.0;
            bool threeBarCore = pullbackBars == 3 &&
                prior50Slope >= 20.0 && prior24Slope >= 30.0 &&
                current50Slope >= 0.0 && gap2450 >= 1.5 &&
                deepestPenetration >= 0.0 && deepestPenetration <= 5.0 &&
                recovery >= 2.0;
            bool oneBarStrictStructure = pullbackBars == 1 &&
                prior50Slope >= 10.0 && current50Slope >= 10.0 &&
                gap2450 >= 3.0 && deepestPenetration >= 0.0 &&
                deepestPenetration <= 5.0 && recovery >= 0.0;

            return oneBarNoTailMomentum || oneBarRejection ||
                   twoBarRejection || threeBarCore ||
                   oneBarStrictStructure;
        }

        private bool IsQualityMomentumPin(int direction, double tickSize)
        {
            double rangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            if (Math.Abs(rangeTicks - 5.0) > 0.001) return false;

            bool trendColor = direction > 0
                ? Bars.Close[0] >= Bars.Open[0]
                : Bars.Close[0] <= Bars.Open[0];
            if (!trendColor) return false;

            double directionalTail = direction > 0
                ? (Bars.Open[0] - Bars.Low[0]) / tickSize
                : (Bars.High[0] - Bars.Open[0]) / tickSize;
            if (directionalTail < 2.9 || directionalTail > 5.1) return false;

            double bodyNearPrice = direction > 0
                ? Math.Min(Bars.Open[0], Bars.Close[0])
                : Math.Max(Bars.Open[0], Bars.Close[0]);
            double bodyDistanceFrom8 = direction *
                (bodyNearPrice - m_FastEMA[0]) / tickSize;
            if (bodyDistanceFrom8 <= 0.0) return false;

            double barExtreme = direction > 0 ? Bars.Low[0] : Bars.High[0];
            double barDistanceFrom8 = direction *
                (barExtreme - m_FastEMA[0]) / tickSize;
            double slope8 = direction * GetAngle(
                m_FastEMA[0], m_FastEMA[SlopeBars], SlopeBars, tickSize);
            double slope24 = direction * GetAngle(
                m_SlowEMA[0], m_SlowEMA[SlopeBars], SlopeBars, tickSize);
            double slope50 = direction * GetAngle(
                m_TrendEMA[0], m_TrendEMA[SlopeBars], SlopeBars, tickSize);
            double gap824 = Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) / tickSize;
            double gap2450 = Math.Abs(m_SlowEMA[0] - m_TrendEMA[0]) / tickSize;
            double closeExtension = direction *
                (Bars.Close[0] - Bars.Close[1]) / tickSize;

            // This is deliberately a continuation play: a fully ordered,
            // sharply sloped EMA fan and a pin bar extended away from the
            // 8 EMA, not a touch or rejection of an average.
            return slope8 >= 60.0 && slope24 >= 45.0 && slope50 >= 39.0 &&
                   gap824 >= 1.5 && gap2450 >= 3.0 &&
                   barDistanceFrom8 >= 4.0 && closeExtension >= 2.0;
        }

        private bool IsSevenBarClearMomentumPin(int direction, double tickSize,
                                                double minimumDistanceTicks,
                                                double maximumDistanceTicks)
        {
            double rangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            if (Math.Abs(rangeTicks - 5.0) > 0.001) return false;

            bool trendColor = direction > 0
                ? Bars.Close[0] >= Bars.Open[0]
                : Bars.Close[0] <= Bars.Open[0];
            if (!trendColor) return false;

            double directionalTail = direction > 0
                ? (Bars.Open[0] - Bars.Low[0]) / tickSize
                : (Bars.High[0] - Bars.Open[0]) / tickSize;
            if (directionalTail < 2.9 || directionalTail > 5.1) return false;

            double bodyNearPrice = direction > 0
                ? Math.Min(Bars.Open[0], Bars.Close[0])
                : Math.Max(Bars.Open[0], Bars.Close[0]);
            double bodyDistanceFrom8 = direction *
                (bodyNearPrice - m_FastEMA[0]) / tickSize;
            if (bodyDistanceFrom8 <= 0.0) return false;

            double barExtreme = direction > 0 ? Bars.Low[0] : Bars.High[0];
            double barDistanceFrom8 = direction *
                (barExtreme - m_FastEMA[0]) / tickSize;
            if (barDistanceFrom8 < minimumDistanceTicks ||
                barDistanceFrom8 >= maximumDistanceTicks)
                return false;

            // For a long, no prior high may reach the lower edge of the pin
            // body. For a short, no prior low may reach its upper edge.
            for (int barsBack = 1; barsBack <= 7; barsBack++)
            {
                bool bodyLevelBlocked = direction > 0
                    ? Bars.High[barsBack] >= bodyNearPrice
                    : Bars.Low[barsBack] <= bodyNearPrice;
                if (bodyLevelBlocked) return false;
            }
            return true;
        }

        private bool IsSevenBarClearNoTailMomentumBar(int direction,
                                                       double tickSize)
        {
            double rangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            if (Math.Abs(rangeTicks - 5.0) > 0.001) return false;

            bool fullTrendBody = direction > 0
                ? Bars.Close[0] >= Bars.Open[0] &&
                  (Bars.Open[0] - Bars.Low[0]) / tickSize <= 0.1 &&
                  (Bars.High[0] - Bars.Close[0]) / tickSize <= 0.1
                : Bars.Close[0] <= Bars.Open[0] &&
                  (Bars.High[0] - Bars.Open[0]) / tickSize <= 0.1 &&
                  (Bars.Close[0] - Bars.Low[0]) / tickSize <= 0.1;
            double bodyTicks = Math.Abs(Bars.Close[0] - Bars.Open[0]) / tickSize;
            if (!fullTrendBody || bodyTicks < 4.9) return false;

            double bodyNearPrice = Bars.Open[0];
            if (direction * (bodyNearPrice - m_FastEMA[0]) / tickSize <= 0.0)
                return false;

            double slope8 = direction * GetAngle(
                m_FastEMA[0], m_FastEMA[SlopeBars], SlopeBars, tickSize);
            double slope24 = direction * GetAngle(
                m_SlowEMA[0], m_SlowEMA[SlopeBars], SlopeBars, tickSize);
            double slope50 = direction * GetAngle(
                m_TrendEMA[0], m_TrendEMA[SlopeBars], SlopeBars, tickSize);
            double gap824 = Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) / tickSize;
            double gap2450 = Math.Abs(m_SlowEMA[0] - m_TrendEMA[0]) / tickSize;
            if (slope8 < 60.0 || slope24 < 45.0 || slope50 < 39.0 ||
                gap824 < 1.5 || gap2450 < 3.0)
                return false;

            double closeLevel = Bars.Close[0];
            for (int barsBack = 1; barsBack <= 7; barsBack++)
            {
                bool closeLevelBlocked = direction > 0
                    ? Bars.High[barsBack] >= closeLevel
                    : Bars.Low[barsBack] <= closeLevel;
                if (closeLevelBlocked) return false;
            }
            return true;
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
            return GetBestPriorDirectionalSlope(
                ema, direction, tickSize, PriorSlopeLookbackBars);
        }

        private double GetBestPriorDirectionalSlope(XAverage ema, int direction,
                                                     double tickSize,
                                                     int lookbackBars)
        {
            double best = Double.NegativeInfinity;
            for (int barsBack = 1; barsBack <= lookbackBars; barsBack++)
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

        private void DrawSignalArrow(int direction, double tickSize,
                                     bool sevenBarClearPin,
                                     bool extendedSevenBarClearPin,
                                     bool sevenBarClearNoTailBar)
        {
            double price = direction > 0 ? Bars.Low[0] - 2 * tickSize :
                                           Bars.High[0] + 2 * tickSize;
            IArrowObject arrow = DrwArrow.Create(
                new ChartPoint(Bars.Time[0], price), direction < 0);
            if (arrow == null) return;
            arrow.Color = sevenBarClearNoTailBar
                ? Color.Gold
                : (extendedSevenBarClearPin
                    ? Color.Magenta
                    : (sevenBarClearPin
                        ? Color.Cyan
                        : (direction > 0 ? Color.Lime : Color.Red)));
            arrow.Size = 4;
            m_DisplayDrawings.Add(arrow);
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
