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
    public class clicktrader1 : SignalObject
    {
        // Input variables
        [Input] public int TicksBelow { get; set; }
        [Input] public int OrderQty { get; set; }
        [Input] public bool ShowDebug { get; set; }

        // State variables - minimal set
        private double m_ClickPrice = 0;
        private bool m_ActiveOrder = false;   // Flag to track if we have an active order
        private bool m_CancelOrder = false;   // Flag to track if we need to cancel orders
        private double m_StopPrice = 0;
        private double m_LimitPrice = 0;
        private bool m_OrderFilled = false;   // Flag to track if our order was filled
        private int m_LastOrderId = 0;        // Track the last order ID we submitted
        private bool m_ClosingPosition = false; // Flag to track if we're trying to close a position
        private bool m_EmergencyExit = false; // Flag for emergency position exit

        // Throttling variables
        private int m_TickCount = 0;
        private const int DEBUG_THROTTLE = 100;  // Only output debug every 100 ticks
        private DateTime m_LastOrderTime = DateTime.MinValue; // Track last order submission time
        private const int MAX_ORDERS_PER_SECOND = 20; // Maximum orders per second
        private const int MIN_MS_BETWEEN_ORDERS = 50; // Minimum 50ms between orders (20 per second)

        // Order objects
        private IOrderStopLimit m_StopLimitSell;
        private IOrderMarket m_ExitShortOrder; // For closing short positions

        public clicktrader1(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            TicksBelow = 15;
            OrderQty = 1;
            ShowDebug = true;
        }

        protected override void Create()
        {
            base.Create();

            // Create sell stop limit order object
            m_StopLimitSell = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "S", EOrderAction.SellShort));
                
            // Create market order for exiting short positions
            m_ExitShortOrder = OrderCreator.MarketThisBar(
                new SOrderParameters(Contracts.Default, "ExitShort", EOrderAction.BuyToCover));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override void CalcBar()
        {
            // Throttle debug output to prevent console flooding
            m_TickCount++;
            bool shouldDebug = ShowDebug && (m_TickCount % DEBUG_THROTTLE == 0 ||
                                          m_ActiveOrder ||
                                          m_OrderFilled);

            // Only output debug when something interesting is happening
            if (shouldDebug)
            {
                Output.WriteLine("CalcBar: MarketPosition=" + StrategyInfo.MarketPosition +
                                ", m_ActiveOrder=" + m_ActiveOrder +
                                ", m_OrderFilled=" + m_OrderFilled);
            }

            // Check for emergency exit first (highest priority)
            if (m_EmergencyExit)
            {
                // Close any open positions
                if (StrategyInfo.MarketPosition < 0)
                {
                    // Exit short position with market order
                    m_ExitShortOrder.Send(Math.Abs(StrategyInfo.MarketPosition));
                    if (ShowDebug) Output.WriteLine("CalcBar - Emergency exit - Closing short position with market order");
                }
                
                // Reset all flags
                m_ActiveOrder = false;
                m_OrderFilled = false;
                m_CancelOrder = false;
                m_EmergencyExit = false;
                m_ClosingPosition = false;
                
                // Skip all other order processing this bar
                return;
            }

            // Check for order cancellation
            if (m_CancelOrder)
            {
                // Reset flags to prevent resubmission
                m_ActiveOrder = false;
                m_CancelOrder = false;
                
                if (ShowDebug) Output.WriteLine("CalcBar - Order cancellation - Resetting flags");
                
                // Skip all other order processing this bar
                return;
            }
            
            // Check for position closing
            if (m_ClosingPosition && StrategyInfo.MarketPosition < 0)
            {
                // Close short position with market order
                m_ExitShortOrder.Send(Math.Abs(StrategyInfo.MarketPosition));
                if (ShowDebug) Output.WriteLine("CalcBar - Closing short position with market order");
                
                // Reset flag to prevent repeated closing attempts
                m_ClosingPosition = false;
                
                // Skip all other order processing this bar
                return;
            }

            // Reset the filled flag if we no longer have a position
            // This allows submitting new orders after a position is closed
            if (StrategyInfo.MarketPosition == 0)
            {
                if (m_OrderFilled)
                {
                    m_OrderFilled = false;
                    if (ShowDebug) Output.WriteLine("Position closed, reset m_OrderFilled = false");
                }
            }

            // Check if our active order was filled
            if (m_ActiveOrder && !m_OrderFilled && StrategyInfo.MarketPosition < 0)
            {
                // Order was filled, stop resubmitting
                m_ActiveOrder = false;
                m_OrderFilled = true;
                if (ShowDebug) Output.WriteLine("CalcBar - Order was filled, stopping resubmission");
                return;
            }

            // Submit new order if active and not filled
            if (m_ActiveOrder && !m_OrderFilled)
            {
                // Throttle order submission to prevent flooding the broker
                DateTime now = DateTime.Now;
                TimeSpan timeSinceLastOrder = now - m_LastOrderTime;
                
                if (timeSinceLastOrder.TotalMilliseconds >= MIN_MS_BETWEEN_ORDERS)
                {
                    // Submit stop limit sell order
                    m_StopLimitSell.Send(m_StopPrice, m_LimitPrice, OrderQty);
                    m_LastOrderId = m_StopLimitSell.ID;
                    m_LastOrderTime = now;
                    
                    if (ShowDebug) Output.WriteLine("CalcBar - Submitting stop limit sell order: Stop=" + 
                                                  m_StopPrice + ", Limit=" + m_LimitPrice + 
                                                  ", Qty=" + OrderQty + ", ID=" + m_LastOrderId);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // We don't use event handlers - simpler approach
        
        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            // Only process mouse events when we're on the last bar
            if (!Bars.LastBarOnChart)
                return;

            // Handle mouse events based on button and modifiers
            if (arg.keys == Keys.Control)
            {
                // Handle ctrl+left click for order submission
                if (arg.buttons == MouseButtons.Left)
                {
                    // Ctrl+Left-click: Submit a new order
                    m_ClickPrice = arg.point.Price;
                    m_StopPrice = m_ClickPrice - TicksBelow * Bars.Info.MinMove / Bars.Info.PriceScale;
                    m_LimitPrice = m_StopPrice - 1 * Bars.Info.MinMove / Bars.Info.PriceScale;
                    
                    // Set flags for order submission in CalcBar
                    m_ActiveOrder = true;
                    m_OrderFilled = false;
                    m_CancelOrder = false;
                    m_EmergencyExit = false;

                    // No debug output for ctrl+left-click
                    return;
                }
                
                // Handle ctrl+right click for emergency reset
                if (arg.buttons == MouseButtons.Right)
                {
                    // Ctrl+Right-click: Emergency reset of all flags
                    m_OrderFilled = false;
                    m_ActiveOrder = false;
                    m_CancelOrder = false;
                    m_EmergencyExit = false;
                    
                    // No debug output for ctrl+right-click
                    return;
                }
            }
            else
            {
                // Handle right click for cancellation and position closing
                if (arg.buttons == MouseButtons.Right)
                {
                    // Right-click: Cancel pending orders and trigger emergency exit
                    m_CancelOrder = true;
                    m_EmergencyExit = true;
                    
                    // No debug output for right-click
                    return;
                }
            }
        }
    }
}
