using System;
using System.Drawing;
using System.Collections.Generic;
using System.Windows.Forms;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true), RecoverDrawings(false), MouseEvents(true)]
    public class pricemonitor : IndicatorObject
    {
        // Input variables
        [Input] public Color LineColor { get; set; }
        [Input] public Keys CancelKey { get; set; }
        [Input] public Keys SendKey { get; set; }
        [Input] public Keys AdjustKey { get; set; } // Key for adjusting existing lines

        // List to store all drawings
        private List<ITrendLineObject> m_Lines = new List<ITrendLineObject>();
        private double m_CurrentTargetPrice = 0;

        public pricemonitor(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            LineColor = Color.Cyan;
            CancelKey = Keys.Alt;      // Alt+Click to cancel orders
            SendKey = Keys.Control;    // Ctrl+Click to send orders (same as strategy)
            AdjustKey = Keys.Shift;    // Shift+Click to adjust existing lines
        }

        protected override void Create()
        {
            // Output version identifier to confirm we're using the latest code
            Output.WriteLine("*** PRICE MONITOR INDICATOR - VERSION 1.0 ***");
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

                    // Clear our own lines
                    ClearAllLines();
                }
                // Check for Shift+Click (adjust existing line)
                else if (arg.keys == AdjustKey)
                {
                    double newPrice = arg.point.Price;
                    Output.WriteLine("Indicator: Adjusting target price to " + newPrice);

                    // Signal to strategy to update the target price (using plot value 4)
                    // The strategy will handle the adjustment without applying offset
                    StrategyInfo.SetPlotValue(4, newPrice);

                    // Clear existing lines and draw a new one at the adjusted price
                    ClearAllLines();
                    DrawHorizontalLine(newPrice);

                    // Update current target price
                    m_CurrentTargetPrice = newPrice;
                }
                // Check for Ctrl+Click (send order)
                else if (arg.keys == SendKey)
                {
                    double clickPrice = arg.point.Price;
                    Output.WriteLine("Indicator: Sending click price " + clickPrice + " to strategy");

                    // Signal to strategy to place an order at this price (using plot value 3)
                    // The strategy will apply any configured tick offset
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
            // Check if we need to clear target price (signal from strategy)
            double clearSignal = StrategyInfo.GetPlotValue(2);
            if (clearSignal > 0)
            {
                // Clear all lines
                ClearAllLines();
                // Reset the clear signal
                StrategyInfo.SetPlotValue(2, 0);
                Output.WriteLine("Cleared target price display");
            }

            // Get target price from strategy if available
            double targetPrice = StrategyInfo.GetPlotValue(1);
            if (targetPrice > 0 && targetPrice != m_CurrentTargetPrice)
            {
                // Clear existing lines first
                ClearAllLines();

                // Draw a horizontal line at the target price
                DrawHorizontalLine(targetPrice);

                // Update current target price
                m_CurrentTargetPrice = targetPrice;

                Output.WriteLine("Displaying target price: " + targetPrice);
                // Reset the strategy value to prevent duplicate updates
                StrategyInfo.SetPlotValue(1, 0);
            }
        }

        private void DrawHorizontalLine(double price)
        {
            try
            {
                // Use the most basic approach possible
                ChartPoint begin = new ChartPoint(Bars.Time[0], price);
                ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(1), price);

                // Create a horizontal line with minimal properties
                ITrendLineObject line = DrwTrendLine.Create(begin, end);

                // Set only the essential properties
                line.Color = LineColor;
                line.ExtLeft = true;
                line.ExtRight = true;

                // Store the line reference
                m_Lines.Add(line);

                Output.WriteLine("Drew horizontal line at " + price + " using simplified method");
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error drawing line: " + ex.Message);
                Output.WriteLine("Stack trace: " + ex.StackTrace);

                // Try an alternative method as fallback
                try {
                    // Create a simple line using the Draw method
                    DrwTrendLine.Create(new ChartPoint(Bars.Time[0], price), new ChartPoint(Bars.Time[1], price));
                    Output.WriteLine("Drew line using alternative method with different time points");
                } catch (Exception fallbackEx) {
                    Output.WriteLine("Fallback method also failed: " + fallbackEx.Message);
                }
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
                m_CurrentTargetPrice = 0;

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
