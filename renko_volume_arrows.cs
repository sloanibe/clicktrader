using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    public class renko_volume_arrows : IndicatorObject
    {
        [Input] public int VolLookback { get; set; }
        [Input] public double VolFactor { get; set; } // 1.5 = 50% more than average
        [Input] public int FastEMAPeriod { get; set; }
        [Input] public int SlowEMAPeriod { get; set; }
        [Input] public bool ShowArrows { get; set; }

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_AvgVolume;

        public renko_volume_arrows(object ctx) : base(ctx)
        {
            VolLookback = 20; VolFactor = 1.5;
            FastEMAPeriod = 5; SlowEMAPeriod = 15;
            ShowArrows = true;
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this); m_SlowEMA = new XAverage(this);
            m_AvgVolume = new XAverage(this);
        }

        protected override void StartCalc()
        {
            m_FastEMA.Length = FastEMAPeriod; m_FastEMA.Price = Bars.Close;
            m_SlowEMA.Length = SlowEMAPeriod; m_SlowEMA.Price = Bars.Close;
            m_AvgVolume.Length = VolLookback;
        }

        protected override void CalcBar()
        {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale; if (tickSize <= 0) tickSize = 0.25;
            
            // 1. CALCULATE VOLUME BASELINE (EMA of Volume)
            m_AvgVolume.Price = Bars.Volume;
            double avgVol = m_AvgVolume[0];
            double curVol = Bars.Volume[0];
            
            // 2. TREND ALIGNMENT
            bool bullsFanned = m_FastEMA[0] > m_SlowEMA[0];
            bool bearsFanned = m_FastEMA[0] < m_SlowEMA[0];

            // 3. VOLUME BURST DECISION
            bool volumeHigh = (curVol >= (avgVol * VolFactor));

            // 4. DRAWING ARROWS ON COMPLETION (Flipped Booleans for platform logic)
            if (ShowArrows && volumeHigh && Bars.Status == EBarState.Close) {
                if (bullsFanned && Bars.Close[0] > Bars.Open[0]) {
                     IArrowObject arrow = DrwArrow.Create(new ChartPoint(Bars.Time[0], Bars.Low[0] - (tickSize * 4)), false); // false = UP
                     if (arrow != null) { arrow.Color = Color.Lime; arrow.Size = 4; }
                } else if (bearsFanned && Bars.Close[0] < Bars.Open[0]) {
                     IArrowObject arrow = DrwArrow.Create(new ChartPoint(Bars.Time[0], Bars.High[0] + (tickSize * 4)), true); // true = DOWN
                     if (arrow != null) { arrow.Color = Color.Red; arrow.Size = 4; }
                }
            }
        }
    }
}
