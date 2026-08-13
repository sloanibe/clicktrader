using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PowerLanguage;

namespace PowerLanguage.Indicator
{
    // Marks the first chart bar at or after the configured session start and
    // navigates between sessions with Left/Right Arrow plus a left-click.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    [MouseEvents(true)]
    [UpdateOnEveryTick(true)]
    public class RangeSessionNavigator : IndicatorObject
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [Input] public int SessionStartHour { get; set; }
        [Input] public int SessionStartMinute { get; set; }

        private DateTime m_LastMarkedSessionDate = DateTime.MinValue;
        private readonly List<int> m_SessionBarNumbers = new List<int>();
        private readonly List<DateTime> m_SessionTimes = new List<DateTime>();
        // Retain references so MultiCharts keeps all historical day labels
        // alive after the bar that created them has finished calculating.
        private readonly List<ITextObject> m_SessionDayLabels =
            new List<ITextObject>();
        private ITextObject m_StatusMessage;

        public RangeSessionNavigator(object ctx) : base(ctx)
        {
            SessionStartHour = 6;
            SessionStartMinute = 30;
        }

        protected override void StartCalc()
        {
            m_LastMarkedSessionDate = DateTime.MinValue;
            m_SessionBarNumbers.Clear();
            m_SessionTimes.Clear();
            m_SessionDayLabels.Clear();
        }

        protected override void CalcBar()
        {
            if (Bars.Status != EBarState.Close || Bars.CurrentBar < 1) return;

            DateTime currentBarTime = Bars.Time[0];
            DateTime sessionStart = currentBarTime.Date
                .AddHours(SessionStartHour)
                .AddMinutes(SessionStartMinute);

            // Range bars do not necessarily print at exactly 06:30. Mark the
            // first completed bar at or after 06:30 when the preceding bar is
            // still before that session boundary.
            if (currentBarTime < sessionStart || Bars.Time[1] >= sessionStart)
                return;
            if (m_LastMarkedSessionDate == sessionStart.Date) return;

            DrawSessionLine(currentBarTime);
            RememberSessionMarker(currentBarTime);
            m_LastMarkedSessionDate = sessionStart.Date;
        }

        private void RememberSessionMarker(DateTime sessionBarTime)
        {
            // This MultiCharts version exposes the full-series count but not
            // FullSymbolData.CurrentBar. Bars.CurrentBar is offset by the
            // study's MaxBarsBack, so convert it to the absolute bar number
            // expected by ChartCommands.ScrollToBar.
            int absoluteBarNumber = Bars.CurrentBar + ExecInfo.MaxBarsBack;
            if (m_SessionBarNumbers.Count > 0 &&
                m_SessionBarNumbers[m_SessionBarNumbers.Count - 1] == absoluteBarNumber)
                return;

            m_SessionBarNumbers.Add(absoluteBarNumber);
            m_SessionTimes.Add(sessionBarTime);
        }

        private int FindSessionIndexAt(DateTime chartTime)
        {
            // Session markers are stored chronologically. The session that
            // owns a chart location is the latest marker at or before the
            // clicked time, including overnight bars before the next 06:30.
            for (int index = m_SessionTimes.Count - 1; index >= 0; index--)
            {
                if (m_SessionTimes[index] <= chartTime) return index;
            }
            return -1;
        }

        private bool NavigateToPreviousSession(DateTime chartTime)
        {
            if (m_SessionBarNumbers.Count == 0) return false;

            int currentSessionIndex = FindSessionIndexAt(chartTime);
            if (currentSessionIndex <= 0) return false;

            ScrollToSession(currentSessionIndex - 1);
            return true;
        }

        private bool NavigateToNextSession(DateTime chartTime)
        {
            if (m_SessionBarNumbers.Count == 0) return false;

            int currentSessionIndex = FindSessionIndexAt(chartTime);
            // A click before the oldest known session can move forward to the
            // first marker; otherwise advance from the clicked session.
            int targetSessionIndex = currentSessionIndex < 0
                ? 0
                : currentSessionIndex + 1;
            if (targetSessionIndex >= m_SessionBarNumbers.Count) return false;

            ScrollToSession(targetSessionIndex);
            return true;
        }

        private void ScrollToSession(int sessionIndex)
        {
            int targetBar = m_SessionBarNumbers[sessionIndex];
            ChartCommands.ScrollToBar(1, targetBar);
            Output.WriteLine(
                "Range Session Navigator: ScrollToBar requested " +
                m_SessionTimes[sessionIndex].ToString(
                    "M/d/yyyy h:mm tt") + " at absolute bar " +
                targetBar.ToString());
        }

