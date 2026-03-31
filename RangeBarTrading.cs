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
    public class RangeBarTrading : SignalObject
    {
        [Input] public int OrderQty { get; set; }
        [Input] public int RangeSizeTicks { get; set; }
        [Input] public int LimitOffsetTicks { get; set; }
        [Input] public int StopTailOffsetTicks { get; set; }
        [Input] public bool ShowHUD { get; set; }

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
        private bool m_CancelRequested = false;
        private bool m_FlattenRequested = false;
        
        private ITrendLineObject m_PriceLine;
        private ITextObject m_HUDLabel;

        public RangeBarTrading(object ctx) : base(ctx)
        {
            OrderQty = 1;
            RangeSizeTicks = 7;
            LimitOffsetTicks = 1;
            StopTailOffsetTicks = 2;
            ShowHUD = true;
        }

        protected override void Create()
        {
            m_BuyStopLimit = OrderCreator.StopLimit(new SOrderParameters(Contracts.Default, "RangeBuy", EOrderAction.Buy));
            m_SellStopLimit = OrderCreator.StopLimit(new SOrderParameters(Contracts.Default, "RangeSell", EOrderAction.SellShort));
            
            m_BuyExit = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectLong", EOrderAction.Sell, OrderExit.FromAll));
            m_SellExit = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectShort", EOrderAction.BuyToCover, OrderExit.FromAll));
            
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
            m_CancelRequested = false;
            m_FlattenRequested = false;
            if (m_HUDLabel != null) m_HUDLabel.Delete();
        }

        protected override void CalcBar()
        {
            if (!Environment.IsRealTimeCalc) return;

            int currentPosition = StrategyInfo.MarketPosition;
            double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;

            // 1. Handle Emergency Flatten
            if (m_FlattenRequested && currentPosition != 0)
            {
                if (currentPosition > 0) m_CloseLongNextBar.Send(Math.Abs(currentPosition));
                else m_CloseShortNextBar.Send(Math.Abs(currentPosition));
                m_ProtectiveStopPrice = 0;
            }
            else if (m_FlattenRequested && currentPosition == 0)
            {
                m_FlattenRequested = false;
            }

            // 2. Detect NEW Fill
            if (currentPosition != 0 && m_LastMarketPosition == 0)
            {
                if (currentPosition > 0)
                {
                    double lowestTail = Math.Min(Bars.Low[0], Bars.Low[1]);
                    m_ProtectiveStopPrice = lowestTail - (StopTailOffsetTicks * tickSize);
                }
                else
                {
                    double highestTail = Math.Max(Bars.High[0], Bars.High[1]);
                    m_ProtectiveStopPrice = highestTail + (StopTailOffsetTicks * tickSize);
                }
                
                // Clear active entry states
                m_BuyOrderActive = m_SellOrderActive = false;
                m_StopPrice = 0;
                if (m_PriceLine != null) m_PriceLine.Delete();
            }

            // 3. Dynamic "Chasing" Logic for Range Completion
            if (currentPosition == 0 && !m_CancelRequested)
            {
                if (m_BuyOrderActive)
                {
                    // For Long: Tether to the developing bottom of the bar
                    double currentLow = Math.Min(Bars.Low[0], (Bars.StatusLine.Ask > 0 ? Bars.StatusLine.Ask : Bars.Close[0]));
                    m_StopPrice = Math.Round((currentLow + (RangeSizeTicks * tickSize)) / tickSize) * tickSize;
                    m_LimitPrice = m_StopPrice + (LimitOffsetTicks * tickSize);
                    
                    m_BuyStopLimit.Send(m_StopPrice, m_LimitPrice, OrderQty);
                    UpdateVisualMarker();
                }
                else if (m_SellOrderActive)
                {
                    // For Short: Tether to the developing top of the bar
                    double currentHigh = Math.Max(Bars.High[0], (Bars.StatusLine.Bid > 0 ? Bars.StatusLine.Bid : Bars.Close[0]));
                    m_StopPrice = Math.Round((currentHigh - (RangeSizeTicks * tickSize)) / tickSize) * tickSize;
                    m_LimitPrice = m_StopPrice - (LimitOffsetTicks * tickSize);
                    
                    m_SellStopLimit.Send(m_StopPrice, m_LimitPrice, OrderQty);
                    UpdateVisualMarker();
                }
            }

            // 4. Maintain Exit Orders
            if (currentPosition > 0 && m_ProtectiveStopPrice > 0)
            {
                m_BuyExit.Send(m_ProtectiveStopPrice);
            }
            else if (currentPosition < 0 && m_ProtectiveStopPrice > 0)
            {
                m_SellExit.Send(m_ProtectiveStopPrice);
            }

            // 5. Position Closed
            if (currentPosition == 0 && m_LastMarketPosition != 0)
            {
                m_ProtectiveStopPrice = 0;
            }

            // 6. Cancellation Cleanup
            if (m_CancelRequested)
            {
                m_BuyOrderActive = m_SellOrderActive = false;
                m_StopPrice = 0;
                if (m_PriceLine != null) m_PriceLine.Delete();
                m_CancelRequested = false;
            }

            m_LastMarketPosition = currentPosition;
            if (ShowHUD) UpdateHUD();
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            if (arg.buttons != MouseButtons.Left) return;

            bool ctrl = (arg.keys & Keys.Control) == Keys.Control;
            bool shift = (arg.keys & Keys.Shift) == Keys.Shift;

            if (ctrl)
            {
                double clickPrice = arg.point.Price;
                double currentPrice = Bars.Close[0];

                if (clickPrice > currentPrice)
                {
                    m_BuyOrderActive = true;
                    m_SellOrderActive = false;
                }
                else
                {
                    m_SellOrderActive = true;
                    m_BuyOrderActive = false;
                }
            }
            else if (shift)
            {
                m_CancelRequested = true;
                if (StrategyInfo.MarketPosition != 0) m_FlattenRequested = true;
            }
        }

        private void UpdateVisualMarker() {
            if (!m_BuyOrderActive && !m_SellOrderActive) {
                if (m_PriceLine != null) m_PriceLine.Delete();
                return;
            }
            if (m_PriceLine != null) m_PriceLine.Delete();
            
            m_PriceLine = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], m_StopPrice), new ChartPoint(Bars.Time[0].AddMinutes(5), m_StopPrice));
            m_PriceLine.Color = m_BuyOrderActive ? Color.Cyan : Color.Magenta;
            m_PriceLine.Style = ETLStyle.ToolDashed; // Dashed like the other signal
            m_PriceLine.Size = 2; // Thicker for better visibility
            m_PriceLine.ExtRight = true;
            m_PriceLine.ExtLeft = true;
        }

        private void UpdateHUD()
        {
            double pnl = StrategyInfo.ClosedEquity;
            string status = "IDLE";
            if (m_BuyOrderActive) status = "CHASING BUY";
            if (m_SellOrderActive) status = "CHASING SELL";
            if (StrategyInfo.MarketPosition != 0) status = "IN TRADE";

            string text = string.Format("RANGE TRADER | {0} | PnL: {1:C2}", status, pnl);
            
            if (m_HUDLabel == null)
            {
                m_HUDLabel = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.High[0]), text);
                m_HUDLabel.Size = 14;
            }

            m_HUDLabel.Text = text;
            m_HUDLabel.Color = pnl >= 0 ? Color.LimeGreen : Color.Tomato;
            m_HUDLabel.Location = new ChartPoint(Bars.Time[0], Bars.High[0]);
        }

        protected override void Destroy()
        {
            if (m_HUDLabel != null) m_HUDLabel.Delete();
            if (m_PriceLine != null) m_PriceLine.Delete();
        }
    }
}
