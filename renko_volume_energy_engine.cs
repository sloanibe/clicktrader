using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    public class renko_volume_energy_engine : IndicatorObject
    {
        [Input] public int AvgLookback { get; set; }
        [Input] public double EnergyFactor { get; set; } // 2.0 = Energy doubled
        [Input] public int FastEMAPeriod { get; set; }
        [Input] public int SlowEMAPeriod { get; set; }

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        
        private VariableSeries<double> m_VolumeEnergy; // Volume DIVIDED by Time
        private XAverage m_AvgEnergy;
        private VariableSeries<double> m_BarDuration;

        public renko_volume_energy_engine(object ctx) : base(ctx)
        {
            AvgLookback = 10; EnergyFactor = 2.0;
            FastEMAPeriod = 5; SlowEMAPeriod = 15;
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this); m_SlowEMA = new XAverage(this);
            m_VolumeEnergy = new VariableSeries<double>(this);
            m_BarDuration = new VariableSeries<double>(this);
            m_AvgEnergy = new XAverage(this);
        }

        protected override void StartCalc()
        {
            m_FastEMA.Length = FastEMAPeriod; m_FastEMA.Price = Bars.Close;
            m_SlowEMA.Length = SlowEMAPeriod; m_SlowEMA.Price = Bars.Close;
            m_AvgEnergy.Length = AvgLookback;
        }

        protected override void CalcBar()
        {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale; if (tickSize <= 0) tickSize = 0.25;
            
            // 1. CALCULATE BAR DURATION (SECONDS)
            double duration = 0;
            if (Bars.CurrentBar > 1) {
                duration = (Bars.Time[0] - Bars.Time[1]).TotalSeconds;
            }
            if (duration <= 0) duration = 0.1; // Floor to prevent division by zero
            m_BarDuration.Value = duration;

            // 2. CALCULATE VOLUME ENERGY (VOLUME PER SECOND)
            // This is the "Heat" of the brick.
            m_VolumeEnergy.Value = (double)Bars.Volume[0] / duration;
            
            // 3. UPDATING ENERGY BASELINE (THE HEARTBEAT)
            m_AvgEnergy.Price = m_VolumeEnergy;
            double avgEnergy = m_AvgEnergy[0];
            double curEnergy = m_VolumeEnergy[0];
            
            // 4. THE BURST DECISION (SOPHISTICATED FILTER)
            // Only fire if Energy has at least Doubled (High Magnitude) 
            // AND we have a trend-aligned fanning.
            bool energyBurst = (curEnergy >= (avgEnergy * EnergyFactor));
            bool bullsFanned = m_FastEMA[0] > m_SlowEMA[0];
            bool bearsFanned = m_FastEMA[0] < m_SlowEMA[0];

            // 5. DRAWING "SOPHISTICATED" JUMP-IN ARROWS (Flipped Booleans for platform)
            if (energyBurst && Bars.Status == EBarState.Close) {
                if (bullsFanned && Bars.Close[0] > Bars.Open[0]) {
                     IArrowObject arrow = DrwArrow.Create(new ChartPoint(Bars.Time[0], Bars.Low[0] - (tickSize * 4)), false); // UP
                     if (arrow != null) { arrow.Color = Color.Lime; arrow.Size = 4; }
                } else if (bearsFanned && Bars.Close[0] < Bars.Open[0]) {
                     IArrowObject arrow = DrwArrow.Create(new ChartPoint(Bars.Time[0], Bars.High[0] + (tickSize * 4)), true); // DOWN
                     if (arrow != null) { arrow.Color = Color.Red; arrow.Size = 4; }
                }
            }
        }
    }
}
