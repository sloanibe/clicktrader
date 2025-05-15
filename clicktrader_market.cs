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
    public class clicktrader_market : SignalObject
    {
        // Input variables
        [Input] public int OrderQty { get; set; }

        // Order object
        private IOrderMarket m_MarketSell;
        private bool m_OrderCreatedInMouseEvent = false;

        public clicktrader_market(object ctx) : base(ctx)
        {
            // Initialize default values for inputs
            OrderQty = 1;
        }

        protected override void Create()
        {
            base.Create();

            // Create market sell order with basic parameters
            m_MarketSell = OrderCreator.MarketNextBar(
                new SOrderParameters(Contracts.Default, "MarketSell", EOrderAction.Sell));

            // Log initialization information
            Output.WriteLine("*** SIMPLIFIED MARKET CLICKTRADER INITIALIZED ***");
            Output.WriteLine("Use Ctrl+Click to place a market SELL order");
        }

        protected override void CalcBar()
        {
            try
            {
                // Process new orders that were created in OnMouseEvent
                if (m_OrderCreatedInMouseEvent && StrategyInfo.MarketPosition == 0)
                {
                    try
                    {
                        // Send the market sell order
                        m_MarketSell.Send(OrderQty);
                        Output.WriteLine("SELL Market Order sent: Quantity: " + OrderQty);
                        
                        // Reset flag after order is placed
                        m_OrderCreatedInMouseEvent = false;
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("Error processing new order: " + ex.Message);
                    }
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
                // Only process left mouse clicks with Ctrl key
                if (arg.buttons != MouseButtons.Left || arg.keys != Keys.Control)
                    return;

                // Get the current click price for logging
                double currentClickPrice = arg.point.Price;
                
                // Flag for new order creation
                m_OrderCreatedInMouseEvent = true;
                
                Output.WriteLine("PLACING ORDER: Market SELL at current price (around " + currentClickPrice + ")");
                Output.WriteLine("Order will be created in next CalcBar. Quantity: " + OrderQty);
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error processing mouse event: " + ex.Message);
            }
        }
    }
}
