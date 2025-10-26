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
        // Order objects for buy and sell stop orders (broker-routed for speed)
        private IOrderPriced m_BuyStopOrder;
        private IOrderPriced m_SellStopOrder;
        
        // Emergency exit order
        private IOrderMarket m_EmergencyExit;

        // Target price and order state
        private double m_TargetPrice = 0;
        private bool m_BuyOrderActive = false;
        private bool m_SellOrderActive = false;

        public click_trade_strategy(object ctx) : base(ctx)
        {
        }

        protected override void Create()
        {
            // Create buy and sell stop order objects (broker-routed, not algorithmic)
            m_BuyStopOrder = OrderCreator.Stop(
                new SOrderParameters(Contracts.Default, "BuyStop", EOrderAction.Buy));
            m_SellStopOrder = OrderCreator.Stop(
                new SOrderParameters(Contracts.Default, "SellStop", EOrderAction.SellShort));
            
            // Create emergency exit market order
            // Note: Using MarketThisBar - executes at current bar close
            // This is the fastest option available in MultiCharts .NET
            m_EmergencyExit = OrderCreator.MarketThisBar(
                new SOrderParameters(Contracts.Default, "EmergencyExit", EOrderAction.Sell));

            Output.WriteLine("======================================");
            Output.WriteLine("⚠️  LIVE TRADING MODE ACTIVE ⚠️");
            Output.WriteLine("======================================");
            Output.WriteLine("This strategy will place REAL orders with REAL money!");
            Output.WriteLine("Shift+Left Click above price: Buy Stop");
            Output.WriteLine("Shift+Left Click below price: Sell Stop");
            Output.WriteLine("Ctrl+Left Click: EMERGENCY EXIT (cancel orders & close position)");
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

            // Handle buy stop order
            if (m_BuyOrderActive && m_TargetPrice > 0)
            {
                try
                {
                    m_BuyStopOrder.Send(m_TargetPrice);
                }
                catch (Exception ex)
                {
                    Output.WriteLine("⚠️  ERROR sending buy order: " + ex.Message);
                }
            }

            // Handle sell stop order
            if (m_SellOrderActive && m_TargetPrice > 0)
            {
                try
                {
                    m_SellStopOrder.Send(m_TargetPrice);
                }
                catch (Exception ex)
                {
                    Output.WriteLine("⚠️  ERROR sending sell order: " + ex.Message);
                }
            }
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            // Debug: Log all mouse events
            Output.WriteLine("Mouse event: Button=" + arg.buttons + " Keys=" + arg.keys + " Price=" + arg.point.Price.ToString("F2"));

            // Shift+Left Click: Place entry order
            if (arg.buttons == MouseButtons.Left && (arg.keys & Keys.Shift) == Keys.Shift)
            {
                double clickPrice = arg.point.Price;
                double currentPrice = Bars.Close[0];

                // FIRST: Clear flags to stop any resubmission
                m_BuyOrderActive = false;
                m_SellOrderActive = false;
                m_TargetPrice = 0;
                
                // SECOND: Determine order direction and SEND ORDER IMMEDIATELY (no cancellation step)
                if (clickPrice > currentPrice)
                {
                    // Click above current price = Buy Stop-Limit
                    m_TargetPrice = clickPrice;
                    m_BuyOrderActive = true;

                    // SEND ORDER IMMEDIATELY - don't wait for CalcBar()
                    try
                    {
                        m_BuyStopOrder.Send(m_TargetPrice);
                        
                        Output.WriteLine("======================================");
                        Output.WriteLine("⚠️  LIVE BUY STOP ORDER PLACED ⚠️");
                        Output.WriteLine("Stop Price: " + m_TargetPrice.ToString("F2"));
                        Output.WriteLine("Current price: " + currentPrice.ToString("F2"));
                        Output.WriteLine("Symbol: " + Bars.Info.Name);
                        Output.WriteLine("✓ Order sent IMMEDIATELY from mouse handler");
                        Output.WriteLine("======================================");
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("⚠️  ERROR sending immediate buy order: " + ex.Message);
                    }
                }
                else if (clickPrice < currentPrice)
                {
                    // Click below current price = Sell Stop-Limit
                    m_TargetPrice = clickPrice;
                    m_SellOrderActive = true;

                    // SEND ORDER IMMEDIATELY - don't wait for CalcBar()
                    try
                    {
                        m_SellStopOrder.Send(m_TargetPrice);
                        
                        Output.WriteLine("======================================");
                        Output.WriteLine("⚠️  LIVE SELL STOP ORDER PLACED ⚠️");
                        Output.WriteLine("Stop Price: " + m_TargetPrice.ToString("F2"));
                        Output.WriteLine("Current price: " + currentPrice.ToString("F2"));
                        Output.WriteLine("Symbol: " + Bars.Info.Name);
                        Output.WriteLine("✓ Order sent IMMEDIATELY from mouse handler");
                        Output.WriteLine("======================================");
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("⚠️  ERROR sending immediate sell order: " + ex.Message);
                    }
                }
                else
                {
                    Output.WriteLine("⚠️  Cannot place order at current price. Click above for Buy or below for Sell.");
                }
            }
            
            // Ctrl+Left Click: Cancel all orders
            if (arg.buttons == MouseButtons.Left && (arg.keys & Keys.Control) == Keys.Control)
            {
                Output.WriteLine("======================================");
                Output.WriteLine("⚠️  CTRL+CLICK - CANCELLING ALL ORDERS ⚠️");
                Output.WriteLine("======================================");
                
                // FIRST: Clear flags to stop resubmitting
                m_BuyOrderActive = false;
                m_SellOrderActive = false;
                m_TargetPrice = 0;
                Output.WriteLine("✓ Stopped resubmitting orders");
                
                // SECOND: Cancel entry orders by sending with 0
                try
                {
                    m_BuyStopOrder.Send(0);
                    m_SellStopOrder.Send(0);
                    Output.WriteLine("✓ Entry orders cancelled");
                }
                catch (Exception ex)
                {
                    Output.WriteLine("⚠️  ERROR cancelling entry orders: " + ex.Message);
                }
                
                // THIRD: Close any open position with market order (immediate execution from mouse handler)
                if (StrategyInfo.MarketPosition != 0)
                {
                    try
                    {
                        // Send market order immediately - don't wait for CalcBar()
                        m_EmergencyExit.Send();
                        Output.WriteLine("⚠️  EMERGENCY: Closing position at market!");
                        Output.WriteLine("  Position: " + StrategyInfo.MarketPosition + " contracts");
                        Output.WriteLine("  Order sent immediately from mouse handler");
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("⚠️  ERROR closing position: " + ex.Message);
                    }
                }
                
                Output.WriteLine("✓ All pending orders cancelled and position closed");
            }
        }
    }
}
