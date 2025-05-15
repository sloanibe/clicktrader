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
    public class clicktrader_both : SignalObject
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
        private bool m_IsBuyOrder = true; // Flag to track if the order is buy or sell
        private bool m_Debug = false;

        // Order objects
        private IOrderStopLimit m_StopLimitBuy;
        private IOrderStopLimit m_StopLimitSell;

        public clicktrader_both(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            TicksDistance = 15;
            OrderQty = 1;
            Development = false; // Disable debug mode by default
        }

        protected override void Create()
        {
            base.Create();

            // Create both buy and sell stop limit order objects
            m_StopLimitBuy = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "StopLimitBuy", EOrderAction.Buy));
                
            m_StopLimitSell = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "StopLimitSell", EOrderAction.Sell));

            // Set debug flag based on development mode
            m_Debug = Development;

            // Log initialization information
            StringBuilder envInfo = new StringBuilder();
            envInfo.AppendLine("*** DUAL CLICKTRADER INITIALIZED ***");
            envInfo.AppendLine("Use Shift+Click to place a stop limit BUY order");
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
                if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0 && StrategyInfo.MarketPosition == 0)
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
                        }

                        // Send the appropriate stop limit order with stop and limit prices
                        if (m_IsBuyOrder)
                        {
                            m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
                            Output.WriteLine("BUY Order sent: Stop @ " + m_StopPrice + ", Limit @ " + m_LimitPrice);
                        }
                        else
                        {
                            // For sell orders, ensure stop price > limit price
                            m_StopLimitSell.Send(m_StopPrice, m_LimitPrice, OrderQty);
                            Output.WriteLine("SELL Order sent: Stop @ " + m_StopPrice + ", Limit @ " + m_LimitPrice);
                        }
                        
                        // Set flags
                        m_ActiveStopLimitOrder = true;
                        
                        // Reset flags after order is placed
                        m_ClickPrice = 0;
                        m_OrderCreatedInMouseEvent = false;
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
                        if (m_IsBuyOrder)
                        {
                            m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
                            if (m_Debug && Bars.LastBarOnChart)
                            {
                                Output.WriteLine("CalcBar: Resubmitting BUY stop limit order");
                            }
                        }
                        else
                        {
                            m_StopLimitSell.Send(m_StopPrice, m_LimitPrice, OrderQty);
                            if (m_Debug && Bars.LastBarOnChart)
                            {
                                Output.WriteLine("CalcBar: Resubmitting SELL stop limit order");
                            }
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
                
                // If we have a position, stop resubmitting the order
                else if (StrategyInfo.MarketPosition != 0 && m_ActiveStopLimitOrder)
                {
                    Output.WriteLine("Position opened, stop limit order no longer needed");
                    m_ActiveStopLimitOrder = false;
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in CalcBar: " + ex.Message);
            }
        }
        
        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            try
            {
                // Only process left mouse clicks
                if (arg.buttons != MouseButtons.Left)
                    return;

                // Minimal debug logging
                if (m_Debug)
                {
                    Output.WriteLine("DEBUG: Mouse event - Key: " + arg.keys + ", Price: " + arg.point.Price);
                }

                // Get the current click price and tick size
                double currentClickPrice = arg.point.Price;
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                
                // Handle Shift+Click for buy orders
                if (arg.keys == Keys.Shift)
                {
                    m_IsBuyOrder = true;
                    
                    // Calculate stop and limit prices for buy order
                    // For buy orders, stop price is above the click price
                    m_StopPrice = currentClickPrice + (TicksDistance * tickSize);
                    // Set limit price 1 tick above stop price for a reasonable fill
                    m_LimitPrice = m_StopPrice + tickSize;
                    
                    // Store the click price for CalcBar to handle
                    m_ClickPrice = currentClickPrice;
                    
                    // Flag for new order creation
                    m_OrderCreatedInMouseEvent = true;
                    
                    Output.WriteLine("PLACING ORDER: Stop Limit BUY at stop price " + m_StopPrice +
                                  " (" + TicksDistance + " ticks above " + currentClickPrice + ")");
                    Output.WriteLine("OnMouseEvent: Setting up BUY order for processing in CalcBar");
                    
                    return;
                }
                
                // Handle Ctrl+Click for sell orders
                if (arg.keys == Keys.Control)
                {
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
                    
                    return;
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error processing mouse event: " + ex.Message);
            }
        }
    }
}
