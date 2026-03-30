using System;
using System.Drawing;
using System.Windows.Forms;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Strategy
{
    [IOGMode(IOGMode.Enabled)]
    [MouseEvents(true)]
    [SameAsSymbol(true)]
    [AllowSendOrdersAlways]
    public class FastManualStopLimit : SignalObject
    {
        [Input] public int OrderQty { get; set; }
        [Input] public int LimitOffsetTicks { get; set; }
        [Input] public int StopTailOffsetTicks { get; set; }
        [Input] public bool ShowPriceLine { get; set; }
        [Input] public int ProximityTicks { get; set; }

        private IOrderStopLimit m_BuyStopLimit;
        private IOrderStopLimit m_SellStopLimit;
        private IOrderPriced m_BuyExit;
        private IOrderPriced m_SellExit;
        private IOrderMarket m_CloseLongNextBar;
        private IOrderMarket m_CloseShortNextBar;
        private double m_StopPrice = 0;
        private double m_LimitPrice = 0;
        private double m_ProtectiveStopPrice = 0;
        private int m_LastMarketPosition = 0;
        private bool m_BuyOrderActive = false;
        private bool m_SellOrderActive = false;
        private bool m_OrderCreatedInMouseEvent = false;
        private bool m_CancelRequested = false;
        private string m_CancelReason = "";
        private double m_ClickPrice = 0;
        private double m_LastSentStopPrice = 0;
        private ITrendLineObject m_PriceLine;

        public FastManualStopLimit(object ctx) : base(ctx)
        {
            OrderQty = 1;
            LimitOffsetTicks = 1;
            StopTailOffsetTicks = 2;
            ShowPriceLine = true;
            ProximityTicks = 5;
        }

        protected override void Create()
        {
            m_BuyStopLimit = OrderCreator.StopLimit(new SOrderParameters(Contracts.Default, "ManualBuy", EOrderAction.Buy));
            m_SellStopLimit = OrderCreator.StopLimit(new SOrderParameters(Contracts.Default, "ManualSell", EOrderAction.SellShort));
            
            // Protective Stop Exits
            m_BuyExit = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectLong", EOrderAction.Sell, OrderExit.FromAll));
            m_SellExit = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectShort", EOrderAction.BuyToCover, OrderExit.FromAll));
            
            // Fast Flatten Exits
            m_CloseLongNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "EmergCloseLong", EOrderAction.Sell, OrderExit.FromAll));
            m_CloseShortNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "EmergCloseShort", EOrderAction.BuyToCover, OrderExit.FromAll));
        }

        protected override void StartCalc()
        {
            m_BuyOrderActive = m_SellOrderActive = false;
            m_StopPrice = 0;
            m_LimitPrice = 0;
            m_ProtectiveStopPrice = 0;
            m_LastMarketPosition = 0;
            m_OrderCreatedInMouseEvent = false;
            m_CancelRequested = false;
        }

        protected override void CalcBar()
        {
            if (!Environment.IsRealTimeCalc) return;

            int currentPosition = StrategyInfo.MarketPosition;

            // Handle Emergency Flatten Request
            if (m_FlattenRequested && currentPosition != 0)
            {
                int qtyToClose = Math.Abs(currentPosition);
                
                if (currentPosition > 0) m_CloseLongNextBar.Send(qtyToClose);
                else if (currentPosition < 0) m_CloseShortNextBar.Send(qtyToClose);
                
                // Do NOT set m_FlattenRequested = false here! 
                // We keep calling Send() on every tick until the broker confirms the position is closed. 
                m_ProtectiveStopPrice = 0; 
                
                // We removed the 'return;' here so the code continues down and successfully 
                // processes the visual line cancellation logic for m_CancelRequested.
            }
            else if (m_FlattenRequested && currentPosition == 0) 
            {
                Output.WriteLine("🚨 FLATTENING COMPLETE!");
                m_FlattenRequested = false; 
            }

            // Detect NEW fill
            if (currentPosition != 0 && m_LastMarketPosition == 0)
            {
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                if (currentPosition > 0) // We went Long
                {
                    // For Renko: If we just started a new bar, its Low is high up. 
                    // Math.Min guarantees we grab the true lowest tail between the current forming bar and the previous completed bar.
                    double lowestTail = Math.Min(Bars.Low[0], Bars.Low[1]);
                    m_ProtectiveStopPrice = lowestTail - (StopTailOffsetTicks * tickSize);
                    Output.WriteLine(string.Format("🛡️ POSITION FILLED (LONG). Protective Stop set at {0} (Lowest Tail: {1})", m_ProtectiveStopPrice, lowestTail));
                }
                else if (currentPosition < 0) // We went Short
                {
                    // For Renko: Math.Max guarantees we grab the true highest tail between the current forming bar and the previous completed bar.
                    double highestTail = Math.Max(Bars.High[0], Bars.High[1]);
                    m_ProtectiveStopPrice = highestTail + (StopTailOffsetTicks * tickSize);
                    Output.WriteLine(string.Format("🛡️ POSITION FILLED (SHORT). Protective Stop set at {0} (Highest Tail: {1})", m_ProtectiveStopPrice, highestTail));
                }
                
                // Clear the manual entry visual/flags
                m_BuyOrderActive = m_SellOrderActive = false;
                m_StopPrice = 0;
                m_LimitPrice = 0;
                if (m_PriceLine != null) m_PriceLine.Delete();
                m_CancelRequested = false;
            }

            // Maintain Exit Orders while in position
            if (currentPosition > 0 && m_ProtectiveStopPrice > 0)
            {
                m_BuyExit.Send(m_ProtectiveStopPrice);
            }
            else if (currentPosition < 0 && m_ProtectiveStopPrice > 0)
            {
                m_SellExit.Send(m_ProtectiveStopPrice);
            }

            // Detect Closed Position
            if (currentPosition == 0 && m_LastMarketPosition != 0)
            {
                m_ProtectiveStopPrice = 0;
                Output.WriteLine("✅ POSITION CLOSED. Protective Stop Reset.");
            }

            m_LastMarketPosition = currentPosition;

            // Auto-cancel entry logic if user manually closed position and forgot to cancel
            if (currentPosition != 0 && (m_BuyOrderActive || m_SellOrderActive))
            {
                m_CancelReason = "Position Found - Clearing Manual Order";
                m_CancelRequested = true;
            }

            // Process cancellation request
            if (m_CancelRequested)
            {
                // In MultiCharts IOGMode, we DO NOT send an invalid Send(0, 0) to cancel.
                // Setting these active flags to false ensures the Send() loop skips on the next tick,
                // which tells the MultiCharts engine to natively pull the order from the broker.
                m_BuyOrderActive = m_SellOrderActive = false;
                m_StopPrice = 0;
                m_LimitPrice = 0;
                if (m_PriceLine != null) m_PriceLine.Delete();
                
                Output.WriteLine("❌ CANCELLED: " + m_CancelReason);
                m_CancelRequested = false;
            }

            // Process new order from mouse
            if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0)
            {
                ProcessManualOrderRequest(m_ClickPrice);
                m_OrderCreatedInMouseEvent = false;
                m_ClickPrice = 0;
            }

            // In MultiCharts IOGMode, Send() MUST be called unconditionally on EVERY tick 
            // to keep a managed order alive at the broker. The engine natively prevents API spam.
            if (!m_CancelRequested && currentPosition == 0) // Only send entries if flat
            {
                if (m_BuyOrderActive && m_StopPrice > 0)
                {
                    m_BuyStopLimit.Send(m_StopPrice, m_LimitPrice, OrderQty);
                    
                    if (m_StopPrice != m_LastSentStopPrice)
                    {
                        Output.WriteLine(string.Format("🚀 SENT BUY STOP LIMIT TO BROKER @ Stop: {0}, Limit: {1}", m_StopPrice, m_LimitPrice));
                        m_LastSentStopPrice = m_StopPrice;
                    }
                }
                else if (m_SellOrderActive && m_StopPrice > 0)
                {
                    m_SellStopLimit.Send(m_StopPrice, m_LimitPrice, OrderQty);
                    
                    if (m_StopPrice != m_LastSentStopPrice)
                    {
                        Output.WriteLine(string.Format("🚀 SENT SELL STOP LIMIT TO BROKER @ Stop: {0}, Limit: {1}", m_StopPrice, m_LimitPrice));
                        m_LastSentStopPrice = m_StopPrice;
                    }
                }
            }

        }

        private bool m_FlattenRequested = false;

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            if (arg.buttons != MouseButtons.Left) return;

            bool ctrl = (arg.keys & Keys.Control) == Keys.Control;
            bool shift = (arg.keys & Keys.Shift) == Keys.Shift;

            if (ctrl)
            {
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                double clickPrice = arg.point.Price;

                if ((m_BuyOrderActive || m_SellOrderActive) && 
                    Math.Abs(clickPrice - m_StopPrice) <= (ProximityTicks * tickSize))
                {
                    m_CancelReason = "Proximity Cancellation via Ctrl+Click";
                    m_CancelRequested = true;
                    return;
                }

                m_ClickPrice = clickPrice;
                m_OrderCreatedInMouseEvent = true;
            }
            else if (shift) 
            {
                m_CancelReason = "Emergency Cancel & Flatten via Shift+Click";
                m_CancelRequested = true;
                if (StrategyInfo.MarketPosition != 0) {
                    m_FlattenRequested = true;
                }
            }
        }

        private void ProcessManualOrderRequest(double clickPrice)
        {
            double currentAsk = (Bars.StatusLine.Ask > 0) ? Bars.StatusLine.Ask : Bars.Close[0];
            double currentBid = (Bars.StatusLine.Bid > 0) ? Bars.StatusLine.Bid : Bars.Close[0];
            
            double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
            double stopPrice = Math.Round(clickPrice / tickSize) * tickSize;
            double minDistance = tickSize * 3;

            if (stopPrice >= currentAsk + minDistance) {
                m_StopPrice = stopPrice;
                m_LimitPrice = m_StopPrice + (LimitOffsetTicks * tickSize);
                m_BuyOrderActive = true; 
                m_SellOrderActive = false;
                Output.WriteLine(string.Format("🔵 SETUP BUY STOP-LIMIT @ Stop: {0}, Limit: {1}", m_StopPrice, m_LimitPrice));
            } else if (stopPrice <= currentBid - minDistance) {
                m_StopPrice = stopPrice;
                m_LimitPrice = m_StopPrice - (LimitOffsetTicks * tickSize);
                m_SellOrderActive = true; 
                m_BuyOrderActive = false;
                Output.WriteLine(string.Format("🔴 SETUP SELL STOP-LIMIT @ Stop: {0}, Limit: {1}", m_StopPrice, m_LimitPrice));
            }
            else
            {
                Output.WriteLine(string.Format("⚠️ INTERNAL REJECT: Click Price {0} is too close to Ask ({1}) / Bid ({2}). Min Distance: {3}", 
                    stopPrice, currentAsk, currentBid, minDistance));
                m_BuyOrderActive = m_SellOrderActive = false;
                m_StopPrice = 0;
                m_LimitPrice = 0;
                if (m_PriceLine != null) m_PriceLine.Delete();
                return;
            }
            UpdateVisualMarker();
        }

        private void UpdateVisualMarker()
        {
            if (!ShowPriceLine) return;
            if (m_PriceLine != null) m_PriceLine.Delete();

            Output.WriteLine(string.Format("🖌️ DRAWING LINE: {0} at {1}", m_BuyOrderActive ? "BUY" : "SELL", m_StopPrice));

            // Use AddMinutes(5) or similar to ensure the two points have distinctly different 
            // timestamps. In fast Renko charts, Time[0] and Time[1] can have the exact same 
            // timestamp, causing the MultiCharts drawing engine to fail to render the line segment.
            m_PriceLine = DrwTrendLine.Create(
                new ChartPoint(Bars.Time[0], m_StopPrice),
                new ChartPoint(Bars.Time[0].AddMinutes(5), m_StopPrice));
            
            m_PriceLine.Color = m_BuyOrderActive ? Color.Cyan : Color.Magenta;
            m_PriceLine.Style = ETLStyle.ToolDashed;
            m_PriceLine.Size = 2;
            m_PriceLine.ExtRight = true;
            m_PriceLine.ExtLeft = true; // Add ExtLeft to make it a true infinite horizontal line
        }
    }
}
