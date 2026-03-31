using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    [UpdateOnEveryTick(true)]
    public class Tradegrid : IndicatorObject
    {
        [Input] public int GridLinesCount { get; set; }
        [Input] public Color GridLineColor { get; set; }

        private double m_LastDrawnClose = 0;
        private List<ITrendLineObject> m_GridLines = new List<ITrendLineObject>();

        public Tradegrid(object ctx) : base(ctx)
        {
            GridLinesCount = 60; // Extra coverage for wide scrolling
            GridLineColor = Color.Black; 
        }

        protected override void Create() { m_GridLines = new List<ITrendLineObject>(); }

        protected override void StartCalc() { m_LastDrawnClose = 0; ClearGrid(); }

        protected override void CalcBar()
        {
            if (!Environment.IsRealTimeCalc) return;

            // SIMPLIFIED APPROACH: Anchor to the MOST RECENT CLOSE on the chart
            // This aligns the grid with the physical Renko bricks without Signal communication
            double currentClose = Bars.Close[0];
            
            // Measure the physical height of the PREVIOUS COMPLETED BRICK
            // This ensures the grid matches the chart's specific Renko/Range settings
            double stepSize = Math.Abs(Bars.Close[1] - Bars.Open[1]);
            
            // Safety fallback if no bricks exist yet
            if (stepSize <= 0) 
            {
                double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
                stepSize = 20 * tickSize; // Default to 20 ticks for MNQ
            }

            // REDRAW: Only update the grid if the current bar's close has moved to a new level
            if (Math.Abs(currentClose - m_LastDrawnClose) > 0.0001)
            {
                DrawGrid(currentClose, stepSize);
                m_LastDrawnClose = currentClose;
            }
        }

        private void DrawGrid(double centerPrice, double stepSize)
        {
            ClearGrid();
            
            // Anchors for horizontal lines
            DateTime t1 = Bars.CurrentBar > 1 ? Bars.Time[1] : Bars.Time[0].AddDays(-1);
            DateTime t2 = Bars.Time[0];

            // Loop outward from the center close to fill the screen
            for (int i = 0; i <= GridLinesCount / 2; i++)
            {
                // Above
                double upPrice = centerPrice + (stepSize * i);
                var upL = DrwTrendLine.Create(new ChartPoint(t1, upPrice), new ChartPoint(t2, upPrice));
                upL.Color = GridLineColor; upL.Style = ETLStyle.ToolDashed; upL.Size = 1; upL.ExtLeft = upL.ExtRight = true;
                m_GridLines.Add(upL);

                // Below
                if (i > 0)
                {
                    double dnPrice = centerPrice - (stepSize * i);
                    var dnL = DrwTrendLine.Create(new ChartPoint(t1, dnPrice), new ChartPoint(t2, dnPrice));
                    dnL.Color = GridLineColor; dnL.Style = ETLStyle.ToolDashed; dnL.Size = 1; dnL.ExtLeft = dnL.ExtRight = true;
                    m_GridLines.Add(dnL);
                }
            }
        }

        private void ClearGrid()
        {
            foreach (var line in m_GridLines) if (line != null) line.Delete();
            m_GridLines.Clear();
        }

        protected override void Destroy() { ClearGrid(); }
    }
}
