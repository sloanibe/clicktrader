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
    public class clicktrader_debug : SignalObject
    {
        // Input variables
        [Input] public int TicksDistance { get; set; }
        [Input] public int OrderQty { get; set; }
        [Input] public bool Development { get; set; }

        // State variables
        private double m_ClickPrice = 0;
        private bool m_ActiveStopLimitOrder = false;
        private double m_StopPrice = 0;
        private double m_LimitPrice = 0;
        private bool m_OrderCreatedInMouseEvent = false;
        private bool m_Debug = false;

        // Order objects with different naming conventions
        private IOrderStopLimit m_StopLimitBuy;
        private IOrderStopLimit m_StopLimitSell1; // Original naming
        private IOrderStopLimit m_StopLimitSell2; // "ExitSell" naming

        public clicktrader_debug(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            TicksDistance = 15;
            OrderQty = 1;
            Development = true; // Enable debug mode by default for this test
        }

        protected override void Create()
        {
            base.Create();

            // Create stop limit buy order with basic parameters
            m_StopLimitBuy = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "StopLimitBuy", EOrderAction.Buy));
                
            // Create sell stop limit order with original naming
            m_StopLimitSell1 = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "StopLimitSell", EOrderAction.Sell));
                
            // Create sell stop limit order with "exit" naming
            m_StopLimitSell2 = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "ExitSell", EOrderAction.Sell));

            // Set debug flag based on development mode
            m_Debug = Development;

            // Log initialization information
            StringBuilder envInfo = new StringBuilder();
            envInfo.AppendLine("*** DEBUG CLICKTRADER INITIALIZED ***");
            envInfo.AppendLine("Use Shift+Click to place a stop limit BUY order");
            envInfo.AppendLine("Use Ctrl+Click to place a stop limit SELL order (original naming)");
            envInfo.AppendLine("Use Alt+Click to place a stop limit SELL order (exit naming)");
            envInfo.AppendLine("Ticks Distance: " + TicksDistance);
            envInfo.AppendLine("Development Mode: " + (Development ? "ON" : "OFF"));
            
            try {
                envInfo.AppendLine("Auto Trading Mode: " + (Environment.IsAutoTradingMode ? "ON" : "OFF"));
            } catch {}

            Output.WriteLine(envInfo.ToString());
            
            // Log detailed information about the strategy
            Output.WriteLine("Strategy Name: " + this.GetType().Name);
            Output.WriteLine("Strategy ID: " + this.GetHashCode());
            
            // Log information about the order objects
            Output.WriteLine("Buy Order Object: " + m_StopLimitBuy.GetType().Name);
            Output.WriteLine("Sell Order Object 1: " + m_StopLimitSell1.GetType().Name);
            Output.WriteLine("Sell Order Object 2: " + m_StopLimitSell2.GetType().Name);
        }

        // Order type flag
        private int m_OrderType = 0; // 0 = Buy, 1 = Sell1, 2 = Sell2

        protected override void CalcBar()
        {
            try
            {
                // Always log market position for debugging
                if (Bars.LastBarOnChart)
                {
                    Output.WriteLine("DEBUG: CalcBar - Market Position: " + StrategyInfo.MarketPosition + 
                                  ", Broker Position: " + StrategyInfo.MarketPositionAtBroker +
                                  ", Active Order: " + m_ActiveStopLimitOrder);
                }

                // Process new orders that were created in OnMouseEvent
                if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0 && StrategyInfo.MarketPosition == 0)
                {
                    try
                    {
                        // Cancel any existing orders first
                        if (m_ActiveStopLimitOrder)
                        {
                            Output.WriteLine("Canceling existing order before placing new one");
                            m_ActiveStopLimitOrder = false;
                        }

                        // Send the appropriate stop limit order with stop and limit prices
                        if (m_OrderType == 0) // Buy order
                        {
                            m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
                            Output.WriteLine("BUY Order sent: Stop @ " + m_StopPrice + ", Limit @ " + m_LimitPrice);
                        }
                        else if (m_OrderType == 1) // Sell order with original naming
                        {
                            m_StopLimitSell1.Send(m_StopPrice, m_LimitPrice, OrderQty);
                            Output.WriteLine("SELL Order (original) sent: Stop @ " + m_StopPrice + ", Limit @ " + m_LimitPrice);
                        }
                        else if (m_OrderType == 2) // Sell order with exit naming
                        {
                            m_StopLimitSell2.Send(m_StopPrice, m_LimitPrice, OrderQty);
                            Output.WriteLine("SELL Order (exit) sent: Stop @ " + m_StopPrice + ", Limit @ " + m_LimitPrice);
                        }
                        
                        // Set flags
                        m_ActiveStopLimitOrder = true;
                        
                        // Reset flags after order is placed
                        m_ClickPrice = 0;
                        m_OrderCreatedInMouseEvent = false;
                        
                        // Log additional information for debugging
                        Output.WriteLine("Order sent with type: " + m_OrderType);
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("Error processing new order: " + ex.Message + "\nStack Trace: " + ex.StackTrace);
                    }
                }
                
                // If we have an active order and we're still flat, keep it alive
                else if (m_ActiveStopLimitOrder && StrategyInfo.MarketPosition == 0)
                {
                    try
                    {
                        // Following the example document's approach - resubmit the order each bar
                        if (m_OrderType == 0) // Buy order
                        {
                            m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
                            
                            if (m_Debug && Bars.LastBarOnChart)
                            {
                                Output.WriteLine("CalcBar: Resubmitting BUY stop limit order");
                            }
                        }
                        else if (m_OrderType == 1) // Sell order with original naming
                        {
                            m_StopLimitSell1.Send(m_StopPrice, m_LimitPrice, OrderQty);
                            
                            if (m_Debug && Bars.LastBarOnChart)
                            {
                                Output.WriteLine("CalcBar: Resubmitting SELL stop limit order (original)");
                            }
                        }
                        else if (m_OrderType == 2) // Sell order with exit naming
                        {
                            m_StopLimitSell2.Send(m_StopPrice, m_LimitPrice, OrderQty);
                            
                            if (m_Debug && Bars.LastBarOnChart)
                            {
                                Output.WriteLine("CalcBar: Resubmitting SELL stop limit order (exit)");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("Error resubmitting order: " + ex.Message + "\nStack Trace: " + ex.StackTrace);
                    }
                }
                
                // If we have a position, stop resubmitting the order
                else if (StrategyInfo.MarketPosition != 0 && m_ActiveStopLimitOrder)
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
                // Only process left mouse clicks with modifier keys
                if (arg.buttons != MouseButtons.Left)
                    return;

                // Get the current click price and tick size
                double currentClickPrice = arg.point.Price;
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                
                // Log all mouse events for debugging
                Output.WriteLine("DEBUG: Mouse event - Key: " + arg.keys + ", Price: " + currentClickPrice);
                
                // Handle Shift+Click for buy orders
                if (arg.keys == Keys.Shift)
                {
                    m_OrderType = 0; // Buy order
                    
                    // Calculate stop and limit prices for buy order
                    m_StopPrice = currentClickPrice + (TicksDistance * tickSize);
                    m_LimitPrice = m_StopPrice + tickSize;
                    
                    // Store the click price for CalcBar to handle
                    m_ClickPrice = currentClickPrice;
                    
                    // Flag for new order creation
                    m_OrderCreatedInMouseEvent = true;
                    
                    Output.WriteLine("PLACING ORDER: Stop Limit BUY at stop price " + m_StopPrice +
                                  " (" + TicksDistance + " ticks above " + currentClickPrice + ")");
                }
                // Handle Ctrl+Click for sell orders with original naming
                else if (arg.keys == Keys.Control)
                {
                    m_OrderType = 1; // Sell order with original naming
                    
                    // Calculate stop and limit prices for sell order
                    m_StopPrice = currentClickPrice - (TicksDistance * tickSize);
                    m_LimitPrice = m_StopPrice - tickSize;
                    
                    // Store the click price for CalcBar to handle
                    m_ClickPrice = currentClickPrice;
                    
                    // Flag for new order creation
                    m_OrderCreatedInMouseEvent = true;
                    
                    Output.WriteLine("PLACING ORDER: Stop Limit SELL (original) at stop price " + m_StopPrice +
                                  " (" + TicksDistance + " ticks below " + currentClickPrice + ")");
                }
                // Handle Alt+Click for sell orders with exit naming
                else if (arg.keys == Keys.Alt)
                {
                    m_OrderType = 2; // Sell order with exit naming
                    
                    // Calculate stop and limit prices for sell order
                    m_StopPrice = currentClickPrice - (TicksDistance * tickSize);
                    m_LimitPrice = m_StopPrice - tickSize;
                    
                    // Store the click price for CalcBar to handle
                    m_ClickPrice = currentClickPrice;
                    
                    // Flag for new order creation
                    m_OrderCreatedInMouseEvent = true;
                    
                    Output.WriteLine("PLACING ORDER: Stop Limit SELL (exit) at stop price " + m_StopPrice +
                                  " (" + TicksDistance + " ticks below " + currentClickPrice + ")");
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error processing mouse event: " + ex.Message + "\nStack Trace: " + ex.StackTrace);
            }
        }
    }
}
