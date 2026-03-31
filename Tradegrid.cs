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

        private double m_MasterAnchor = 0;
        private double m_MasterHeight = 0;
        private List<ITrendLineObject> m_GridLines = new List<ITrendLineObject>();

        public Tradegrid(object ctx) : base(ctx)
        {
            GridLinesCount = 300; // Complete chart coverage
            GridLineColor = Color.Gainsboro; // Ultra-Subtle Ghosted Gray
        }

        protected override void Create() { m_GridLines = new List<ITrendLineObject>(); }

        protected override void StartCalc() { m_MasterAnchor = 0; m_MasterHeight = 0; ClearGrid(); }

        protected override void CalcBar()
        {
            if (!Environment.IsRealTimeCalc) return;

            // ABSOLUTE STATIC MASTER GRID:
            // 1. Look at the FIRST COMPLETED BAR (Bars.Close[1] and Bars.Open[1]).
            // 2. Measure the height (Body) and the anchor (Close).
            // 3. Draw once and NEVER redraw again.
            
            if (m_MasterAnchor == 0 && Bars.CurrentBar > 1)
            {
                // Capture the Previous Completed Bar
                m_MasterAnchor = Bars.Close[1]; 
                m_MasterHeight = Math.Abs(Bars.Close[1] - Bars.Open[1]);

                if (m_MasterHeight > 0)
                {
                    DrawPermanentGrid(m_MasterAnchor, m_MasterHeight);
                    Output.WriteLine("📊 GRID: Anchored at {0} with height {1}. Grid LOCKED.", m_MasterAnchor, m_MasterHeight);
                }
            }
        }

        private void DrawPermanentGrid(double anchor, double height)
        {
            ClearGrid();
            
            // Fixed horizontal anchors (entire chart)
            DateTime tStart = Bars.Time[Bars.CurrentBar - 1]; 
            DateTime tEnd = Bars.Time[0];

            for (int i = -GridLinesCount/2; i <= GridLinesCount/2; i++)
            {
                double price = anchor + (height * i);
                
                var line = DrwTrendLine.Create(new ChartPoint(tStart, price), new ChartPoint(tEnd, price));
                line.Color = GridLineColor;
                line.Style = ETLStyle.ToolSolid;
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
