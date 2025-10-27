using System;
using System.Drawing;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true)]
    public class projected_future_renko : IndicatorObject
    {
        [Input]
        public double RenkoBoxSize { get; set; }

        [Input]
        public Color ProjectionColor { get; set; }

        private IPlotObject m_Plot;
        private double m_LastClosePrice;
        private DateTime m_LastCloseTime;
        private ITrendLineObject m_ProjectionLine;

        public projected_future_renko(object ctx) : base(ctx)
        {
            RenkoBoxSize = 10;
            ProjectionColor = Color.Yellow;
        }

        protected override void Create()
        {
            m_Plot = AddPlot(new PlotAttributes("Projection", EPlotShapes.Line, Color.Transparent));
        }

        protected override void StartCalc()
        {
            if (!Environment.IsRealTimeCalc)
                return;

            if (Bars.CurrentBar > 1)
            {
                m_LastClosePrice = Bars.Close[0];
                m_LastCloseTime = Bars.Time[0];
                DrawProjection();
            }
        }

        protected override void CalcBar()
        {
            m_Plot.Set(0);

            if (!Environment.IsRealTimeCalc)
                return;

            if (Bars.Status == EBarState.Close && Bars.Close[0] != m_LastClosePrice)
            {
                m_LastClosePrice = Bars.Close[0];
                m_LastCloseTime = Bars.Time[0];
                
                // Clear old line
                if (m_ProjectionLine != null)
                {
                    try { m_ProjectionLine.Delete(); } catch { }
                }
                
                DrawProjection();
            }
        }

        private void DrawProjection()
        {
            try
            {
                bool isUpBar = Bars.Close[0] > Bars.Open[0];
                double projectionPrice = isUpBar ? m_LastClosePrice + RenkoBoxSize : m_LastClosePrice - RenkoBoxSize;
                
                DateTime rightTime = m_LastCloseTime.AddMinutes(5);
                
                ChartPoint leftPoint = new ChartPoint(m_LastCloseTime, projectionPrice);
                ChartPoint rightPoint = new ChartPoint(rightTime, projectionPrice);
                
                m_ProjectionLine = DrwTrendLine.Create(leftPoint, rightPoint);
                m_ProjectionLine.Color = ProjectionColor;
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error drawing projection: " + ex.Message);
            }
        }
    }
}
