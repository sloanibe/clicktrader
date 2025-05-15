using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;
using System.Text;

namespace PowerLanguage.Strategy
{
    [MouseEvents(true), IOGMode(IOGMode.Enabled), RecoverDrawings(false)]
    [SameAsSymbol(true)]
    public class clicktrader : SignalObject
    {
        // Input variables
        [Input] public int TicksBelow { get; set; }
        [Input] public int OrderQty { get; set; }
        [Input] public bool Development { get; set; }

        // State variables
        private double m_ClickPrice = 0;
        private bool m_ActiveStopLimitOrder = false;
        private double m_StopPrice = 0;
        private double m_LimitPrice = 0;
        private bool m_OrderCreatedInMouseEvent = false;
        private bool m_IsExitOrder = false; // Flag to track if we're placing an exit order
        private bool m_Debug = false;
        private bool m_CancelOrder = false; // Flag to indicate order cancellation

        // Order objects
        private IOrderStopLimit m_StopLimitSell;
        private IOrderMarket m_BuyToCover;

        public clicktrader(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            TicksBelow = 15;
            OrderQty = 1;
            Development = false; // Disable debug mode by default
        }

        protected override void Create()
        {
            base.Create();

            // Create stop limit sell short order with basic parameters
            m_StopLimitSell = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "StopLimitSellShort", EOrderAction.SellShort));
                
            // Create market buy to cover order for exiting short positions
            m_BuyToCover = OrderCreator.MarketNextBar(
                new SOrderParameters(Contracts.Default, "BuyToCover", EOrderAction.BuyToCover));

            // Set debug flag based on development mode
            m_Debug = Development;

            // Log initialization information
            StringBuilder envInfo = new StringBuilder();
            envInfo.AppendLine("*** SELL SHORT STOP LIMIT CLICKTRADER INITIALIZED ***");
            envInfo.AppendLine("Use Ctrl+Click to place a stop limit SELL SHORT order");
            envInfo.AppendLine("Use Alt+Click to exit short position with BUY TO COVER order");
            envInfo.AppendLine("Use Ctrl+Right Click to cancel pending orders");
            envInfo.AppendLine("Use F12 to cancel all pending orders");
            envInfo.AppendLine("Ticks Below Click: " + TicksBelow);
            envInfo.AppendLine("Development Mode: " + (Development ? "ON" : "OFF"));
            
            try {
                envInfo.AppendLine("Auto Trading Mode: " + (Environment.IsAutoTradingMode ? "ON" : "OFF"));
            } catch {}

