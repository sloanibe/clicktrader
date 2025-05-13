using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;         // Added for mouse and keyboard handling
using System.Text;                // Added for StringBuilder

namespace PowerLanguage.Strategy
{
    [MouseEvents(true), IOGMode(IOGMode.Enabled), RecoverDrawings(false)]
    public class clicktrader : SignalObject
    {
        // Input variables
        [Input] public int TicksAbove { get; set; }
        [Input] public int OrderQty { get; set; }
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
            ClearPreviousLines = true;
            CancelOrderKey = Keys.Escape; // Default to Escape key for canceling orders
        }

        // Order objects
        private IOrderMarket m_MarketBuy;
        private double m_OrderPrice;
        private bool m_OrderCreatedInMouseEvent = false;
        
        // Debug flag
        private bool m_Debug = true;
        
        // Order tracking flags
        private bool m_OrderSent = false;
        private DateTime m_LastOrderTime = DateTime.MinValue;

        protected override void Create()
        {
            base.Create();

            // Create a market buy order with basic parameters
            m_MarketBuy = OrderCreator.MarketNextBar(
                new SOrderParameters(Contracts.Default, "MarketBuy", EOrderAction.Buy));
                
            // Note: Your version of MultiCharts .NET doesn't support additional order parameters
            // like Account, AllowMultipleEntriesInSameDirection, etc.
                
            // Log initialization information
            StringBuilder envInfo = new StringBuilder();
            envInfo.AppendLine("*** CLICKTRADER STRATEGY INITIALIZED - VERSION 1.5 ***");
            envInfo.AppendLine("Use Ctrl+Click to place a market buy order");
            envInfo.AppendLine("Use " + CancelOrderKey.ToString() + " key to cancel pending orders");
            
            Output.WriteLine(envInfo.ToString());
            
            if (m_Debug) Output.WriteLine("DEBUG: Market buy order object created");
        }

        protected override void CalcBar()
        {
            try
            {
                // Log detailed information about the current bar and strategy state
                if (m_Debug)
                {
                    StringBuilder stateInfo = new StringBuilder();
                    stateInfo.AppendLine("DEBUG: CalcBar execution");
                    stateInfo.AppendLine("- Current Bar: " + Bars.CurrentBar);
                    stateInfo.AppendLine("- Time: " + Bars.Time[0]);
                    stateInfo.AppendLine("- Market Position: " + StrategyInfo.MarketPosition);
                    // Environment properties can be safely accessed in CalcBar
                    stateInfo.AppendLine("- IOG Enabled: " + Environment.IOGEnabled);
                    stateInfo.AppendLine("- Order Pending Flag: " + m_OrderPending);
                    stateInfo.AppendLine("- Click Price: " + m_ClickPrice);
                    stateInfo.AppendLine("- Order Sent Flag: " + m_OrderSent);
                    
                    if (m_OrderSent)
                    {
                        stateInfo.AppendLine("- Last Order Time: " + m_LastOrderTime);
                        stateInfo.AppendLine("- Time Since Order: " + (DateTime.Now - m_LastOrderTime).TotalSeconds + " seconds");
                    }
                    
                    Output.WriteLine(stateInfo.ToString());
                }
                
                // Check for any orders that were sent but not confirmed
                if (m_OrderSent && (DateTime.Now - m_LastOrderTime).TotalSeconds > 5)
                {
                    Output.WriteLine("WARNING: Order sent but not confirmed after 5 seconds. This may indicate an issue with order processing.");
                    m_OrderSent = false; // Reset flag to prevent repeated warnings
                }
                
                // Process orders that were created in OnMouseEvent but need to be sent in CalcBar
                if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0 && StrategyInfo.MarketPosition == 0)
                {
                    // Calculate reference price (X ticks above click)
                    double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                    m_OrderPrice = m_ClickPrice + (TicksAbove * tickSize);

                    // Submit the market order with detailed logging
                    Output.WriteLine("CalcBar: Sending market buy order: Reference Price = " + m_OrderPrice +
                                    ", Quantity = " + OrderQty);
                    
                    // Explicitly set the quantity
                    m_MarketBuy.Send(OrderQty);
                    
                    // Verify order was sent
                    Output.WriteLine("CalcBar: Order sent successfully");
                    m_OrderSent = true;
                    m_LastOrderTime = DateTime.Now;

                    // Create a marker for this order
                    OrderMarker marker = new OrderMarker
                    {
                        OrderName = "MarketBuy",
                        Price = m_OrderPrice,
                        Time = Bars.Time[0]
                    };

                    // Draw a marker on the chart and store the line reference
                    marker.Line = DrawMarker(m_OrderPrice);

                    // Add to active orders list
                    m_ActiveOrders.Add(marker);

                    // Reset flags after order is placed
                    m_ClickPrice = 0;
                    m_OrderCreatedInMouseEvent = false;
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in CalcBar: " + ex.Message + "\nStack Trace: " + ex.StackTrace);
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
                    // Log detailed information about the mouse event
                    if (m_Debug)
                    {
                        StringBuilder clickInfo = new StringBuilder();
                        clickInfo.AppendLine("DEBUG: Mouse event detected");
                        clickInfo.AppendLine("- Click Price: " + arg.point.Price);
                        clickInfo.AppendLine("- Click Time: " + DateTime.Now);
                        Output.WriteLine(clickInfo.ToString());
                    }
                    
                    // If configured to clear previous lines, do so
                    if (ClearPreviousLines)
                    {
                        ClearAllDrawings();

                        // Also tell the indicator to clear its lines
                        StrategyInfo.SetPlotValue(2, 1); // Signal to clear lines

                        // Cancel any pending orders
                        CancelAllOrders();
                    }

                    // Instead of storing for CalcBar, process the order immediately
                    double clickPrice = arg.point.Price;
                    double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                    double orderPrice = clickPrice + (TicksAbove * tickSize);
                    
                    // Output information about the order being placed
                    Output.WriteLine("PLACING ORDER: Market Buy at reference price " + orderPrice +
                                    " (" + TicksAbove + " ticks above " + clickPrice + ")");
                    
                    try
                    {
                        // Instead of trying to create and send the order here, which might be causing issues,
                        // we'll set flags for CalcBar to handle it
                        Output.WriteLine("OnMouseEvent: Setting up order for processing in CalcBar");
                        m_ClickPrice = clickPrice;
                        m_OrderCreatedInMouseEvent = true;
                        
                        // Calculate and display the reference price for user feedback
                        double clickTickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                        double clickOrderPrice = clickPrice + (TicksAbove * clickTickSize);
                        
                        // Draw a temporary marker for visual feedback
                        DrawMarker(clickOrderPrice);
                        
                        Output.WriteLine("OnMouseEvent: Order will be processed in next CalcBar. Price: " + clickOrderPrice + ", Quantity: " + OrderQty);
                    }
                    catch (Exception orderEx)
                    {
                        Output.WriteLine("ERROR creating/sending order: " + orderEx.Message + "\nStack Trace: " + orderEx.StackTrace);
                        
                        // Fall back to the old method
                        Output.WriteLine("Falling back to CalcBar order processing");
                        m_ClickPrice = clickPrice;
                        m_OrderPrice = orderPrice;
                    }
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error processing mouse event: " + ex.Message + "\nStack Trace: " + ex.StackTrace);
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
