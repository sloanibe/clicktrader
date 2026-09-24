using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Read-only companion export for a short-interval Heikin Ashi chart.
    // Attach it to the HA chart, then Alt+D-click the first and last bars of
    // the desired interval. It does not stage or transmit orders.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    [MouseEvents(true)]
    public class HeikinAshiDiagnostic : IndicatorObject
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        private const int FastEmaLength = 8;
        private const int SlowEmaLength = 24;
        private const int TrendEmaLength = 50;
        private const int SlopeBars = 3;
        private const int PriorLevelBars = 7;

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_TrendEMA;
        private VariableSeries<double> m_HaOpen;
        private VariableSeries<double> m_HaHigh;
        private VariableSeries<double> m_HaLow;
        private VariableSeries<double> m_HaClose;
        private readonly Dictionary<int, Snapshot> m_Snapshots =
            new Dictionary<int, Snapshot>();
        private DateTime m_FirstSelectionTime = DateTime.MinValue;
        private ITextObject m_FirstSelectionNotice;
        private ITextObject m_CompletionNotice;
        private Form m_DiagnosticWindow;
        private TextBox m_DiagnosticText;

        [Input] public string ExportDirectory { get; set; }
        // True when this study is attached directly to a Heikin Ashi chart.
        // False calculates standard HA values from the chart's source OHLC.
        [Input] public bool ChartBarsAreHeikinAshi { get; set; }
        [Input] public int ExpectedBarSeconds { get; set; }
        [Input] public int BodyComparisonLookbackBars { get; set; }
        [Input] public int VolumeComparisonLookbackBars { get; set; }

        private class Snapshot
        {
            public int BarNumber;
            public DateTime Time;
            public double ChartOpen, ChartHigh, ChartLow, ChartClose;
            public double HaOpen, HaHigh, HaLow, HaClose;
            public double RangeTicks, BodyTicks, UpperWickTicks, LowerWickTicks;
            public double BodyToRangeRatio, CloseLocation;
            public int Direction, DirectionStreak, ContractionStreak;
            public double PreviousBodyTicks, PriorBodyAverageTicks;
            public double BodyVsPriorAverage;
            public bool TwoSidedWicks;
            public double Prior7High, Prior7Low;
            public double BreakAbovePrior7Ticks, BreakBelowPrior7Ticks;
            public double Ema8, Ema24, Ema50;
            public double Slope8, Slope24, Slope50;
            public double Gap824Ticks, Gap2450Ticks;
            public bool BullishOrder, BearishOrder;
            public double Volume, UpTicks, DownTicks, Delta;
            public double PriorVolumeAverage, VolumeRatio;
            public double BarDurationSeconds;
        }

        public HeikinAshiDiagnostic(object ctx) : base(ctx)
        {
            ExportDirectory = @"C:\rangebar_diagnostics";
            ChartBarsAreHeikinAshi = true;
            ExpectedBarSeconds = 2;
            BodyComparisonLookbackBars = 3;
            VolumeComparisonLookbackBars = 20;
        }

        protected override void Create()
        {
            m_HaOpen = new VariableSeries<double>(this);
            m_HaHigh = new VariableSeries<double>(this);
            m_HaLow = new VariableSeries<double>(this);
            m_HaClose = new VariableSeries<double>(this);
            m_FastEMA = new XAverage(this);
            m_SlowEMA = new XAverage(this);
            m_TrendEMA = new XAverage(this);
        }

        protected override void StartCalc()
        {
            m_Snapshots.Clear();
            m_FirstSelectionTime = DateTime.MinValue;
            ClearFirstSelectionNotice();
            ClearCompletionNotice();
            m_FastEMA.Length = FastEmaLength;
            m_SlowEMA.Length = SlowEmaLength;
            m_TrendEMA.Length = TrendEmaLength;
            if (ChartBarsAreHeikinAshi)
            {
                m_FastEMA.Price = Bars.Close;
                m_SlowEMA.Price = Bars.Close;
                m_TrendEMA.Price = Bars.Close;
            }
            else
            {
                m_FastEMA.Price = m_HaClose;
                m_SlowEMA.Price = m_HaClose;
                m_TrendEMA.Price = m_HaClose;
            }
        }

        protected override void CalcBar()
        {
            SetCurrentHeikinAshiValues();
            if (Bars.Status != EBarState.Close ||
                Bars.CurrentBar < Math.Max(TrendEmaLength,
                    Math.Max(PriorLevelBars,
                        Math.Max(BodyComparisonLookbackBars,
                                 VolumeComparisonLookbackBars))) +
                    SlopeBars)
                return;

            double tickSize = GetTickSize();
            Snapshot snapshot = BuildSnapshot(tickSize);
            m_Snapshots[Bars.CurrentBar] = snapshot;
        }

        private void SetCurrentHeikinAshiValues()
        {
            if (ChartBarsAreHeikinAshi)
            {
                m_HaOpen.Value = Bars.Open[0];
                m_HaHigh.Value = Bars.High[0];
                m_HaLow.Value = Bars.Low[0];
                m_HaClose.Value = Bars.Close[0];
                return;
            }

            double haClose = (Bars.Open[0] + Bars.High[0] +
                              Bars.Low[0] + Bars.Close[0]) / 4.0;
            double haOpen = Bars.CurrentBar <= 1
                ? (Bars.Open[0] + Bars.Close[0]) / 2.0
                : (m_HaOpen[1] + m_HaClose[1]) / 2.0;
            m_HaOpen.Value = haOpen;
            m_HaClose.Value = haClose;
            m_HaHigh.Value = Math.Max(Bars.High[0], Math.Max(haOpen, haClose));
            m_HaLow.Value = Math.Min(Bars.Low[0], Math.Min(haOpen, haClose));
        }

        private Snapshot BuildSnapshot(double tickSize)
        {
            Snapshot result = new Snapshot();
            result.BarNumber = Bars.CurrentBar;
            result.Time = Bars.Time[0];
            result.ChartOpen = Bars.Open[0]; result.ChartHigh = Bars.High[0];
            result.ChartLow = Bars.Low[0]; result.ChartClose = Bars.Close[0];
            result.HaOpen = m_HaOpen[0]; result.HaHigh = m_HaHigh[0];
            result.HaLow = m_HaLow[0]; result.HaClose = m_HaClose[0];

            result.RangeTicks = (result.HaHigh - result.HaLow) / tickSize;
            result.BodyTicks = Math.Abs(result.HaClose - result.HaOpen) / tickSize;
            double bodyHigh = Math.Max(result.HaOpen, result.HaClose);
            double bodyLow = Math.Min(result.HaOpen, result.HaClose);
            result.UpperWickTicks = (result.HaHigh - bodyHigh) / tickSize;
            result.LowerWickTicks = (bodyLow - result.HaLow) / tickSize;
            result.BodyToRangeRatio = result.RangeTicks > 0
                ? result.BodyTicks / result.RangeTicks : 0.0;
            result.CloseLocation = result.HaHigh > result.HaLow
                ? (result.HaClose - result.HaLow) /
                  (result.HaHigh - result.HaLow) : 0.5;
            result.Direction = result.HaClose > result.HaOpen ? 1 :
                               result.HaClose < result.HaOpen ? -1 : 0;
            result.TwoSidedWicks = result.UpperWickTicks > 0.1 &&
                                   result.LowerWickTicks > 0.1;

            result.PreviousBodyTicks = GetBodyTicks(1, tickSize);
            int bodyLookback = Math.Max(1, BodyComparisonLookbackBars);
            double priorBodyTotal = 0.0;
            for (int back = 1; back <= bodyLookback; back++)
                priorBodyTotal += GetBodyTicks(back, tickSize);
            result.PriorBodyAverageTicks = priorBodyTotal / bodyLookback;
            result.BodyVsPriorAverage = result.PriorBodyAverageTicks > 0
                ? result.BodyTicks / result.PriorBodyAverageTicks : 0.0;
            result.DirectionStreak = GetDirectionStreak(result.Direction);
            result.ContractionStreak = GetContractionStreak(tickSize);

            result.Prior7High = Double.NegativeInfinity;
            result.Prior7Low = Double.PositiveInfinity;
            for (int back = 1; back <= PriorLevelBars; back++)
            {
                result.Prior7High = Math.Max(result.Prior7High, GetHaHigh(back));
                result.Prior7Low = Math.Min(result.Prior7Low, GetHaLow(back));
            }
            result.BreakAbovePrior7Ticks =
                (result.HaHigh - result.Prior7High) / tickSize;
            result.BreakBelowPrior7Ticks =
                (result.Prior7Low - result.HaLow) / tickSize;

            result.Ema8 = m_FastEMA[0]; result.Ema24 = m_SlowEMA[0];
            result.Ema50 = m_TrendEMA[0];
            result.Slope8 = GetAngle(m_FastEMA[0], m_FastEMA[SlopeBars],
                                     SlopeBars, tickSize);
            result.Slope24 = GetAngle(m_SlowEMA[0], m_SlowEMA[SlopeBars],
                                      SlopeBars, tickSize);
            result.Slope50 = GetAngle(m_TrendEMA[0], m_TrendEMA[SlopeBars],
                                      SlopeBars, tickSize);
            result.Gap824Ticks = Math.Abs(result.Ema8 - result.Ema24) / tickSize;
            result.Gap2450Ticks = Math.Abs(result.Ema24 - result.Ema50) / tickSize;
            result.BullishOrder = result.Ema8 > result.Ema24 &&
                                  result.Ema24 > result.Ema50;
            result.BearishOrder = result.Ema8 < result.Ema24 &&
                                  result.Ema24 < result.Ema50;

            result.Volume = Bars.Volume[0];
            result.UpTicks = Bars.UpTicks[0];
            result.DownTicks = Bars.DownTicks[0];
            result.Delta = result.UpTicks - result.DownTicks;
            int volumeLookback = Math.Max(1, VolumeComparisonLookbackBars);
            double priorVolumeTotal = 0.0;
            for (int back = 1; back <= volumeLookback; back++)
                priorVolumeTotal += Bars.Volume[back];
            result.PriorVolumeAverage = priorVolumeTotal / volumeLookback;
            result.VolumeRatio = result.PriorVolumeAverage > 0
                ? result.Volume / result.PriorVolumeAverage : 0.0;
            result.BarDurationSeconds = Bars.CurrentBar > 1
                ? (Bars.Time[0] - Bars.Time[1]).TotalSeconds : 0.0;
            return result;
        }

        private double GetHaOpen(int barsBack)
        {
            return ChartBarsAreHeikinAshi ? Bars.Open[barsBack] : m_HaOpen[barsBack];
        }

        private double GetHaHigh(int barsBack)
        {
            return ChartBarsAreHeikinAshi ? Bars.High[barsBack] : m_HaHigh[barsBack];
        }

        private double GetHaLow(int barsBack)
        {
            return ChartBarsAreHeikinAshi ? Bars.Low[barsBack] : m_HaLow[barsBack];
        }

        private double GetHaClose(int barsBack)
        {
            return ChartBarsAreHeikinAshi ? Bars.Close[barsBack] : m_HaClose[barsBack];
        }

        private double GetBodyTicks(int barsBack, double tickSize)
        {
            return Math.Abs(GetHaClose(barsBack) - GetHaOpen(barsBack)) / tickSize;
        }

        private int GetDirection(int barsBack)
        {
            return GetHaClose(barsBack) > GetHaOpen(barsBack) ? 1 :
                   GetHaClose(barsBack) < GetHaOpen(barsBack) ? -1 : 0;
        }

        private int GetDirectionStreak(int currentDirection)
        {
            if (currentDirection == 0) return 0;
            int streak = 1;
            for (int back = 1; back <= 20; back++)
            {
                if (GetDirection(back) != currentDirection) break;
                streak++;
            }
            return streak;
        }

        private int GetContractionStreak(double tickSize)
        {
            int streak = 0;
            int maximum = Math.Max(1, BodyComparisonLookbackBars);
            for (int back = 0; back < maximum; back++)
            {
                if (GetBodyTicks(back, tickSize) >=
                    GetBodyTicks(back + 1, tickSize))
                    break;
                streak++;
            }
            return streak;
        }

        private double GetAngle(double current, double previous, int barsBack,
                                double tickSize)
        {
            return Math.Atan2(current - previous, barsBack * tickSize) *
                   (180.0 / Math.PI);
        }

        private double GetTickSize()
        {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            return tickSize > 0 ? tickSize : 0.25;
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            if (arg.buttons != MouseButtons.Left || !IsAltHeld(arg.keys) ||
                !IsDHeld(arg.keys))
                return;

            Snapshot selected = FindSnapshot(arg.point.Time, arg.point.Price);
            if (selected == null)
            {
                string message = "No completed Heikin Ashi diagnostic bar was " +
                    "captured at the selected point. If this is the live last " +
                    "bar, select the most recent completed bar instead.";
                Output.WriteLine(message);
                ShowDiagnosticWindow(message, "Heikin Ashi Diagnostic");
                return;
            }
            if (m_FirstSelectionTime == DateTime.MinValue)
            {
                m_FirstSelectionTime = selected.Time;
                ClearCompletionNotice();
                ShowFirstSelectionNotice(selected.Time, arg.point.Price);
                Output.WriteLine("Heikin Ashi export start selected: " +
                                 selected.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                return;
            }

            DateTime start = m_FirstSelectionTime <= selected.Time
                ? m_FirstSelectionTime : selected.Time;
            DateTime end = m_FirstSelectionTime <= selected.Time
                ? selected.Time : m_FirstSelectionTime;
            m_FirstSelectionTime = DateTime.MinValue;
            ClearFirstSelectionNotice();
            ShowCompletionNotice(selected.Time, arg.point.Price);
            ExportSelectedRange(start, end);
        }

        private Snapshot FindSnapshot(DateTime time, double price)
        {
            Snapshot closest = null;
            double distance = Double.MaxValue;
            foreach (KeyValuePair<int, Snapshot> pair in m_Snapshots)
            {
                Snapshot candidate = pair.Value;
                if (candidate.Time != time) continue;
                double candidateDistance = price < candidate.HaLow
                    ? candidate.HaLow - price
                    : price > candidate.HaHigh ? price - candidate.HaHigh : 0.0;
                if (candidateDistance < distance ||
                    (candidateDistance == distance &&
                     (closest == null || candidate.BarNumber > closest.BarNumber)))
                {
                    closest = candidate;
                    distance = candidateDistance;
                }
            }
            return closest;
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

        private void ShowFirstSelectionNotice(DateTime time, double price)
        {
            ClearFirstSelectionNotice();
            try
            {
                m_FirstSelectionNotice = DrwText.Create(
                    new ChartPoint(time, price),
                    "Range 1 selected\nPlease select the second Heikin Ashi bar.");
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
                m_CompletionNotice = DrwText.Create(
                    new ChartPoint(time, price),
                    "Selection complete\nExporting Heikin Ashi diagnostic...");
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

        private void ExportSelectedRange(DateTime start, DateTime end)
        {
            List<Snapshot> selected = new List<Snapshot>();
            foreach (KeyValuePair<int, Snapshot> pair in m_Snapshots)
            {
                if (pair.Value.Time >= start && pair.Value.Time <= end)
                    selected.Add(pair.Value);
            }
            selected.Sort(delegate(Snapshot left, Snapshot right)
            {
                int byTime = left.Time.CompareTo(right.Time);
                return byTime != 0 ? byTime :
                    left.BarNumber.CompareTo(right.BarNumber);
            });
            if (selected.Count == 0)
            {
                string message =
                    "No Heikin Ashi bars were found in the selected range.";
                Output.WriteLine(message);
                ShowDiagnosticWindow(message, "Heikin Ashi Diagnostic");
                return;
            }

            try
            {
                string directory = String.IsNullOrEmpty(ExportDirectory)
                    ? @"C:\rangebar_diagnostics" : ExportDirectory;
                Directory.CreateDirectory(directory);
                string stem = "HeikinAshiDiagnostic_" +
                    DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                string csvPath = Path.Combine(directory, stem + ".csv");
                string reportPath = Path.Combine(directory, stem + ".md");
                string report = BuildReport(selected, csvPath);
                File.WriteAllText(csvPath, BuildCsv(selected));
                File.WriteAllText(reportPath, report);
                Output.WriteLine("Heikin Ashi diagnostic exported: " + csvPath);
                ShowDiagnosticWindow(
                    report + "\r\nExported CSV: " + csvPath +
                    "\r\nSummary report: " + reportPath,
                    "Heikin Ashi Diagnostic: Selected Range");
            }
            catch (Exception exception)
            {
                string message = "Heikin Ashi diagnostic export failed: " +
                                 exception.Message;
                Output.WriteLine(message);
                ShowDiagnosticWindow(message, "Heikin Ashi Diagnostic");
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
                    m_DiagnosticWindow.StartPosition =
                        FormStartPosition.CenterScreen;
                    m_DiagnosticWindow.FormClosed += OnDiagnosticWindowClosed;
                    m_DiagnosticText = new TextBox();
                    m_DiagnosticText.Multiline = true;
                    m_DiagnosticText.ReadOnly = true;
                    m_DiagnosticText.WordWrap = false;
                    m_DiagnosticText.ScrollBars = ScrollBars.Both;
                    m_DiagnosticText.Dock = DockStyle.Fill;
                    m_DiagnosticText.Font = new System.Drawing.Font(
                        "Consolas", 9.0f, System.Drawing.FontStyle.Regular);
                    m_DiagnosticWindow.Controls.Add(m_DiagnosticText);
                }
                m_DiagnosticWindow.Text = title;
                m_DiagnosticText.Text = text;
                m_DiagnosticText.SelectionStart = 0;
                m_DiagnosticText.SelectionLength = 0;
                m_DiagnosticText.ScrollToCaret();
                m_DiagnosticWindow.TopMost = true;
                m_DiagnosticWindow.BringToFront();
                m_DiagnosticWindow.Activate();
                m_DiagnosticWindow.ShowDialog();
            }
            catch (Exception exception)
            {
                Output.WriteLine("Heikin Ashi diagnostic window failed: " +
                                 exception.Message);
            }
        }

        private void OnDiagnosticWindowClosed(object sender,
                                               FormClosedEventArgs args)
        {
            m_DiagnosticWindow = null;
            m_DiagnosticText = null;
        }

        private string BuildCsv(List<Snapshot> snapshots)
        {
            StringBuilder csv = new StringBuilder();
            csv.AppendLine("Contract,BarNumber,Time,ChartOpen,ChartHigh,ChartLow,ChartClose," +
                "HAOpen,HAHigh,HALow,HAClose,HARangeTicks,HABodyTicks,HAUpperWickTicks," +
                "HALowerWickTicks,HABodyToRangeRatio,HACloseLocation,HADirection," +
                "HADirectionStreak,HAContractionStreak,HAPreviousBodyTicks," +
                "HAPriorBodyAverageTicks,HABodyVsPriorAverage,HATwoSidedWicks," +
                "HAPrior7High,HAPrior7Low,HABreakAbovePrior7Ticks,HABreakBelowPrior7Ticks," +
                "HAEMA8,HAEMA24,HAEMA50,HASlope8,HASlope24,HASlope50," +
                "HAGap8_24Ticks,HAGap24_50Ticks,HABullishOrder,HABearishOrder," +
                "Volume,UpTicks,DownTicks,Delta,PriorVolumeAverage,VolumeRatio," +
                "BarDurationSeconds,ChartBarsAreHeikinAshi");
            foreach (Snapshot x in snapshots)
            {
                csv.AppendFormat(CultureInfo.InvariantCulture,
                    "{0},{1},{2:yyyy-MM-dd HH:mm:ss.fff},{3},{4},{5},{6},{7},{8},{9},{10}," +
                    "{11},{12},{13},{14},{15},{16},{17},{18},{19},{20},{21},{22},{23}," +
                    "{24},{25},{26},{27},{28},{29},{30},{31},{32},{33},{34},{35},{36}," +
                    "{37},{38},{39},{40},{41},{42},{43},{44},{45}\r\n",
                    CsvText(Bars.Info.Name), x.BarNumber, x.Time,
                    N(x.ChartOpen), N(x.ChartHigh), N(x.ChartLow), N(x.ChartClose),
                    N(x.HaOpen), N(x.HaHigh), N(x.HaLow), N(x.HaClose),
                    N(x.RangeTicks), N(x.BodyTicks), N(x.UpperWickTicks),
                    N(x.LowerWickTicks), N(x.BodyToRangeRatio), N(x.CloseLocation),
                    x.Direction, x.DirectionStreak, x.ContractionStreak,
                    N(x.PreviousBodyTicks), N(x.PriorBodyAverageTicks),
                    N(x.BodyVsPriorAverage), x.TwoSidedWicks,
                    N(x.Prior7High), N(x.Prior7Low), N(x.BreakAbovePrior7Ticks),
                    N(x.BreakBelowPrior7Ticks), N(x.Ema8), N(x.Ema24), N(x.Ema50),
                    N(x.Slope8), N(x.Slope24), N(x.Slope50), N(x.Gap824Ticks),
                    N(x.Gap2450Ticks), x.BullishOrder, x.BearishOrder,
                    N(x.Volume), N(x.UpTicks), N(x.DownTicks), N(x.Delta),
                    N(x.PriorVolumeAverage), N(x.VolumeRatio),
                    N(x.BarDurationSeconds), ChartBarsAreHeikinAshi);
            }
            return csv.ToString();
        }

        private string BuildReport(List<Snapshot> snapshots, string csvPath)
        {
            int bullish = 0, bearish = 0, twoSided = 0, contracting = 0;
            int measuredIntervals = 0, matchingIntervals = 0;
            foreach (Snapshot x in snapshots)
            {
                if (x.Direction > 0) bullish++;
                if (x.Direction < 0) bearish++;
                if (x.TwoSidedWicks) twoSided++;
                if (x.ContractionStreak > 0) contracting++;
                if (x.BarDurationSeconds > 0)
                {
                    measuredIntervals++;
                    if (ExpectedBarSeconds > 0 &&
                        Math.Abs(x.BarDurationSeconds - ExpectedBarSeconds) <= 0.001)
                        matchingIntervals++;
                }
            }
            StringBuilder report = new StringBuilder();
            report.AppendLine("# Heikin Ashi Diagnostic");
            report.AppendLine();
            report.AppendFormat("- Contract: {0}\n", Bars.Info.Name);
            report.AppendFormat("- Start: {0:yyyy-MM-dd HH:mm:ss.fff}\n",
                                snapshots[0].Time);
            report.AppendFormat("- End: {0:yyyy-MM-dd HH:mm:ss.fff}\n",
                                snapshots[snapshots.Count - 1].Time);
            report.AppendFormat("- Bars: {0}\n", snapshots.Count);
            report.AppendFormat("- Bullish/bearish HA bars: {0}/{1}\n",
                                bullish, bearish);
            report.AppendFormat("- Two-sided-wick bars: {0}\n", twoSided);
            report.AppendFormat("- Contracting-body bars: {0}\n", contracting);
            report.AppendFormat("- Chart bars treated as HA: {0}\n",
                                ChartBarsAreHeikinAshi);
            report.AppendFormat("- Expected bar interval: {0} seconds\n",
                                ExpectedBarSeconds);
            report.AppendFormat("- Bars matching expected interval: {0}/{1}\n",
                                matchingIntervals, measuredIntervals);
            if (ExpectedBarSeconds > 0 && measuredIntervals > 0 &&
                matchingIntervals < measuredIntervals * 0.95)
                report.AppendLine("- WARNING: fewer than 95% of measured bar " +
                                  "intervals match ExpectedBarSeconds.");
            report.AppendLine();
            report.AppendLine("The full dataset is written beside this report:");
            report.AppendLine();
            report.AppendLine("`" + Path.GetFileName(csvPath) + "`");
            report.AppendLine();
            report.AppendLine("HA values are synthetic pattern measurements, not " +
                              "executable prices. Synchronize bid/ask tick files " +
                              "for entry and exit analysis.");
            return report.ToString();
        }

        private string N(double value)
        {
            return value.ToString("F6", CultureInfo.InvariantCulture);
        }

        private string CsvText(string value)
        {
            if (value == null) return "";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        protected override void Destroy()
        {
            ClearFirstSelectionNotice();
            ClearCompletionNotice();
        }
    }
}
