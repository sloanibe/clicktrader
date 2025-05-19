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
        [Input] public double TickOffset { get; set; } // Number of ticks to offset the target price
        
        // No order objects - visualization only mode
        
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
            TickOffset = 15;  // Default to 15 ticks offset
        }

        protected override void Create()
        {
            base.Create();

            // Visualization-only mode - no order objects created
            Output.WriteLine("VISUALIZATION MODE: No orders will be created or executed");
            Output.WriteLine("This mode only shows target prices on the chart");
            
            // Strategy is ready
            Output.WriteLine("Strategy initialized in visualization-only mode");
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
            StrategyInfo.SetPlotValue(2, 1); // Signal 2 = clear lines
            
            Output.WriteLine("All state reset");
        }
        
        // Tell the indicator to draw a line at the specified price
        private void UpdateIndicatorLine(double price)
        {
            if (price > 0)
            {
                // First, clear any existing lines in the indicator by sending signal 2
                StrategyInfo.SetPlotValue(2, 1);
                
                // Force reset the plot value to 0 first to ensure the indicator sees the change
                StrategyInfo.SetPlotValue(1, 0);
                
                // Now set the actual target price
                StrategyInfo.SetPlotValue(1, price); // Signal 1 = draw line at price
                Output.WriteLine("TARGET LINE: Drawing at price " + price);
                
                // Reset the clear signal
                StrategyInfo.SetPlotValue(2, 0);
            }
        }

        // Main calculation method - runs on each tick in IOG mode
        protected override void CalcBar()
        {
            // Check for signals from the indicator
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
                    Output.WriteLine("TARGET ADJUSTED: Buy target at " + adjustPrice);
                    UpdateIndicatorLine(m_TargetBuyPrice);
                }
                else if (m_MonitoringSellPrice)
                {
                    m_TargetSellPrice = adjustPrice;
                    Output.WriteLine("TARGET ADJUSTED: Sell target at " + adjustPrice);
                    UpdateIndicatorLine(m_TargetSellPrice);
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
                
                // Update the indicator with the new target price
                UpdateIndicatorLine(m_TargetBuyPrice);
                
                Output.WriteLine("TARGET SET: Buy target at " + m_TargetBuyPrice);
                
                // Reset the signal
                StrategyInfo.SetPlotValue(3, 0);
            }
            
            // Process cancel request
            if (m_CancelOrder)
            {
                // Reset the monitoring state
                Output.WriteLine("CANCELING: Target price monitoring");
                ResetAllState();
                
                // Reset cancel flag
                m_CancelOrder = false;
            }
            
            // In visualization-only mode, we just monitor price but don't execute orders
            if (m_MonitoringBuyPrice)
            {
                // Calculate the value of 2 ticks in price points
                double twoTicksInPrice = 2 * Bars.Info.MinMove / Bars.Info.PriceScale;
                
                // Check if price has reached or exceeded target price
                bool priceConditionMet = (Bars.Close[0] >= m_TargetBuyPrice);
                
                // Log when price hits target (occasionally to avoid flooding output)
                if (priceConditionMet && Bars.CurrentBar % 10 == 0)
                {
                    Output.WriteLine("PRICE ALERT: Current price (" + Bars.Close[0] + ") has reached buy target (" + m_TargetBuyPrice + ")");
                }
            }
            
            // Monitor for target sell price
            if (m_MonitoringSellPrice)
            {
                // Calculate the value of 2 ticks in price points
                double twoTicksInPrice = 2 * Bars.Info.MinMove / Bars.Info.PriceScale;
                
                // Check if price has reached or fallen below target price
                bool priceConditionMet = (Bars.Close[0] <= m_TargetSellPrice);
                
                // Log when price hits target (occasionally to avoid flooding output)
                if (priceConditionMet && Bars.CurrentBar % 10 == 0)
                {
                    Output.WriteLine("PRICE ALERT: Current price (" + Bars.Close[0] + ") has reached sell target (" + m_TargetSellPrice + ")");
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
                double offsetInPrice = TickOffset * Bars.Info.MinMove / Bars.Info.PriceScale;
                m_TargetBuyPrice = clickPrice + offsetInPrice;
                
                // Update the indicator with the target price
                UpdateIndicatorLine(m_TargetBuyPrice);
                
                // Set the monitoring flags
                m_MonitoringBuyPrice = true;
                m_MonitoringSellPrice = false;
                
                Output.WriteLine("TARGET SET: Buy target at " + m_TargetBuyPrice);
            }
            
            // CTRL+LEFT CLICK: Set target sell price with offset below click price
            if (arg.buttons == MouseButtons.Left && (arg.keys & Keys.Control) == Keys.Control)
            {
                // Get the price at the click position
                double clickPrice = arg.point.Price;
                
                // Calculate target price using the configurable tick offset
                double offsetInPrice = TickOffset * Bars.Info.MinMove / Bars.Info.PriceScale;
                m_TargetSellPrice = clickPrice - offsetInPrice;
                
                // Update the indicator with the target price
                UpdateIndicatorLine(m_TargetSellPrice);
                
                // Set the monitoring flags
                m_MonitoringSellPrice = true;
                m_MonitoringBuyPrice = false;
                
                Output.WriteLine("TARGET SET: Sell target at " + m_TargetSellPrice);
            }
            
            // RIGHT CLICK: Cancel target price monitoring
            if (arg.buttons == MouseButtons.Right)
            {
                m_CancelOrder = true;
                Output.WriteLine("CANCEL REQUESTED: Will clear target price");
            }
            
            // CTRL+RIGHT CLICK: Reset all state
            if (arg.buttons == MouseButtons.Right && (arg.keys & Keys.Control) == Keys.Control)
            {
                ResetAllState();
                Output.WriteLine("MANUAL RESET: All state variables reset");
            }
        }
    }
}
