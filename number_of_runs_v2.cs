using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true)]
    public class number_of_runs_v2 : IndicatorObject
    {
        [Input]
        public int RunLength { get; set; }

        [Input]
        public DateTime TargetDate { get; set; }

        private IPlotObject m_Plot;
        private int m_TotalRunCount;
        private bool m_ProcessingEnabled;
        private DateTime m_StartTime;
        private DateTime m_EndTime;
        
        // Variables to track the current run
        private bool m_InRun;
        private bool m_CurrentRunIsUp;
        private int m_CurrentRunLength;
        private DateTime m_CurrentRunStartTime;
        
        // Dictionary to track run length distribution
        private Dictionary<int, int> m_RunLengthDistribution;

        public number_of_runs_v2(object _ctx) : base(_ctx)
        {
            RunLength = 7;
            TargetDate = DateTime.Today;
        }

        protected override void Create()
        {
            // Create a plot for the total number of runs
            m_Plot = AddPlot(new PlotAttributes("Runs ≥ " + RunLength, EPlotShapes.Line, Color.Blue));
        }

        protected override void StartCalc()
        {
            // Initialize counters
            m_TotalRunCount = 0;
            m_InRun = false;
            m_CurrentRunLength = 0;
            m_RunLengthDistribution = new Dictionary<int, int>();

            // Set the start and end times for the target date
            // Start at 6:30 AM on the target date
            m_StartTime = new DateTime(TargetDate.Year, TargetDate.Month, TargetDate.Day, 6, 30, 0);

            // End at 1:59 PM on the target date
            m_EndTime = new DateTime(TargetDate.Year, TargetDate.Month, TargetDate.Day, 13, 59, 0);

            // This indicator only works on historical data, not real-time
            m_ProcessingEnabled = !Environment.IsRealTimeCalc;

            if (!m_ProcessingEnabled)
            {
                Output.WriteLine("This indicator is designed for historical analysis only.");
            }
            else
            {
                Output.WriteLine("Analyzing runs for date: " + TargetDate.ToShortDateString());
                Output.WriteLine("Time Period: 6:30 AM - 1:59 PM");
                Output.WriteLine("Minimum run length: " + RunLength + " bars");
            }
        }

        protected override void CalcBar()
        {
            // Skip if processing is not enabled (real-time data)
            if (!m_ProcessingEnabled)
            {
                return;
            }

            // Only process bars on the target date between start and end times
            if (Bars.Time[0] >= m_StartTime && Bars.Time[0] <= m_EndTime)
            {
                // Determine if this bar is up or down
                bool isUpBar = Bars.Close[0] > Bars.Open[0];
                bool isDownBar = Bars.Close[0] < Bars.Open[0];

                // Skip neutral bars (close = open)
                if (!isUpBar && !isDownBar)
                {
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
                    }
                    else
                    {
                        // The run has ended, record it
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

        private void StartNewRun(bool isUpBar)
        {
            m_InRun = true;
            m_CurrentRunIsUp = isUpBar;
            m_CurrentRunLength = 1;
            m_CurrentRunStartTime = Bars.Time[0];
        }

        private void RecordRun()
        {
            // Update run length distribution
            if (m_RunLengthDistribution.ContainsKey(m_CurrentRunLength))
            {
                m_RunLengthDistribution[m_CurrentRunLength]++;
            }
            else
            {
                m_RunLengthDistribution[m_CurrentRunLength] = 1;
            }

            // Only count runs that meet the minimum length criteria
            if (m_CurrentRunLength >= RunLength)
            {
                m_TotalRunCount++;
                
                // Output information about the run
                string direction = m_CurrentRunIsUp ? "UP" : "DOWN";
                Output.WriteLine("Run #" + m_TotalRunCount + " (" + direction + ") - Date: " + 
                    Bars.Time[0].ToShortDateString() + ", Start: " + 
                    m_CurrentRunStartTime.ToString("HH:mm:ss") + ", End: " + 
                    Bars.Time[0].ToString("HH:mm:ss") + ", Length: " + m_CurrentRunLength + " bars");
            }

            // Reset the run tracking
            m_InRun = false;
            m_CurrentRunLength = 0;
        }

        protected override void StopCalc()
        {
            // Check if we're in a run at the end of calculation
            if (m_InRun)
            {
                // Record the final run
                RecordRun();
            }

            // Output summary
            Output.WriteLine("");
            Output.WriteLine("╔════════════════════════════════════════════════════════════╗");
            Output.WriteLine("║                   RUN ANALYSIS SUMMARY                    ║");
            Output.WriteLine("╠════════════════════════════════════════════════════════════╣");
            Output.WriteLine("║ Date: " + TargetDate.ToShortDateString() + "                                        ║");
            Output.WriteLine("║ Time Period: 6:30 AM - 1:59 PM                           ║");
            Output.WriteLine("║ Minimum Run Length: " + RunLength + " bars                             ║");
            Output.WriteLine("║ TOTAL RUNS ≥ " + RunLength + ": " + m_TotalRunCount + "                                   ║");
            Output.WriteLine("╚════════════════════════════════════════════════════════════╝");
            
            // Output distribution of run lengths that meet or exceed the minimum
            Output.WriteLine("");
            Output.WriteLine("Run Length Distribution (≥ " + RunLength + "):");
            
            // Find the maximum run length
            int maxRunLength = 0;
            foreach (int length in m_RunLengthDistribution.Keys)
            {
                if (length >= RunLength && length > maxRunLength)
                {
                    maxRunLength = length;
                }
            }
            
            // Output the distribution
            for (int i = RunLength; i <= maxRunLength; i++)
            {
                int count = m_RunLengthDistribution.ContainsKey(i) ? m_RunLengthDistribution[i] : 0;
                if (count > 0)
                {
                    Output.WriteLine("  Length " + i + ": " + count + " runs");
                }
            }
        }
    }
}
