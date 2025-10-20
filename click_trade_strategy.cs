using System;
using System.Drawing;
using System.Windows.Forms;
using PowerLanguage.Function;

namespace PowerLanguage.Strategy
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    [MouseEvents(true)]
    [IOGMode(IOGMode.Enabled)]
    [AllowSendOrdersAlways]
    public class click_trade_strategy : SignalObject
    {
        // Order objects for buy and sell stop-limit orders
        private IOrderStopLimit m_BuyStopLimitOrder;
        private IOrderStopLimit m_SellStopLimitOrder;
        
        // Target price and order state
        private double m_TargetPrice = 0;
        private bool m_BuyOrderActive = false;
        private bool m_SellOrderActive = false;

        public click_trade_strategy(object ctx) : base(ctx)
        {
        }

        protected override void Create()
        {
            // Create buy and sell stop-limit order objects
            m_BuyStopLimitOrder = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "BuyStopLimit", EOrderAction.Buy));
            m_SellStopLimitOrder = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "SellStopLimit", EOrderAction.SellShort));
            
            Output.WriteLine("======================================");
            Output.WriteLine("⚠️  LIVE TRADING MODE ACTIVE ⚠️");
            Output.WriteLine("======================================");
            Output.WriteLine("This strategy will place REAL orders with REAL money!");
            Output.WriteLine("Shift+Left Click above price: Buy Stop-Limit");
            Output.WriteLine("Shift+Left Click below price: Sell Stop-Limit");
            Output.WriteLine("Shift+Right Click: Cancel all orders");
            Output.WriteLine("======================================");
        }

        protected override void StartCalc()
        {
            // Log trading mode at startup
            Output.WriteLine("=== Strategy Startup ===");
            Output.WriteLine("Real-time mode: " + Environment.IsRealTimeCalc);
            Output.WriteLine("Symbol: " + Bars.Info.Name);
            Output.WriteLine("Contract size: " + Contracts.Default);

            // Skip all historical bars - only work in real-time
            if (!Environment.IsRealTimeCalc)
            {
                Output.WriteLine("SKIPPING HISTORICAL CALCULATION - Strategy will activate in real-time");
            }
            else
            {
                Output.WriteLine("⚠️  LIVE TRADING ACTIVE - Orders will be sent to broker ⚠️");
            }
        }

        protected override void CalcBar()
        {
            // Skip historical bars - only work in real-time
            if (!Environment.IsRealTimeCalc)
                return;

            // Only place orders when flat
            if (StrategyInfo.MarketPosition == 0)
            {
                // Handle buy stop-limit order - continuously resubmit (required for IOG mode)
                if (m_BuyOrderActive && m_TargetPrice > 0)
                {
                    try
                    {
                        // Stop-limit: stop price = target, limit price = target + 1 tick
                        double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                        double limitPrice = m_TargetPrice + tickSize;
                        
                        m_BuyStopLimitOrder.Send(m_TargetPrice, limitPrice);
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("⚠️  ERROR sending buy order: " + ex.Message);
                    }
                }
                
                // Handle sell stop-limit order - continuously resubmit (required for IOG mode)
                if (m_SellOrderActive && m_TargetPrice > 0)
                {
                    try
                    {
                        // Stop-limit: stop price = target, limit price = target - 1 tick
                        double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                        double limitPrice = m_TargetPrice - tickSize;
                        
                        m_SellStopLimitOrder.Send(m_TargetPrice, limitPrice);
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("⚠️  ERROR sending sell order: " + ex.Message);
                    }
                }
            }
            else
            {
                // If we have a position, clear all orders
                m_BuyOrderActive = false;
                m_SellOrderActive = false;
                Output.WriteLine("✓ POSITION FILLED! Entry: " + StrategyInfo.AvgEntryPrice.ToString("F2") + 
                               " | Size: " + StrategyInfo.MarketPosition + " contracts");
            }
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            // Debug: Log all mouse events
            Output.WriteLine("Mouse event: Button=" + arg.buttons + " Keys=" + arg.keys + " Price=" + arg.point.Price.ToString("F2"));
            
            // Shift+Left Click: Smart order placement based on price position
            if (arg.buttons == MouseButtons.Left && (arg.keys & Keys.Shift) == Keys.Shift)
            {
                double clickPrice = arg.point.Price;
                double currentPrice = Bars.Close[0];
                
                // Clear any existing orders first
                m_BuyOrderActive = false;
                m_SellOrderActive = false;
                
                // Determine order direction based on click position relative to current price
                if (clickPrice > currentPrice)
                {
                    // Click above current price = Buy Stop-Limit
                    m_TargetPrice = clickPrice;
                    m_BuyOrderActive = true;
                    
                    Output.WriteLine("======================================");
                    Output.WriteLine("⚠️  LIVE BUY STOP-LIMIT ORDER PLACED ⚠️");
                    Output.WriteLine("Stop Price: " + m_TargetPrice.ToString("F2"));
                    Output.WriteLine("Current price: " + currentPrice.ToString("F2"));
                    Output.WriteLine("Symbol: " + Bars.Info.Name);
                    Output.WriteLine("======================================");
                }
                else if (clickPrice < currentPrice)
                {
                    // Click below current price = Sell Stop-Limit
                    m_TargetPrice = clickPrice;
                    m_SellOrderActive = true;
                    
                    Output.WriteLine("======================================");
                    Output.WriteLine("⚠️  LIVE SELL STOP-LIMIT ORDER PLACED ⚠️");
                    Output.WriteLine("Stop Price: " + m_TargetPrice.ToString("F2"));
                    Output.WriteLine("Current price: " + currentPrice.ToString("F2"));
                    Output.WriteLine("Symbol: " + Bars.Info.Name);
                    Output.WriteLine("======================================");
                }
                else
                {
                    Output.WriteLine("⚠️  Cannot place order at current price. Click above for Buy or below for Sell.");
                }
            }
            
            // Shift+Right Click: Cancel all orders
            if (arg.buttons == MouseButtons.Right && (arg.keys & Keys.Shift) == Keys.Shift)
            {
                m_BuyOrderActive = false;
                m_SellOrderActive = false;
                m_TargetPrice = 0;
                
                Output.WriteLine("✓ All orders cancelled - No longer active");
            }
        }
    }
}
