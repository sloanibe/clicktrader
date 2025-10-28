using System;
using System.Drawing;
using System.Collections.Generic;
using System.Windows.Forms;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true), MouseEvents(true)]
    public class renko_tail_signals : IndicatorObject
    {
        [Input]
        public int FastMALength { get; set; }

        [Input]
        public int SlowMALength { get; set; }

        [Input]
        public double MinTailSizeTicks { get; set; }

        [Input]
        public int SlopeLookback { get; set; }

        [Input]
        public double MinMASeparationTicks { get; set; }

        [Input]
        public int DirectionalBarsLookback { get; set; }

        [Input]
        public double MinDirectionalPct { get; set; }

        [Input]
        public double MinVolumeMultiplier { get; set; }

        [Input]
        public double MinRangeMultiplier { get; set; }

        [Input]
        public int CVDLookback { get; set; }

        [Input]
        public bool UseCVDFilter { get; set; }

        [Input]
        public bool UseVolumePatternFilter { get; set; }

        [Input]
        public double FastMAMinSlopeAngle { get; set; }  // Minimum slope angle in degrees for fast MA

        [Input]
        public double SlowMAMinSlopeAngle { get; set; }  // Minimum slope angle in degrees for slow MA

        [Input]
        public bool UseSlowMAAutoSignal { get; set; }  // Auto-select signals when bar reaches 20 MA with steep slope

        [Input]
        public double SlowMAAutoSlopeAngle { get; set; }  // Minimum slope angle for auto-signal (typically higher than normal)

        [Input]
        public double MinTailLengthTicks { get; set; }  // Minimum tail length in ticks for auto-signal

        [Input]
        public bool ExportSignalData { get; set; }

        [Input]
        public string ExportFilePath { get; set; }

        [Input]
        public bool ShowBarNumbers { get; set; }

        [Input]
        public string ClickExportFolder { get; set; }

        private IPlotObject m_BullishTail;
        private IPlotObject m_BearishTail;
        private IPlotObject m_TrendIndicator;
        private IPlotObject m_FastMAPlot;
        private IPlotObject m_SlowMAPlot;
        private XAverage m_FastMA;
        private XAverage m_SlowMA;
        private bool m_ExportHeaderWritten;
        private DateTime m_LastReportedDate;
        private int m_DayCounter;

        // Store signal data for click export
        private class SignalData
        {
            public DateTime Time;
            public string SignalType;
            public double Open, High, Low, Close;
            public double FastMA, SlowMA;
            public double FastMASlope, SlowMASlope;
            public double MASeparation;
            public double BullPct, BearPct;
            public double VolumeRatio, RangeRatio;
            public double CVDSlope;
            public bool StrongBullTrend, StrongBearTrend;
            public double PrevBodyTop, PrevBodyBottom;
            public double PullbackVolume, TrendVolume;  // For volume story
            public double TailBarVolume;  // Current bar volume
        }

        private Dictionary<int, SignalData> m_SignalsByBar;
        private Dictionary<DateTime, SignalData> m_SignalsByTime;

        // Signal clustering filter
        private double m_LastSignalHigh;
        private double m_LastSignalLow;
        private bool m_HasLastSignal;
        private int m_BarssSinceLastSignal;
        private int m_MinBarsBetweenSignals = 5;  // Minimum bars between signals

        // Signal validation tracking
        private class SignalValidation
        {
            public int BarNumber;
            public DateTime SignalTime;
            public string SignalType;  // "BullishTail" or "BearishTail"
            public double SignalBarHigh;
            public double SignalBarLow;
            public double TailHigh;  // Invalidation level (bearish tail high)
            public double TailLow;   // Invalidation level (bullish tail low)
            public string Status;    // "Pending", "Success", "Failed"
            public int PlotIndex;  // Index to identify which plot (0=bullish, 1=bearish)
            public ITextObject BarNumberText;  // Reference to bar number text for updating
        }

        private Dictionary<int, SignalValidation> m_ActiveSignals;  // Signals awaiting validation
        private List<ITextObject> m_PopupTexts;  // Store popup text objects to keep them persistent
        private int m_TotalSignalsGenerated = 0;
        private int m_SuccessfulSignals = 0;
        private int m_FailedSignals = 0;

        public renko_tail_signals(object ctx) : base(ctx)
        {
            FastMALength = 10;
            SlowMALength = 20;
            MinTailSizeTicks = 2;
            SlopeLookback = 10;  // Check slope over 10 bars for sustained movement
            MinMASeparationTicks = 8;  // MAs must be separated
            DirectionalBarsLookback = 10;
            MinDirectionalPct = 50;  // Require 5 of 10 bars directional
            MinVolumeMultiplier = 1.0;  // Require average volume
            MinRangeMultiplier = 1.0;  // Require average range
            CVDLookback = 10;
            UseCVDFilter = false;  // Disable CVD filtering for now
            UseVolumePatternFilter = false;  // Disable volume pattern filtering for now
            FastMAMinSlopeAngle = 45;  // Default to 45 degrees for fast MA
            SlowMAMinSlopeAngle = 30;  // Default to 30 degrees for slow MA
            UseSlowMAAutoSignal = false;  // Disabled by default
            SlowMAAutoSlopeAngle = 60;  // Require 60 degrees for auto-signal (very steep)
            MinTailLengthTicks = 0.5;  // Require at least 0.5 ticks of tail (adjust as needed)
            ExportSignalData = false;
            ExportFilePath = @"C:\Users\mark\tail_signals_debug\signals.csv";
            ShowBarNumbers = true;  // Show bar numbers next to signals for reference
            ClickExportFolder = @"C:\Users\mark\tail_signals_debug\clicked";

            // Initialize clustering filter
            m_HasLastSignal = false;
            m_LastSignalHigh = 0;
            m_LastSignalLow = 0;
            m_BarssSinceLastSignal = 0;

            // Initialize validation tracking
            m_ActiveSignals = new Dictionary<int, SignalValidation>();
            m_PopupTexts = new List<ITextObject>();
            m_TotalSignalsGenerated = 0;
            m_SuccessfulSignals = 0;
            m_FailedSignals = 0;
        }

        protected override void Create()
        {
            // XAverage is MultiCharts' built-in Exponential Moving Average function
            m_FastMA = new XAverage(this);
            m_SlowMA = new XAverage(this);
            m_SignalsByBar = new Dictionary<int, SignalData>();
            m_SignalsByTime = new Dictionary<DateTime, SignalData>();

            // Dark blue for bullish tails, dark red for bearish tails
            m_BullishTail = AddPlot(new PlotAttributes("BullTail", EPlotShapes.Point,
                Color.DarkBlue, Color.Empty, 10, 0, true));
            m_BearishTail = AddPlot(new PlotAttributes("BearTail", EPlotShapes.Point,
                Color.DarkRed, Color.Empty, 10, 0, true));

            // Moving averages - 10 MA (green), 20 MA (black)
            m_FastMAPlot = AddPlot(new PlotAttributes("10MA", EPlotShapes.Line,
                Color.Green, Color.Empty, 2, 0, true));
            m_SlowMAPlot = AddPlot(new PlotAttributes("20MA", EPlotShapes.Line,
                Color.Black, Color.Empty, 2, 0, true));

            // Trend indicator - disabled to prevent scaling issues
            // m_TrendIndicator = AddPlot(new PlotAttributes("Trend", EPlotShapes.Line,
            //     Color.Gray, Color.Empty, 1, 0, true));
        }

        protected override void StartCalc()
        {
            // Configure the exponential moving averages
            m_FastMA.Price = Bars.Close;
            m_FastMA.Length = FastMALength;
            m_SlowMA.Price = Bars.Close;
            m_SlowMA.Length = SlowMALength;
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            // Check if the clicked bar has a signal using time lookup
            if (m_SignalsByTime.ContainsKey(arg.point.Time))
            {
                var signalData = m_SignalsByTime[arg.point.Time];

                // Draw popup on chart showing signal attributes
                DrawSignalPopup(arg.point.Time, signalData);

                // Also export to CSV
                ExportSignalDetails(arg.bar_number, signalData);
                Output.WriteLine(string.Format("✓ Exported {0} signal at {1:yyyy-MM-dd HH:mm:ss}",
                    signalData.SignalType, arg.point.Time));
            }
            else
            {
                // No signal exists, but generate analysis for this bar anyway
                ExportBarAnalysis(arg.bar_number, arg.point.Time);
                Output.WriteLine(string.Format("✓ Exported bar analysis (no signal) at {0:yyyy-MM-dd HH:mm:ss} (bar {1})",
                    arg.point.Time, arg.bar_number));
            }
        }

        private void DrawSignalPopup(DateTime signalTime, SignalData data)
        {
            try
            {
                // Find the price level for the popup (above bullish, below bearish)
                double popupPrice = data.SignalType == "BullishTail" ?
                    data.High + (Bars.Info.MinMove / Bars.Info.PriceScale * 20) :
                    data.Low - (Bars.Info.MinMove / Bars.Info.PriceScale * 20);

                // Convert slopes to degrees for human readability
                // Slope in ticks/bar -> convert to degrees using arctan
                double maxSlope = Math.Max(Math.Abs(data.FastMASlope), Math.Abs(data.SlowMASlope));
                double slopeDegrees = Math.Atan(maxSlope) * (180.0 / Math.PI);

                // Determine which MA is driving the signal
                string maSource = Math.Abs(data.FastMASlope) > Math.Abs(data.SlowMASlope) ? "10MA" : "20MA";

                // Determine trend direction
                string trendDir = data.SignalType == "BullishTail" ? "BULLISH" : "BEARISH";

                // Calculate volume story
                double pullbackToTrendRatio = data.TrendVolume > 0 ? data.PullbackVolume / data.TrendVolume : 0;
                double tailToTrendRatio = data.TrendVolume > 0 ? data.TailBarVolume / data.TrendVolume : 0;

                // Create human-readable popup text with volume story
                string popupText = string.Format(
                    "{0}\n" +
                    "{1} ({2:F0}°)\n" +
                    "MAs: {3:F1}t apart | Trend: {4:F0}%\n" +
                    "Vol Story: Pullback {5:F2}x → Tail {6:F2}x",
                    trendDir,
                    maSource,
                    slopeDegrees,
                    data.MASeparation,
                    data.BullPct > data.BearPct ? data.BullPct : data.BearPct,
                    pullbackToTrendRatio,
                    tailToTrendRatio
                );

                // Draw the popup text on the chart
                var textObj = DrwText.Create(new ChartPoint(signalTime, popupPrice), popupText);
                textObj.Color = data.SignalType == "BullishTail" ? Color.Blue : Color.Red;
                textObj.VStyle = ETextStyleV.Above;  // Position text above the point
                
                // Store the text object to keep it persistent
                m_PopupTexts.Add(textObj);
            }
            catch (Exception)
            {
                // Silently fail on popup draw errors
            }
        }

        private void ExportSignalDetails(int barNumber, SignalData data)
        {
            try
            {
                // Create folder if it doesn't exist
                if (!System.IO.Directory.Exists(ClickExportFolder))
                {
                    System.IO.Directory.CreateDirectory(ClickExportFolder);
                    Output.WriteLine("Created directory: " + ClickExportFolder);
                }

                // Create unique filename with timestamp
                string filename = string.Format("signal_{0}_{1:yyyyMMdd_HHmmss}.csv",
                    data.SignalType, data.Time);
                string filepath = System.IO.Path.Combine(ClickExportFolder, filename);
                
                Output.WriteLine("Attempting to export signal to: " + filepath);

                // Build detailed CSV content
                var lines = new List<string>();
                lines.Add("SIGNAL DETAILS");
                lines.Add("Category,Value");
                lines.Add(string.Format("BarNumber,{0}", barNumber));
                lines.Add(string.Format("SignalType,{0}", data.SignalType));
                lines.Add(string.Format("DateTime,{0:yyyy-MM-dd HH:mm:ss}", data.Time));
                lines.Add("");
                lines.Add("PRICE DATA");
                lines.Add("Category,Value");
                lines.Add(string.Format("Open,{0:F2}", data.Open));
                lines.Add(string.Format("High,{0:F2}", data.High));
                lines.Add(string.Format("Low,{0:F2}", data.Low));
                lines.Add(string.Format("Close,{0:F2}", data.Close));
                lines.Add(string.Format("PrevBodyTop,{0:F2}", data.PrevBodyTop));
                lines.Add(string.Format("PrevBodyBottom,{0:F2}", data.PrevBodyBottom));
                lines.Add("");
                lines.Add("MOVING AVERAGES");
                lines.Add("Category,Value");
                lines.Add(string.Format("FastMA(10),{0:F2}", data.FastMA));
                lines.Add(string.Format("SlowMA(20),{0:F2}", data.SlowMA));
                lines.Add(string.Format("MASeparation(ticks),{0:F2}", data.MASeparation));
                lines.Add(string.Format("FastMASlope(ticks/bar),{0:F4}", data.FastMASlope));
                lines.Add(string.Format("SlowMASlope(ticks/bar),{0:F4}", data.SlowMASlope));
                lines.Add("");
                lines.Add("DIRECTIONAL BIAS");
                lines.Add("Category,Value");
                lines.Add(string.Format("BullishBarsPct,{0:F1}%", data.BullPct));
                lines.Add(string.Format("BearishBarsPct,{0:F1}%", data.BearPct));
                lines.Add("");
                lines.Add("VOLUME & RANGE");
                lines.Add("Category,Value");
                lines.Add(string.Format("VolumeRatio,{0:F2}x", data.VolumeRatio));
                lines.Add(string.Format("RangeRatio,{0:F2}x", data.RangeRatio));
                lines.Add("");
                lines.Add("CVD");
                lines.Add("Category,Value");
                lines.Add(string.Format("CVDSlope,{0:F2}", data.CVDSlope));
                lines.Add("");
                lines.Add("TREND STATUS");
                lines.Add("Category,Value");
                lines.Add(string.Format("StrongBullTrend,{0}", data.StrongBullTrend));
                lines.Add(string.Format("StrongBearTrend,{0}", data.StrongBearTrend));
                lines.Add("");
                lines.Add("FILTER THRESHOLDS");
                lines.Add("Category,Value");
                lines.Add(string.Format("MinMASeparation,{0} ticks", MinMASeparationTicks));
                lines.Add(string.Format("MinFastMASlope,2.5 ticks/bar"));
                lines.Add(string.Format("MinSlowMASlope,1.5 ticks/bar (60% of fast)"));
                lines.Add(string.Format("MinDirectionalPct,{0}%", MinDirectionalPct));
                lines.Add(string.Format("MinVolumeMultiplier,{0}x", MinVolumeMultiplier));
                lines.Add(string.Format("MinRangeMultiplier,{0}x", MinRangeMultiplier));
                lines.Add(string.Format("SlopeLookback,{0} bars", SlopeLookback));

                System.IO.File.WriteAllLines(filepath, lines.ToArray());

                Output.WriteLine("SUCCESS: Exported signal details to: " + filepath);
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error exporting clicked signal: " + ex.Message);
            }
        }

        private void ExportBarAnalysis(int barNumber, DateTime barTime)
        {
            int barsAgo = Bars.CurrentBar - barNumber;
            
            Output.WriteLine(string.Format("ExportBarAnalysis: barNumber={0}, currentBar={1}, barsAgo={2}", barNumber, Bars.CurrentBar, barsAgo));

            // Validate the bar
            if (!ValidateBarForAnalysis(barNumber, barsAgo))
            {
                Output.WriteLine("Validation failed - aborting analysis");
                return;
            }

            // Calculate metrics for this bar
            var metrics = CalculateBarMetrics(barsAgo);
            if (metrics == null)
            {
                Output.WriteLine("Metrics calculation failed - aborting analysis");
                return;
            }
            
            Output.WriteLine("Metrics calculated successfully - proceeding with analysis");

            // Print detailed analysis to console
            try
            {
                PrintBarAnalysisToConsole(barNumber, barTime, barsAgo, metrics);
            }
            catch (Exception ex)
            {
                Output.WriteLine("Exception in PrintBarAnalysisToConsole: " + ex.Message + " | " + ex.StackTrace);
            }

            // Export to CSV
            try
            {
                ExportBarMetricsToCSV(barNumber, barTime, barsAgo, metrics);
            }
            catch (Exception ex)
            {
                Output.WriteLine("Exception in ExportBarMetricsToCSV: " + ex.Message + " | " + ex.StackTrace);
            }

            // Draw popup on chart
            try
            {
                DrawBarAnalysisPopup(barsAgo, metrics.FastMA, metrics.SlowMA, metrics.FastMASlope, 
                    metrics.SlowMASlope, metrics.MASeparation, metrics.BullPct, metrics.VolumeRatio, 
                    metrics.RangeRatio, metrics.TickSize);
            }
            catch (Exception ex)
            {
                Output.WriteLine("Exception in DrawBarAnalysisPopup: " + ex.Message);
            }
        }

        private bool ValidateBarForAnalysis(int barNumber, int barsAgo)
        {
            int minBarsNeeded = Math.Max(FastMALength, SlowMALength) + SlopeLookback + 2;

            if (barsAgo < 0)
            {
                Output.WriteLine("Bar is in the future - cannot analyze");
                return false;
            }

            if (barNumber < minBarsNeeded)
            {
                Output.WriteLine(string.Format("Bar too early in history - need at least {0} bars loaded", minBarsNeeded));
                return false;
            }

            if (barsAgo >= Bars.CurrentBar)
            {
                Output.WriteLine("Bar index out of range");
                return false;
            }

            return true;
        }

        private class BarMetrics
        {
            public double FastMA, SlowMA, FastMASlope, SlowMASlope, MASeparation;
            public double BullPct, BearPct, VolumeRatio, RangeRatio, CVDSlope;
            public double TickSize;
            public bool StrongBullTrend, StrongBearTrend;
            public double PrevBodyTop, PrevBodyBottom;
        }

        private BarMetrics CalculateBarMetrics(int barsAgo)
        {
            try
            {
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                double fastMA, slowMA, fastMASlope, slowMASlope;

                // Get MA values
                try
                {
                    // Check if we have enough history for this bar
                    if (barsAgo < 0 || barsAgo >= Bars.CurrentBar)
                    {
                        Output.WriteLine(string.Format("Bar index out of range: barsAgo={0}, CurrentBar={1}", barsAgo, Bars.CurrentBar));
                        return null;
                    }
                    
                    // Try to get from indicator first (for recent bars)
                    try
                    {
                        fastMA = m_FastMA[barsAgo];
                        slowMA = m_SlowMA[barsAgo];
                        Output.WriteLine(string.Format("Got MA values from indicator: FastMA={0:F2}, SlowMA={1:F2}", fastMA, slowMA));
                    }
                    catch (Exception ex)
                    {
                        // If indicator doesn't have history, log the error
                        Output.WriteLine(string.Format("Failed to get MA from indicator at barsAgo={0}: {1}", barsAgo, ex.Message));
                        Output.WriteLine(string.Format("Trying alternative access method..."));
                        
                        // Try alternative access
                        try
                        {
                            fastMA = m_FastMA.Value;
                            slowMA = m_SlowMA.Value;
                            Output.WriteLine(string.Format("Got current MA values: FastMA={0:F2}, SlowMA={1:F2}", fastMA, slowMA));
                        }
                        catch (Exception ex2)
                        {
                            Output.WriteLine(string.Format("Alternative access also failed: {0}", ex2.Message));
                            throw;
                        }
                    }

                    if (barsAgo + SlopeLookback >= Bars.CurrentBar)
                    {
                        Output.WriteLine(string.Format("Not enough bars for slope calculation: need {0} bars ahead, only have {1}", SlopeLookback, Bars.CurrentBar - barsAgo));
                        return null;
                    }

                    // Calculate slopes manually
                    double fastMAFuture = CalculateEMA(barsAgo + SlopeLookback, FastMALength);
                    double slowMAFuture = CalculateEMA(barsAgo + SlopeLookback, SlowMALength);
                    
                    fastMASlope = (fastMA - fastMAFuture) / SlopeLookback;
                    slowMASlope = (slowMA - slowMAFuture) / SlopeLookback;
                }
                catch (Exception ex)
                {
                    Output.WriteLine("Exception getting MA values: " + ex.Message);
                    return null;
                }

                double minStrongSlope = tickSize * 2.5;
                double maSeparation = Math.Abs(fastMA - slowMA);

                // Calculate directional consistency
                int bullBars = 0, bearBars = 0;
                for (int i = 0; i < DirectionalBarsLookback && (barsAgo + i) < Bars.CurrentBar; i++)
                {
                    if (Bars.Close[barsAgo + i] > Bars.Open[barsAgo + i])
                        bullBars++;
                    else if (Bars.Close[barsAgo + i] < Bars.Open[barsAgo + i])
                        bearBars++;
                }
                double bullPct = (bullBars / (double)DirectionalBarsLookback) * 100.0;
                double bearPct = (bearBars / (double)DirectionalBarsLookback) * 100.0;

                // Calculate volume ratio
                double avgVolume = 0;
                for (int i = 1; i <= 10 && (barsAgo + i) < Bars.CurrentBar; i++)
                    avgVolume += Bars.Volume[barsAgo + i];
                avgVolume = avgVolume / 10.0;
                double volumeRatio = avgVolume > 0 ? Bars.Volume[barsAgo] / avgVolume : 1.0;

                // Calculate range ratio
                double avgRange = 0;
                for (int i = 1; i <= 10 && (barsAgo + i) < Bars.CurrentBar; i++)
                    avgRange += (Bars.High[barsAgo + i] - Bars.Low[barsAgo + i]);
                avgRange = avgRange / 10.0;
                double currentRange = Bars.High[barsAgo] - Bars.Low[barsAgo];
                double rangeRatio = avgRange > 0 ? currentRange / avgRange : 1.0;

                // Calculate CVD slope
                double cvdSlope = 0;
                if (UseCVDFilter && CVDLookback > 0 && (barsAgo + CVDLookback) < Bars.CurrentBar)
                {
                    double currentCVD = Bars.UpTicks[barsAgo] - Bars.DownTicks[barsAgo];
                    double pastCVD = Bars.UpTicks[barsAgo + CVDLookback] - Bars.DownTicks[barsAgo + CVDLookback];
                    cvdSlope = (currentCVD - pastCVD) / CVDLookback;
                }

                double prevBodyTop = Math.Max(Bars.Open[barsAgo + 1], Bars.Close[barsAgo + 1]);
                double prevBodyBottom = Math.Min(Bars.Open[barsAgo + 1], Bars.Close[barsAgo + 1]);

                return new BarMetrics
                {
                    FastMA = fastMA,
                    SlowMA = slowMA,
                    FastMASlope = fastMASlope,
                    SlowMASlope = slowMASlope,
                    MASeparation = maSeparation,
                    BullPct = bullPct,
                    BearPct = bearPct,
                    VolumeRatio = volumeRatio,
                    RangeRatio = rangeRatio,
                    CVDSlope = cvdSlope,
                    TickSize = tickSize,
                    PrevBodyTop = prevBodyTop,
                    PrevBodyBottom = prevBodyBottom
                };
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error calculating bar metrics: " + ex.Message);
                return null;
            }
        }

        private void PrintBarAnalysisToConsole(int barNumber, DateTime barTime, int barsAgo, BarMetrics metrics)
        {
            try
            {
                double tickSize = metrics.TickSize;
                double fastMAMinSlope = Math.Tan(FastMAMinSlopeAngle * Math.PI / 180.0) * tickSize;
                double slowMAMinSlope = Math.Tan(SlowMAMinSlopeAngle * Math.PI / 180.0) * tickSize;
                double minSeparation = MinMASeparationTicks * tickSize;

                Output.WriteLine("");
                Output.WriteLine("=== BAR ANALYSIS: " + barTime.ToString("HH:mm:ss") + " (Bar " + barNumber + ") ===");
                Output.WriteLine("");
                Output.WriteLine("PRICE DATA:");
                Output.WriteLine(string.Format("  Open={0:F2}, High={1:F2}, Low={2:F2}, Close={3:F2}", 
                    Bars.Open[barsAgo], Bars.High[barsAgo], Bars.Low[barsAgo], Bars.Close[barsAgo]));
                Output.WriteLine("");
                Output.WriteLine("MOVING AVERAGES:");
                Output.WriteLine(string.Format("  FastMA(10)={0:F2}, SlowMA(20)={1:F2}", metrics.FastMA, metrics.SlowMA));
                Output.WriteLine(string.Format("  MA Separation: {0:F2} ticks (min required: {1:F2})", 
                    metrics.MASeparation / tickSize, MinMASeparationTicks));
                Output.WriteLine(string.Format("  FastMA Slope: {0:F4} ticks/bar (min: {1:F4}) - {2}", 
                    metrics.FastMASlope / tickSize, fastMAMinSlope / tickSize, 
                    Math.Abs(metrics.FastMASlope) >= Math.Abs(fastMAMinSlope) ? "✓ OK" : "✗ FAIL"));
                Output.WriteLine(string.Format("  SlowMA Slope: {0:F4} ticks/bar (min: {1:F4}) - {2}", 
                    metrics.SlowMASlope / tickSize, slowMAMinSlope / tickSize,
                    Math.Abs(metrics.SlowMASlope) >= Math.Abs(slowMAMinSlope) ? "✓ OK" : "✗ FAIL"));
                Output.WriteLine("");
                Output.WriteLine("DIRECTIONAL BIAS:");
                Output.WriteLine(string.Format("  Bullish: {0:F1}% (min: {1}%)", metrics.BullPct, MinDirectionalPct));
                Output.WriteLine(string.Format("  Bearish: {0:F1}% (min: {1}%)", metrics.BearPct, MinDirectionalPct));
                Output.WriteLine("");
                Output.WriteLine("VOLUME & RANGE:");
                Output.WriteLine(string.Format("  Volume Ratio: {0:F2}x (min: {1}x) - {2}", 
                    metrics.VolumeRatio, MinVolumeMultiplier, 
                    metrics.VolumeRatio >= MinVolumeMultiplier ? "✓ OK" : "✗ FAIL"));
                Output.WriteLine(string.Format("  Range Ratio: {0:F2}x (min: {1}x) - {2}", 
                    metrics.RangeRatio, MinRangeMultiplier,
                    metrics.RangeRatio >= MinRangeMultiplier ? "✓ OK" : "✗ FAIL"));
                Output.WriteLine("");
                Output.WriteLine("TREND STATUS:");
                Output.WriteLine(string.Format("  Strong Bull Trend: {0}", metrics.StrongBullTrend ? "YES" : "NO"));
                Output.WriteLine(string.Format("  Strong Bear Trend: {0}", metrics.StrongBearTrend ? "YES" : "NO"));
                Output.WriteLine("");
                Output.WriteLine("CONCLUSION:");
                if (!metrics.StrongBullTrend && !metrics.StrongBearTrend)
                {
                    Output.WriteLine("  ✗ NO SIGNAL - Neither bullish nor bearish trend conditions met");
                }
                else if (metrics.StrongBullTrend)
                {
                    Output.WriteLine("  ✓ BULLISH SIGNAL CONDITIONS MET");
                }
                else if (metrics.StrongBearTrend)
                {
                    Output.WriteLine("  ✓ BEARISH SIGNAL CONDITIONS MET");
                }
                Output.WriteLine("=====================================");
                Output.WriteLine("");
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error printing bar analysis: " + ex.Message);
            }
        }

        private void ExportBarMetricsToCSV(int barNumber, DateTime barTime, int barsAgo, BarMetrics metrics)
        {
            try
            {
                // Verify and create directory
                if (!System.IO.Directory.Exists(ClickExportFolder))
                {
                    System.IO.Directory.CreateDirectory(ClickExportFolder);
                    Output.WriteLine("Created directory: " + ClickExportFolder);
                }

                // Use HHmmss instead of HH:mm:ss to avoid invalid filename characters
                string filename = string.Format("bar_analysis_{0:yyyyMMdd_HHmmss}.csv", barTime);
                string filepath = System.IO.Path.Combine(ClickExportFolder, filename);
                
                Output.WriteLine("Attempting to export to: " + filepath);

                var lines = new List<string>();
                lines.Add("BAR ANALYSIS");
                lines.Add("Category,Value");
                lines.Add(string.Format("BarNumber,{0}", barNumber));
                lines.Add(string.Format("DateTime,{0:yyyy-MM-dd HH:mm:ss}", barTime));
                lines.Add("");
                lines.Add("PRICE DATA");
                lines.Add("Category,Value");
                lines.Add(string.Format("Open,{0:F2}", Bars.Open[barsAgo]));
                lines.Add(string.Format("High,{0:F2}", Bars.High[barsAgo]));
                lines.Add(string.Format("Low,{0:F2}", Bars.Low[barsAgo]));
                lines.Add(string.Format("Close,{0:F2}", Bars.Close[barsAgo]));
                lines.Add("");
                lines.Add("MOVING AVERAGES");
                lines.Add("Category,Value");
                lines.Add(string.Format("FastMA(10),{0:F2}", metrics.FastMA));
                lines.Add(string.Format("SlowMA(20),{0:F2}", metrics.SlowMA));
                lines.Add(string.Format("MASeparation(ticks),{0:F2}", metrics.MASeparation / metrics.TickSize));
                lines.Add(string.Format("FastMASlope(ticks/bar),{0:F4}", metrics.FastMASlope / metrics.TickSize));
                lines.Add(string.Format("SlowMASlope(ticks/bar),{0:F4}", metrics.SlowMASlope / metrics.TickSize));
                lines.Add("");
                lines.Add("DIRECTIONAL BIAS");
                lines.Add("Category,Value");
                lines.Add(string.Format("BullishBarsPct,{0:F1}%", metrics.BullPct));
                lines.Add(string.Format("BearishBarsPct,{0:F1}%", metrics.BearPct));
                lines.Add("");
                lines.Add("VOLUME & RANGE");
                lines.Add("Category,Value");
                lines.Add(string.Format("VolumeRatio,{0:F2}x", metrics.VolumeRatio));
                lines.Add(string.Format("RangeRatio,{0:F2}x", metrics.RangeRatio));
                lines.Add("");
                lines.Add("CVD");
                lines.Add("Category,Value");
                lines.Add(string.Format("CVDSlope,{0:F2}", metrics.CVDSlope));

                System.IO.File.WriteAllLines(filepath, lines.ToArray());
                Output.WriteLine("SUCCESS: Exported bar analysis to: " + filepath);
            }
            catch (Exception ex)
            {
                Output.WriteLine("ERROR exporting bar analysis: " + ex.Message);
            }
        }

        private void DrawBarAnalysisPopup(int barsAgo, double fastMA, double slowMA, double fastMASlope,
            double slowMASlope, double maSeparation, double bullPct, double volumeRatio, double rangeRatio, double tickSize)
        {
            try
            {
                // Position popup above the bar
                double popupPrice = Bars.High[barsAgo] + (Bars.Info.MinMove / Bars.Info.PriceScale * 20);

                // Create popup text with key metrics
                string popupText = string.Format(
                    "BAR ANALYSIS\n" +
                    "FastMA: {0:F2} | SlowMA: {1:F2}\n" +
                    "Slope: {2:F1}° / {3:F1}°\n" +
                    "MASep: {4:F1}t | Dir: {5:F0}%\n" +
                    "Vol: {6:F2}x | Range: {7:F2}x",
                    fastMA,
                    slowMA,
                    Math.Atan(fastMASlope) * (180.0 / Math.PI),
                    Math.Atan(slowMASlope) * (180.0 / Math.PI),
                    maSeparation / tickSize,
                    bullPct > 50 ? bullPct : 100 - bullPct,
                    volumeRatio,
                    rangeRatio
                );

                // Draw the popup text on the chart
                var textObj = DrwText.Create(new ChartPoint(Bars.Time[barsAgo], popupPrice), popupText);
                textObj.Color = Color.Purple;
                textObj.VStyle = ETextStyleV.Above;
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error drawing bar analysis popup: " + ex.Message);
            }
        }

        private string GetTrendFailureReason(bool isBullish, double fastMA, double slowMA,
            double fastMASlope, double slowMASlope, double dirPct,
            double volumeRatio, double rangeRatio, double tickSize,
            bool tailNearFastMA, bool tailNearSlowMA, bool fastMAQualifies, bool slowMAQualifies,
            bool volumePatternOK, double pullbackVolume, double trendVolume)
        {
            var reasons = new List<string>();
            double minStrongSlope = tickSize * 2.5;
            double maSeparation = Math.Abs(fastMA - slowMA);

            // Check MA bounce logic
            if (!tailNearFastMA && !tailNearSlowMA)
            {
                reasons.Add("Tail doesn't reach either MA (not within 3 ticks)");
            }
            else
            {
                if (tailNearFastMA && !fastMAQualifies)
                {
                    if (isBullish)
                        reasons.Add(string.Format("Tail near 10MA but slope {0:F2} < {1:F2} ticks/bar",
                            fastMASlope/tickSize, 2.5));
                    else
                        reasons.Add(string.Format("Tail near 10MA but slope {0:F2} > -{1:F2} ticks/bar",
                            fastMASlope/tickSize, 2.5));
                }

                if (tailNearSlowMA && !slowMAQualifies)
                {
                    if (isBullish)
                        reasons.Add(string.Format("Tail near 20MA but slope {0:F2} < {1:F2} ticks/bar",
                            slowMASlope/tickSize, 1.5));
                    else
                        reasons.Add(string.Format("Tail near 20MA but slope {0:F2} > -{1:F2} ticks/bar",
                            slowMASlope/tickSize, 1.5));
                }
            }

            // Check MA order
            if (isBullish && !(fastMA > slowMA))
                reasons.Add("Fast MA not above Slow MA");
            else if (!isBullish && !(fastMA < slowMA))
                reasons.Add("Fast MA not below Slow MA");

            // Check MA separation
            if (!(maSeparation >= MinMASeparationTicks * tickSize))
                reasons.Add(string.Format("MA separation {0:F2} < {1} ticks", maSeparation/tickSize, MinMASeparationTicks));

            // Check other filters
            if (!(dirPct >= MinDirectionalPct))
                reasons.Add(string.Format("Directional % {0:F1} < {1}%", dirPct, MinDirectionalPct));
            if (!(volumeRatio >= MinVolumeMultiplier))
                reasons.Add(string.Format("Volume ratio {0:F2} < {1}x", volumeRatio, MinVolumeMultiplier));
            if (!(rangeRatio >= MinRangeMultiplier))
                reasons.Add(string.Format("Range ratio {0:F2} < {1}x", rangeRatio, MinRangeMultiplier));

            // Check volume pattern
            if (UseVolumePatternFilter && !volumePatternOK)
            {
                if (trendVolume > 0)
                    reasons.Add(string.Format("Volume pattern failed: pullback vol {0:F0} not < trend vol {1:F0} * 0.85",
                        pullbackVolume, trendVolume));
                else
                    reasons.Add("Volume pattern failed: CVD pattern not orderly during pullback");
            }

            return string.Join("; ", reasons.ToArray());
        }

        // Validate all active signals against current bar
        private void ValidateActiveSignals()
        {
            var completedSignals = new List<int>();

            foreach (var kvp in m_ActiveSignals)
            {
                int barNum = kvp.Key;
                SignalValidation signal = kvp.Value;

                if (signal.Status != "Pending")
                    continue;

                bool isSuccess = false;
                bool isFailed = false;

                if (signal.SignalType == "BullishTail")
                {
                    // Success: price forms a new brick ABOVE signal bar high
                    if (Bars.Close[0] > signal.SignalBarHigh && Bars.Close[0] > Bars.Open[0])
                        isSuccess = true;

                    // Failure: any bar's low goes BELOW tail low
                    if (Bars.Low[0] < signal.TailLow)
                        isFailed = true;
                }
                else if (signal.SignalType == "BearishTail")
                {
                    // Success: price forms a new brick BELOW signal bar low
                    if (Bars.Close[0] < signal.SignalBarLow && Bars.Close[0] < Bars.Open[0])
                        isSuccess = true;

                    // Failure: any bar's high goes ABOVE tail high
                    if (Bars.High[0] > signal.TailHigh)
                        isFailed = true;
                }

                if (isSuccess)
                {
                    signal.Status = "Success";
                    m_SuccessfulSignals++;
                    DrawSignalMarker(signal, true);
                    completedSignals.Add(barNum);
                }
                else if (isFailed)
                {
                    signal.Status = "Failed";
                    m_FailedSignals++;
                    DrawSignalMarker(signal, false);
                    completedSignals.Add(barNum);
                }
            }

            // Remove completed signals from active tracking
            foreach (int barNum in completedSignals)
                m_ActiveSignals.Remove(barNum);
        }

        // Update bar number text with success/failure status
        private void DrawSignalMarker(SignalValidation signal, bool isSuccess)
        {
            try
            {
                if (signal.BarNumberText != null)
                {
                    // Update the text to show bar number with success or failure underneath
                    string statusText = isSuccess ? "SUCCESS" : "FAILURE";
                    signal.BarNumberText.Text = signal.BarNumber + "\n" + statusText;
                    signal.BarNumberText.Color = isSuccess ? Color.Green : Color.Red;
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error updating signal marker: " + ex.Message);
            }
        }

        // Output signal statistics
        private void OutputSignalStatistics()
        {
            if (m_TotalSignalsGenerated == 0)
                return;

            int successRate = (m_SuccessfulSignals * 100) / m_TotalSignalsGenerated;
            int failureRate = (m_FailedSignals * 100) / m_TotalSignalsGenerated;
            int pendingCount = m_ActiveSignals.Count;

            Output.WriteLine("=== SIGNAL STATISTICS ===");
            Output.WriteLine(string.Format("Total Signals Generated: {0}", m_TotalSignalsGenerated));
            Output.WriteLine(string.Format("Successful Signals: {0} ({1}%)", m_SuccessfulSignals, successRate));
            Output.WriteLine(string.Format("Failed Signals: {0} ({1}%)", m_FailedSignals, failureRate));
            Output.WriteLine(string.Format("Pending Signals: {0}", pendingCount));
            Output.WriteLine("========================");
        }

        // Check if bar qualifies for auto-signal: tail extends beyond previous body + steep 20 MA slope
        private bool CheckSlowMAAutoSignal(bool tailExtendsBeyondPrevBody, double slowMASlope, double slowMAAutoMinSlope, bool isBullish)
        {
            if (!UseSlowMAAutoSignal)
                return false;

            // Tail must extend beyond previous body
            if (!tailExtendsBeyondPrevBody)
                return false;

            // 20 MA slope must be steep enough
            if (isBullish && slowMASlope < slowMAAutoMinSlope)
                return false;
            if (!isBullish && slowMASlope > -slowMAAutoMinSlope)
                return false;

            // All conditions met - signal triggered!
            // Debug output disabled to reduce console noise
            // string signalType = isBullish ? "Bullish" : "Bearish";
            // Output.WriteLine(string.Format("*** {0} AUTO-SIGNAL TRIGGERED at {1:HH:mm:ss}: Slope={2:F2}, MA={3:F2} ***", 
            //     signalType, Bars.Time[0], slowMASlope, Bars.Close[0]));
            return true;
        }

        // Helper method to check if we should block a new signal due to clustering
        private bool ShouldBlockSignalForClustering()
        {
            if (!m_HasLastSignal)
                return false;

            // Block if we haven't waited enough bars since last signal
            if (m_BarssSinceLastSignal < m_MinBarsBetweenSignals)
                return true;

            // Also block if current bar is within the last signal's price range
            bool withinRange = (Bars.High[0] >= m_LastSignalLow && Bars.High[0] <= m_LastSignalHigh) ||
                               (Bars.Low[0] >= m_LastSignalLow && Bars.Low[0] <= m_LastSignalHigh) ||
                               (Bars.Low[0] <= m_LastSignalLow && Bars.High[0] >= m_LastSignalHigh);

            return withinRange;
        }

        protected override void CalcBar()
        {
            // Validate all active signals against current bar price action
            ValidateActiveSignals();

            // Output statistics every 1000 bars
            if (Bars.CurrentBar % 1000 == 0)
            {
                OutputSignalStatistics();
            }

            // Increment bars since last signal
            if (m_HasLastSignal)
                m_BarssSinceLastSignal++;

            // Need enough bars for MAs, slope calculation, and previous bar
            if (Bars.CurrentBar < Math.Max(FastMALength, SlowMALength) + SlopeLookback + 1)
                return;

            double fastMA = m_FastMA.Value;
            double slowMA = m_SlowMA.Value;
            double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;

            // Calculate MA slope (change over lookback period)
            double fastMASlope = (m_FastMA[0] - m_FastMA[SlopeLookback]) / SlopeLookback;
            double slowMASlope = (m_SlowMA[0] - m_SlowMA[SlopeLookback]) / SlopeLookback;

            // Convert angle parameters to slope values
            // slope = tan(angle in radians) * tickSize
            double fastMAMinSlope = Math.Tan(FastMAMinSlopeAngle * Math.PI / 180.0) * tickSize;
            double slowMAMinSlope = Math.Tan(SlowMAMinSlopeAngle * Math.PI / 180.0) * tickSize;
            double slowMAAutoMinSlope = Math.Tan(SlowMAAutoSlopeAngle * Math.PI / 180.0) * tickSize;

            // Check MA separation
            double maSeparation = Math.Abs(fastMA - slowMA);
            double minSeparation = MinMASeparationTicks * tickSize;

            // Calculate directional consistency (% of bars in same direction)
            int bullBars = 0;
            int bearBars = 0;
            for (int i = 0; i < DirectionalBarsLookback && i < Bars.CurrentBar; i++)
            {
                if (Bars.Close[i] > Bars.Open[i])
                    bullBars++;
                else if (Bars.Close[i] < Bars.Open[i])
                    bearBars++;
            }
            double bullPct = (bullBars / (double)DirectionalBarsLookback) * 100.0;
            double bearPct = (bearBars / (double)DirectionalBarsLookback) * 100.0;

            // Calculate volume trend (current vs average)
            double avgVolume = 0;
            for (int i = 1; i <= 10 && i < Bars.CurrentBar; i++)
                avgVolume += Bars.Volume[i];
            avgVolume = avgVolume / 10.0;
            double volumeRatio = avgVolume > 0 ? Bars.Volume[0] / avgVolume : 1.0;

            // Calculate range trend (current vs average)
            double avgRange = 0;
            for (int i = 1; i <= 10 && i < Bars.CurrentBar; i++)
                avgRange += (Bars.High[i] - Bars.Low[i]);
            avgRange = avgRange / 10.0;
            double currentRange = Bars.High[0] - Bars.Low[0];
            double rangeRatio = avgRange > 0 ? currentRange / avgRange : 1.0;

            // Calculate CVD slope
            double cvdSlope = 0;
            bool cvdRising = true;
            bool cvdFalling = true;

            if (UseCVDFilter && CVDLookback > 0)
            {
                // Calculate CVD for current and lookback bar
                double currentCVD = Bars.UpTicks[0] - Bars.DownTicks[0];
                double pastCVD = Bars.UpTicks[CVDLookback] - Bars.DownTicks[CVDLookback];

                // Calculate slope (change per bar)
                cvdSlope = (currentCVD - pastCVD) / CVDLookback;
                cvdRising = cvdSlope > 0;
                cvdFalling = cvdSlope < 0;
            }

            // Current bar body
            double currentBodyTop = Math.Max(Bars.Open[0], Bars.Close[0]);
            double currentBodyBottom = Math.Min(Bars.Open[0], Bars.Close[0]);

            // Previous bar body
            double prevBodyTop = Math.Max(Bars.Open[1], Bars.Close[1]);
            double prevBodyBottom = Math.Min(Bars.Open[1], Bars.Close[1]);

            // Volume pattern check: declining volume during pullback
            bool volumePatternOK = true;
            if (UseVolumePatternFilter && Bars.CurrentBar >= 6)
            {
                // Calculate average volume during pullback (bars 1-3)
                double pullbackVolume = (Bars.Volume[1] + Bars.Volume[2] + Bars.Volume[3]) / 3.0;

                // Calculate average volume during trend (bars 4-6)
                double trendVolume = (Bars.Volume[4] + Bars.Volume[5] + Bars.Volume[6]) / 3.0;

                // Pullback volume should be lower (orderly consolidation)
                bool volumeDeclining = pullbackVolume < trendVolume * 0.85; // 85% threshold

                // Calculate CVD during pullback vs trend
                double pullbackCVD = 0;
                double trendCVD = 0;
                for (int i = 1; i <= 3 && i < Bars.CurrentBar; i++)
                    pullbackCVD += (Bars.UpTicks[i] - Bars.DownTicks[i]);
                for (int i = 4; i <= 6 && i < Bars.CurrentBar; i++)
                    trendCVD += (Bars.UpTicks[i] - Bars.DownTicks[i]);

                // For bullish setup: CVD should be declining/flat during pullback (selling drying up)
                // For bearish setup: CVD should be rising/flat during pullback (buying drying up)
                bool cvdPatternOK = true;
                if (fastMA > slowMA) // Bullish trend
                    cvdPatternOK = pullbackCVD <= trendCVD * 0.5; // CVD declining during pullback
                else if (fastMA < slowMA) // Bearish trend
                    cvdPatternOK = pullbackCVD >= trendCVD * 0.5; // CVD rising during pullback

                volumePatternOK = volumeDeclining && cvdPatternOK;
            }

            // NEW LOGIC: Check which MA the tail is bouncing off and require that MA to have strong slope
            // Define "near" as within 10 ticks of the MA (increased from 3 for debugging)
            double nearThreshold = tickSize * 10;

            // Check if tail reaches near the Fast MA (10 MA - green)
            bool tailNearFastMA = Math.Abs(Bars.Low[0] - fastMA) <= nearThreshold ||
                                  Math.Abs(Bars.High[0] - fastMA) <= nearThreshold;

            // Check if tail reaches near the Slow MA (20 MA - black)
            bool tailNearSlowMA = Math.Abs(Bars.Low[0] - slowMA) <= nearThreshold ||
                                  Math.Abs(Bars.High[0] - slowMA) <= nearThreshold;

            // For bullish setup: tail bounces off MA that has strong upward slope
            bool fastMAQualifies = tailNearFastMA && fastMASlope >= fastMAMinSlope;
            bool slowMAQualifies = tailNearSlowMA && slowMASlope >= slowMAMinSlope; // Use configured slow MA slope

            bool strongBullTrend = (fastMAQualifies || slowMAQualifies) &&
                                   fastMA > slowMA &&  // MAs in correct order
                                   maSeparation >= minSeparation &&  // MAs must be separated
                                   bullPct >= MinDirectionalPct &&
                                   volumeRatio >= MinVolumeMultiplier &&
                                   rangeRatio >= MinRangeMultiplier &&
                                   (!UseCVDFilter || cvdRising) &&
                                   (!UseVolumePatternFilter || volumePatternOK);  // Volume pattern check

            // For bearish setup: tail bounces off MA that has strong downward slope
            bool fastMAQualifiesBear = tailNearFastMA && fastMASlope <= -fastMAMinSlope;
            bool slowMAQualifiesBear = tailNearSlowMA && slowMASlope <= -slowMAMinSlope;

            bool strongBearTrend = (fastMAQualifiesBear || slowMAQualifiesBear) &&
                                   fastMA < slowMA &&  // MAs in correct order
                                   maSeparation >= minSeparation &&  // MAs must be separated
                                   bearPct >= MinDirectionalPct &&
                                   volumeRatio >= MinVolumeMultiplier &&
                                   rangeRatio >= MinRangeMultiplier &&
                                   (!UseCVDFilter || cvdFalling) &&
                                   (!UseVolumePatternFilter || volumePatternOK);  // Volume pattern check

            // Plot the moving averages
            m_FastMAPlot.Set(0, fastMA);
            m_SlowMAPlot.Set(0, slowMA);

            // Trend indicator disabled - was causing scaling issues
            // if (strongBullTrend)
            // {
            //     m_TrendIndicator.Set(0, 1);  // Positive = bullish
            // }
            // else if (strongBearTrend)
            // {
            //     m_TrendIndicator.Set(0, -1);  // Negative = bearish
            // }
            // else
            // {
            //     m_TrendIndicator.Set(0, 0);  // Zero = neutral
            // }

            // BULLISH SETUP: Strong uptrend + blue bar + tail extends to/below previous body
            bool isBullishBar = Bars.Close[0] > Bars.Open[0];
            bool tailExtendsBelowPrevBody = Bars.Low[0] <= prevBodyBottom;

            // Debug: Check for potential signals that are being rejected
            // if (isBullishBar && tailExtendsBelowPrevBody && !strongBullTrend)
            // {
            //     Output.WriteLine(string.Format("Bullish tail rejected at {0:HH:mm:ss}: FastMAQual={1}, SlowMAQual={2}, MASep={3:F1}, DirPct={4:F0}%, Vol={5:F2}, Range={6:F2}, VolPattern={7}, FastSlope={8:F2}, SlowSlope={9:F2}",
            //         Bars.Time[0], fastMAQualifies, slowMAQualifies, maSeparation/tickSize, bullPct, volumeRatio, rangeRatio, volumePatternOK, fastMASlope, slowMASlope));
            // }

            // Check auto-signal: tail extends below previous body + steep 20 MA
            bool bullishAutoSignal = CheckSlowMAAutoSignal(tailExtendsBelowPrevBody, slowMASlope, slowMAAutoMinSlope, true);

            if ((strongBullTrend || bullishAutoSignal) && isBullishBar && tailExtendsBelowPrevBody && !ShouldBlockSignalForClustering())
            {
                // Don't draw the circle yet - wait for validation
                // m_BullishTail.Set(0, Bars.Low[0] - (3 * tickSize));

                // Update last signal range to prevent clustering
                m_LastSignalHigh = Bars.High[0];
                m_LastSignalLow = Bars.Low[0];
                m_HasLastSignal = true;
                m_BarssSinceLastSignal = 0;
                
                // Debug output disabled
                // if (bullishAutoSignal)
                //     Output.WriteLine(string.Format("*** Bullish AUTO-SIGNAL REGISTERED at {0:HH:mm:ss} bar {1} ***", Bars.Time[0], Bars.CurrentBar));

                // Calculate volume data for the story (if available)
                double pullbackVol = 0, trendVol = 0;
                if (Bars.CurrentBar >= 6)
                {
                    pullbackVol = (Bars.Volume[1] + Bars.Volume[2] + Bars.Volume[3]) / 3.0;
                    trendVol = (Bars.Volume[4] + Bars.Volume[5] + Bars.Volume[6]) / 3.0;
                }

                // Store signal data
                var signalData = new SignalData
                {
                    Time = Bars.Time[0],
                    SignalType = "BullishTail",
                    Open = Bars.Open[0],
                    High = Bars.High[0],
                    Low = Bars.Low[0],
                    Close = Bars.Close[0],
                    FastMA = fastMA,
                    SlowMA = slowMA,
                    FastMASlope = fastMASlope / tickSize,
                    SlowMASlope = slowMASlope / tickSize,
                    MASeparation = maSeparation / tickSize,
                    BullPct = bullPct,
                    BearPct = bearPct,
                    VolumeRatio = volumeRatio,
                    RangeRatio = rangeRatio,
                    CVDSlope = cvdSlope,
                    StrongBullTrend = strongBullTrend,
                    StrongBearTrend = strongBearTrend,
                    PrevBodyTop = prevBodyTop,
                    PrevBodyBottom = prevBodyBottom,
                    PullbackVolume = pullbackVol,
                    TrendVolume = trendVol,
                    TailBarVolume = Bars.Volume[0]
                };

                m_SignalsByBar[Bars.CurrentBar] = signalData;
                m_SignalsByTime[Bars.Time[0]] = signalData;  // Store by time for click lookup

                // Draw bar number label if enabled
                ITextObject bullishBarText = null;
                if (ShowBarNumbers)
                {
                    bullishBarText = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.Low[0] - (5 * tickSize)),
                        Bars.CurrentBar.ToString());
                    bullishBarText.Color = Color.Black;
                }

                // Register signal for validation tracking
                var validation = new SignalValidation
                {
                    BarNumber = Bars.CurrentBar,
                    SignalTime = Bars.Time[0],
                    SignalType = "BullishTail",
                    SignalBarHigh = Bars.High[0],
                    SignalBarLow = Bars.Low[0],
                    TailLow = Bars.Low[0] - (3 * tickSize),
                    TailHigh = 0,  // Not used for bullish
                    Status = "Pending",
                    PlotIndex = 0,  // Bullish plot
                    BarNumberText = bullishBarText
                };
                m_ActiveSignals[Bars.CurrentBar] = validation;
                m_TotalSignalsGenerated++;
            }

            // BEARISH SETUP: Strong downtrend + red bar + tail extends to/above previous body
            bool isBearishBar = Bars.Close[0] < Bars.Open[0];
            bool tailExtendsAbovePrevBody = Bars.High[0] >= prevBodyTop;

            // Debug: Check for potential signals that are being rejected
            // if (isBearishBar && tailExtendsAbovePrevBody && !strongBearTrend)
            // {
            //     Output.WriteLine(string.Format("Bearish tail rejected at {0:HH:mm:ss}: FastMAQual={1}, SlowMAQual={2}, MASep={3:F1}, DirPct={4:F0}%, Vol={5:F2}, Range={6:F2}, VolPattern={7}, FastSlope={8:F2}, SlowSlope={9:F2}",
            //         Bars.Time[0], fastMAQualifiesBear, slowMAQualifiesBear, maSeparation/tickSize, bearPct, volumeRatio, rangeRatio, volumePatternOK, fastMASlope, slowMASlope));
            // }

            // Check auto-signal: tail extends above previous body + steep 20 MA
            bool bearishAutoSignal = CheckSlowMAAutoSignal(tailExtendsAbovePrevBody, slowMASlope, slowMAAutoMinSlope, false);

            if ((strongBearTrend || bearishAutoSignal) && isBearishBar && tailExtendsAbovePrevBody && !ShouldBlockSignalForClustering())
            {
                // Don't draw the circle yet - wait for validation
                // m_BearishTail.Set(0, Bars.High[0] + (3 * tickSize));

                // Update last signal range to prevent clustering
                m_LastSignalHigh = Bars.High[0];
                m_LastSignalLow = Bars.Low[0];
                m_HasLastSignal = true;
                m_BarssSinceLastSignal = 0;
                
                // Debug output disabled
                // if (bearishAutoSignal)
                //     Output.WriteLine(string.Format("*** Bearish AUTO-SIGNAL REGISTERED at {0:HH:mm:ss} bar {1} ***", Bars.Time[0], Bars.CurrentBar));

                // Calculate volume data for the story (if available)
                double pullbackVol = 0, trendVol = 0;
                if (Bars.CurrentBar >= 6)
                {
                    pullbackVol = (Bars.Volume[1] + Bars.Volume[2] + Bars.Volume[3]) / 3.0;
                    trendVol = (Bars.Volume[4] + Bars.Volume[5] + Bars.Volume[6]) / 3.0;
                }

                // Store signal data
                var signalData = new SignalData
                {
                    Time = Bars.Time[0],
                    SignalType = "BearishTail",
                    Open = Bars.Open[0],
                    High = Bars.High[0],
                    Low = Bars.Low[0],
                    Close = Bars.Close[0],
                    FastMA = fastMA,
                    SlowMA = slowMA,
                    FastMASlope = fastMASlope / tickSize,
                    SlowMASlope = slowMASlope / tickSize,
                    MASeparation = maSeparation / tickSize,
                    BullPct = bullPct,
                    BearPct = bearPct,
                    VolumeRatio = volumeRatio,
                    RangeRatio = rangeRatio,
                    CVDSlope = cvdSlope,
                    StrongBullTrend = strongBullTrend,
                    StrongBearTrend = strongBearTrend,
                    PrevBodyTop = prevBodyTop,
                    PrevBodyBottom = prevBodyBottom,
                    PullbackVolume = pullbackVol,
                    TrendVolume = trendVol,
                    TailBarVolume = Bars.Volume[0]
                };

                m_SignalsByBar[Bars.CurrentBar] = signalData;
                m_SignalsByTime[Bars.Time[0]] = signalData;  // Store by time for click lookup

                // Draw bar number label if enabled
                ITextObject bearishBarText = null;
                if (ShowBarNumbers)
                {
                    bearishBarText = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.High[0] + (5 * tickSize)),
                        Bars.CurrentBar.ToString());
                    bearishBarText.Color = Color.Black;
                }

                // Register signal for validation tracking
                var validationBear = new SignalValidation
                {
                    BarNumber = Bars.CurrentBar,
                    SignalTime = Bars.Time[0],
                    SignalType = "BearishTail",
                    SignalBarHigh = Bars.High[0],
                    SignalBarLow = Bars.Low[0],
                    TailLow = 0,  // Not used for bearish
                    TailHigh = Bars.High[0] + (3 * tickSize),
                    Status = "Pending",
                    PlotIndex = 1,  // Bearish plot
                    BarNumberText = bearishBarText
                };
                m_ActiveSignals[Bars.CurrentBar] = validationBear;
                m_TotalSignalsGenerated++;
            }

            // Export signal data if enabled
            if (ExportSignalData && !string.IsNullOrEmpty(ExportFilePath))
            {
                // Only export data between 6:30 AM and 10:00 AM
                TimeSpan currentTime = Bars.Time[0].TimeOfDay;
                TimeSpan startTime = new TimeSpan(6, 30, 0);
                TimeSpan endTime = new TimeSpan(10, 0, 0);

                if (currentTime >= startTime && currentTime < endTime)
                {
                    try
                    {
                        // Write header on first export
                        if (!m_ExportHeaderWritten)
                        {
                            string header = "BarNumber,SignalType,DateTime,Open,High,Low,Close,FastMA,SlowMA,MASlope,MASeparation,DirectionalPct,VolumeRatio,RangeRatio,CVDSlope,BarColor";
                            System.IO.File.WriteAllText(ExportFilePath, header + "\r\n");
                            m_ExportHeaderWritten = true;
                            m_LastReportedDate = DateTime.MinValue;
                            m_DayCounter = 0;
                        }

                        // Report progress when date changes - show day number on chart only
                        DateTime currentDate = Bars.Time[0].Date;
                        if (currentDate != m_LastReportedDate)
                        {
                            m_DayCounter++;

                            // Draw day number on chart
                            DrwText.Create(new ChartPoint(Bars.Time[0], Bars.High[0] + 50), "Day " + m_DayCounter.ToString());

                            m_LastReportedDate = currentDate;
                        }

                        string signalType = "None";
                        if (strongBullTrend && isBullishBar && tailExtendsBelowPrevBody)
                            signalType = "BullishTail";
                        else if (strongBearTrend && isBearishBar && tailExtendsAbovePrevBody)
                            signalType = "BearishTail";
                        else if (strongBullTrend)
                            signalType = "StrongBull";
                        else if (strongBearTrend)
                            signalType = "StrongBear";

                        // Determine bar color based on close vs open
                        string barColor = "Doji";
                        double barOpen = Bars.Open[0];
                        double barClose = Bars.Close[0];

                        if (barClose > barOpen)
                            barColor = "Bull";
                        else if (barClose < barOpen)
                            barColor = "Bear";

                        string line = string.Format("{0},{1},{2:yyyy-MM-dd HH:mm:ss},{3:F2},{4:F2},{5:F2},{6:F2},{7:F2},{8:F2},{9:F1},{10:F2},{11:F2},{12:F2},{13:F2},{14:F2},{15}",
                            Bars.CurrentBar,
                            signalType,
                            Bars.Time[0],
                            Bars.Open[0],
                            Bars.High[0],
                            Bars.Low[0],
                            Bars.Close[0],
                            fastMA,
                            slowMA,
                            fastMASlope / tickSize,
                            maSeparation / tickSize,
                            bullPct > bearPct ? bullPct : bearPct,
                            volumeRatio,
                            rangeRatio,
                            cvdSlope,
                            barColor);

                        System.IO.File.AppendAllText(ExportFilePath, line + "\r\n");
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("Error exporting data: " + ex.Message);
                    }
                }
            }
        }

        // Helper method to calculate SMA (Simple Moving Average) for a historical bar
        private double CalculateEMA(int barsAgo, int period)
        {
            try
            {
                if (barsAgo < 0 || barsAgo >= Bars.CurrentBar)
                    return 0;

                // Calculate simple moving average using close prices
                double sum = 0;
                int count = 0;

                // Look back from the bar
                for (int i = 0; i < period && (barsAgo + i) < Bars.CurrentBar; i++)
                {
                    sum += Bars.Close[barsAgo + i];
                    count++;
                }

                if (count == 0)
                    return Bars.Close[barsAgo];

                return sum / count;
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in CalculateEMA: " + ex.Message);
                return Bars.Close[barsAgo];  // Fallback to current close
            }
        }
    }
}
