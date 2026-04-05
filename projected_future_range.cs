using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    [UpdateOnEveryTick(true)] 
    public class projected_future_range : IndicatorObject
    {
        [Input] public int Ticks { get; set; } // 0 = Auto-detect
        [Input] public int FastEMAPeriod { get; set; }
        [Input] public int SlowEMAPeriod { get; set; }
        [Input] public int MasterEMAPeriod { get; set; }
        [Input] public int MinExpansionTicks { get; set; }
        [Input] public int TrendRecencyBars { get; set; }
        [Input] public int MinBreadth_15_60 { get; set; }
        [Input] public int MinBreadth_5_15 { get; set; }
        [Input] public double MinSlopeTicks { get; set; }
        [Input] public bool ShowHistory { get; set; }
        [Input] public Color BullishColor { get; set; }
        [Input] public Color BearishColor { get; set; }
        
        [Input] public bool ShowLightBlue { get; set; }
        [Input] public bool ShowDarkBlue { get; set; }

        private IPlotObject m_FastEMAPlot;
        private IPlotObject m_SlowEMAPlot;
        private IPlotObject m_MasterEMAPlot;
        private IPlotObject m_GoTier1Plot;
        private IPlotObject m_GoTier2Plot;
        
        private ITrendLineObject m_BullishLine;
        private ITrendLineObject m_BearishLine;
        private ITrendLineObject m_ProjEmaHigh;
        private ITrendLineObject m_ProjEmaLow;
        private ITrendLineObject m_GoSignalMarker;
        
        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_MasterEMA;
        
        private double m_AutoRangeTicks = 0;
        private double m_LastLow;
        private double m_LastHigh;
        private int m_LastBarIndex = -1;

        public projected_future_range(object ctx) : base(ctx)
        {
            Ticks = 0; 
            FastEMAPeriod = 5; SlowEMAPeriod = 15; MasterEMAPeriod = 60;
            MinExpansionTicks = 15; TrendRecencyBars = 30; MinBreadth_15_60 = 5; MinBreadth_5_15 = 4; MinSlopeTicks = 1.0;
            ShowHistory = false; BullishColor = Color.Cyan; BearishColor = Color.DeepPink;
            ShowLightBlue = true; ShowDarkBlue = true;
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this); m_SlowEMA = new XAverage(this); m_MasterEMA = new XAverage(this);
            m_FastEMAPlot = AddPlot(new PlotAttributes("Fast EMA", EPlotShapes.Line, Color.Yellow, Color.Empty, 2, 0, true));
            m_SlowEMAPlot = AddPlot(new PlotAttributes("Slow EMA", EPlotShapes.Line, Color.DarkBlue, Color.Empty, 2, 0, true));
            m_MasterEMAPlot = AddPlot(new PlotAttributes("Master EMA", EPlotShapes.Line, Color.Black, Color.Empty, 4, 0, true));
            m_GoTier1Plot = AddPlot(new PlotAttributes("Tier 1 (Cyan)", EPlotShapes.Point, Color.Cyan, Color.Empty, 10, 0, true));
            m_GoTier2Plot = AddPlot(new PlotAttributes("Tier 2 (Royal)", EPlotShapes.Point, Color.RoyalBlue, Color.Empty, 12, 0, true));
        }

        protected override void StartCalc()
        {
            m_FastEMA.Length = FastEMAPeriod; m_FastEMA.Price = Bars.Close;
            m_SlowEMA.Length = SlowEMAPeriod; m_SlowEMA.Price = Bars.Close;
            m_MasterEMA.Length = MasterEMAPeriod; m_MasterEMA.Price = Bars.Close;
            ClearLines();
        }

        protected override void CalcBar()
        {
            double tSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale; if (tSize <= 0) tSize = 0.25;
            if (Bars.Status == EBarState.Close || m_AutoRangeTicks <= 0) m_AutoRangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tSize;
            m_FastEMAPlot.Set(m_FastEMA[0]); m_SlowEMAPlot.Set(m_SlowEMA[0]); m_MasterEMAPlot.Set(m_MasterEMA[0]);
            if (ShowHistory && Bars.CurrentBar > 10) CheckHistoricalSignal(tSize);
            if (!Environment.IsRealTimeCalc) return;
            bool isNewBar = (Bars.CurrentBar != m_LastBarIndex);
            m_LastLow = Bars.Low[0]; m_LastHigh = Bars.High[0]; m_LastBarIndex = Bars.CurrentBar;
            if (isNewBar) ClearLines(); DrawProjections(tSize);
        }

        private void CheckHistoricalSignal(double tSize)
        {
             double h10 = Bars.High.Highest(10); double l10 = Bars.Low.Lowest(10);
             bool expValid = ((h10 - l10) / tSize) >= MinExpansionTicks;
             
             double a60 = GetAngle(m_MasterEMA[1], m_MasterEMA[6], 5, tSize);
             double a15 = GetAngle(m_SlowEMA[1], m_SlowEMA[6], 5, tSize);
             double a5  = GetAngle(m_FastEMA[1], m_FastEMA[4], 3, tSize);

             if (Bars.Close[0] > Bars.Open[0]) { // BULLISH
                 bool bB = (m_SlowEMA[1] - m_MasterEMA[1]) >= (MinBreadth_15_60 * tSize); // LAG BREADTH FOR STABILITY
                 bool bF = (m_FastEMA[1] - m_SlowEMA[1]) >= (MinBreadth_5_15 * tSize);
                 if (expValid && m_SlowEMA[0] > m_MasterEMA[0] && bB) {
                     // TIER 2 CHECK: 2-TICK BUFFER FOR "TOUCH"
                     if (ShowDarkBlue && Bars.Low[0] <= m_SlowEMA[0] + (2 * tSize) && a60 >= 30 && a15 >= 30) m_GoTier2Plot.Set(Bars.Low[0] - (tSize * 4));
                     // TIER 1 CHECK: 2-TICK BUFFER FOR "TOUCH"
                     else if (ShowLightBlue && m_FastEMA[0] > m_SlowEMA[0] && bF && Bars.Low[0] <= m_FastEMA[0] + (2 * tSize) && a60 >= 45 && a15 >= 45 && a5 >= 45) m_GoTier1Plot.Set(Bars.Low[0] - (tSize * 4));
                 }
             } else if (Bars.Close[0] < Bars.Open[0]) { // BEARISH
                 bool bR = (m_MasterEMA[1] - m_SlowEMA[1]) >= (MinBreadth_15_60 * tSize);
                 bool bF = (m_SlowEMA[1] - m_FastEMA[1]) >= (MinBreadth_5_15 * tSize);
                 if (expValid && m_SlowEMA[0] < m_MasterEMA[0] && bR) {
                     if (ShowDarkBlue && Bars.High[0] >= m_SlowEMA[0] - (2 * tSize) && a60 <= -30 && a15 <= -30) { m_GoTier2Plot.Set(Bars.High[0] + (tSize * 4)); m_GoTier2Plot.BGColor = Color.DeepPink; }
                     else if (ShowLightBlue && m_FastEMA[0] < m_SlowEMA[0] && bF && Bars.High[0] >= m_FastEMA[0] - (2 * tSize) && a60 <= -45 && a15 <= -45 && a5 <= -45) { m_GoTier1Plot.Set(Bars.High[0] + (tSize * 4)); m_GoTier1Plot.BGColor = Color.Magenta; }
                 }
             }
        }

        private void DrawProjections(double tSize)
        {
            try {
                ClearLines(); double activeTicks = (Ticks > 0) ? Ticks : m_AutoRangeTicks; if (activeTicks <= 0) activeTicks = 7;
                double bProj = m_LastLow + (activeTicks * tSize); double sProj = m_LastHigh - (activeTicks * tSize);
                DateTime sTime = Bars.Time[0]; DateTime eTime = sTime.AddMinutes(2); 
                double al5 = 2.0 / 6.0; double al15 = 2.0 / 16.0;
                double pE5B = m_FastEMA[0] + al5 * (bProj - m_FastEMA[0]); double pE15B = m_SlowEMA[0] + al15 * (bProj - m_SlowEMA[0]);
                double pE5R = m_FastEMA[0] + al5 * (sProj - m_FastEMA[0]); double pE15R = m_SlowEMA[0] + al15 * (sProj - m_SlowEMA[0]);
                m_BullishLine = DrwTrendLine.Create(new ChartPoint(sTime, bProj), new ChartPoint(eTime, bProj)); if (m_BullishLine != null) { m_BullishLine.Color = Color.FromArgb(100, BullishColor); m_BullishLine.ExtRight = true; }
                m_BearishLine = DrwTrendLine.Create(new ChartPoint(sTime, sProj), new ChartPoint(eTime, sProj)); if (m_BearishLine != null) { m_BearishLine.Color = Color.FromArgb(100, BearishColor); m_BearishLine.ExtRight = true; }
                double h10 = Bars.High.Highest(10); double l10 = Bars.Low.Lowest(10); bool expV = ((h10 - l10) / tSize) >= MinExpansionTicks;
                double a60 = GetAngle(m_MasterEMA[0], m_MasterEMA[5], 5, tSize);
                double a15 = GetAngle(m_SlowEMA[0], m_SlowEMA[5], 5, tSize);
                double a5  = GetAngle(m_FastEMA[0], m_FastEMA[3], 3, tSize);
                if (expV && m_SlowEMA[0] > m_MasterEMA[0]) {
                    double lB = (m_SlowEMA[1] - m_MasterEMA[1]) / tSize;
                    if (ShowDarkBlue && pE15B > m_LastLow - (2 * tSize) && lB >= MinBreadth_15_60 && a60 >= 30 && a15 >= 30) { m_GoSignalMarker = DrwTrendLine.Create(new ChartPoint(sTime, m_LastLow - (tSize * 4)), new ChartPoint(sTime, m_LastLow - (tSize * 4))); m_GoSignalMarker.Color = Color.RoyalBlue; m_GoSignalMarker.Size = 12; }
                    else if (ShowLightBlue && m_FastEMA[0] > m_SlowEMA[0] && pE5B > m_LastLow - (2 * tSize) && a60 >= 45 && a15 >= 45 && a5 >= 45) { m_GoSignalMarker = DrwTrendLine.Create(new ChartPoint(sTime, m_LastLow - (tSize * 4)), new ChartPoint(sTime, m_LastLow - (tSize * 4))); m_GoSignalMarker.Color = Color.Cyan; m_GoSignalMarker.Size = 10; }
                }
                if (expV && m_SlowEMA[0] < m_MasterEMA[0]) {
                    double rB = (m_MasterEMA[1] - m_SlowEMA[1]) / tSize;
                    if (ShowDarkBlue && pE15R < m_LastHigh + (2 * tSize) && rB >= MinBreadth_15_60 && a60 <= -30 && a15 <= -30) { m_GoSignalMarker = DrwTrendLine.Create(new ChartPoint(sTime, m_LastHigh + (tSize * 4)), new ChartPoint(sTime, m_LastHigh + (tSize * 4))); m_GoSignalMarker.Color = Color.DeepPink; m_GoSignalMarker.Size = 12; }
                    else if (ShowLightBlue && m_FastEMA[0] < m_SlowEMA[0] && pE5R < m_LastHigh + (2 * tSize) && a60 <= -45 && a15 <= -45 && a5 <= -45) { m_GoSignalMarker = DrwTrendLine.Create(new ChartPoint(sTime, m_LastHigh + (tSize * 4)), new ChartPoint(sTime, m_LastHigh + (tSize * 4))); m_GoSignalMarker.Color = Color.Magenta; m_GoSignalMarker.Size = 10; }
                }
            } catch { }
        }

        private double GetAngle(double vC, double vO, int bB, double tS) { double r = vC - vO; double u = (double)bB * tS; return Math.Atan2(r, u) * (180.0 / Math.PI); }
        private void ClearLines() { if (m_BullishLine != null) m_BullishLine.Delete(); if (m_BearishLine != null) m_BearishLine.Delete(); if (m_ProjEmaHigh != null) m_ProjEmaHigh.Delete(); if (m_ProjEmaLow != null) m_ProjEmaLow.Delete(); if (m_GoSignalMarker != null) m_GoSignalMarker.Delete(); }
    }
}