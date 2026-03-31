using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    [UpdateOnEveryTick(false)] // Static grid, only update on Bar Close
    public class Tradegrid : IndicatorObject
    {
        [Input] public int GridLinesCount { get; set; }
        [Input] public Color GridLineColor { get; set; }

        private double m_AnchorPrice = 0;
        private List<ITrendLineObject> m_GridLines = new List<ITrendLineObject>();

        public Tradegrid(object ctx) : base(ctx)
        {
            GridLinesCount = 200; // Large coverage for the whole chart
            GridLineColor = Color.FromArgb(64, 64, 64); // Subtle Gray/Black
        }

        protected override void Create() { m_GridLines = new List<ITrendLineObject>(); }

        protected override void StartCalc() { m_AnchorPrice = 0; ClearGrid(); }

        protected override void CalcBar()
        {
            // STATIC GRID LOGIC:
            // 1. We anchor to the FIRST real Renko brick's body (Open/Close).
            // 2. Once fixed, the grid NEVER moves, even as new bars are drawn.
            // 3. Spacing matches the physical brick height.

            if (m_AnchorPrice == 0 && Bars.CurrentBar > 1)
            {
                // Align with the body height of the first available brick
                m_AnchorPrice = Bars.Close[0];
                double brickHeight = Math.Abs(Bars.Close[0] - Bars.Open[0]);
                
                if (brickHeight > 0)
                {
                    DrawStaticGrid(m_AnchorPrice, brickHeight);
                }
            }
        }

        private void DrawStaticGrid(double anchor, double height)
        {
            ClearGrid();
            
            // Fixed horizontal anchors (start of chart to end of time)
            DateTime t1 = Bars.Time[Bars.CurrentBar - 1]; 
            DateTime t2 = Bars.Time[0];

            for (int i = -GridLinesCount/2; i <= GridLinesCount/2; i++)
            {
                double price = anchor + (height * i);
                
                var line = DrwTrendLine.Create(new ChartPoint(t1, price), new ChartPoint(t2, price));
                line.Color = GridLineColor;
                line.Style = ETLStyle.ToolDashed;
                line.Size = 1;
                line.ExtLeft = true;
                line.ExtRight = true;
                
                m_GridLines.Add(line);
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
