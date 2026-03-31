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
    public class RangeBarTrading : SignalObject
    {
        [Input] public int OrderQty { get; set; }
        [Input] public int RangeSizeTicks { get; set; }
        [Input] public int ProfitTargetTicks { get; set; } // 0 = Auto-detect 1 Range Bar
        [Input] public int LimitOffsetTicks { get; set; }
        [Input] public int StopTailOffsetTicks { get; set; }
        [Input] public int ProximityTicks { get; set; }
        [Input] public bool ShowHUD { get; set; }
        [Input] public bool ShowGrid { get; set; }
        [Input] public int GridLinesCount { get; set; }

        private IOrderPriced m_BuyStop;
        private IOrderPriced m_SellStop;
        
        // Exits
        private IOrderPriced m_BuyExitStop;
        private IOrderPriced m_SellExitStop;
        private IOrderPriced m_BuyExitLimit;
        private IOrderPriced m_SellExitLimit;
        
        private IOrderMarket m_CloseLongNextBar;
        private IOrderMarket m_CloseShortNextBar;

        private double m_StopPrice = 0;
        private double m_LimitPrice = 0;
        private double m_ProtectiveStopPrice = 0;
        private double m_ProfitTargetPrice = 0;
        
        private int m_LastMarketPosition = 0;
        private bool m_BuyOrderActive = false;
        private bool m_SellOrderActive = false;
        private bool m_CancelRequested = false;
        private bool m_FlattenRequested = false;
        private bool m_DraggingTarget = false;
        private bool m_DraggingStop = false;
        
        private ITrendLineObject m_PriceLine;
        private ITrendLineObject m_TargetLine;
        private ITrendLineObject m_StopLine;
        private List<ITrendLineObject> m_GridLines = new List<ITrendLineObject>();
        private ITextObject m_HUDLabel;

        public RangeBarTrading(object ctx) : base(ctx)
        {
            OrderQty = 1;
            RangeSizeTicks = 7;
            ProfitTargetTicks = 0; // 0 = Auto-detect 1 Range Bar
            LimitOffsetTicks = 1;
            StopTailOffsetTicks = 2;
            ProximityTicks = 5;
            ShowHUD = true;
            ShowGrid = true;
            GridLinesCount = 5;
        }

        protected override void Create()
        {
            m_BuyStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "RangeBuy", EOrderAction.Buy));
            m_SellStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "RangeSell", EOrderAction.SellShort));
            
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
            m_StopPrice = 0;
            m_LimitPrice = 0;
            m_ProtectiveStopPrice = 0;
            m_ProfitTargetPrice = 0;
            m_LastMarketPosition = 0;
            m_CancelRequested = false;
            m_FlattenRequested = false;
            m_DraggingTarget = false;
            m_DraggingStop = false;
            if (m_HUDLabel != null) m_HUDLabel.Delete();
            if (m_PriceLine != null) m_PriceLine.Delete();
            if (m_TargetLine != null) m_TargetLine.Delete();
            if (m_StopLine != null) m_StopLine.Delete();
            ClearGrid();
        }

        protected override void CalcBar()
        {
            if (!Environment.IsRealTimeCalc) return;

            int currentPosition = StrategyInfo.MarketPosition;
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize == 0) tickSize = 0.25; // Safety fallback

            if (m_FlattenRequested && currentPosition != 0)
            {
                if (currentPosition > 0) m_CloseLongNextBar.Send(Math.Abs(currentPosition));
                else m_CloseShortNextBar.Send(Math.Abs(currentPosition));
                m_ProtectiveStopPrice = 0;
                m_ProfitTargetPrice = 0;
            }
            else if (m_FlattenRequested && currentPosition == 0) m_FlattenRequested = false;

            // DETECT NEW FILL
            if (currentPosition != 0 && m_LastMarketPosition == 0)
            {
                double entryPrice = StrategyInfo.AvgEntryPrice;
                if (entryPrice == 0) entryPrice = Bars.Close[0]; // Fallback for IOG timing

                double targetDistance = ProfitTargetTicks > 0 ? (ProfitTargetTicks * tickSize) : Math.Abs(Bars.High[1] - Bars.Low[1]);
                if (targetDistance == 0) targetDistance = RangeSizeTicks * tickSize;

                if (currentPosition > 0)
                {
                    double lowestTail = Math.Min(Bars.Low[0], (Bars.StatusLine.Ask > 0 ? Bars.StatusLine.Ask : Bars.Close[0]));
                    m_ProtectiveStopPrice = lowestTail - (StopTailOffsetTicks * tickSize);
                    m_ProfitTargetPrice = entryPrice + targetDistance;
                }
                else
                {
                    double highestTail = Math.Max(Bars.High[0], (Bars.StatusLine.Bid > 0 ? Bars.StatusLine.Bid : Bars.Close[0]));
                    m_ProtectiveStopPrice = highestTail + (StopTailOffsetTicks * tickSize);
                    m_ProfitTargetPrice = entryPrice - targetDistance;
                }
                
                m_BuyOrderActive = m_SellOrderActive = false;
                m_StopPrice = 0;
                if (m_PriceLine != null) m_PriceLine.Delete();
                UpdateTargetLine();
                UpdateStopLine();
                if (ShowGrid) UpdateGridLines(entryPrice);
                Output.WriteLine("📊 RANGE SYSTEM: Trade Active. Entry: {0} | Target: {1}", entryPrice, m_ProfitTargetPrice);
            }

            // Entry logic (only if not in trade)
            if (currentPosition == 0 && !m_CancelRequested)
            {
                if (m_BuyOrderActive)
                {
                    double currentLow = Math.Min(Bars.Low[0], (Bars.StatusLine.Ask > 0 ? Bars.StatusLine.Ask : Bars.Close[0]));
                    m_StopPrice = Math.Round((currentLow + (RangeSizeTicks * tickSize)) / tickSize) * tickSize;
                    m_LimitPrice = m_StopPrice + (LimitOffsetTicks * tickSize);
                    m_BuyStop.Send(m_StopPrice, OrderQty);
                    UpdateVisualMarker();
                }
                else if (m_SellOrderActive)
                {
                    double currentHigh = Math.Max(Bars.High[0], (Bars.StatusLine.Bid > 0 ? Bars.StatusLine.Bid : Bars.Close[0]));
                    m_StopPrice = Math.Round((currentHigh - (RangeSizeTicks * tickSize)) / tickSize) * tickSize;
                    m_LimitPrice = m_StopPrice - (LimitOffsetTicks * tickSize);
                    m_SellStop.Send(m_StopPrice, OrderQty);
                    UpdateVisualMarker();
                }
            }

            // Exits
            if (currentPosition > 0)
            {
                if (m_ProtectiveStopPrice > 0) m_BuyExitStop.Send(m_ProtectiveStopPrice);
                if (m_ProfitTargetPrice > 0) m_BuyExitLimit.Send(m_ProfitTargetPrice);
            }
            else if (currentPosition < 0)
            {
                if (m_ProtectiveStopPrice > 0) m_SellExitStop.Send(m_ProtectiveStopPrice);
                if (m_ProfitTargetPrice > 0) m_SellExitLimit.Send(m_ProfitTargetPrice);
            }

            if (currentPosition == 0 && m_LastMarketPosition != 0)
            {
                m_ProtectiveStopPrice = 0;
                m_ProfitTargetPrice = 0;
                m_BuyOrderActive = m_SellOrderActive = false;
                if (m_TargetLine != null) m_TargetLine.Delete();
                if (m_PriceLine != null) m_PriceLine.Delete();
                if (m_StopLine != null) m_StopLine.Delete();
                ClearGrid();
                Output.WriteLine("📊 RANGE SYSTEM: Trade Closed. All orders cleared.");
            }

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
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize == 0) tickSize = 0.25;

            if (m_DraggingTarget) { m_ProfitTargetPrice = Math.Round(arg.point.Price / tickSize) * tickSize; m_DraggingTarget = false; UpdateTargetLine(); return; }
            if (m_DraggingStop) { m_ProtectiveStopPrice = Math.Round(arg.point.Price / tickSize) * tickSize; m_DraggingStop = false; UpdateStopLine(); return; }

            if (m_ProfitTargetPrice > 0 && Math.Abs(arg.point.Price - m_ProfitTargetPrice) <= (ProximityTicks * tickSize)) { m_DraggingTarget = true; UpdateTargetLine(); return; }
            if (m_ProtectiveStopPrice > 0 && Math.Abs(arg.point.Price - m_ProtectiveStopPrice) <= (ProximityTicks * tickSize)) { m_DraggingStop = true; UpdateStopLine(); return; }

            if (ctrl)
            {
                double clickPrice = arg.point.Price;
                double currentPrice = Bars.Close[0];
                if (clickPrice > currentPrice) { m_BuyOrderActive = true; m_SellOrderActive = false; }
                else { m_SellOrderActive = true; m_BuyOrderActive = false; }
            }
            else if (shift) { m_CancelRequested = true; if (StrategyInfo.MarketPosition != 0) m_FlattenRequested = true; }
        }

        private void ClearGrid() { foreach (var line in m_GridLines) if (line != null) line.Delete(); m_GridLines.Clear(); }

        private void UpdateGridLines(double entryPrice)
        {
            ClearGrid();
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize == 0) tickSize = 0.25;
            
            double targetDistance = ProfitTargetTicks > 0 ? (ProfitTargetTicks * tickSize) : Math.Abs(Bars.High[1] - Bars.Low[1]);
            double stepSize = targetDistance > 0 ? targetDistance : (RangeSizeTicks * tickSize);

            for (int i = 1; i <= GridLinesCount; i++)
            {
                double upPrice = entryPrice + (stepSize * i);
                var upL = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], upPrice), new ChartPoint(Bars.Time[0].AddMinutes(5), upPrice));
                upL.Color = Color.Black; upL.Style = ETLStyle.ToolDashed; upL.Size = 1; upL.ExtLeft = upL.ExtRight = true;
                m_GridLines.Add(upL);

                double dnPrice = entryPrice - (stepSize * i);
                var dnL = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], dnPrice), new ChartPoint(Bars.Time[0].AddMinutes(5), dnPrice));
                dnL.Color = Color.Black; dnL.Style = ETLStyle.ToolDashed; dnL.Size = 1; dnL.ExtLeft = dnL.ExtRight = true;
                m_GridLines.Add(dnL);
            }
        }

        private void UpdateVisualMarker() {
            if (!m_BuyOrderActive && !m_SellOrderActive) { if (m_PriceLine != null) m_PriceLine.Delete(); return; }
            if (m_PriceLine != null) m_PriceLine.Delete();
            m_PriceLine = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], m_StopPrice), new ChartPoint(Bars.Time[0].AddMinutes(5), m_StopPrice));
            m_PriceLine.Color = m_BuyOrderActive ? Color.Cyan : Color.Magenta;
            m_PriceLine.Style = ETLStyle.ToolDashed; m_PriceLine.Size = 2; m_PriceLine.ExtRight = m_PriceLine.ExtLeft = true;
        }

        private void UpdateTargetLine() {
            if (m_TargetLine != null) m_TargetLine.Delete();
            if (m_ProfitTargetPrice <= 0) return;
            m_TargetLine = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], m_ProfitTargetPrice), new ChartPoint(Bars.Time[0].AddMinutes(5), m_ProfitTargetPrice));
            m_TargetLine.Color = m_DraggingTarget ? Color.White : Color.Gold;
            m_TargetLine.Style = ETLStyle.ToolDashed; m_TargetLine.Size = 2; m_TargetLine.ExtRight = m_TargetLine.ExtLeft = true;
        }

        private void UpdateStopLine() {
            if (m_StopLine != null) m_StopLine.Delete();
            if (m_ProtectiveStopPrice <= 0) return;
            m_StopLine = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], m_ProtectiveStopPrice), new ChartPoint(Bars.Time[0].AddMinutes(5), m_ProtectiveStopPrice));
            m_StopLine.Color = m_DraggingStop ? Color.White : Color.Red;
            m_StopLine.Style = ETLStyle.ToolDashed; m_StopLine.Size = 2; m_StopLine.ExtRight = m_StopLine.ExtLeft = true;
        }

        private void UpdateHUD() {
            double pnl = StrategyInfo.ClosedEquity;
            string status = "IDLE";
            if (m_BuyOrderActive) status = "CHASING BUY";
            if (m_SellOrderActive) status = "CHASING SELL";
            if (StrategyInfo.MarketPosition != 0) status = "IN TRADE";
            string text = string.Format("RANGE TRADER | {0} | PnL: {1:C2}", status, pnl);
            if (m_HUDLabel == null) { m_HUDLabel = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.High[0]), text); m_HUDLabel.Size = 14; }
            m_HUDLabel.Color = pnl >= 0 ? Color.LimeGreen : Color.Tomato;
            m_HUDLabel.Location = new ChartPoint(Bars.Time[0], Bars.High[0]);
        }

        protected override void Destroy() { if (m_HUDLabel != null) m_HUDLabel.Delete(); if (m_PriceLine != null) m_PriceLine.Delete(); if (m_TargetLine != null) m_TargetLine.Delete(); if (m_StopLine != null) m_StopLine.Delete(); ClearGrid(); }
    }
}
