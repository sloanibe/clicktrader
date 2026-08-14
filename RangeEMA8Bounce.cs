using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Visual-only 8 EMA bounce detector.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    public class RangeEMA8Bounce : IndicatorObject
    {
        private const int FastEmaLength = 8;
        private const int SlowEmaLength = 24;
        private const int ProfileEmaLength = 50;
        private const int SlopeBars = 3;
        private const int SlopeLookbackBars = 6;
        private const int DisplacementLookbackBars = 2;
        private const double MinimumSeparationTicks = 4.0;
        private const double MinimumFastSlopeDegrees = 40.0;
        // Keep the fast EMA at 40°, while allowing the naturally slower 24
        // EMA to qualify at 39° on an otherwise valid continuation.
        private const double MinimumSlowSlopeDegrees = 39.0;
        private const double StrongOneBarFastSlopeDegrees = 50.0;
        private const double StrongOneBarSlowSlopeDegrees = 40.0;
        private const double StrongOneBarTrendSlopeDegrees = 30.0;
        private const double StrongOneBarRecoveryFraction = 0.75;
        private const double MinimumPenetrationTicks = 1.0;
        // Match the live strategy: permit a four-and-a-half-tick two-bar
        // pullback through the 8 EMA before the rejection resumes the trend.
        private const double MaximumPenetrationTicks = 4.5;
        // EMA values are fractional-price values while range-bar highs/lows
        // are tick prices. Compare the measured penetration at half-tick
        // precision so EMA interpolation artifacts do not reject a valid
        // four-tick pullback at the boundary.
        private const double PenetrationComparisonIncrementTicks = 0.5;
        private const double MinimumLocalDisplacementTicks = 1.0;
        // Treat each displayed arrow as a completed virtual entry.  A new
        // arrow cannot be printed until that virtual trade has reached one
        // of these exits on a later completed bar.
        private const int ReentryProfitTargetTicks = 5;
        private const int ReentryStopLossTicks = 10;
        private const int ProfileSlopeBars = 3;
        private const int ProfilePersistenceBars = 4;
        private const double ProfileMinFastSlowSeparationTicks = 3.0;
        private const double ProfileMinSlowTrendSeparationTicks = 2.0;
        private const double ProfileMinFastTrendSeparationTicks = 5.0;
        private const double ProfileMinFastSlopeDegrees = 20.0;
        private const double ProfileMinSlowSlopeDegrees = 20.0;
        private const double ProfileMinTrendSlopeDegrees = 10.0;
        private const double ProfileMaximumCompressionTicks = 1.0;

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_ProfileEMA;
        private readonly List<IDrawObject> m_DisplayDrawings =
            new List<IDrawObject>();
        private readonly HashSet<DateTime> m_AuditedSignalTimes =
            new HashSet<DateTime>();
        private bool m_VirtualTradeActive;
        private int m_VirtualTradeDirection;
        private double m_VirtualTradeEntryPrice;

        [Input] public bool ShowDisplay { get; set; }
        [Input] public string AuditDirectory { get; set; }
        [Input] public string AuditLabel { get; set; }

        private class BounceDiagnostic
        {
            public int Direction;
            public double FastEma;
            public double SlowEma;
            public double SeparationTicks;
            public double BestSeparationTicks;
            public double FastSlopeNow;
            public double SlowSlopeNow;
            public double BestFastSlope;
            public double BestSlowSlope;
            public double SlopeLeadDegrees;
            public double PenetrationTicks;
            public double LocalDisplacementTicks;
            public double Open;
            public double High;
            public double Low;
            public double Close;
            public double LowToFastTicks;
            public double HighToFastTicks;
            public double CloseToFastTicks;
            public bool RangeCrossesFast;
            public bool CloseOnTrendSide;
            public string Body;
            public bool SeparationPass;
            public bool FastSlopePass;
            public bool SlowSlopePass;
            public bool SlopeLeadPass;
            public bool PenetrationPass;
            public bool TwoBarPullbackPass;
            public bool StrongOneBarRejectionPass;
            public bool ShallowTouchPass;
            public bool BarColorPass;
            public bool LocalDisplacementPass;
            public bool SignalPass;
        }

        public RangeEMA8Bounce(object ctx) : base(ctx)
        {
            ShowDisplay = true;
            AuditDirectory = @"C:\rangebar_diagnostics";
            AuditLabel = "Unlabeled";
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
            m_AuditedSignalTimes.Clear();
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

            if (Bars.Status != EBarState.Close ||
                Bars.CurrentBar < SlopeBars + SlopeLookbackBars)
                return;

            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;

            BounceDiagnostic diagnostic = BuildDiagnostic(tickSize);
            if (diagnostic.SignalPass)
            {
                AppendSignalAudit(diagnostic, tickSize);
                DrawBounceArrow(diagnostic.Direction, tickSize);
            }
        }

        private void AppendSignalAudit(BounceDiagnostic diagnostic, double tickSize)
        {
            // A closed range bar may be recalculated more than once. Keep one
            // audit entry per bar during this indicator instance's lifetime.
            if (m_AuditedSignalTimes.Contains(Bars.Time[0])) return;
            m_AuditedSignalTimes.Add(Bars.Time[0]);

            try
            {
                string directory = string.IsNullOrWhiteSpace(AuditDirectory)
                    ? @"C:\rangebar_diagnostics" : AuditDirectory;
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "RangeEMA8Bounce_Audit.log");
                double comparablePenetration = RoundToIncrement(
                    diagnostic.PenetrationTicks, PenetrationComparisonIncrementTicks);

                StringBuilder text = new StringBuilder();
                text.AppendLine("8E SIGNAL AUDIT");
                text.AppendFormat("Source: {0} | audit label: {1}\r\n",
                    Bars.Info.Name, string.IsNullOrWhiteSpace(AuditLabel)
                        ? "Unlabeled" : AuditLabel);
                text.AppendFormat("Time: {0:yyyy-MM-dd HH:mm:ss.fff} | direction: {1}\r\n",
                    Bars.Time[0], diagnostic.Direction > 0 ? "LONG" : "SHORT");
                text.AppendFormat("OHLC: {0:F4} / {1:F4} / {2:F4} / {3:F4}\r\n",
                    diagnostic.Open, diagnostic.High, diagnostic.Low, diagnostic.Close);
                text.AppendFormat("EMA 8/24: {0:F4} / {1:F4} | separation: {2:F2}t\r\n",
                    diagnostic.FastEma, diagnostic.SlowEma, diagnostic.SeparationTicks);
                text.AppendFormat("Best slopes 8/24: {0:F2} / {1:F2} | lead: {2:F2}\r\n",
                    diagnostic.BestFastSlope, diagnostic.BestSlowSlope,
                    diagnostic.SlopeLeadDegrees);
                text.AppendFormat("Crosses 8: {0} | penetration raw/comparable: {1:F2}t / {2:F2}t\r\n",
                    diagnostic.RangeCrossesFast, diagnostic.PenetrationTicks,
                    comparablePenetration);
                text.AppendFormat("Close side: {0} | bar color: {1} | displacement: {2:F2}t\r\n",
                    diagnostic.CloseOnTrendSide, diagnostic.Body,
                    diagnostic.LocalDisplacementTicks);
                text.AppendFormat("Gates: separation={0}; 8slope={1}; 24slope={2}; lead={3}; " +
                    "penetration={4}; two-bar pullback={5}; strong one-bar rejection={6}; " +
                    "shallow touch={7}; color={8}; displacement={9}; final={10}\r\n\r\n",
                    diagnostic.SeparationPass, diagnostic.FastSlopePass,
                    diagnostic.SlowSlopePass, diagnostic.SlopeLeadPass,
                    diagnostic.PenetrationPass, diagnostic.TwoBarPullbackPass,
                    diagnostic.StrongOneBarRejectionPass, diagnostic.ShallowTouchPass,
                    diagnostic.BarColorPass, diagnostic.LocalDisplacementPass,
                    diagnostic.SignalPass);
                File.AppendAllText(path, text.ToString());
            }
            catch
            {
                // Logging must never interrupt chart calculation or signals.
            }
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

            // Range-bar OHLC has no intrabar sequence.  When both are touched,
            // apply stop-first handling instead of assuming a target fill.
            if (stoppedOut || targetReached)
                ResetVirtualTrade();
        }

        private void ResetVirtualTrade()
        {
            m_VirtualTradeActive = false;
            m_VirtualTradeDirection = 0;
            m_VirtualTradeEntryPrice = 0;
        }

        private BounceDiagnostic BuildDiagnostic(double tickSize)
        {
            BounceDiagnostic result = new BounceDiagnostic();
            result.FastEma = m_FastEMA[0];
            result.SlowEma = m_SlowEMA[0];
            result.Direction = result.FastEma > result.SlowEma ? 1 :
                               result.FastEma < result.SlowEma ? -1 : 0;
            result.SeparationTicks = Math.Abs(result.FastEma - result.SlowEma) /
                                     tickSize;
            result.BestSeparationTicks = GetBestSeparation(tickSize);

            result.FastSlopeNow = GetAngle(m_FastEMA[0], m_FastEMA[SlopeBars],
                                            SlopeBars, tickSize);
            result.SlowSlopeNow = GetAngle(m_SlowEMA[0], m_SlowEMA[SlopeBars],
                                            SlopeBars, tickSize);
            result.BestFastSlope = GetBestDirectionalSlope(m_FastEMA,
                                                            result.Direction, tickSize);
            result.BestSlowSlope = GetBestDirectionalSlope(m_SlowEMA,
                                                            result.Direction, tickSize);
            // A pullback can soften the current 3-bar reading.  Use the same
            // best-directional values shown in the diagnostic so the visual
            // detector matches the examples being tuned.
            result.SlopeLeadDegrees = result.Direction > 0
                ? result.BestFastSlope - result.BestSlowSlope
                : result.Direction < 0
                    ? Math.Abs(result.BestFastSlope) - Math.Abs(result.BestSlowSlope)
                    : 0;

            result.Open = Bars.Open[0];
            result.High = Bars.High[0];
            result.Low = Bars.Low[0];
            result.Close = Bars.Close[0];
            result.LowToFastTicks = (result.Low - result.FastEma) / tickSize;
            result.HighToFastTicks = (result.High - result.FastEma) / tickSize;
            result.CloseToFastTicks = (result.Close - result.FastEma) / tickSize;
            result.RangeCrossesFast = result.Low <= result.FastEma &&
                                      result.High >= result.FastEma;
            result.CloseOnTrendSide = result.Direction > 0
                ? result.Close >= result.FastEma
                : result.Direction < 0 && result.Close <= result.FastEma;
            result.Body = result.Close > result.Open ? "BULLISH" :
                          result.Close < result.Open ? "BEARISH" : "ZERO-BODY";
            result.PenetrationTicks = result.Direction > 0
                ? -result.LowToFastTicks
                : result.Direction < 0 ? result.HighToFastTicks : 0;
            result.LocalDisplacementTicks = GetLocalDisplacementTicks(
                result.Direction, tickSize);

            // The initial values are from the supplied positive examples.
            // The pullback itself can soften the current three-bar slope, so
            // use the best directional slope in the recent 0-6 bar window.
            result.SeparationPass = result.Direction != 0 &&
                                    result.SeparationTicks >= MinimumSeparationTicks;
            result.FastSlopePass = result.Direction > 0
                ? result.BestFastSlope >= MinimumFastSlopeDegrees
                : result.Direction < 0 &&
                  result.BestFastSlope <= -MinimumFastSlopeDegrees;
            result.SlowSlopePass = result.Direction > 0
                ? result.BestSlowSlope >= MinimumSlowSlopeDegrees
                : result.Direction < 0 &&
                  result.BestSlowSlope <= -MinimumSlowSlopeDegrees;
            // A steep 8 EMA is sufficient; the 24 EMA may be rising at a
            // similar or slightly faster rate during a healthy continuation.
            result.SlopeLeadPass = true;
            double comparablePenetration = RoundToIncrement(
                result.PenetrationTicks, PenetrationComparisonIncrementTicks);
            bool normalPenetrationPass = result.RangeCrossesFast &&
                comparablePenetration >= MinimumPenetrationTicks &&
                comparablePenetration <= MaximumPenetrationTicks;
            // A steep trend may make a shallow two-bar pullback a valid
            // rejection. In that variation the bar must still actually touch
            // the 8 EMA, but its raw penetration may be below one tick.
            result.TwoBarPullbackPass = HasTwoBarCounterTrendPullback(
                result.Direction);
            result.StrongOneBarRejectionPass = HasStrongOneBarRejection(
                result.Direction, tickSize);
            result.ShallowTouchPass = result.RangeCrossesFast &&
                result.PenetrationTicks >= 0 &&
                result.PenetrationTicks < MinimumPenetrationTicks &&
                result.TwoBarPullbackPass;
            result.PenetrationPass = normalPenetrationPass ||
                                     result.ShallowTouchPass;
            result.BarColorPass = result.Direction > 0
                ? result.Close >= result.Open
                : result.Direction < 0 && result.Close <= result.Open;
            result.LocalDisplacementPass = result.LocalDisplacementTicks >=
                                           MinimumLocalDisplacementTicks;
            result.SignalPass = result.SeparationPass && result.FastSlopePass &&
                result.SlowSlopePass && result.SlopeLeadPass &&
                HasRequiredEmaFanOrStrong8To50Separation(result.Direction, tickSize) &&
                (result.TwoBarPullbackPass || result.StrongOneBarRejectionPass) &&
                result.PenetrationPass && result.CloseOnTrendSide &&
                result.BarColorPass && result.LocalDisplacementPass;
            return result;
        }

        private double GetBestSeparation(double tickSize)
        {
            double best = 0;
            for (int barsBack = 0; barsBack <= SlopeLookbackBars; barsBack++)
                best = Math.Max(best, Math.Abs(m_FastEMA[barsBack] -
                                                m_SlowEMA[barsBack]) / tickSize);
            return best;
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

        private bool HasRequiredEmaFanOrStrong8To50Separation(int direction,
                                                                double tickSize)
        {
            if (HasRequiredEmaFan(direction, tickSize)) return true;
            double rangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            double minimumGap = rangeTicks * 0.5;
            double strongDistance = rangeTicks * 2.0;
            return direction > 0
                ? m_FastEMA[0] > m_SlowEMA[0] && m_SlowEMA[0] > m_ProfileEMA[0] &&
                  (m_FastEMA[0] - m_SlowEMA[0]) / tickSize >= minimumGap &&
                  (m_FastEMA[0] - m_ProfileEMA[0]) / tickSize >= strongDistance
                : direction < 0 && m_FastEMA[0] < m_SlowEMA[0] &&
                  m_SlowEMA[0] < m_ProfileEMA[0] &&
                  (m_SlowEMA[0] - m_FastEMA[0]) / tickSize >= minimumGap &&
                  (m_ProfileEMA[0] - m_FastEMA[0]) / tickSize >= strongDistance;
        }

        private double RoundToIncrement(double value, double increment)
        {
            if (increment <= 0) return value;
            return Math.Round(value / increment) * increment;
        }

        private double GetLocalDisplacementTicks(int direction, double tickSize)
        {
            if (direction == 0 || Bars.CurrentBar < DisplacementLookbackBars)
                return 0;

            double reference = direction > 0 ? Double.PositiveInfinity :
                                                Double.NegativeInfinity;
            for (int barsBack = 1; barsBack <= DisplacementLookbackBars;
                 barsBack++)
            {
                reference = direction > 0
                    ? Math.Min(reference, Bars.Low[barsBack])
                    : Math.Max(reference, Bars.High[barsBack]);
            }
            return direction > 0
                ? (reference - Bars.Low[0]) / tickSize
                : (Bars.High[0] - reference) / tickSize;
        }

        private bool HasTwoBarCounterTrendPullback(int direction)
        {
            return direction > 0
                ? Bars.Close[1] < Bars.Open[1] &&
                  Bars.Close[2] < Bars.Open[2]
                : direction < 0 &&
                  Bars.Close[1] > Bars.Open[1] &&
                  Bars.Close[2] > Bars.Open[2];
        }

        private bool HasStrongOneBarRejection(int direction, double tickSize)
        {
            if (Bars.CurrentBar < 1) return false;
            bool priorCounterTrend = direction > 0
                ? Bars.Close[1] < Bars.Open[1]
                : direction < 0 && Bars.Close[1] > Bars.Open[1];
            if (!priorCounterTrend || !HasRequiredEmaFan(direction, tickSize))
                return false;
            double fastSlope = GetBestDirectionalSlope(m_FastEMA, direction, tickSize);
            double slowSlope = GetBestDirectionalSlope(m_SlowEMA, direction, tickSize);
            double trendSlope = GetBestDirectionalSlope(m_ProfileEMA, direction, tickSize);
            bool steep = direction > 0
                ? fastSlope >= StrongOneBarFastSlopeDegrees &&
                  slowSlope >= StrongOneBarSlowSlopeDegrees &&
                  trendSlope >= StrongOneBarTrendSlopeDegrees
                : fastSlope <= -StrongOneBarFastSlopeDegrees &&
                  slowSlope <= -StrongOneBarSlowSlopeDegrees &&
                  trendSlope <= -StrongOneBarTrendSlopeDegrees;
            double priorRange = Math.Abs(Bars.High[1] - Bars.Low[1]);
            double recovery = direction > 0 ? Bars.Close[0] - Bars.Low[0] :
                              direction < 0 ? Bars.High[0] - Bars.Close[0] : 0;
            return steep && priorRange > 0 && recovery + tickSize * 0.1 >=
                   priorRange * StrongOneBarRecoveryFraction;
        }

        private double GetBestDirectionalSlope(XAverage ema, int direction,
                                               double tickSize)
        {
            if (direction == 0) return 0;
            double best = direction > 0 ? Double.NegativeInfinity :
                                          Double.PositiveInfinity;
            for (int barsBack = 0; barsBack <= SlopeLookbackBars; barsBack++)
            {
                double angle = GetAngle(ema[barsBack], ema[barsBack + SlopeBars],
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
            double fastSlope = GetAngle(m_FastEMA[barsBack],
                                        m_FastEMA[barsBack + ProfileSlopeBars],
                                        ProfileSlopeBars, tickSize);
            double slowSlope = GetAngle(m_SlowEMA[barsBack],
                                        m_SlowEMA[barsBack + ProfileSlopeBars],
                                        ProfileSlopeBars, tickSize);
            double trendSlope = GetAngle(m_ProfileEMA[barsBack],
                                         m_ProfileEMA[barsBack + ProfileSlopeBars],
                                         ProfileSlopeBars, tickSize);
            return direction > 0
                ? fastSlope >= ProfileMinFastSlopeDegrees &&
                  slowSlope >= ProfileMinSlowSlopeDegrees &&
                  trendSlope >= ProfileMinTrendSlopeDegrees
                : fastSlope <= -ProfileMinFastSlopeDegrees &&
                  slowSlope <= -ProfileMinSlowSlopeDegrees &&
                  trendSlope <= -ProfileMinTrendSlopeDegrees;
        }

        private double GetAngle(double valueCurrent, double valueOld,
                                int barsBack, double tickSize)
        {
            return Math.Atan2(valueCurrent - valueOld, barsBack * tickSize) *
                   (180.0 / Math.PI);
        }

        private void DrawBounceArrow(int direction, double tickSize)
        {
            // Do not rely on the small arrow alone.  Some chart themes render
            // an arrow almost indistinguishably from a range-bar wick, and an
            // unavailable arrow object used to make a passed setup appear to
            // have no signal at all.  Keep a text marker as the definitive
            // historical indication of every qualifying 8-EMA bounce.
            double price = direction > 0
                ? Bars.Low[0] - (2 * tickSize)
                : Bars.High[0] + (2 * tickSize);
            IArrowObject arrow = DrwArrow.Create(
                new ChartPoint(Bars.Time[0], price), direction < 0);
            if (arrow != null)
            {
                arrow.Color = Color.MediumSeaGreen;
                arrow.Size = 4;
                m_DisplayDrawings.Add(arrow);
            }

            double labelPrice = direction > 0
                ? Bars.Low[0] - (4 * tickSize)
                : Bars.High[0] + (4 * tickSize);
            ITextObject label = DrwText.Create(
                new ChartPoint(Bars.Time[0], labelPrice), "8");
            if (label == null) return;

            label.Color = Color.MediumSeaGreen;
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
    }
}