        private bool IsKeyDown(Keys key)
        {
            try
            {
                return (GetAsyncKeyState((int)key) & 0x8000) != 0;
            }
            catch
            {
                return false;
            }
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            if (arg.buttons != MouseButtons.Left) return;

            bool leftArrowHeld = (arg.keys & Keys.KeyCode) == Keys.Left ||
                                 IsKeyDown(Keys.Left);
            if (leftArrowHeld)
            {
                if (!NavigateToPreviousSession(arg.point.Time))
                    ShowChartMessage(arg.point, "NO EARLIER SESSION");
                return;
            }

            bool rightArrowHeld = (arg.keys & Keys.KeyCode) == Keys.Right ||
                                  IsKeyDown(Keys.Right);
            if (rightArrowHeld)
            {
                if (!NavigateToNextSession(arg.point.Time))
                    ShowChartMessage(arg.point, "NO LATER SESSION");
                return;
            }
        }

        private void ShowChartMessage(ChartPoint point, string text)
        {
            try
            {
                if (m_StatusMessage == null)
                    m_StatusMessage = DrwText.Create(point, text);
                if (m_StatusMessage == null) return;

                m_StatusMessage.Location = point;
                m_StatusMessage.Text = text;
                m_StatusMessage.Color = Color.DarkOrange;
                m_StatusMessage.Size = 12;
                m_StatusMessage.HStyle = ETextStyleH.Right;
                m_StatusMessage.VStyle = ETextStyleV.Above;
            }
            catch (NullReferenceException)
            {
                // MultiCharts may rebuild drawings during a chart refresh.
                // A later test click will recreate the notification.
                m_StatusMessage = null;
            }
        }

        protected override void Destroy()
        {
            if (m_StatusMessage != null)
            {
                try { m_StatusMessage.Delete(); }
                catch { }
                m_StatusMessage = null;
            }
        }

        private void DrawSessionLine(DateTime barTime)
        {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;

            double lowerPrice = Bars.Low[0] - tickSize;
            double upperPrice = Bars.High[0] + tickSize;
            if (upperPrice <= lowerPrice)
                upperPrice = lowerPrice + tickSize;

            // Create the text independently of the line: MultiCharts can
            // occasionally decline a trend-line creation while rebuilding a
            // chart, but that must not suppress the day marker.
            DrawSessionDayLabel(barTime, (lowerPrice + upperPrice) / 2.0);

            ITrendLineObject line = DrwTrendLine.Create(
                new ChartPoint(barTime, lowerPrice),
                new ChartPoint(barTime, upperPrice));
            if (line == null) return;

            line.Color = Color.FromArgb(70, 70, 70);
            line.Style = ETLStyle.ToolDashed;
            line.Size = 1;
            // Extending both ends of a vertical trend line fills the chart
            // pane without choosing artificial prices that distort scaling.
            line.ExtLeft = true;
            line.ExtRight = true;
            line.AnchorToBars = true;
        }

        private void DrawSessionDayLabel(DateTime barTime, double price)
        {
            ITextObject label = DrwText.Create(
                new ChartPoint(barTime, price), GetSessionLabel(barTime));
            if (label == null) return;

            // Right alignment keeps the label's left edge just to the right
            // of the session line.  The price midpoint keeps it visually
            // centered on the marker rather than above or below the bars.
            label.Color = Color.DarkViolet;
            label.Size = 10;
            label.HStyle = ETextStyleH.Right;
            label.VStyle = ETextStyleV.Above;
            m_SessionDayLabels.Add(label);
        }

        private string GetDayAbbreviation(DayOfWeek day)
        {
            switch (day)
            {
                case DayOfWeek.Monday: return "MON";
                case DayOfWeek.Tuesday: return "TUE";
                case DayOfWeek.Wednesday: return "WED";
                case DayOfWeek.Thursday: return "THU";
                case DayOfWeek.Friday: return "FRI";
                case DayOfWeek.Saturday: return "SAT";
                default: return "SUN";
            }
        }

        private string GetSessionLabel(DateTime sessionTime)
        {
            return GetDayAbbreviation(sessionTime.DayOfWeek) + ": " +
                sessionTime.ToString("M/d/yy");
        }
    }
}
