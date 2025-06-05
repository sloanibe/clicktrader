using System;
using System.Drawing;
using PowerLanguage.Function;
using System.Collections.Generic;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    [UpdateOnEveryTick(true)]
    public class renko_long_tail : IndicatorObject
    {
        // Input parameter to control whether to show only live data
        /// <summary>
        /// If true, the indicator will only show signals on live bars. If false, it will show signals on all bars (historical and live).
        /// </summary>
        [Input]
        public bool ShowOnlyLiveData { get; set; }

        /// <summary>
        /// Length of the moving average used for filtering signals
        /// </summary>
        [Input]
        public int MALength { get; set; }

        /// <summary>
        /// Number of consecutive bars that must be above/below MA for signal
        /// </summary>
        [Input]
        public int MABarCount { get; set; }

        // Variables to track the current arrows
        private IArrowObject currentBullishArrow = null;
        private IArrowObject currentBearishArrow = null;
        private double lastBullishLow = double.MaxValue;
        private double lastBearishHigh = double.MinValue;

        // Variables to track horizontal lines
        private ITrendLineObject currentBullishLine = null;
        private ITrendLineObject currentBearishLine = null;

        // Moving average variables
        private XAverage xAvg;
        private double currentMAValue = 0;

        public renko_long_tail(object _ctx) : base(_ctx)
        {
            // Default to showing only live data
            ShowOnlyLiveData = true;
            MALength = 12; // Default MA length to 12 periods
            MABarCount = 3; // Default to requiring 3 bars above/below MA
        }

        protected override void Create()
        {
            // Initialize the moving average
            xAvg = new XAverage(this);
        }

        protected override void StartCalc()
        {
            // Reset tracking variables
            currentBullishArrow = null;
            currentBearishArrow = null;
            lastBullishLow = double.MaxValue;
            lastBearishHigh = double.MinValue;

            // Clear any existing horizontal lines
            ClearHorizontalLines();

            // Initialize the moving average
            xAvg.Price = Bars.Close;
            xAvg.Length = MALength;
        }

        protected override void CalcBar()
        {
            // Skip calculations if we don't have enough bars
            if (Bars.CurrentBar < 2)
                return;

            // Calculate the MA value
            currentMAValue = xAvg.Value;

            // Check if enough bars are above/below the MA
            bool enoughBarsAboveMA = true;
            bool enoughBarsBelowMA = true;

            // Check the required number of bars
            for (int i = 0; i < MABarCount; i++)
            {
                if (i < Bars.CurrentBar)
                {
                    if (Bars.Close[i] <= currentMAValue)
                        enoughBarsAboveMA = false;

                    if (Bars.Close[i] >= currentMAValue)
                        enoughBarsBelowMA = false;
                }
            }

            // Different logic based on whether we're showing only live data or all data
            if (ShowOnlyLiveData)
            {
                // Only process live bars
                if (Bars.Status != EBarState.Close)
                {
                    // Get data for bar before previous bar (2 bars ago)
                    double bar2Open = Bars.Open[2];
                    double bar2Close = Bars.Close[2];
                    bool bar2ClosedUp = bar2Close > bar2Open;
                    bool bar2ClosedDown = bar2Close < bar2Open;

                    // Get previous bar data
                    double prevBarOpen = Bars.Open[1];
                    double prevBarClose = Bars.Close[1];
                    bool prevBarClosedUp = prevBarClose > prevBarOpen;
                    bool prevBarClosedDown = prevBarClose < prevBarOpen;

                    // Get current live bar data
                    double currBarOpen = Bars.Open[0];
                    double currBarHigh = Bars.High[0];
                    double currBarLow = Bars.Low[0];
                    DateTime currBarTime = Bars.Time[0];
                    double currBarClose = Bars.Close[0]; // Current close for MA comparison

                    // Get previous bar close for MA comparison
                    double prevBarCloseMA = Bars.Close[1];

                    // Get bar 2 close for MA comparison
                    double bar2CloseMA = Bars.Close[2];

                    // BULLISH PATTERN for live bar:
                    // Previous 2 bars closed up, current live bar's low reaches or goes beyond previous bar's open,
                    // AND required number of bars are above the MA
                    if (bar2ClosedUp && prevBarClosedUp && currBarLow <= prevBarOpen && enoughBarsAboveMA)
                    {
                        // Check if we need to update the arrow (if the low has changed)
                        if (currBarLow < lastBullishLow)
                        {
                            // Remove the old arrow if it exists
                            if (currentBullishArrow != null)
                            {
                                currentBullishArrow.Delete();
                            }

                            // Create a chart point slightly below the low of the current bar for better visibility
                            double offset = 10 * Bars.Point;
                            ChartPoint arrowPoint = new ChartPoint(currBarTime, currBarLow - offset);

                            // Create a green up arrow
                            currentBullishArrow = DrwArrow.Create(arrowPoint, false);
                            // Set the arrow color to green
                            currentBullishArrow.Color = Color.Green;
                            // Make the arrow significantly larger for better visibility
                            currentBullishArrow.Size = 5;

                            // Draw horizontal line one tick ABOVE the previous bar's close
                            double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                            double lineLevel = prevBarClose + tickSize;

                            // Remove old line if it exists
                            if (currentBullishLine != null)
                            {
                                currentBullishLine.Delete();
                            }

                            // Calculate start and end points for the trend line
                            DateTime startTime = currBarTime;
                            DateTime endTime = startTime.AddDays(365); // Extend a year into the future

                            ChartPoint lineStart = new ChartPoint(startTime, lineLevel);
                            ChartPoint lineEnd = new ChartPoint(endTime, lineLevel);

                            // Create the horizontal line
                            currentBullishLine = DrwTrendLine.Create(lineStart, lineEnd);
                            currentBullishLine.Color = Color.White;

                            // Update the last low
                            lastBullishLow = currBarLow;
                        }
                    }
                    else
                    {
                        // Reset bullish tracking if conditions no longer met
                        if (currentBullishArrow != null)
                        {
                            currentBullishArrow.Delete();
                            currentBullishArrow = null;
                        }

                        // Remove the horizontal line
                        if (currentBullishLine != null)
                        {
                            currentBullishLine.Delete();
                            currentBullishLine = null;
                        }

                        lastBullishLow = double.MaxValue;
                    }

                    // BEARISH PATTERN for live bar:
                    // Previous 2 bars closed down, current live bar's high reaches or goes beyond previous bar's open,
                    // AND required number of bars are below the MA
                    if (bar2ClosedDown && prevBarClosedDown && currBarHigh >= prevBarOpen && enoughBarsBelowMA)
                    {
                        // Check if we need to update the arrow (if the high has changed)
                        if (currBarHigh > lastBearishHigh)
                        {
                            // Remove the old arrow if it exists
                            if (currentBearishArrow != null)
                            {
                                currentBearishArrow.Delete();
                            }

                            // Create a chart point slightly above the high of the current bar for better visibility
                            double offset = 10 * Bars.Point;
                            ChartPoint arrowPoint = new ChartPoint(currBarTime, currBarHigh + offset);

                            // Create a red down arrow
                            currentBearishArrow = DrwArrow.Create(arrowPoint, true);
                            // Set the arrow color to red
                            currentBearishArrow.Color = Color.Red;
                            // Make the arrow significantly larger for better visibility
                            currentBearishArrow.Size = 5;

                            // Draw horizontal line one tick BELOW the previous bar's close
                            double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                            double lineLevel = prevBarClose - tickSize;

                            // Remove old line if it exists
                            if (currentBearishLine != null)
                            {
                                currentBearishLine.Delete();
                            }

                            // Calculate start and end points for the trend line
                            DateTime startTime = currBarTime;
                            DateTime endTime = startTime.AddDays(365); // Extend a year into the future

                            ChartPoint lineStart = new ChartPoint(startTime, lineLevel);
                            ChartPoint lineEnd = new ChartPoint(endTime, lineLevel);

                            // Create the horizontal line
                            currentBearishLine = DrwTrendLine.Create(lineStart, lineEnd);
                            currentBearishLine.Color = Color.White;

                            // Update the last high
                            lastBearishHigh = currBarHigh;
                        }
                    }
                    else
                    {
                        // Reset bearish tracking if conditions no longer met
                        if (currentBearishArrow != null)
                        {
                            currentBearishArrow.Delete();
                            currentBearishArrow = null;
                        }

                        // Remove the horizontal line
                        if (currentBearishLine != null)
                        {
                            currentBearishLine.Delete();
                            currentBearishLine = null;
                        }

                        lastBearishHigh = double.MinValue;
                    }

                    // Note: We don't need to reset on every bar, as we're already handling this properly
                    // The reset is now handled in StartCalc and when conditions are no longer met
                    // Removing the incorrect comparison that was causing the error
                }
            }
            else
            {
                // Original logic for historical data
                // Get previous bar data
                double prevBarOpen = Bars.Open[1];
                double prevBarClose = Bars.Close[1];
                bool prevBarClosedUp = prevBarClose > prevBarOpen;
                bool prevBarClosedDown = prevBarClose < prevBarOpen;

                // Get current bar data
                double currBarOpen = Bars.Open[0];
                double currBarClose = Bars.Close[0];
                double currBarHigh = Bars.High[0];
                double currBarLow = Bars.Low[0];
                bool currBarClosedUp = currBarClose > currBarOpen;
                bool currBarClosedDown = currBarClose < currBarOpen;

                // Get bar 2 close for MA comparison
                double bar2CloseMA = Bars.Close[2];

                // BULLISH PATTERN: Previous bar closed up, current bar's low reaches or goes beyond previous bar's open,
                // current bar closes up, AND required number of bars are above the MA
                if (prevBarClosedUp && currBarLow <= prevBarOpen && currBarClosedUp && enoughBarsAboveMA)
                {
                    // Create a chart point slightly below the low of the current bar for better visibility
                    double offset = 10 * Bars.Point;
                    ChartPoint arrowPoint = new ChartPoint(Bars.Time[0], currBarLow - offset);

                    // Create a green up arrow
                    IArrowObject arrow = DrwArrow.Create(arrowPoint, false);
                    // Set the arrow color to green
                    arrow.Color = Color.Green;
                    // Make the arrow significantly larger for better visibility
                    arrow.Size = 5;

                    // Draw horizontal line one tick ABOVE the previous bar's close
                    double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                    double lineLevel = prevBarClose + tickSize;

                    // Calculate start and end points for the trend line
                    DateTime startTime = Bars.Time[0];
                    DateTime endTime = startTime.AddDays(365); // Extend a year into the future

                    ChartPoint lineStart = new ChartPoint(startTime, lineLevel);
                    ChartPoint lineEnd = new ChartPoint(endTime, lineLevel);

                    // Create the horizontal line
                    ITrendLineObject line = DrwTrendLine.Create(lineStart, lineEnd);
                    line.Color = Color.White;
                }

                // BEARISH PATTERN: Previous bar closed down, current bar's high reaches or goes beyond previous bar's open,
                // current bar closes down, AND required number of bars are below the MA
                if (prevBarClosedDown && currBarHigh >= prevBarOpen && currBarClosedDown && enoughBarsBelowMA)
                {
                    // Create a chart point slightly above the high of the current bar for better visibility
                    double offset = 10 * Bars.Point;
                    ChartPoint arrowPoint = new ChartPoint(Bars.Time[0], currBarHigh + offset);

                    // Create a red down arrow
                    IArrowObject arrow = DrwArrow.Create(arrowPoint, true);
                    // Set the arrow color to red
                    arrow.Color = Color.Red;
                    // Make the arrow significantly larger for better visibility
                    arrow.Size = 5;

                    // Draw horizontal line one tick BELOW the previous bar's close
                    double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                    double lineLevel = prevBarClose - tickSize;

                    // Calculate start and end points for the trend line
                    DateTime startTime = Bars.Time[0];
                    DateTime endTime = startTime.AddDays(365); // Extend a year into the future

                    ChartPoint lineStart = new ChartPoint(startTime, lineLevel);
                    ChartPoint lineEnd = new ChartPoint(endTime, lineLevel);

                    // Create the horizontal line
                    ITrendLineObject line = DrwTrendLine.Create(lineStart, lineEnd);
                    line.Color = Color.White;
                }
            }
        }

        // Method to clear horizontal lines
        private void ClearHorizontalLines()
        {
            try
            {
                // Clear bullish line
                if (currentBullishLine != null)
                {
                    currentBullishLine.Delete();
                    currentBullishLine = null;
                }

                // Clear bearish line
                if (currentBearishLine != null)
                {
                    currentBearishLine.Delete();
                    currentBearishLine = null;
                }
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }
    }
}
