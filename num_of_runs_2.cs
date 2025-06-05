using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    public class num_of_runs_2 : IndicatorObject
    {
        [Input]
        public int NumRuns { get; set; }

        [Input]
        public DateTime TargetDate { get; set; }

        private IPlotObject m_Plot;
        private int m_UpRunCount;
        private int m_DownRunCount;
        private int m_TotalRunCount;
        private DateTime m_StartTime;
        private DateTime m_EndTime;
        private int m_TotalBars;
        private bool m_DebugMode;
        private bool m_HasOutputSummary;

        // Variables to track the current run
        private bool m_InRun;
        private bool m_CurrentRunIsUp;
        private int m_CurrentRunLength;
        private int m_RunStartBarIndex;
        private Dictionary<int, DateTime> m_RunStartTimes;

        public num_of_runs_2(object ctx) : base(ctx)
        {
            NumRuns = 7; // Default to 7 bars in a run
            TargetDate = DateTime.Today.AddDays(-1); // Default to yesterday
            m_DebugMode = true; // Enable debug mode to help troubleshoot
        }

        protected override void Create()
        {
            // Create a plot to display the number of runs
            m_Plot = AddPlot(new PlotAttributes("Runs", EPlotShapes.Line, Color.Blue));

            // Initialize counters
            m_UpRunCount = 0;
            m_DownRunCount = 0;
            m_TotalRunCount = 0;
            m_TotalBars = 0;
            m_HasOutputSummary = false;

            // Initialize run tracking
            m_InRun = false;
            m_CurrentRunLength = 0;
            m_RunStartBarIndex = -1;
            m_RunStartTimes = new Dictionary<int, DateTime>();

            Output.Clear(); // Clear the output window
            Output.WriteLine("Create method completed.");
        }

        protected override void StartCalc()
        {
            // Initialize counters
            m_UpRunCount = 0;
            m_DownRunCount = 0;
            m_TotalRunCount = 0;
            m_TotalBars = 0;
            m_HasOutputSummary = false;

            // Initialize run tracking
            m_InRun = false;
            m_CurrentRunLength = 0;
            m_RunStartBarIndex = -1;
            m_RunStartTimes = new Dictionary<int, DateTime>();

            // Set the start and end times for the target date
            // Start at 6:30 AM on the target date
            m_StartTime = new DateTime(TargetDate.Year, TargetDate.Month, TargetDate.Day, 6, 30, 0);

            // End at 1:59 PM on the target date
            m_EndTime = new DateTime(TargetDate.Year, TargetDate.Month, TargetDate.Day, 13, 59, 0);

            Output.WriteLine("Analyzing runs for date: " + TargetDate.ToShortDateString());
            Output.WriteLine("Start time: 6:30 AM, End time: 1:59 PM");
            Output.WriteLine("Minimum run length: " + NumRuns.ToString() + " bars");
            Output.WriteLine("Debug mode: " + (m_DebugMode ? "ON" : "OFF"));
            Output.WriteLine("Current date/time: " + DateTime.Now.ToString());
            Output.WriteLine("First bar time: " + (Bars.CurrentBar > 0 ? Bars.Time[0].ToString() : "No bars"));
            Output.WriteLine("Total bars in chart: " + Bars.FullSymbolData.Count.ToString());
        }

        protected override void CalcBar()
        {
            // Skip processing if we're in real-time calculation mode
            if (Environment.IsRealTimeCalc)
            {
                if (m_DebugMode && Bars.CurrentBar % 100 == 0)
                {
                    Output.WriteLine("Skipping real-time calculation at bar " + Bars.CurrentBar.ToString());
                }
                return;
            }

            // Only process bars on the target date between start and end times
            DateTime barTime = Bars.Time[0];
            DateTime barDate = barTime.Date;

            // Debug output for first few bars to verify date comparison
            if (m_DebugMode && Bars.CurrentBar < 5)
            {
                Output.WriteLine("Bar " + Bars.CurrentBar.ToString() + " time: " + barTime.ToString());
                Output.WriteLine("Bar date: " + barDate.ToString() + ", Target date: " + TargetDate.Date.ToString());
                Output.WriteLine("Date comparison: " + (barDate == TargetDate.Date).ToString());
                Output.WriteLine("Time window check: " + (barTime >= m_StartTime && barTime <= m_EndTime).ToString());
            }

            if (barDate == TargetDate.Date && barTime >= m_StartTime && barTime <= m_EndTime)
            {
                // Count total bars for debugging
                m_TotalBars++;

                if (m_DebugMode && (m_TotalBars <= 5 || m_TotalBars % 50 == 0))
                {
                    Output.WriteLine("Processed " + m_TotalBars.ToString() + " bars within time window");
                }

                // Determine if this bar is up or down compared to previous bar
                // We need at least one previous bar to compare
                if (Bars.CurrentBar > 0)
                {
                    bool isUpBar = Bars.Close[0] > Bars.Close[1];
                    bool isDownBar = Bars.Close[0] < Bars.Close[1];

                    if (m_DebugMode && (m_TotalBars <= 5 || m_TotalBars % 50 == 0))
                    {
                        Output.WriteLine("Bar " + m_TotalBars.ToString() + ": Close=" + Bars.Close[0].ToString() + ", PrevClose=" + Bars.Close[1].ToString() + ", IsUp=" + isUpBar.ToString() + ", IsDown=" + isDownBar.ToString());
                    }

                    // Skip neutral bars (close = previous close)
                    if (!isUpBar && !isDownBar)
                    {
                        if (m_DebugMode && (m_TotalBars <= 5 || m_TotalBars % 50 == 0))
                        {
                            Output.WriteLine("Neutral bar - skipping");
                        }
                        return;
                    }

                    // Check if we're already tracking a run
                    if (m_InRun)
                    {
                        // Check if this bar continues the current run
                        if ((m_CurrentRunIsUp && isUpBar) || (!m_CurrentRunIsUp && isDownBar))
                        {
                            // Increment the run length
                            m_CurrentRunLength++;

                            if (m_DebugMode && m_CurrentRunLength >= NumRuns - 2)
                            {
                                Output.WriteLine("Run continuing - Direction: " + (m_CurrentRunIsUp ? "UP" : "DOWN") + ", Length: " + m_CurrentRunLength.ToString());
                            }
                        }
                        else
                        {
                            // The run has ended, record it if it meets the criteria
                            RecordRun();

                            // Start a new run with this bar
                            StartNewRun(isUpBar);
                        }
                    }
                    else
                    {
                        // Start a new run with this bar
                        StartNewRun(isUpBar);
                    }

                    // Update the plot with the total number of runs
                    m_Plot.Set(m_TotalRunCount);
                }
            }

            // Check if we've reached the end of the day
            // Use the last bar to output summary if we're at the end of the chart
            bool isLastBar = Bars.CurrentBar == Bars.FullSymbolData.Count - 1;
            bool isEndOfDay = barTime >= m_EndTime && barTime < m_EndTime.AddMinutes(1);

            if ((isEndOfDay || isLastBar) && !m_HasOutputSummary)
            {
                // Check if we're in a run
                if (m_InRun)
                {
                    // Record the final run
                    RecordRun();
                }

                // Output the summary
                OutputSummary();
                m_HasOutputSummary = true;
            }
        }

        private void StartNewRun(bool isUpBar)
        {
            m_InRun = true;
            m_CurrentRunIsUp = isUpBar;
            m_CurrentRunLength = 1;
            m_RunStartBarIndex = Bars.CurrentBar;

            // Store the start time of this run
            m_RunStartTimes[m_RunStartBarIndex] = Bars.Time[0];

            if (m_DebugMode && (m_TotalBars <= 5 || m_TotalBars % 50 == 0))
            {
                Output.WriteLine("Starting new " + (isUpBar ? "UP" : "DOWN") + " run");
            }
        }

        private void RecordRun()
        {
            if (m_DebugMode && m_CurrentRunLength >= NumRuns - 2)
            {
                Output.WriteLine("Run ended - Direction: " + (m_CurrentRunIsUp ? "UP" : "DOWN") + ", Length: " + m_CurrentRunLength.ToString() + ", Meets criteria: " + (m_CurrentRunLength >= NumRuns).ToString());
            }

            // Only count runs that meet the minimum length criteria
            if (m_CurrentRunLength >= NumRuns)
            {
                // Increment the appropriate counter
                if (m_CurrentRunIsUp)
                {
                    m_UpRunCount++;
                }
                else
                {
                    m_DownRunCount++;
                }

                m_TotalRunCount++;

                // Output information about the run
                string direction = m_CurrentRunIsUp ? "UP" : "DOWN";
                Output.WriteLine("Run #" + m_TotalRunCount.ToString() + " (" + direction + ") - Length: " + m_CurrentRunLength.ToString() + " bars");

                // Draw an arrow at the beginning of the run
                DrawRunArrow(m_RunStartBarIndex, m_CurrentRunIsUp);
            }

            // Reset the run tracking
            m_InRun = false;
            m_CurrentRunLength = 0;
            m_RunStartBarIndex = -1;
        }

        private void DrawRunArrow(int barIndex, bool isUpRun)
        {
            if (!m_RunStartTimes.ContainsKey(barIndex))
            {
                Output.WriteLine("Error: Cannot find start time for bar index " + barIndex.ToString());
                return;
            }

            DateTime startTime = m_RunStartTimes[barIndex];

            // Create a chart point for the arrow
            ChartPoint arrowPoint;
            if (isUpRun)
            {
                // Place green up arrow below the low of the bar
                double offset = 10 * Bars.Point;
                arrowPoint = new ChartPoint(startTime, Bars.Low[Bars.CurrentBar - barIndex] - offset);

                // Create a green up arrow
                IArrowObject arrow = DrwArrow.Create(arrowPoint, false);
                arrow.Color = Color.Green;
                arrow.Size = 5;  // Make it larger for visibility
            }
            else
            {
                // Place red down arrow above the high of the bar
                double offset = 10 * Bars.Point;
                arrowPoint = new ChartPoint(startTime, Bars.High[Bars.CurrentBar - barIndex] + offset);

                // Create a red down arrow
                IArrowObject arrow = DrwArrow.Create(arrowPoint, true);
                arrow.Color = Color.Red;
                arrow.Size = 5;  // Make it larger for visibility
            }

            Output.WriteLine("Drew " + (isUpRun ? "UP" : "DOWN") + " arrow at bar index " + barIndex.ToString() + " (time: " + startTime.ToString() + ")");
        }

        private void OutputSummary()
        {
            Output.WriteLine("=== Run Analysis Summary ===");
            Output.WriteLine("Date: " + TargetDate.ToShortDateString());
            Output.WriteLine("Time Period: 6:30 AM - 1:59 PM");
            Output.WriteLine("Total Bars Analyzed: " + m_TotalBars.ToString());
            Output.WriteLine("Minimum Run Length: " + NumRuns.ToString() + " bars");
            Output.WriteLine("Up Runs: " + m_UpRunCount.ToString());
            Output.WriteLine("Down Runs: " + m_DownRunCount.ToString());
            Output.WriteLine("Total Runs: " + m_TotalRunCount.ToString());
            Output.WriteLine("=========================");
        }
    }
}
