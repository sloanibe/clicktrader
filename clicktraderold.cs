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
        [Input] public Keys CancelOrderKey { get; set; }

        // State variables
        private double m_ClickPrice = 0;
        private bool m_OrderPending = false;

        // Order tracking
        private class OrderMarker
        {
            public string OrderName { get; set; }
            public double Price { get; set; }
            public DateTime Time { get; set; }
            public ITrendLineObject Line { get; set; }
        }

        private List<OrderMarker> m_ActiveOrders = new List<OrderMarker>();

        public clicktrader(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            TicksAbove = 15;
            OrderQty = 1;
            SimulateOrder = false;
            ClearPreviousLines = true;
            CancelOrderKey = Keys.Escape; // Default to Escape key for canceling orders
        }

        // Order objects
        private IOrderStopLimit m_StopLimitBuy;
        private double m_StopPrice, m_LimitPrice;

        protected override void Create()
        {
            base.Create();

            // Create the stop limit buy order with a visible name
            m_StopLimitBuy = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "StopLimitBuy", EOrderAction.Buy));

            // Output confirmation that the strategy is initialized
            Output.WriteLine("*** CLICKTRADER STRATEGY INITIALIZED - VERSION 1.1 (SIMULATION MODE) ***");
            Output.WriteLine("Use Ctrl+Click to place a stop limit buy order");
            Output.WriteLine("Use " + CancelOrderKey.ToString() + " key to cancel pending orders");
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
                    m_StopPrice = m_ClickPrice + (TicksAbove * tickSize);
                    m_LimitPrice = m_StopPrice; // Same price for both stop and limit

                    // Submit the stop limit order with detailed logging
                    Output.WriteLine("SIMULATION: Sending stop limit buy order: Stop Price = " + m_StopPrice +
                                    ", Limit Price = " + m_LimitPrice + ", Quantity = " + OrderQty);
                    m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
                    
                    // Force recalculation to ensure order is processed
                    this.RecalculateChart();
                    
                    // Verify order was sent
                    Output.WriteLine("Order sent with name: " + m_StopLimitBuy.Name + ", Action: " + m_StopLimitBuy.Action);

                    // Create a marker for this order
                    OrderMarker marker = new OrderMarker
                    {
                        OrderName = "StopLimitBuy",
                        Price = m_StopPrice,
                        Time = Bars.Time[0]
                    };

                    // Draw a marker on the chart and store the line reference
                    marker.Line = DrawMarker(m_StopPrice);

                    // Add to active orders list
                    m_ActiveOrders.Add(marker);

                    // Output information
                    Output.WriteLine("Stop Limit Buy order placed at " + m_StopPrice);

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
                    m_StopPrice = currentPrice + (TicksAbove * tickSize);
                    m_LimitPrice = m_StopPrice; // Same price for both stop and limit

                    // Submit the stop limit order with detailed logging
                    Output.WriteLine("SIMULATION: Sending stop limit buy order: Stop Price = " + m_StopPrice +
                                    ", Limit Price = " + m_LimitPrice + ", Quantity = " + OrderQty);
                    m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
                    
                    // Force recalculation to ensure order is processed
                    this.RecalculateChart();
                    
                    // Verify order was sent
                    Output.WriteLine("Order sent with name: " + m_StopLimitBuy.Name + ", Action: " + m_StopLimitBuy.Action);

                    // Create a marker for this order
                    OrderMarker marker = new OrderMarker
                    {
                        OrderName = "StopLimitBuy",
                        Price = m_StopPrice,
                        Time = Bars.Time[0]
                    };

                    // Draw a marker on the chart and store the line reference
                    marker.Line = DrawMarker(m_StopPrice);

                    // Add to active orders list
                    m_ActiveOrders.Add(marker);

                    // Output information
                    Output.WriteLine("Simulated stop limit buy order placed at " + m_StopPrice);

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

                // Check if the cancel key is pressed - cancel all pending orders
                if (arg.keys == CancelOrderKey)
                {
                    CancelAllOrders();
                    return;
                }

                // Check if Control key is pressed - place new order
                if (arg.keys == Keys.Control)
                {
                    // If configured to clear previous lines, do so
                    if (ClearPreviousLines)
                    {
                        ClearAllDrawings();

                        // Also tell the indicator to clear its lines
                        StrategyInfo.SetPlotValue(2, 1); // Signal to clear lines

                        // Cancel any pending orders
                        CancelAllOrders();
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

        private void CancelAllOrders()
        {
            try
            {
                // In MultiCharts.NET, orders are automatically canceled when not resubmitted
                // We'll clear our tracking list and remove the lines

                // Clear all drawings
                ClearAllDrawings();

                // Also tell the indicator to clear its lines
                StrategyInfo.SetPlotValue(2, 1);

                // Clear our active orders list
                m_ActiveOrders.Clear();

                Output.WriteLine("Canceled all pending orders");
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error canceling orders: " + ex.Message);
            }
        }

        // List to store all drawings
        private List<IDrawObject> m_Drawings = new List<IDrawObject>();

        private ITrendLineObject DrawMarker(double price)
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

                // Return the line object so it can be tracked with the order
                return trendLine;
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error drawing line: " + ex.Message);
                return null;
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