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
        [Input] public int TickOffset { get; set; }
        [Input] public Color BullishColor { get; set; }
        [Input] public Color BearishColor { get; set; }

        private ITrendLineObject m_BullishLine;
        private ITrendLineObject m_BearishLine;
        private double m_LastLow;
        private double m_LastHigh;
        private int m_LastBarIndex = -1;

        public projected_future_range(object ctx) : base(ctx)
        {
            TickOffset = 7; // Matches RangeBarTrading default
            BullishColor = Color.Green;
            BearishColor = Color.Red;
        }

        protected override void Create()
        {
            // The "Wrinkle Fix": We rely on ExtRight = true for trendlines.
        }

        protected override void StartCalc()
        {
            ClearLines();
            if (!Environment.IsRealTimeCalc) return;
            
            if (Bars.CurrentBar > 0)
            {
                m_LastLow = Bars.Low[0];
                m_LastHigh = Bars.High[0];
            }
        }

        protected override void CalcBar()
        {
            if (!Environment.IsRealTimeCalc) return;
            
            bool isNewBar = (Bars.CurrentBar != m_LastBarIndex);
            
            // Standard Range Bar Projection Logic
            m_LastLow = Bars.Status == EBarState.Close ? Bars.Low[0] : Math.Min(Bars.Low[0], Bars.Close[0]);
            m_LastHigh = Bars.Status == EBarState.Close ? Bars.High[0] : Math.Max(Bars.High[0], Bars.Close[0]);
            m_LastBarIndex = Bars.CurrentBar;
            
            if (isNewBar) ClearLines();
            
            DrawProjections();
        }

        private void DrawProjections()
        {
            try
            {
                ClearLines();
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                
                double bullishProjection = m_LastLow + (TickOffset * tickSize);
                double bearishProjection = m_LastHigh - (TickOffset * tickSize);
                
                DateTime startTime = Bars.Time[0];
                DateTime endTime = startTime.AddMinutes(5); 
                
                try
                {
                    // Draw bullish line with ExtRight extension logic
                    m_BullishLine = DrwTrendLine.Create(new ChartPoint(startTime, bullishProjection), new ChartPoint(endTime, bullishProjection));
                    if (m_BullishLine != null)
                    {
                        m_BullishLine.Color = BullishColor;
                        m_BullishLine.ExtRight = true; 
                    }
                    
                    // Draw bearish line with ExtRight extension logic
                    m_BearishLine = DrwTrendLine.Create(new ChartPoint(startTime, bearishProjection), new ChartPoint(endTime, bearishProjection));
                    if (m_BearishLine != null)
                    {
                        m_BearishLine.Color = BearishColor;
                        m_BearishLine.ExtRight = true; 
                    }
                }
                catch (Exception ex)
                {
                    Output.WriteLine("Error drawing lines: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in DrawProjections: " + ex.Message);
            }
        }

        private void ClearLines()
        {
            if (m_BullishLine != null) { m_BullishLine.Delete(); m_BullishLine = null; }
            if (m_BearishLine != null) { m_BearishLine.Delete(); m_BearishLine = null; }
        }
    }
}
