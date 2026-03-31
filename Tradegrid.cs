using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    [UpdateOnEveryTick(true)] // Allow live drawing
    public class Tradegrid : IndicatorObject
    {
        [Input] public int GridLinesCount { get; set; }
        [Input] public Color GridLineColor { get; set; }

        private double m_FixedAnchor = 0;
        private double m_FixedHeight = 0;
        private List<ITrendLineObject> m_GridLines = new List<ITrendLineObject>();

        public Tradegrid(object ctx) : base(ctx)
        {
            GridLinesCount = 200; 
            GridLineColor = Color.FromArgb(64, 64, 64); // Professional Dark Gray
        }

        protected override void Create() { m_GridLines = new List<ITrendLineObject>(); }

        protected override void StartCalc() { m_FixedAnchor = 0; m_FixedHeight = 0; ClearGrid(); }

        protected override void CalcBar()
        {
            if (!Environment.IsRealTimeCalc) return;

            // DYNAMIC-BUT-STABLE ANCHORING:
            // Capture the VERY FIRST real-time bar's body.
            // Once captured, we NEVER look at the price again.
            if (m_FixedAnchor == 0)
            {
                m_FixedAnchor = Bars.Close[0];
                m_FixedHeight = Math.Abs(Bars.Close[0] - Bars.Open[0]);
                
                if (m_FixedHeight > 0)
                {
                    DrawFixedGrid(m_FixedAnchor, m_FixedHeight);
                }
            }
        }

        private void DrawFixedGrid(double anchor, double height)
        {
            ClearGrid();
            
            // Anchors for horizontal lines (Full chart width)
            DateTime t1 = Bars.Time[Bars.CurrentBar - 1]; 
            DateTime t2 = Bars.Time[0];

            for (int i = -GridLinesCount / 2; i <= GridLinesCount / 2; i++)
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
