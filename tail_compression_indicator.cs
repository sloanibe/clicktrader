using System;
using System.Drawing;
using System.Linq;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true)]
    public class tail_compression_indicator : IndicatorObject
    {
        // ==================== INPUTS ====================
        
        // Moving Average Settings
        [Input]
        public int FastMALength { get; set; }
        
        [Input]
        public int SlowMALength { get; set; }
        
        // Trend Filter Settings
        [Input]
        public int MinBarsAboveMA { get; set; }
        
        [Input]
        public int MinBullishRatio { get; set; }
        
        [Input]
        public int BullishRatioLookback { get; set; }
        
        // Tail Detection Settings
        [Input]
        public double MinTailDepthTicks { get; set; }
        
        [Input]
        public bool CheckBothBarColors { get; set; }
        
        // Compression Settings
        [Input]
        public double VolumeCompressionPct { get; set; }
        
        [Input]
        public double RangeCompressionPct { get; set; }
        
        [Input]
        public int CompressionLookback { get; set; }
        
        // MA Proximity Settings
        [Input]
        public bool RequireMAProximity { get; set; }
        
        [Input]
        public double MaxTicksFromMA { get; set; }
        
        // CVD (Cumulative Volume Delta) Settings
        [Input]
        public bool UseCVDFilter { get; set; }
        
        [Input]
        public int CVDFilterMode { get; set; }
        
        [Input]
        public double CVDSlopeThreshold { get; set; }
        
        [Input]
        public double CVDCompressionPct { get; set; }
        
        [Input]
        public int CVDLookback { get; set; }
        
        // Visual Settings
        [Input]
        public bool ShowBullishSetups { get; set; }
        
        [Input]
        public bool ShowBearishSetups { get; set; }
        
        [Input]
        public bool EnableDebugOutput { get; set; }
        
        [Input]
        public bool SkipHistoricalCalc { get; set; }
        
        // ==================== VARIABLES ====================
        
        private IPlotObject m_BullishSignal;
        private IPlotObject m_BearishSignal;
        
        private XAverage m_FastMA;
        private XAverage m_SlowMA;
        
        private int m_BullishSignalCount;
        private int m_BearishSignalCount;
        
        // CVD tracking
        private double m_CVD;
        
        // ==================== INITIALIZATION ====================
        
        public tail_compression_indicator(object ctx) : base(ctx)
        {
            // Default values
            FastMALength = 10;
            SlowMALength = 20;
            MinBarsAboveMA = 1;  // Very lenient - just need 1 bar above/below MA
            MinBullishRatio = 50;  // 50% - very lenient
            BullishRatioLookback = 10;
            MinTailDepthTicks = 0;
            CheckBothBarColors = true;
            VolumeCompressionPct = 90;  // Very lenient - allows up to 90% of average
            RangeCompressionPct = 90;  // Very lenient - allows up to 90% of average
            CompressionLookback = 10;
            RequireMAProximity = false;
            MaxTicksFromMA = 10;
            UseCVDFilter = false;
            CVDFilterMode = 1;
            CVDSlopeThreshold = 100;
            CVDCompressionPct = 50;
            CVDLookback = 10;
            ShowBullishSetups = true;
            ShowBearishSetups = true;
            EnableDebugOutput = true;  // Enable by default to see what's happening
            SkipHistoricalCalc = false;
        }
        
        protected override void Create()
        {
            // Create moving averages
            m_FastMA = new XAverage(this);
            m_SlowMA = new XAverage(this);
            
            // Create plot objects for signals (using Point shape with maximum size)
            m_BullishSignal = AddPlot(new PlotAttributes("BullSignal", EPlotShapes.Point, Color.DarkBlue, Color.Empty, 10, 0, true));
            m_BearishSignal = AddPlot(new PlotAttributes("BearSignal", EPlotShapes.Point, Color.DarkRed, Color.Empty, 10, 0, true));
        }
        
        protected override void StartCalc()
        {
            // Configure moving averages - use exponential type
            m_FastMA.Price = Bars.Close;
            m_FastMA.Length = FastMALength;
            
            m_SlowMA.Price = Bars.Close;
            m_SlowMA.Length = SlowMALength;
            
            m_BullishSignalCount = 0;
            m_BearishSignalCount = 0;
            m_CVD = 0;
            
            // Skip historical if requested
            if (SkipHistoricalCalc && !Environment.IsRealTimeCalc)
            {
                return;
            }
        }
        
        protected override void CalcBar()
        {
            // Skip historical if requested
            if (SkipHistoricalCalc && !Environment.IsRealTimeCalc)
            {
                return;
            }
            
            // Need enough bars for calculations
            int minBarsNeeded = Math.Max(Math.Max(FastMALength, SlowMALength), 
                                        Math.Max(CompressionLookback, BullishRatioLookback)) + MinBarsAboveMA + 1;
            
            if (Bars.CurrentBar < minBarsNeeded)
            {
                return;
            }
            
            // Calculate current MA values
            double fastMA = m_FastMA.Value;
            double slowMA = m_SlowMA.Value;
            
            // Get tick size for calculations
            double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
            
            // Update CVD if filter is enabled
            if (UseCVDFilter)
            {
                UpdateCVD();
            }
            
            // ==================== BULLISH SETUP DETECTION ====================
            
            if (ShowBullishSetups)
            {
                bool bullishSetup = CheckBullishSetup(fastMA, slowMA, tickSize);
                
                if (bullishSetup)
                {
                    m_BullishSignalCount++;
                    m_BullishSignal.Set(0, Bars.Low[0] - (3 * tickSize));
                    
                    if (EnableDebugOutput)
                    {
                        Output.WriteLine("BULLISH SIGNAL #{0} at bar {1}, Price: {2}, Low: {3}", 
                            m_BullishSignalCount, Bars.CurrentBar, Bars.Close[0], Bars.Low[0]);
                    }
                }
            }
            
            // ==================== BEARISH SETUP DETECTION ====================
            
            if (ShowBearishSetups)
            {
                bool bearishSetup = CheckBearishSetup(fastMA, slowMA, tickSize);
                
                if (bearishSetup)
                {
                    m_BearishSignalCount++;
                    m_BearishSignal.Set(0, Bars.High[0] + (3 * tickSize));
                    
                    if (EnableDebugOutput)
                    {
                        Output.WriteLine("BEARISH SIGNAL #{0} at bar {1}, Price: {2}, High: {3}", 
                            m_BearishSignalCount, Bars.CurrentBar, Bars.Close[0], Bars.High[0]);
                    }
                }
            }
        }
        
        // ==================== BULLISH SETUP LOGIC ====================
        
        private bool CheckBullishSetup(double fastMA, double slowMA, double tickSize)
        {
            // 1. Check trend alignment (Fast MA > Slow MA)
            if (fastMA <= slowMA)
            {
                if (EnableDebugOutput && Bars.CurrentBar % 100 == 0)
                {
                    Output.WriteLine("Bar {0}: No bullish trend - FastMA ({1:F2}) <= SlowMA ({2:F2})", 
                        Bars.CurrentBar, fastMA, slowMA);
                }
                return false;
            }
            
            // 2. Check bars above MA
            int barsAboveMA = CountConsecutiveBarsAboveMA();
            if (barsAboveMA < MinBarsAboveMA)
            {
                if (EnableDebugOutput && Bars.CurrentBar % 100 == 0)
                {
                    Output.WriteLine("Bar {0}: Not enough bars above MA - {1} of {2} required", 
                        Bars.CurrentBar, barsAboveMA, MinBarsAboveMA);
                }
                return false;
            }
            
            // 3. Check bullish ratio
            double bullishRatio = CalculateBullishRatio(BullishRatioLookback);
            if (bullishRatio < MinBullishRatio)
            {
                if (EnableDebugOutput && Bars.CurrentBar % 100 == 0)
                {
                    Output.WriteLine("Bar {0}: Bullish ratio too low - {1:F1}% of {2}% required", 
                        Bars.CurrentBar, bullishRatio, MinBullishRatio);
                }
                return false;
            }
            
            // 4. Check for tail (lower wick)
            // A bullish tail means the low extends below the body
            double bodyBottom = Math.Min(Bars.Open[0], Bars.Close[0]);
            double tailLength = bodyBottom - Bars.Low[0];
            double minTailLength = MinTailDepthTicks * tickSize;
            
            // Also check if it extends below previous bar's low
            bool extendsBelowPrevious = Bars.Low[0] < Bars.Low[1];
            
            if (EnableDebugOutput && tailLength > 0)
            {
                Output.WriteLine("Bar {0}: BULLISH TAIL CHECK - TailLength: {1:F2}, MinRequired: {2:F2}, ExtendsBelowPrev: {3}, Low[0]: {4:F2}, Low[1]: {5:F2}",
                    Bars.CurrentBar, tailLength, minTailLength, extendsBelowPrevious, Bars.Low[0], Bars.Low[1]);
            }
            
            if (!extendsBelowPrevious)
            {
                return false; // Didn't go lower than previous bar
            }
            
            if (tailLength < minTailLength)
            {
                return false; // Tail not long enough
            }
            
            // 5. Check bar color if required
            if (!CheckBothBarColors)
            {
                if (Bars.Close[0] <= Bars.Open[0])
                {
                    return false; // Must be bullish bar
                }
            }
            
            // 6. Check MA proximity if required
            if (RequireMAProximity)
            {
                double distanceToMA = Math.Abs(Bars.Low[0] - fastMA);
                if (distanceToMA > MaxTicksFromMA * tickSize)
                {
                    return false; // Tail too far from MA
                }
            }
            
            // 7. Check volume compression
            double avgVolume = CalculateAverageVolume(CompressionLookback);
            double currentVolume = Bars.Volume[0];
            double volumePct = 0;
            
            // Safety check for division
            if (avgVolume > 0.0001)
            {
                volumePct = (currentVolume / avgVolume) * 100.0;
                
                if (volumePct >= VolumeCompressionPct)
                {
                    if (EnableDebugOutput)
                    {
                        Output.WriteLine("Bar {0}: Volume not compressed - {1:F1}% of avg (need < {2}%)", 
                            Bars.CurrentBar, volumePct, VolumeCompressionPct);
                    }
                    return false;
                }
            }
            
            // 8. Check range compression
            double avgRange = CalculateAverageRange(CompressionLookback);
            double currentRange = Bars.High[0] - Bars.Low[0];
            double rangePct = 0;
            
            // Safety check for division
            if (avgRange > 0.0001)
            {
                rangePct = (currentRange / avgRange) * 100.0;
                
                if (rangePct >= RangeCompressionPct)
                {
                    if (EnableDebugOutput)
                    {
                        Output.WriteLine("Bar {0}: Range not compressed - {1:F1}% of avg (need < {2}%)", 
                            Bars.CurrentBar, rangePct, RangeCompressionPct);
                    }
                    return false;
                }
            }
            
            // 9. Check CVD filter if enabled
            if (UseCVDFilter)
            {
                if (!CheckBullishCVD())
                {
                    if (EnableDebugOutput)
                    {
                        Output.WriteLine("Bar {0}: CVD filter failed for bullish setup", Bars.CurrentBar);
                    }
                    return false;
                }
            }
            
            // All conditions met!
            if (EnableDebugOutput)
            {
                Output.WriteLine("Bar {0}: BULLISH SETUP CONFIRMED - Vol: {1:F1}%, Range: {2:F1}%, BullRatio: {3:F1}%", 
                    Bars.CurrentBar, volumePct, rangePct, bullishRatio);
            }
            
            return true;
        }
        
        // ==================== BEARISH SETUP LOGIC ====================
        
        private bool CheckBearishSetup(double fastMA, double slowMA, double tickSize)
        {
            // 1. Check trend alignment (Fast MA < Slow MA)
            if (fastMA >= slowMA)
            {
                return false;
            }
            
            // 2. Check bars below MA
            int barsBelowMA = CountConsecutiveBarsBelowMA();
            if (barsBelowMA < MinBarsAboveMA)
            {
                return false;
            }
            
            // 3. Check bearish ratio
            double bearishRatio = CalculateBearishRatio(BullishRatioLookback);
            if (bearishRatio < MinBullishRatio)
            {
                return false;
            }
            
            // 4. Check for tail (upper wick)
            // A bearish tail means the high extends above the body
            double bodyTop = Math.Max(Bars.Open[0], Bars.Close[0]);
            double tailLength = Bars.High[0] - bodyTop;
            double minTailLength = MinTailDepthTicks * tickSize;
            
            // Also check if it extends above previous bar's high
            bool extendsAbovePrevious = Bars.High[0] > Bars.High[1];
            
            if (EnableDebugOutput && tailLength > 0)
            {
                Output.WriteLine("Bar {0}: BEARISH TAIL CHECK - TailLength: {1:F2}, MinRequired: {2:F2}, ExtendsAbovePrev: {3}, High[0]: {4:F2}, High[1]: {5:F2}",
                    Bars.CurrentBar, tailLength, minTailLength, extendsAbovePrevious, Bars.High[0], Bars.High[1]);
            }
            
            if (!extendsAbovePrevious)
            {
                return false; // Didn't go higher than previous bar
            }
            
            if (tailLength < minTailLength)
            {
                return false; // Tail not long enough
            }
            
            // 5. Check bar color if required
            if (!CheckBothBarColors)
            {
                if (Bars.Close[0] >= Bars.Open[0])
                {
                    return false; // Must be bearish bar
                }
            }
            
            // 6. Check MA proximity if required
            if (RequireMAProximity)
            {
                double distanceToMA = Math.Abs(Bars.High[0] - fastMA);
                if (distanceToMA > MaxTicksFromMA * tickSize)
                {
                    return false; // Tail too far from MA
                }
            }
            
            // 7. Check volume compression
            double avgVolume = CalculateAverageVolume(CompressionLookback);
            double currentVolume = Bars.Volume[0];
            
            // Safety check for division
            if (avgVolume > 0.0001)
            {
                double volumePct = (currentVolume / avgVolume) * 100.0;
                
                if (volumePct >= VolumeCompressionPct)
                {
                    return false;
                }
            }
            
            // 8. Check range compression
            double avgRange = CalculateAverageRange(CompressionLookback);
            double currentRange = Bars.High[0] - Bars.Low[0];
            
            // Safety check for division
            if (avgRange > 0.0001)
            {
                double rangePct = (currentRange / avgRange) * 100.0;
                
                if (rangePct >= RangeCompressionPct)
                {
                    return false;
                }
            }
            
            // 9. Check CVD filter if enabled
            if (UseCVDFilter)
            {
                if (!CheckBearishCVD())
                {
                    return false;
                }
            }
            
            return true;
        }
        
        // ==================== HELPER METHODS ====================
        
        private int CountConsecutiveBarsAboveMA()
        {
            int count = 0;
            for (int i = 0; i < MinBarsAboveMA + 5 && i < Bars.CurrentBar; i++)
            {
                // Compare each bar's close to that bar's MA value
                double maValue = m_FastMA[i];
                if (Bars.Close[i] > maValue)
                {
                    count++;
                }
                else
                {
                    break;
                }
            }
            return count;
        }
        
        private int CountConsecutiveBarsBelowMA()
        {
            int count = 0;
            for (int i = 0; i < MinBarsAboveMA + 5 && i < Bars.CurrentBar; i++)
            {
                // Compare each bar's close to that bar's MA value
                double maValue = m_FastMA[i];
                if (Bars.Close[i] < maValue)
                {
                    count++;
                }
                else
                {
                    break;
                }
            }
            return count;
        }
        
        private double CalculateBullishRatio(int lookback)
        {
            int bullishBars = 0;
            int totalBars = Math.Min(lookback, Bars.CurrentBar);
            
            for (int i = 0; i < totalBars; i++)
            {
                if (Bars.Close[i] > Bars.Open[i])
                {
                    bullishBars++;
                }
            }
            
            return (bullishBars / (double)totalBars) * 100.0;
        }
        
        private double CalculateBearishRatio(int lookback)
        {
            int bearishBars = 0;
            int totalBars = Math.Min(lookback, Bars.CurrentBar);
            
            for (int i = 0; i < totalBars; i++)
            {
                if (Bars.Close[i] < Bars.Open[i])
                {
                    bearishBars++;
                }
            }
            
            return (bearishBars / (double)totalBars) * 100.0;
        }
        
        private double CalculateAverageVolume(int lookback)
        {
            double sum = 0;
            int count = Math.Min(lookback, Bars.CurrentBar);
            
            for (int i = 1; i <= count; i++) // Start at 1 to exclude current bar
            {
                sum += Bars.Volume[i];
            }
            
            // Return at least 1.0 to prevent division by zero
            return count > 0 && sum > 0 ? sum / count : 1.0;
        }
        
        private double CalculateAverageRange(int lookback)
        {
            double sum = 0;
            int count = Math.Min(lookback, Bars.CurrentBar);
            
            for (int i = 1; i <= count; i++) // Start at 1 to exclude current bar
            {
                sum += (Bars.High[i] - Bars.Low[i]);
            }
            
            // Return at least 1.0 to prevent division by zero
            return count > 0 && sum > 0 ? sum / count : 1.0;
        }
        
        // ==================== CVD METHODS ====================
        
        private void UpdateCVD()
        {
            // Calculate delta from upticks and downticks
            double buyVolume = Bars.UpTicks[0];
            double sellVolume = Bars.DownTicks[0];
            double delta = buyVolume - sellVolume;
            
            // Update cumulative volume delta
            m_CVD += delta;
            
            if (EnableDebugOutput && Bars.CurrentBar % 100 == 0)
            {
                Output.WriteLine("Bar {0}: CVD = {1:F0}, Delta = {2:F0} (Buy: {3:F0}, Sell: {4:F0})",
                    Bars.CurrentBar, m_CVD, delta, buyVolume, sellVolume);
            }
        }
        
        private bool CheckBullishCVD()
        {
            // Need at least 2 bars for comparison
            if (Bars.CurrentBar < 2)
            {
                return true;
            }
            
            double currentDelta = Bars.UpTicks[0] - Bars.DownTicks[0];
            double prevDelta = Bars.UpTicks[1] - Bars.DownTicks[1];
            
            // Mode 1: CVD Slope Check
            // For bullish setup, we don't want CVD falling sharply (aggressive selling)
            if (CVDFilterMode == 1)
            {
                if (currentDelta < -CVDSlopeThreshold)
                {
                    if (EnableDebugOutput)
                    {
                        Output.WriteLine("Bar {0}: CVD slope too negative - Delta: {1:F0} (threshold: {2:F0})",
                            Bars.CurrentBar, currentDelta, -CVDSlopeThreshold);
                    }
                    return false;
                }
            }
            // Mode 2: CVD Compression
            // Delta should be small (low conviction move)
            else if (CVDFilterMode == 2)
            {
                double avgDelta = CalculateAverageDelta(CVDLookback);
                double deltaPct = Math.Abs(currentDelta) / Math.Max(avgDelta, 1.0) * 100.0;
                
                if (deltaPct >= CVDCompressionPct)
                {
                    if (EnableDebugOutput)
                    {
                        Output.WriteLine("Bar {0}: CVD not compressed - {1:F1}% of avg (need < {2}%)",
                            Bars.CurrentBar, deltaPct, CVDCompressionPct);
                    }
                    return false;
                }
            }
            // Mode 3: CVD Divergence
            // Price making lower low, but CVD not falling (bullish divergence)
            else if (CVDFilterMode == 3)
            {
                if (Bars.Low[0] < Bars.Low[1] && currentDelta < prevDelta)
                {
                    if (EnableDebugOutput)
                    {
                        Output.WriteLine("Bar {0}: No bullish CVD divergence - Price lower but CVD also lower",
                            Bars.CurrentBar);
                    }
                    return false;
                }
            }
            
            return true;
        }
        
        private bool CheckBearishCVD()
        {
            // Need at least 2 bars for comparison
            if (Bars.CurrentBar < 2)
            {
                return true;
            }
            
            double currentDelta = Bars.UpTicks[0] - Bars.DownTicks[0];
            double prevDelta = Bars.UpTicks[1] - Bars.DownTicks[1];
            
            // Mode 1: CVD Slope Check
            // For bearish setup, we don't want CVD rising sharply (aggressive buying)
            if (CVDFilterMode == 1)
            {
                if (currentDelta > CVDSlopeThreshold)
                {
                    return false;
                }
            }
            // Mode 2: CVD Compression
            // Delta should be small (low conviction move)
            else if (CVDFilterMode == 2)
            {
                double avgDelta = CalculateAverageDelta(CVDLookback);
                double deltaPct = Math.Abs(currentDelta) / Math.Max(avgDelta, 1.0) * 100.0;
                
                if (deltaPct >= CVDCompressionPct)
                {
                    return false;
                }
            }
            // Mode 3: CVD Divergence
            // Price making higher high, but CVD not rising (bearish divergence)
            else if (CVDFilterMode == 3)
            {
                if (Bars.High[0] > Bars.High[1] && currentDelta > prevDelta)
                {
                    return false;
                }
            }
            
            return true;
        }
        
        private double CalculateAverageDelta(int lookback)
        {
            double sum = 0;
            int count = Math.Min(lookback, Bars.CurrentBar);
            
            for (int i = 1; i <= count; i++)
            {
                double delta = Math.Abs(Bars.UpTicks[i] - Bars.DownTicks[i]);
                sum += delta;
            }
            
            return count > 0 ? sum / count : 1.0;
        }
    }
}
