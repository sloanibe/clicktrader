using System;
using System.Drawing;
using System.Collections.Generic;
using System.Windows.Forms;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true), RecoverDrawings(false), MouseEvents(true)]
    public class clicktrader_lines_indicator : IndicatorObject
    {
        // Input variables
        [Input] public int LineThickness { get; set; }
        [Input] public bool UseDashedLine { get; set; }
        [Input] public Color LineColor { get; set; }
        [Input] public Keys CancelKey { get; set; }
        [Input] public Keys SendKey { get; set; }

        // List to store all drawings
        private List<ITrendLineObject> m_Lines = new List<ITrendLineObject>();

        public clicktrader_lines_indicator(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            LineThickness = 1;
            UseDashedLine = true;
            LineColor = Color.Black;
            CancelKey = Keys.Alt;      // Alt+Click to cancel orders
            SendKey = Keys.Control;    // Ctrl+Click to send orders (same as strategy)
        }

        protected override void Create()
        {
            // Output version identifier to confirm we're using the latest code
            Output.WriteLine("*** CLICKTRADER LINES INDICATOR - VERSION 6.0 (INTERACTIVE VERSION) ***");
            Output.WriteLine("Use Ctrl+Click to place orders (same as strategy)");
            Output.WriteLine("Use Alt+Click to cancel all orders");
        }

        protected override void StartCalc()
        {
            // Reset any necessary state variables here if needed
        }
        
        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            try
            {
                // Only process left mouse clicks
                if (arg.buttons != MouseButtons.Left)
                    return;
                    
                // Check for Alt+Click (cancel orders)
                if (arg.keys == CancelKey)
                {
                    Output.WriteLine("Indicator: Sending cancel signal to strategy");
                    
                    // Signal to strategy to cancel orders (using plot value 2)
                    StrategyInfo.SetPlotValue(2, 1);
                    
                    // Clear our own lines as well
                    ClearAllLines();
                }
                // Check for Ctrl+Click (send order)
                else if (arg.keys == SendKey)
                {
                    double clickPrice = arg.point.Price;
                    Output.WriteLine("Indicator: Sending click price " + clickPrice + " to strategy");
                    
                    // Signal to strategy to place an order at this price (using plot value 3)
                    StrategyInfo.SetPlotValue(3, clickPrice);
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in indicator mouse event: " + ex.Message);
            }
        }

        protected override void CalcBar()
        {
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
                Output.WriteLine("VERSION 5.0: Simplified indicator - strategy-controlled lines only");

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
