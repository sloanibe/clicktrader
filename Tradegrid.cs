using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Bridge Class
    public static class TGridShared
    {
        public static Dictionary<string, double> ActiveEntries = new Dictionary<string, double>();
        public static Dictionary<string, double> StepSizes = new Dictionary<string, double>();
    }

    // Safety Alias
    public static class TradeGridState 
    {
        public static Dictionary<string, double> ActiveEntries { get { return TGridShared.ActiveEntries; } }
        public static Dictionary<string, double> StepSizes { get { return TGridShared.StepSizes; } }
    }

    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    [UpdateOnEveryTick(true)]
    public class Tradegrid : IndicatorObject
    {
        [Input] public int GridLinesCount { get; set; }
        [Input] public Color GridLineColor { get; set; }
        [Input] public int DebugStepTicks { get; set; } // Default 20 for MNQ

        private double m_LastDrawnCenter = 0;
        private List<ITrendLineObject> m_GridLines = new List<ITrendLineObject>();

        public Tradegrid(object ctx) : base(ctx)
        {
            GridLinesCount = 100;
            GridLineColor = Color.Red;
            DebugStepTicks = 20;
        }

        protected override void Create()
        {
            m_GridLines = new List<ITrendLineObject>();
        }

        protected override void StartCalc()
        {
            m_LastDrawnCenter = 0;
            ClearGrid();
        }

        protected override void CalcBar()
        {
            if (!Environment.IsRealTimeCalc) return;

            string symbol = Bars.Info.Name;
            
            double activeEntry = 0;
            if (TGridShared.ActiveEntries.ContainsKey(symbol))
                activeEntry = TGridShared.ActiveEntries[symbol];
                
            double stepSize = 0;
            if (TGridShared.StepSizes.ContainsKey(symbol))
                stepSize = TGridShared.StepSizes[symbol];

            // FORCED DEBUG MODE: If no active trade, draw a static 20-tick grid centered on the first real-time price
            if (activeEntry <= 0)
            {
                double centerPrice = Bars.Close[0];
                double debugStep = DebugStepTicks * ((double)Bars.Info.MinMove / Bars.Info.PriceScale);
                
                // Only redraw if price has moved significantly or grid isn't there
                if (m_LastDrawnCenter == 0) 
                {
                    DrawGrid(centerPrice, debugStep);
                    m_LastDrawnCenter = centerPrice;
                }
            }
            else if (activeEntry > 0 && activeEntry != m_LastDrawnCenter && stepSize > 0)
            {
                // TRADE MODE: Draw grid around the actual entry price
                DrawGrid(activeEntry, stepSize);
                m_LastDrawnCenter = activeEntry;
            }
        }

        private void DrawGrid(double centerPrice, double stepSize)
        {
            ClearGrid();
            
            // Fixed historical anchors for maximum MultiCharts compatibility
            DateTime t1 = Bars.CurrentBar > 1 ? Bars.Time[1] : Bars.Time[0].AddDays(-1);
            DateTime t2 = Bars.Time[0];

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
            foreach (var line in m_GridLines) 
                if (line != null) line.Delete(); 
            m_GridLines.Clear();
        }

        protected override void Destroy()
        {
            ClearGrid();
        }
    }
}
