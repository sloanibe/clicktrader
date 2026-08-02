using System;
using System.Collections.Generic;
using System.Drawing;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Historical/display-only companion to RangeBarTradingV3's PB1 mode.
    // It intentionally contains no mouse handling, arming, or order logic.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    public class RangePBBounce : IndicatorObject
    {
        private const int PinBarRangeTicks = 5;
        private const int FastEmaLength = 8;
        private const int SlowEmaLength = 24;
        // Keep PB1 markers out of flat, overlapping-EMA congestion.
        private const int MinimumEmaSeparationTicks = 3;
        private const int EmaSlopeBars = 3;
        private const double MinimumFastEmaSlopeDegrees = 20.0;
        // Each displayed PB1 is treated as a virtual entry.  Another signal
        // is not shown until this virtual trade exits on a later bar.
        private const int ReentryProfitTargetTicks = 5;
        private const int ReentryStopLossTicks = 10;

        // Retained only so existing chart-study settings continue to load.
        // PB1 display is now direction-neutral, so this value is ignored.
        [Input] public bool IsAskChart { get; set; }
        [Input] public bool ShowDisplay { get; set; }

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private readonly List<IDrawObject> m_DisplayDrawings =
            new List<IDrawObject>();
        private bool m_VirtualTradeActive;
        private int m_VirtualTradeDirection;
        private double m_VirtualTradeEntryPrice;

        public RangePBBounce(object ctx) : base(ctx)
        {
            IsAskChart = true;
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

            // Historical bars arrive as closed bars, and this prevents a
            // forming live range bar from being marked repeatedly or early.
            if (Bars.Status != EBarState.Close ||
                Bars.CurrentBar < EmaSlopeBars) return;

            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;

            // An exit bar is consumed by the virtual trade; it cannot also
            // become a new PB1 entry marker.
            if (m_VirtualTradeActive)
            {
                UpdateVirtualTrade(tickSize);
                return;
            }

            int direction = GetSlowEmaDirection();
            if (!IsPB1EmaOrderValid(direction) ||
                !IsPB1TrendFilterValid(direction, tickSize) ||
                !IsCloseBeyondPreviousBarRange(direction) ||
                !IsOpenOnCorrectEmaSide(direction, tickSize))
                return;

            int bodyTicks;
            if (!TryGetCompletedPB1BodyTicks(direction, tickSize, out bodyTicks))
                return;

            DrawPB1Label(direction, bodyTicks, tickSize);
            StartVirtualTrade(direction, Bars.Close[0]);
        }

        private int GetSlowEmaDirection()
        {
            return m_SlowEMA[0] >= m_SlowEMA[1] ? 1 : -1;
        }

        private bool IsPB1EmaOrderValid(int direction)
        {
            return direction > 0
                ? m_FastEMA[0] > m_SlowEMA[0]
                : m_FastEMA[0] < m_SlowEMA[0];
        }

        private bool IsPB1TrendFilterValid(int direction, double tickSize)
        {
            double separation = direction > 0
                ? m_FastEMA[0] - m_SlowEMA[0]
                : m_SlowEMA[0] - m_FastEMA[0];
            if (separation < MinimumEmaSeparationTicks * tickSize)
                return false;

            double fastEmaSlope = GetAngle(m_FastEMA[0],
                                            m_FastEMA[EmaSlopeBars],
                                            EmaSlopeBars, tickSize);
            return direction > 0
                ? fastEmaSlope >= MinimumFastEmaSlopeDegrees
                : fastEmaSlope <= -MinimumFastEmaSlopeDegrees;
        }

        private bool IsCloseBeyondPreviousBarRange(int direction)
        {
            return direction > 0
                ? Bars.Close[0] > Bars.High[1]
                : Bars.Close[0] < Bars.Low[1];
        }

        private bool IsOpenOnCorrectEmaSide(int direction, double tickSize)
        {
            double open = RoundToTick(Bars.Open[0], tickSize);
            return direction > 0 ? open > m_SlowEMA[0] : open < m_SlowEMA[0];
        }

        private bool TryGetCompletedPB1BodyTicks(int direction, double tickSize,
                                                 out int bodyTicks)
        {
            bodyTicks = 0;
            double tolerance = tickSize * 0.1;
            double open = RoundToTick(Bars.Open[0], tickSize);
            double high = RoundToTick(Bars.High[0], tickSize);
            double low = RoundToTick(Bars.Low[0], tickSize);
            double close = RoundToTick(Bars.Close[0], tickSize);

            int tailTicks;
            if (direction > 0)
            {
                // A long PB1 touches its lower tail and finishes at the high.
                tailTicks = ToTicks(open - low, tickSize);
                bodyTicks = ToTicks(high - open, tickSize);
                if (Math.Abs(close - high) > tolerance) return false;
            }
            else
            {
                // A short PB1 touches its upper tail and finishes at the low.
                tailTicks = ToTicks(high - open, tickSize);
                bodyTicks = ToTicks(open - low, tickSize);
                if (Math.Abs(close - low) > tolerance) return false;
            }

            // Keep the historical display aligned with the strategy's PB1
            // family: 2/3, 1/4, and 0/5 (body/tail).
            return (bodyTicks == 0 || bodyTicks == 1 || bodyTicks == 2) &&
                   tailTicks + bodyTicks == PinBarRangeTicks;
        }

        private void DrawPB1Label(int direction, int bodyTicks, double tickSize)
        {
            // Blue keeps the PB bounce family readable in either direction.
            // DrwArrow uses false for an up arrow and true for a down arrow.
            double arrowPrice = direction > 0
                ? Bars.Low[0] - (2 * tickSize)
                : Bars.High[0] + (2 * tickSize);
            IArrowObject arrow = DrwArrow.Create(
                new ChartPoint(Bars.Time[0], arrowPrice), direction < 0);
            if (arrow != null)
            {
                arrow.Color = Color.DodgerBlue;
                arrow.Size = 4;
                m_DisplayDrawings.Add(arrow);
            }

            // Preserve the same horizontal anchor as the arrow, but put the
            // text beyond it vertically so it cannot cover the arrowhead.
            double labelPrice = direction > 0
                ? Bars.Low[0] - (4 * tickSize)
                : Bars.High[0] + (4 * tickSize);
            ITextObject label = DrwText.Create(
                new ChartPoint(Bars.Time[0], labelPrice),
                bodyTicks == 2 ? "PB2" : bodyTicks == 1 ? "PB1" : "PB0");
            if (label == null) return;

            label.Color = Color.DodgerBlue;
            label.Size = 9;
            label.HStyle = ETextStyleH.Center;
            label.VStyle = direction > 0 ? ETextStyleV.Below : ETextStyleV.Above;
            m_DisplayDrawings.Add(label);
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

            // OHLC cannot reveal which threshold was touched first.  Treat a
            // bar that reaches both as stopped out, rather than assuming it
            // made the profit target first.
            if (stoppedOut || targetReached)
                ResetVirtualTrade();
        }

        private void ResetVirtualTrade()
        {
            m_VirtualTradeActive = false;
            m_VirtualTradeDirection = 0;
            m_VirtualTradeEntryPrice = 0;
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

        private int ToTicks(double priceDistance, double tickSize)
        {
            return (int)Math.Round(priceDistance / tickSize);
        }

        private double RoundToTick(double price, double tickSize)
        {
            return Math.Round(price / tickSize) * tickSize;
        }

        private double GetAngle(double valueCurrent, double valueOld,
                                int barsBack, double tickSize)
        {
            return Math.Atan2(valueCurrent - valueOld, barsBack * tickSize) *
                   (180.0 / Math.PI);
        }
    }
}
