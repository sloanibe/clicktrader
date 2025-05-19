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

        // Main calculation method - minimal implementation
        protected override void CalcBar()
        {
            // Process cancel request if set
            if (m_CancelOrder)
            {
                ResetAllState();
                m_CancelOrder = false;
            }
            
            // Monitor price levels and update indicator if needed
            if (m_NeedToUpdateIndicator)
            {
                if (m_MonitoringBuyPrice)
                    UpdateIndicatorLine(m_TargetBuyPrice);
                else if (m_MonitoringSellPrice)
                    UpdateIndicatorLine(m_TargetSellPrice);
                    
                m_NeedToUpdateIndicator = false;
            }
            
            // Minimal price monitoring - just log alerts occasionally
            if (m_MonitoringBuyPrice && Bars.Close[0] >= m_TargetBuyPrice && Bars.CurrentBar % 20 == 0)
            {
                Output.WriteLine("PRICE ALERT: " + Bars.Close[0] + " reached buy target " + m_TargetBuyPrice);
            }
            else if (m_MonitoringSellPrice && Bars.Close[0] <= m_TargetSellPrice && Bars.CurrentBar % 20 == 0)
            {
                Output.WriteLine("PRICE ALERT: " + Bars.Close[0] + " reached sell target " + m_TargetSellPrice);
            }
            
            // No duplicate code needed here
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
                
                // Set flag to update indicator in CalcBar
                m_NeedToUpdateIndicator = true;
                
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
                
                // Set flag to update indicator in CalcBar
                m_NeedToUpdateIndicator = true;
                
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
