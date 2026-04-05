using System;
using System.Drawing;
using System.Windows.Forms;
using PowerLanguage;
using PowerLanguage.Function;
using System.Collections.Generic;

namespace PowerLanguage.Strategy
{
    [IOGMode(IOGMode.Enabled)]
    [MouseEvents(true)]
    [SameAsSymbol(true)]
    [AllowSendOrdersAlways]
    public class RenkoBarTrading : SignalObject
    {
        [Input] public int OrderQty { get; set; }
        [Input] public int Level1 { get; set; } // 0 = Auto-detect from chart
        [Input] public int StopTailOffsetTicks { get; set; }
        [Input] public bool ShowPriceLine { get; set; }
        [Input] public int ProximityTicks { get; set; }
        [Input] public bool ShowHUD { get; set; }
        [Input] public bool UseLimitLoss { get; set; }

        private IOrderPriced m_BuyStop;
        private IOrderPriced m_SellStop;
        
        // Exits
        private IOrderPriced m_BuyExitStop;
        private IOrderPriced m_SellExitStop;
        
        private IOrderMarket m_CloseLongNextBar;
        private IOrderMarket m_CloseShortNextBar;
        
        private double m_LastClosePrice = 0;
        private double m_LastOpenPrice = 0;
        private bool m_LastBarWasUp = true;
        private int m_LastBarIndex = -1;
        private double m_AutoDetectedBrickSize = 0;
        
        private double m_StopPrice = 0;
        private double m_ProtectiveStopPrice = 0;
        private double m_LimitLossTriggerPrice = 0;
        private bool m_LimitLossArmed = false;
        private bool m_TrailingActive = false;
        
        private int m_LastMarketPosition = 0;
        private bool m_BuyOrderActive = false;
        private bool m_SellOrderActive = false;
        private bool m_OrderCreatedInMouseEvent = false;
        private bool m_FlattenRequested = false;
        private bool m_CancelRequested = false;
        private double m_ClickPrice = 0;
        
        private ITrendLineObject m_PriceLine;
        private ITrendLineObject m_StopLine;
        private ITextObject m_LabelHUD;

        public RenkoBarTrading(object ctx) : base(ctx)
        {
            OrderQty = 1;
            Level1 = 0;
            StopTailOffsetTicks = 2;
            ShowPriceLine = true;
            ProximityTicks = 5;
            ShowHUD = true;
            UseLimitLoss = false;
        }

