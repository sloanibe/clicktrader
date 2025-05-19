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
        [Input] public int OrderQty { get; set; } // Quantity of contracts to trade
        [Input] public bool UseIndicatorForVisualization { get; set; } // Whether to use companion indicator
        [Input] public double TickOffset { get; set; } // Number of ticks to offset the target price
        
        // Order objects - created in Create() method
        private IOrderMarket m_BuyMarketOrder;
        private IOrderMarket m_SellMarketOrder;
        private IOrderMarket m_ExitLongOrder;
        private IOrderMarket m_ExitShortOrder;
        
        // Price monitoring variables
        private double m_TargetBuyPrice = 0;
        private double m_TargetSellPrice = 0;
        private bool m_MonitoringBuyPrice = false;
        private bool m_MonitoringSellPrice = false;
        private bool m_CancelOrder = false;
        private bool m_OrderFilled = false;
        private int m_LastKnownPosition = 0;
        private bool m_FirstTickAfterOrder = false;
        private bool m_NeedToUpdateIndicator = false;
        
        public clicktrader(object ctx) : base(ctx)
        {
            // Default quantity
            OrderQty = 1;
            UseIndicatorForVisualization = true;
            TickOffset = 15;  // Default to 15 ticks offset
        }

        protected override void Create()
        {
            base.Create();

            // Create order objects with explicit broker routing
            // Use MarketNextBar instead of MarketThisBar to ensure orders are sent to broker
            m_BuyMarketOrder = OrderCreator.MarketNextBar(
                new SOrderParameters(Contracts.Default, "BuyMarket", EOrderAction.Buy));
                
            m_SellMarketOrder = OrderCreator.MarketNextBar(
                new SOrderParameters(Contracts.Default, "SellMarket", EOrderAction.Sell));
                
            m_ExitLongOrder = OrderCreator.MarketNextBar(
                new SOrderParameters(Contracts.Default, "ExitLong", EOrderAction.Sell));
                
            m_ExitShortOrder = OrderCreator.MarketNextBar(
                new SOrderParameters(Contracts.Default, "ExitShort", EOrderAction.Buy));
                
            Output.WriteLine("ORDER OBJECTS: Created with broker routing enabled");

            // Strategy is ready
            Output.WriteLine("TRADING MODE: Strategy initialized");
            
            Output.WriteLine("Strategy created - ready for trading");
            Output.WriteLine("Make sure to add the pricemonitor indicator to visualize target prices");
        }

        // Reset all state variables to start fresh
        private void ResetAllState()
        {
            m_MonitoringBuyPrice = false;
            m_MonitoringSellPrice = false;
            m_TargetBuyPrice = 0;
            m_TargetSellPrice = 0;
            m_OrderFilled = false;
            m_CancelOrder = false;
            m_FirstTickAfterOrder = false;
            
            // Signal to the indicator to clear all lines
            if (UseIndicatorForVisualization)
            {
                StrategyInfo.SetPlotValue(2, 1); // Signal 2 = clear lines
            }
            
            Output.WriteLine("All state reset");
        }
        
        // Tell the indicator to draw a line at the specified price
        private void UpdateIndicatorLine(double price)
        {
            if (UseIndicatorForVisualization && price > 0)
            {
                // First, clear any existing lines in the indicator by sending signal 2
                StrategyInfo.SetPlotValue(2, 1);
                
                // Force reset the plot value to 0 first to ensure the indicator sees the change
                StrategyInfo.SetPlotValue(1, 0);
                
                // Now set the actual target price
                StrategyInfo.SetPlotValue(1, price); // Signal 1 = draw line at price
                Output.WriteLine("STRATEGY: Sent target price " + price + " to indicator for visualization");
                
                // Reset the clear signal
                StrategyInfo.SetPlotValue(2, 0);
            }
        }

        // Main calculation method - runs on each tick in IOG mode
        protected override void CalcBar()
        {
            
            // Check for signals from the indicator
            if (UseIndicatorForVisualization)
            {
                // Check for cancel signal from indicator (Alt+Click)
                double cancelSignal = StrategyInfo.GetPlotValue(2);
                if (cancelSignal > 0)
                {
                    m_CancelOrder = true;
                    StrategyInfo.SetPlotValue(2, 0); // Reset the signal
                }
                
                // Check for line adjustment from indicator (Shift+Click)
                double adjustPrice = StrategyInfo.GetPlotValue(4);
                if (adjustPrice > 0)
                {
                    // Update the target price directly (no offset)
                    if (m_MonitoringBuyPrice)
                    {
                        m_TargetBuyPrice = adjustPrice;
                        Output.WriteLine("TARGET ADJUSTED: Buy market order will now trigger at " + adjustPrice);
                    }
                    else if (m_MonitoringSellPrice)
                    {
                        m_TargetSellPrice = adjustPrice;
                        Output.WriteLine("TARGET ADJUSTED: Sell market order will now trigger at " + adjustPrice);
                    }
                    
                    // Reset the signal
                    StrategyInfo.SetPlotValue(4, 0);
                }
                
                // Check for click price from indicator (Ctrl+Click)
                double clickPrice = StrategyInfo.GetPlotValue(3);
                if (clickPrice > 0)
                {
                    // Set target buy price using the configurable tick offset
                    double offsetInPrice = TickOffset * Bars.Info.MinMove / Bars.Info.PriceScale;
                    m_TargetBuyPrice = clickPrice + offsetInPrice;
                    m_MonitoringBuyPrice = true;
                    m_MonitoringSellPrice = false;
                    m_OrderFilled = false;
                    m_FirstTickAfterOrder = false;
                    
                    // Update the indicator with the new target price
                    UpdateIndicatorLine(m_TargetBuyPrice);
                    
                    Output.WriteLine("TARGET SET: Buy market order will trigger at " + m_TargetBuyPrice);
                    
                    // Reset the signal
                    StrategyInfo.SetPlotValue(3, 0);
                }
            }
            
            // Check if position was closed externally
            if (StrategyInfo.MarketPosition == 0 && m_LastKnownPosition != 0)
            {
                ResetAllState();
            }
            
            // Update last known position
            m_LastKnownPosition = StrategyInfo.MarketPosition;
            
            // Process cancel request
            if (m_CancelOrder)
            {
                // Close any open positions
                if (StrategyInfo.MarketPosition > 0)
                {
                    m_ExitLongOrder.Send(StrategyInfo.MarketPosition);
                    Output.WriteLine("CLOSING: Long position with market order");
                }
                else if (StrategyInfo.MarketPosition < 0)
                {
                    m_ExitShortOrder.Send(Math.Abs(StrategyInfo.MarketPosition));
                    Output.WriteLine("CLOSING: Short position with market order");
                }
                
                // Reset monitoring state
                ResetAllState();
                
                // Reset cancel flag
                m_CancelOrder = false;
            }
            
            // Skip processing on the first tick after order submission when the bar is still forming
            // This prevents order cancellation issues with Renko bars
            if (Bars.Status == EBarState.Inside && m_FirstTickAfterOrder)
            {
                return;
            }
            
            // Monitor for target buy price
            if (m_MonitoringBuyPrice && !m_OrderFilled)
            {
                // Calculate the value of 2 ticks in price points
                double twoTicksInPrice = 2 * Bars.Info.MinMove / Bars.Info.PriceScale;
                
                // Check if current price or high of the bar has reached or exceeded target price
                // This ensures we don't miss price movements that passed through our target
                bool priceConditionMet = (Bars.Close[0] >= m_TargetBuyPrice || // Current close price
                                        Bars.High[0] >= m_TargetBuyPrice || // High of current bar
                                        Math.Abs(Bars.Close[0] - m_TargetBuyPrice) < 0.0001) && // Small tolerance
                                        (Bars.Close[0] <= m_TargetBuyPrice + twoTicksInPrice); // Not more than 2 ticks above
                
                // Log the price check for debugging
                if (Bars.Close[0] > m_TargetBuyPrice + twoTicksInPrice)
                {
                    Output.WriteLine("ORDER SKIPPED: Current price (" + Bars.Close[0] + ") is more than 2 ticks above target price (" + m_TargetBuyPrice + ")");
                }
                
                if (priceConditionMet)
                {
                    Output.WriteLine("ATTEMPTING ORDER: Sending buy market order at " + Bars.Close[0]);
                    
                    try
                    {
                        // Submit market order
                        m_BuyMarketOrder.Send(OrderQty);
                        Output.WriteLine("ORDER EXECUTED: Buy market order at " + Bars.Close[0]);
                        Output.WriteLine("POSITION STATUS: Current position = " + StrategyInfo.MarketPosition);
                        
                        // Update state
                        m_OrderFilled = true;
                        m_FirstTickAfterOrder = true;
                        m_MonitoringBuyPrice = false;
                        
                        // Tell indicator to clear the line
                        if (UseIndicatorForVisualization)
                        {
                            StrategyInfo.SetPlotValue(2, 1); // Signal to clear lines
                        }
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("ERROR SENDING ORDER: " + ex.Message);
                    }
                }
            }
            
            // Monitor for target sell price
            if (m_MonitoringSellPrice && !m_OrderFilled)
            {
                // Calculate the value of 2 ticks in price points
                double twoTicksInPrice = 2 * Bars.Info.MinMove / Bars.Info.PriceScale;
                
                // Check if current price or low of the bar has reached or gone below target price
                // This ensures we don't miss price movements that passed through our target
                bool priceConditionMet = (Bars.Close[0] <= m_TargetSellPrice || // Current close price
                                        Bars.Low[0] <= m_TargetSellPrice || // Low of current bar
                                        Math.Abs(Bars.Close[0] - m_TargetSellPrice) < 0.0001) && // Small tolerance
                                        (Bars.Close[0] >= m_TargetSellPrice - twoTicksInPrice); // Not more than 2 ticks below
                
                // Log the price check for debugging
                if (Bars.Close[0] < m_TargetSellPrice - twoTicksInPrice)
                {
                    Output.WriteLine("ORDER SKIPPED: Current price (" + Bars.Close[0] + ") is more than 2 ticks below target price (" + m_TargetSellPrice + ")");
                }
                
                if (priceConditionMet)
                {
                    Output.WriteLine("ATTEMPTING ORDER: Sending sell market order at " + Bars.Close[0]);
                    
                    try
                    {
                        // Submit market order
                        m_SellMarketOrder.Send(OrderQty);
                        Output.WriteLine("ORDER EXECUTED: Sell market order at " + Bars.Close[0]);
                        Output.WriteLine("POSITION STATUS: Current position = " + StrategyInfo.MarketPosition);
                        
                        // Update state
                        m_OrderFilled = true;
                        m_FirstTickAfterOrder = true;
                        m_MonitoringSellPrice = false;
                        
                        // Tell indicator to clear the line
                        if (UseIndicatorForVisualization)
                        {
                            StrategyInfo.SetPlotValue(2, 1); // Signal to clear lines
                        }
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("ERROR SENDING ORDER: " + ex.Message);
                    }
                }
            }
            
            // If we need to update the indicator with new target prices
            if (m_NeedToUpdateIndicator)
            {
                if (m_MonitoringBuyPrice)
                {
                    UpdateIndicatorLine(m_TargetBuyPrice);
                }
                else if (m_MonitoringSellPrice)
                {
                    UpdateIndicatorLine(m_TargetSellPrice);
                }
                m_NeedToUpdateIndicator = false;
            }
        }

        // Mouse event handler - only sets flags and target prices
        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            // SHIFT+LEFT CLICK: Set target buy price with offset above click price
            if (arg.buttons == MouseButtons.Left && (arg.keys & Keys.Shift) == Keys.Shift)
            {
                // Get the price at the click position
                double clickPrice = arg.point.Price;
                
                // Calculate target price using the configurable tick offset
                // Convert ticks to price points using MinMove and PriceScale
                double offsetInPrice = TickOffset * Bars.Info.MinMove / Bars.Info.PriceScale;
                m_TargetBuyPrice = clickPrice + offsetInPrice;
                
                // First update the indicator with the target price
                // This ensures the line appears at the correct offset price
                if (UseIndicatorForVisualization)
                {
                    UpdateIndicatorLine(m_TargetBuyPrice);
                }
                
                // Now set the monitoring flags after the indicator is updated
                m_MonitoringBuyPrice = true;
                m_MonitoringSellPrice = false;
                m_OrderFilled = false;
                m_FirstTickAfterOrder = false;
                
                Output.WriteLine("TARGET SET: Buy market order will trigger at " + m_TargetBuyPrice);
            }
            
            // CTRL+LEFT CLICK: Set target sell price with offset below click price
            if (arg.buttons == MouseButtons.Left && (arg.keys & Keys.Control) == Keys.Control)
            {
                // Get the price at the click position
                double clickPrice = arg.point.Price;
                
                // Calculate target price using the configurable tick offset
                // Convert ticks to price points using MinMove and PriceScale
                double offsetInPrice = TickOffset * Bars.Info.MinMove / Bars.Info.PriceScale;
                m_TargetSellPrice = clickPrice - offsetInPrice;
                
                // First update the indicator with the target price
                // This ensures the line appears at the correct offset price
                if (UseIndicatorForVisualization)
                {
                    UpdateIndicatorLine(m_TargetSellPrice);
                }
                
                // Now set the monitoring flags after the indicator is updated
                m_MonitoringSellPrice = true;
                m_MonitoringBuyPrice = false;
                m_OrderFilled = false;
                m_FirstTickAfterOrder = false;
                
                Output.WriteLine("TARGET SET: Sell market order will trigger at " + m_TargetSellPrice);
            }
            
            // RIGHT CLICK: Cancel order monitoring or close position
            if (arg.buttons == MouseButtons.Right)
            {
                m_CancelOrder = true;
                Output.WriteLine("CANCEL REQUESTED: Will process in CalcBar");
            }
            
            // CTRL+RIGHT CLICK: Reset all state (for when positions are closed externally)
            if (arg.buttons == MouseButtons.Right && (arg.keys & Keys.Control) == Keys.Control)
            {
                ResetAllState();
                Output.WriteLine("MANUAL RESET: All state variables reset");
            }
        }
    }
}
