using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true)]
    public class trend_continuation : IndicatorObject
    {
        [Input]
        public int MinRunLength { get; set; }

        [Input]
        public DateTime TargetDate { get; set; }
        
        [Input]
        public bool DebugMode { get; set; }

        private bool m_ProcessingEnabled;
        private DateTime m_StartTime;
        private DateTime m_EndTime;
        
        // Variables to track the current state
        private int m_CurrentRunLength;
        private bool m_CurrentRunIsUp;
        private bool m_InRun;
        private int m_PatternState; // 0 = in run, 1 = reversal, 2 = back to trend, 3 = next bar
        
        // Dictionaries to track trend continuation patterns
        private Dictionary<int, int> m_UpTrendPatternCount;
        private Dictionary<int, int> m_UpTrendContinuationCount;
        private Dictionary<int, int> m_DownTrendPatternCount;
        private Dictionary<int, int> m_DownTrendContinuationCount;

        public trend_continuation(object _ctx) : base(_ctx)
        {
            MinRunLength = 3; // Minimum run length to consider
            TargetDate = DateTime.Today;
            DebugMode = false;
        }

        protected override void Create()
        {
            // Add a simple plot to confirm the indicator is loaded
            AddPlot(new PlotAttributes("TrendCont", EPlotShapes.Line, Color.Blue, Color.Empty, 0, 0, true));
        }

        protected override void StartCalc()
        {
            // Initialize pattern tracking
            m_CurrentRunLength = 0;
            m_InRun = false;
            m_PatternState = 0;
            
            // Initialize dictionaries
            m_UpTrendPatternCount = new Dictionary<int, int>();
            m_UpTrendContinuationCount = new Dictionary<int, int>();
            m_DownTrendPatternCount = new Dictionary<int, int>();
            m_DownTrendContinuationCount = new Dictionary<int, int>();

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
                Output.WriteLine("Analyzing trend continuation patterns for date: " + TargetDate.ToShortDateString());
                Output.WriteLine("Time Period: 6:30 AM - 1:59 PM");
                Output.WriteLine("Minimum run length: " + MinRunLength + " bars");
            }
        }

        protected override void CalcBar()
        {
            // Plot a constant value to show the indicator is active
            Plots[0].Set(1);
            
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
                
                // Process the bar based on the current pattern state
                switch (m_PatternState)
                {
                    case 0: // In run - looking for a run of n bars
                        ProcessRunState(isUpBar, isDownBar);
                        break;
                        
                    case 1: // Reversal - looking for a single bar reversal
                        ProcessReversalState(isUpBar, isDownBar);
                        break;
                        
                    case 2: // Back to trend - looking for a bar back in the original trend direction
                        ProcessBackToTrendState(isUpBar, isDownBar);
                        break;
                        
                    case 3: // Next bar - checking if the next bar continues the trend
                        ProcessNextBarState(isUpBar, isDownBar);
                        break;
                }
                
                // Output the results when we reach the last bar of the day
                if (Bars.Time[0].Hour == 13 && Bars.Time[0].Minute >= 55 && Bars.LastBarOnChart)
                {
                    OutputTrendContinuationTable();
                }
            }
        }
        
        private void ProcessRunState(bool isUpBar, bool isDownBar)
        {
            if (!m_InRun)
            {
                // Start a new run
                m_InRun = true;
                m_CurrentRunIsUp = isUpBar;
                m_CurrentRunLength = 1;
            }
            else if ((m_CurrentRunIsUp && isUpBar) || (!m_CurrentRunIsUp && isDownBar))
            {
                // Continue the current run
                m_CurrentRunLength++;
            }
            else
            {
                // Run ended with a reversal
                if (m_CurrentRunLength >= MinRunLength)
                {
                    // We have a run of sufficient length followed by a reversal
                    // Move to the next state in the pattern
                    m_PatternState = 1;
                    
                    if (DebugMode)
                    {
                        Output.WriteLine("[DEBUG] Run of " + m_CurrentRunLength + " " + 
                            (m_CurrentRunIsUp ? "UP" : "DOWN") + " bars ended with reversal at " + 
                            Bars.Time[0].ToString("HH:mm:ss"));
                    }
                }
                else
                {
                    // Run was too short, reset and start a new run
                    m_InRun = true;
                    m_CurrentRunIsUp = isUpBar;
                    m_CurrentRunLength = 1;
                }
            }
        }
        
        private void ProcessReversalState(bool isUpBar, bool isDownBar)
        {
            // We're looking for a reversal back to the original trend
            if ((m_CurrentRunIsUp && isUpBar) || (!m_CurrentRunIsUp && isDownBar))
            {
                // We got a bar back in the original trend direction
                m_PatternState = 2;
                
                if (DebugMode)
                {
                    Output.WriteLine("[DEBUG] Reversal back to " + 
                        (m_CurrentRunIsUp ? "UP" : "DOWN") + " trend at " + 
                        Bars.Time[0].ToString("HH:mm:ss"));
                }
            }
            else
            {
                // The reversal continued, so this isn't the pattern we're looking for
                // Reset to look for a new run
                m_PatternState = 0;
                m_InRun = true;
                m_CurrentRunIsUp = isUpBar;
                m_CurrentRunLength = 1;
                
                if (DebugMode)
                {
                    Output.WriteLine("[DEBUG] Reversal continued, pattern broken at " + 
                        Bars.Time[0].ToString("HH:mm:ss"));
                }
            }
        }
        
        private void ProcessBackToTrendState(bool isUpBar, bool isDownBar)
        {
            // Now we're looking at the next bar after returning to the trend
            m_PatternState = 3;
            
            if (DebugMode)
            {
                Output.WriteLine("[DEBUG] Checking next bar for trend continuation at " + 
                    Bars.Time[0].ToString("HH:mm:ss"));
            }
        }
        
        private void ProcessNextBarState(bool isUpBar, bool isDownBar)
        {
            // Check if the next bar continues the original trend
            bool trendContinued = (m_CurrentRunIsUp && isUpBar) || (!m_CurrentRunIsUp && isDownBar);
            
            // Record the pattern occurrence and whether the trend continued
            RecordPatternResult(m_CurrentRunLength, m_CurrentRunIsUp, trendContinued);
            
            if (DebugMode)
            {
                Output.WriteLine("[DEBUG] Pattern completed - Run length: " + m_CurrentRunLength + 
                    ", Trend: " + (m_CurrentRunIsUp ? "UP" : "DOWN") + 
                    ", Continuation: " + (trendContinued ? "YES" : "NO") + 
                    " at " + Bars.Time[0].ToString("HH:mm:ss"));
            }
            
            // Reset to look for a new run
            m_PatternState = 0;
            m_InRun = true;
            m_CurrentRunIsUp = isUpBar;
            m_CurrentRunLength = 1;
        }
        
        private void RecordPatternResult(int runLength, bool isUpTrend, bool trendContinued)
        {
            // Cap the run length at 20 for the table
            if (runLength > 20)
            {
                runLength = 20;
            }
            
            if (isUpTrend)
            {
                // Record the pattern occurrence for up trends
                if (!m_UpTrendPatternCount.ContainsKey(runLength))
                {
                    m_UpTrendPatternCount[runLength] = 0;
                }
                m_UpTrendPatternCount[runLength]++;
                
                // Record if the trend continued
                if (trendContinued)
                {
                    if (!m_UpTrendContinuationCount.ContainsKey(runLength))
                    {
                        m_UpTrendContinuationCount[runLength] = 0;
                    }
                    m_UpTrendContinuationCount[runLength]++;
                }
            }
            else
            {
                // Record the pattern occurrence for down trends
                if (!m_DownTrendPatternCount.ContainsKey(runLength))
                {
                    m_DownTrendPatternCount[runLength] = 0;
                }
                m_DownTrendPatternCount[runLength]++;
                
                // Record if the trend continued
                if (trendContinued)
                {
                    if (!m_DownTrendContinuationCount.ContainsKey(runLength))
                    {
                        m_DownTrendContinuationCount[runLength] = 0;
                    }
                    m_DownTrendContinuationCount[runLength]++;
                }
            }
        }

        protected override void StopCalc()
        {
            // No need to output here as we'll do it at the end of historical calculation
        }
        
        private void OutputTrendContinuationTable()
        {
            // Calculate summary statistics for runs ≥ MinRunLength
            int totalUpPatterns = 0;
            int totalUpContinuations = 0;
            int totalDownPatterns = 0;
            int totalDownContinuations = 0;
            
            for (int length = MinRunLength; length <= 20; length++)
            {
                // Add up all the patterns and continuations for each length
                if (m_UpTrendPatternCount.ContainsKey(length))
                {
                    totalUpPatterns += m_UpTrendPatternCount[length];
                    if (m_UpTrendContinuationCount.ContainsKey(length))
                    {
                        totalUpContinuations += m_UpTrendContinuationCount[length];
                    }
                }
                
                if (m_DownTrendPatternCount.ContainsKey(length))
                {
                    totalDownPatterns += m_DownTrendPatternCount[length];
                    if (m_DownTrendContinuationCount.ContainsKey(length))
                    {
                        totalDownContinuations += m_DownTrendContinuationCount[length];
                    }
                }
            }
            
            // Calculate the overall probabilities
            double upProbability = totalUpPatterns > 0 ? (double)totalUpContinuations / totalUpPatterns * 100 : 0;
            double downProbability = totalDownPatterns > 0 ? (double)totalDownContinuations / totalDownPatterns * 100 : 0;
            double combinedProbability = (totalUpPatterns + totalDownPatterns) > 0 ? 
                (double)(totalUpContinuations + totalDownContinuations) / (totalUpPatterns + totalDownPatterns) * 100 : 0;
            
            // Output the summary box
            Output.WriteLine("");
            Output.WriteLine("╔═════════════════════════════════════════════════════════════════════════════════╗");
            Output.WriteLine("║                    TREND CONTINUATION SUMMARY (RUNS ≥ " + MinRunLength.ToString() + ")                   ║");
            Output.WriteLine("╠═════════════════════════════════════════════════════════════════════════════════╣");
            Output.WriteLine("║ UP TREND:    " + totalUpPatterns.ToString().PadLeft(3) + " patterns, " + 
                              totalUpContinuations.ToString().PadLeft(3) + " continuations, " + 
                              upProbability.ToString("F2").PadLeft(6) + "% probability ║");
            Output.WriteLine("║ DOWN TREND:  " + totalDownPatterns.ToString().PadLeft(3) + " patterns, " + 
                              totalDownContinuations.ToString().PadLeft(3) + " continuations, " + 
                              downProbability.ToString("F2").PadLeft(6) + "% probability ║");
            Output.WriteLine("║ COMBINED:    " + (totalUpPatterns + totalDownPatterns).ToString().PadLeft(3) + " patterns, " + 
                              (totalUpContinuations + totalDownContinuations).ToString().PadLeft(3) + " continuations, " + 
                              combinedProbability.ToString("F2").PadLeft(6) + "% probability ║");
            Output.WriteLine("╚═════════════════════════════════════════════════════════════════════════════════╝");
            Output.WriteLine("");
            
            // Output the detailed table
            Output.WriteLine("╔═════════════════════════════════════════════════════════════════════════════════╗");
            Output.WriteLine("║                      TREND CONTINUATION AFTER PULLBACK PATTERN                  ║");
            Output.WriteLine("╠════════════════╦═════════════════════════════╦═════════════════════════════╣");
            Output.WriteLine("║  Initial Run   ║         UP TREND            ║        DOWN TREND           ║");
            Output.WriteLine("╠════════════════╦═════════════════════════════╦═════════════════════════════╣");
            Output.WriteLine("║    Length     ║ Total | Continued | Prob %  ║ Total | Continued | Prob %  ║");
            Output.WriteLine("╠════════════════╦═════════════════════════════╦═════════════════════════════╣");
            
            // Output the data for each run length from MinRunLength to 20
            for (int length = MinRunLength; length <= 20; length++)
            {
                // Get the counts for up trends
                int upTotal = m_UpTrendPatternCount.ContainsKey(length) ? m_UpTrendPatternCount[length] : 0;
                int upContinued = m_UpTrendContinuationCount.ContainsKey(length) ? m_UpTrendContinuationCount[length] : 0;
                double upProbabilityForLength = upTotal > 0 ? (double)upContinued / upTotal * 100 : 0;
                
                // Get the counts for down trends
                int downTotal = m_DownTrendPatternCount.ContainsKey(length) ? m_DownTrendPatternCount[length] : 0;
                int downContinued = m_DownTrendContinuationCount.ContainsKey(length) ? m_DownTrendContinuationCount[length] : 0;
                double downProbabilityForLength = downTotal > 0 ? (double)downContinued / downTotal * 100 : 0;
                
                // Skip rows where there's no data for either direction
                if (upTotal == 0 && downTotal == 0)
                {
                    continue;
                }
                
                // Format the output row
                string upTotalStr = upTotal.ToString().PadLeft(5);
                string upContinuedStr = upContinued.ToString().PadLeft(9);
                string upProbStr = upProbabilityForLength.ToString("F2").PadLeft(7);
                
                string downTotalStr = downTotal.ToString().PadLeft(5);
                string downContinuedStr = downContinued.ToString().PadLeft(9);
                string downProbStr = downProbabilityForLength.ToString("F2").PadLeft(7);
                
                Output.WriteLine("║      " + length.ToString().PadLeft(2) + "       ║" + upTotalStr + " |" + upContinuedStr + " |" + upProbStr + " ║" + downTotalStr + " |" + downContinuedStr + " |" + downProbStr + " ║");
            }
            
            Output.WriteLine("╚════════════════╩═════════════════════════════╩═════════════════════════════╝");
            
            // Output a description of the pattern
            Output.WriteLine("");
            Output.WriteLine("Pattern Description:");
            Output.WriteLine("1. Initial run of N bars in one direction (establishing a trend)");
            Output.WriteLine("2. Followed by a single bar reversal (against the trend)");
            Output.WriteLine("3. Followed by a reversal back to the original trend direction");
            Output.WriteLine("4. Analysis of whether the next bar continues with the original trend");
            Output.WriteLine("");
            Output.WriteLine("This analysis helps identify the probability of trend continuation");
            Output.WriteLine("after a brief pullback/retracement in the market.");
        }
    }
}
