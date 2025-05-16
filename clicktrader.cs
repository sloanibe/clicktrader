using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;
using System.Text;
using System.Runtime.CompilerServices;

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
        private bool m_OrderFilled = false; // Flag to track if our order was filled
        private bool m_Debug = false;
        
        // Debug variables
        private int m_TickCount = 0;

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
        
        // Force this method to be as fast as possible
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override void CalcBar()
        {
            // Early exit if no work to do - major performance optimization
            if (!m_ActiveStopLimitOrder && !m_OrderCreatedInMouseEvent && 
                !m_CancelOrder && !m_EmergencyExit && !m_HasProtectiveStop)
                return;
                
            // Cache frequently accessed values
            int marketPosition = StrategyInfo.MarketPosition;
            
            // High-priority emergency exit handling
            if (m_EmergencyExit)
            {
                HandleEmergencyExit(marketPosition);
                return;
            }
            
            // Handle order cancellation
            if (m_CancelOrder)
            {
                m_ActiveStopLimitOrder = m_CancelOrder = false;
                if (m_Debug) Output.WriteLine("Orders canceled");
                return;
            }
            
            // Process new orders from mouse events
            if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0)
            {
                ProcessNewOrder(marketPosition);
            }
            
            // Reset filled flag if position is closed
            if (marketPosition == 0 && m_OrderFilled)
            {
                m_OrderFilled = false;
                if (m_Debug) Output.WriteLine("*** POSITION CLOSED - RESETTING ORDER FILLED FLAG ***");
            }
            
            // Check if order was filled
            if (m_ActiveStopLimitOrder && !m_OrderFilled)
            {
                // If order was filled, we'll need to place a protective stop
                CheckOrderFilled(marketPosition);
            }
            
            // Submit orders if active and not filled
            if (m_ActiveStopLimitOrder && !m_OrderFilled)
            {
                SubmitOrders();
            }
            
            // Manage protective stops
            if (m_HasProtectiveStop || marketPosition != 0)
            {
                ManageProtectiveStops();
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void HandleEmergencyExit(int marketPosition)
        {
            if (marketPosition > 0)
            {
                m_ExitLongOrder.Send(OrderQty);
                if (m_Debug) Output.WriteLine("SEND: EMERGENCY EXIT - Exiting LONG position with market order");
            }
            else if (marketPosition < 0)
            {
                m_ExitShortOrder.Send(OrderQty);
                if (m_Debug) Output.WriteLine("SEND: EMERGENCY EXIT - Exiting SHORT position with market order");
            }
            
            // Reset all flags with compound assignment for better performance
            m_ActiveStopLimitOrder = m_OrderFilled = m_CancelOrder = m_EmergencyExit = false;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessNewOrder(int marketPosition)
        {
            // Cache tick size calculation
            double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;

            if (m_IsExitOrder)
            {
                // Handle exit orders
                if (marketPosition != 0)
                {
                    if (marketPosition > 0)
                    {
                        m_ExitLongOrder.Send(OrderQty);
                        if (m_Debug) Output.WriteLine("SEND: Exiting LONG position with market order");
                    }
                    else
                    {
                        m_ExitShortOrder.Send(OrderQty);
                        if (m_Debug) Output.WriteLine("SEND: Exiting SHORT position with market order");
                    }

                    m_HasProtectiveStop = false;
                }
                else if (m_Debug)
                {
                    Output.WriteLine("No position to exit");
                }

                // Reset flags
                m_OrderCreatedInMouseEvent = m_IsExitOrder = false;
                m_ClickPrice = 0;
            }
            else
            {
                // Handle entry orders
                if (m_IsBuyOrder)
                {
                    m_StopPrice = m_ClickPrice + (TicksBelow * tickSize);
                    m_LimitPrice = m_StopPrice + tickSize;

                    if (m_Debug) Output.WriteLine("BUY order created at " + m_ClickPrice + 
                                  " with stop price " + m_StopPrice + 
                                  " and limit price " + m_LimitPrice);
                }
                else
                {
                    m_StopPrice = m_ClickPrice - (TicksBelow * tickSize);
                    m_LimitPrice = m_StopPrice - tickSize;

                    if (m_Debug) Output.WriteLine("SELL SHORT order created at " + m_ClickPrice + 
                                  " with stop price " + m_StopPrice + 
                                  " and limit price " + m_LimitPrice);
                }

                // Set active order flag
                m_ActiveStopLimitOrder = true;
                m_OrderFilled = false;
                m_OrderCreatedInMouseEvent = false;
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CheckOrderFilled(int marketPosition)
        {
            // For buy orders, check if we now have a long position
            if (m_IsBuyOrder && marketPosition > 0)
            {
                m_ActiveStopLimitOrder = false;
                m_OrderFilled = true;
                if (m_Debug) Output.WriteLine("*** BUY ORDER FILLED - STOPPING RESUBMISSION *** Position: " + marketPosition);
            }
            // For sell orders, check if we now have a short position
            else if (!m_IsBuyOrder && marketPosition < 0)
            {
                m_ActiveStopLimitOrder = false;
                m_OrderFilled = true;
                if (m_Debug) Output.WriteLine("*** SELL ORDER FILLED - STOPPING RESUBMISSION *** Position: " + marketPosition);
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SubmitOrders()
        {
            m_TickCount++;
            
            if (m_IsBuyOrder)
            {
                m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
                
                // Only output detailed logs when debug is enabled and throttle frequency
                if (m_Debug)
                {
                    if (m_TickCount % 100 == 0)
                    {
                        Output.WriteLine("TICK " + m_TickCount + ": Submitting BUY order at " + m_StopPrice);
                    }
                }
            }
            else
            {
                m_StopLimitSell.Send(m_StopPrice, m_LimitPrice, OrderQty);
                
                // Only output detailed logs when debug is enabled and throttle frequency
                if (m_Debug)
                {
                    if (m_TickCount % 100 == 0)
                    {
                        Output.WriteLine("TICK " + m_TickCount + ": Submitting SELL SHORT order at " + m_StopPrice);
                    }
                }
            }
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
                        Output.WriteLine("SEND: Protective stop placed for LONG position at " + m_ProtectiveStopPrice +
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
                        Output.WriteLine("SEND: Protective stop placed for SHORT position at " + m_ProtectiveStopPrice +
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
                        Output.WriteLine("SEND: Resubmitting protective stop for LONG position at " + m_ProtectiveStopPrice);
                    }
                    else if (StrategyInfo.MarketPosition < 0) // Short position
                    {
                        // For stop limit orders, we need both stop and limit prices
                        double limitPrice = m_ProtectiveStopPrice + (Bars.Info.MinMove / Bars.Info.PriceScale);
                        m_ProtectiveStopShort.Send(m_ProtectiveStopPrice, limitPrice, OrderQty);
                        Output.WriteLine("SEND: Resubmitting protective stop for SHORT position at " + m_ProtectiveStopPrice);
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
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
