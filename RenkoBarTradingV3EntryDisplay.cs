using System;
using System.Collections.Generic;
using System.Drawing;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Display-only companion for RenkoBarTradingV3.  It deliberately evaluates
    // both directions: chart role, arming, and order management belong to the
    // signal, not to this visual research tool.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    public class RenkoBarTradingV3EntryDisplay : IndicatorObject
    {
        private const int StrongSlopeBars = 3;
        private const double Minimum24SlopeDegrees = 40.0;
        private const double Minimum50SlopeDegrees = 20.0;

        [Input] public bool ShowDisplay { get; set; }
        // This is deliberately independent of RangeBarDiagnostic's 4.5-tick
        // strong-continuation value, which measures 8/24 separation.  Renko
        // entries need a substantially matured 24/50 fan.
        [Input] public double Minimum2450SeparationTicks { get; set; }
        // A mature 8/24 fan is required, while allowing the validated long
        // and short profiles whose diagnostic gaps were 27.19t and 28.35t.
        [Input] public double Minimum824SeparationTicks { get; set; }

        private XAverage m_Ema8;
        private XAverage m_Ema24;
        private XAverage m_Ema50;
        private readonly List<IDrawObject> m_DisplayDrawings =
            new List<IDrawObject>();

        public RenkoBarTradingV3EntryDisplay(object ctx) : base(ctx)
        {
            ShowDisplay = true;
            Minimum2450SeparationTicks = 14.0;
            Minimum824SeparationTicks = 27.0;
        }

        protected override void Create()
        {
            m_Ema8 = new XAverage(this);
            m_Ema24 = new XAverage(this);
            m_Ema50 = new XAverage(this);
        }

        protected override void StartCalc()
        {
            ClearDisplayDrawings();
            m_Ema8.Length = 8; m_Ema8.Price = Bars.Close;
            m_Ema24.Length = 24; m_Ema24.Price = Bars.Close;
            m_Ema50.Length = 50; m_Ema50.Price = Bars.Close;
        }

        protected override void CalcBar()
        {
            if (!ShowDisplay)
            {
                ClearDisplayDrawings();
                return;
            }

            // This is a completed-bar display.  It produces one durable mark
            // per confirmed historical Renko setup, never a repeating mark on
            // every tick of a still-forming brick.
            if (Bars.Status != EBarState.Close || Bars.CurrentBar < 51) return;

            double tickSize = GetTickSize();
            int direction = GetAlignedTrendDirection();
            if (direction == 0 || !HasStrong2450Profile(direction, tickSize) ||
                !HasMinimum824Separation(tickSize) ||
                !HasThreePriorDirectionalCloses(direction) ||
                !HasDirectionalClose(direction) ||
                !HasTailPierce(direction) ||
                !HasTwoBarBreakout(direction)) return;

            DrawEntry(direction, tickSize);
        }

        // Same internal EMA gate as the V3 signal: 8/24/50 must be stacked
        // and each average must slope in the aligned direction.
        private int GetAlignedTrendDirection()
        {
            bool bullish = m_Ema8[0] > m_Ema24[0] && m_Ema24[0] > m_Ema50[0] &&
                           m_Ema8[0] > m_Ema8[1] && m_Ema24[0] > m_Ema24[1] &&
                           m_Ema50[0] > m_Ema50[1];
            bool bearish = m_Ema8[0] < m_Ema24[0] && m_Ema24[0] < m_Ema50[0] &&
                           m_Ema8[0] < m_Ema8[1] && m_Ema24[0] < m_Ema24[1] &&
                           m_Ema50[0] < m_Ema50[1];
            return bullish ? 1 : bearish ? -1 : 0;
        }

        // The pullback tail reaches the preceding Renko brick's open.
        private bool HasTailPierce(int direction)
        {
            return direction > 0
                ? Bars.Low[0] <= Bars.Open[1]
                : Bars.High[0] >= Bars.Open[1];
        }

        // The confirmation brick must finish in the trade direction.  A
        // bearish close cannot display a buy marker, and vice versa.
        private bool HasDirectionalClose(int direction)
        {
            return direction > 0
                ? Bars.Close[0] > Bars.Open[0]
                : Bars.Close[0] < Bars.Open[0];
        }

        // The three bricks leading into the pullback/continuation setup must
        // already demonstrate the same directional conviction as the trade.
        private bool HasThreePriorDirectionalCloses(int direction)
        {
            for (int barsBack = 1; barsBack <= 3; barsBack++)
            {
                bool closesWithTrend = direction > 0
                    ? Bars.Close[barsBack] > Bars.Open[barsBack]
                    : Bars.Close[barsBack] < Bars.Open[barsBack];
                if (!closesWithTrend) return false;
            }
            return true;
        }

        // Requires a materially mature 24/50 fan and decisive three-bar
        // slopes in the trade direction.  The gap is its own Renko setting;
        // the Range diagnostic's 4.5t strong-continuation threshold belongs
        // to the 8/24 gap and is intentionally not reused here.
        private bool HasStrong2450Profile(int direction, double tickSize)
        {
            double separationTicks = Math.Abs(m_Ema24[0] - m_Ema50[0]) / tickSize;
            // Older saved indicator instances may initialize a newly added
            // input to zero.  Keep the safety filter effective in that case.
            double requiredSeparation = Minimum2450SeparationTicks > 0
                ? Minimum2450SeparationTicks : 14.0;
            if (separationTicks < requiredSeparation) return false;

            double slope24 = GetAngle(m_Ema24[0], m_Ema24[StrongSlopeBars],
                                      StrongSlopeBars, tickSize);
            double slope50 = GetAngle(m_Ema50[0], m_Ema50[StrongSlopeBars],
                                      StrongSlopeBars, tickSize);
            return direction > 0
                ? slope24 >= Minimum24SlopeDegrees && slope50 >= Minimum50SlopeDegrees
                : slope24 <= -Minimum24SlopeDegrees && slope50 <= -Minimum50SlopeDegrees;
        }

        // The 8/24 fan must also be fully developed.  This gap is absolute so
        // the same filter applies symmetrically to bullish and bearish setups.
        private bool HasMinimum824Separation(double tickSize)
        {
            double requiredSeparation = Minimum824SeparationTicks > 0
                ? Minimum824SeparationTicks : 27.0;
            return Math.Abs(m_Ema8[0] - m_Ema24[0]) / tickSize >= requiredSeparation;
        }

        // Additional confirmation requested for V3 research: the current
        // long brick breaks above both preceding highs; shorts mirror it by
        // breaking below both preceding lows.
        private bool HasTwoBarBreakout(int direction)
        {
            return direction > 0
                ? Bars.High[0] > Bars.High[1] && Bars.High[0] > Bars.High[2]
                : Bars.Low[0] < Bars.Low[1] && Bars.Low[0] < Bars.Low[2];
        }

        private void DrawEntry(int direction, double tickSize)
        {
            double arrowPrice = direction > 0
                ? Bars.Low[0] - 2 * tickSize : Bars.High[0] + 2 * tickSize;
            IArrowObject arrow = DrwArrow.Create(
                new ChartPoint(Bars.Time[0], arrowPrice), direction < 0);
            if (arrow != null)
            {
                arrow.Color = direction > 0 ? Color.DodgerBlue : Color.DeepPink;
                arrow.Size = 4;
                m_DisplayDrawings.Add(arrow);
            }

            ITextObject label = DrwText.Create(
                new ChartPoint(Bars.Time[0], arrowPrice), "B");
            if (label != null)
            {
                label.Color = direction > 0 ? Color.DodgerBlue : Color.DeepPink;
                label.Size = 8;
                label.HStyle = ETextStyleH.Center;
                label.VStyle = direction > 0 ? ETextStyleV.Below : ETextStyleV.Above;
                m_DisplayDrawings.Add(label);
            }
        }

        private double GetTickSize()
        {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            return tickSize > 0 ? tickSize : 0.25;
        }

        private double GetAngle(double current, double previous, int barsBack,
                                double tickSize)
        {
            return Math.Atan2(current - previous, barsBack * tickSize) *
                   (180.0 / Math.PI);
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
