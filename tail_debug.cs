using System;
using System.Drawing;
using System.Collections.Generic;
using System.Windows.Forms;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true), MouseEvents(true)]
    public class tail_debug : IndicatorObject
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
        }

        private Dictionary<int, SignalData> m_SignalsByBar;
        private Dictionary<DateTime, SignalData> m_SignalsByTime;

        public tail_debug(object ctx) : base(ctx)
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
            ExportSignalData = false;
            ExportFilePath = @"C:\Users\mark\tail_signals_debug\signals.csv";
            ShowBarNumbers = true;  // Show bar numbers next to signals for reference
            ClickExportFolder = @"C:\Users\mark\tail_signals_debug\clicked";
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

            // Trend indicator - line at bottom of chart
            m_TrendIndicator = AddPlot(new PlotAttributes("Trend", EPlotShapes.Line,
                Color.Gray, Color.Empty, 1, 0, true));
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

        private void ExportSignalDetails(int barNumber, SignalData data)
        {
            try
            {
                // Create folder if it doesn't exist
                if (!System.IO.Directory.Exists(ClickExportFolder))
                    System.IO.Directory.CreateDirectory(ClickExportFolder);

                // Create unique filename with timestamp
                string filename = string.Format("signal_{0}_{1:yyyyMMdd_HHmmss}.csv",
                    data.SignalType, data.Time);
                string filepath = System.IO.Path.Combine(ClickExportFolder, filename);

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

                Output.WriteLine("Exported signal details to: " + filename);
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error exporting clicked signal: " + ex.Message);
            }
        }

        private void ExportBarAnalysis(int barNumber, DateTime barTime)
        {
            try
            {
                // Use the bar number directly - it represents bars ago from current
                // barNumber from mouse click is the absolute bar index
                int barsAgo = Bars.CurrentBar - barNumber;

                // Validate range - need enough bars for all calculations
                int minBarsNeeded = Math.Max(FastMALength, SlowMALength) + SlopeLookback + 2;

                if (barsAgo < 0)
                {
                    Output.WriteLine("Bar is in the future - cannot analyze");
                    return;
                }

                if (barNumber < minBarsNeeded)
                {
                    Output.WriteLine(string.Format("Bar too early in history - need at least {0} bars loaded", minBarsNeeded));
                    return;
                }

                if (barsAgo >= Bars.CurrentBar)
                {
                    Output.WriteLine("Bar index out of range");
                    return;
                }

                // Calculate all the same metrics as CalcBar
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;

                // Try to access MA values - they may not be available for old bars
                double fastMA, slowMA, fastMASlope, slowMASlope;
                try
                {
                    fastMA = m_FastMA[barsAgo];
                    slowMA = m_SlowMA[barsAgo];

                    // Calculate slopes
                    if (barsAgo + SlopeLookback >= Bars.CurrentBar)
                    {
                        Output.WriteLine("Not enough bars for slope calculation");
                        return;
                    }
                    fastMASlope = (m_FastMA[barsAgo] - m_FastMA[barsAgo + SlopeLookback]) / SlopeLookback;
                    slowMASlope = (m_SlowMA[barsAgo] - m_SlowMA[barsAgo + SlopeLookback]) / SlopeLookback;
                }
                catch (Exception ex)
                {
                    Output.WriteLine(string.Format("Cannot access MA data for bar {0} ({1} bars ago). MA data may not be available this far back.",
                        barNumber, barsAgo));
                    Output.WriteLine("Try clicking on a more recent bar.");
                    return;
                }
                double minStrongSlope = tickSize * 2.5;

                // MA separation
                double maSeparation = Math.Abs(fastMA - slowMA);

                // Directional consistency
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

                // Volume ratio
                double avgVolume = 0;
                for (int i = 1; i <= 10 && (barsAgo + i) < Bars.CurrentBar; i++)
                    avgVolume += Bars.Volume[barsAgo + i];
                avgVolume = avgVolume / 10.0;
                double volumeRatio = avgVolume > 0 ? Bars.Volume[barsAgo] / avgVolume : 1.0;

                // Range ratio
                double avgRange = 0;
                for (int i = 1; i <= 10 && (barsAgo + i) < Bars.CurrentBar; i++)
                    avgRange += (Bars.High[barsAgo + i] - Bars.Low[barsAgo + i]);
                avgRange = avgRange / 10.0;
                double currentRange = Bars.High[barsAgo] - Bars.Low[barsAgo];
                double rangeRatio = avgRange > 0 ? currentRange / avgRange : 1.0;

                // CVD
                double cvdSlope = 0;
                if (UseCVDFilter && CVDLookback > 0 && (barsAgo + CVDLookback) < Bars.CurrentBar)
                {
                    double currentCVD = Bars.UpTicks[barsAgo] - Bars.DownTicks[barsAgo];
                    double pastCVD = Bars.UpTicks[barsAgo + CVDLookback] - Bars.DownTicks[barsAgo + CVDLookback];
                    cvdSlope = (currentCVD - pastCVD) / CVDLookback;
                }

                // Previous bar body
                double prevBodyTop = Math.Max(Bars.Open[barsAgo + 1], Bars.Close[barsAgo + 1]);
                double prevBodyBottom = Math.Min(Bars.Open[barsAgo + 1], Bars.Close[barsAgo + 1]);

                // Check which MA the tail is bouncing off
                double nearThreshold = tickSize * 3;
                bool tailNearFastMA = Math.Abs(Bars.Low[barsAgo] - fastMA) <= nearThreshold ||
                                      Math.Abs(Bars.High[barsAgo] - fastMA) <= nearThreshold;
                bool tailNearSlowMA = Math.Abs(Bars.Low[barsAgo] - slowMA) <= nearThreshold ||
                                      Math.Abs(Bars.High[barsAgo] - slowMA) <= nearThreshold;

                // Check trend conditions using bounce-based logic
                bool fastMAQualifies = tailNearFastMA && fastMASlope >= minStrongSlope;
                bool slowMAQualifies = tailNearSlowMA && slowMASlope >= minStrongSlope * 0.6;

                // Volume pattern check
                bool volumePatternOK = true;
                double pullbackVolume = 0, trendVolume = 0, pullbackCVD = 0, trendCVD = 0;
                if (UseVolumePatternFilter && (barsAgo + 6) < Bars.CurrentBar)
                {
                    pullbackVolume = (Bars.Volume[barsAgo + 1] + Bars.Volume[barsAgo + 2] + Bars.Volume[barsAgo + 3]) / 3.0;
                    trendVolume = (Bars.Volume[barsAgo + 4] + Bars.Volume[barsAgo + 5] + Bars.Volume[barsAgo + 6]) / 3.0;
                    bool volumeDeclining = pullbackVolume < trendVolume * 0.85;

                    for (int i = 1; i <= 3 && (barsAgo + i) < Bars.CurrentBar; i++)
                        pullbackCVD += (Bars.UpTicks[barsAgo + i] - Bars.DownTicks[barsAgo + i]);
                    for (int i = 4; i <= 6 && (barsAgo + i) < Bars.CurrentBar; i++)
                        trendCVD += (Bars.UpTicks[barsAgo + i] - Bars.DownTicks[barsAgo + i]);

                    bool cvdPatternOK = true;
                    if (fastMA > slowMA)
                        cvdPatternOK = pullbackCVD <= trendCVD * 0.5;
                    else if (fastMA < slowMA)
                        cvdPatternOK = pullbackCVD >= trendCVD * 0.5;

                    volumePatternOK = volumeDeclining && cvdPatternOK;
                }

                bool strongBullTrend = (fastMAQualifies || slowMAQualifies) &&
                                       fastMA > slowMA &&
                                       maSeparation >= MinMASeparationTicks * tickSize &&
                                       bullPct >= MinDirectionalPct &&
                                       volumeRatio >= MinVolumeMultiplier &&
                                       rangeRatio >= MinRangeMultiplier &&
                                       (!UseVolumePatternFilter || volumePatternOK);

                bool fastMAQualifiesBear = tailNearFastMA && fastMASlope <= -minStrongSlope;
                bool slowMAQualifiesBear = tailNearSlowMA && slowMASlope <= -minStrongSlope * 0.6;

                bool strongBearTrend = (fastMAQualifiesBear || slowMAQualifiesBear) &&
                                       fastMA < slowMA &&
                                       maSeparation >= MinMASeparationTicks * tickSize &&
                                       bearPct >= MinDirectionalPct &&
                                       volumeRatio >= MinVolumeMultiplier &&
                                       rangeRatio >= MinRangeMultiplier &&
                                       (!UseVolumePatternFilter || volumePatternOK);

                // Check if bar would qualify as signal
                bool isBullishBar = Bars.Close[barsAgo] > Bars.Open[barsAgo];
                bool isBearishBar = Bars.Close[barsAgo] < Bars.Open[barsAgo];
                bool tailExtendsBelowPrevBody = Bars.Low[barsAgo] <= prevBodyBottom;
                bool tailExtendsAbovePrevBody = Bars.High[barsAgo] >= prevBodyTop;

                string signalType = "NoSignal";
                string failureReason = "";

                if (isBullishBar && tailExtendsBelowPrevBody)
                {
                    if (strongBullTrend)
                        signalType = "BullishTail";
                    else
                        failureReason = GetTrendFailureReason(true, fastMA, slowMA, fastMASlope, slowMASlope,
                            bullPct, volumeRatio, rangeRatio, tickSize, tailNearFastMA, tailNearSlowMA,
                            fastMAQualifies, slowMAQualifies, volumePatternOK, pullbackVolume, trendVolume);
                }
                else if (isBearishBar && tailExtendsAbovePrevBody)
                {
                    if (strongBearTrend)
                        signalType = "BearishTail";
                    else
                        failureReason = GetTrendFailureReason(false, fastMA, slowMA, fastMASlope, slowMASlope,
                            bearPct, volumeRatio, rangeRatio, tickSize, tailNearFastMA, tailNearSlowMA,
                            fastMAQualifiesBear, slowMAQualifiesBear, volumePatternOK, pullbackVolume, trendVolume);
                }
                else
                {
                    failureReason = "Bar pattern doesn't match: ";
                    if (!isBullishBar && !isBearishBar)
                        failureReason += "Doji bar. ";
                    if (isBullishBar && !tailExtendsBelowPrevBody)
                        failureReason += "Bullish bar but tail doesn't extend below previous body. ";
                    if (isBearishBar && !tailExtendsAbovePrevBody)
                        failureReason += "Bearish bar but tail doesn't extend above previous body. ";
                }

                // Create CSV
                if (!System.IO.Directory.Exists(ClickExportFolder))
                    System.IO.Directory.CreateDirectory(ClickExportFolder);

                string filename = string.Format("bar_analysis_{0:yyyyMMdd_HHmmss}.csv", barTime);
                string filepath = System.IO.Path.Combine(ClickExportFolder, filename);

                var lines = new List<string>();
                lines.Add("BAR ANALYSIS (NO SIGNAL)");
                lines.Add("Category,Value");
                lines.Add(string.Format("BarNumber,{0}", barNumber));
                lines.Add(string.Format("DateTime,{0:yyyy-MM-dd HH:mm:ss}", barTime));
                lines.Add(string.Format("SignalType,{0}", signalType));
                if (!string.IsNullOrEmpty(failureReason))
                    lines.Add(string.Format("FailureReason,\"{0}\"", failureReason));
                lines.Add("");
                lines.Add("PRICE DATA");
                lines.Add("Category,Value");
                lines.Add(string.Format("Open,{0:F2}", Bars.Open[barsAgo]));
                lines.Add(string.Format("High,{0:F2}", Bars.High[barsAgo]));
                lines.Add(string.Format("Low,{0:F2}", Bars.Low[barsAgo]));
                lines.Add(string.Format("Close,{0:F2}", Bars.Close[barsAgo]));
                lines.Add(string.Format("PrevBodyTop,{0:F2}", prevBodyTop));
                lines.Add(string.Format("PrevBodyBottom,{0:F2}", prevBodyBottom));
                lines.Add("");
                lines.Add("MOVING AVERAGES");
                lines.Add("Category,Value");
                lines.Add(string.Format("FastMA(10),{0:F2}", fastMA));
                lines.Add(string.Format("SlowMA(20),{0:F2}", slowMA));
                lines.Add(string.Format("MASeparation(ticks),{0:F2}", maSeparation / tickSize));
                lines.Add(string.Format("FastMASlope(ticks/bar),{0:F4}", fastMASlope / tickSize));
                lines.Add(string.Format("SlowMASlope(ticks/bar),{0:F4}", slowMASlope / tickSize));
                lines.Add("");
                lines.Add("DIRECTIONAL BIAS");
                lines.Add("Category,Value");
                lines.Add(string.Format("BullishBarsPct,{0:F1}%", bullPct));
                lines.Add(string.Format("BearishBarsPct,{0:F1}%", bearPct));
                lines.Add("");
                lines.Add("VOLUME & RANGE");
                lines.Add("Category,Value");
                lines.Add(string.Format("VolumeRatio,{0:F2}x", volumeRatio));
                lines.Add(string.Format("RangeRatio,{0:F2}x", rangeRatio));
                lines.Add("");
                lines.Add("CVD");
                lines.Add("Category,Value");
                lines.Add(string.Format("CVDSlope,{0:F2}", cvdSlope));
                lines.Add("");
                lines.Add("TREND STATUS");
                lines.Add("Category,Value");
                lines.Add(string.Format("StrongBullTrend,{0}", strongBullTrend));
                lines.Add(string.Format("StrongBearTrend,{0}", strongBearTrend));
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
                Output.WriteLine("Exported bar analysis to: " + filename);
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error exporting bar analysis: " + ex.Message);
                Output.WriteLine("Stack trace: " + ex.StackTrace);
                if (ex.InnerException != null)
                    Output.WriteLine("Inner exception: " + ex.InnerException.Message);
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

        protected override void CalcBar()
        {
            // Need enough bars for MAs, slope calculation, and previous bar
            if (Bars.CurrentBar < Math.Max(FastMALength, SlowMALength) + SlopeLookback + 1)
                return;

            double fastMA = m_FastMA.Value;
            double slowMA = m_SlowMA.Value;
            double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;

            // Calculate MA slope (change over lookback period)
            double fastMASlope = (m_FastMA[0] - m_FastMA[SlopeLookback]) / SlopeLookback;
            double slowMASlope = (m_SlowMA[0] - m_SlowMA[SlopeLookback]) / SlopeLookback;

            // Require strong slope - 2.5 ticks per bar minimum (steeper than 45 degrees)
            // This ensures we only trade in clearly trending markets
            double minStrongSlope = tickSize * 2.5;

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
            bool fastMAQualifies = tailNearFastMA && fastMASlope >= minStrongSlope;
            bool slowMAQualifies = tailNearSlowMA && slowMASlope >= minStrongSlope * 0.6; // Slightly lower threshold for 20 MA

            bool strongBullTrend = (fastMAQualifies || slowMAQualifies) &&
                                   fastMA > slowMA &&  // MAs in correct order
                                   maSeparation >= minSeparation &&  // MAs must be separated
                                   bullPct >= MinDirectionalPct &&
                                   volumeRatio >= MinVolumeMultiplier &&
                                   rangeRatio >= MinRangeMultiplier &&
                                   (!UseCVDFilter || cvdRising) &&
                                   (!UseVolumePatternFilter || volumePatternOK);  // Volume pattern check

            // For bearish setup: tail bounces off MA that has strong downward slope
            bool fastMAQualifiesBear = tailNearFastMA && fastMASlope <= -minStrongSlope;
            bool slowMAQualifiesBear = tailNearSlowMA && slowMASlope <= -minStrongSlope * 0.6;

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

            // Show trend state as a line
            // You can manually color this green/red in MultiCharts format settings
            if (strongBullTrend)
            {
                m_TrendIndicator.Set(0, 1);  // Positive = bullish
            }
            else if (strongBearTrend)
            {
                m_TrendIndicator.Set(0, -1);  // Negative = bearish
            }
            else
            {
                m_TrendIndicator.Set(0, 0);  // Zero = neutral
            }

            // BULLISH SETUP: Strong uptrend + blue bar + tail extends to/below previous body
            bool isBullishBar = Bars.Close[0] > Bars.Open[0];
            bool tailExtendsBelowPrevBody = Bars.Low[0] <= prevBodyBottom;

            // Debug: Check for potential signals that are being rejected
            if (isBullishBar && tailExtendsBelowPrevBody && !strongBullTrend)
            {
                Output.WriteLine(string.Format("Bullish tail rejected at {0:HH:mm:ss}: FastMAQual={1}, SlowMAQual={2}, MASep={3:F1}, DirPct={4:F0}, Vol={5:F2}, Range={6:F2}, VolPattern={7}",
                    Bars.Time[0], fastMAQualifies, slowMAQualifies, maSeparation/tickSize, bullPct, volumeRatio, rangeRatio, volumePatternOK));
            }

            if (strongBullTrend && isBullishBar && tailExtendsBelowPrevBody)
            {
                m_BullishTail.Set(0, Bars.Low[0] - (3 * tickSize));

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
                    PrevBodyBottom = prevBodyBottom
                };

                m_SignalsByBar[Bars.CurrentBar] = signalData;
                m_SignalsByTime[Bars.Time[0]] = signalData;  // Store by time for click lookup

                // Draw bar number label if enabled
                if (ShowBarNumbers)
                {
                    var textObj = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.Low[0] - (5 * tickSize)),
                        Bars.CurrentBar.ToString());
                    textObj.Color = Color.Black;
                }
            }

            // BEARISH SETUP: Strong downtrend + red bar + tail extends to/above previous body
            bool isBearishBar = Bars.Close[0] < Bars.Open[0];
            bool tailExtendsAbovePrevBody = Bars.High[0] >= prevBodyTop;

            // Debug: Check for potential signals that are being rejected
            if (isBearishBar && tailExtendsAbovePrevBody && !strongBearTrend)
            {
                Output.WriteLine(string.Format("Bearish tail rejected at {0:HH:mm:ss}: FastMAQual={1}, SlowMAQual={2}, MASep={3:F1}, DirPct={4:F0}, Vol={5:F2}, Range={6:F2}, VolPattern={7}",
                    Bars.Time[0], fastMAQualifiesBear, slowMAQualifiesBear, maSeparation/tickSize, bearPct, volumeRatio, rangeRatio, volumePatternOK));
            }

            if (strongBearTrend && isBearishBar && tailExtendsAbovePrevBody)
            {
                m_BearishTail.Set(0, Bars.High[0] + (3 * tickSize));

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
                    PrevBodyBottom = prevBodyBottom
                };

                m_SignalsByBar[Bars.CurrentBar] = signalData;
                m_SignalsByTime[Bars.Time[0]] = signalData;  // Store by time for click lookup

                // Draw bar number label if enabled
                if (ShowBarNumbers)
                {
                    var textObj = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.High[0] + (5 * tickSize)),
                        Bars.CurrentBar.ToString());
                    textObj.Color = Color.Black;
                }
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
    }
}
