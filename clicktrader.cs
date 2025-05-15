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
            // OPTIMIZATION: Check for cancellation or emergency exit at the very beginning
            // This ensures we process these high-priority actions before anything else
            if (m_CancelOrder || m_EmergencyExit)
            {
                // Immediately stop all order activity first
                bool wasActiveOrder = m_ActiveStopLimitOrder;
                bool hadProtectiveStop = m_HasProtectiveStop;
                
                // Immediately disable all order flags to prevent any resubmission
                m_ActiveStopLimitOrder = false;
                m_HasProtectiveStop = false;
                
                // Process emergency exit with highest priority if needed
                if (m_EmergencyExit)
                {
                    if (StrategyInfo.MarketPosition > 0)
                    {
                        // Exit long position immediately
                        m_ExitLongOrder.Send(OrderQty);
                        // Emergency exit logging removed for performance
                    }
                    else if (StrategyInfo.MarketPosition < 0)
                    {
                        // Exit short position immediately
                        m_ExitShortOrder.Send(OrderQty);
                        // Emergency exit logging removed for performance
                    }
                    
                    m_EmergencyExit = false; // Reset flag after processing
                }
                
                // Only output cancellation message if we actually had active orders
                // Cancel logging removed for performance
                
                m_CancelOrder = false;
                
                // Skip all other order processing this bar
                return;
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
                            // Exit logging removed for performance
                        }
                        else if (StrategyInfo.MarketPosition < 0)
                        {
                            // Exit short position with a market buy to cover order
                            m_ExitShortOrder.Send(OrderQty);
                            // Exit logging removed for performance
                        }

                        // Reset protective stop when exiting position
                        m_HasProtectiveStop = false;
                    }
                    // No position logging removed for performance

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

                        // Buy order creation logging removed for performance
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

                        // Sell order creation logging removed for performance
                    }

                    // Reset flag since we've processed the order
                    m_OrderCreatedInMouseEvent = false;
                }
            }

            // OPTIMIZATION: Check if we have pending orders before resubmitting
            // If we have an active stop limit order, keep submitting it until filled or canceled
            if (m_ActiveStopLimitOrder)
            {
                // Submit the appropriate order based on the buy/sell flag
                if (m_IsBuyOrder)
                {
                    m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
                    // Resubmit logging removed for performance
                }
                else
                {
                    m_StopLimitSell.Send(m_StopPrice, m_LimitPrice, OrderQty);
                    // Resubmit logging removed for performance
                }
            }

            // Only manage protective stops if we haven't canceled orders
            if (!m_CancelOrder && !m_EmergencyExit)
            {
                ManageProtectiveStops();
            }
        }

        private void ManageProtectiveStops()
        {
            try
            {
                // OPTIMIZATION: Quick early return if cancellation is in progress
                if (m_CancelOrder || m_EmergencyExit)
                {
                    return;
                }
                
                // If we have no position, clear the protective stop flag to prevent resubmission
                if (StrategyInfo.MarketPosition == 0)
                {
                    if (m_HasProtectiveStop)
                    {
                        // Position closed logging removed for performance
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
                        // Protective stop logging removed for performance

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
                        // Protective stop logging removed for performance

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
                            // Resubmit protective stop logging removed for performance
                        }
                    }
                    else if (StrategyInfo.MarketPosition < 0) // Short position
                    {
                        // For stop limit orders, we need both stop and limit prices
                        double limitPrice = m_ProtectiveStopPrice + (Bars.Info.MinMove / Bars.Info.PriceScale);
                        m_ProtectiveStopShort.Send(m_ProtectiveStopPrice, limitPrice, OrderQty);
                        if (m_Debug && Bars.LastBarOnChart)
                        {
                            // Resubmit protective stop logging removed for performance
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Error logging removed for performance
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
                    // OPTIMIZATION: Set flags in the correct order for maximum responsiveness
                    // First disable all order flags to immediately stop resubmission
                    m_ActiveStopLimitOrder = false;
                    m_HasProtectiveStop = false;
                    // Then set action flags
                    m_CancelOrder = true;
                    return;
                }

                // Handle right click - ultra-optimized for maximum responsiveness
                if (arg.buttons == MouseButtons.Right)
                {
                    // OPTIMIZATION: Set flags in the correct order for maximum responsiveness
                    // First disable all order flags to immediately stop resubmission
                    m_ActiveStopLimitOrder = false;
                    m_HasProtectiveStop = false;
                    // Then set action flags
                    m_CancelOrder = true;
                    m_EmergencyExit = true;  // High-priority flag
                    
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
                    // Smart order handling - check if we already have an active buy order
                    bool isAdjustingExistingOrder = m_ActiveStopLimitOrder && m_IsBuyOrder;
                    
                    // Set flags for buy order
                    m_IsBuyOrder = true;
                    m_IsExitOrder = false;
                    m_OrderCreatedInMouseEvent = true;
                    
                    // If we're adjusting an existing order, we could add special handling here if needed
                    // Currently, the same code path works for both new orders and adjustments
                }
                // Handle Ctrl+Click for Sell Short orders
                else if ((arg.keys & Keys.Control) == Keys.Control)
                {
                    // Smart order handling - check if we already have an active sell order
                    bool isAdjustingExistingOrder = m_ActiveStopLimitOrder && !m_IsBuyOrder;
                    
                    // Set flags for sell order
                    m_IsBuyOrder = false;
                    m_IsExitOrder = false;
                    m_OrderCreatedInMouseEvent = true;
                    
                    // If we're adjusting an existing order, we could add special handling here if needed
                    // Currently, the same code path works for both new orders and adjustments
                }
            }
            catch (Exception ex)
            {
                // Error logging removed for performance
            }
        }
    }
}
