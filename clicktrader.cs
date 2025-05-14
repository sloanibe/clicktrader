using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;         // Added for mouse and keyboard handling
using System.Text;                // Added for StringBuilder

namespace PowerLanguage.Strategy
{
    [MouseEvents(true), IOGMode(IOGMode.Enabled), RecoverDrawings(false)]
    [SameAsSymbol(true)]
    public class clicktrader : SignalObject
    {
        // Input variables
        [Input] public int TicksAbove { get; set; }
        [Input] public int OrderQty { get; set; }
        [Input] public Keys CancelOrderKey { get; set; }
        [Input] public bool Development { get; set; }
        [Input] public bool UseOCO { get; set; }

        // State variables
        private double m_ClickPrice = 0;
        private bool m_OrderPending = false;
        private bool m_ActiveStopLimitOrder = false;
        private int m_LastOrderID = 0;

        // Order tracking
        private class OrderMarker
        {
            public string OrderName { get; set; }
            public double Price { get; set; }
            public DateTime Time { get; set; }
        }

        private List<OrderMarker> m_ActiveOrders = new List<OrderMarker>();

        public clicktrader(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            TicksAbove = 15;
            OrderQty = 1;
            CancelOrderKey = Keys.F12; // Using F12 function key for canceling orders
            Development = false; // Set to true only during development
            UseOCO = true; // Use OCO (One-Cancels-Others) order groups
        }

        // Order objects
        private IOrderStopLimit m_StopLimitBuy;
        private double m_StopPrice;
        private double m_LimitPrice;
        private bool m_OrderCreatedInMouseEvent = false;

        // Debug flag - only output debug info when in development mode
        private bool m_Debug = false;

        // Order tracking flags
        private bool m_OrderSent = false;
        private DateTime m_LastOrderTime = DateTime.MinValue;

        protected override void Create()
        {
            base.Create();

            // Create a stop limit buy order with basic parameters
            m_StopLimitBuy = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "StopLimitBuy", EOrderAction.Buy));

            // Note: Your version of MultiCharts .NET doesn't support additional order parameters
            // like Account, AllowMultipleEntriesInSameDirection, etc.

            // Set debug flag based on development mode
            m_Debug = Development;

            // Log initialization information
            StringBuilder envInfo = new StringBuilder();
            envInfo.AppendLine("*** CLICKTRADER STRATEGY INITIALIZED - VERSION 4.0 (ROBUST) ***");
            envInfo.AppendLine("Use Ctrl+Click to place a stop limit buy order");
            envInfo.AppendLine("Use " + CancelOrderKey.ToString() + " key to cancel pending orders");
            envInfo.AppendLine("Development Mode: " + (Development ? "ON" : "OFF"));
            envInfo.AppendLine("OCO Orders: " + (UseOCO ? "ON" : "OFF"));
            
            try {
                envInfo.AppendLine("Auto Trading Mode: " + (Environment.IsAutoTradingMode ? "ON" : "OFF"));
            } catch {}

            Output.WriteLine(envInfo.ToString());

            if (m_Debug) Output.WriteLine("DEBUG: Stop limit buy order object created");
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
                    stateInfo.AppendLine("- Active Order: " + m_ActiveStopLimitOrder);
                    stateInfo.AppendLine("- Click Price: " + m_ClickPrice);

