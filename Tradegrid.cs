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
        [Input] public int DefaultStepTicks { get; set; }

        private double m_LastDrawnCenter = 0;
        private List<ITrendLineObject> m_GridLines = new List<ITrendLineObject>();

        public Tradegrid(object ctx) : base(ctx)
        {
            GridLinesCount = 40;
            GridLineColor = Color.Black; 
            DefaultStepTicks = 20;
        }

        protected override void Create() { m_GridLines = new List<ITrendLineObject>(); }

        protected override void StartCalc() { m_LastDrawnCenter = 0; ClearGrid(); }

        protected override void CalcBar()
        {
            if (!Environment.IsRealTimeCalc) return;

            string symPrefix = Bars.Info.Name + "_";
            
            // Listen to the OFFICIAL MultiCharts Global Bridge
            double entry = GlobalVariables.GetVariable(symPrefix + "Entry").DoubleValue;
            double step = GlobalVariables.GetVariable(symPrefix + "Step").DoubleValue;

            if (step <= 0) step = DefaultStepTicks * ((double)Bars.Info.MinMove / Bars.Info.PriceScale);

            if (entry > 0)
            {
                if (Math.Abs(entry - m_LastDrawnCenter) > 0.0001)
                {
                    DrawGrid(entry, step);
                    m_LastDrawnCenter = entry;
                }
            }
            else
            {
                if (m_LastDrawnCenter != 0)
                {
                    ClearGrid();
                    m_LastDrawnCenter = 0;
                }
            }
        }

        private void DrawGrid(double centerPrice, double stepSize)
        {
            ClearGrid();
            DateTime t1 = Bars.CurrentBar > 1 ? Bars.Time[1] : Bars.Time[0].AddDays(-1);
            DateTime t2 = Bars.Time[0];

            for (int i = 1; i <= GridLinesCount / 2; i++)
            {
                double upPrice = centerPrice + (stepSize * i);
                var upL = DrwTrendLine.Create(new ChartPoint(t1, upPrice), new ChartPoint(t2, upPrice));
                upL.Color = GridLineColor; upL.Style = ETLStyle.ToolDashed; upL.Size = 1; upL.ExtLeft = upL.ExtRight = true;
                m_GridLines.Add(upL);

                double dnPrice = centerPrice - (stepSize * i);
                var dnL = DrwTrendLine.Create(new ChartPoint(t1, dnPrice), new ChartPoint(t2, dnPrice));
                dnL.Color = GridLineColor; dnL.Style = ETLStyle.ToolDashed; dnL.Size = 1; dnL.ExtLeft = dnL.ExtRight = true;
                m_GridLines.Add(dnL);
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
