using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    public class number_of_runs : IndicatorObject
    {
        [Input]
        public int RunLength { get; set; }

        [Input]
        public DateTime TargetDate { get; set; }

        [Input]
        public Color UpRunColor { get; set; }

        [Input]
        public Color DownRunColor { get; set; }

        [Input]
        public int MarkerSize { get; set; }

        [Input]
        public bool ShowStatisticalAnalysis { get; set; }

        [Input]
        public bool DebugMode { get; set; }

        private IPlotObject m_Plot;
        private List<ITrendLineObject> m_RunMarkers;
        private int m_UpRunCount;
        private int m_DownRunCount;
        private int m_TotalRunCount;
        private bool m_ProcessingEnabled;
        private DateTime m_StartTime;
        private DateTime m_EndTime;
        private DateTime m_DaySessionEndTime;

        // Variables to track the current run
        private bool m_InRun;
        private bool m_CurrentRunIsUp;
        private int m_CurrentRunLength;
        private DateTime m_CurrentRunStartTime;
        private double m_CurrentRunStartPrice;

        // Variables for statistical analysis
        private int m_TotalBars;
        private Dictionary<int, int> m_RunLengthDistribution;
        private int m_MaxRunLength;

        public number_of_runs(object ctx) : base(ctx)
        {
            RunLength = 5; // Default to 5 bars in a run
            TargetDate = DateTime.Today; // Default to today
            UpRunColor = Color.Green;
            DownRunColor = Color.Red;
            MarkerSize = 2; // Default marker size in minutes (very small)
            ShowStatisticalAnalysis = true; // Default to showing statistical analysis
            DebugMode = false; // Default to no debug output
        }

        protected override void Create()
        {
            // Create a plot to display the number of runs
            m_Plot = AddPlot(new PlotAttributes("Runs", EPlotShapes.Line, Color.Blue));
            m_RunMarkers = new List<ITrendLineObject>();

            // Initialize counters
            m_UpRunCount = 0;
            m_DownRunCount = 0;
            m_TotalRunCount = 0;
            m_ProcessingEnabled = false;

            // Initialize run tracking
            m_InRun = false;
            m_CurrentRunLength = 0;

            // Initialize statistical analysis
            m_TotalBars = 0;
            m_RunLengthDistribution = new Dictionary<int, int>();
            m_MaxRunLength = 0;
        }

        protected override void StartCalc()
        {
            // Clear any existing markers
            ClearMarkers();

            // Initialize counters
            m_UpRunCount = 0;
            m_DownRunCount = 0;
            m_TotalRunCount = 0;

            // Initialize run tracking
            m_InRun = false;
            m_CurrentRunLength = 0;

            // Initialize statistical analysis
            m_TotalBars = 0;
            m_RunLengthDistribution = new Dictionary<int, int>();
            m_MaxRunLength = 0;

            // Set the start and end times for the target date
            // Start at 6:30 AM on the target date
            m_StartTime = new DateTime(TargetDate.Year, TargetDate.Month, TargetDate.Day, 6, 30, 0);

            // End at 11:59:59 PM on the target date
            m_EndTime = new DateTime(TargetDate.Year, TargetDate.Month, TargetDate.Day, 23, 59, 59);

            // Day session ends at 1:59 PM
            m_DaySessionEndTime = new DateTime(TargetDate.Year, TargetDate.Month, TargetDate.Day, 13, 59, 0);

            // This indicator only works on historical data, not real-time
            m_ProcessingEnabled = !Environment.IsRealTimeCalc;

            if (!m_ProcessingEnabled)
            {
                Output.WriteLine("This indicator is designed for historical analysis only.");
            }
            else
            {
                Output.WriteLine("Analyzing runs for date: " + TargetDate.ToShortDateString());
                Output.WriteLine("Start time: 6:30 AM, End time: 1:59 PM");
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
                // Count total bars for statistical analysis
                m_TotalBars++;

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
                        // The run has ended
                        RecordRun(); // This will check if it meets minimum length and update distribution

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

                // If we've reached the end of the day session, output the summary
                if (Bars.Time[0] >= m_DaySessionEndTime && Bars.Time[0] < m_DaySessionEndTime.AddMinutes(1))
                {
                    // Check if we're in a run
                    if (m_InRun)
                    {
                        // Record the final run
                        RecordRun();
                    }

                    // Output the summary
                    OutputSummary();

                    // Output statistical analysis if enabled
                    if (ShowStatisticalAnalysis)
                    {
                        OutputStatisticalAnalysis();
                    }
                }
            }
        }

        private void StartNewRun(bool isUpBar)
        {
            m_InRun = true;
            m_CurrentRunIsUp = isUpBar;
            m_CurrentRunLength = 1;
            m_CurrentRunStartTime = Bars.Time[0];
            m_CurrentRunStartPrice = Bars.Close[0];
        }

        private void RecordRun()
        {
            // Always update the run length distribution regardless of minimum length
            UpdateRunLengthDistribution(m_CurrentRunLength);

            if (DebugMode)
            {
                string direction = m_CurrentRunIsUp ? "UP" : "DOWN";
                Output.WriteLine("[DEBUG] Run detected - Direction: " + direction +
                    ", Length: " + m_CurrentRunLength +
                    ", Start: " + m_CurrentRunStartTime.ToString("HH:mm:ss") +
                    ", End: " + Bars.Time[0].ToString("HH:mm:ss") +
                    (m_CurrentRunLength >= RunLength ? " (COUNTED)" : " (not counted)"));
            }

            // Only count runs that meet the minimum length criteria for the official count
            if (m_CurrentRunLength >= RunLength)
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

                // Mark the start of the run
                MarkRunStart();

                // Output information about the run
                string direction = m_CurrentRunIsUp ? "UP" : "DOWN";
                Output.WriteLine("Run #" + m_TotalRunCount + " (" + direction + ") - Start: " +
                    m_CurrentRunStartTime.ToString("HH:mm:ss") + ", End: " +
                    Bars.Time[0].ToString("HH:mm:ss") + ", Length: " + m_CurrentRunLength + " bars");
            }

            // Reset the run tracking
            m_InRun = false;
            m_CurrentRunLength = 0;
        }

        private void MarkRunStart()
        {
            try
            {
                // Create a very short horizontal line at the start of the run
                DateTime startTime = m_CurrentRunStartTime;

                // Make the line extremely short - just a small tick mark
                // Use the MarkerSize parameter to control the length in minutes
                DateTime endTime = startTime.AddMinutes(MarkerSize);

                double price = m_CurrentRunStartPrice;

                // Create the marker line
                ChartPoint start = new ChartPoint(startTime, price);
                ChartPoint end = new ChartPoint(endTime, price);

                ITrendLineObject marker = DrwTrendLine.Create(start, end);
                marker.Color = m_CurrentRunIsUp ? UpRunColor : DownRunColor;

                // Add to our list for cleanup
                m_RunMarkers.Add(marker);
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error marking run start: " + ex.Message);
            }
        }

        private void UpdateRunLengthDistribution(int runLength)
        {
            // Update the distribution of run lengths
            if (m_RunLengthDistribution.ContainsKey(runLength))
            {
                m_RunLengthDistribution[runLength]++;
            }
            else
            {
                m_RunLengthDistribution[runLength] = 1;
            }

            // Update the maximum run length observed
            if (runLength > m_MaxRunLength)
            {
                m_MaxRunLength = runLength;
            }
        }

        private void OutputSummary()
        {
            Output.WriteLine("=== Run Analysis Summary ===");
            Output.WriteLine("Date: " + TargetDate.ToShortDateString());
            Output.WriteLine("Time Period: 6:30 AM - 1:59 PM");
            Output.WriteLine("Minimum Run Length: " + RunLength + " bars");
            Output.WriteLine("Up Runs: " + m_UpRunCount);
            Output.WriteLine("Down Runs: " + m_DownRunCount);
            Output.WriteLine("Total Runs: " + m_TotalRunCount);
            Output.WriteLine("Total Bars Analyzed: " + m_TotalBars);
            Output.WriteLine("=========================");
        }

        private void OutputStatisticalAnalysis()
        {
            Output.WriteLine("");
            Output.WriteLine("=== Statistical Analysis ===");
            Output.WriteLine("Comparing observed runs to random (coin-flip) model");
            Output.WriteLine("");

            // Add specific analysis for total runs of length ≥ RunLength (7)
            OutputTotalRunsSignificance();

            // Add analysis of reversal probabilities
            AnalyzeReversalStats();

            // Output the distribution of run lengths
            Output.WriteLine("Run Length Distribution:");

            // Make sure we display all run lengths, including those ≥ RunLength
            int maxLengthToShow = Math.Max(m_MaxRunLength, 20); // Show at least up to length 20 if available

            // Calculate total runs from distribution for verification
            int totalRunsFromDistribution = 0;
            int totalRunsOfMinLengthFromDistribution = 0;

            for (int i = 1; i <= maxLengthToShow; i++)
            {
                int count = m_RunLengthDistribution.ContainsKey(i) ? m_RunLengthDistribution[i] : 0;

                totalRunsFromDistribution += count;
                if (i >= RunLength)
                {
                    totalRunsOfMinLengthFromDistribution += count;
                }

                // Highlight the runs that meet or exceed the minimum run length
                string highlight = (i >= RunLength) ? " " : "";

                // Only show non-zero counts or lengths up to 15 to avoid cluttering the output
                if (count > 0 || i <= 15)
                {
                    Output.WriteLine("  Length " + i + ": " + count + " runs" + highlight);
                }
            }
            Output.WriteLine("");

            // Detailed verification of counts
            Output.WriteLine("=== Count Verification ===");
            Output.WriteLine("Total runs (all lengths): " + totalRunsFromDistribution);
            Output.WriteLine("Total runs ≥ " + RunLength + " bars: " + totalRunsOfMinLengthFromDistribution);
            Output.WriteLine("Reported Up Runs: " + m_UpRunCount);
            Output.WriteLine("Reported Down Runs: " + m_DownRunCount);
            Output.WriteLine("Reported Total Runs: " + m_TotalRunCount);

            // Check for discrepancies
            if (totalRunsOfMinLengthFromDistribution != m_TotalRunCount)
            {
                Output.WriteLine("WARNING: Distribution count (" + totalRunsOfMinLengthFromDistribution +
                    ") does not match total runs count (" + m_TotalRunCount + ")");
            }
            Output.WriteLine("");

            // Calculate and output random walk probabilities
            Output.WriteLine("=== Random Walk Probabilities ===");
            Output.WriteLine("Probability of observing specific run lengths in a random walk (coin-flip) model:");
            Output.WriteLine("");

            // Table header
            Output.WriteLine(string.Format("{0,-10} {1,-15} {2,-15} {3,-15} {4,-15}",
                "Length", "Prob. of Run", "Expected Runs", "Observed Runs", "P-value"));

            // For each run length of interest
            for (int k = 1; k <= Math.Min(maxLengthToShow, 20); k++)
            {
                // Probability of a single run of exactly length k in a random walk
                double probExactRun = 1.0 / Math.Pow(2, k);

                // Expected number of runs of exactly length k
                double expectedExactRuns = (m_TotalBars - k + 1) * probExactRun;

                // Observed number of runs of exactly length k
                int observedExactRuns = m_RunLengthDistribution.ContainsKey(k) ? m_RunLengthDistribution[k] : 0;

                // Calculate p-value using Poisson approximation
                double pValue = CalculateExactPValue(expectedExactRuns, observedExactRuns);

                // Format the output with significance indicators
                string significance = pValue < 0.01 ? "**" : (pValue < 0.05 ? "*" : "");

                Output.WriteLine(string.Format("{0,-10} {1,-15:P6} {2,-15:F2} {3,-15} {4,-15:E6}{5}",
                    k, probExactRun, expectedExactRuns, observedExactRuns, pValue, significance));
            }

            Output.WriteLine("");
            Output.WriteLine("=== Cumulative Probabilities (Length ≥ K) ===");
            Output.WriteLine("Probability of observing runs of length K or longer in a random walk:");
            Output.WriteLine("");

            // Table header for cumulative probabilities
            Output.WriteLine(string.Format("{0,-10} {1,-15} {2,-15} {3,-15} {4,-15}",
                "Length ≥", "Cum. Prob.", "Expected Runs", "Observed Runs", "P-value"));

            // Calculate and output expected runs for different lengths
            for (int k = RunLength; k <= Math.Min(maxLengthToShow, 15); k++)
            {
                // Calculate expected number of runs of length k or longer in a random sequence
                double expectedRuns = CalculateExpectedRuns(m_TotalBars, k);

                // Count observed runs of length k or longer
                int observedRuns = CountRunsOfLengthOrLonger(k);

                // Calculate cumulative probability
                double cumulativeProbability = expectedRuns / (double)m_TotalBars;

                // Calculate p-value (probability of observing this many runs by chance)
                double pValue = CalculatePValue(m_TotalBars, k, observedRuns);

                // Format the output
                string significance = pValue < 0.01 ? "**" : (pValue < 0.05 ? "*" : "");

                Output.WriteLine(string.Format("{0,-10} {1,-15:P6} {2,-15:F2} {3,-15} {4,-15:E6}{5}",
                    k, cumulativeProbability, expectedRuns, observedRuns, pValue, significance));
            }

            Output.WriteLine("");
            Output.WriteLine("Significance: * p<0.05, ** p<0.01");

            // Add interpretation
            Output.WriteLine("");
            Output.WriteLine("Interpretation:");

            // Calculate overall randomness score
            double randomnessScore = CalculateRandomnessScore();

            if (randomnessScore > 2.0)
            {
                Output.WriteLine("The price movement shows STRONG NON-RANDOM patterns.");
                Output.WriteLine("There are significantly more runs than expected by chance.");
                Output.WriteLine("This suggests potential mean-reversion behavior.");
            }
            else if (randomnessScore > 1.5)
            {
                Output.WriteLine("The price movement shows MODERATE NON-RANDOM patterns.");
                Output.WriteLine("There are more runs than expected by chance.");
            }
            else if (randomnessScore > 0.67)
            {
                Output.WriteLine("The price movement appears MOSTLY RANDOM.");
                Output.WriteLine("The number of runs is close to what would be expected by chance.");
            }
            else if (randomnessScore > 0.5)
            {
                Output.WriteLine("The price movement shows MODERATE TRENDING behavior.");
                Output.WriteLine("There are fewer runs than expected by chance.");
            }
            else
            {
                Output.WriteLine("The price movement shows STRONG TRENDING behavior.");
                Output.WriteLine("There are significantly fewer runs than expected by chance.");
            }

            Output.WriteLine("=========================");
        }

        private void OutputTotalRunsSignificance()
        {
            // Count total runs of length RunLength (7) or greater
            int totalLongRuns = CountRunsOfLengthOrLonger(RunLength);

            // Calculate expected number of such runs in a random walk
            double expectedLongRuns = CalculateExpectedRuns(m_TotalBars, RunLength);

            // Calculate the ratio of observed to expected
            double ratio = totalLongRuns / expectedLongRuns;

            // Calculate the p-value using the Poisson distribution
            double pValue = CalculatePValue(m_TotalBars, RunLength, totalLongRuns);

            // Output the results in a highlighted box
            Output.WriteLine("════════════════════════════════════════════════════════════╗");
            Output.WriteLine("║                STATISTICAL SIGNIFICANCE                    ║");
            Output.WriteLine("╠════════════════════════════════════════════════════════════╣");
            Output.WriteLine(string.Format("║ Total Bars Analyzed:                {0,-24} ║", m_TotalBars));
            Output.WriteLine(string.Format("║ Minimum Run Length:                 {0,-24} ║", RunLength));
            Output.WriteLine(string.Format("║ Observed Runs ≥ {0}:                  {1,-24} ║", RunLength, totalLongRuns));
            Output.WriteLine(string.Format("║ Expected Runs ≥ {0} (Random Walk):    {1,-24:F2} ║", RunLength, expectedLongRuns));
            Output.WriteLine(string.Format("║ Ratio (Observed/Expected):          {0,-24:F2} ║", ratio));
            Output.WriteLine(string.Format("║ P-value:                            {0,-24:E6} ║", pValue));

            // Add interpretation
            Output.WriteLine("║                                                            ║");
            if (pValue < 0.0001)
            {
                Output.WriteLine("║ Interpretation: EXTREMELY SIGNIFICANT                      ║");
                Output.WriteLine("║ The probability of observing this many runs by random      ║");
                Output.WriteLine("║ chance is less than 0.01%.                                 ║");
            }
            else if (pValue < 0.01)
            {
                Output.WriteLine("║ Interpretation: HIGHLY SIGNIFICANT                         ║");
                Output.WriteLine("║ The probability of observing this many runs by random      ║");
                Output.WriteLine("║ chance is less than 1%.                                    ║");
            }
            else if (pValue < 0.05)
            {
                Output.WriteLine("║ Interpretation: SIGNIFICANT                                ║");
                Output.WriteLine("║ The probability of observing this many runs by random      ║");
                Output.WriteLine("║ chance is less than 5%.                                    ║");
            }
            else
            {
                Output.WriteLine("║ Interpretation: NOT SIGNIFICANT                            ║");
                Output.WriteLine("║ The observed number of runs is consistent with what        ║");
                Output.WriteLine("║ would be expected in a random walk.                        ║");
            }
            Output.WriteLine("════════════════════════════════════════════════════════════╝");
            Output.WriteLine("");
        }

        private void AnalyzeReversalStats()
        {
            // This method analyzes the probability of a reversal after X consecutive bars in the same direction
            Output.WriteLine("=== Reversal Probability Analysis ===");
            Output.WriteLine("Probability of a reversal after consecutive bars in the same direction:");
            Output.WriteLine("");

            // Create dictionaries to track:
            // 1. Count of occurrences of X consecutive bars in same direction
            // 2. Count of those that were followed by a reversal
            Dictionary<int, int> upSequenceCount = new Dictionary<int, int>();
            Dictionary<int, int> upSequenceReversals = new Dictionary<int, int>();
            Dictionary<int, int> downSequenceCount = new Dictionary<int, int>();
            Dictionary<int, int> downSequenceReversals = new Dictionary<int, int>();

            // Initialize dictionaries for lengths 1-7
            for (int i = 1; i <= 7; i++)
            {
                upSequenceCount[i] = 0;
                upSequenceReversals[i] = 0;
                downSequenceCount[i] = 0;
                downSequenceReversals[i] = 0;
            }

            // Analyze the data
            bool currentlyUp = false;
            int currentRunLength = 0;

            // Use the total bars we've already counted in the CalcBar method
            // Skip the first bar as we need a reference point
            for (int i = 1; i < m_TotalBars; i++)
            {
                bool isUp = Bars.Close[i] >= Bars.Close[i - 1];

                if (i == 1)
                {
                    // Initialize on the second bar
                    currentlyUp = isUp;
                    currentRunLength = 1;
                    continue;
                }

                if (isUp == currentlyUp)
                {
                    // Continuing the current run
                    currentRunLength++;
                }
                else
                {
                    // A reversal has occurred
                    // Record the sequence that just ended
                    if (currentRunLength <= 7)
                    {
                        if (currentlyUp)
                        {
                            upSequenceCount[currentRunLength]++;
                            upSequenceReversals[currentRunLength]++;
                        }
                        else
                        {
                            downSequenceCount[currentRunLength]++;
                            downSequenceReversals[currentRunLength]++;
                        }
                    }

                    // Start a new run
                    currentlyUp = isUp;
                    currentRunLength = 1;
                }

                // If we're not at the last bar, check if we should increment the sequence count
                if (i < m_TotalBars - 1)
                {
                    if (currentRunLength <= 7)
                    {
                        if (currentlyUp)
                        {
                            upSequenceCount[currentRunLength]++;
                        }
                        else
                        {
                            downSequenceCount[currentRunLength]++;
                        }
                    }
                }
            }

            // Output the results in a table
            Output.WriteLine(string.Format("{0,-10} {1,-15} {2,-15} {3,-15} {4,-15} {5,-15}",
                "Length", "Up Sequences", "Up Reversals", "Up Rev %", "Down Reversals", "Down Rev %"));

            for (int i = 1; i <= 7; i++)
            {
                double upReversalPct = upSequenceCount[i] > 0 ?
                    (double)upSequenceReversals[i] / upSequenceCount[i] * 100 : 0;
                double downReversalPct = downSequenceCount[i] > 0 ?
                    (double)downSequenceReversals[i] / downSequenceCount[i] * 100 : 0;

                Output.WriteLine(string.Format("{0,-10} {1,-15} {2,-15} {3,-15:F2}% {4,-15} {5,-15:F2}%",
                    i, upSequenceCount[i], upSequenceReversals[i], upReversalPct,
                    downSequenceReversals[i], downReversalPct));
            }

            // Add combined statistics (both up and down)
            Output.WriteLine("");
            Output.WriteLine("Combined Statistics (Up and Down):");
            Output.WriteLine(string.Format("{0,-10} {1,-15} {2,-15} {3,-15} {4,-15}",
                "Length", "Total Sequences", "Total Reversals", "Reversal %", "Random Expectation"));

            for (int i = 1; i <= 7; i++)
            {
                int totalSequences = upSequenceCount[i] + downSequenceCount[i];
                int totalReversals = upSequenceReversals[i] + downSequenceReversals[i];
                double reversalPct = totalSequences > 0 ?
                    (double)totalReversals / totalSequences * 100 : 0;

                // In a random walk, the expectation is always 50%
                double randomExpectation = 50.0;

                Output.WriteLine(string.Format("{0,-10} {1,-15} {2,-15} {3,-15:F2}% {4,-15:F2}%",
                    i, totalSequences, totalReversals, reversalPct, randomExpectation));
            }

            Output.WriteLine("");
            Output.WriteLine("Interpretation:");
            Output.WriteLine("- In a random walk, the probability of reversal is always 50%");
            Output.WriteLine("- Values < 50% indicate trend continuation tendency");
            Output.WriteLine("- Values > 50% indicate mean-reversion tendency");
            Output.WriteLine("=========================");
        }

        private double CalculateExpectedRuns(int totalBars, int runLength)
        {
            // In a random sequence, the expected number of runs of length k or longer is:
            // E(runs ≥ k) ≈ (N - k + 1) / 2^k
            // where N is the total number of bars

            if (totalBars < runLength)
                return 0;

            return (totalBars - runLength + 1) / Math.Pow(2, runLength);
        }

        private int CountRunsOfLengthOrLonger(int minLength)
        {
            int count = 0;
            for (int i = minLength; i <= m_MaxRunLength; i++)
            {
                if (m_RunLengthDistribution.ContainsKey(i))
                {
                    count += m_RunLengthDistribution[i];
                }
            }
            return count;
        }

        private double CalculatePValue(int totalBars, int runLength, int observedRuns)
        {
            // Calculate the probability of observing this many runs or more by chance
            // This is a simplified approximation using the Poisson distribution

            double lambda = CalculateExpectedRuns(totalBars, runLength);

            // If observed is less than expected, return 1 (not significant)
            if (observedRuns <= lambda)
                return 1.0;

            try
            {
                // Calculate the Poisson cumulative distribution function (CDF)
                // P(X ≥ observed) = 1 - P(X < observed)
                double cdf = 0;

                // Limit the calculation to avoid overflow
                int maxIterations = Math.Min(observedRuns, 100); // Prevent excessive iterations

                for (int i = 0; i < maxIterations; i++)
                {
                    double term = SafePoissonTerm(lambda, i);
                    if (double.IsInfinity(term) || double.IsNaN(term))
                        break;

                    cdf += term;

                    // If we've accumulated enough probability, stop
                    if (cdf > 0.9999)
                        break;
                }

                return Math.Max(0.0, Math.Min(1.0, 1.0 - cdf)); // Ensure result is between 0 and 1
            }
            catch (Exception)
            {
                // If there's an overflow or other arithmetic error, return a conservative p-value
                return 0.001; // Indicate statistical significance but not extreme
            }
        }

        private double CalculateExactPValue(double lambda, int observed)
        {
            // Calculate the probability of observing exactly k events in a Poisson distribution
            // For a two-tailed test, we need to find the probability of observing a value as extreme or more extreme than observed

            if (observed == lambda)
                return 1.0; // Observed equals expected, p-value is 1

            try
            {
                // Determine if we're looking at the upper or lower tail
                bool upperTail = observed > lambda;

                double pValue = 0;

                if (upperTail)
                {
                    // Sum P(X ≥ observed)
                    int maxIterations = observed + 100; // Limit to avoid excessive iterations

                    for (int i = observed; i <= maxIterations; i++)
                    {
                        double term = SafePoissonTerm(lambda, i);
                        if (double.IsInfinity(term) || double.IsNaN(term))
                            break;

                        pValue += term;

                        // Stop if we've accumulated enough probability
                        if (pValue > 0.9999)
                            break;
                    }
                }
                else
                {
                    // Sum P(X ≤ observed)
                    for (int i = 0; i <= observed; i++)
                    {
                        double term = SafePoissonTerm(lambda, i);
                        if (double.IsInfinity(term) || double.IsNaN(term))
                            break;

                        pValue += term;

                        // Stop if we've accumulated enough probability
                        if (pValue > 0.9999)
                            break;
                    }
                }

                return Math.Max(0.0, Math.Min(1.0, pValue)); // Ensure result is between 0 and 1
            }
            catch (Exception)
            {
                // If there's an overflow or other arithmetic error, return a conservative p-value
                return 0.001; // Indicate statistical significance but not extreme
            }
        }

        private double SafePoissonTerm(double lambda, int k)
        {
            try
            {
                // Calculate log(lambda^k * e^-lambda / k!) to avoid overflow
                // log(lambda^k * e^-lambda / k!) = k*log(lambda) - lambda - log(k!)

                double logTerm = k * Math.Log(lambda) - lambda - LogFactorial(k);
                return Math.Exp(logTerm);
            }
            catch (Exception)
            {
                return 0.0; // Return 0 on error
            }
        }

        private double LogFactorial(int n)
        {
            // Calculate log(n!) using Stirling's approximation for large n
            if (n <= 1)
                return 0.0;

            if (n <= 20)
            {
                // For small n, calculate directly
                double result = 0.0;
                for (int i = 2; i <= n; i++)
                {
                    result += Math.Log(i);
                }
                return result;
            }
            else
            {
                // Stirling's approximation: log(n!) ≈ n*log(n) - n + 0.5*log(2*π*n)
                return n * Math.Log(n) - n + 0.5 * Math.Log(2 * Math.PI * n);
            }
        }

        private double PoissonPMF(double lambda, int k)
        {
            // Use the safer implementation
            return SafePoissonTerm(lambda, k);
        }

        private double Factorial(int n)
        {
            // Use a safer implementation that avoids overflow
            if (n <= 1)
                return 1.0;

            if (n > 20)
            {
                // For large n, use Stirling's approximation
                return Math.Exp(LogFactorial(n));
            }

            double result = 1.0;
            for (int i = 2; i <= n; i++)
            {
                result *= i;

                // Check for overflow
                if (double.IsInfinity(result))
                    return double.MaxValue;
            }
            return result;
        }

        private double CalculateRandomnessScore()
        {
            // Calculate a score that represents how random vs. non-random the price movement is
            // Higher values indicate more runs than expected (potential mean reversion)
            // Lower values indicate fewer runs than expected (potential trending)

            // Use runs of length RunLength as the baseline
            double expected = CalculateExpectedRuns(m_TotalBars, RunLength);
            int observed = CountRunsOfLengthOrLonger(RunLength);

            if (expected == 0)
                return 1.0; // Default to neutral if we can't calculate

            return observed / expected;
        }

        private void ClearMarkers()
        {
            try
            {
                if (m_RunMarkers != null)
                {
                    foreach (var marker in m_RunMarkers)
                    {
                        try
                        {
                            marker.Delete();
                        }
                        catch
                        {
                            // Ignore errors during cleanup
                        }
                    }
                    m_RunMarkers.Clear();
                }
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }
    }
}
