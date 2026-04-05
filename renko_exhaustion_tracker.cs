using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(false)] // SHOWS IN SUB-CHART
    public class renko_exhaustion_tracker : IndicatorObject
    {
        [Input] public int ScanLookback { get; set; }
        [Input] public int MinTrendRun { get; set; }

        private VariableSeries<double> m_BarDuration;
        private List<double> m_ExhaustionEnergy = new List<double>();
        
        private IPlotObject m_PressurePlot;
        private IPlotObject m_FatalLine;

        public renko_exhaustion_tracker(object ctx) : base(ctx)
        {
            ScanLookback = 30; MinTrendRun = 3;
        }

        protected override void Create()
        {
            m_BarDuration = new VariableSeries<double>(this);
            m_PressurePlot = AddPlot(new PlotAttributes("Exhaustion Pressure", EPlotShapes.Histogram, Color.Gray, Color.Empty, 4, 0, true));
            m_FatalLine = AddPlot(new PlotAttributes("Fatal Threshold", EPlotShapes.Line, Color.Red, Color.Empty, 2, 0, true));
        }

        protected override void StartCalc()
        {
            m_ExhaustionEnergy.Clear();
        }

        protected override void CalcBar()
        {
            // 1. CALCULATE BAR DURATION
            double dur = (Bars.CurrentBar > 1) ? (Bars.Time[0] - Bars.Time[1]).TotalSeconds : 60.0;
            if (dur <= 0) dur = 0.1;
            m_BarDuration.Value = dur;

            // 2. SCAN FOR REVERSAL PATTERNS (Update the "Fatal" Energy list)
            UpdateExhaustionDatabase();

            // 3. CALCULATE CURRENT "VOLUME ENERGY" (Volume / Time)
            double curEnergy = (double)Bars.Volume[0] / dur;
            
            // 4. MAP THE ENERGY TO THE "FATAL SAMPLES"
            if (m_ExhaustionEnergy.Count > 0) {
                double avgFatalEnergy = GetAverage(m_ExhaustionEnergy);
                m_FatalLine.Set(avgFatalEnergy);
                
                // Set the Histogram
                m_PressurePlot.Set(curEnergy);
                
                // Color Logic: Is current energy matching the fatal historical signature?
                if (curEnergy >= (avgFatalEnergy * 0.95)) {
                    m_PressurePlot.BGColor = Color.Red; // FATAL ZONE
                } else if (curEnergy >= (avgFatalEnergy * 0.70)) {
                    m_PressurePlot.BGColor = Color.Orange; // CAUTION ZONE
                } else {
                    m_PressurePlot.BGColor = Color.LightSkyBlue; // SAFE ZONE
                }
            } else {
                m_PressurePlot.Set(curEnergy);
                m_PressurePlot.BGColor = Color.Gray;
            }
        }

        private void UpdateExhaustionDatabase()
        {
            m_ExhaustionEnergy.Clear();
            if (Bars.CurrentBar < ScanLookback + 5) return;

            // Find all historical reversals in the last 30 bars
            for (int i = 1; i <= ScanLookback; i++) {
                bool isReversalAt_iMinus1 = (Bars.Close[i] > Bars.Open[i] && Bars.Close[i-1] < Bars.Open[i-1]) || (Bars.Close[i] < Bars.Open[i] && Bars.Close[i-1] > Bars.Open[i-1]);
                if (isReversalAt_iMinus1 && IsHistoricalRun(i, MinTrendRun)) {
                    // This was a FATAL brick—it lead to a reversal.
                    // Store its Volume Energy (Volume / Duration)
                    double fDur = (i < Bars.CurrentBar-1) ? (Bars.Time[i] - Bars.Time[i+1]).TotalSeconds : 60.0;
                    if (fDur <= 0) fDur = 0.1;
                    double fEnergy = (double)Bars.Volume[i] / fDur;
                    m_ExhaustionEnergy.Add(fEnergy);
                }
            }
        }

        private bool IsHistoricalRun(int startIdx, int runLen) {
            bool bull = true; bool bear = true;
            for (int j = 0; j < runLen; j++) {
                if (Bars.Close[startIdx+j] < Bars.Open[startIdx+j]) bull = false;
                if (Bars.Close[startIdx+j] > Bars.Open[startIdx+j]) bear = false;
            }
            return bull || bear;
        }

        private double GetAverage(List<double> list) {
            if (list.Count == 0) return 0;
            double sum = 0; foreach (double d in list) sum += d;
            return sum / (double)list.Count;
        }
    }
}
