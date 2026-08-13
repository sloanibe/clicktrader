using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Standalone read-only diagnostic companion for the range-bar strategies.
    // Alt+D + left-click a bar to see every relevant PB/EMA gate for that bar.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    [MouseEvents(true)]
    public class RangeBarDiagnostic : IndicatorObject
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        private const int FastEmaLength = 8;
        private const int SlowEmaLength = 24;
        private const int TrendEmaLength = 50;
        private const int SlopeBars = 3;
        private const int SlopeLookbackBars = 6;
        private const int BasePinBarRangeTicks = 5;
        private const int PBSlowEmaPersistenceBars = 4;
        private const double PBMinimumSeparationTicks = 3.0;
        private const double PBMinimumFastSlopeDegrees = 20.0;
        private const double Ema8MinimumSeparationTicks = 4.5;
        private const double Ema8MinimumFastSlopeDegrees = 40.0;
        private const double Ema8MinimumSlowSlopeDegrees = 40.0;
        private const double Ema8MinimumPenetrationTicks = 1.0;
        private const double Ema8MaximumPenetrationTicks = 4.5;
        private const double Ema24MinimumCurrentSeparationTicks = 1.5;
        private const double Ema24MinimumBestSeparationTicks = 2.0;
        private const double Ema24MinimumSlowSlopeDegrees = 20.0;
        private const double Ema24TouchToleranceTicks = 0.25;
        private const int ProfilePersistenceBars = 4;
        private const double ProfileMinFastSlowSeparationTicks = 3.0;
        private const double ProfileMinSlowTrendSeparationTicks = 2.0;
        private const double ProfileMinFastTrendSeparationTicks = 5.0;
        private const double ProfileMinFastSlopeDegrees = 20.0;
        private const double ProfileMinSlowSlopeDegrees = 20.0;
        private const double ProfileMinTrendSlopeDegrees = 10.0;
        private const double ProfileMaximumCompressionTicks = 1.0;
        private const double StrongContinuationFastSlopeDegrees = 45.0;
        private const double StrongContinuationSlowSlopeDegrees = 40.0;
        private const double StrongContinuationTrendSlopeDegrees = 20.0;
        private const double StrongContinuationSeparationTicks = 4.5;
        private const double Ema50TouchToleranceTicks = 0.75;
        private const double Ema50MinimumSlowTrendSeparationTicks = 3.0;
        private const double Ema50MinimumPriorTrendSlopeDegrees = 15.0;

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_TrendEMA;
        private readonly Dictionary<DateTime, DiagnosticSnapshot> m_SnapshotsByTime =
            new Dictionary<DateTime, DiagnosticSnapshot>();
        private Form m_DiagnosticWindow;
        private TextBox m_DiagnosticText;
        private TextBox m_NotesText;
        private Button m_SaveNotesButton;
        private ITextObject m_FirstSelectionNotice;
        private ITextObject m_CompletionNotice;
        private DateTime m_FirstSelectionTime = DateTime.MinValue;
        private DateTime m_SecondSelectionTime = DateTime.MinValue;
        private List<DiagnosticSnapshot> m_ActiveRangeSnapshots;
        private string m_ActiveRangeReport = "";
        private string m_ActiveRangeExportPath = "";

        [Input] public string ExportDirectory { get; set; }

        private class DiagnosticSnapshot
        {
            public DateTime Time;
            public double Open, High, Low, Close;
            public double FastEma, SlowEma, TrendEma;
            public double FastSlope, SlowSlope, TrendSlope;
            public double Separation824, Separation850, Separation2450;
            public bool BullishOrder, BearishOrder;
            public bool CompactShortPB, CompactLongPB, GeneralPB;
            public bool Ema8Bounce, Ema24Bounce, Ema50Bounce;
            public string GeneralPBDetail;
            public string Report;
        }

        public RangeBarDiagnostic(object ctx) : base(ctx)
        {
            ExportDirectory = @"C:\rangebar_diagnostics";
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this);
            m_SlowEMA = new XAverage(this);
            m_TrendEMA = new XAverage(this);
        }

        protected override void StartCalc()
        {
            m_SnapshotsByTime.Clear();
            m_FirstSelectionTime = DateTime.MinValue;
            m_SecondSelectionTime = DateTime.MinValue;
            m_ActiveRangeSnapshots = null;
            m_ActiveRangeReport = "";
            m_ActiveRangeExportPath = "";
            ClearFirstSelectionNotice();
            ClearCompletionNotice();
            m_FastEMA.Length = FastEmaLength;
            m_FastEMA.Price = Bars.Close;
            m_SlowEMA.Length = SlowEmaLength;
            m_SlowEMA.Price = Bars.Close;
            m_TrendEMA.Length = TrendEmaLength;
            m_TrendEMA.Price = Bars.Close;
        }

        protected override void CalcBar()
        {
            // The visual bounce indicators decide only when a range bar has
            // completed. Recording forming-bar values here could associate a
            // later click with a partial state that never produced a signal.
            if (Bars.Status != EBarState.Close ||
                Bars.CurrentBar < SlopeBars + SlopeLookbackBars) return;
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;
            string report = BuildReport(tickSize);
            m_SnapshotsByTime[Bars.Time[0]] = BuildSnapshot(tickSize, report);
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            if (arg.buttons != MouseButtons.Left || !IsAltHeld(arg.keys) ||
                !IsDHeld(arg.keys))
                return;

            DiagnosticSnapshot snapshot;
            if (!m_SnapshotsByTime.TryGetValue(arg.point.Time, out snapshot))
            {
                ShowDiagnosticWindow("No diagnostic was captured for this bar. " +
                    "Click a completed bar after the chart has recalculated.",
                    "Range Bar Diagnostic");
                return;
            }

            if (m_FirstSelectionTime == DateTime.MinValue)
            {
                m_FirstSelectionTime = snapshot.Time;
                ClearActiveRange();
                ClearCompletionNotice();
                ShowFirstSelectionNotice(snapshot.Time, arg.point.Price);
                return;
            }

            if (m_SecondSelectionTime == DateTime.MinValue)
            {
                m_SecondSelectionTime = snapshot.Time;
                ClearFirstSelectionNotice();
                ShowCompletionNotice(snapshot.Time, arg.point.Price);
                string exportPath;
                string rangeReport = BuildRangeReport(m_FirstSelectionTime,
                                                       m_SecondSelectionTime,
                                                       out exportPath);
                // The range is complete. Keep its report available for notes,
                // but reset click-selection state so the next Alt+D click can
                // immediately begin a fresh range.
                m_FirstSelectionTime = DateTime.MinValue;
                m_SecondSelectionTime = DateTime.MinValue;
                ShowDiagnosticWindow(rangeReport,
                    "Range Bar Diagnostic: Selected Range");
                return;
            }
        }

        private bool IsDHeld(Keys eventKeys)
        {
            if ((eventKeys & Keys.KeyCode) == Keys.D) return true;
            try { return (GetAsyncKeyState((int)Keys.D) & 0x8000) != 0; }
            catch { return false; }
        }

        private bool IsAltHeld(Keys eventKeys)
        {
            if ((eventKeys & Keys.Alt) == Keys.Alt) return true;
            try { return (GetAsyncKeyState((int)Keys.Menu) & 0x8000) != 0; }
            catch { return false; }
        }

        private string BuildReport(double tickSize)
        {
            StringBuilder text = new StringBuilder();
            int direction = GetUnarmedPBDisplayDirection(tickSize);
            double fastSlope = GetAngle(m_FastEMA[0], m_FastEMA[SlopeBars],
                                        SlopeBars, tickSize);
            double slowSlope = GetAngle(m_SlowEMA[0], m_SlowEMA[SlopeBars],
                                        SlopeBars, tickSize);
            double trendSlope = GetAngle(m_TrendEMA[0], m_TrendEMA[SlopeBars],
                                         SlopeBars, tickSize);
            double separation = Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) / tickSize;

            text.AppendLine("RANGE BAR DIAGNOSTIC");
            text.AppendFormat("OHLC: {0:F2} / {1:F2} / {2:F2} / {3:F2} [{4}]\n",
                Bars.Open[0], Bars.High[0], Bars.Low[0], Bars.Close[0],
                BodyName());
            text.AppendFormat("8 / 24 / 50 EMA: {0:F2} / {1:F2} / {2:F2} | context: {3}\n",
                m_FastEMA[0], m_SlowEMA[0], m_TrendEMA[0], DirectionName(direction));
            text.AppendFormat("8/24 separation: {0:F2}t | slopes 8/24/50: {1:F1}° / " +
                "{2:F1}° / {3:F1}°\n\n", separation, fastSlope, slowSlope,
                trendSlope);

            AppendPBReport(text, direction, separation, fastSlope, tickSize);
            text.AppendLine();
            AppendEma8Report(text, direction, separation, tickSize);
            text.AppendLine();
            AppendEma24Report(text, direction, separation, tickSize);
            text.AppendLine();
            AppendEma50Report(text, tickSize);
            text.AppendLine();
            AppendEma50BounceReport(text, tickSize);
            text.AppendLine();
            AppendTradeProfileReport(text, tickSize);
            text.AppendLine();
            text.AppendLine("Note: this indicator diagnoses completed-bar rules only. " +
                            "Live order arming, chart-side direction, and an active " +
                            "virtual-trade lock are strategy/display state, not bar filters.");
            return text.ToString();
        }

        private DiagnosticSnapshot BuildSnapshot(double tickSize, string report)
        {
            DiagnosticSnapshot result = new DiagnosticSnapshot();
            result.Time = Bars.Time[0];
            result.Open = Bars.Open[0]; result.High = Bars.High[0];
            result.Low = Bars.Low[0]; result.Close = Bars.Close[0];
            result.FastEma = m_FastEMA[0]; result.SlowEma = m_SlowEMA[0];
            result.TrendEma = m_TrendEMA[0];
            result.FastSlope = GetAngle(m_FastEMA[0], m_FastEMA[SlopeBars],
                                        SlopeBars, tickSize);
            result.SlowSlope = GetAngle(m_SlowEMA[0], m_SlowEMA[SlopeBars],
                                        SlopeBars, tickSize);
            result.TrendSlope = GetAngle(m_TrendEMA[0], m_TrendEMA[SlopeBars],
                                         SlopeBars, tickSize);
            result.Separation824 = Math.Abs(result.FastEma - result.SlowEma) /
                                    tickSize;
            result.Separation850 = (result.FastEma - result.TrendEma) / tickSize;
            result.Separation2450 = (result.SlowEma - result.TrendEma) / tickSize;
            result.BullishOrder = result.FastEma > result.SlowEma &&
                                  result.SlowEma > result.TrendEma;
            result.BearishOrder = result.FastEma < result.SlowEma &&
                                  result.SlowEma < result.TrendEma;
            result.Report = report;

            int tail, body;
            result.CompactShortPB = IsCompactPBContinuationValid(-1, tickSize);
            result.CompactLongPB = IsCompactPBContinuationValid(1, tickSize);

            int direction = GetUnarmedPBDisplayDirection(tickSize);
            bool normalShape = TryGetPBShape(direction, tickSize, out tail, out body);
            bool twoPriorCloses = HasTwoPriorPBTrendCloses(direction);
            bool priorDirection = HasPriorPBTrendDirection(direction);
            bool directionalFastSlope = HasDirectionalFastEmaSlopeForThreeBars(direction);
            bool tailOnFastSide = IsCompletedPinBarTailOnFastEmaSide(direction);
            bool emaOrder = IsPB1EmaOrderValid(direction);
            bool trendFilter = IsPB1TrendFilterValid(direction, tickSize);
            bool openSide = direction > 0 ? Bars.Open[0] > result.SlowEma :
                            direction < 0 && Bars.Open[0] < result.SlowEma;
            bool leadIn = HasPBLeadInStructure(direction);
            bool strongContinuation = IsStrongTrendContinuationValid(direction, tickSize);
            // PB1/PB0 are evaluated only by the compact path above.  The
            // regular PB path is PB2+ and follows RangePBBounce exactly.
            result.GeneralPB = normalShape &&
                               body > GetPB1BodyTicks(tickSize) && twoPriorCloses &&
                               priorDirection && directionalFastSlope &&
                               tailOnFastSide && emaOrder && trendFilter &&
                               (leadIn || strongContinuation) && openSide;
            result.GeneralPBDetail = string.Format(
                "shape PB2+ {0}; two prior closes {1}; prior direction {2}; " +
                "8 slope sequence {3}; tail side {4}; order {5}; trend {6}; " +
                "lead-in {7}; strong continuation {8}; open side {9}",
                Pass(normalShape && body > GetPB1BodyTicks(tickSize)),
                Pass(twoPriorCloses),
                Pass(priorDirection), Pass(directionalFastSlope),
                Pass(tailOnFastSide), Pass(emaOrder), Pass(trendFilter),
                Pass(leadIn), Pass(strongContinuation), Pass(openSide));

            double bestFast = GetBestDirectionalSlope(m_FastEMA, direction, tickSize);
            double bestSlow = GetBestDirectionalSlope(m_SlowEMA, direction, tickSize);
            double lead = direction > 0 ? bestFast - bestSlow :
                          direction < 0 ? Math.Abs(bestFast) - Math.Abs(bestSlow) : 0;
            bool crossesFast = Bars.Low[0] <= result.FastEma && Bars.High[0] >= result.FastEma;
            double rawPenetration = direction > 0 ? (result.FastEma - Bars.Low[0]) / tickSize :
                                    direction < 0 ? (Bars.High[0] - result.FastEma) / tickSize : 0;
            double penetration = Math.Round(rawPenetration * 2.0) / 2.0;
            bool twoBarPullback = HasTwoBarCounterTrendEma8Pullback(direction);
            bool shallowTouch = crossesFast && rawPenetration >= 0 &&
                                rawPenetration < Ema8MinimumPenetrationTicks &&
                                twoBarPullback;
            bool fastSlope = direction > 0 ? bestFast >= Ema8MinimumFastSlopeDegrees :
                             direction < 0 && bestFast <= -Ema8MinimumFastSlopeDegrees;
            bool slowSlope = direction > 0 ? bestSlow >= Ema8MinimumSlowSlopeDegrees :
                             direction < 0 && bestSlow <= -Ema8MinimumSlowSlopeDegrees;
            bool closeFastSide = direction > 0 ? Bars.Close[0] >= result.FastEma :
                                 direction < 0 && Bars.Close[0] <= result.FastEma;
            bool color = direction > 0 ? Bars.Close[0] > Bars.Open[0] :
                         direction < 0 && Bars.Close[0] <= Bars.Open[0];
            result.Ema8Bounce = direction != 0 &&
                result.Separation824 >= Ema8MinimumSeparationTicks && fastSlope && slowSlope &&
                crossesFast &&
                ((penetration >= Ema8MinimumPenetrationTicks &&
                  penetration <= Ema8MaximumPenetrationTicks) || shallowTouch) &&
                twoBarPullback &&
                closeFastSide && color &&
                GetLocalDisplacement(direction, tickSize) >= 1.0;

            double bestSeparation = GetBestDirectionalSeparation(direction, tickSize);
            double slowGap = result.SlowEma < Bars.Low[0]
                ? (Bars.Low[0] - result.SlowEma) / tickSize
                : result.SlowEma > Bars.High[0]
                    ? (result.SlowEma - Bars.High[0]) / tickSize : 0;
            bool closeSlowSide = direction > 0 ? Bars.Close[0] > result.SlowEma :
                                 direction < 0 && Bars.Close[0] < result.SlowEma;
            result.Ema24Bounce = direction != 0 &&
                result.Separation824 >= Ema24MinimumCurrentSeparationTicks &&
                bestSeparation >= Ema24MinimumBestSeparationTicks &&
                (direction > 0 ? bestSlow >= Ema24MinimumSlowSlopeDegrees :
                 bestSlow <= -Ema24MinimumSlowSlopeDegrees) &&
                slowGap <= Ema24TouchToleranceTicks && closeSlowSide && color;
            result.Ema50Bounce = IsFiftyEmaBounce(tickSize);
            return result;
        }

        private string BuildRangeReport(DateTime firstTime, DateTime secondTime,
                                        out string exportPath)
        {
            DateTime start = firstTime <= secondTime ? firstTime : secondTime;
            DateTime end = firstTime <= secondTime ? secondTime : firstTime;
            List<DiagnosticSnapshot> snapshots = new List<DiagnosticSnapshot>();
            foreach (KeyValuePair<DateTime, DiagnosticSnapshot> entry in m_SnapshotsByTime)
            {
                if (entry.Key >= start && entry.Key <= end)
                    snapshots.Add(entry.Value);
            }
            snapshots.Sort(delegate(DiagnosticSnapshot left, DiagnosticSnapshot right) {
                return left.Time.CompareTo(right.Time);
            });

            StringBuilder text = new StringBuilder();
            text.AppendLine("RANGE BAR DIAGNOSTIC: SELECTED RANGE");
            text.AppendFormat("Start: {0} | End: {1} | Bars: {2}\n\n",
                start.ToString("yyyy-MM-dd HH:mm:ss.fff"), end.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                snapshots.Count);
            if (snapshots.Count == 0)
            {
                exportPath = "";
                text.AppendLine("No completed diagnostic bars were found in this selection.");
                return text.ToString();
            }

            int bullish = 0, bearish = 0, compactShort = 0, compactLong = 0;
            int generalPB = 0, ema8 = 0, ema24 = 0, ema50 = 0;
            foreach (DiagnosticSnapshot snapshot in snapshots)
            {
                if (snapshot.BullishOrder) bullish++;
                if (snapshot.BearishOrder) bearish++;
                if (snapshot.CompactShortPB) compactShort++;
                if (snapshot.CompactLongPB) compactLong++;
                if (snapshot.GeneralPB) generalPB++;
                if (snapshot.Ema8Bounce) ema8++;
                if (snapshot.Ema24Bounce) ema24++;
                if (snapshot.Ema50Bounce) ema50++;
            }
            text.AppendLine("EMA PROFILE SUMMARY");
            text.AppendFormat("8 slope min/avg/max: {0}\n", FormatRange(snapshots, 0));
            text.AppendFormat("24 slope min/avg/max: {0}\n", FormatRange(snapshots, 1));
            text.AppendFormat("50 slope min/avg/max: {0}\n", FormatRange(snapshots, 2));
            text.AppendFormat("8/24 separation min/avg/max: {0}\n", FormatRange(snapshots, 3));
            text.AppendFormat("8/50 separation min/avg/max: {0}\n", FormatRange(snapshots, 4));
            text.AppendFormat("24/50 separation min/avg/max: {0}\n", FormatRange(snapshots, 5));
            text.AppendFormat("8>24>50: {0}/{1} | 8<24<50: {2}/{1}\n", bullish,
                snapshots.Count, bearish);
            text.AppendFormat("Signals: compact long PB {0}, compact short PB {1}, " +
                "general PB {2}, 8 EMA {3}, 24 EMA {4}, 50 EMA {5}\n\n", compactLong,
                compactShort, generalPB, ema8, ema24, ema50);
            text.AppendLine("PER-BAR RESULTS");
            foreach (DiagnosticSnapshot snapshot in snapshots)
            {
                text.AppendFormat("{0}: OHLC {1:F2}/{2:F2}/{3:F2}/{4:F2} | " +
                    "8/24/50 {5:F2}/{6:F2}/{7:F2} | slopes {8:F1}/{9:F1}/{10:F1} | " +
                    "PB L/S/G {11}/{12}/{13} | 8E/24E/50E {14}/{15}/{16}\n",
                    snapshot.Time.ToString("HH:mm:ss.fff"), snapshot.Open, snapshot.High,
                    snapshot.Low, snapshot.Close, snapshot.FastEma, snapshot.SlowEma,
                    snapshot.TrendEma, snapshot.FastSlope, snapshot.SlowSlope,
                    snapshot.TrendSlope, Pass(snapshot.CompactLongPB),
                    Pass(snapshot.CompactShortPB), Pass(snapshot.GeneralPB),
                    Pass(snapshot.Ema8Bounce), Pass(snapshot.Ema24Bounce),
                    Pass(snapshot.Ema50Bounce));
                text.AppendFormat("  General PB gates: {0}\n", snapshot.GeneralPBDetail);
                // Keep the exact completed-bar decisions produced for this
                // snapshot. This lets a marked signal be compared directly
                // with its 8/24/50-EMA gate values rather than only a summary.
                text.AppendLine("  Exact completed-bar diagnostic:");
                text.Append(snapshot.Report);
                text.AppendLine();
            }

            m_ActiveRangeSnapshots = snapshots;
            m_ActiveRangeReport = text.ToString();
            m_ActiveRangeExportPath = ExportRangeReport(snapshots,
                m_NotesText == null ? "" : m_NotesText.Text, "");
            exportPath = m_ActiveRangeExportPath;
            text.AppendLine();
            text.AppendLine(exportPath.Length > 0 ? "Report exported: " + exportPath :
                "Report export failed; see the diagnostic output log for the error.");
            text.AppendLine("Selection cleared automatically. Alt+D-click a new first bar to begin another range.");
            return text.ToString();
        }

        private void AppendPBReport(StringBuilder text, int direction,
                                    double separation, double fastSlope,
                                    double tickSize)
        {
            int shortTail, shortBody;
            bool shortShape = TryGetPBShape(-1, tickSize, out shortTail,
                                            out shortBody);
            bool compactShort = shortShape && shortBody <= GetPB1BodyTicks(tickSize);
            bool compactPass = IsCompactPBContinuationValid(-1, tickSize);
            bool compactShortPriorPass =
                HasCompactPBPriorTrendConfirmation(-1, tickSize);

            text.AppendLine("PB BOUNCE");
            text.AppendFormat("Short shape: tail {0}t / body {1}t [{2}]\n",
                shortTail, shortBody, shortShape ? "VALID PB" : "NOT A PB SHAPE");
            text.AppendFormat("Compact short PB1/PB0: shape [{0}], 8<24 + persistent 24 down, " +
                "8/24 >= 3t, 8 slope/sequence down, close below prior 3, tail <= 8\n",
                Pass(compactShort));
            text.AppendFormat("Prior confirmation: down-close or directional PB0 [{0}]\n",
                Pass(compactShortPriorPass));
            text.AppendFormat("Compact PB1/PB0 RESULT: [{0}]\n", Pass(compactPass));

            int longTail, longBody;
            bool longShape = TryGetPBShape(1, tickSize, out longTail, out longBody);
            bool compactLong = longShape && longBody <= GetPB1BodyTicks(tickSize);
            bool compactLongPass = IsCompactPBContinuationValid(1, tickSize);
            bool compactLongPriorPass =
                HasCompactPBPriorTrendConfirmation(1, tickSize);
            text.AppendFormat("Compact long PB1/PB0: shape [{0}], 8>24 + persistent 24 up, " +
                "8/24 >= 3t, 8 slope/sequence up, close above prior 3, tail >= 8\n",
                Pass(compactLong));
            text.AppendFormat("Prior confirmation: up-close or directional PB0 [{0}]\n",
                Pass(compactLongPriorPass));
            text.AppendFormat("Compact long PB1/PB0 RESULT: [{0}]\n",
                Pass(compactLongPass));

            int tail, body;
            bool normalShape = TryGetPBShape(direction, tickSize, out tail, out body);
            bool pb2PlusShape = normalShape &&
                                body > GetPB1BodyTicks(tickSize);
            bool twoPriorCloses = HasTwoPriorPBTrendCloses(direction);
            bool priorDirection = HasPriorPBTrendDirection(direction);
            bool fastSequence = HasDirectionalFastEmaSlopeForThreeBars(direction);
            bool tailSide = IsCompletedPinBarTailOnFastEmaSide(direction);
            bool emaOrderPass = IsPB1EmaOrderValid(direction);
            bool trendFilterPass = IsPB1TrendFilterValid(direction, tickSize);
            bool leadInPass = HasPBLeadInStructure(direction);
            bool strongContinuationPass = IsStrongTrendContinuationValid(direction,
                                                                           tickSize);
            bool openSidePass = direction > 0 ? Bars.Open[0] > m_SlowEMA[0] :
                                direction < 0 && Bars.Open[0] < m_SlowEMA[0];
            bool normalPass = pb2PlusShape && twoPriorCloses && priorDirection &&
                              fastSequence && tailSide && emaOrderPass &&
                              trendFilterPass &&
                              (leadInPass || strongContinuationPass) && openSidePass;
            text.AppendFormat("Regular PB2+ ({0}): shape tail/body {1}/{2} [{3}], " +
                "two prior closes [{4}], prior direction [{5}], 8 sequence [{6}]\n",
                DirectionName(direction), tail, body, Pass(pb2PlusShape),
                Pass(twoPriorCloses), Pass(priorDirection), Pass(fastSequence));
            text.AppendFormat("Tail side [{0}], 8/24 order [{1}], 3t + 8-slope trend " +
                "filter [{2}], lead-in OR strong [{3}], open side [{4}]\n",
                Pass(tailSide), Pass(emaOrderPass), Pass(trendFilterPass),
                Pass(leadInPass || strongContinuationPass), Pass(openSidePass));
            text.AppendFormat("Regular PB2+ RESULT: [{0}]\n", Pass(normalPass));
        }

        private void AppendEma8Report(StringBuilder text, int direction,
                                      double separation, double tickSize)
        {
            double bestFast = GetBestDirectionalSlope(m_FastEMA, direction, tickSize);
            double bestSlow = GetBestDirectionalSlope(m_SlowEMA, direction, tickSize);
            double lead = direction > 0 ? bestFast - bestSlow :
                          direction < 0 ? Math.Abs(bestFast) - Math.Abs(bestSlow) : 0;
            bool separationPass = direction != 0 &&
                                  separation >= Ema8MinimumSeparationTicks;
            bool fastPass = direction > 0 ? bestFast >= Ema8MinimumFastSlopeDegrees :
                            direction < 0 && bestFast <= -Ema8MinimumFastSlopeDegrees;
            bool slowPass = direction > 0 ? bestSlow >= Ema8MinimumSlowSlopeDegrees :
                            direction < 0 && bestSlow <= -Ema8MinimumSlowSlopeDegrees;
            bool leadPass = true;
            bool crosses = Bars.Low[0] <= m_FastEMA[0] && Bars.High[0] >= m_FastEMA[0];
            double penetration = direction > 0
                ? (m_FastEMA[0] - Bars.Low[0]) / tickSize
                : direction < 0 ? (Bars.High[0] - m_FastEMA[0]) / tickSize : 0;
            double comparablePenetration = Math.Round(penetration * 2.0) / 2.0;
            bool penetrationPass = crosses &&
                comparablePenetration >= Ema8MinimumPenetrationTicks &&
                comparablePenetration <= Ema8MaximumPenetrationTicks;
            bool twoBarPullback = HasTwoBarCounterTrendEma8Pullback(direction);
            bool shallowTouchPass = crosses && penetration >= 0 &&
                                    penetration < Ema8MinimumPenetrationTicks &&
                                    twoBarPullback;
            penetrationPass = penetrationPass || shallowTouchPass;
            bool closeSidePass = direction > 0 ? Bars.Close[0] >= m_FastEMA[0] :
                                 direction < 0 && Bars.Close[0] <= m_FastEMA[0];
            bool colorPass = direction > 0 ? Bars.Close[0] > Bars.Open[0] :
                             direction < 0 && Bars.Close[0] <= Bars.Open[0];
            double displacement = GetLocalDisplacement(direction, tickSize);
            bool displacementPass = displacement >= 1.0;
            bool result = separationPass && fastPass && slowPass && leadPass &&
                          twoBarPullback &&
                          penetrationPass && closeSidePass && colorPass &&
                          displacementPass;
            text.AppendLine("8 EMA BOUNCE");
            text.AppendFormat("Best slopes 8/24: {0:F1}° / {1:F1}° | lead {2:F1}°\n",
                bestFast, bestSlow, lead);
            text.AppendFormat("Separation >= 4.5t [{0}], 8 slope >= 40° [{1}], " +
                "24 slope >= 40° [{2}], slope lead informational [{3}]\n", Pass(separationPass),
                Pass(fastPass), Pass(slowPass), Pass(leadPass));
            text.AppendFormat("Crosses 8 EMA [{0}], penetration {1:F2}t (1-4.5) [{2}], " +
                "close side [{3}], color [{4}], displacement {5:F2}t [{6}]\n",
                Pass(crosses), comparablePenetration, Pass(penetrationPass),
                Pass(closeSidePass), Pass(colorPass), displacement, Pass(displacementPass));
            text.AppendFormat("Two countertrend bars REQUIRED [{0}], shallow touch (<1t) [{1}]\n",
                Pass(twoBarPullback), Pass(shallowTouchPass));
            text.AppendFormat("8 EMA BOUNCE RESULT: [{0}]\n", Pass(result));
        }

        private void AppendEma24Report(StringBuilder text, int direction,
                                       double separation, double tickSize)
        {
            double bestSeparation = GetBestDirectionalSeparation(direction, tickSize);
            double bestSlow = GetBestDirectionalSlope(m_SlowEMA, direction, tickSize);
            bool currentSepPass = direction != 0 &&
                                  separation >= Ema24MinimumCurrentSeparationTicks;
            bool bestSepPass = bestSeparation >= Ema24MinimumBestSeparationTicks;
            bool slopePass = direction > 0 ? bestSlow >= Ema24MinimumSlowSlopeDegrees :
                             direction < 0 && bestSlow <= -Ema24MinimumSlowSlopeDegrees;
            double gap = m_SlowEMA[0] < Bars.Low[0]
                ? (Bars.Low[0] - m_SlowEMA[0]) / tickSize
                : m_SlowEMA[0] > Bars.High[0]
                    ? (m_SlowEMA[0] - Bars.High[0]) / tickSize : 0;
            bool touchPass = gap <= Ema24TouchToleranceTicks;
            bool closePass = direction > 0 ? Bars.Close[0] > m_SlowEMA[0] :
                             direction < 0 && Bars.Close[0] < m_SlowEMA[0];
            bool colorPass = direction > 0 ? Bars.Close[0] > Bars.Open[0] :
                             direction < 0 && Bars.Close[0] <= Bars.Open[0];
            bool result = currentSepPass && bestSepPass && slopePass && touchPass &&
                          closePass && colorPass;
            text.AppendLine("24 EMA BOUNCE");
            text.AppendFormat("Current separation {0:F2}t >= 1.5 [{1}], best {2:F2}t >= 2 [{3}], " +
                "best 24 slope {4:F1}° >= 20° [{5}]\n", separation,
                Pass(currentSepPass), bestSeparation, Pass(bestSepPass), bestSlow,
                Pass(slopePass));
            text.AppendFormat("24 EMA gap {0:F2}t <= 0.25 [{1}], close side [{2}], color [{3}]\n",
                gap, Pass(touchPass), Pass(closePass), Pass(colorPass));
            text.AppendFormat("24 EMA BOUNCE RESULT: [{0}]\n", Pass(result));
        }

        private void AppendEma50Report(StringBuilder text, double tickSize)
        {
            double slope = GetAngle(m_TrendEMA[0], m_TrendEMA[SlopeBars],
                                    SlopeBars, tickSize);
            double fastSeparation = (m_FastEMA[0] - m_TrendEMA[0]) / tickSize;
            double slowSeparation = (m_SlowEMA[0] - m_TrendEMA[0]) / tickSize;
            string barLocation = Bars.Low[0] > m_TrendEMA[0] ? "ABOVE" :
                                 Bars.High[0] < m_TrendEMA[0] ? "BELOW" :
                                 "CROSSES";
            text.AppendLine("50 EMA CONTEXT (informational)");
            text.AppendFormat("50 EMA: {0:F2} | 3-bar slope: {1:F1}°\n",
                m_TrendEMA[0], slope);
            text.AppendFormat("8 vs 50: {0:F2}t [{1}] | 24 vs 50: {2:F2}t [{3}]\n",
                fastSeparation, fastSeparation >= 0 ? "ABOVE" : "BELOW",
                slowSeparation, slowSeparation >= 0 ? "ABOVE" : "BELOW");
            text.AppendFormat("Clicked bar relative to 50 EMA: [{0}]\n", barLocation);
        }

        private void AppendEma50BounceReport(StringBuilder text, double tickSize)
        {
            int direction = GetFiftyBounceDirection();
            double gap = m_TrendEMA[0] < Bars.Low[0]
                ? (Bars.Low[0] - m_TrendEMA[0]) / tickSize
                : m_TrendEMA[0] > Bars.High[0]
                    ? (m_TrendEMA[0] - Bars.High[0]) / tickSize : 0;
            bool touchPass = direction != 0 && gap <= Ema50TouchToleranceTicks;
            bool closeColorPass = direction > 0
                ? Bars.Close[0] > Bars.Open[0] && Bars.Close[0] > m_TrendEMA[0]
                : direction < 0 && Bars.Close[0] < Bars.Open[0] &&
                  Bars.Close[0] < m_TrendEMA[0];
            double slowTrendGap = Math.Abs(m_SlowEMA[0] - m_TrendEMA[0]) / tickSize;
            bool slowTrendSeparationPass = slowTrendGap >=
                                           Ema50MinimumSlowTrendSeparationTicks;
            bool priorTrendPass = HasPriorDirectionalTrendSlope(direction, tickSize);
            double bestPriorTrendSlope = GetBestPriorDirectionalSlope(m_TrendEMA,
                                                                        direction, tickSize);
            bool result = touchPass && closeColorPass && slowTrendSeparationPass &&
                          priorTrendPass;
            text.AppendLine("50 EMA BOUNCE");
            text.AppendFormat("Direction / current order: {0} [{1}] | 50 EMA gap {2:F2}t <= 0.75 [{3}]\n",
                DirectionName(direction), Pass(direction != 0), gap, Pass(touchPass));
            text.AppendFormat("Close back on 50-EMA trend side with color [{0}]\n",
                Pass(closeColorPass));
            text.AppendFormat("Current 24/50 separation {0:F2}t >= 3t [{1}]\n",
                slowTrendGap, Pass(slowTrendSeparationPass));
            text.AppendFormat("Best prior-six directional 50 slope {0:F1}° (threshold ±15°) [{1}]\n",
                bestPriorTrendSlope, Pass(priorTrendPass));
            text.AppendFormat("50 EMA BOUNCE RESULT: [{0}]\n", Pass(result));
        }

        // This is the shared 8/24/50 market-profile gate used by the PB, 8 EMA,
        // and 24 EMA displays and by the live strategy.  Keeping it here makes a
        // setup/filter disagreement visible in the same diagnostic report.
        private void AppendTradeProfileReport(StringBuilder text, double tickSize)
        {
            text.AppendLine("TRADE PROFILE GATE (PB / 8 EMA / 24 EMA)");

            int direction = GetProfileOrderDirectionAt(0);
            string directionName = direction > 0 ? "BULLISH (8 > 24 > 50)" :
                                   direction < 0 ? "BEARISH (8 < 24 < 50)" :
                                   "NOT ORDERED";
            text.AppendFormat("Current EMA order: {0} [{1}]\n", directionName,
                Pass(direction != 0));
            if (direction == 0)
            {
                text.AppendLine("Required current-bar profile: [FAIL]");
                return;
            }

            bool currentProfilePass = HasTradeProfileDirectionAt(direction, 0, tickSize);
            bool persistencePass = true;
            StringBuilder bars = new StringBuilder();
            for (int barsBack = 0; barsBack < ProfilePersistenceBars; barsBack++)
            {
                bool barPass = HasTradeProfileDirectionAt(direction, barsBack, tickSize);
                persistencePass = persistencePass && barPass;
                if (barsBack > 0) bars.Append(", ");
                bars.AppendFormat("-{0} [{1}]", barsBack, Pass(barPass));
            }
            int oldestBar = ProfilePersistenceBars - 1;
            double currentFastSlow = Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) / tickSize;
            double currentSlowTrend = Math.Abs(m_SlowEMA[0] - m_TrendEMA[0]) / tickSize;
            double oldFastSlow = Math.Abs(m_FastEMA[oldestBar] -
                                          m_SlowEMA[oldestBar]) / tickSize;
            double oldSlowTrend = Math.Abs(m_SlowEMA[oldestBar] -
                                           m_TrendEMA[oldestBar]) / tickSize;
            bool compressionPass = currentFastSlow + ProfileMaximumCompressionTicks >= oldFastSlow &&
                                   currentSlowTrend + ProfileMaximumCompressionTicks >= oldSlowTrend;
            text.AppendFormat("Required current-bar order / spread / slope profile [{0}]\n",
                Pass(currentProfilePass));
            text.AppendFormat("Recent four-bar history (informational): {0} [{1}]\n",
                bars, Pass(persistencePass));
            text.AppendFormat("8/24 spread now {0:F2}t vs -3 {1:F2}t; 24/50 now {2:F2}t vs -3 {3:F2}t\n",
                currentFastSlow, oldFastSlow, currentSlowTrend, oldSlowTrend);
            text.AppendFormat("Compression context (informational), allowance {0:F1}t [{1}]\n",
                ProfileMaximumCompressionTicks, Pass(compressionPass));
            text.AppendFormat("TRADE PROFILE RESULT: [{0}]\n",
                Pass(currentProfilePass));
        }

        private int GetProfileOrderDirectionAt(int barsBack)
        {
            double fast = m_FastEMA[barsBack];
            double slow = m_SlowEMA[barsBack];
            double trend = m_TrendEMA[barsBack];
            return fast > slow && slow > trend ? 1 :
                   fast < slow && slow < trend ? -1 : 0;
        }

        private bool HasTradeProfileDirectionAt(int direction, int barsBack,
                                                double tickSize)
        {
            if (GetProfileOrderDirectionAt(barsBack) != direction) return false;
            double fast = m_FastEMA[barsBack];
            double slow = m_SlowEMA[barsBack];
            double trend = m_TrendEMA[barsBack];
            double fastSlow = Math.Abs(fast - slow) / tickSize;
            double slowTrend = Math.Abs(slow - trend) / tickSize;
            double fastTrend = Math.Abs(fast - trend) / tickSize;
            if (fastSlow < ProfileMinFastSlowSeparationTicks ||
                slowTrend < ProfileMinSlowTrendSeparationTicks ||
                fastTrend < ProfileMinFastTrendSeparationTicks)
                return false;

            double fastSlope = GetAngle(m_FastEMA[barsBack],
                m_FastEMA[barsBack + SlopeBars], SlopeBars, tickSize);
            double slowSlope = GetBestPriorDirectionalSlope(m_SlowEMA, direction,
                                                             tickSize);
            double trendSlope = GetAngle(m_TrendEMA[barsBack],
                m_TrendEMA[barsBack + SlopeBars], SlopeBars, tickSize);
            return direction > 0
                ? fastSlope >= ProfileMinFastSlopeDegrees &&
                  slowSlope >= ProfileMinSlowSlopeDegrees &&
                  trendSlope >= ProfileMinTrendSlopeDegrees
                : fastSlope <= -ProfileMinFastSlopeDegrees &&
                  slowSlope <= -ProfileMinSlowSlopeDegrees &&
                  trendSlope <= -ProfileMinTrendSlopeDegrees;
        }

        private bool Has24EmaTradeProfileDirectionAt(int direction, int barsBack,
                                                      double tickSize)
        {
            if (GetProfileOrderDirectionAt(barsBack) != direction) return false;
            double fast = m_FastEMA[barsBack];
            double slow = m_SlowEMA[barsBack];
            double trend = m_TrendEMA[barsBack];
            if (Math.Abs(fast - slow) / tickSize < ProfileMinFastSlowSeparationTicks ||
                Math.Abs(slow - trend) / tickSize < ProfileMinSlowTrendSeparationTicks ||
                Math.Abs(fast - trend) / tickSize < ProfileMinFastTrendSeparationTicks)
                return false;
            double slowSlope = GetAngle(m_SlowEMA[barsBack],
                m_SlowEMA[barsBack + SlopeBars], SlopeBars, tickSize);
            double trendSlope = GetAngle(m_TrendEMA[barsBack],
                m_TrendEMA[barsBack + SlopeBars], SlopeBars, tickSize);
            return direction > 0
                ? slowSlope >= ProfileMinSlowSlopeDegrees &&
                  trendSlope >= ProfileMinTrendSlopeDegrees
                : slowSlope <= -ProfileMinSlowSlopeDegrees &&
                  trendSlope <= -ProfileMinTrendSlopeDegrees;
        }

        private int GetFiftyBounceDirection()
        {
            return m_FastEMA[0] > m_SlowEMA[0] && m_SlowEMA[0] > m_TrendEMA[0]
                ? 1
                : m_FastEMA[0] < m_SlowEMA[0] && m_SlowEMA[0] < m_TrendEMA[0]
                    ? -1 : 0;
        }

        private bool IsFiftyEmaBounce(double tickSize)
        {
            int direction = GetFiftyBounceDirection();
            if (direction == 0) return false;
            double gap = m_TrendEMA[0] < Bars.Low[0]
                ? (Bars.Low[0] - m_TrendEMA[0]) / tickSize
                : m_TrendEMA[0] > Bars.High[0]
                    ? (m_TrendEMA[0] - Bars.High[0]) / tickSize : 0;
            bool closeAndColor = direction > 0
                ? Bars.Close[0] > Bars.Open[0] && Bars.Close[0] > m_TrendEMA[0]
                : Bars.Close[0] < Bars.Open[0] && Bars.Close[0] < m_TrendEMA[0];
            bool slowTrendSeparation = Math.Abs(m_SlowEMA[0] - m_TrendEMA[0]) /
                                       tickSize >= Ema50MinimumSlowTrendSeparationTicks;
            return gap <= Ema50TouchToleranceTicks && closeAndColor &&
                   slowTrendSeparation &&
                   HasPriorDirectionalTrendSlope(direction, tickSize);
        }

        private bool HasPriorDirectionalTrendSlope(int direction, double tickSize)
        {
            if (direction == 0) return false;
            for (int barsBack = 1; barsBack <= SlopeLookbackBars; barsBack++)
            {
                double trendSlope = GetAngle(m_TrendEMA[barsBack],
                    m_TrendEMA[barsBack + SlopeBars], SlopeBars, tickSize);
                if (direction > 0 && trendSlope >= Ema50MinimumPriorTrendSlopeDegrees)
                    return true;
                if (direction < 0 && trendSlope <= -Ema50MinimumPriorTrendSlopeDegrees)
                    return true;
            }
            return false;
        }

        private double GetBestPriorDirectionalSlope(XAverage ema, int direction,
                                                     double tickSize)
        {
            if (direction == 0) return 0;
            double best = direction > 0 ? Double.NegativeInfinity : Double.PositiveInfinity;
            for (int barsBack = 1; barsBack <= SlopeLookbackBars; barsBack++)
            {
                double angle = GetAngle(ema[barsBack], ema[barsBack + SlopeBars],
                                        SlopeBars, tickSize);
                best = direction > 0 ? Math.Max(best, angle) : Math.Min(best, angle);
            }
            return best;
        }

        private bool IsStrongTrendContinuationValid(int direction, double tickSize)
        {
            if (direction == 0 || !HasTradeProfileDirectionAt(direction, 0, tickSize))
                return false;
            double fastSlope = GetAngle(m_FastEMA[0], m_FastEMA[SlopeBars],
                                        SlopeBars, tickSize);
            double slowSlope = GetAngle(m_SlowEMA[0], m_SlowEMA[SlopeBars],
                                        SlopeBars, tickSize);
            double trendSlope = GetAngle(m_TrendEMA[0], m_TrendEMA[SlopeBars],
                                         SlopeBars, tickSize);
            double separation = Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) / tickSize;
            return direction > 0
                ? Bars.Close[1] > Bars.Open[1] && Bars.Low[0] >= m_FastEMA[0] &&
                  separation >= StrongContinuationSeparationTicks &&
                  fastSlope >= StrongContinuationFastSlopeDegrees &&
                  slowSlope >= StrongContinuationSlowSlopeDegrees &&
                  trendSlope >= StrongContinuationTrendSlopeDegrees
                : Bars.Close[1] < Bars.Open[1] && Bars.High[0] <= m_FastEMA[0] &&
                  separation >= StrongContinuationSeparationTicks &&
                  fastSlope <= -StrongContinuationFastSlopeDegrees &&
                  slowSlope <= -StrongContinuationSlowSlopeDegrees &&
                  trendSlope <= -StrongContinuationTrendSlopeDegrees;
        }

        private bool TryGetPBShape(int direction, double tickSize, out int tail,
                                   out int body)
        {
            tail = body = 0;
            double tolerance = tickSize * 0.1;
            double open = RoundToTick(Bars.Open[0], tickSize);
            double high = RoundToTick(Bars.High[0], tickSize);
            double low = RoundToTick(Bars.Low[0], tickSize);
            double close = RoundToTick(Bars.Close[0], tickSize);
            if (direction > 0)
            {
                tail = ToTicks(open - low, tickSize);
                body = ToTicks(high - open, tickSize);
                if (Math.Abs(close - high) > tolerance) return false;
            }
            else
            {
                tail = ToTicks(high - open, tickSize);
                body = ToTicks(open - low, tickSize);
                if (Math.Abs(close - low) > tolerance) return false;
            }
            int rangeTicks = GetPinBarRangeTicks(tickSize);
            return (body == 0 || body == GetPB1BodyTicks(tickSize) ||
                    body == GetPB2BodyTicks(tickSize)) &&
                   tail + body == rangeTicks;
        }

        private int GetPinBarRangeTicks(double tickSize)
        {
            return Math.Max(1, ToTicks(Bars.High[0] - Bars.Low[0], tickSize));
        }

        private int GetPB2BodyTicks(double tickSize)
        {
            return Math.Max(1, (int)Math.Round(GetPinBarRangeTicks(tickSize) *
                                                2.0 / BasePinBarRangeTicks));
        }

        private int GetPB1BodyTicks(double tickSize)
        {
            return Math.Max(1, (int)Math.Round(GetPinBarRangeTicks(tickSize) *
                                                1.0 / BasePinBarRangeTicks));
        }

        // Mirrors RangePBBounce's current PB routing, including the compact
        // PB1/PB0 three-close breakout rule.
        private int GetUnarmedPBDisplayDirection(double tickSize)
        {
            if (HasSharplyMovingFastEma(1, tickSize)) return 1;
            if (HasSharplyMovingFastEma(-1, tickSize)) return -1;
            return m_SlowEMA[0] >= m_SlowEMA[1] ? 1 : -1;
        }

        private bool IsCompactPBContinuationValid(int direction, double tickSize)
        {
            int tail, body;
            return TryGetPBShape(direction, tickSize, out tail, out body) &&
                   body <= GetPB1BodyTicks(tickSize) &&
                   HasDirectional824PBTrendContext(direction) &&
                   HasMinimumPBFastSlowSeparation(tickSize) &&
                   HasSharplyMovingFastEma(direction, tickSize) &&
                   HasDirectionalFastEmaSlopeForThreeBars(direction) &&
                   HasThreeBarPBCloseBreakout(direction) &&
                   HasCompactPBPriorTrendConfirmation(direction, tickSize) &&
                   IsCompletedPinBarTailOnFastEmaSide(direction);
        }

        private bool HasDirectional824PBTrendContext(int direction)
        {
            return direction > 0
                ? m_FastEMA[0] > m_SlowEMA[0] && HasPersistentPBSlowEmaDirection(1)
                : direction < 0 && m_FastEMA[0] < m_SlowEMA[0] &&
                  HasPersistentPBSlowEmaDirection(-1);
        }

        private bool HasPersistentPBSlowEmaDirection(int direction)
        {
            for (int barsBack = 0; barsBack < PBSlowEmaPersistenceBars; barsBack++)
            {
                if (direction > 0 && m_SlowEMA[barsBack] <= m_SlowEMA[barsBack + 1]) return false;
                if (direction < 0 && m_SlowEMA[barsBack] >= m_SlowEMA[barsBack + 1]) return false;
            }
            return true;
        }

        private bool HasMinimumPBFastSlowSeparation(double tickSize)
        {
            return Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) >=
                   PBMinimumSeparationTicks * tickSize;
        }

        private bool HasSharplyMovingFastEma(int direction, double tickSize)
        {
            double slope = GetAngle(m_FastEMA[0], m_FastEMA[SlopeBars], SlopeBars, tickSize);
            return direction > 0 ? slope >= PBMinimumFastSlopeDegrees :
                   direction < 0 && slope <= -PBMinimumFastSlopeDegrees;
        }

        private bool HasDirectionalFastEmaSlopeForThreeBars(int direction)
        {
            return direction > 0
                ? m_FastEMA[0] > m_FastEMA[1] && m_FastEMA[1] > m_FastEMA[2] &&
                  m_FastEMA[2] > m_FastEMA[3]
                : direction < 0 && m_FastEMA[0] < m_FastEMA[1] &&
                  m_FastEMA[1] < m_FastEMA[2] && m_FastEMA[2] < m_FastEMA[3];
        }

        private bool HasPriorPBTrendDirection(int direction)
        {
            return direction > 0 ? Bars.Close[1] > Bars.Open[1] :
                   direction < 0 && Bars.Close[1] < Bars.Open[1];
        }

        private bool HasCompactPBPriorTrendConfirmation(int direction,
                                                         double tickSize)
        {
            return HasPriorPBTrendDirection(direction) ||
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

        private bool HasTwoPriorPBTrendCloses(int direction)
        {
            for (int barsBack = 1; barsBack <= 2; barsBack++)
            {
                bool withTrend = direction > 0
                    ? Bars.Close[barsBack] > Bars.Open[barsBack]
                    : Bars.Close[barsBack] < Bars.Open[barsBack];
                if (!withTrend) return false;
            }
            return true;
        }

        private bool HasThreeBarPBCloseBreakout(int direction)
        {
            for (int barsBack = 1; barsBack <= 3; barsBack++)
            {
                if (direction > 0 && Bars.Close[0] <= Bars.Close[barsBack]) return false;
                if (direction < 0 && Bars.Close[0] >= Bars.Close[barsBack]) return false;
            }
            return true;
        }

        private bool IsCompletedPinBarTailOnFastEmaSide(int direction)
        {
            return direction > 0 ? Bars.Low[0] >= m_FastEMA[0] :
                   direction < 0 && Bars.High[0] <= m_FastEMA[0];
        }

        private bool IsPB1EmaOrderValid(int direction)
        {
            return direction > 0 ? m_FastEMA[0] > m_SlowEMA[0] :
                   direction < 0 && m_FastEMA[0] < m_SlowEMA[0];
        }

        private bool IsPB1TrendFilterValid(int direction, double tickSize)
        {
            return direction != 0 && HasMinimumPBFastSlowSeparation(tickSize) &&
                   HasSharplyMovingFastEma(direction, tickSize);
        }

        private bool HasPBLeadInStructure(int direction)
        {
            if (direction == 0) return false;
            for (int barsBack = 1; barsBack <= 2; barsBack++)
            {
                int priorBar = barsBack + 1;
                bool pass = direction > 0
                    ? Bars.Low[barsBack] >= Bars.Low[priorBar] &&
                      Bars.Close[barsBack] > Bars.Open[barsBack] &&
                      Bars.Close[barsBack] > Bars.Close[priorBar]
                    : Bars.High[barsBack] <= Bars.High[priorBar] &&
                      Bars.Close[barsBack] < Bars.Open[barsBack] &&
                      Bars.Close[barsBack] < Bars.Close[priorBar];
                if (!pass) return false;
            }
            return true;
        }

        // The shallow 8 EMA-bounce variation is valid only after two complete
        // bars pulled back against the established EMA direction.
        private bool HasTwoBarCounterTrendEma8Pullback(int direction)
        {
            return direction > 0
                ? Bars.Close[1] < Bars.Open[1] && Bars.Close[2] < Bars.Open[2]
                : direction < 0 && Bars.Close[1] > Bars.Open[1] &&
                  Bars.Close[2] > Bars.Open[2];
        }

        private bool HasRangeClearance(int direction, int lookbackBars)
        {
            if (direction == 0) return false;
            for (int barsBack = 1; barsBack <= lookbackBars; barsBack++)
            {
                if (direction > 0 && Bars.Close[0] <= Bars.High[barsBack]) return false;
                if (direction < 0 && Bars.Close[0] >= Bars.Low[barsBack]) return false;
            }
            return true;
        }

        private double GetBestDirectionalSlope(XAverage ema, int direction,
                                               double tickSize)
        {
            if (direction == 0) return 0;
            double best = direction > 0 ? Double.NegativeInfinity : Double.PositiveInfinity;
            for (int barsBack = 0; barsBack <= SlopeLookbackBars; barsBack++)
            {
                double angle = GetAngle(ema[barsBack], ema[barsBack + SlopeBars],
                                        SlopeBars, tickSize);
                best = direction > 0 ? Math.Max(best, angle) : Math.Min(best, angle);
            }
            return best;
        }

        private double GetBestDirectionalSeparation(int direction, double tickSize)
        {
            if (direction == 0) return 0;
            double best = 0;
            for (int barsBack = 0; barsBack <= SlopeLookbackBars; barsBack++)
            {
                double separation = direction > 0
                    ? m_FastEMA[barsBack] - m_SlowEMA[barsBack]
                    : m_SlowEMA[barsBack] - m_FastEMA[barsBack];
                best = Math.Max(best, separation / tickSize);
            }
            return best;
        }

        private double GetLocalDisplacement(int direction, double tickSize)
        {
            if (direction == 0) return 0;
            double reference = direction > 0
                ? Math.Min(Bars.Low[1], Bars.Low[2])
                : Math.Max(Bars.High[1], Bars.High[2]);
            return direction > 0 ? (reference - Bars.Low[0]) / tickSize :
                                   (Bars.High[0] - reference) / tickSize;
        }

        private int GetTrendDirection()
        {
            return m_FastEMA[0] > m_SlowEMA[0] ? 1 :
                   m_FastEMA[0] < m_SlowEMA[0] ? -1 : 0;
        }

        private string BodyName()
        {
            return Bars.Close[0] > Bars.Open[0] ? "BULLISH" :
                   Bars.Close[0] < Bars.Open[0] ? "BEARISH" : "ZERO-BODY";
        }

        private string DirectionName(int direction)
        {
            return direction > 0 ? "LONG" : direction < 0 ? "SHORT" : "FLAT";
        }

        private string Pass(bool value) { return value ? "PASS" : "FAIL"; }

        private int ToTicks(double distance, double tickSize)
        {
            return (int)Math.Round(distance / tickSize);
        }

        private double RoundToTick(double price, double tickSize)
        {
            return Math.Round(price / tickSize) * tickSize;
        }

        private double GetAngle(double current, double previous, int barsBack,
                                double tickSize)
        {
            return Math.Atan2(current - previous, barsBack * tickSize) *
                   (180.0 / Math.PI);
        }

        private string FormatRange(List<DiagnosticSnapshot> snapshots, int metric)
        {
            double minimum = Double.PositiveInfinity;
            double maximum = Double.NegativeInfinity;
            double total = 0;
            foreach (DiagnosticSnapshot snapshot in snapshots)
            {
                double value = metric == 0 ? snapshot.FastSlope :
                               metric == 1 ? snapshot.SlowSlope :
                               metric == 2 ? snapshot.TrendSlope :
                               metric == 3 ? snapshot.Separation824 :
                               metric == 4 ? snapshot.Separation850 :
                               snapshot.Separation2450;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                total += value;
            }
            return string.Format("{0:F2} / {1:F2} / {2:F2}", minimum,
                                 total / snapshots.Count, maximum);
        }

        private string ExportRangeReport(List<DiagnosticSnapshot> snapshots,
                                         string notes, string existingPath)
        {
            try
            {
                string directory = String.IsNullOrEmpty(ExportDirectory)
                    ? @"C:\rangebar_diagnostics" : ExportDirectory;
                Directory.CreateDirectory(directory);
                string path = String.IsNullOrEmpty(existingPath)
                    ? Path.Combine(directory, "RangeBarDiagnostics_" +
                        DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".md")
                    : existingPath;
                StringBuilder csv = new StringBuilder();
                csv.AppendLine("Time,Open,High,Low,Close,EMA8,EMA24,EMA50," +
                    "Slope8,Slope24,Slope50,Separation8_24,Separation8_50," +
                    "Separation24_50,BullishOrder,BearishOrder,CompactLongPB," +
                    "CompactShortPB,GeneralPB,Ema8Bounce,Ema24Bounce,Ema50Bounce");
                foreach (DiagnosticSnapshot snapshot in snapshots)
                {
                    csv.AppendFormat("{0:yyyy-MM-dd HH:mm:ss},{1:F4},{2:F4},{3:F4},{4:F4}," +
                        "{5:F4},{6:F4},{7:F4},{8:F4},{9:F4},{10:F4},{11:F4},{12:F4}," +
                        "{13:F4},{14},{15},{16},{17},{18},{19},{20},{21}\r\n",
                        snapshot.Time, snapshot.Open, snapshot.High, snapshot.Low,
                        snapshot.Close, snapshot.FastEma, snapshot.SlowEma,
                        snapshot.TrendEma, snapshot.FastSlope, snapshot.SlowSlope,
                        snapshot.TrendSlope, snapshot.Separation824, snapshot.Separation850,
                        snapshot.Separation2450, snapshot.BullishOrder,
                        snapshot.BearishOrder, snapshot.CompactLongPB,
                        snapshot.CompactShortPB, snapshot.GeneralPB,
                        snapshot.Ema8Bounce, snapshot.Ema24Bounce,
                        snapshot.Ema50Bounce);
                }
                StringBuilder document = new StringBuilder();
                document.AppendLine("# Range Bar Diagnostic");
                document.AppendLine();
                document.AppendLine("## Notes");
                document.AppendLine(String.IsNullOrEmpty(notes)
                    ? "_No notes entered._" : notes);
                document.AppendLine();
                document.AppendLine("## Range Summary");
                document.AppendLine("```text");
                document.AppendLine(m_ActiveRangeReport);
                document.AppendLine("```");
                document.AppendLine();
                document.AppendLine("## Per-Bar Data (CSV)");
                document.AppendLine("```csv");
                document.Append(csv.ToString());
                document.AppendLine("```");
                File.WriteAllText(path, document.ToString());
                return path;
            }
            catch (Exception exception)
            {
                Output.WriteLine("Range Bar Diagnostic report export failed: " +
                                 exception.Message);
                return "";
            }
        }

        private void ShowDiagnosticWindow(string text, string title)
        {
            try
            {
                if (m_DiagnosticWindow == null || m_DiagnosticWindow.IsDisposed)
                {
                    m_DiagnosticWindow = new Form();
                    m_DiagnosticWindow.Width = 900;
                    m_DiagnosticWindow.Height = 700;
                    m_DiagnosticWindow.StartPosition = FormStartPosition.CenterScreen;
                    m_DiagnosticWindow.FormClosed += OnDiagnosticWindowClosed;
                    SplitContainer layout = new SplitContainer();
                    layout.Dock = DockStyle.Fill;
                    layout.Orientation = Orientation.Horizontal;
                    layout.SplitterDistance = 145;
                    layout.IsSplitterFixed = true;
                    TableLayoutPanel notesLayout = new TableLayoutPanel();
                    notesLayout.Dock = DockStyle.Fill;
                    notesLayout.ColumnCount = 1;
                    notesLayout.RowCount = 3;
                    notesLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
                    notesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                    notesLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
                    Label notesLabel = new Label();
                    notesLabel.Text = "Notes / interpretation (saved at the top of the range report):";
                    notesLabel.Dock = DockStyle.Fill;
                    notesLayout.Controls.Add(notesLabel, 0, 0);
                    m_SaveNotesButton = new Button();
                    m_SaveNotesButton.Text = "Save Notes";
                    m_SaveNotesButton.Width = 100;
                    m_SaveNotesButton.Anchor = AnchorStyles.Right;
                    m_SaveNotesButton.Click += OnSaveNotesClicked;
                    m_NotesText = new TextBox();
                    m_NotesText.Multiline = true;
                    m_NotesText.ReadOnly = false;
                    m_NotesText.Enabled = true;
                    m_NotesText.TabStop = true;
                    m_NotesText.AcceptsReturn = true;
                    m_NotesText.AcceptsTab = true;
                    m_NotesText.ShortcutsEnabled = true;
                    m_NotesText.WordWrap = true;
                    m_NotesText.ScrollBars = ScrollBars.Vertical;
                    m_NotesText.BackColor = System.Drawing.Color.LemonChiffon;
                    m_NotesText.ForeColor = System.Drawing.Color.Black;
                    m_NotesText.BorderStyle = BorderStyle.FixedSingle;
                    m_NotesText.Dock = DockStyle.Fill;
                    notesLayout.Controls.Add(m_NotesText, 0, 1);
                    notesLayout.Controls.Add(m_SaveNotesButton, 0, 2);
                    layout.Panel1.Controls.Add(notesLayout);
                    m_DiagnosticText = new TextBox();
                    m_DiagnosticText.Multiline = true;
                    m_DiagnosticText.ReadOnly = true;
                    m_DiagnosticText.WordWrap = false;
                    m_DiagnosticText.ScrollBars = ScrollBars.Both;
                    m_DiagnosticText.Dock = DockStyle.Fill;
                    m_DiagnosticText.Font = new System.Drawing.Font(
                        "Consolas", 9.0f, System.Drawing.FontStyle.Regular);
                    layout.Panel2.Controls.Add(m_DiagnosticText);
                    m_DiagnosticWindow.Controls.Add(layout);
                }
                m_DiagnosticWindow.Text = title;
                m_DiagnosticText.Text = text;
                m_DiagnosticText.SelectionStart = 0;
                m_DiagnosticText.SelectionLength = 0;
                m_DiagnosticText.ScrollToCaret();
                // MultiCharts reliably routes keyboard input to a modal form,
                // whereas modeless forms created from a study mouse event can
                // show a caret without receiving typed characters.
                // A completed selection must be obvious even when other
                // application windows currently have focus.
                m_DiagnosticWindow.TopMost = true;
                m_DiagnosticWindow.BringToFront();
                m_DiagnosticWindow.Activate();
                m_DiagnosticWindow.ShowDialog();
            }
            catch (Exception exception)
            {
                Output.WriteLine("Range Bar Diagnostic window failed: " +
                                 exception.Message);
            }
        }

        private void OnDiagnosticWindowClosed(object sender, FormClosedEventArgs args)
        {
            m_DiagnosticWindow = null;
            m_DiagnosticText = null;
            m_NotesText = null;
            m_SaveNotesButton = null;
        }

        private void OnSaveNotesClicked(object sender, EventArgs args)
        {
            if (m_ActiveRangeSnapshots == null || m_ActiveRangeSnapshots.Count == 0)
            {
                if (m_DiagnosticText != null)
                    m_DiagnosticText.Text =
                        "Select two bars with Alt+D before saving a range report.";
                return;
            }
            m_ActiveRangeExportPath = ExportRangeReport(m_ActiveRangeSnapshots,
                m_NotesText == null ? "" : m_NotesText.Text,
                m_ActiveRangeExportPath);
            if (m_ActiveRangeExportPath.Length > 0 && m_DiagnosticText != null)
            {
                m_DiagnosticText.Text = m_ActiveRangeReport + "\r\n\r\n" +
                    "Report saved with notes: " + m_ActiveRangeExportPath + "\r\n" +
                    "Alt+D-click a new first bar to begin another range.";
            }
        }

        private void ClearActiveRange()
        {
            m_ActiveRangeSnapshots = null;
            m_ActiveRangeReport = "";
            m_ActiveRangeExportPath = "";
            if (m_NotesText != null)
                m_NotesText.Text = "";
        }

        private void ShowFirstSelectionNotice(DateTime time, double price)
        {
            ClearFirstSelectionNotice();
            try
            {
                m_FirstSelectionNotice = DrwText.Create(new ChartPoint(time, price),
                    "rangebar1selected\nPlease select the second range bar.");
                if (m_FirstSelectionNotice == null) return;
                m_FirstSelectionNotice.Color = System.Drawing.Color.DodgerBlue;
                m_FirstSelectionNotice.Size = 10;
                m_FirstSelectionNotice.HStyle = ETextStyleH.Center;
                m_FirstSelectionNotice.VStyle = ETextStyleV.Above;
            }
            catch { m_FirstSelectionNotice = null; }
        }

        private void ClearFirstSelectionNotice()
        {
            if (m_FirstSelectionNotice == null) return;
            try { m_FirstSelectionNotice.Delete(); }
            catch { }
            m_FirstSelectionNotice = null;
        }

        private void ShowCompletionNotice(DateTime time, double price)
        {
            ClearCompletionNotice();
            try
            {
                m_CompletionNotice = DrwText.Create(new ChartPoint(time, price),
                    "Selection complete\nOpening diagnostic window...");
                if (m_CompletionNotice == null) return;
                m_CompletionNotice.Color = System.Drawing.Color.ForestGreen;
                m_CompletionNotice.Size = 10;
                m_CompletionNotice.HStyle = ETextStyleH.Center;
                m_CompletionNotice.VStyle = ETextStyleV.Above;
            }
            catch { m_CompletionNotice = null; }
        }

        private void ClearCompletionNotice()
        {
            if (m_CompletionNotice == null) return;
            try { m_CompletionNotice.Delete(); }
            catch { }
            m_CompletionNotice = null;
        }

        protected override void Destroy()
        {
            ClearFirstSelectionNotice();
            ClearCompletionNotice();
        }
    }
}
