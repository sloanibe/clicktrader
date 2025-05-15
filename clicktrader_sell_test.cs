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
    public class clicktrader_sell_example : SignalObject
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

        // Order objects - following the example document pattern
        private IOrderMarket m_BuyMarketOrder;
        private IOrderStopLimit m_SellStopLimitOrder;

        public clicktrader_sell_example(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            TicksDistance = 15;
            OrderQty = 1;
            Development = false; // Disable debug mode by default
        }

        protected override void Create()
        {
            base.Create();

            // Create market buy order for entry - following the example document pattern
            m_BuyMarketOrder = OrderCreator.MarketNextBar(
                new SOrderParameters(Contracts.Default, "EntryBuy", EOrderAction.Buy));
                
            // Create stop limit sell order for exit - following the example document pattern
            m_SellStopLimitOrder = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "ExitSell", EOrderAction.Sell));

            // Set debug flag based on development mode
            m_Debug = Development;

            // Log initialization information
            StringBuilder envInfo = new StringBuilder();
            envInfo.AppendLine("*** SELL STOP LIMIT TEST INITIALIZED ***");
            envInfo.AppendLine("Use Shift+Click to place a market BUY order");
            envInfo.AppendLine("Use Ctrl+Click to place a stop limit SELL order");
            envInfo.AppendLine("Ticks Distance: " + TicksDistance);
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
                        // If we're flat and want to enter with a market buy
                        if (StrategyInfo.MarketPosition == 0 && m_IsBuyOrder)
                        {
                            // Send the market buy order
                            m_BuyMarketOrder.Send(OrderQty);
                            Output.WriteLine("BUY Market order sent: Quantity: " + OrderQty);
                        }
                        // If we have a long position and want to place a sell stop limit
                        else if (StrategyInfo.MarketPosition > 0 && !m_IsBuyOrder)
                        {
                            // Send the stop limit sell order with stop and limit prices
                            m_SellStopLimitOrder.Send(m_StopPrice, m_LimitPrice, OrderQty);
                            Output.WriteLine("SELL Stop Limit order sent: Stop @ " + m_StopPrice + ", Limit @ " + m_LimitPrice);
                            
                            // Set flags
                            m_ActiveStopLimitOrder = true;
                        }
                        else
                        {
                            Output.WriteLine("Order not sent: Invalid market position for the requested order type.");
                        }
                        
                        // Reset flags after order is processed
                        m_ClickPrice = 0;
                        m_OrderCreatedInMouseEvent = false;
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("Error processing new order: " + ex.Message + "\nStack Trace: " + ex.StackTrace);
                    }
                }
                
                // If we have an active sell stop limit order and we're still long, keep it alive
                else if (m_ActiveStopLimitOrder && StrategyInfo.MarketPosition > 0)
                {
                    try
                    {
                        // Following the example document's approach - resubmit the order each bar
                        m_SellStopLimitOrder.Send(m_StopPrice, m_LimitPrice, OrderQty);
                        
                        if (m_Debug && Bars.LastBarOnChart)
                        {
                            Output.WriteLine("CalcBar: Resubmitting SELL stop limit order to keep it active");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (m_Debug)
                        {
                            Output.WriteLine("Error resubmitting order: " + ex.Message);
                        }
                    }
                }
                
                // If we're flat but had an active sell stop limit order, clear the flag
                else if (StrategyInfo.MarketPosition == 0 && m_ActiveStopLimitOrder)
                {
                    Output.WriteLine("Position closed, stop limit order no longer needed");
                    m_ActiveStopLimitOrder = false;
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in CalcBar: " + ex.Message);
            }
        }
        
        // Flag to track if the order is buy or sell
        private bool m_IsBuyOrder = true;
        
        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            try
            {
                // Only process left mouse clicks with modifier keys
                if (arg.buttons != MouseButtons.Left || (arg.keys != Keys.Control && arg.keys != Keys.Shift))
                    return;

                // Minimal debug logging
                if (m_Debug)
                {
                    Output.WriteLine("DEBUG: Mouse event - Key: " + arg.keys + ", Price: " + arg.point.Price);
                }

                // Get the current click price and tick size
                double currentClickPrice = arg.point.Price;
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                
                // Handle Shift+Click for market buy orders
                if (arg.keys == Keys.Shift)
                {
                    m_IsBuyOrder = true;
                    
                    // Store the click price for CalcBar to handle
                    m_ClickPrice = currentClickPrice;
                    
                    // Flag for new order creation
                    m_OrderCreatedInMouseEvent = true;
                    
                    Output.WriteLine("PLACING ORDER: Market BUY at current market price");
                    Output.WriteLine("OnMouseEvent: Setting up BUY order for processing in CalcBar");
                }
                // Handle Ctrl+Click for sell stop limit orders
                else if (arg.keys == Keys.Control)
                {
                    // Only allow sell stop limit orders if we have a long position
                    if (StrategyInfo.MarketPosition <= 0)
                    {
                        Output.WriteLine("Cannot place sell stop limit order: No long position exists");
                        return;
                    }
                    
                    m_IsBuyOrder = false;
                    
                    // Calculate stop and limit prices for sell order
                    // For sell orders, stop price is below the click price
                    m_StopPrice = currentClickPrice - (TicksDistance * tickSize);
                    // Set limit price 1 tick below stop price for a reasonable fill
                    m_LimitPrice = m_StopPrice - tickSize;
                    
                    // Store the click price for CalcBar to handle
                    m_ClickPrice = currentClickPrice;
                    
                    // Flag for new order creation
                    m_OrderCreatedInMouseEvent = true;
                    
                    Output.WriteLine("PLACING ORDER: Stop Limit SELL at stop price " + m_StopPrice +
                                  " (" + TicksDistance + " ticks below " + currentClickPrice + ")");
                    Output.WriteLine("OnMouseEvent: Setting up SELL order for processing in CalcBar");
                    
                    // If we have an active order, log that we'll be canceling it
                    if (m_ActiveStopLimitOrder)
                    {
                        Output.WriteLine("OnMouseEvent: Existing order will be canceled before creating a new one");
                    }
                    
                    Output.WriteLine("OnMouseEvent: New SELL order will be created in next CalcBar. Stop Price: " + m_StopPrice +
                                  ", Limit Price: " + m_LimitPrice + ", Quantity: " + OrderQty);
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error processing mouse event: " + ex.Message);
            }
        }
    }
}
