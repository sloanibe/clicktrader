using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage;

namespace PowerLanguage.Indicator
{
    // Simplified Static Bridge
    public static class TGridShared
    {
        public static Dictionary<string, double> ActiveEntries = new Dictionary<string, double>();
        public static Dictionary<string, double> StepSizes = new Dictionary<string, double>();
    }

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

            string sym = Bars.Info.Name;
            double entry = 0;
            double step = 0;

            if (TGridShared.ActiveEntries.ContainsKey(sym)) entry = TGridShared.ActiveEntries[sym];
            if (TGridShared.StepSizes.ContainsKey(sym)) step = TGridShared.StepSizes[sym];

            // If no step is provided by the signal, use the Default (20 ticks)
            if (step <= 0) step = DefaultStepTicks * ((double)Bars.Info.MinMove / Bars.Info.PriceScale);

            if (entry > 0)
            {
                // TRADE ACTIVE: Snap grid to the entry price
                if (Math.Abs(entry - m_LastDrawnCenter) > 0.0001)
                {
                    DrawGrid(entry, step);
                    m_LastDrawnCenter = entry;
                }
            }
            else
            {
                // NO TRADE: Clean the grid
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
