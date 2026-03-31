using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Move State inside to satisfy MultiCharts compiler entry point
    public static class TradeGridState
    {
        public static Dictionary<string, double> ActiveEntries = new Dictionary<string, double>();
        public static Dictionary<string, double> StepSizes = new Dictionary<string, double>();
    }

    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    [UpdateOnEveryTick(true)]
    public class trade_grid : IndicatorObject
    {
        [Input] public int GridLinesCount { get; set; }
        [Input] public Color GridLineColor { get; set; }

        private double m_LastDrawnEntry = 0;
        private List<ITrendLineObject> m_GridLines = new List<ITrendLineObject>();

        public trade_grid(object ctx) : base(ctx)
        {
            GridLinesCount = 5;
            GridLineColor = Color.Red;
        }

        protected override void Create()
        {
            m_GridLines = new List<ITrendLineObject>();
        }

        protected override void StartCalc()
        {
            m_LastDrawnEntry = 0;
            ClearGrid();
        }

        protected override void CalcBar()
        {
            if (!Environment.IsRealTimeCalc) return;

            string symbol = Bars.Info.Name;
            
            double activeEntry = 0;
            if (TradeGridState.ActiveEntries.ContainsKey(symbol))
                activeEntry = TradeGridState.ActiveEntries[symbol];
                
            double stepSize = 0;
            if (TradeGridState.StepSizes.ContainsKey(symbol))
                stepSize = TradeGridState.StepSizes[symbol];

            if (activeEntry == 0 && m_LastDrawnEntry != 0)
            {
                ClearGrid();
                m_LastDrawnEntry = 0;
            }
            else if (activeEntry > 0 && activeEntry != m_LastDrawnEntry && stepSize > 0)
            {
                DrawGrid(activeEntry, stepSize);
                m_LastDrawnEntry = activeEntry;
            }
        }

        private void DrawGrid(double centerPrice, double stepSize)
        {
            ClearGrid();
            
            DateTime t1 = Bars.CurrentBar > 1 ? Bars.Time[1] : Bars.Time[0].AddDays(-1);
            DateTime t2 = Bars.Time[0];

            for (int i = 1; i <= GridLinesCount; i++)
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
