using System;
using System.Collections.Generic;
using System.Drawing;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // The selected level is the largest permitted PB body size. Smaller
    // bodies remain visible, so PB3 includes PB3/PB2/PB1/PB0.
    public enum EPBDisplayLevel
    {
        PB0 = 0,
        PB1 = 1,
        PB2 = 2,
        PB3 = 3
    }

    // Historical/display-only companion to RangeBarTradingV3's PB1 mode.
    // It intentionally contains no mouse handling, arming, or order logic.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    public class RangePBBounce : IndicatorObject
    {
        private const int PinBarRangeTicks = 5;
        private const int FastEmaLength = 8;
        private const int SlowEmaLength = 24;
        private const int ProfileEmaLength = 50;
        // Keep PB1 markers out of flat, overlapping-EMA congestion.
        private const int MinimumEmaSeparationTicks = 3;
        private const int EmaSlopeBars = 3;
        private const double MinimumFastEmaSlopeDegrees = 20.0;
        private const double StrongContinuationFastSlopeDegrees = 40.0;
        private const double StrongContinuationSlowSlopeDegrees = 15.0;
        private const double StrongContinuationSeparationTicks = 3.0;
        // Match RangeBarTrading's one-sided chart convention: ask charts show
        // longs, and bid charts show shorts.
        [Input] public bool IsAskChart { get; set; }
        [Input] public bool ShowDisplay { get; set; }
        [Input] public EPBDisplayLevel DisplayPBLevel { get; set; }

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_ProfileEMA;
        private readonly List<IDrawObject> m_DisplayDrawings =
            new List<IDrawObject>();

        public RangePBBounce(object ctx) : base(ctx)
        {
            IsAskChart = true;
            ShowDisplay = true;
            DisplayPBLevel = EPBDisplayLevel.PB2;
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

            // Historical bars arrive as closed bars, and this prevents a
            // forming live range bar from being marked repeatedly or early.
            if (Bars.Status != EBarState.Close || Bars.CurrentBar < 8) return;

            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;

            // PB1/PB0 continuations are governed only by their directional
            // 8-EMA rule, not the general PB 24-EMA/lead-in filters.
            int compactShortBodyTicks;
            if (IsDisplayDirectionAllowed(-1) &&
                TryGetCompletedPB1BodyTicks(-1, tickSize,
                                             out compactShortBodyTicks) &&
                compactShortBodyTicks <= 1 &&
                IsSelectedPBBody(compactShortBodyTicks) &&
                IsCompactShortPBContinuationValid(tickSize))
            {
                DrawPB1Label(-1, compactShortBodyTicks, tickSize);
                return;
            }

            int compactLongBodyTicks;
            if (IsDisplayDirectionAllowed(1) &&
                TryGetCompletedPB1BodyTicks(1, tickSize,
                                             out compactLongBodyTicks) &&
                compactLongBodyTicks <= 1 &&
                IsSelectedPBBody(compactLongBodyTicks) &&
                IsCompactLongPBContinuationValid(tickSize))
            {
                DrawPB1Label(1, compactLongBodyTicks, tickSize);
                return;
            }

            int direction = GetUnarmedPinBarDirection(tickSize);
            if (!IsDisplayDirectionAllowed(direction) ||
                !HasPriorBarTrendDirection(direction) ||
                !HasDirectionalFastEmaSlopeForThreeBars(direction) ||
                !IsCompletedPinBarTailOnFastEmaSide(direction) ||
                !IsPB1EmaOrderValid(direction) ||
                !IsPB1TrendFilterValid(direction, tickSize) ||
                !(HasPBLeadInStructure(direction) ||
                  IsStrongTrendContinuationValid(direction, tickSize)) ||
                !IsOpenOnCorrectEmaSide(direction, tickSize))
                return;

            int bodyTicks;
            if (!TryGetCompletedPB1BodyTicks(direction, tickSize, out bodyTicks) ||
                !IsSelectedPBBody(bodyTicks))
                return;

            // PB0/PB1 are compact setups.  RangeBarTrading evaluates them
            // only through the compact 8-EMA continuation rule above; they
            // must never fall through to the PB2 rule set.
            if (bodyTicks <= 1)
                return;

            DrawPB1Label(direction, bodyTicks, tickSize);
        }

        private int GetUnarmedPinBarDirection(double tickSize)
        {
            if (HasSharplyMovingFastEma(1, tickSize)) return 1;
            if (HasSharplyMovingFastEma(-1, tickSize)) return -1;
            return GetSlowEmaDirection();
        }

        private int GetSlowEmaDirection()
        {
            return m_SlowEMA[0] >= m_SlowEMA[1] ? 1 : -1;
        }

        private bool IsDisplayDirectionAllowed(int direction)
        {
            return direction > 0 ? IsAskChart : direction < 0 && !IsAskChart;
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

        private bool HasSharplyMovingFastEma(int direction, double tickSize)
        {
            double slope = GetAngle(m_FastEMA[0], m_FastEMA[EmaSlopeBars],
                                    EmaSlopeBars, tickSize);
            return direction > 0
                ? slope >= MinimumFastEmaSlopeDegrees
                : direction < 0 && slope <= -MinimumFastEmaSlopeDegrees;
        }

        private bool HasDirectionalFastEmaSlopeForThreeBars(int direction)
        {
            return direction > 0
                ? m_FastEMA[0] > m_FastEMA[1] &&
                  m_FastEMA[1] > m_FastEMA[2] &&
                  m_FastEMA[2] > m_FastEMA[3]
                : direction < 0 &&
                  m_FastEMA[0] < m_FastEMA[1] &&
                  m_FastEMA[1] < m_FastEMA[2] &&
                  m_FastEMA[2] < m_FastEMA[3];
        }

        private bool HasPriorBarTrendDirection(int direction)
        {
            return direction > 0
                ? Bars.Close[1] > Bars.Open[1]
                : direction < 0 && Bars.Close[1] < Bars.Open[1];
        }

        private bool IsCompletedPinBarTailOnFastEmaSide(int direction)
        {
            return direction > 0
                ? Bars.Low[0] >= m_FastEMA[0]
                : direction < 0 && Bars.High[0] <= m_FastEMA[0];
        }

        // The two completed lead-in bars must advance consecutively.  For a
        // long PB, each holds at/above the prior low, closes positively, and
        // closes above the prior bar.  The current PB is deliberately not
        // checked here: its lower rejection tail remains valid.
        private bool HasPBLeadInStructure(int direction)
        {
            for (int barsBack = 1; barsBack <= 2; barsBack++)
            {
                int priorBar = barsBack + 1;
                bool passes = direction > 0
                    ? Bars.Low[barsBack] >= Bars.Low[priorBar] &&
                      Bars.Close[barsBack] > Bars.Open[barsBack] &&
                      Bars.Close[barsBack] > Bars.Close[priorBar]
                    : Bars.High[barsBack] <= Bars.High[priorBar] &&
                      Bars.Close[barsBack] < Bars.Open[barsBack] &&
                      Bars.Close[barsBack] < Bars.Close[priorBar];
                if (!passes)
                    return false;
            }
            return true;
        }

        // In a sharply fanned 8/24/50 trend, a PB2 may be a healthy
        // continuation even if the older of the two lead-in bars did not
        // advance perfectly. The immediately prior bar must still advance,
        // and the PB rejection tail must remain on the 8-EMA trend side.
        private bool IsStrongTrendContinuationValid(int direction, double tickSize)
        {
            double fastSlope = GetAngle(m_FastEMA[0], m_FastEMA[EmaSlopeBars],
                                        EmaSlopeBars, tickSize);
            double slowSlope = GetAngle(m_SlowEMA[0], m_SlowEMA[EmaSlopeBars],
                                        EmaSlopeBars, tickSize);
            double separation = Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) / tickSize;
            return direction > 0
                ? Bars.Close[1] > Bars.Open[1] && Bars.Low[0] >= m_FastEMA[0] &&
                  separation >= StrongContinuationSeparationTicks &&
                  fastSlope >= StrongContinuationFastSlopeDegrees &&
                  slowSlope >= StrongContinuationSlowSlopeDegrees
                : Bars.Close[1] < Bars.Open[1] && Bars.High[0] <= m_FastEMA[0] &&
                  separation >= StrongContinuationSeparationTicks &&
                  fastSlope <= -StrongContinuationFastSlopeDegrees &&
                  slowSlope <= -StrongContinuationSlowSlopeDegrees;
        }

        private bool IsCompactShortPBContinuationValid(double tickSize)
        {
            return HasSharplyMovingFastEma(-1, tickSize) &&
                   HasDirectionalFastEmaSlopeForThreeBars(-1) &&
                   HasPriorBarTrendDirection(-1) &&
                   IsCompletedPinBarTailOnFastEmaSide(-1);
        }

        private bool IsCompactLongPBContinuationValid(double tickSize)
        {
            return HasSharplyMovingFastEma(1, tickSize) &&
                   HasDirectionalFastEmaSlopeForThreeBars(1) &&
                   HasPriorBarTrendDirection(1) &&
                   IsCompletedPinBarTailOnFastEmaSide(1);
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

            // The display supports PB3 through PB0: body/tail 3/2, 2/3,
            // 1/4, and 0/5. DisplayPBLevel selects the largest shown body.
            return bodyTicks >= 0 && bodyTicks <= 3 &&
                   tailTicks + bodyTicks == PinBarRangeTicks;
        }

        private bool IsSelectedPBBody(int bodyTicks)
        {
            return bodyTicks >= 0 && bodyTicks <= (int)DisplayPBLevel;
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
                "PB" + bodyTicks.ToString());
            if (label == null) return;

            label.Color = Color.DodgerBlue;
            label.Size = 9;
            label.HStyle = ETextStyleH.Center;
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
