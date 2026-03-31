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
    public class RenkoBarTrading : SignalObject
    {
        [Input] public int OrderQty { get; set; }
        [Input] public int Level1 { get; set; } // Matches Indicator (Ticks offset)
        [Input] public int LimitOffsetTicks { get; set; }
        [Input] public int StopTailOffsetTicks { get; set; }
        [Input] public bool ShowPriceLine { get; set; }
        [Input] public int ProximityTicks { get; set; }
        [Input] public bool ShowHUD { get; set; }

        private IOrderStopLimit m_BuyStopLimit;
        private IOrderStopLimit m_SellStopLimit;
        private IOrderPriced m_BuyExit;
        private IOrderPriced m_SellExit;
        private IOrderMarket m_CloseLongNextBar;
        private IOrderMarket m_CloseShortNextBar;
        
        private double m_LastClosePrice = 0;
        private double m_LastOpenPrice = 0;
        private bool m_LastBarWasUp = true;
        private int m_LastBarIndex = -1;
        
        private double m_StopPrice = 0;
        private double m_LimitPrice = 0;
        private double m_ProtectiveStopPrice = 0;
        private int m_LastMarketPosition = 0;
        private bool m_BuyOrderActive = false;
        private bool m_SellOrderActive = false;
        private bool m_OrderCreatedInMouseEvent = false;
        private bool m_CancelRequested = false;
        private double m_ClickPrice = 0;
        
        private ITrendLineObject m_PriceLine;
        private ITextObject m_ScoreLabel;

        public RenkoBarTrading(object ctx) : base(ctx)
        {
            OrderQty = 1;
            Level1 = 4; // Default to match indicator standard
            LimitOffsetTicks = 1;
            StopTailOffsetTicks = 2;
            ShowPriceLine = true;
            ProximityTicks = 5;
            ShowHUD = true;
        }

        protected override void Create()
        {
            m_BuyStopLimit = OrderCreator.StopLimit(new SOrderParameters(Contracts.Default, "ManualBuy", EOrderAction.Buy));
            m_SellStopLimit = OrderCreator.StopLimit(new SOrderParameters(Contracts.Default, "ManualSell", EOrderAction.SellShort));
            
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
            m_OrderCreatedInMouseEvent = false;
            m_CancelRequested = false;
            m_LastBarIndex = -1;
            if (m_ScoreLabel != null) m_ScoreLabel.Delete();
        }

        protected override void CalcBar()
        {
            // Sync with LAST COMPLETED BRICK (Standard Renko logic)
            if (Bars.Status == EBarState.Close)
            {
                m_LastClosePrice = Bars.Close[0];
                m_LastOpenPrice = Bars.Open[0];
                m_LastBarWasUp = (m_LastClosePrice > m_LastOpenPrice);
                m_LastBarIndex = Bars.CurrentBar;
            }

            if (!Environment.IsRealTimeCalc) return;

            int currentPosition = StrategyInfo.MarketPosition;
            double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;

            if (m_FlattenRequested && currentPosition != 0)
            {
                int qtyToClose = Math.Abs(currentPosition);
                if (currentPosition > 0) m_CloseLongNextBar.Send(qtyToClose);
                else if (currentPosition < 0) m_CloseShortNextBar.Send(qtyToClose);
                m_ProtectiveStopPrice = 0; 
            }
            else if (m_FlattenRequested && currentPosition == 0) 
            {
                m_FlattenRequested = false; 
            }

            // DETECT FILL
            if (currentPosition != 0 && m_LastMarketPosition == 0)
            {
                if (currentPosition > 0)
                {
                    double lowestTail = Math.Min(Bars.Low[0], Bars.Low[1]);
                    m_ProtectiveStopPrice = lowestTail - (StopTailOffsetTicks * tickSize);
                }
                else if (currentPosition < 0)
                {
                    double highestTail = Math.Max(Bars.High[0], Bars.High[1]);
                    m_ProtectiveStopPrice = highestTail + (StopTailOffsetTicks * tickSize);
                }
                m_BuyOrderActive = m_SellOrderActive = false;
                m_StopPrice = 0;
                m_LimitPrice = 0;
                if (m_PriceLine != null) m_PriceLine.Delete();
                m_CancelRequested = false;
            }

            if (currentPosition > 0 && m_ProtectiveStopPrice > 0) m_BuyExit.Send(m_ProtectiveStopPrice);
            else if (currentPosition < 0 && m_ProtectiveStopPrice > 0) m_SellExit.Send(m_ProtectiveStopPrice);

            if (currentPosition == 0 && m_LastMarketPosition != 0) m_ProtectiveStopPrice = 0;

            m_LastMarketPosition = currentPosition;

            if (currentPosition != 0 && (m_BuyOrderActive || m_SellOrderActive))
            {
                m_CancelRequested = true;
            }

            if (m_CancelRequested)
            {
                m_BuyOrderActive = m_SellOrderActive = false;
                m_StopPrice = 0;
                m_LimitPrice = 0;
                if (m_PriceLine != null) m_PriceLine.Delete();
                m_CancelRequested = false;
            }

            if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0)
            {
                ProcessManualOrderRequest(m_ClickPrice);
                m_OrderCreatedInMouseEvent = false;
                m_ClickPrice = 0;
            }

            if (!m_CancelRequested && currentPosition == 0)
            {
                if (m_BuyOrderActive && m_StopPrice > 0) m_BuyStopLimit.Send(m_StopPrice, m_LimitPrice, OrderQty);
                else if (m_SellOrderActive && m_StopPrice > 0) m_SellStopLimit.Send(m_StopPrice, m_LimitPrice, OrderQty);
            }

            if (ShowHUD) UpdateTickHUD();
        }

        private void UpdateTickHUD()
        {
            double totalProfitCurrency = StrategyInfo.ClosedEquity; 
            double tickValue = (Bars.Info.PriceScale != 0) ? (Bars.Info.MinMove / Bars.Info.PriceScale * Bars.Info.BigPointValue) : 0;
            double totalTicks = (tickValue != 0) ? (totalProfitCurrency / tickValue) : 0;
            string sign = totalTicks >= 0 ? "+" : "";
            string scoreText = string.Format("RENKO HUD: {0}{1:F1} Ticks ({2}{3:C2})", sign, totalTicks, sign, totalProfitCurrency);
            if (m_ScoreLabel == null) { m_ScoreLabel = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.High[0]), scoreText); m_ScoreLabel.Size = 14; }
            m_ScoreLabel.Text = scoreText;
            m_ScoreLabel.Color = totalTicks >= 0 ? Color.LimeGreen : Color.Tomato;
            m_ScoreLabel.Location = new ChartPoint(Bars.Time[0], Bars.High[0]);
        }

        private bool m_FlattenRequested = false;

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            if (arg.buttons != MouseButtons.Left) return;
            bool ctrl = (arg.keys & Keys.Control) == Keys.Control;
            bool shift = (arg.keys & Keys.Shift) == Keys.Shift;
            if (ctrl) {
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                double clickPrice = arg.point.Price;
                if ((m_BuyOrderActive || m_SellOrderActive) && Math.Abs(clickPrice - m_StopPrice) <= (ProximityTicks * tickSize)) { m_CancelRequested = true; return; }
                m_ClickPrice = clickPrice; m_OrderCreatedInMouseEvent = true;
            } else if (shift) {
                m_CancelRequested = true; if (StrategyInfo.MarketPosition != 0) m_FlattenRequested = true;
            }
        }

        private void ProcessManualOrderRequest(double clickPrice)
        {
            double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;

            double bullishProjection = 0;
            double bearishProjection = 0;

            // Indicator Logic Clone:
            if (m_LastBarWasUp) {
                bullishProjection = m_LastClosePrice + (Level1 * tickSize); // Continuation (UP)
                bearishProjection = m_LastOpenPrice - (Level1 * tickSize); // Reversal (DOWN) - This matches the YELLOW LINE
            } else {
                bearishProjection = m_LastClosePrice - (Level1 * tickSize); // Continuation (DOWN)
                bullishProjection = m_LastOpenPrice + (Level1 * tickSize); // Reversal (UP) - This matches the YELLOW LINE
            }

            // Snap behavior: Using LAST CLOSE as the stable reference for "Above or Below"
            if (clickPrice > m_LastClosePrice) { 
                m_StopPrice = bullishProjection; 
                m_LimitPrice = m_StopPrice + (LimitOffsetTicks * tickSize);
                m_BuyOrderActive = true; 
                m_SellOrderActive = false;
            } else { 
                m_StopPrice = bearishProjection; 
                m_LimitPrice = m_StopPrice - (LimitOffsetTicks * tickSize);
                m_SellOrderActive = true; 
                m_BuyOrderActive = false;
            }
            UpdateVisualMarker();
        }

        private void UpdateVisualMarker()
        {
            if (!ShowPriceLine) return;
            if (m_PriceLine != null) m_PriceLine.Delete();
            m_PriceLine = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], m_StopPrice), new ChartPoint(Bars.Time[0].AddMinutes(5), m_StopPrice));
            m_PriceLine.Color = m_BuyOrderActive ? Color.Cyan : Color.Magenta;
            m_PriceLine.Style = ETLStyle.ToolDashed;
            m_PriceLine.Size = 2;
            m_PriceLine.ExtRight = true;
            m_PriceLine.ExtLeft = true;
        }

        protected override void Destroy() { if (m_ScoreLabel != null) m_ScoreLabel.Delete(); }
    }
}
