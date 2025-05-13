using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;         // Added for mouse and keyboard handling

namespace PowerLanguage.Strategy
{
    [MouseEvents(true), IOGMode(IOGMode.Enabled), RecoverDrawings(false)]
    public class clicktrader : SignalObject
    {
        // Input variables
        [Input] public int TicksAbove { get; set; }
        [Input] public int OrderQty { get; set; }
        [Input] public bool SimulateOrder { get; set; }
        [Input] public bool ClearPreviousLines { get; set; }

        // State variables
        private double m_ClickPrice = 0;
        private bool m_OrderPending = false;

        // Order tracking
        private class OrderMarker
        {
            public int OrderId { get; set; }
            public double Price { get; set; }
            public DateTime Time { get; set; }
        }

        private List<OrderMarker> m_ActiveOrders = new List<OrderMarker>();

        public clicktrader(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            TicksAbove = 15;
            OrderQty = 1;
            SimulateOrder = false;
            ClearPreviousLines = true;
        }

        // Order objects
        private IOrderStopLimit m_StopLimitBuy;

        protected override void Create()
        {
            base.Create();

            // Create the stop limit buy order
            m_StopLimitBuy = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, EOrderAction.Buy));
        }

        protected override void CalcBar()
        {
            // Process orders based on market position
            if (StrategyInfo.MarketPosition == 0) // Flat position
            {
                // If we have a click price and it's valid, place the order
                if (m_ClickPrice > 0)
                {
                    // Calculate the stop price (X ticks above click)
                    double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                    double stopPrice = m_ClickPrice + (TicksAbove * tickSize);

                    // Submit the stop limit order
                    m_StopLimitBuy.Send(stopPrice, stopPrice, OrderQty);

                    // Draw a marker on the chart
                    DrawMarker(stopPrice);

                    // Reset click price after order is placed
                    m_ClickPrice = 0;
                }

                // If simulation is enabled, place an order at the current price + ticks
                if (SimulateOrder)
                {
                    // Use current price as reference
                    double currentPrice = Bars.Close[0];

                    // Calculate the order price (X ticks above current price)
                    double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                    double orderPrice = currentPrice + (TicksAbove * tickSize);

                    // Submit the stop limit order
                    m_StopLimitBuy.Send(orderPrice, orderPrice, OrderQty);

                    // Output information
                    Output.WriteLine("Simulated stop limit buy order at " + orderPrice);

                    // Draw a marker on the chart
                    DrawMarker(orderPrice);

                    // Turn off simulation after placing the order
                    SimulateOrder = false;
                }
            }
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            try
            {
                // Only process left mouse clicks
                if (arg.buttons != MouseButtons.Left)
                    return;

                // Check if Control key is pressed
                if (arg.keys == Keys.Control)
                {
                    // If configured to clear previous lines, do so
                    if (ClearPreviousLines)
                    {
                        ClearAllDrawings();
                        
                        // Also tell the indicator to clear its lines
                        StrategyInfo.SetPlotValue(2, 1); // Signal to clear lines
                    }
                    
                    // Store the click price for processing in CalcBar
                    m_ClickPrice = arg.point.Price;
                    
                    // Calculate the stop price (X ticks above click)
                    double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                    double stopPrice = m_ClickPrice + (TicksAbove * tickSize);
                    
                    // Output information about the pending order
                    Output.WriteLine("Stop Limit Buy order will be placed at " + stopPrice + 
                                    " (" + TicksAbove + " ticks above " + m_ClickPrice + ")");
                    
                    // Trigger a recalculation to place the order
                    this.CalcBar();
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error processing mouse event: " + ex.Message);
            }
        }

        // List to store all drawings
        private List<IDrawObject> m_Drawings = new List<IDrawObject>();

        private void DrawMarker(double price)
        {
            try
            {
                // Create two ChartPoints with the same price but different times
                // This creates a horizontal line
                ChartPoint begin = new ChartPoint(Bars.Time[0], price);
                ChartPoint end = new ChartPoint(Bars.Time[1], price);

                // Instead of drawing directly, communicate with the indicator
                // Set the plot value for the indicator to use
                StrategyInfo.SetPlotValue(1, price);
                
                // Also draw a temporary line for immediate feedback
                ITrendLineObject trendLine = DrwTrendLine.Create(begin, end);
                trendLine.ExtLeft = true;
                trendLine.ExtRight = true;
                trendLine.Color = Color.Black;
                trendLine.Size = 2;
                trendLine.AnchorToBars = true;
                
                // Add the line to our drawings list
                m_Drawings.Add(trendLine);

                // Output confirmation
                Output.WriteLine("Drew horizontal line at price " + price + " (" + TicksAbove + " ticks above click)");
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error drawing line: " + ex.Message);
            }
        }



        // Add a method to clear all drawings if needed
        private void ClearAllDrawings()
        {
            try
            {
                // Remove each drawing from the chart
                foreach (IDrawObject drawing in m_Drawings)
                {
                    if (drawing != null && drawing.Exist)
                    {
                        drawing.Delete();
                    }
                }

                // Clear the list
                m_Drawings.Clear();
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error clearing drawings: " + ex.Message);
            }
        }
    }
}