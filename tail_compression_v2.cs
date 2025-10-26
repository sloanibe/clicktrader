using System;
using System.Drawing;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true)]
    public class tail_compression_v2 : IndicatorObject
    {
        [Input]
        public int FastMALength { get; set; }
        
        [Input]
        public int SlowMALength { get; set; }
        
        [Input]
        public int MinBarsAboveMA { get; set; }
        
        [Input]
        public int MinBullishRatio { get; set; }
        
        [Input]
        public double VolumeCompressionPct { get; set; }
        
        [Input]
        public double RangeCompressionPct { get; set; }
        
        [Input]
        public bool EnableDebugOutput { get; set; }
        
        private IPlotObject m_BullishSignal;
        private IPlotObject m_BearishSignal;
        private XAverage m_FastMA;
        private XAverage m_SlowMA;
        
        public tail_compression_v2(object ctx) : base(ctx)
        {
            FastMALength = 10;
            SlowMALength = 20;
            MinBarsAboveMA = 1;
            MinBullishRatio = 50;
            VolumeCompressionPct = 90;
            RangeCompressionPct = 90;
            EnableDebugOutput = true;
        }
        
        protected override void Create()
        {
            m_FastMA = new XAverage(this);
            m_SlowMA = new XAverage(this);
            m_BullishSignal = AddPlot(new PlotAttributes("BullSignal", EPlotShapes.Point, Color.DarkBlue, Color.Empty, 10, 0, true));
            m_BearishSignal = AddPlot(new PlotAttributes("BearSignal", EPlotShapes.Point, Color.DarkRed, Color.Empty, 10, 0, true));
        }
        
        protected override void StartCalc()
        {
            m_FastMA.Price = Bars.Close;
            m_FastMA.Length = FastMALength;
            m_SlowMA.Price = Bars.Close;
            m_SlowMA.Length = SlowMALength;
        }
        
        protected override void CalcBar()
        {
            if (Bars.CurrentBar < Math.Max(FastMALength, SlowMALength) + 10)
                return;
            
            double fastMA = m_FastMA.Value;
            double slowMA = m_SlowMA.Value;
            double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
            
            // Check bullish setup
            if (fastMA > slowMA)
            {
                if (CheckBullishTail(fastMA, tickSize))
                {
                    m_BullishSignal.Set(0, Bars.Low[0] - (3 * tickSize));
                    if (EnableDebugOutput)
                    {
                        Output.WriteLine("BULLISH SIGNAL at bar {0}", Bars.CurrentBar);
                    }
                }
            }
            
            // Check bearish setup
            if (fastMA < slowMA)
            {
                if (CheckBearishTail(fastMA, tickSize))
                {
                    m_BearishSignal.Set(0, Bars.High[0] + (3 * tickSize));
                    if (EnableDebugOutput)
                    {
                        Output.WriteLine("BEARISH SIGNAL at bar {0}", Bars.CurrentBar);
                    }
                }
            }
        }
        
        private bool CheckBullishTail(double fastMA, double tickSize)
        {
            // Check bars above MA
            int count = 0;
            for (int i = 0; i < MinBarsAboveMA && i < Bars.CurrentBar; i++)
            {
                if (Bars.Close[i] > m_FastMA[i])
                    count++;
                else
                    break;
            }
            if (count < MinBarsAboveMA)
                return false;
            
            // Check bullish ratio
            int bullBars = 0;
            for (int i = 0; i < 10 && i < Bars.CurrentBar; i++)
            {
                if (Bars.Close[i] > Bars.Open[i])
                    bullBars++;
            }
            if ((bullBars / 10.0) * 100.0 < MinBullishRatio)
                return false;
            
            // Check for tail extending below previous bar
            if (Bars.Low[0] >= Bars.Low[1])
                return false;
            
            // Check tail length
            double bodyBottom = Math.Min(Bars.Open[0], Bars.Close[0]);
            double tailLength = bodyBottom - Bars.Low[0];
            
            if (EnableDebugOutput && tailLength > 0)
            {
                Output.WriteLine("Bar {0}: Bullish tail length = {1:F2}, Low[0]={2:F2}, Low[1]={3:F2}", 
                    Bars.CurrentBar, tailLength, Bars.Low[0], Bars.Low[1]);
            }
            
            // Check volume compression
            double avgVol = 0;
            for (int i = 1; i <= 10 && i < Bars.CurrentBar; i++)
                avgVol += Bars.Volume[i];
            avgVol = avgVol / 10.0;
            
            if (avgVol > 0 && (Bars.Volume[0] / avgVol) * 100.0 >= VolumeCompressionPct)
                return false;
            
            // Check range compression
            double avgRange = 0;
            for (int i = 1; i <= 10 && i < Bars.CurrentBar; i++)
                avgRange += (Bars.High[i] - Bars.Low[i]);
            avgRange = avgRange / 10.0;
            
            double currentRange = Bars.High[0] - Bars.Low[0];
            if (avgRange > 0 && (currentRange / avgRange) * 100.0 >= RangeCompressionPct)
                return false;
            
            return true;
        }
        
        private bool CheckBearishTail(double fastMA, double tickSize)
        {
            // Check bars below MA
            int count = 0;
            for (int i = 0; i < MinBarsAboveMA && i < Bars.CurrentBar; i++)
            {
                if (Bars.Close[i] < m_FastMA[i])
                    count++;
                else
                    break;
            }
            if (count < MinBarsAboveMA)
                return false;
            
            // Check bearish ratio
            int bearBars = 0;
            for (int i = 0; i < 10 && i < Bars.CurrentBar; i++)
            {
                if (Bars.Close[i] < Bars.Open[i])
                    bearBars++;
            }
            if ((bearBars / 10.0) * 100.0 < MinBullishRatio)
                return false;
            
            // Check for tail extending above previous bar
            if (Bars.High[0] <= Bars.High[1])
                return false;
            
            // Check tail length
            double bodyTop = Math.Max(Bars.Open[0], Bars.Close[0]);
            double tailLength = Bars.High[0] - bodyTop;
            
            if (EnableDebugOutput && tailLength > 0)
            {
                Output.WriteLine("Bar {0}: Bearish tail length = {1:F2}, High[0]={2:F2}, High[1]={3:F2}", 
                    Bars.CurrentBar, tailLength, Bars.High[0], Bars.High[1]);
            }
            
            // Check volume compression
            double avgVol = 0;
            for (int i = 1; i <= 10 && i < Bars.CurrentBar; i++)
                avgVol += Bars.Volume[i];
            avgVol = avgVol / 10.0;
            
            if (avgVol > 0 && (Bars.Volume[0] / avgVol) * 100.0 >= VolumeCompressionPct)
                return false;
            
            // Check range compression
            double avgRange = 0;
            for (int i = 1; i <= 10 && i < Bars.CurrentBar; i++)
                avgRange += (Bars.High[i] - Bars.Low[i]);
            avgRange = avgRange / 10.0;
            
            double currentRange = Bars.High[0] - Bars.Low[0];
            if (avgRange > 0 && (currentRange / avgRange) * 100.0 >= RangeCompressionPct)
                return false;
            
            return true;
        }
    }
}
