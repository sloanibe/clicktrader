using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    [UpdateOnEveryTick(true)] // CRITICAL: This allows us to see "Inside" the forming brick
    public class renko_ha_sync : IndicatorObject
    {
        private double m_IntHAOpen, m_IntHAHigh, m_IntHALow, m_IntHAClose;
        private DateTime m_LastHAStartTime;
        
        // BUFFERS FOR PATTERN MATCHING
        private List<double> m_PrevHABodies = new List<double>();
        private List<double> m_PrevHAHighs = new List<double>();
        private List<double> m_PrevHALows = new List<double>();
        private List<int> m_PrevHAColors = new List<int>(); // 1 = Blue, -1 = Red
        
        private IPlotObject m_HASyncPlot;

        public renko_ha_sync(object ctx) : base(ctx) { }

        protected override void Create()
        {
            m_HASyncPlot = AddPlot(new PlotAttributes("Internal HA Sync", EPlotShapes.Point, Color.Fuchsia, Color.Empty, 14, 0, true));
        }

        protected override void StartCalc()
        {
            m_IntHAOpen = Bars.Open[0];
            m_IntHAHigh = Bars.High[0];
            m_IntHALow = Bars.Low[0];
            m_IntHAClose = Bars.Close[0];
            m_LastHAStartTime = Bars.Time[0];
        }

        protected override void CalcBar()
        {
            // 1. RAW TICK ACCESS
            // We analyze the momentum of the current tick relative to the current Renko brick.
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale; if (tickSize <= 0) tickSize = 0.25;
            double curPrice = Bars.Close[0];
            DateTime curTime = Bars.Time[0];

            // 2. INTERNAL 1-SECOND "SLICE" MANAGEMENT
            if ((curTime - m_LastHAStartTime).TotalSeconds >= 1.0) {
               // Record the completed slice before resetting
               RecordHASlice(m_IntHAOpen, m_IntHAHigh, m_IntHALow, m_IntHAClose);
               
               m_IntHAOpen = (m_IntHAOpen + m_IntHAClose) / 2.0; 
               m_IntHAHigh = Math.Max(curPrice, m_IntHAOpen);
               m_IntHALow = Math.Min(curPrice, m_IntHAOpen);
               m_IntHAClose = curPrice;
               m_LastHAStartTime = curTime;
            } else {
               m_IntHAClose = curPrice;
               m_IntHAHigh = Math.Max(m_IntHAHigh, curPrice);
               m_IntHALow = Math.Min(m_IntHALow, curPrice);
            }

            // 3. EXHAUSTION ANALYSIS (Look back at the last 5 slices)
            bool isExhausted = CheckForExhaustionSlices();

            // 4. THE "GO" TRIGGER (SURGICAL EVENT-BASED)
            bool haBullish = m_IntHAClose > m_IntHAOpen;
            bool haBearish = m_IntHAClose < m_IntHAOpen;
            
            // Did the internal HA JUST FLIP into the trend color?
            bool justFlippedBull = haBullish && (m_PrevHAColors.Count > 0 && m_PrevHAColors[0] == -1);
            bool justFlippedBear = haBearish && (m_PrevHAColors.Count > 0 && m_PrevHAColors[0] == 1);

            if (justFlippedBull && Bars.Close[0] > Bars.Open[0] && isExhausted) {
                m_HASyncPlot.Set(Bars.Low[0] - (tickSize * 6));
                m_HASyncPlot.BGColor = Color.Fuchsia; // THE ENTRY BOLT
            } else if (justFlippedBear && Bars.Close[0] < Bars.Open[0] && isExhausted) {
                m_HASyncPlot.Set(Bars.High[0] + (tickSize * 6));
                m_HASyncPlot.BGColor = Color.Fuchsia;
            }
        }

        private void RecordHASlice(double o, double h, double l, double c) {
            m_PrevHABodies.Insert(0, Math.Abs(c - o));
            m_PrevHAColors.Insert(0, (c > o) ? 1 : -1);
            m_PrevHAHighs.Insert(0, h);
            m_PrevHALows.Insert(0, l);
            if (m_PrevHABodies.Count > 10) {
                m_PrevHABodies.RemoveAt(10); m_PrevHAColors.RemoveAt(10);
                m_PrevHAHighs.RemoveAt(10); m_PrevHALows.RemoveAt(10);
            }
        }

        private bool CheckForExhaustionSlices() {
            if (m_PrevHABodies.Count < 3) return false;
            // Does history show shrinking bodies or long opposing tails?
            bool slowing = m_PrevHABodies[0] < m_PrevHABodies[1];
            bool hasTails = false;
            if (Bars.Close[0] > Bars.Open[0]) { // Bullish Trend
                hasTails = (m_PrevHAHighs[0] - Math.Max(m_IntHAOpen, m_IntHAClose)) > m_PrevHABodies[0];
            } else { // Bearish Trend
                hasTails = (Math.Min(m_IntHAOpen, m_IntHAClose) - m_PrevHALows[0]) > m_PrevHABodies[0];
            }
            return slowing || hasTails;
        }
    }
}
