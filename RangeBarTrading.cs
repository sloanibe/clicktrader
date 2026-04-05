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
    public class RangeBarTradingV3 : SignalObject
    {
        [Input] public int OrderQtyTier1 { get; set; }
        [Input] public int OrderQtyTier2 { get; set; }
        [Input] public int RangeSizeTicks { get; set; } // 0 = Auto-detect
        [Input] public int ProfitTargetTicksTier1 { get; set; } 
        [Input] public int ProfitTargetTicksTier2 { get; set; } 
        [Input] public int StopTailOffsetTicks { get; set; }
        [Input] public int ProximityTicks { get; set; }
        [Input] public int EntryOffsetTicks { get; set; }
        [Input] public bool ShowHUD { get; set; }
        [Input] public int MasterTrendPeriod { get; set; }
        [Input] public int MinExpansionTicks { get; set; }
        [Input] public int MinBreadth_15_60 { get; set; }
        [Input] public int MinBreadth_5_15 { get; set; }
        [Input] public double MinSlopeTicks { get; set; }
        [Input] public int TrendRecencyBars { get; set; }

        private IOrderPriced m_BuyStop;
        private IOrderPriced m_SellStop;
        private IOrderPriced m_BuyExitStop;
        private IOrderPriced m_SellExitStop;
        private IOrderPriced m_BuyExitLimit;
        private IOrderPriced m_SellExitLimit;
        private IOrderMarket m_CloseLongNextBar;
        private IOrderMarket m_CloseShortNextBar;

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_MasterEMA;

        private double m_StopPrice = 0;
        private double m_ProtectiveStopPrice = 0;
        private double m_ProfitTargetPrice = 0;
        private double m_LastSentPrice = 0;
        
        private int m_LastMarketPosition = 0;
        private int m_CurrentTradeTier = 1;

        private bool m_BuyOrderActive = false;
        private bool m_SellOrderActive = false;
        private bool m_OrderCreatedInMouseEvent = false;
        private double m_ClickPrice = 0;
        private bool m_CancelRequested = false;
        private bool m_FlattenRequested = false;
        private bool m_DraggingTarget = false;
        private bool m_DraggingStop = false;
        private double m_AutoRangeTicks = 0;
        
        private ITrendLineObject m_TargetLine;
        private ITrendLineObject m_StopLine;
        private ITrendLineObject m_GoSignalMarker;
        private ITextObject m_HUDLabel;

        public RangeBarTradingV3(object ctx) : base(ctx)
        {
            OrderQtyTier1 = 1; OrderQtyTier2 = 1; 
            RangeSizeTicks = 0; 
            ProfitTargetTicksTier1 = 10; ProfitTargetTicksTier2 = 15; 
            StopTailOffsetTicks = 2; ProximityTicks = 5; EntryOffsetTicks = 1;
            ShowHUD = true; MasterTrendPeriod = 60;
            MinBreadth_15_60 = 5; MinBreadth_5_15 = 4; MinSlopeTicks = 1.0; 
            MinExpansionTicks = 25; TrendRecencyBars = 30; 
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this); m_SlowEMA = new XAverage(this); m_MasterEMA = new XAverage(this);
            m_BuyStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "RangeBuy", EOrderAction.Buy));
            m_SellStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "RangeSell", EOrderAction.SellShort));
            m_BuyExitStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectLong", EOrderAction.Sell, OrderExit.FromAll));
            m_SellExitStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectShort", EOrderAction.BuyToCover, OrderExit.FromAll));
            m_BuyExitLimit = OrderCreator.Limit(new SOrderParameters(Contracts.Default, "ProfitLong", EOrderAction.Sell, OrderExit.FromAll));
            m_SellExitLimit = OrderCreator.Limit(new SOrderParameters(Contracts.Default, "ProfitShort", EOrderAction.BuyToCover, OrderExit.FromAll));
            m_CloseLongNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "EmergLong", EOrderAction.Sell, OrderExit.FromAll));
            m_CloseShortNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "EmergShort", EOrderAction.BuyToCover, OrderExit.FromAll));
        }

        protected override void StartCalc()
        {
            m_FastEMA.Length = 5; m_FastEMA.Price = Bars.Close;
            m_SlowEMA.Length = 15; m_SlowEMA.Price = Bars.Close;
            m_MasterEMA.Length = MasterTrendPeriod; m_MasterEMA.Price = Bars.Close;
            m_BuyOrderActive = m_SellOrderActive = false; m_StopPrice = m_ProtectiveStopPrice = m_ProfitTargetPrice = m_LastSentPrice = 0;
            ClearTradingDrawings();
        }

        private void ClearTradingDrawings() {
            if (m_HUDLabel != null) m_HUDLabel.Delete(); if (m_TargetLine != null) m_TargetLine.Delete();
            if (m_StopLine != null) m_StopLine.Delete(); if (m_GoSignalMarker != null) m_GoSignalMarker.Delete();
        }

        protected override void CalcBar()
        {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;

            if (Bars.Status == EBarState.Close || m_AutoRangeTicks <= 0) m_AutoRangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            if (!Environment.IsRealTimeCalc) return;

            int currentPosition = StrategyInfo.MarketPosition;
            if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0) {
                m_CurrentTradeTier = 1; 
                if (m_ClickPrice >= Bars.High[0]) { m_BuyOrderActive = true; m_SellOrderActive = false; }
                else if (m_ClickPrice <= Bars.Low[0]) { m_SellOrderActive = true; m_BuyOrderActive = false; }
                m_OrderCreatedInMouseEvent = false; m_ClickPrice = 0;
            }

            if (currentPosition == 0 && !m_BuyOrderActive && !m_SellOrderActive && !m_CancelRequested) CheckForHiddenPierceSignals(tickSize);

            if (m_FlattenRequested && currentPosition != 0) { if (currentPosition > 0) m_CloseLongNextBar.Send(); else m_CloseShortNextBar.Send(); m_ProtectiveStopPrice = m_ProfitTargetPrice = 0; }
            else if (m_FlattenRequested && currentPosition == 0) m_FlattenRequested = false;

            if (currentPosition != 0 && m_LastMarketPosition == 0) {
                double entryPrice = StrategyInfo.AvgEntryPrice != 0 ? StrategyInfo.AvgEntryPrice : Bars.Close[0];
                double activeTicks = (RangeSizeTicks > 0) ? RangeSizeTicks : m_AutoRangeTicks;
                int currentPTicks = (m_CurrentTradeTier == 2) ? ProfitTargetTicksTier2 : ProfitTargetTicksTier1;
                double targetDist = currentPTicks > 0 ? (currentPTicks * tickSize) : (activeTicks * tickSize);
                
                if (currentPosition > 0) { m_ProtectiveStopPrice = Bars.Low[0] - (StopTailOffsetTicks * tickSize); m_ProfitTargetPrice = entryPrice + targetDist; }
                else { m_ProtectiveStopPrice = Bars.High[0] + (StopTailOffsetTicks * tickSize); m_ProfitTargetPrice = entryPrice - targetDist; }
                m_BuyOrderActive = m_SellOrderActive = false; m_StopPrice = m_LastSentPrice = 0;
                if (m_GoSignalMarker != null) m_GoSignalMarker.Delete(); UpdateTargetLine(); UpdateStopLine();                
            }

            if (currentPosition == 0 && !m_CancelRequested) {
                double activeTicks = (RangeSizeTicks > 0) ? RangeSizeTicks : m_AutoRangeTicks;
                int currentQty = (m_CurrentTradeTier == 2) ? OrderQtyTier2 : OrderQtyTier1;
                if (m_BuyOrderActive) {
                    m_StopPrice = Math.Round((Bars.Low[0] + (activeTicks * tickSize) + (EntryOffsetTicks * tickSize)) / tickSize) * tickSize;
                    if (Math.Abs(m_StopPrice - m_LastSentPrice) > (tickSize / 2)) { m_BuyStop.Send(m_StopPrice, currentQty); m_LastSentPrice = m_StopPrice; }
                } else if (m_SellOrderActive) {
                    m_StopPrice = Math.Round((Bars.High[0] - (activeTicks * tickSize) - (EntryOffsetTicks * tickSize)) / tickSize) * tickSize;
                    if (Math.Abs(m_StopPrice - m_LastSentPrice) > (tickSize / 2)) { m_SellStop.Send(m_StopPrice, currentQty); m_LastSentPrice = m_StopPrice; }
                }
            }

            if (currentPosition > 0) { if (m_ProtectiveStopPrice > 0) m_BuyExitStop.Send(m_ProtectiveStopPrice); if (m_ProfitTargetPrice > 0) m_BuyExitLimit.Send(m_ProfitTargetPrice); }
            else if (currentPosition < 0) { if (m_ProtectiveStopPrice > 0) m_SellExitStop.Send(m_ProtectiveStopPrice); if (m_ProfitTargetPrice > 0) m_SellExitLimit.Send(m_ProfitTargetPrice); }

            if (currentPosition == 0 && m_LastMarketPosition != 0) { 
                m_ProtectiveStopPrice = m_ProfitTargetPrice = 0; 
                m_BuyOrderActive = m_SellOrderActive = false; 
                m_LastSentPrice = 0; 
                ClearTradingDrawings(); 
            }
            if (m_CancelRequested) { m_BuyOrderActive = m_SellOrderActive = false; m_StopPrice = m_LastSentPrice = 0; m_CancelRequested = false; if (m_GoSignalMarker != null) m_GoSignalMarker.Delete(); }

            m_LastMarketPosition = currentPosition; if (ShowHUD) UpdateHUD();
        }

        private void CheckForHiddenPierceSignals(double tickSize) {
            double activeTicks = (RangeSizeTicks > 0) ? RangeSizeTicks : m_AutoRangeTicks;
            if (activeTicks <= 0) activeTicks = 7;
            double alpha5 = 2.0 / 6.0; double alpha15 = 2.0 / 16.0;
            
            double highest10 = Bars.High.Highest(10);
            double lowest10 = Bars.Low.Lowest(10);
            double range10 = (highest10 - lowest10) / tickSize;
            bool expansionValid = range10 >= MinExpansionTicks;

            // PROJECT BULLISH
            double projCloseBull = Bars.Low[0] + (activeTicks * tickSize);
            double projEma5Bull = m_FastEMA[0] + alpha5 * (projCloseBull - m_FastEMA[0]);
            double projEma15Bull = m_SlowEMA[0] + alpha15 * (projCloseBull - m_SlowEMA[0]);

            // CALCULATE ANGLES (BULLISH)
            double a60 = GetAngle(m_MasterEMA[0], m_MasterEMA[3], 3, tickSize);
            double a15 = GetAngle(m_SlowEMA[0], m_SlowEMA[3], 3, tickSize);
            double a5  = GetAngle(m_FastEMA[0], m_FastEMA[3], 3, tickSize);
            
            bool fanStackBull = m_FastEMA[0] > m_SlowEMA[0] && m_SlowEMA[0] > m_MasterEMA[0]; 
            bool breadth15_60B = (m_SlowEMA[0] - m_MasterEMA[0]) >= (MinBreadth_15_60 * tickSize);
            bool breadth5_15B = (m_FastEMA[0] - m_SlowEMA[0]) >= (MinBreadth_5_15 * tickSize);
            
            bool angleValidBull = a60 >= 45 && a15 >= 45 && a5 >= 45;

            if (expansionValid && fanStackBull && breadth15_60B && breadth5_15B && angleValidBull) {
                if (projEma15Bull > Bars.Low[0]) { 
                    m_CurrentTradeTier = 2; m_BuyOrderActive = true;
                    if (m_GoSignalMarker == null) {
                        m_GoSignalMarker = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], Bars.Low[0] - (3 * tickSize)), new ChartPoint(Bars.Time[0].AddMinutes(0), Bars.Low[0] - (3 * tickSize)));
                        m_GoSignalMarker.Color = Color.RoyalBlue; m_GoSignalMarker.Size = 12;
                    }
                } else if (m_FastEMA[0] > m_FastEMA[1] && projEma5Bull > Bars.Low[0]) { 
                    m_CurrentTradeTier = 1; m_BuyOrderActive = true;
                    if (m_GoSignalMarker == null) {
                        m_GoSignalMarker = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], Bars.Low[0] - (3 * tickSize)), new ChartPoint(Bars.Time[0].AddMinutes(0), Bars.Low[0] - (3 * tickSize)));
                        m_GoSignalMarker.Color = Color.Cyan; m_GoSignalMarker.Size = 10;
                    }
                } 
            } else {
                // PROJECT BEARISH
                double projCloseBear = Bars.High[0] - (activeTicks * tickSize);
                double projEma5Bear = m_FastEMA[0] + alpha5 * (projCloseBear - m_FastEMA[0]);
                double projEma15Bear = m_SlowEMA[0] + alpha15 * (projCloseBear - m_SlowEMA[0]);
                
                // CALCULATE ANGLES (BEARISH)
                double a60S = GetAngle(m_MasterEMA[0], m_MasterEMA[3], 3, tickSize);
                double a15S = GetAngle(m_SlowEMA[0], m_SlowEMA[3], 3, tickSize);
                double a5S  = GetAngle(m_FastEMA[0], m_FastEMA[3], 3, tickSize);

                bool fanStackBear = m_FastEMA[0] < m_SlowEMA[0] && m_SlowEMA[0] < m_MasterEMA[0]; 
                bool breadth15_60S = (m_MasterEMA[0] - m_SlowEMA[0]) >= (MinBreadth_15_60 * tickSize);
                bool breadth5_15S = (m_SlowEMA[0] - m_FastEMA[0]) >= (MinBreadth_5_15 * tickSize);
                
                bool angleValidBear = a60S <= -45 && a15S <= -45 && a5S <= -45;

                if (expansionValid && fanStackBear && breadth15_60S && breadth5_15S && angleValidBear) {
                    if (projEma15Bear < Bars.High[0]) { 
                        m_CurrentTradeTier = 2; m_SellOrderActive = true;
                        if (m_GoSignalMarker == null) {
                            m_GoSignalMarker = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], Bars.High[0] + (3 * tickSize)), new ChartPoint(Bars.Time[0].AddMinutes(0), Bars.High[0] + (3 * tickSize)));
                            m_GoSignalMarker.Color = Color.DeepPink; m_GoSignalMarker.Size = 12;
                        }
                    } else if (m_FastEMA[0] < m_FastEMA[1] && projEma5Bear < Bars.High[0]) { 
                        m_CurrentTradeTier = 1; m_SellOrderActive = true;
                        if (m_GoSignalMarker == null) {
                            m_GoSignalMarker = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], Bars.High[0] + (3 * tickSize)), new ChartPoint(Bars.Time[0].AddMinutes(0), Bars.High[0] + (3 * tickSize)));
                            m_GoSignalMarker.Color = Color.Magenta; m_GoSignalMarker.Size = 10;
                        }
                    }
                } else if (m_GoSignalMarker != null) { m_GoSignalMarker.Delete(); m_GoSignalMarker = null; }
            }
        }

        protected override void OnMouseEvent(MouseClickArgs arg) {
            if (arg.buttons != MouseButtons.Left) return;
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale; if (tickSize <= 0) tickSize = 0.25;
            if ((arg.keys & Keys.Control) == Keys.Control) { m_ClickPrice = arg.point.Price; m_OrderCreatedInMouseEvent = true; }
            else if ((arg.keys & Keys.Shift) == Keys.Shift) { m_CancelRequested = true; if (StrategyInfo.MarketPosition != 0) m_FlattenRequested = true; }
            else if (m_DraggingTarget) { m_ProfitTargetPrice = Math.Round(arg.point.Price / tickSize) * tickSize; m_DraggingTarget = false; UpdateTargetLine(); }
            else if (m_DraggingStop) { m_ProtectiveStopPrice = Math.Round(arg.point.Price / tickSize) * tickSize; m_DraggingStop = false; UpdateStopLine(); }
            else if (m_ProfitTargetPrice > 0 && Math.Abs(arg.point.Price - m_ProfitTargetPrice) <= (ProximityTicks * tickSize)) m_DraggingTarget = true;
            else if (m_ProtectiveStopPrice > 0 && Math.Abs(arg.point.Price - m_ProtectiveStopPrice) <= (ProximityTicks * tickSize)) m_DraggingStop = true;
        }

        private void UpdateTargetLine() {
            if (m_TargetLine != null) m_TargetLine.Delete(); if (m_ProfitTargetPrice <= 0) return;
            m_TargetLine = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], m_ProfitTargetPrice), new ChartPoint(Bars.Time[0].AddMinutes(5), m_ProfitTargetPrice));
            m_TargetLine.Color = Color.Gold; m_TargetLine.Style = ETLStyle.ToolDashed; m_TargetLine.Size = 2; m_TargetLine.ExtRight = true;
        }

        private void UpdateStopLine() {
            if (m_StopLine != null) m_StopLine.Delete(); if (m_ProtectiveStopPrice <= 0) return;
            m_StopLine = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], m_ProtectiveStopPrice), new ChartPoint(Bars.Time[0].AddMinutes(5), m_ProtectiveStopPrice));
            m_StopLine.Color = Color.Red; m_StopLine.Style = ETLStyle.ToolDashed; m_StopLine.Size = 2; m_StopLine.ExtRight = true;
        }

        private void UpdateHUD() {
            double pnl = StrategyInfo.OpenEquity; double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale; if (tickSize <= 0) tickSize = 0.25;
            string status = "IDLE"; if (m_BuyOrderActive) status = m_CurrentTradeTier == 1? "GO BUY (T1)":"GO BUY (T2)"; if (m_SellOrderActive) status = m_CurrentTradeTier == 1? "GO SELL (T1)":"GO SELL (T2)"; if (StrategyInfo.MarketPosition != 0) status = "IN TRADE";
            string text = string.Format("RANGE TRADER | {0} | PnL: {1:C2}", status, pnl);
            if (m_HUDLabel == null) { m_HUDLabel = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.High[0]), text); m_HUDLabel.Size = 14; }
            m_HUDLabel.Text = text; m_HUDLabel.Color = pnl >= 0 ? Color.LimeGreen : Color.Tomato;
            m_HUDLabel.Location = new ChartPoint(Bars.Time[0], Bars.High[0] + (12 * tickSize));
        }

        private double GetAngle(double valCurrent, double valOld, int barsBack, double tickSize) {
            double rise = valCurrent - valOld;
            double run = (double)barsBack * tickSize; 
            return Math.Atan2(rise, run) * (180.0 / Math.PI);
        }

        protected override void Destroy() { ClearTradingDrawings(); }
    }
}
