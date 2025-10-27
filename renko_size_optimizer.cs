using System;
using System.Drawing;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true), MouseEvents(true)]
    public class renko_size_optimizer : IndicatorObject
    {
        [Input]
        public int LookbackBars { get; set; }

        [Input]
        public int ATRLength { get; set; }

        [Input]
        public double TargetBarTimeSeconds { get; set; }

        [Input]
        public double ATRMultiplier { get; set; }

        [Input]
        public bool EnableOutput { get; set; }

        [Input]
        public double MinRenkoSize { get; set; }

        [Input]
        public double MaxRenkoSize { get; set; }

        public renko_size_optimizer(object ctx) : base(ctx)
        {
            LookbackBars = 50;
            ATRLength = 14;
            TargetBarTimeSeconds = 4.0;
            ATRMultiplier = 1.0;
            EnableOutput = false;  // Disabled by default to reduce noise
            MinRenkoSize = 5;      // Minimum practical size
            MaxRenkoSize = 20;     // Maximum practical size
        }

        protected override void Create()
        {
            // No plots - indicator only outputs to console
        }

        protected override void StartCalc()
        {
            if (EnableOutput)
            {
                Output.WriteLine("[RENKO OPTIMIZER] Started - LookbackBars: " + LookbackBars + ", TargetTime: " + TargetBarTimeSeconds + "s, ATRMult: " + ATRMultiplier);
            }
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            try
            {
                // Calculate metrics for the clicked bar
                int barsAgo = Bars.CurrentBar - arg.bar_number;
                
                if (barsAgo < 0 || barsAgo >= Bars.CurrentBar)
                {
                    Output.WriteLine("[RENKO OPTIMIZER] Invalid bar clicked");
                    return;
                }

                // Need enough bars for lookback AND ATR calculation
                int minBarsNeeded = Math.Max(LookbackBars, ATRLength) + 1;
                if (barsAgo + minBarsNeeded >= Bars.CurrentBar)
                {
                    Output.WriteLine("[RENKO OPTIMIZER] Not enough historical data for this bar (need " + minBarsNeeded + " bars)");
                    return;
                }

                double atr = 0;
                double avgBarTime = 0;
                double recommendedSize = 0;
                double currentSize = 0;

                try
                {
                    // Calculate ATR at this point
                    atr = CalculateATRAtBar(barsAgo, ATRLength);
                    Output.WriteLine("[RENKO OPTIMIZER] ATR calculated: " + atr);
                }
                catch (Exception ex)
                {
                    Output.WriteLine("[RENKO OPTIMIZER] Error calculating ATR: " + ex.Message);
                    atr = 0;
                }

                try
                {
                    // Calculate average bar time at this point
                    avgBarTime = CalculateAverageBarTimeAtBar(barsAgo, LookbackBars);
                    Output.WriteLine("[RENKO OPTIMIZER] AvgBarTime calculated: " + avgBarTime);
                }
                catch (Exception ex)
                {
                    Output.WriteLine("[RENKO OPTIMIZER] Error calculating AvgBarTime: " + ex.Message);
                    avgBarTime = 0;
                }

                try
                {
                    // Calculate recommended size
                    recommendedSize = CalculateRecommendedSize(atr, avgBarTime);
                    currentSize = EstimateCurrentRenkoSize(barsAgo);
                    Output.WriteLine("[RENKO OPTIMIZER] Recommended size calculated: " + recommendedSize);
                }
                catch (Exception ex)
                {
                    Output.WriteLine("[RENKO OPTIMIZER] Error calculating recommended size: " + ex.Message);
                    recommendedSize = 10;
                }

                try
                {
                    // Draw popup on chart
                    DrawRecommendationPopup(barsAgo, recommendedSize, atr, avgBarTime);
                    Output.WriteLine("[RENKO OPTIMIZER] Popup drawn successfully");
                }
                catch (Exception ex)
                {
                    Output.WriteLine("[RENKO OPTIMIZER] Error drawing popup: " + ex.Message);
                }
                
                // Also output to console
                Output.WriteLine(string.Format(
                    "[RENKO OPTIMIZER] Bar {0} ({1:yyyy-MM-dd HH:mm:ss}): ATR={2:F2} | AvgBarTime={3:F2}s | Current Size={4:F1} | Recommended={5:F0}",
                    arg.bar_number,
                    Bars.Time[barsAgo],
                    atr,
                    avgBarTime,
                    currentSize,
                    recommendedSize
                ));
            }
            catch (Exception ex)
            {
                Output.WriteLine("[RENKO OPTIMIZER] Unexpected error in OnMouseEvent: " + ex.Message);
                if (ex.InnerException != null)
                    Output.WriteLine("[RENKO OPTIMIZER] Inner Exception: " + ex.InnerException.Message);
            }
        }

        protected override void CalcBar()
        {
            // Skip if we don't have enough bars
            if (Bars.CurrentBar < Math.Max(LookbackBars, ATRLength) + 1)
                return;

            // Calculate ATR
            double atr = CalculateATR(ATRLength);

            // Calculate average bar formation time over lookback period
            double avgBarTime = CalculateAverageBarTime(LookbackBars);

            // Calculate recommended Renko size
            double recommendedSize = CalculateRecommendedSize(atr, avgBarTime);

            // Output recommendation on every bar (only if EnableOutput is true)
            if (EnableOutput)
            {
                OutputRecommendation(atr, avgBarTime, recommendedSize);
            }
        }

        private double CalculateATRAtBar(int barsAgo, int length)
        {
            // Calculate ATR looking back from a specific bar
            double sumTR = 0;
            int count = 0;
            
            for (int i = barsAgo; i < barsAgo + length && i < Bars.CurrentBar; i++)
            {
                double tr = CalculateTrueRange(i);
                sumTR += tr;
                count++;
            }
            
            return count > 0 ? sumTR / count : 0;
        }

        private double CalculateAverageBarTimeAtBar(int barsAgo, int lookbackBars)
        {
            // Calculate average bar time looking back from a specific bar
            if (barsAgo + lookbackBars >= Bars.CurrentBar)
                return 0;

            double totalSeconds = 0;
            int barCount = 0;

            for (int i = barsAgo; i < barsAgo + lookbackBars && i + 1 < Bars.CurrentBar; i++)
            {
                DateTime currentTime = Bars.Time[i];
                DateTime prevTime = Bars.Time[i + 1];
                
                TimeSpan timeDiff = currentTime - prevTime;
                totalSeconds += timeDiff.TotalSeconds;
                barCount++;
            }

            return barCount > 0 ? totalSeconds / barCount : 0;
        }

        private double EstimateCurrentRenkoSize(int barsAgo)
        {
            // Estimate Renko size at a specific bar
            if (barsAgo >= Bars.CurrentBar)
                return 10;

            double avgRange = 0;
            int sampleSize = Math.Min(10, Bars.CurrentBar - barsAgo);
            
            for (int i = barsAgo; i < barsAgo + sampleSize && i < Bars.CurrentBar; i++)
            {
                avgRange += (Bars.High[i] - Bars.Low[i]);
            }
            
            return sampleSize > 0 ? avgRange / sampleSize : 10;
        }

        private double CalculateATR(int length)
        {
            double sumTR = 0;
            int count = 0;
            
            for (int i = 0; i < length && i < Bars.CurrentBar; i++)
            {
                double tr = CalculateTrueRange(i);
                sumTR += tr;
                count++;
            }
            
            return count > 0 ? sumTR / count : 0;
        }

        private double CalculateTrueRange(int barsAgo)
        {
            double high = Bars.High[barsAgo];
            double low = Bars.Low[barsAgo];
            double prevClose = barsAgo + 1 < Bars.CurrentBar ? Bars.Close[barsAgo + 1] : Bars.Close[barsAgo];

            double tr1 = high - low;
            double tr2 = Math.Abs(high - prevClose);
            double tr3 = Math.Abs(low - prevClose);

            return Math.Max(tr1, Math.Max(tr2, tr3));
        }

        private double CalculateAverageBarTime(int lookbackBars)
        {
            // Calculate average time between bars over the lookback period
            if (Bars.CurrentBar < lookbackBars + 1)
                return 0;

            double totalSeconds = 0;
            int barCount = 0;

            for (int i = 0; i < lookbackBars && i + 1 < Bars.CurrentBar; i++)
            {
                DateTime currentTime = Bars.Time[i];
                DateTime prevTime = Bars.Time[i + 1];
                
                TimeSpan timeDiff = currentTime - prevTime;
                totalSeconds += timeDiff.TotalSeconds;
                barCount++;
            }

            return barCount > 0 ? totalSeconds / barCount : 0;
        }

        private double CalculateRecommendedSize(double atr, double avgBarTime)
        {
            // Strategy 1: Based on ATR (volatility)
            double atrBasedSize = atr * ATRMultiplier;

            // Strategy 2: Based on target bar timing
            // If current bars form in avgBarTime seconds, and we want them in TargetBarTimeSeconds,
            // we need to scale the Renko size proportionally
            double timingScaleFactor = avgBarTime > 0 ? TargetBarTimeSeconds / avgBarTime : 1.0;
            
            // Get current Renko size estimate
            double currentRenkoSize = EstimateCurrentRenkoSize();
            double timingBasedSize = currentRenkoSize * timingScaleFactor;

            // Blend both strategies (50/50 weight)
            double recommendedSize = (atrBasedSize + timingBasedSize) / 2.0;

            // Round to nearest whole number for practical use
            recommendedSize = Math.Round(recommendedSize, 0);

            // Apply min/max bounds
            if (recommendedSize < MinRenkoSize)
                recommendedSize = MinRenkoSize;
            if (recommendedSize > MaxRenkoSize)
                recommendedSize = MaxRenkoSize;

            return recommendedSize;
        }

        private double EstimateCurrentRenkoSize()
        {
            // Estimate current Renko size by looking at average bar range
            if (Bars.CurrentBar < 10)
                return 10;

            double avgRange = 0;
            int sampleSize = Math.Min(10, Bars.CurrentBar);
            
            for (int i = 0; i < sampleSize; i++)
            {
                avgRange += (Bars.High[i] - Bars.Low[i]);
            }
            
            return sampleSize > 0 ? avgRange / sampleSize : 10;
        }

        private void OutputRecommendation(double atr, double avgBarTime, double recommendedSize)
        {
            try
            {
                double currentSize = EstimateCurrentRenkoSize();
                string recommendation = recommendedSize > currentSize ? "INCREASE" : 
                                       recommendedSize < currentSize ? "DECREASE" : "KEEP";

                Output.WriteLine(string.Format(
                    "[{0:HH:mm:ss}] ATR: {1:F2} | BarTime: {2:F2}s | AvgRange: {3:F1} | Recommended: {4:F0} ({5})",
                    Bars.Time[0],
                    atr,
                    avgBarTime,
                    currentSize,
                    recommendedSize,
                    recommendation
                ));
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in OutputRecommendation: " + ex.Message);
            }
        }

        private void DrawRecommendationPopup(int barsAgo, double recommendedSize, double atr, double avgBarTime)
        {
            try
            {
                // Position popup above the bar
                double popupPrice = Bars.High[barsAgo] + (Bars.Info.MinMove / Bars.Info.PriceScale * 20);
                
                // Create popup text
                string popupText = string.Format(
                    "RECOMMENDED RENKO SIZE\n" +
                    "{0:F0} ticks\n" +
                    "ATR: {1:F2} | Bar Time: {2:F2}s",
                    recommendedSize,
                    atr,
                    avgBarTime
                );
                
                // Draw the popup text on the chart
                var textObj = DrwText.Create(new ChartPoint(Bars.Time[barsAgo], popupPrice), popupText);
                textObj.Color = Color.Blue;
                textObj.VStyle = ETextStyleV.Above;
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error drawing popup: " + ex.Message);
            }
        }
    }
}
