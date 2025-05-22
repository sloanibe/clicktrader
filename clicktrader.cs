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
        [Input] public int ProtectiveStopTicks { get; set; } // Number of ticks for protective stop

        // Order objects for trade execution
        private IOrderStopLimit m_BuyStopLimitOrder;
        private IOrderPriced m_BuyStopOrder;
        private IOrderMarket m_BuyMarketOrder;
        private IOrderStopLimit m_SellStopLimitOrder;
        private IOrderMarket m_ExitLongOrder;
        private IOrderMarket m_ExitShortOrder;
        private IOrderPriced m_ProtectiveStopShort; // Protective stop for short positions
        private IOrderPriced m_ProtectiveStopLong; // Protective stop for long positions

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
        private bool m_ClosePositionFlag = false;
        private bool m_WaitingForSellFill = false;
        private int m_SellOrderQty = 0;
        private double m_SellStopPrice = 0;
        private double m_SellLimitPrice = 0;
        private bool m_WaitingForBuyFill = false;
        private int m_BuyOrderQty = 0;
        private double m_BuyStopPrice = 0;
        private double m_BuyLimitPrice = 0;
        private double m_ProtectiveStopShortPrice = 0; // Price level for protective stop for short positions
        private bool m_ProtectiveStopShortActive = false; // Flag to track if we have a protective stop pending for short positions
        private double m_ProtectiveStopLongPrice = 0; // Price level for protective stop for long positions
        private bool m_ProtectiveStopLongActive = false; // Flag to track if we have a protective stop pending for long positions

        public clicktrader(object ctx) : base(ctx)
        {
            // Default quantity
            OrderQty = 1;
            TickOffset = 15;  // Default to 15 ticks offset
            ProtectiveStopTicks = 22; // Default to 22 ticks for protective stop
        }

        protected override void Create()
        {
            base.Create();

            // Create order objects for trade execution
            m_BuyStopLimitOrder = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "BuyStopLimit", EOrderAction.Buy));
                
            // Add Buy Stop order for faster execution
            m_BuyStopOrder = OrderCreator.Stop(
                new SOrderParameters(Contracts.Default, "BuyStop", EOrderAction.Buy));
                
            // Add Buy Market order for immediate execution
            m_BuyMarketOrder = OrderCreator.MarketNextBar(
                new SOrderParameters(Contracts.Default, "BuyMarket", EOrderAction.Buy));

            m_SellStopLimitOrder = OrderCreator.StopLimit(
                new SOrderParameters(Contracts.Default, "SellStopLimit", EOrderAction.SellShort));

            m_ExitLongOrder = OrderCreator.MarketNextBar(
                new SOrderParameters(Contracts.Default, "ExitLong", EOrderAction.Sell));

            m_ExitShortOrder = OrderCreator.MarketThisBar(
                new SOrderParameters(Contracts.Default, "ExitShort", EOrderAction.BuyToCover));
                
            m_ProtectiveStopShort = OrderCreator.Stop(
                new SOrderParameters(Contracts.Default, "ProtectiveStopShort", EOrderAction.BuyToCover));
                
            m_ProtectiveStopLong = OrderCreator.Stop(
                new SOrderParameters(Contracts.Default, "ProtectiveStopLong", EOrderAction.Sell));

            Output.WriteLine("ORDER OBJECTS: Created market orders for buy, sell, and position exits");

            // Initialize the strategy
            Output.WriteLine("ClickTrader Ready");
        }

        // Reset all state variables to start fresh
        private void ResetAllState()
        {
            // Set flag to close positions in CalcBar
            if (StrategyInfo.MarketPosition != 0)
            {
                m_ClosePositionFlag = true;
                Output.WriteLine("POSITION RESET: Will close position in CalcBar");
            }

            m_MonitoringBuyPrice = false;
            m_MonitoringSellPrice = false;
            m_TargetBuyPrice = 0;
            m_TargetSellPrice = 0;
            m_OrderFilled = false;
            m_CancelOrder = false;
            m_FirstTickAfterOrder = false;
            m_WaitingForSellFill = false; // Reset sell order tracking
            m_SellOrderQty = 0; // Clear sell order quantity
            m_SellStopPrice = 0; // Clear sell stop price
            m_SellLimitPrice = 0; // Clear sell limit price
            m_WaitingForBuyFill = false; // Reset buy order tracking
            m_BuyOrderQty = 0; // Clear buy order quantity
            m_BuyStopPrice = 0; // Clear buy stop price
            m_BuyLimitPrice = 0; // Clear buy limit price
            m_ProtectiveStopShortPrice = 0; // Clear protective stop price for short positions
            m_ProtectiveStopShortActive = false; // Reset protective stop flag for short positions
            m_ProtectiveStopLongPrice = 0; // Clear protective stop price for long positions
            m_ProtectiveStopLongActive = false; // Reset protective stop flag for long positions
            m_LastKnownPosition = 0; // Force position tracking reset

            // Signal to the indicator to clear all lines
            StrategyInfo.SetPlotValue(2, 1); // Signal 2 = clear lines
            
            // Note: We don't need to explicitly cancel orders in MultiCharts .NET
            // Orders are automatically canceled when new ones are submitted

            Output.WriteLine("All state reset");
        }

        // Synchronize the strategy's position tracking with the actual broker position
        private void SynchronizePositionTracking()
        {
            // Get the current position from StrategyInfo
            int currentPosition = StrategyInfo.MarketPosition;

            // If our tracking is different, update it
            if (m_LastKnownPosition != currentPosition)
            {
                Output.WriteLine("POSITION SYNC: Position changed from " + m_LastKnownPosition + " to " + currentPosition);
                m_LastKnownPosition = currentPosition;
            }
        }

        private void processCancelRequest() {
            // Process cancel request if set
            if (m_CancelOrder)
            {
                // First, check if we have an open position that needs to be closed
                if (StrategyInfo.MarketPosition > 0)
                {
                    // Close long position
                    m_ExitLongOrder.Send(StrategyInfo.MarketPosition);
                    Output.WriteLine("POSITION RESET: Closing long position of " + StrategyInfo.MarketPosition + " contracts");
                }
                else if (StrategyInfo.MarketPosition < 0)
                {
                    // Close short position
                    m_ExitShortOrder.Send(Math.Abs(StrategyInfo.MarketPosition));
                    Output.WriteLine("POSITION RESET: Closing short position of " + Math.Abs(StrategyInfo.MarketPosition) + " contracts");
                }
                else
                {
                    Output.WriteLine("CANCEL: No open position to close");
                }

                // Then reset all state variables
                ResetAllState();
                m_CancelOrder = false;
            }

            // Process position closing if flagged (this is for the Ctrl+Right click case)
            if (m_ClosePositionFlag)
            {
                if (StrategyInfo.MarketPosition > 0)
                {
                    m_ExitLongOrder.Send(StrategyInfo.MarketPosition);
                    Output.WriteLine("POSITION RESET: Closing long position of " + StrategyInfo.MarketPosition + " contracts");
                }
                else if (StrategyInfo.MarketPosition < 0)
                {
                    m_ExitShortOrder.Send(Math.Abs(StrategyInfo.MarketPosition));
                    Output.WriteLine("POSITION RESET: Closing short position of " + Math.Abs(StrategyInfo.MarketPosition) + " contracts");
                }

                m_ClosePositionFlag = false;
            }
        }

        // Main calculation method - minimal implementation
        protected override void CalcBar()
        {
            // Synchronize position tracking at the beginning of each calculation
            //SynchronizePositionTracking();
            processCancelRequest();

            // Monitor price levels and update indicator if needed
            if (m_NeedToUpdateIndicator)
            {
                if (m_MonitoringBuyPrice)
                    UpdateIndicatorLine(m_TargetBuyPrice);
                else if (m_MonitoringSellPrice)
                    UpdateIndicatorLine(m_TargetSellPrice);

                m_NeedToUpdateIndicator = false;
            }

            // Price monitoring - log when price reaches target
            if (m_MonitoringBuyPrice && Bars.Close[0] >= m_TargetBuyPrice)
            {
                processBuyTarget();
            }
            else if (m_MonitoringSellPrice && Bars.Close[0] <= m_TargetSellPrice)
            {
                processSellTarget();
            }

            // Check if we're waiting for a sell order to be filled
            if (m_WaitingForSellFill)
            {
                // Check if position has changed, indicating the order was filled
                if (StrategyInfo.MarketPosition < m_LastKnownPosition)
                {
                    // Order was filled
                    m_WaitingForSellFill = false;
                    m_SellOrderQty = 0;
                    m_SellStopPrice = 0;
                    m_SellLimitPrice = 0;
                    
                    // Calculate the protective stop price (22 ticks above entry)
                    double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                    m_ProtectiveStopShortPrice = Bars.Close[0] + (ProtectiveStopTicks * tickSize);
                    
                    // Set the flag to indicate we need to place a protective stop
                    m_ProtectiveStopShortActive = true;
                }
                else
                {
                    // Order not filled yet, resubmit
                    try
                    {
                        m_SellStopLimitOrder.Send(m_SellStopPrice, m_SellLimitPrice, m_SellOrderQty);
                    }
                    catch (Exception)
                    {
                        // Silently handle exception
                    }
                }

                // Update last known position
                m_LastKnownPosition = StrategyInfo.MarketPosition;
            }

            // Check if we're waiting for a buy order to be filled
            if (m_WaitingForBuyFill)
            {
                // Check if position has changed, indicating the order was filled
                if (StrategyInfo.MarketPosition > m_LastKnownPosition)
                {
                    // Order was filled
                    m_WaitingForBuyFill = false;
                    m_BuyOrderQty = 0;
                    m_BuyStopPrice = 0;
                    m_BuyLimitPrice = 0;
                    
                    // Calculate the protective stop price (22 ticks below entry)
                    double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                    m_ProtectiveStopLongPrice = Bars.Close[0] - (ProtectiveStopTicks * tickSize);
                    
                    // Set the flag to indicate we need to place a protective stop
                    m_ProtectiveStopLongActive = true;
                }
                else
                {
                    // Order not filled yet, resubmit
                    try
                    {
                        m_BuyStopLimitOrder.Send(m_BuyStopPrice, m_BuyLimitPrice, m_BuyOrderQty);
                    }
                    catch (Exception)
                    {
                        // Silently handle exception
                    }
                }

                // Update last known position
                m_LastKnownPosition = StrategyInfo.MarketPosition;
            }
            
            // Check if we need to place or resubmit a protective stop for short positions
            if (m_ProtectiveStopShortActive && StrategyInfo.MarketPosition < 0)
            {
                try
                {
                    // Place or resubmit a stop order at the protective stop price
                    m_ProtectiveStopShort.Send(m_ProtectiveStopShortPrice, Math.Abs(StrategyInfo.MarketPosition));
                }
                catch (Exception)
                {
                    // Silently handle exception
                }
                
                // Check if the position has been closed, indicating the stop was triggered
                if (StrategyInfo.MarketPosition == 0)
                {
                    // Reset the protective stop tracking variables
                    m_ProtectiveStopShortActive = false;
                    m_ProtectiveStopShortPrice = 0;
                }
            }
            else if (m_ProtectiveStopShortActive && StrategyInfo.MarketPosition >= 0)
            {
                // If we no longer have a short position but the flag is still active,
                // reset the protective stop tracking variables
                m_ProtectiveStopShortActive = false;
                m_ProtectiveStopShortPrice = 0;
            }
            
            // Check if we need to place or resubmit a protective stop for long positions
            if (m_ProtectiveStopLongActive && StrategyInfo.MarketPosition > 0)
            {
                try
                {
                    // Place or resubmit a stop order at the protective stop price
                    m_ProtectiveStopLong.Send(m_ProtectiveStopLongPrice, StrategyInfo.MarketPosition);
                }
                catch (Exception)
                {
                    // Silently handle exception
                }
                
                // Check if the position has been closed, indicating the stop was triggered
                if (StrategyInfo.MarketPosition == 0)
                {
                    // Reset the protective stop tracking variables
                    m_ProtectiveStopLongActive = false;
                    m_ProtectiveStopLongPrice = 0;
                }
            }
            else if (m_ProtectiveStopLongActive && StrategyInfo.MarketPosition <= 0)
            {
                // If we no longer have a long position but the flag is still active,
                // reset the protective stop tracking variables
                m_ProtectiveStopLongActive = false;
                m_ProtectiveStopLongPrice = 0;
            }

            // No duplicate code needed here
        }

        // Helper method to check order status
        private void CheckOrderStatus()
        {
            Output.WriteLine("ORDER STATUS CHECK: " + DateTime.Now.ToString("HH:mm:ss.fff"));

            // Log current position information
            Output.WriteLine("CURRENT POSITION: " + StrategyInfo.MarketPosition +
                           " contracts, AvgEntryPrice=" + StrategyInfo.AvgEntryPrice);

            // Log trading environment information
            Output.WriteLine("TRADING MODE: IsRealTimeCalc=" + Environment.IsRealTimeCalc);

            // Log paper trader status
            Output.WriteLine("PAPER TRADER STATUS: Checking if orders are being processed");
        }

        private void processBuyTarget()
        {
            // Execute buy market order with error handling
            try
            {
                // For a market order:
                // - Executes immediately at the current market price
                // - Fastest possible execution with no price conditions
                
                // Log environment information
                Output.WriteLine("ENVIRONMENT: IsRealTimeCalc=" + Environment.IsRealTimeCalc);
                
                // Log order submission attempt
                Output.WriteLine("SUBMITTING ORDER: Buy market order at current price");
                
                // Submit the market order once for immediate execution
                try
                {
                    // Send the market order with quantity
                    // According to documentation: IOrderMarket.Send(int numLots)
                    m_BuyMarketOrder.Send(OrderQty);
                    Output.WriteLine("ORDER SENT: Buy market order sent for " + OrderQty + " contracts");
                    
                    // Set flags to track order fill status immediately after sending
                    m_WaitingForBuyFill = true;
                    m_BuyOrderQty = OrderQty;
                    m_LastKnownPosition = StrategyInfo.MarketPosition; // Store current position for comparison
                }
                catch (Exception ex)
                {
                    Output.WriteLine("ORDER ERROR: " + ex.Message);
                }
                
                // Check order status immediately after sending
                CheckOrderStatus();
            }
            catch (Exception ex)
            {
                Output.WriteLine("PROCESS ERROR: " + ex.Message);
            }

            // Clear the target price and monitoring flag
            m_MonitoringBuyPrice = false;
            m_TargetBuyPrice = 0;

            // Clear the indicator line
            StrategyInfo.SetPlotValue(2, 1);
        }

        private void processSellTarget()
        {
            // Update last known position
            m_LastKnownPosition = StrategyInfo.MarketPosition;

            // Execute sell stop-limit order with error handling
            try
            {
                // For a sell stop-limit order:
                // - Stop price: The price that triggers the order (at or below current price)
                // - Limit price: The minimum price you're willing to accept (slightly below stop price)

                // Calculate and store stop and limit prices
                // Set stop price 1 tick below target price
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                m_SellStopPrice = m_TargetSellPrice - tickSize;
                m_SellLimitPrice = m_SellStopPrice - tickSize; // 1 tick below stop price

                // Send the stop-limit order with stop price, limit price, and quantity as int
                m_SellStopLimitOrder.Send(m_SellStopPrice, m_SellLimitPrice, OrderQty);

                // Set flags to track order fill status
                m_WaitingForSellFill = true;
                m_SellOrderQty = OrderQty;
                m_LastKnownPosition = StrategyInfo.MarketPosition; // Store current position for comparison
            }
            catch (Exception)
            {
                // Silently handle exception
            }

            // Clear the target price and monitoring flag
            m_MonitoringSellPrice = false;
            m_TargetSellPrice = 0;

            // Clear the indicator line
            StrategyInfo.SetPlotValue(2, 1);
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

            // RIGHT CLICK: Cancel all orders
            if (arg.buttons == MouseButtons.Right)
            {
                m_CancelOrder = true;
                Output.WriteLine("CANCEL REQUEST: Will cancel all orders in CalcBar");
            }

            // CTRL+RIGHT CLICK: Close all positions
            if (arg.buttons == MouseButtons.Right && (arg.keys & Keys.Control) == Keys.Control)
            {
                m_ClosePositionFlag = true;
                Output.WriteLine("CLOSE POSITION REQUEST: Will close all positions in CalcBar");
            }
        }

        // Helper method to update the indicator line on the chart
        private void UpdateIndicatorLine(double price)
        {
            // Draw a horizontal line at the target price
            StrategyInfo.SetPlotValue(1, price);
        }
    }
}
