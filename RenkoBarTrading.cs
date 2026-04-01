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
        [Input] public int ProfitTargetTicks { get; set; } // 0 = Mirror Stop Distance (1:1 Risk/Reward)
        [Input] public int LimitOffsetTicks { get; set; }
        [Input] public int StopTailOffsetTicks { get; set; }
        [Input] public bool ShowPriceLine { get; set; }
        [Input] public int ProximityTicks { get; set; }
        [Input] public bool ShowHUD { get; set; }

        private IOrderPriced m_BuyStop;
        private IOrderPriced m_SellStop;
        
        // Exits
        private IOrderPriced m_BuyExitStop;
        private IOrderPriced m_SellExitStop;
        private IOrderPriced m_BuyExitLimit;
        private IOrderPriced m_SellExitLimit;
        
        private IOrderMarket m_CloseLongNextBar;
        private IOrderMarket m_CloseShortNextBar;
        
        private double m_LastClosePrice = 0;
        private double m_LastOpenPrice = 0;
        private bool m_LastBarWasUp = true;
        private int m_LastBarIndex = -1;
        private double m_AutoDetectedBrickSize = 0;
        
        private double m_StopPrice = 0;
        private double m_ProtectiveStopPrice = 0;
        private double m_ProfitTargetPrice = 0;
        
        private int m_LastMarketPosition = 0;
        private bool m_BuyOrderActive = false;
        private bool m_SellOrderActive = false;
        private bool m_OrderCreatedInMouseEvent = false;
        private bool m_FlattenRequested = false;
        private bool m_CancelRequested = false;
        private double m_ClickPrice = 0;
        
        private ITrendLineObject m_PriceLine;
        private ITrendLineObject m_TargetLine;
        private ITrendLineObject m_StopLine;
        private ITextObject m_LabelHUD;

        public RenkoBarTrading(object ctx) : base(ctx)
        {
            OrderQty = 1;
            Level1 = 0;
            ProfitTargetTicks = 0;
            LimitOffsetTicks = 1;
            StopTailOffsetTicks = 2;
            ShowPriceLine = true;
            ProximityTicks = 5;
            ShowHUD = true;
        }

        protected override void Create()
        {
            m_BuyStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ManualBuy", EOrderAction.Buy));
            m_SellStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ManualSell", EOrderAction.SellShort));
            
            m_BuyExitStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectLong", EOrderAction.Sell, OrderExit.FromAll));
            m_SellExitStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectShort", EOrderAction.BuyToCover, OrderExit.FromAll));
            
            m_BuyExitLimit = OrderCreator.Limit(new SOrderParameters(Contracts.Default, "ProfitLong", EOrderAction.Sell, OrderExit.FromAll));
            m_SellExitLimit = OrderCreator.Limit(new SOrderParameters(Contracts.Default, "ProfitShort", EOrderAction.BuyToCover, OrderExit.FromAll));
            
            m_CloseLongNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "EmergCloseLong", EOrderAction.Sell, OrderExit.FromAll));
            m_CloseShortNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "EmergCloseShort", EOrderAction.BuyToCover, OrderExit.FromAll));
        }

        protected override void StartCalc()
        {
            m_BuyOrderActive = m_SellOrderActive = false;
            m_StopPrice = m_ProtectiveStopPrice = m_ProfitTargetPrice = 0;
            m_LastMarketPosition = 0;
            m_OrderCreatedInMouseEvent = m_FlattenRequested = m_CancelRequested = false;
            m_LastBarIndex = -1;
            ClearTradingDrawings();
        }

        private void ClearTradingDrawings()
        {
            if (m_TargetLine != null) m_TargetLine.Delete();
            if (m_StopLine != null) m_StopLine.Delete();
            if (m_LabelHUD != null) m_LabelHUD.Delete();
            if (m_PriceLine != null) m_PriceLine.Delete();
        }

        protected override void CalcBar()
        {
            int MP = StrategyInfo.MarketPosition;
            double tickSize = Bars.Info.TickSize;

            // HISTORICAL/BAR CLOSE LOGIC
            if (Bars.Status == EBarState.Close)
            {
                m_LastClosePrice = Bars.Close[0]; 
                m_LastOpenPrice = Bars.Open[0];
                m_LastBarWasUp = (m_LastClosePrice > m_LastOpenPrice);
                m_LastBarIndex = Bars.CurrentBar;
                m_AutoDetectedBrickSize = Math.Abs(m_LastClosePrice - m_LastOpenPrice);

                if (MP == 0) { 
                    m_BuyOrderActive = m_SellOrderActive = false; m_StopPrice = 0;
                    if (m_PriceLine != null) m_PriceLine.Delete();
                }
            }

            if (!Environment.IsRealTimeCalc) return;

            // DETECT FILL
            if (MP != 0 && m_LastMarketPosition == 0)
            {
                double entry = StrategyInfo.AvgEntryPrice > 0 ? StrategyInfo.AvgEntryPrice : Bars.Close[0];
                
                if (MP > 0) {
                    double lowestTail = Math.Min(Bars.Low[0], Bars.Close[0]);
                    m_ProtectiveStopPrice = lowestTail - (StopTailOffsetTicks * tickSize);
                } else {
                    double highestTail = Math.Max(Bars.High[0], Bars.Close[0]);
                    m_ProtectiveStopPrice = highestTail + (StopTailOffsetTicks * tickSize);
                }

                double stopDist = Math.Abs(entry - m_ProtectiveStopPrice);
                double targetDist = (ProfitTargetTicks > 0) ? (ProfitTargetTicks * tickSize) : stopDist;

                if (MP > 0) m_ProfitTargetPrice = entry + targetDist;
                else m_ProfitTargetPrice = entry - targetDist;

                m_BuyOrderActive = m_SellOrderActive = false; m_StopPrice = 0;
                if (m_PriceLine != null) m_PriceLine.Delete();
                
                // Draw initial state
                UpdateTargetLine(); UpdateStopLine(); UpdateDollarHUD(entry, m_ProfitTargetPrice, m_ProtectiveStopPrice);
            }

            // ORDER SUBMISSION (ONLY SEND IF PRICE IS VALID)
            if (MP > 0) {
                if (m_ProtectiveStopPrice > 0) m_BuyExitStop.Send(m_ProtectiveStopPrice);
                if (m_ProfitTargetPrice > 0) m_BuyExitLimit.Send(m_ProfitTargetPrice);
            } else if (MP < 0) {
                if (m_ProtectiveStopPrice > 0) m_SellExitStop.Send(m_ProtectiveStopPrice);
                if (m_ProfitTargetPrice > 0) m_SellExitLimit.Send(m_ProfitTargetPrice);
            }

            // REFRESH VISUALS IF MP CHANGED OR LINES ARE ACTIVE
            if (MP != 0) {
                UpdateTargetLine(); UpdateStopLine();
                UpdateDollarHUD(StrategyInfo.AvgEntryPrice, m_ProfitTargetPrice, m_ProtectiveStopPrice);
            }

            if (MP == 0 && m_LastMarketPosition != 0) { m_ProtectiveStopPrice = m_ProfitTargetPrice = 0; ClearTradingDrawings(); }
            m_LastMarketPosition = MP;

            // HOTKEY HANDLING
            if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0) { ProcessManualOrderRequest(m_ClickPrice); m_OrderCreatedInMouseEvent = false; m_ClickPrice = 0; }
            if (!m_CancelRequested && MP == 0) {
                if (m_BuyOrderActive && m_StopPrice > 0) m_BuyStop.Send(m_StopPrice, OrderQty);
                if (m_SellOrderActive && m_StopPrice > 0) m_SellStop.Send(m_StopPrice, OrderQty);
            }
        }

        private void UpdateDollarHUD(double entry, double target, double stop)
        {
            if (!ShowHUD) return;
            if (m_LabelHUD != null) m_LabelHUD.Delete();
            double tickVal = (Bars.Info.PriceScale != 0) ? ((double)Bars.Info.MinMove / Bars.Info.PriceScale * Bars.Info.BigPointValue) : 0;
            double tickSize = Bars.Info.TickSize;
            double profitUSD = (Math.Abs(target - entry) / tickSize) * tickVal * OrderQty;
            double riskUSD = (Math.Abs(stop - entry) / tickSize) * tickVal * OrderQty;
            string text = string.Format("PROFIT: +{0:C2}\nRISK: -{1:C2}", profitUSD, riskUSD);
            m_LabelHUD = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.High[0]), text);
            m_LabelHUD.Color = Color.White; m_LabelHUD.Size = 14; 
            m_LabelHUD.Location = new ChartPoint(Bars.Time[0], Bars.High[0] + (25 * tickSize));
        }

        private void UpdateTargetLine()
        {
            if (m_TargetLine != null) m_TargetLine.Delete();
            if (m_ProfitTargetPrice <= 0) return;
            m_TargetLine = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], m_ProfitTargetPrice), new ChartPoint(Bars.Time[0].AddMinutes(5), m_ProfitTargetPrice));
            m_TargetLine.Color = Color.Gold; m_TargetLine.Style = ETLStyle.ToolDashed; m_TargetLine.Size = 2; m_TargetLine.ExtRight = true;
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
            double tickSize = Bars.Info.TickSize;
            if (m_DraggingTarget) { m_ProfitTargetPrice = Math.Round(arg.point.Price / tickSize) * tickSize; m_DraggingTarget = false; return; }
            if (m_DraggingStop) { m_ProtectiveStopPrice = Math.Round(arg.point.Price / tickSize) * tickSize; m_DraggingStop = false; return; }
            if (m_ProfitTargetPrice > 0 && Math.Abs(arg.point.Price - m_ProfitTargetPrice) <= (5 * tickSize)) { m_DraggingTarget = true; return; }
            if (m_ProtectiveStopPrice > 0 && Math.Abs(arg.point.Price - m_ProtectiveStopPrice) <= (5 * tickSize)) { m_DraggingStop = true; return; }
            if (ctrl) { m_ClickPrice = arg.point.Price; m_OrderCreatedInMouseEvent = true; }
        }

        private bool m_DraggingTarget = false;
        private bool m_DraggingStop = false;

        private void ProcessManualOrderRequest(double clickPrice)
        {
            double tickSize = Bars.Info.TickSize;
            double activeShift = (Level1 > 0) ? (Level1 * tickSize) : m_AutoDetectedBrickSize;
            if (activeShift <= 0) activeShift = 20 * tickSize;
            if (clickPrice > m_LastOpenPrice) { m_StopPrice = m_LastClosePrice + activeShift; m_BuyOrderActive = true; m_SellOrderActive = false; }
            else { m_StopPrice = m_LastOpenPrice - activeShift; m_SellOrderActive = true; m_BuyOrderActive = false; }
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
