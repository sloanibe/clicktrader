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
        private double m_LimitPrice = 0;
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
        private ITextObject m_LabelHUD; // Consolidated HUD for Dollar Values

        public RenkoBarTrading(object ctx) : base(ctx)
        {
            OrderQty = 1;
            Level1 = 0; // Default to Auto-Detect
            ProfitTargetTicks = 0; // 0 = Mirror Stop Distance (1:1 Risk/Reward)
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
            
            // Protective Stop MUST remain a STOP order
            m_BuyExitStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectLong", EOrderAction.Sell, OrderExit.FromAll));
            m_SellExitStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectShort", EOrderAction.BuyToCover, OrderExit.FromAll));
            
            // Profit Target MUST remain a LIMIT order (even if we label it 'Stop' in name, the type must be Limit)
            m_BuyExitLimit = OrderCreator.Limit(new SOrderParameters(Contracts.Default, "ProfitLong", EOrderAction.Sell, OrderExit.FromAll));
            m_SellExitLimit = OrderCreator.Limit(new SOrderParameters(Contracts.Default, "ProfitShort", EOrderAction.BuyToCover, OrderExit.FromAll));
            
            m_CloseLongNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "EmergCloseLong", EOrderAction.Sell, OrderExit.FromAll));
            m_CloseShortNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "EmergCloseShort", EOrderAction.BuyToCover, OrderExit.FromAll));
        }

        protected override void StartCalc()
        {
            m_BuyOrderActive = m_SellOrderActive = false;
            m_StopPrice = m_LimitPrice = m_ProtectiveStopPrice = m_ProfitTargetPrice = 0;
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
        }

        protected override void CalcBar()
        {
            int currentPosition = StrategyInfo.MarketPosition;
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize == 0) tickSize = 0.25;

            // Sync with bar closes
            if (Bars.Status == EBarState.Close)
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

            // Flatten logic
            if (m_FlattenRequested && currentPosition != 0) {
                int qty = Math.Abs(currentPosition);
                if (currentPosition > 0) m_CloseLongNextBar.Send(qty);
                else m_CloseShortNextBar.Send(qty);
                m_ProtectiveStopPrice = m_ProfitTargetPrice = 0;
            } else if (m_FlattenRequested && currentPosition == 0) { m_FlattenRequested = false; }

            // Fill detection
            if (currentPosition != 0 && m_LastMarketPosition == 0)
            {
                double entry = StrategyInfo.AvgEntryPrice > 0 ? StrategyInfo.AvgEntryPrice : Bars.Close[0];
                
                if (currentPosition > 0) m_ProtectiveStopPrice = Math.Min(Bars.Low[0], Bars.Close[0]) - (StopTailOffsetTicks * tickSize);
                else m_ProtectiveStopPrice = Math.Max(Bars.High[0], Bars.Close[0]) + (StopTailOffsetTicks * tickSize);

                double stopDist = Math.Abs(entry - m_ProtectiveStopPrice);
                double targetDist = (ProfitTargetTicks > 0) ? (ProfitTargetTicks * tickSize) : stopDist;

                if (currentPosition > 0) m_ProfitTargetPrice = entry + targetDist;
                else m_ProfitTargetPrice = entry - targetDist;

                m_BuyOrderActive = m_SellOrderActive = false;
                UpdateTargetLine(); UpdateStopLine(); UpdateDollarHUD(entry, m_ProfitTargetPrice, m_ProtectiveStopPrice);
            }

            // Order Execution (RESTORED LIMIT TYPE FOR TARGET)
            if (currentPosition > 0) {
                if (m_ProtectiveStopPrice > 0) m_BuyExitStop.Send(m_ProtectiveStopPrice);
                if (m_ProfitTargetPrice > 0) m_BuyExitLimit.Send(m_ProfitTargetPrice);
            } else if (currentPosition < 0) {
                if (m_ProtectiveStopPrice > 0) m_SellExitStop.Send(m_ProtectiveStopPrice);
                if (m_ProfitTargetPrice > 0) m_SellExitLimit.Send(m_ProfitTargetPrice);
            }

            if (currentPosition == 0 && m_LastMarketPosition != 0) { m_ProtectiveStopPrice = m_ProfitTargetPrice = 0; ClearTradingDrawings(); }
            m_LastMarketPosition = currentPosition;

            if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0) { ProcessManualOrderRequest(m_ClickPrice); m_OrderCreatedInMouseEvent = false; m_ClickPrice = 0; }
            if (!m_CancelRequested && currentPosition == 0) {
                if (m_BuyOrderActive && m_StopPrice > 0) m_BuyStop.Send(m_StopPrice, OrderQty);
                else if (m_SellOrderActive && m_StopPrice > 0) m_SellStop.Send(m_StopPrice, OrderQty);
            }
        }

        private void UpdateDollarHUD(double entry, double target, double stop)
        {
            if (m_LabelHUD != null) m_LabelHUD.Delete();
            double tickVal = (Bars.Info.PriceScale != 0) ? ((double)Bars.Info.MinMove / Bars.Info.PriceScale * Bars.Info.BigPointValue) : 0;
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize == 0) tickSize = 0.25;
            double profitUSD = (Math.Abs(target - entry) / tickSize) * tickVal * OrderQty;
            double riskUSD = (Math.Abs(stop - entry) / tickSize) * tickVal * OrderQty;
            string text = string.Format("PROFIT: +{0:C2}\nRISK: -{1:C2}", profitUSD, riskUSD);
            m_LabelHUD = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.High[0] + (10 * tickSize)), text);
            m_LabelHUD.Color = Color.White; m_LabelHUD.Size = 12;
            m_LabelHUD.Location = new ChartPoint(Bars.Time[0], Bars.High[0] + (10 * tickSize));
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

        private bool m_DraggingTarget = false;
        private bool m_DraggingStop = false;

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            if (arg.buttons != MouseButtons.Left) return;
            bool ctrl = (arg.keys & Keys.Control) == Keys.Control;
            bool shift = (arg.keys & Keys.Shift) == Keys.Shift;
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize == 0) tickSize = 0.25;

            if (m_DraggingTarget) {
                m_ProfitTargetPrice = Math.Round(arg.point.Price / tickSize) * tickSize;
                m_DraggingTarget = false; UpdateTargetLine(); 
                UpdateDollarHUD(StrategyInfo.AvgEntryPrice > 0 ? StrategyInfo.AvgEntryPrice : Bars.Close[0], m_ProfitTargetPrice, m_ProtectiveStopPrice);
                return;
            }
            if (m_DraggingStop) {
                m_ProtectiveStopPrice = Math.Round(arg.point.Price / tickSize) * tickSize;
                m_DraggingStop = false; UpdateStopLine();
                UpdateDollarHUD(StrategyInfo.AvgEntryPrice > 0 ? StrategyInfo.AvgEntryPrice : Bars.Close[0], m_ProfitTargetPrice, m_ProtectiveStopPrice);
                return;
            }
            if (m_ProfitTargetPrice > 0 && Math.Abs(arg.point.Price - m_ProfitTargetPrice) <= (ProximityTicks * tickSize)) { m_DraggingTarget = true; UpdateTargetLine(); return; }
            if (m_ProtectiveStopPrice > 0 && Math.Abs(arg.point.Price - m_ProtectiveStopPrice) <= (ProximityTicks * tickSize)) { m_DraggingStop = true; UpdateStopLine(); return; }
            
            if (ctrl) { m_ClickPrice = arg.point.Price; m_OrderCreatedInMouseEvent = true; }
            else if (shift) { m_CancelRequested = true; if (StrategyInfo.MarketPosition != 0) m_FlattenRequested = true; }
        }

        private void ProcessManualOrderRequest(double clickPrice)
        {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize == 0) tickSize = 0.25;
            double activeShift = (Level1 > 0) ? (Level1 * tickSize) : m_AutoDetectedBrickSize;
            if (activeShift <= 0) activeShift = 20 * tickSize;
            if (clickPrice > m_LastClosePrice) { m_StopPrice = m_LastClosePrice + activeShift; m_BuyOrderActive = true; m_SellOrderActive = false; }
            else { m_StopPrice = m_LastOpenPrice - activeShift; m_SellOrderActive = true; m_BuyOrderActive = false; }
            UpdateVisualMarker();
        }

        private void UpdateVisualMarker()
        {
            if (m_PriceLine != null) m_PriceLine.Delete();
            m_PriceLine = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], m_StopPrice), new ChartPoint(Bars.Time[0].AddMinutes(5), m_StopPrice));
            m_PriceLine.Color = m_BuyOrderActive ? Color.Cyan : Color.Magenta; m_PriceLine.Style = ETLStyle.ToolDashed; m_PriceLine.Size = 2; m_PriceLine.ExtRight = true;
        }

        protected override void Destroy() { ClearTradingDrawings(); }
    }
}