        protected override void Create()
        {
            m_BuyStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ManualBuy", EOrderAction.Buy));
            m_SellStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ManualSell", EOrderAction.SellShort));
            
            m_BuyExitStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectLong", EOrderAction.Sell, OrderExit.FromAll));
            m_SellExitStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectShort", EOrderAction.BuyToCover, OrderExit.FromAll));
            
            m_CloseLongNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "EmergCloseLong", EOrderAction.Sell, OrderExit.FromAll));
            m_CloseShortNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "EmergCloseShort", EOrderAction.BuyToCover, OrderExit.FromAll));
        }

        protected override void StartCalc()
        {
            m_BuyOrderActive = m_SellOrderActive = false;
            m_StopPrice = m_ProtectiveStopPrice = 0;
            m_LastMarketPosition = 0;
            m_OrderCreatedInMouseEvent = m_FlattenRequested = m_CancelRequested = false;
            m_LastBarIndex = -1;
            m_TrailingActive = m_LimitLossArmed = false;
            ClearTradingDrawings();
        }

        private void ClearTradingDrawings()
        {
            if (m_StopLine != null) m_StopLine.Delete();
            if (m_LabelHUD != null) m_LabelHUD.Delete();
            if (m_PriceLine != null) m_PriceLine.Delete();
        }

        protected override void CalcBar()
        {
            int currentPosition = StrategyInfo.MarketPosition;
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize == 0) tickSize = 0.25;

            // CAPTURE INITIAL STATE
            if (Bars.Status == EBarState.Close || m_AutoDetectedBrickSize <= 0)
            {
                m_LastClosePrice = Bars.Close[0]; 
                m_LastOpenPrice = Bars.Open[0];
                m_LastBarWasUp = (m_LastClosePrice > m_LastOpenPrice);
                m_LastBarIndex = Bars.CurrentBar;
                m_AutoDetectedBrickSize = Math.Abs(m_LastClosePrice - m_LastOpenPrice);

                if (currentPosition == 0 && (m_BuyOrderActive || m_SellOrderActive)) { 
                    m_BuyOrderActive = m_SellOrderActive = false; m_StopPrice = 0;
                    if (m_PriceLine != null) m_PriceLine.Delete();
                }
            }

            if (!Environment.IsRealTimeCalc) return;

            // DETECT FILL
            if (currentPosition != 0 && m_LastMarketPosition == 0)
            {
                double entry = StrategyInfo.AvgEntryPrice > 0 ? StrategyInfo.AvgEntryPrice : Bars.Close[0];
                if (currentPosition > 0) m_ProtectiveStopPrice = Math.Min(Bars.Low[0], Bars.Close[0]) - (StopTailOffsetTicks * tickSize);
                else m_ProtectiveStopPrice = Math.Max(Bars.High[0], Bars.Close[0]) + (StopTailOffsetTicks * tickSize);
                
                // Limit Loss Activation Threshold (1.5x Brick Size)
                double brickSize = (Level1 > 0) ? (Level1 * tickSize) : m_AutoDetectedBrickSize;
                if (brickSize <= 0) brickSize = 20 * tickSize; 
                double threshold = brickSize * 1.5; 
                if (currentPosition > 0) m_LimitLossTriggerPrice = entry + threshold;
                else m_LimitLossTriggerPrice = entry - threshold;
                m_LimitLossArmed = false;

                m_BuyOrderActive = m_SellOrderActive = false; m_StopPrice = 0; if (m_PriceLine != null) m_PriceLine.Delete();
                UpdateStopLine(); UpdateDollarHUD(entry, m_ProtectiveStopPrice);
            }

            // TREND TRAILING STOP
            if (Bars.Status == EBarState.Close && currentPosition != 0 && m_AutoDetectedBrickSize > 0)
            {
                double brickSize = (Level1 > 0) ? (Level1 * tickSize) : m_AutoDetectedBrickSize;
                double profitBarrier = 2 * brickSize; // 2 Bricks of profit required to activate trail
                double reversalDist = 2 * brickSize;  // 2 Bricks back for the stop
                double entry = StrategyInfo.AvgEntryPrice;

                // Check for Trail Activation
                if (!m_TrailingActive) {
                    if ((currentPosition > 0 && Bars.Close[0] >= entry + profitBarrier) || 
                        (currentPosition < 0 && Bars.Close[0] <= entry - profitBarrier)) { m_TrailingActive = true; }
                }

                if (m_TrailingActive) {
                    if (currentPosition > 0) {
                        double trailStop = Bars.Close[0] - reversalDist;
                        if (trailStop > m_ProtectiveStopPrice) { m_ProtectiveStopPrice = trailStop; UpdateStopLine(); }
                    } else if (currentPosition < 0) {
                        double trailStop = Bars.Close[0] + reversalDist;
                        if (m_ProtectiveStopPrice == 0 || trailStop < m_ProtectiveStopPrice) { m_ProtectiveStopPrice = trailStop; UpdateStopLine(); }
                    }
                }
            }

            // MOMENTUM FAILURE REVERSAL (Limit Loss)
            if (UseLimitLoss && currentPosition != 0)
            {
                if (!m_LimitLossArmed) {
                    if ((currentPosition > 0 && Bars.High[0] >= m_LimitLossTriggerPrice) || 
                        (currentPosition < 0 && Bars.Low[0] <= m_LimitLossTriggerPrice)) { m_LimitLossArmed = true; }
                } else if (Bars.Status == EBarState.Close) {
                    bool barIsUp = (Bars.Close[0] > Bars.Open[0]);
                    if ((currentPosition > 0 && !barIsUp) || (currentPosition < 0 && barIsUp)) { m_FlattenRequested = true; }
                }
            }

            // ORDER EXECUTION LOOP
            if (currentPosition > 0) {
                if (m_ProtectiveStopPrice > 0) m_BuyExitStop.Send(m_ProtectiveStopPrice);
            } else if (currentPosition < 0) {
                if (m_ProtectiveStopPrice > 0) m_SellExitStop.Send(m_ProtectiveStopPrice);
            }

            if (currentPosition != 0) { UpdateDollarHUD(StrategyInfo.AvgEntryPrice, m_ProtectiveStopPrice); }
            
            if (currentPosition == 0 && m_LastMarketPosition != 0) { 
                m_ProtectiveStopPrice = m_LimitLossTriggerPrice = 0; 
                m_LimitLossArmed = m_TrailingActive = false;
                ClearTradingDrawings(); 
            }
            m_LastMarketPosition = currentPosition;

            if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0) { ProcessManualOrderRequest(m_ClickPrice); m_OrderCreatedInMouseEvent = false; m_ClickPrice = 0; }
            if (m_CancelRequested) { m_BuyOrderActive = m_SellOrderActive = false; m_StopPrice = 0; if (m_PriceLine != null) m_PriceLine.Delete(); m_CancelRequested = false; }
            if (m_FlattenRequested) { if (currentPosition > 0) m_CloseLongNextBar.Send(); else if (currentPosition < 0) m_CloseShortNextBar.Send(); m_FlattenRequested = false; }

            if (currentPosition == 0) {
                if (m_BuyOrderActive && m_StopPrice > 0) m_BuyStop.Send(m_StopPrice, OrderQty);
                if (m_SellOrderActive && m_StopPrice > 0) m_SellStop.Send(m_StopPrice, OrderQty);
            }
        }

        private void UpdateDollarHUD(double entry, double stop)
        {
            if (!ShowHUD) return;
            if (m_LabelHUD != null) m_LabelHUD.Delete();
            double tickVal = (Bars.Info.PriceScale != 0) ? ((double)Bars.Info.MinMove / Bars.Info.PriceScale * Bars.Info.BigPointValue) : 0;
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize == 0) tickSize = 0.25; 
            double riskUSD = (Math.Abs(stop - entry) / tickSize) * tickVal * OrderQty;
            string text = string.Format("${0:F2}", riskUSD);
            m_LabelHUD = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.High[0]), text);
            m_LabelHUD.Color = Color.Red; m_LabelHUD.Size = 10;
            m_LabelHUD.Location = new ChartPoint(Bars.Time[0], Bars.High[0] + (10 * tickSize));
        }

        private void UpdateStopLine()
        {
            if (m_StopLine != null) m_StopLine.Delete();
            if (m_ProtectiveStopPrice <= 0) return;
            m_StopLine = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], m_ProtectiveStopPrice), new ChartPoint(Bars.Time[0].AddMinutes(5), m_ProtectiveStopPrice));
            m_StopLine.Color = Color.Red; m_StopLine.Style = ETLStyle.ToolDashed; m_StopLine.Size = 2; m_StopLine.ExtRight = true;
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            if (arg.buttons != MouseButtons.Left) return;
            bool ctrl = (arg.keys & Keys.Control) == Keys.Control;
            bool shift = (arg.keys & Keys.Shift) == Keys.Shift;
            if (shift) { if (StrategyInfo.MarketPosition != 0) m_FlattenRequested = true; m_CancelRequested = true; } 
            else if (ctrl) { m_ClickPrice = arg.point.Price; m_OrderCreatedInMouseEvent = true; }
        }

        private void ProcessManualOrderRequest(double clickPrice)
        {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize == 0) tickSize = 0.25; 
            double activeShift = (Level1 > 0) ? (Level1 * tickSize) : m_AutoDetectedBrickSize;
            if (activeShift <= 0) activeShift = 20 * tickSize;
            if (m_LastBarWasUp) {
                if (clickPrice >= m_LastClosePrice) { m_StopPrice = m_LastClosePrice + activeShift; m_BuyOrderActive = true; m_SellOrderActive = false; }
                else { m_StopPrice = m_LastOpenPrice - activeShift; m_SellOrderActive = true; m_BuyOrderActive = false; }
            } else {
                if (clickPrice <= m_LastClosePrice) { m_StopPrice = m_LastClosePrice - activeShift; m_SellOrderActive = true; m_BuyOrderActive = false; }
                else { m_StopPrice = m_LastOpenPrice + activeShift; m_BuyOrderActive = true; m_SellOrderActive = false; }
            }
            UpdateVisualMarker();
        }

        private void UpdateVisualMarker()
        {
            if (m_PriceLine != null) m_PriceLine.Delete();
            if (m_StopPrice <= 0) return;
            m_PriceLine = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], m_StopPrice), new ChartPoint(Bars.Time[0].AddMinutes(5), m_StopPrice));
            m_PriceLine.Color = m_BuyOrderActive ? Color.Cyan : Color.Magenta; m_PriceLine.Size = 2; m_PriceLine.Style = ETLStyle.ToolDashed; m_PriceLine.ExtRight = true;
        }

        protected override void Destroy() { ClearTradingDrawings(); }
    }
}
