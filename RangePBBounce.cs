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
        private const int BasePinBarRangeTicks = 5;
        private const int FastEmaLength = 8;
        private const int SlowEmaLength = 24;
        private const int ProfileEmaLength = 50;
        // Keep PB1 markers out of flat, overlapping-EMA congestion.
        private const int MinimumEmaSeparationTicks = 3;
        private const int EmaSlopeBars = 3;
        private const int SlowEmaPersistenceBars = 4;
        private const double MinimumFastEmaSlopeDegrees = 20.0;
        private const double StrongContinuationFastSlopeDegrees = 40.0;
        private const double StrongContinuationSlowSlopeDegrees = 15.0;
        private const double StrongContinuationSeparationTicks = 3.0;
        [Input] public bool ShowDisplay { get; set; }
        [Input] public EPBDisplayLevel DisplayPBLevel { get; set; }

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_ProfileEMA;
        private readonly List<IDrawObject> m_DisplayDrawings =
            new List<IDrawObject>();

        public RangePBBounce(object ctx) : base(ctx)
        {
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
                compactShortBodyTicks <= GetPB1BodyTicks(
                    GetPinBarRangeTicks(tickSize)) &&
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
                compactLongBodyTicks <= GetPB1BodyTicks(
                    GetPinBarRangeTicks(tickSize)) &&
                IsSelectedPBBody(compactLongBodyTicks) &&
                IsCompactLongPBContinuationValid(tickSize))
            {
                DrawPB1Label(1, compactLongBodyTicks, tickSize);
                return;
            }

            int direction = GetUnarmedPinBarDirection(tickSize);
            if (!IsDisplayDirectionAllowed(direction) ||
                !HasTwoPriorTrendCloses(direction) ||
                !HasPriorBarTrendDirection(direction) ||
                !HasRequiredEmaFan(direction, tickSize) ||
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
            if (bodyTicks <= GetPB1BodyTicks(GetPinBarRangeTicks(tickSize)))
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
            // This is a research/display indicator.  It shows both valid PB
            // directions on every chart; ask/bid routing is enforced only by
            // the live RangeBarTrading signal when it submits an order.
            return direction != 0;
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

        // A PB0 has no body: its open and close are both at the completion
        // extreme.  It is therefore directional by structure, but cannot
        // satisfy the strict Close > Open (or Close < Open) test above.  Let
        // a completed directional PB0 lead directly into a compact PB1/PB0
        // continuation without relaxing the normal PB2+ lead-in rule.
        private bool HasCompactPriorBarTrendConfirmation(int direction,
                                                         double tickSize)
        {
            return HasPriorBarTrendDirection(direction) ||
                   IsPriorDirectionalPB0(direction, tickSize);
        }

        private bool IsPriorDirectionalPB0(int direction, double tickSize)
        {
            double tolerance = tickSize * 0.1;
            double open = RoundToTick(Bars.Open[1], tickSize);
            double high = RoundToTick(Bars.High[1], tickSize);
            double low = RoundToTick(Bars.Low[1], tickSize);
            double close = RoundToTick(Bars.Close[1], tickSize);

            return direction > 0
                ? Math.Abs(open - high) <= tolerance &&
                  Math.Abs(close - high) <= tolerance &&
                  ToTicks(open - low, tickSize) == GetPinBarRangeTicks(tickSize)
                : direction < 0 &&
                  Math.Abs(open - low) <= tolerance &&
                  Math.Abs(close - low) <= tolerance &&
                  ToTicks(high - open, tickSize) == GetPinBarRangeTicks(tickSize);
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
            return HasDirectional824TrendContext(-1, tickSize) &&
                   HasRequiredEmaFan(-1, tickSize) &&
                   HasMinimumFastSlowSeparation(tickSize) &&
                   HasSharplyMovingFastEma(-1, tickSize) &&
                   HasDirectionalFastEmaSlopeForThreeBars(-1) &&
                   HasThreeBarCloseBreakout(-1) &&
                   HasCompactPriorBarTrendConfirmation(-1, tickSize) &&
                   IsCompletedPinBarTailOnFastEmaSide(-1);
        }

        private bool IsCompactLongPBContinuationValid(double tickSize)
        {
            return HasDirectional824TrendContext(1, tickSize) &&
                   HasRequiredEmaFan(1, tickSize) &&
                   HasMinimumFastSlowSeparation(tickSize) &&
                   HasSharplyMovingFastEma(1, tickSize) &&
                   HasDirectionalFastEmaSlopeForThreeBars(1) &&
                   HasThreeBarCloseBreakout(1) &&
                   HasCompactPriorBarTrendConfirmation(1, tickSize) &&
                   IsCompletedPinBarTailOnFastEmaSide(1);
        }

        // Compact PB1/PB0 patterns may use a sharply reversing 8 EMA, but
        // they must never trade against the 8/24 trend context.  The 50 EMA
        // is intentionally not part of this rule.
        private bool HasDirectional824TrendContext(int direction, double tickSize)
        {
            return direction > 0
                ? m_FastEMA[0] > m_SlowEMA[0] &&
                  HasPersistentSlowEmaDirection(1)
                : direction < 0 && m_FastEMA[0] < m_SlowEMA[0] &&
                  HasPersistentSlowEmaDirection(-1);
        }

        private bool HasPersistentSlowEmaDirection(int direction)
        {
            for (int barsBack = 0; barsBack < SlowEmaPersistenceBars; barsBack++)
            {
                if (direction > 0 && m_SlowEMA[barsBack] <= m_SlowEMA[barsBack + 1])
                    return false;
                if (direction < 0 && m_SlowEMA[barsBack] >= m_SlowEMA[barsBack + 1])
                    return false;
            }
            return true;
        }

        // A PB is a continuation pattern, so the two completed bars leading
        // into it must already have closed with the candidate direction.
        private bool HasTwoPriorTrendCloses(int direction)
        {
            for (int barsBack = 1; barsBack <= 2; barsBack++)
            {
                bool closesWithTrend = direction > 0
                    ? Bars.Close[barsBack] > Bars.Open[barsBack]
                    : Bars.Close[barsBack] < Bars.Open[barsBack];
                if (!closesWithTrend) return false;
            }
            return true;
        }

        // PB1/PB0 may recover from a mixed-color micro-pullback only when
        // their own close breaks beyond every one of the prior three closes.
        private bool HasThreeBarCloseBreakout(int direction)
        {
            for (int barsBack = 1; barsBack <= 3; barsBack++)
            {
                if (direction > 0 && Bars.Close[0] <= Bars.Close[barsBack])
                    return false;
                if (direction < 0 && Bars.Close[0] >= Bars.Close[barsBack])
                    return false;
            }
            return true;
        }

        private bool HasMinimumFastSlowSeparation(double tickSize)
        {
            return Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) >=
                   MinimumEmaSeparationTicks * tickSize;
        }

        private bool HasRequiredEmaFan(int direction, double tickSize)
        {
            double minimumGap = GetPinBarRangeTicks(tickSize) * 0.5;
            return direction > 0
                ? m_FastEMA[0] > m_SlowEMA[0] && m_SlowEMA[0] > m_ProfileEMA[0] &&
                  (m_FastEMA[0] - m_SlowEMA[0]) / tickSize >= minimumGap &&
                  (m_SlowEMA[0] - m_ProfileEMA[0]) / tickSize >= minimumGap
                : direction < 0 && m_FastEMA[0] < m_SlowEMA[0] && m_SlowEMA[0] < m_ProfileEMA[0] &&
                  (m_SlowEMA[0] - m_FastEMA[0]) / tickSize >= minimumGap &&
                  (m_ProfileEMA[0] - m_SlowEMA[0]) / tickSize >= minimumGap;
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

            int rangeTicks = GetPinBarRangeTicks(tickSize);
            // Scale the original PB2/PB1/PB0 ratios to the active range.
            // The compact PB1 category accepts every nonzero body at or
            // below its scaled body cap: on 8-tick bars, both 6/2 and 7/1.
            return IsSupportedPBBody(rangeTicks, bodyTicks) &&
                   tailTicks + bodyTicks == rangeTicks;
        }

        private bool IsSelectedPBBody(int bodyTicks)
        {
            int rangeTicks = GetPinBarRangeTicks(GetTickSize());
            return GetPBDisplayLevel(rangeTicks, bodyTicks) <=
                   (int)DisplayPBLevel;
        }

        private double GetTickSize()
        {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            return tickSize > 0 ? tickSize : 0.25;
        }

        private int GetPinBarRangeTicks(double tickSize)
        {
            return Math.Max(1, ToTicks(Bars.High[0] - Bars.Low[0], tickSize));
        }

        private int GetPB2BodyTicks(int rangeTicks)
        {
            return Math.Max(1, (int)Math.Round(rangeTicks * 2.0 /
                                                BasePinBarRangeTicks));
        }

        private int GetPB1BodyTicks(int rangeTicks)
        {
            return Math.Max(1, (int)Math.Round(rangeTicks * 1.0 /
                                                BasePinBarRangeTicks));
        }

        private bool IsSupportedPBBody(int rangeTicks, int bodyTicks)
        {
            return bodyTicks == GetPB2BodyTicks(rangeTicks) ||
                   (bodyTicks >= 0 && bodyTicks <= GetPB1BodyTicks(rangeTicks));
        }

        private int GetPBDisplayLevel(int rangeTicks, int bodyTicks)
        {
            if (bodyTicks == 0) return 0;
            if (bodyTicks <= GetPB1BodyTicks(rangeTicks)) return 1;
            return 2;
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
                "PB" + GetPBDisplayLevel(GetPinBarRangeTicks(tickSize),
                                           bodyTicks).ToString());
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