            Output.WriteLine(envInfo.ToString());
        }

        protected override void CalcBar()
        {
            try
            {
                // Minimal logging for performance
                if (m_Debug && Bars.LastBarOnChart)
                {
                    Output.WriteLine("DEBUG: CalcBar - Market Position: " + StrategyInfo.MarketPosition + ", Active Order: " + m_ActiveStopLimitOrder);
                }

                // Process new orders that were created in OnMouseEvent
                if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0)
                {
                    try
                    {
                        // Cancel any existing orders first
                        if (m_ActiveStopLimitOrder)
                        {
                            // Clear any active orders tracking
                            Output.WriteLine("Canceling existing order before placing new one");
                            
                            // Reset the active order flag temporarily
                            m_ActiveStopLimitOrder = false;
                            
                            // Check if we're in auto-trading mode
                            bool isAutoTrading = false;
                            try { isAutoTrading = Environment.IsAutoTradingMode; } catch {}
                            
                            if (isAutoTrading)
                            {
                                Output.WriteLine("Auto-trading mode: Previous order will be canceled automatically");
                            }
                        }

                        // Check if this is an exit order or entry order
                        if (m_IsExitOrder)
                        {
                            // Send the buy to cover market order to exit short position
                            m_BuyToCover.Send(OrderQty);
                            Output.WriteLine("BUY TO COVER Market Order sent to exit short position");
                        }
                        else
                        {
                            // Send the sell short stop limit order with stop and limit prices
                            m_StopLimitSell.Send(m_StopPrice, m_LimitPrice, OrderQty);
                            Output.WriteLine("SELL SHORT Stop Limit Order sent: Stop @ " + m_StopPrice + ", Limit @ " + m_LimitPrice);
                        }
                        
                        // Set flags
                        m_ActiveStopLimitOrder = true;
                        
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
                        Output.WriteLine("Error processing new order: " + ex.Message + "\nStack Trace: " + ex.StackTrace);
                    }
                }
                
                // If we have an active order, keep it alive
                else if (m_ActiveStopLimitOrder)
                {
                    try
                    {
                        // Following the example document's approach - resubmit the order each bar
                        // to keep it active until it's filled or manually canceled
                        if (!m_IsExitOrder)
                        {
                            m_StopLimitSell.Send(m_StopPrice, m_LimitPrice, OrderQty);
                            
                            if (m_Debug && Bars.LastBarOnChart)
                            {
                                Output.WriteLine("CalcBar: Resubmitting SELL SHORT stop limit order to keep it active");
                            }
                        }
                        // We don't need to resubmit market orders as they're executed immediately
                        
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
                            Output.WriteLine("Error resubmitting order: " + ex.Message + "\nStack Trace: " + ex.StackTrace);
                        }
                    }
                }
                
                // If we need to cancel the order for any reason, do it here
                if (m_CancelOrder && m_ActiveStopLimitOrder)
                {
                    // Set the active order flag to false to stop resubmitting
                    m_ActiveStopLimitOrder = false;
                    m_CancelOrder = false;
                    Output.WriteLine("Order canceled successfully");
                    
                    // Draw a visual indicator that order was canceled
                    if (m_StopPrice > 0)
                    {
                        // Draw a red X at the stop price to indicate cancellation
                        ChartPoint cancelPoint = new ChartPoint(Bars.Time[0], m_StopPrice);
                        DrwText.Create(cancelPoint, "X").Color = Color.Red;
                    }
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
                // Handle F12 key press to cancel orders
                if (arg.keys == Keys.F12)
                {
                    if (m_ActiveStopLimitOrder)
                    {
                        m_CancelOrder = true;
                        Output.WriteLine("F12 pressed - Canceling all pending orders");
                    }
                    else
                    {
                        Output.WriteLine("No active orders to cancel");
                    }
                    return;
                }
                
                // Handle Ctrl+Right Click to cancel pending orders
                if (arg.buttons == MouseButtons.Right && arg.keys == Keys.Control)
                {
                    if (m_ActiveStopLimitOrder)
                    {
                        m_CancelOrder = true;
                        Output.WriteLine("Ctrl+Right Click - Canceling all pending orders");
                    }
                    else
                    {
                        Output.WriteLine("No active orders to cancel");
                    }
                    return;
                }
                
                // Only process left mouse clicks with modifier keys
                if (arg.buttons != MouseButtons.Left || (arg.keys != Keys.Control && arg.keys != Keys.Alt))
                    return;

                // Minimal debug logging
                if (m_Debug)
                {
                    Output.WriteLine("DEBUG: Mouse event - Key: " + arg.keys + ", Price: " + arg.point.Price);
                }

                // Get the current click price and tick size
                double currentClickPrice = arg.point.Price;
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                
                // Handle Ctrl+Click for sell short orders
                if (arg.keys == Keys.Control)
                {
                    // Calculate stop and limit prices for sell short order
                    // For sell short orders, stop price is below the click price
                    m_StopPrice = currentClickPrice - (TicksBelow * tickSize);
                    // Set limit price 1 tick below stop price for a reasonable fill
                    m_LimitPrice = m_StopPrice - tickSize;
                    
                    // Store the click price for CalcBar to handle
                    m_ClickPrice = currentClickPrice;
                    
                    // Flag for new order creation
                    m_OrderCreatedInMouseEvent = true;
                    
                    // Set flag to indicate this is a sell short order
                    m_IsExitOrder = false;
                    
                    Output.WriteLine("PLACING ORDER: Stop Limit SELL SHORT at stop price " + m_StopPrice +
                                  " (" + TicksBelow + " ticks below " + currentClickPrice + ")");
                    Output.WriteLine("OnMouseEvent: Setting up SELL SHORT order for processing in CalcBar");
                    Output.WriteLine("Stop Price: " + m_StopPrice + ", Limit Price: " + m_LimitPrice);
                    
                    // If we have an active order, log that we'll be canceling it
                    if (m_ActiveStopLimitOrder)
                    {
                        Output.WriteLine("OnMouseEvent: Existing order will be canceled before creating a new one");
                    }
                    
                    Output.WriteLine("OnMouseEvent: New SELL SHORT order will be created in next CalcBar. Stop Price: " + m_StopPrice +
                                  ", Limit Price: " + m_LimitPrice + ", Quantity: " + OrderQty);
                }
                // Handle Alt+Click for buy to cover (exit short position)
                else if (arg.keys == Keys.Alt)
                {
                    // Check if we can exit a position
                    // Check if we have a short position to exit
                    if (StrategyInfo.MarketPosition >= 0)
                    {
                        Output.WriteLine("Cannot place BUY TO COVER order: No short position exists");
                        return;
                    }
                    
                    // Store the click price for CalcBar to handle
                    m_ClickPrice = currentClickPrice;
                    
                    // Flag for new order creation
                    m_OrderCreatedInMouseEvent = true;
                    
                    // Set flag to indicate this is an exit order
                    m_IsExitOrder = true;
                    
                    Output.WriteLine("PLACING ORDER: Market BUY TO COVER to exit short position");
                    Output.WriteLine("OnMouseEvent: Setting up BUY TO COVER order for processing in CalcBar");
                    
                    Output.WriteLine("OnMouseEvent: New BUY TO COVER order will be created in next CalcBar. Quantity: " + OrderQty);
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error processing mouse event: " + ex.Message + "\nStack Trace: " + ex.StackTrace);
            }
        }
    }
}
