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
        [Input] public int ProtectiveStopPoints { get; set; }
        [Input] public bool Development { get; set; }

        // State variables
        private double m_ClickPrice = 0;
        private bool m_ActiveStopLimitOrder = false;
        private double m_StopPrice = 0;
        private double m_LimitPrice = 0;
        private bool m_OrderCreatedInMouseEvent = false;
        private bool m_IsExitOrder = false; // Flag to track if we're placing an exit order
        private bool m_IsBuyOrder = false; // Flag to track if we're placing a buy order
        private bool m_Debug = false;
        private bool m_CancelOrder = false; // Flag to indicate order cancellation
        private bool m_HasProtectiveStop = false; // Flag to track if we have an active protective stop
        private double m_ProtectiveStopPrice = 0; // Price for the protective stop

        // Order objects
        private IOrderStopLimit m_StopLimitSell;
        private IOrderStopLimit m_StopLimitBuy;
        private IOrderMarket m_BuyToCover;
        private IOrderMarket m_Sell;
        private IOrderStopLimit m_ProtectiveStopLong; // Stop loss for long positions
        private IOrderStopLimit m_ProtectiveStopShort; // Stop loss for short positions

        public clicktrader(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            TicksBelow = 15;
            OrderQty = 1;
            ProtectiveStopPoints = 10; // Default to 10 points for protective stop
            Development = false; // Disable debug mode by default
        }

        protected override void Create()
        {
            base.Create();

            // Create stop limit sell short order with basic parameters
            m_StopLimitSell = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "StopLimitSellShort", EOrderAction.SellShort));

            // Create stop limit buy order with basic parameters
            m_StopLimitBuy = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "StopLimitBuy", EOrderAction.Buy));

            // Create market buy to cover order for exiting short positions
            m_BuyToCover = OrderCreator.MarketNextBar(
                new SOrderParameters(Contracts.Default, "BuyToCover", EOrderAction.BuyToCover));

            // Create market sell order for exiting long positions
            m_Sell = OrderCreator.MarketNextBar(
                new SOrderParameters(Contracts.Default, "Sell", EOrderAction.Sell));
                
            // Create protective stop orders for both long and short positions
            m_ProtectiveStopLong = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "ProtectiveStopLong", EOrderAction.Sell));
                
            m_ProtectiveStopShort = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "ProtectiveStopShort", EOrderAction.BuyToCover));

            // Set debug flag based on development mode
            m_Debug = Development;

            // Log initialization information
            StringBuilder envInfo = new StringBuilder();
            envInfo.AppendLine("*** CLICKTRADER INITIALIZED ***");
            envInfo.AppendLine("Use Ctrl+Click to place a stop limit SELL SHORT order");
            envInfo.AppendLine("Use Shift+Click to place a stop limit BUY order");
            envInfo.AppendLine("Use Right Click to cancel orders and exit positions");
            envInfo.AppendLine("Use F12 to cancel all pending orders");
            envInfo.AppendLine("Ticks Below/Above Click: " + TicksBelow);
            envInfo.AppendLine("Protective Stop Points: " + ProtectiveStopPoints);
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
                // No need to check for manually canceled orders here
                // We'll reset the state when placing a new order

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
                            // Check if we need to exit a short or long position
                            if (StrategyInfo.MarketPosition < 0) // Short position
                            {
                                // Send the buy to cover market order to exit short position
                                m_BuyToCover.Send(OrderQty);
                                Output.WriteLine("BUY TO COVER Market Order sent to exit short position");
                            }
                            else if (StrategyInfo.MarketPosition > 0) // Long position
                            {
                                // Send the sell market order to exit long position
                                m_Sell.Send(OrderQty);
                                Output.WriteLine("SELL Market Order sent to exit long position");
                            }
                            else
                            {
                                Output.WriteLine("No position to exit");
                            }
                        }
                        else
                        {
                            // Check if this is a buy or sell order
                            if (m_IsBuyOrder)
                            {
                                // Send the buy stop limit order with stop and limit prices
                                m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
                                Output.WriteLine("BUY Stop Limit Order sent: Stop @ " + m_StopPrice + ", Limit @ " + m_LimitPrice);
                            }
                            else
                            {
                                // Send the sell short stop limit order with stop and limit prices
                                m_StopLimitSell.Send(m_StopPrice, m_LimitPrice, OrderQty);
                                Output.WriteLine("SELL SHORT Stop Limit Order sent: Stop @ " + m_StopPrice + ", Limit @ " + m_LimitPrice);
                            }
                        }

                        // Set flags
                        m_ActiveStopLimitOrder = true;

                        // Reset flags after order is placed
                        m_ClickPrice = 0;
                        m_OrderCreatedInMouseEvent = false;
                        
                        // Check if we need to place a protective stop for an existing position
                        if (!m_IsExitOrder && !m_HasProtectiveStop && StrategyInfo.MarketPosition != 0)
                        {
                            // Calculate protective stop price based on position direction
                            double pointSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                            
                            if (StrategyInfo.MarketPosition > 0) // Long position
                            {
                                // For long positions, stop is below the current price
                                m_ProtectiveStopPrice = Bars.Close[0] - (ProtectiveStopPoints * pointSize);
                                m_ProtectiveStopLong.Send(m_ProtectiveStopPrice, OrderQty);
                                Output.WriteLine("Protective stop placed for LONG position at " + m_ProtectiveStopPrice + 
                                              " (" + ProtectiveStopPoints + " points below current price)");
                                
                                // Draw a horizontal line at the stop price
                                IPlotObject stopLine = DrwLine.Create(new ChartPoint(Bars.Time[5], m_ProtectiveStopPrice), 
                                                                 new ChartPoint(Bars.Time[0], m_ProtectiveStopPrice));
                                stopLine.Color = Color.Red;
                                stopLine.Width = 2;
                                stopLine.Style = LineStyle.Dashed;
                            }
                            else if (StrategyInfo.MarketPosition < 0) // Short position
                            {
                                // For short positions, stop is above the current price
                                m_ProtectiveStopPrice = Bars.Close[0] + (ProtectiveStopPoints * pointSize);
                                m_ProtectiveStopShort.Send(m_ProtectiveStopPrice, OrderQty);
                                Output.WriteLine("Protective stop placed for SHORT position at " + m_ProtectiveStopPrice + 
                                              " (" + ProtectiveStopPoints + " points above current price)");
                                
                                // Draw a horizontal line at the stop price
                                IPlotObject stopLine = DrwLine.Create(new ChartPoint(Bars.Time[5], m_ProtectiveStopPrice), 
                                                                 new ChartPoint(Bars.Time[0], m_ProtectiveStopPrice));
                                stopLine.Color = Color.Red;
                                stopLine.Width = 2;
                                stopLine.Style = LineStyle.Dashed;
                            }
                            
                            m_HasProtectiveStop = true;
                        }

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
                            // Check if this is a buy or sell order
                            if (m_IsBuyOrder)
                            {
                                m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);

                                if (m_Debug && Bars.LastBarOnChart)
                                {
                                    Output.WriteLine("CalcBar: Resubmitting BUY stop limit order to keep it active");
                                }
                            }
                            else
                            {
                                m_StopLimitSell.Send(m_StopPrice, m_LimitPrice, OrderQty);

                                if (m_Debug && Bars.LastBarOnChart)
                                {
                                    Output.WriteLine("CalcBar: Resubmitting SELL SHORT stop limit order to keep it active");
                                }
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
                        
                        // Manage protective stops
                        ManageProtectiveStops();
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
                
                // Manage protective stops if we didn't already do it
                if (!m_ActiveStopLimitOrder)
                {
                    ManageProtectiveStops();
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in CalcBar: " + ex.Message + "\nStack Trace: " + ex.StackTrace);
            }
        }

        // Method to manage protective stops based on current position
        private void ManageProtectiveStops()
        {
            try
            {
                // Check if we have a position
                if (StrategyInfo.MarketPosition == 0)
                {
                    // No position, no need for protective stop
                    if (m_HasProtectiveStop)
                    {
                        Output.WriteLine("Position closed - removing protective stop");
                        m_HasProtectiveStop = false;
                        m_ProtectiveStopPrice = 0;
                    }
                    return;
                }
                
                // If we have a position but no protective stop, create one
                if (!m_HasProtectiveStop)
                {
                    double pointSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                    
                    if (StrategyInfo.MarketPosition > 0) // Long position
                    {
                        // For long positions, stop is below the current price
                        m_ProtectiveStopPrice = Bars.Close[0] - (ProtectiveStopPoints * pointSize);
                        // For stop limit orders, we need both stop and limit prices
                        // Set limit price 1 tick below stop price for a reasonable fill
                        double limitPrice = m_ProtectiveStopPrice - pointSize;
                        m_ProtectiveStopLong.Send(m_ProtectiveStopPrice, limitPrice, OrderQty);
                        Output.WriteLine("Protective stop placed for LONG position at " + m_ProtectiveStopPrice + 
                                      " (" + ProtectiveStopPoints + " points below current price)");
                        
                        // Draw a horizontal line at the stop price
                        IPlotObject stopLine = DrwLine.Create(new ChartPoint(Bars.Time[5], m_ProtectiveStopPrice), 
                                                         new ChartPoint(Bars.Time[0], m_ProtectiveStopPrice));
                        stopLine.Color = Color.Red;
                        stopLine.Width = 2;
                        stopLine.Style = LineStyle.Dashed;
                    }
                    else if (StrategyInfo.MarketPosition < 0) // Short position
                    {
                        // For short positions, stop is above the current price
                        m_ProtectiveStopPrice = Bars.Close[0] + (ProtectiveStopPoints * pointSize);
                        // For stop limit orders, we need both stop and limit prices
                        // Set limit price 1 tick above stop price for a reasonable fill
                        double limitPrice = m_ProtectiveStopPrice + pointSize;
                        m_ProtectiveStopShort.Send(m_ProtectiveStopPrice, limitPrice, OrderQty);
                        Output.WriteLine("Protective stop placed for SHORT position at " + m_ProtectiveStopPrice + 
                                      " (" + ProtectiveStopPoints + " points above current price)");
                        
                        // Draw a horizontal line at the stop price
                        IPlotObject stopLine = DrwLine.Create(new ChartPoint(Bars.Time[5], m_ProtectiveStopPrice), 
                                                         new ChartPoint(Bars.Time[0], m_ProtectiveStopPrice));
                        stopLine.Color = Color.Red;
                        stopLine.Width = 2;
                        stopLine.Style = LineStyle.Dashed;
                    }
                    
                    m_HasProtectiveStop = true;
                }
                // If we already have a protective stop, keep resubmitting it
                else
                {
                    if (StrategyInfo.MarketPosition > 0) // Long position
                    {
                        // For stop limit orders, we need both stop and limit prices
                        double limitPrice = m_ProtectiveStopPrice - (Bars.Info.MinMove / Bars.Info.PriceScale);
                        m_ProtectiveStopLong.Send(m_ProtectiveStopPrice, limitPrice, OrderQty);
                        if (m_Debug && Bars.LastBarOnChart)
                        {
                            Output.WriteLine("Resubmitting protective stop for LONG position at " + m_ProtectiveStopPrice);
                        }
                    }
                    else if (StrategyInfo.MarketPosition < 0) // Short position
                    {
                        // For stop limit orders, we need both stop and limit prices
                        double limitPrice = m_ProtectiveStopPrice + (Bars.Info.MinMove / Bars.Info.PriceScale);
                        m_ProtectiveStopShort.Send(m_ProtectiveStopPrice, limitPrice, OrderQty);
                        if (m_Debug && Bars.LastBarOnChart)
                        {
                            Output.WriteLine("Resubmitting protective stop for SHORT position at " + m_ProtectiveStopPrice);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error managing protective stops: " + ex.Message);
            }
        }
        
        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            try
            {
                // Debug output to see what keys are being pressed
                Output.WriteLine("DEBUG: Mouse event detected - Keys: " + arg.keys + ", Buttons: " + arg.buttons);
                
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

                // Handle Right Click to cancel pending orders and exit positions
                if (arg.buttons == MouseButtons.Right)
                {
                    bool actionTaken = false;
                    
                    // Cancel any pending orders
                    if (m_ActiveStopLimitOrder)
                    {
                        m_CancelOrder = true;
                        Output.WriteLine("Right Click - Canceling strategy pending orders");
                        actionTaken = true;
                    }
                    
                    // We can only cancel orders through the flag-based mechanism
                    // The strategy will stop resubmitting orders in the next CalcBar call
                    
                    // Exit any open positions
                    if (StrategyInfo.MarketPosition != 0)
                    {
                        // Set up exit order parameters
                        m_ClickPrice = arg.point.Price; // Not really used for market orders but set it anyway
                        m_OrderCreatedInMouseEvent = true;
                        m_IsExitOrder = true;
                        
                        if (StrategyInfo.MarketPosition < 0) // Short position
                        {
                            Output.WriteLine("Right Click - Exiting short position with BUY TO COVER");
                        }
                        else // Long position
                        {
                            Output.WriteLine("Right Click - Exiting long position with SELL");
                        }
                        actionTaken = true;
                    }
                    
                    if (!actionTaken)
                    {
                        Output.WriteLine("No active orders or positions to cancel/exit");
                    }
                    
                    return;
                }

                // Only process left mouse clicks with modifier keys
                if (arg.buttons != MouseButtons.Left || (arg.keys != Keys.Control && arg.keys != Keys.Alt && arg.keys != Keys.Shift))
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
                    m_IsBuyOrder = false;

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
                // Handle Shift+Click for buy stop limit orders
                else if (arg.keys == Keys.Shift)
                {
                    // Calculate stop and limit prices for buy order
                    // For buy orders, stop price is above the click price
                    m_StopPrice = currentClickPrice + (TicksBelow * tickSize);
                    // Set limit price 1 tick above stop price for a reasonable fill
                    m_LimitPrice = m_StopPrice + tickSize;

                    // Store the click price for CalcBar to handle
                    m_ClickPrice = currentClickPrice;

                    // Flag for new order creation
                    m_OrderCreatedInMouseEvent = true;

                    // Set flag to indicate this is a buy order
                    m_IsBuyOrder = true;
                    m_IsExitOrder = false;

                    Output.WriteLine("PLACING ORDER: Stop Limit BUY at stop price " + m_StopPrice +
                                  " (" + TicksBelow + " ticks above " + currentClickPrice + ")");
                    Output.WriteLine("OnMouseEvent: Setting up BUY order for processing in CalcBar");
                    Output.WriteLine("Stop Price: " + m_StopPrice + ", Limit Price: " + m_LimitPrice);

                    // If we have an active order, log that we'll be canceling it
                    if (m_ActiveStopLimitOrder)
                    {
                        Output.WriteLine("OnMouseEvent: Existing order will be canceled before creating a new one");
                    }

                    Output.WriteLine("OnMouseEvent: New BUY order will be created in next CalcBar. Stop Price: " + m_StopPrice +
                                  ", Limit Price: " + m_LimitPrice + ", Quantity: " + OrderQty);
                }
                // Handle Alt+Click for exit positions (buy to cover or sell)
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

                    // Check position type to determine exit order type
                    if (StrategyInfo.MarketPosition < 0) // Short position
                    {
                        Output.WriteLine("PLACING ORDER: Market BUY TO COVER to exit short position");
                        Output.WriteLine("OnMouseEvent: Setting up BUY TO COVER order for processing in CalcBar");
                        Output.WriteLine("OnMouseEvent: New BUY TO COVER order will be created in next CalcBar. Quantity: " + OrderQty);
                    }
                    else if (StrategyInfo.MarketPosition > 0) // Long position
                    {
                        Output.WriteLine("PLACING ORDER: Market SELL to exit long position");
                        Output.WriteLine("OnMouseEvent: Setting up SELL order for processing in CalcBar");
                        Output.WriteLine("OnMouseEvent: New SELL order will be created in next CalcBar. Quantity: " + OrderQty);
                    }
                    else
                    {
                        Output.WriteLine("No position to exit");
                    }
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error processing mouse event: " + ex.Message + "\nStack Trace: " + ex.StackTrace);
            }
        }
    }
}
