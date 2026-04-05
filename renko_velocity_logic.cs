using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    public class renko_velocity_logic : IndicatorObject
    {
        [Input] public int AvgLookback { get; set; }
        [Input] public double VelocityFactor { get; set; } // 1.2 = 20% faster than average
        [Input] public int FastEMAPeriod { get; set; }
        [Input] public int SlowEMAPeriod { get; set; }
        [Input] public int MasterEMAPeriod { get; set; }
        [Input] public bool ShowArrows { get; set; }

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_MasterEMA;
        
        private VariableSeries<double> m_BarDuration;
        private XAverage m_AvgDuration;

        public renko_velocity_logic(object ctx) : base(ctx)
        {
            AvgLookback = 10; VelocityFactor = 1.2;
            FastEMAPeriod = 5; SlowEMAPeriod = 15; MasterEMAPeriod = 60;
            ShowArrows = true;
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this); m_SlowEMA = new XAverage(this); m_MasterEMA = new XAverage(this);
            m_BarDuration = new VariableSeries<double>(this);
            m_AvgDuration = new XAverage(this);
        }

        protected override void StartCalc()
        {
            m_FastEMA.Length = FastEMAPeriod; m_FastEMA.Price = Bars.Close;
            m_SlowEMA.Length = SlowEMAPeriod; m_SlowEMA.Price = Bars.Close;
            m_MasterEMA.Length = MasterEMAPeriod; m_MasterEMA.Price = Bars.Close;
        }

        protected override void CalcBar()
        {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale; if (tickSize <= 0) tickSize = 0.25;
            
            // 1. CALCULATE BAR DURATION
            if (Bars.CurrentBar > 1) {
                m_BarDuration.Value = (Bars.Time[0] - Bars.Time[1]).TotalSeconds;
            } else {
                m_BarDuration.Value = 60.0;
            }
            
            // 2. UPDATING VELOCITY BASELINE
            m_AvgDuration.Price = m_BarDuration;
            double avgDur = m_AvgDuration[0];
            double curDur = m_BarDuration[0];
            
            // 3. STRUCTURAL TREND CHECK
            double em5 = m_FastEMA[0]; double em15 = m_SlowEMA[0]; double em60 = m_MasterEMA[0];
            double a60 = GetAngle(em60, m_MasterEMA[5], 5, tickSize);
            double a15 = GetAngle(em15, m_SlowEMA[5], 5, tickSize);
            
            bool bullsFanned = em5 > em15 && em15 > em60 && a15 >= 30 && a60 >= 30;
            bool bearsFanned = em5 < em15 && em15 < em60 && a15 <= -30 && a60 <= -30;

            // 4. VELOCITY DECISION: Faster than average?
            bool highVelocity = (curDur > 0 && curDur <= (avgDur / VelocityFactor));

            // 5. DRAWING REAL ARROWS (Under/Above Bar)
            if (ShowArrows && highVelocity && Bars.Status == EBarState.Close) {
                if (bullsFanned && Bars.Close[0] > Bars.Open[0]) {
                     IArrowObject arrow = DrwArrow.Create(new ChartPoint(Bars.Time[0], Bars.Low[0] - (tickSize * 4)), false);
                     if (arrow != null) { arrow.Color = Color.Lime; arrow.Size = 4; }
                } else if (bearsFanned && Bars.Close[0] < Bars.Open[0]) {
                     IArrowObject arrow = DrwArrow.Create(new ChartPoint(Bars.Time[0], Bars.High[0] + (tickSize * 4)), true);
                     if (arrow != null) { arrow.Color = Color.Red; arrow.Size = 4; }
                }
            }
        }

        private double GetAngle(double vCur, double vOld, int bBack, double tSize) {
            double rise = vCur - vOld; double run = (double)bBack * tSize; 
            return Math.Atan2(rise, run) * (180.0 / Math.PI);
        }
    }
}
