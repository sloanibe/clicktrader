using System;
using System.Drawing;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Historical/display-only companion to RangeBarTradingV3's PB1 mode.
    // It intentionally contains no mouse handling, arming, or order logic.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    public class RangePB1Display : IndicatorObject
    {
        private const int PinBarRangeTicks = 5;
        private const int FastEmaLength = 8;
        private const int SlowEmaLength = 24;

        // Match the strategy's one-sided workspaces: true for the ask/buy
        // chart and false for the bid/sell chart.
        [Input] public bool IsAskChart { get; set; }

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;

        public RangePB1Display(object ctx) : base(ctx)
        {
            IsAskChart = true;
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this);
            m_SlowEMA = new XAverage(this);
        }

        protected override void StartCalc()
        {
            m_FastEMA.Length = FastEmaLength;
            m_FastEMA.Price = Bars.Close;
            m_SlowEMA.Length = SlowEmaLength;
            m_SlowEMA.Price = Bars.Close;
        }

        protected override void CalcBar()
        {
            // Historical bars arrive as closed bars, and this prevents a
            // forming live range bar from being marked repeatedly or early.
            if (Bars.Status != EBarState.Close || Bars.CurrentBar < 2) return;

            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;

            int direction = GetSlowEmaDirection();
            if (!IsChartDirectionAllowed(direction) ||
                !IsPB1EmaOrderValid(direction) ||
                !IsOpenOnCorrectEmaSide(direction, tickSize))
                return;

            int bodyTicks;
            if (!TryGetCompletedPB1BodyTicks(direction, tickSize, out bodyTicks))
                return;

            DrawPB1Label(direction, bodyTicks, tickSize);
        }

        private int GetSlowEmaDirection()
        {
            return m_SlowEMA[0] >= m_SlowEMA[1] ? 1 : -1;
        }

        private bool IsChartDirectionAllowed(int direction)
        {
            return direction > 0 ? IsAskChart : !IsAskChart;
        }

        // PB1 mode uses only EMA order, matching the simplified PB1 trend
        // filter in RangeBarTradingV3.
        private bool IsPB1EmaOrderValid(int direction)
        {
            return direction > 0
                ? m_FastEMA[0] > m_SlowEMA[0]
                : m_FastEMA[0] < m_SlowEMA[0];
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

            return (bodyTicks == 0 || bodyTicks == 1) &&
                   tailTicks + bodyTicks == PinBarRangeTicks;
        }

        private void DrawPB1Label(int direction, int bodyTicks, double tickSize)
        {
            // Light gray distinguishes these historical/display markers from
            // the strategy's actual entry annotations. DrwArrow uses false
            // for an up arrow and true for a down arrow.
            double arrowPrice = direction > 0
                ? Bars.Low[0] - (3 * tickSize)
                : Bars.High[0] + (3 * tickSize);
            IArrowObject arrow = DrwArrow.Create(
                new ChartPoint(Bars.Time[0], arrowPrice), direction < 0);
            if (arrow != null)
            {
                arrow.Color = Color.LightGray;
                arrow.Size = 3;
            }

            double price = direction > 0
                ? Bars.Low[0] - (2 * tickSize)
                : Bars.High[0] + (2 * tickSize);
            ITextObject label = DrwText.Create(
                new ChartPoint(Bars.Time[0], price),
                bodyTicks == 1 ? "PB1" : "PB0");
            if (label == null) return;

            label.Color = direction > 0 ? Color.DodgerBlue : Color.OrangeRed;
            label.Size = 9;
            label.HStyle = ETextStyleH.Right;
            label.VStyle = direction > 0 ? ETextStyleV.Below : ETextStyleV.Above;
        }

        private int ToTicks(double priceDistance, double tickSize)
        {
            return (int)Math.Round(priceDistance / tickSize);
        }

        private double RoundToTick(double price, double tickSize)
        {
            return Math.Round(price / tickSize) * tickSize;
        }
    }
}
