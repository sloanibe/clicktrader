using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;

namespace PowerLanguage.Strategy
{
    [MouseEvents(true), IOGMode(IOGMode.Enabled), RecoverDrawings(false)]
    public class clicktrader_simple : SignalObject
    {
        // Input variables
        [Input] public int TicksAbove { get; set; }
        [Input] public int OrderQty { get; set; }
        [Input] public bool SimulateOrder { get; set; }
        [Input] public Keys CancelOrderKey { get; set; }

        // State variables
        private double m_ClickPrice = 0;
        private bool m_Debug = true;

        public clicktrader_simple(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            TicksAbove = 15;
            OrderQty = 1;
            SimulateOrder = false;
            CancelOrderKey = Keys.Escape;
        }

        protected override void Create()
        {
            base.Create();
            
            // Output confirmation that the strategy is initialized
            Output.WriteLine("*** CLICKTRADER SIMPLE INITIALIZED ***");
            Output.WriteLine("Use Ctrl+Click to place a buy order");
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
                    // Calculate the target price (X ticks above click)
                    double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                    double targetPrice = m_ClickPrice + (TicksAbove * tickSize);
                    
                    if (m_Debug) Output.WriteLine("DEBUG: About to create buy order");
                    
                    try
                    {
                        // Create and send a simple buy order
                        IOrderMarket buyOrder = OrderCreator.MarketNextBar(
                            new SOrderParameters(Contracts.Default, "BuyOrder", EOrderAction.Buy));
                            
                        if (m_Debug) Output.WriteLine("DEBUG: Order object created");
                        
                        // Submit the order
                        buyOrder.Send(OrderQty);
                        
                        if (m_Debug) Output.WriteLine("DEBUG: Order sent successfully");
                        
                        // Draw a marker on the chart
                        DrawHorizontalLine(targetPrice);
                        
                        // Reset click price after order is placed
                        m_ClickPrice = 0;
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("ERROR placing order: " + ex.Message);
                    }
                }
                
                // If simulation is enabled, place an order at the current price
                if (SimulateOrder)
                {
                    try
                    {
                        if (m_Debug) Output.WriteLine("DEBUG: Creating simulated order");
                        
                        // Create and send a simple buy order
                        IOrderMarket buyOrder = OrderCreator.MarketNextBar(
                            new SOrderParameters(Contracts.Default, "SimBuyOrder", EOrderAction.Buy));
                            
                        // Submit the order
                        buyOrder.Send(OrderQty);
                        
                        Output.WriteLine("Simulated buy order placed");
                        
                        // Turn off simulation after placing the order
                        SimulateOrder = false;
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("ERROR placing simulated order: " + ex.Message);
                    }
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
                    // Store the click price for processing in CalcBar
                    m_ClickPrice = arg.point.Price;

                    // Calculate the target price (X ticks above click)
                    double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                    double targetPrice = m_ClickPrice + (TicksAbove * tickSize);

                    // Output information about the pending order
                    Output.WriteLine("Buy order will be placed at market (" + 
                                    TicksAbove + " ticks above " + m_ClickPrice + ")");

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
                ClearAllDrawings();
                
                Output.WriteLine("Canceled all pending orders");
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error canceling orders: " + ex.Message);
            }
        }

        // List to store all drawings
        private List<IDrawObject> m_Drawings = new List<IDrawObject>();

        private void DrawHorizontalLine(double price)
        {
            try
            {
                // Create two ChartPoints with the same price but different times
                ChartPoint begin = new ChartPoint(Bars.Time[0], price);
                ChartPoint end = new ChartPoint(Bars.Time[Bars.CurrentBar > 1 ? 1 : 0], price);

                // Draw a horizontal line
                ITrendLineObject trendLine = DrwTrendLine.Create(begin, end);
                trendLine.ExtLeft = true;
                trendLine.ExtRight = true;
                trendLine.Color = Color.Blue;
                trendLine.Size = 2;
                trendLine.AnchorToBars = true;

                // Add the line to our drawings list
                m_Drawings.Add(trendLine);

                // Output confirmation
                Output.WriteLine("Drew horizontal line at price " + price);
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error drawing line: " + ex.Message);
            }
        }

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
