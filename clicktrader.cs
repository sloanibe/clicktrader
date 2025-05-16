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
        [Input] public int PointsOffset { get; set; } // Offset in points from click price
        [Input] public int OrderQty { get; set; } // Quantity of contracts to trade

        // State variables
        private double m_ClickPrice = 0;
        private bool m_OrderCreatedInMouseEvent = false;
        private bool m_IsBuyOrder = true; // Flag to track if the order is buy or sell
        private bool m_CancelOrder = false; // Flag to track if we need to cancel orders
        private bool m_ActiveStopLimitOrder = false; // Flag to track if we have an active order

        // Order objects
        private IOrderStopLimit m_StopLimitBuy;
        private IOrderStopLimit m_StopLimitSell;

        public clicktrader(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            PointsOffset = 15; // Exactly 15 points offset from click price
            OrderQty = 1;
        }

        protected override void Create()
        {
            base.Create();

            // Create both buy and sell stop limit order objects
            m_StopLimitBuy = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "StopLimitBuy", EOrderAction.Buy));

            m_StopLimitSell = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "StopLimitSell", EOrderAction.SellShort));
        }

        // State variables for order tracking
        private double m_StopPrice = 0;
        private double m_LimitPrice = 0;

        // CalcBar - submit, resubmit, or cancel orders
        protected override void CalcBar()
        {
            // Check if we need to cancel orders
            if (m_CancelOrder)
            {
                // Cancel orders by sending zero-quantity orders
                if (m_IsBuyOrder && m_StopLimitBuy != null)
                {
                    // Send a zero-quantity order to cancel the existing buy order
                    m_StopLimitBuy.Send(0, 0, 0);
                    Output.WriteLine("Canceled buy order");
                }
                else if (!m_IsBuyOrder && m_StopLimitSell != null)
                {
                    // Send a zero-quantity order to cancel the existing sell order
                    m_StopLimitSell.Send(0, 0, 0);
                    Output.WriteLine("Canceled sell order");
                }

                // Reset flags
                m_CancelOrder = false;
                m_ActiveStopLimitOrder = false;
                m_OrderCreatedInMouseEvent = false;
                Output.WriteLine("Orders cancelled");
                return;
            }

            // Check if we have a new order from mouse click
            if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0)
            {
                // Create the order based on buy/sell flag
                if (m_IsBuyOrder)
                {
                    // For buy orders, place stop PointsOffset points ABOVE the click price
                    m_StopPrice = m_ClickPrice + PointsOffset;
                    // Set limit price 15 points above stop price
                    m_LimitPrice = m_StopPrice + 15;

                    // Send the buy order
                    m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
                    m_ActiveStopLimitOrder = true;
                    Output.WriteLine("Buy order sent at " + m_StopPrice + " (" + PointsOffset + " points above click)");
                }
                else
                {
                    // For sell orders, place stop PointsOffset points BELOW the click price
                    m_StopPrice = m_ClickPrice - PointsOffset;
                    // Set limit price 15 points below stop price
                    m_LimitPrice = m_StopPrice - 15;

                    // Send the sell order
                    m_StopLimitSell.Send(m_StopPrice, m_LimitPrice, OrderQty);
                    m_ActiveStopLimitOrder = true;
                    Output.WriteLine("Sell order sent at " + m_StopPrice + " (" + PointsOffset + " points below click)");
                }

                // Reset flag after sending order
                m_OrderCreatedInMouseEvent = false;
            }
            // Resubmit active orders on each bar until filled or canceled
            else if (m_ActiveStopLimitOrder && !m_OrderCreatedInMouseEvent)
            {
                // Check if the order has been filled
                int marketPosition = StrategyInfo.MarketPosition;
                bool orderFilled = (m_IsBuyOrder && marketPosition > 0) || (!m_IsBuyOrder && marketPosition < 0);

                if (!orderFilled)
                {
                    // Resubmit the order
                    if (m_IsBuyOrder)
                    {
                        m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
                    }
                    else
                    {
                        m_StopLimitSell.Send(m_StopPrice, m_LimitPrice, OrderQty);
                    }
                }
                else
                {
                    // Order has been filled, reset the active order flag
                    m_ActiveStopLimitOrder = false;
                    Output.WriteLine("Order filled");
                }
            }
        }

        // Ultra-minimal mouse handler - just two essential actions
        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            // Only log important mouse events
            
            // RIGHT CLICK: Cancel all orders
            if (arg.buttons == MouseButtons.Right)
            {
                m_CancelOrder = true;
                Output.WriteLine("Right click detected - canceling orders");
                return;
            }

            // SHIFT+LEFT CLICK: Create buy stop limit order
            if (arg.buttons == MouseButtons.Left && (arg.keys & Keys.Shift) == Keys.Shift)
            {
                // Get the price at the click position
                m_ClickPrice = arg.point.Price;

                // Set up for buy order
                m_IsBuyOrder = true;
                m_OrderCreatedInMouseEvent = true;
                Output.WriteLine("Shift+Left click detected - setting up buy order at " + m_ClickPrice);
                return;
            }

            // CTRL+LEFT CLICK: Create sell stop limit order
            if (arg.buttons == MouseButtons.Left && (arg.keys & Keys.Control) == Keys.Control)
            {
                // Get the price at the click position
                m_ClickPrice = arg.point.Price;

                // Set up for sell order
                m_IsBuyOrder = false;
                m_OrderCreatedInMouseEvent = true;
                Output.WriteLine("Ctrl+Left click detected - setting up sell order at " + m_ClickPrice);
                return;
            }
        }
    }
}
