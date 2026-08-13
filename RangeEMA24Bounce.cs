using System;
using System.Collections.Generic;
using System.Drawing;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Historical/display-only 24 EMA bounce visualizer. It has no trading,
    // projections, or order-management code.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    public class RangeEMA24Bounce : IndicatorObject
    {
        private const int FastEmaLength = 8;
        private const int SlowEmaLength = 24;
        private const int ProfileEmaLength = 50;
        private const int SlopeBars = 3;
        private const int SlopeLookbackBars = 6;
        private const double MinimumSlopeDegrees = 20.0;
        // A completed range bar can reject just short of the EMA.  Treat a
        // near-touch (up to three-quarters of a tick) as a valid 24 bounce.
        private const double EmaTouchToleranceTicks = 0.75;
        private const double MinimumEmaSeparationTicks = 2.0;
        private const double MinimumCurrentEmaSeparationTicks = 1.5;
        // Treat each displayed arrow as a completed virtual entry.  A new
        // arrow cannot be printed until that virtual trade has reached one
        // of these exits on a later completed bar.
        private const int ReentryProfitTargetTicks = 5;
        private const int ReentryStopLossTicks = 10;
        private const int ProfilePersistenceBars = 4;
        private const double ProfileMinFastSlowSeparationTicks = 3.0;
        private const double ProfileMinSlowTrendSeparationTicks = 2.0;
        private const double ProfileMinFastTrendSeparationTicks = 5.0;
        private const double ProfileMinFastSlopeDegrees = 20.0;
        private const double ProfileMinSlowSlopeDegrees = 20.0;
        private const double ProfileMinTrendSlopeDegrees = 10.0;
        private const double ProfileMaximumCompressionTicks = 1.0;

        [Input] public bool ShowDisplay { get; set; }

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_ProfileEMA;
        private readonly List<IDrawObject> m_DisplayDrawings =
            new List<IDrawObject>();
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
            m_ProfileEMA = new XAverage(this);
        }

        protected override void StartCalc()
        {
            ClearDisplayDrawings();
            ResetVirtualTrade();
            m_FastEMA.Length = FastEmaLength;
            m_FastEMA.Price = Bars.Close;
            m_SlowEMA.Length = SlowEmaLength;
            m_SlowEMA.Price = Bars.Close;
            m_ProfileEMA.Length = ProfileEmaLength;
            m_ProfileEMA.Price = Bars.Close;
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

            BounceDiagnostic diagnostic = BuildDiagnostic(tickSize);
            if (!diagnostic.SeparationPass || !diagnostic.SlopePass ||
                !diagnostic.TouchPass || !diagnostic.ClosePass ||
                !diagnostic.BarColorPass ||
                !HasRequiredEmaFan(diagnostic.Direction, tickSize))
                return;

            DrawBounceArrow(diagnostic.Direction, tickSize);
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

        private bool HasRequiredEmaFan(int direction, double tickSize)
        {
            double rangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            double minimumGap = rangeTicks * 0.5;
            return direction > 0
                ? m_FastEMA[0] > m_SlowEMA[0] && m_SlowEMA[0] > m_ProfileEMA[0] &&
                  (m_FastEMA[0] - m_SlowEMA[0]) / tickSize >= minimumGap &&
                  (m_SlowEMA[0] - m_ProfileEMA[0]) / tickSize >= minimumGap
                : direction < 0 && m_FastEMA[0] < m_SlowEMA[0] && m_SlowEMA[0] < m_ProfileEMA[0] &&
                  (m_SlowEMA[0] - m_FastEMA[0]) / tickSize >= minimumGap &&
                  (m_ProfileEMA[0] - m_SlowEMA[0]) / tickSize >= minimumGap;
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

        private double GetBestPriorDirectionalSlowSlope(int direction, double tickSize)
        {
            if (direction == 0) return 0;
            double best = direction > 0 ? Double.NegativeInfinity : Double.PositiveInfinity;
            for (int barsBack = 1; barsBack <= SlopeLookbackBars; barsBack++)
            {
                double angle = GetAngle(m_SlowEMA[barsBack],
                                        m_SlowEMA[barsBack + SlopeBars],
                                        SlopeBars, tickSize);
                best = direction > 0 ? Math.Max(best, angle) : Math.Min(best, angle);
            }
            return best;
        }

        private bool HasTradeProfileDirection(int direction, double tickSize)
        {
            return direction != 0 && HasTradeProfileDirectionAt(direction, 0, tickSize);
        }

        private bool HasTradeProfileDirectionAt(int direction, int barsBack,
                                                 double tickSize)
        {
            double fast = m_FastEMA[barsBack];
            double slow = m_SlowEMA[barsBack];
            double trend = m_ProfileEMA[barsBack];
            bool ordered = direction > 0 ? fast > slow && slow > trend :
                           fast < slow && slow < trend;
            if (!ordered ||
                Math.Abs(fast - slow) / tickSize < ProfileMinFastSlowSeparationTicks ||
                Math.Abs(slow - trend) / tickSize < ProfileMinSlowTrendSeparationTicks ||
                Math.Abs(fast - trend) / tickSize < ProfileMinFastTrendSeparationTicks)
                return false;
            // A 24-EMA pullback can flatten its current slope. Retain the
            // trend if the 24 EMA reached the required slope anywhere in the
            // most recent six completed range bars.
            double slowSlope = GetBestPriorDirectionalSlowSlope(direction, tickSize);
            double trendSlope = GetAngle(m_ProfileEMA[barsBack],
                                         m_ProfileEMA[barsBack + SlopeBars],
                                         SlopeBars, tickSize);
            return direction > 0
                ? slowSlope >= ProfileMinSlowSlopeDegrees &&
                  trendSlope >= ProfileMinTrendSlopeDegrees
                : slowSlope <= -ProfileMinSlowSlopeDegrees &&
                  trendSlope <= -ProfileMinTrendSlopeDegrees;
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
            ResetVirtualTrade();
        }
    }
}
