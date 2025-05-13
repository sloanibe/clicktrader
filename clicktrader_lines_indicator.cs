using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true), RecoverDrawings(false)]
    public class clicktrader_lines_indicator : IndicatorObject
    {
        // Input variables
        [Input] public double LinePrice { get; set; }
        [Input] public int LineThickness { get; set; }
        [Input] public bool UseDashedLine { get; set; }
        [Input] public Color LineColor { get; set; }

        // List to store all drawings
        private List<ITrendLineObject> m_Lines = new List<ITrendLineObject>();
        private bool m_LineAdded = false;

        public clicktrader_lines_indicator(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            LinePrice = 0;
            LineThickness = 1;
            UseDashedLine = true;
            LineColor = Color.Black;
        }

        protected override void Create()
        {
            // Output version identifier to confirm we're using the latest code
            Output.WriteLine("*** CLICKTRADER LINES INDICATOR - VERSION 3.0 (SIMPLIFIED VERSION) ***");
        }

        protected override void StartCalc()
        {
            m_LineAdded = false;
        }

        protected override void CalcBar()
        {
            // Check if we have a line price and haven't added it yet
            if (LinePrice > 0 && !m_LineAdded)
            {
                DrawHorizontalLine(LinePrice);
                m_LineAdded = true;
            }

            // Check if we need to clear lines (signal from strategy)
            double clearSignal = StrategyInfo.GetPlotValue(2);
            if (clearSignal > 0)
            {
                ClearAllLines();
                // Reset the clear signal
                StrategyInfo.SetPlotValue(2, 0);
            }

            // Get line price from strategy if available
            double strategyLinePrice = StrategyInfo.GetPlotValue(1);
            if (strategyLinePrice > 0)
            {
                DrawHorizontalLine(strategyLinePrice);
                // Reset the strategy value to prevent duplicate lines
                StrategyInfo.SetPlotValue(1, 0);
            }
        }

        private void DrawHorizontalLine(double price)
        {
            try
            {
                Output.WriteLine("VERSION 4.0: Using safer drawing method");

                // Get the current time for the first point
                DateTime currentTime = Bars.Time[0];
                
                // Create a second time point that's just slightly different to avoid errors
                // Instead of using a bar index that might not exist
                DateTime endTime = currentTime.AddSeconds(1);
                
                // Create two ChartPoints with the same price but different times
                ChartPoint begin = new ChartPoint(currentTime, price);
                ChartPoint end = new ChartPoint(endTime, price);

                // Create the horizontal trend line with minimal styling
                ITrendLineObject trendLine = DrwTrendLine.Create(begin, end);

                // Extend the line in both directions
                trendLine.ExtLeft = true;
                trendLine.ExtRight = true;

                // Only set color - avoid other styling that might cause errors
                trendLine.Color = Color.Black;

                // Anchor the line to bars so it doesn't disappear
                trendLine.AnchorToBars = true;

                // Add the line to our list
                m_Lines.Add(trendLine);

                // Output confirmation
                Output.WriteLine("Indicator drew horizontal line at price " + price + " (safer version)");
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error drawing line: " + ex.Message + "\nStack trace: " + ex.StackTrace);
            }
        }

        private void ClearAllLines()
        {
            try
            {
                // Create a copy of the list to avoid modification during iteration
                List<ITrendLineObject> linesToRemove = new List<ITrendLineObject>(m_Lines);

                // Remove each line from the chart
                foreach (ITrendLineObject line in linesToRemove)
                {
                    try
                    {
                        if (line != null && line.Exist)
                        {
                            line.Delete();
                        }
                    }
                    catch (Exception lineEx)
                    {
                        // Log but continue with other lines
                        Output.WriteLine("Warning: Could not delete line: " + lineEx.Message);
                    }
                }

                // Clear the list
                m_Lines.Clear();

                // Output confirmation
                Output.WriteLine("Cleared all horizontal lines");
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error clearing lines: " + ex.Message);
            }
        }
    }
}
