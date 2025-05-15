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
    [AllowSendOrdersAlways]
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
        private bool m_IsBuyOrder = true; // Flag to track if the order is buy or sell
        private bool m_IsExitOrder = false; // Flag to track if the order is an exit order
        private bool m_CancelOrder = false; // Flag to track if we need to cancel orders
        private bool m_Debug = false;

        // Protective stop variables
        private bool m_HasProtectiveStop = false;
        private double m_ProtectiveStopPrice = 0;

        // Order objects
        private IOrderStopLimit m_StopLimitBuy;
        private IOrderStopLimit m_StopLimitSell;
        private IOrderStopLimit m_ProtectiveStopLong;
        private IOrderStopLimit m_ProtectiveStopShort;

        public clicktrader(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            TicksBelow = 15;
            OrderQty = 1;
            ProtectiveStopPoints = 10; // Default to 10 points for protective stop
            Development = false; // Disable debug mode by default
        }

        // Market order objects for exits
        private IOrderMarket m_ExitLongOrder;
        private IOrderMarket m_ExitShortOrder;
        
        protected override void Create()
        {
            base.Create();

            // Create both buy and sell stop limit order objects
            m_StopLimitBuy = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "StopLimitBuy", EOrderAction.Buy));

            m_StopLimitSell = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "StopLimitSell", EOrderAction.SellShort));

            // Create protective stop order objects
            m_ProtectiveStopLong = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "ProtectiveStopLong", EOrderAction.Sell));

            m_ProtectiveStopShort = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "ProtectiveStopShort", EOrderAction.BuyToCover));
            
            // Create market orders for exits
            m_ExitLongOrder = OrderCreator.MarketNextBar(
                new SOrderParameters(Contracts.Default, "ExitLong", EOrderAction.Sell));
                
            m_ExitShortOrder = OrderCreator.MarketNextBar(
                new SOrderParameters(Contracts.Default, "ExitShort", EOrderAction.BuyToCover));

            // Set debug flag based on development mode
            m_Debug = Development;
        }
        
        protected override void CalcBar()
        {
            // High-priority emergency exit handling - process before anything else
            if (m_EmergencyExit)
            {
                // Process emergency exit with highest priority
                if (StrategyInfo.MarketPosition > 0)
                {
                    // Exit long position immediately
                    m_ExitLongOrder.Send(OrderQty);
                    if (m_Debug) Output.WriteLine("EMERGENCY EXIT: Exiting LONG position");
                }
                else if (StrategyInfo.MarketPosition < 0)
                {
                    // Exit short position immediately
                    m_ExitShortOrder.Send(OrderQty);
                    if (m_Debug) Output.WriteLine("EMERGENCY EXIT: Exiting SHORT position");
                }
                
                m_EmergencyExit = false; // Reset flag after processing
            }
            
            // Process new orders created from mouse events
            if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0)
            {
                // Calculate the number of ticks in price
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;

                if (m_IsExitOrder)
                {
                    // Handle exit orders
                    if (StrategyInfo.MarketPosition != 0)
                    {
                        if (StrategyInfo.MarketPosition > 0)
                        {
                            // Exit long position with a market sell order
                            m_ExitLongOrder.Send(OrderQty);
                            Output.WriteLine("Exiting LONG position with market order");
                        }
                        else if (StrategyInfo.MarketPosition < 0)
                        {
                            // Exit short position with a market buy to cover order
                            m_ExitShortOrder.Send(OrderQty);
                            Output.WriteLine("Exiting SHORT position with market order");
                        }

                        // Reset protective stop when exiting position
                        m_HasProtectiveStop = false;
                    }
                    else
                    {
                        Output.WriteLine("No position to exit");
                    }

                    // Reset flags
                    m_OrderCreatedInMouseEvent = false;
                    m_ClickPrice = 0;
                    m_IsExitOrder = false;
                }
                else
                {
                    // Handle entry orders
                    if (m_IsBuyOrder)
                    {
                        // For buy orders, stop price is above the click price
                        m_StopPrice = m_ClickPrice + (TicksBelow * tickSize);
                        // For stop limit orders, we need both stop and limit prices
                        // Set limit price 1 tick above stop price for a reasonable fill
                        m_LimitPrice = m_StopPrice + tickSize;

                        // Set active order flag to true so we continue to submit the order
                        m_ActiveStopLimitOrder = true;

                        Output.WriteLine("BUY order created at " + m_ClickPrice +
                                      " with stop price " + m_StopPrice +
                                      " and limit price " + m_LimitPrice);
                    }
                    else
                    {
                        // For sell orders, stop price is below the click price
                        m_StopPrice = m_ClickPrice - (TicksBelow * tickSize);
                        // For stop limit orders, we need both stop and limit prices
                        // Set limit price 1 tick below stop price for a reasonable fill
                        m_LimitPrice = m_StopPrice - tickSize;

                        // Set active order flag to true so we continue to submit the order
                        m_ActiveStopLimitOrder = true;

                        Output.WriteLine("SELL SHORT order created at " + m_ClickPrice +
                                      " with stop price " + m_StopPrice +
                                      " and limit price " + m_LimitPrice);
                    }

                    // Reset flag since we've processed the order
                    m_OrderCreatedInMouseEvent = false;
                }
            }

            // Check if we need to cancel orders
            if (m_CancelOrder)
            {
                m_ActiveStopLimitOrder = false;
                m_CancelOrder = false;
                Output.WriteLine("Orders canceled");
            }

            // If we have an active stop limit order, keep submitting it until filled or canceled
            if (m_ActiveStopLimitOrder)
            {
                // We can't check PendingOrders directly, so we'll just continue resubmitting
                // until the user explicitly cancels via our interface
                {
                    // Submit the appropriate order based on the buy/sell flag
                    if (m_IsBuyOrder)
                    {
                        m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
                        if (m_Debug && Bars.LastBarOnChart)
                        {
                            Output.WriteLine("Resubmitting BUY order at " + m_StopPrice);
                        }
                    }
                    else
                    {
                        m_StopLimitSell.Send(m_StopPrice, m_LimitPrice, OrderQty);
                        if (m_Debug && Bars.LastBarOnChart)
                        {
                            Output.WriteLine("Resubmitting SELL SHORT order at " + m_StopPrice);
                        }
                    }
                }
            }

            // Manage protective stops
            ManageProtectiveStops();
        }

        private void ManageProtectiveStops()
        {
            try
            {
                // If we have no position, clear the protective stop flag to prevent resubmission
                if (StrategyInfo.MarketPosition == 0)
                {
                    if (m_HasProtectiveStop)
                    {
                        Output.WriteLine("Position closed - protective stops will not be resubmitted");
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

                        // Visual indicator for protective stop removed - not supported in this version
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

                        // Visual indicator for protective stop removed - not supported in this version
                    }

                    m_HasProtectiveStop = true;
                }
                // If we already have a protective stop, check if we still have a position before resubmitting
                else
                {
                    // Double-check that we still have a position before resubmitting the stop
                    if (StrategyInfo.MarketPosition == 0)
                    {
                        Output.WriteLine("Position closed - protective stops will not be resubmitted");
                        m_HasProtectiveStop = false;
                        m_ProtectiveStopPrice = 0;
                    }
                    else if (StrategyInfo.MarketPosition > 0) // Long position
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

        // Emergency exit flag for high-priority position exit
        private bool m_EmergencyExit = false;
        
        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            try
            {
                // CRITICAL: Keep this method as lightweight as possible for responsiveness
                // No debug output in the main path
                
                // Handle F12 key press to cancel orders - minimal processing
                if (arg.keys == Keys.F12)
                {
                    m_CancelOrder = true;
                    return;
                }

                // Handle right click - ultra-optimized for maximum responsiveness
                if (arg.buttons == MouseButtons.Right)
                {
                    // Immediately set all flags for instant effect
                    m_CancelOrder = true;
                    m_ActiveStopLimitOrder = false;
                    m_HasProtectiveStop = false;
                    m_EmergencyExit = true;  // New high-priority flag
                    
                    // No output, no position checks, no order sending here
                    // All actual order processing moved to CalcBar for better performance
                    return;
                }

                // Only process left mouse button clicks
                if (arg.buttons != MouseButtons.Left)
                {
                    return;
                }

                // Get the price at the click position
                m_ClickPrice = arg.point.Price;

                // Handle Shift+Click for Buy orders
                if ((arg.keys & Keys.Shift) == Keys.Shift)
                {
                    m_IsBuyOrder = true;
                    m_IsExitOrder = false;
                    m_OrderCreatedInMouseEvent = true;
                    Output.WriteLine("Shift+Click detected at price " + m_ClickPrice + " - Creating BUY order");
                }
                // Handle Ctrl+Click for Sell Short orders
                else if ((arg.keys & Keys.Control) == Keys.Control)
                {
                    m_IsBuyOrder = false;
                    m_IsExitOrder = false;
                    m_OrderCreatedInMouseEvent = true;
                    Output.WriteLine("Ctrl+Click detected at price " + m_ClickPrice + " - Creating SELL SHORT order");
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in OnMouseEvent: " + ex.Message);
            }
        }
    }
}
