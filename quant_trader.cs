using System;
using System.Drawing;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    public class quant_trader : IndicatorObject
    {
        public quant_trader(object _ctx) : base(_ctx) { }

        protected override void Create()
        {
            // No initialization needed
        }

        protected override void StartCalc()
        {
            // No initialization needed
        }

        protected override void CalcBar()
        {
            // Calculate the entire range of the previous bar (high to low)
            double prevBarHigh = Bars.High[1];
            double prevBarLow = Bars.Low[1];
            bool closesWithinPrevRange = Bars.Close[0] >= prevBarLow && Bars.Close[0] <= prevBarHigh;
            
            // BULLISH PATTERN: Lower low but closes within previous bar's range
            bool hasLowerLow = Bars.Low[0] < Bars.Low[1];
            if (hasLowerLow && closesWithinPrevRange)
            {
                // Create a chart point slightly below the low of the current bar for better visibility
                // Calculate a small offset (about 10 points or ticks)
                double offset = 10 * Bars.Point;
                ChartPoint arrowPoint = new ChartPoint(Bars.Time[0], Bars.Low[0] - offset);
                
                // Create a green up arrow
                IArrowObject arrow = DrwArrow.Create(arrowPoint, false);
                // Set the arrow color to green
                arrow.Color = Color.Green;

                // Make the arrow significantly larger for better visibility
                arrow.Size = 5;

                // Optionally add some debug output
                if (Bars.Status == EBarState.Close)
                {
                    Output.WriteLine(string.Format("Bar {0}: Found BULLISH pattern - Lower low but closes within previous bar's range", Bars.CurrentBar));
                }
            }
            
            // BEARISH PATTERN: Higher high but closes within previous bar's range
            bool hasHigherHigh = Bars.High[0] > Bars.High[1];
            if (hasHigherHigh && closesWithinPrevRange)
            {
                // Create a chart point slightly above the high of the current bar for better visibility
                // Calculate a small offset (about 10 points or ticks)
                double offset = 10 * Bars.Point;
                ChartPoint arrowPoint = new ChartPoint(Bars.Time[0], Bars.High[0] + offset);
                
                // Create a red down arrow
                IArrowObject arrow = DrwArrow.Create(arrowPoint, true);
                // Set the arrow color to red
                arrow.Color = Color.Red;
                // Make the arrow significantly larger for better visibility
                arrow.Size = 5;
                
                // Optionally add some debug output
                if (Bars.Status == EBarState.Close)
                {
                    Output.WriteLine(string.Format("Bar {0}: Found BEARISH pattern - Higher high but closes within previous bar's range", Bars.CurrentBar));
                }
            }
        }
    }
}