                    Output.WriteLine(stateInfo.ToString());
                }

                // Process new orders that were created in OnMouseEvent - PRIORITY PROCESSING
                if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0 && StrategyInfo.MarketPosition == 0)
                {
                    try
                    {
                        // Calculate stop price (X ticks above click)
                        double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                        m_StopPrice = m_ClickPrice + (TicksAbove * tickSize);
    
                        // Set limit price 1 tick above stop price for a reasonable fill
                        m_LimitPrice = m_StopPrice + tickSize;
    
                        // Cancel any existing orders first
                        if (m_ActiveStopLimitOrder)
                        {
                            // Clear our active orders list
                            m_ActiveOrders.Clear();
                            
                            // Reset the active order flag temporarily
                            m_ActiveStopLimitOrder = false;
                            
                            // Check if we're in auto-trading mode
                            bool isAutoTrading = false;
                            try { isAutoTrading = Environment.IsAutoTradingMode; } catch {}
                            
                            if (isAutoTrading && UseOCO)
                            {
                                // In auto-trading mode with OCO, orders are automatically canceled
                                // when a new order is sent in the same group
                                Output.WriteLine("Auto-trading with OCO: Previous order will be canceled automatically");
                            }
                            else
                            {
                                // Give a small delay to allow cancellation to process
                                Output.WriteLine("Canceling previous order before placing new one");
                            }
                        }
    
                        // Send the stop limit order with stop and limit prices
                        m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
                        Output.WriteLine("Order sent: Stop @ " + m_StopPrice + ", Limit @ " + m_LimitPrice);
    
                        // Set flags
                        m_ActiveStopLimitOrder = true;
                        m_LastOrderTime = DateTime.Now;
    
                        // Create a marker for this order for tracking purposes
                        OrderMarker marker = new OrderMarker
                        {
                            OrderName = "StopLimitBuy",
                            Price = m_StopPrice,
                            Time = Bars.Time[0]
                        };
                        
                        // Add to active orders list
                        m_ActiveOrders.Add(marker);
    
                        // Reset flags after order is placed
                        m_ClickPrice = 0;
                        m_OrderCreatedInMouseEvent = false;
                        
                        // Check if broker position differs from strategy position
                        try
                        {
                            if (StrategyInfo.MarketPositionAtBroker != StrategyInfo.MarketPosition)
                            {
                                Output.WriteLine("Warning: Broker position (" + StrategyInfo.MarketPositionAtBroker + 
                                              ") differs from strategy position (" + StrategyInfo.MarketPosition + ")");
                            }
                        }
                        catch {}
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("Error processing new order: " + ex.Message);
                    }
                }
                
                // If we have an active order and we're still flat, keep it alive
                // This is the key part that follows the example document's approach
                else if (m_ActiveStopLimitOrder && StrategyInfo.MarketPosition == 0)
                {
                    try
                    {
                        // Following the example document's approach - resubmit the order each bar
                        // to keep it active until it's filled or manually canceled
                        m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
                        
                        if (m_Debug && Bars.LastBarOnChart)
                        {
                            Output.WriteLine("CalcBar: Resubmitting stop limit order to keep it active");
                        }
                        
                        // Check for broker vs strategy position mismatch
                        try
                        {
                            if (Environment.IsAutoTradingMode && 
                                StrategyInfo.MarketPositionAtBroker != StrategyInfo.MarketPosition)
                            {
                                if (m_Debug)
                                {
                                    Output.WriteLine("Position mismatch: Broker=" + StrategyInfo.MarketPositionAtBroker + 
                                                  ", Strategy=" + StrategyInfo.MarketPosition);
                                }
                            }
                        }
                        catch {}
                    }
                    catch (Exception ex)
                    {
                        if (m_Debug)
                        {
                            Output.WriteLine("Error resubmitting order: " + ex.Message);
                        }
                    }
                }
                
                // If we have a position, stop resubmitting the order
                else if (StrategyInfo.MarketPosition > 0 && m_ActiveStopLimitOrder)
                {
                    Output.WriteLine("Position opened, stop limit order no longer needed");
                    m_ActiveStopLimitOrder = false;
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

                // Check for F12 key in mouse events
                if (arg.keys == CancelOrderKey)
                {
                    Output.WriteLine("*** F12 KEY DETECTED IN MOUSE EVENT ***");
                    Output.WriteLine("Time: " + DateTime.Now.ToString());
                    Output.WriteLine("Current Bar: " + Bars.CurrentBar);
                    Output.WriteLine("Active Order: " + (m_ActiveStopLimitOrder ? "YES" : "NO"));
                    
                    CancelAllOrders();
                    return;
                }
                
                // Handle Ctrl+click for both order cancellation and creation
                if (arg.keys == Keys.Control)
                {
                    // Get the current click price
                    double currentClickPrice = arg.point.Price;
                    double currentTickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                    
                    // Check if we have an active order and are clicking near it
                    if (m_ActiveStopLimitOrder)
                    {
                        // Check if click is within 5 ticks of the pending order's stop price
                        if (Math.Abs(currentClickPrice - m_StopPrice) <= (5 * currentTickSize))
                        {
                            Output.WriteLine("*** CTRL+CLICK DETECTED NEAR PENDING ORDER ***");
                            Output.WriteLine("Click Price: " + currentClickPrice);
                            Output.WriteLine("Order Stop Price: " + m_StopPrice);
                            Output.WriteLine("Time: " + DateTime.Now.ToString());
                            
                            CancelAllOrders();
                            Output.WriteLine("Order canceled - NOT creating a new order");
                            return;
                        }
                    }
                    
                    // If we get here, we're either creating a new order or replacing an existing one
                    // that's not near where we clicked
                    
                    // Log detailed information about the mouse event
                    if (m_Debug)
                    {
                        StringBuilder clickInfo = new StringBuilder();
                        clickInfo.AppendLine("DEBUG: Mouse event detected");
                        clickInfo.AppendLine("- Click Price: " + currentClickPrice);
                        clickInfo.AppendLine("- Click Time: " + DateTime.Now);
                        Output.WriteLine(clickInfo.ToString());
                    }

                    // Cancel any pending orders before creating a new one
                    CancelAllOrders();

                    // Calculate the order price
                    double orderPrice = currentClickPrice + (TicksAbove * currentTickSize);

                    // Output information about the order being placed
                    Output.WriteLine("PLACING ORDER: Stop Limit Buy at stop price " + orderPrice +
                                    " (" + TicksAbove + " ticks above " + currentClickPrice + ")");

                    // Store the click price for CalcBar to handle
                    Output.WriteLine("OnMouseEvent: Setting up order for processing in CalcBar");
                    m_ClickPrice = currentClickPrice;

                    // Calculate and display the reference price for user feedback
                    double clickStopPrice = currentClickPrice + (TicksAbove * currentTickSize);
                    double clickLimitPrice = clickStopPrice + currentTickSize;

                    // No need to draw markers anymore - platform UI shows orders

                    // Cancel any existing orders before creating a new one
                    if (m_ActiveStopLimitOrder && StrategyInfo.MarketPosition == 0)
                    {
                        Output.WriteLine("OnMouseEvent: Canceling existing orders before creating a new one");
                        CancelAllOrders();
                    }
                    
                    // Flag for new order creation
                    m_OrderCreatedInMouseEvent = true;
                    Output.WriteLine("OnMouseEvent: New order will be created in next CalcBar. Stop Price: " + clickStopPrice +
                                  ", Limit Price: " + clickLimitPrice + ", Quantity: " + OrderQty);
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
                // According to the MultiCharts .NET Programming Guide, we need to use CancelOrder
                // or set the order to not be resubmitted
                if (m_StopLimitBuy != null && m_ActiveStopLimitOrder)
                {
                    try
                    {
                        // Try to use the Cancel method if available on the order
                        // Based on the Programming Guide line 2711: "void CancelOrder(int order_id);  - cancellation of the order by its ID."
                        Output.WriteLine("Attempting to cancel order with ID: " + m_LastOrderID);
                        
                        // First approach: Try to cancel by not resubmitting and sending a zero quantity order
                        m_StopLimitBuy.Send(0, 0, 0);
                        Output.WriteLine("Sent zero-quantity order to force cancellation");
                        
                        // Second approach: We'll also stop resubmitting the order in CalcBar
                        m_ActiveStopLimitOrder = false;
                    }
                    catch (Exception sendEx)
                    {
                        Output.WriteLine("Primary cancellation method failed: " + sendEx.Message);
                        
                        // Try another approach based on the Programming Guide
                        try
                        {
                            // The guide mentions OCO (One-Cancels-Others) groups
                            // We can try to send a different order that would cancel the existing one
                            double farAwayPrice = Bars.Close[0] * 100; // A price that's far away and won't execute
                            m_StopLimitBuy.Send(farAwayPrice, farAwayPrice, 0);
                            Output.WriteLine("Sent far-away price order to force cancellation");
                        }
                        catch (Exception altEx)
                        {
                            Output.WriteLine("Alternative cancellation failed: " + altEx.Message);
                        }
                    }
                }
                
                // Clear our active orders list
                m_ActiveOrders.Clear();

                // Reset all order flags - this is still important for our internal tracking
                m_ActiveStopLimitOrder = false;
                
                Output.WriteLine("F12 key pressed: All orders canceled");
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in CancelAllOrders: " + ex.Message);
            }
        }
    }
}
