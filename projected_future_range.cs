using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    [UpdateOnEveryTick(true)] // Add this to update on every tick
    public class projected_future_range : IndicatorObject
    {
        [Input]
        public int TickOffset { get; set; }
        
        [Input]
        public Color BullishColor { get; set; }
        
        [Input]
        public Color BearishColor { get; set; }
        
        // LineWidth removed as it's not supported by MultiCharts .NET trend lines
        
        [Input]
        public int LineLength { get; set; }

        private IPlotObject m_Plot;
        private ITrendLineObject m_BullishLine;
        private ITrendLineObject m_BearishLine;
        private double m_LastLow;
        private double m_LastHigh;
        private DateTime m_LastBarTime;
        private bool m_NeedToUpdate;

        public projected_future_range(object ctx) : base(ctx)
        {
            TickOffset = 8; // Default to 8 ticks
            BullishColor = Color.Green; // Default to green for bullish
            BearishColor = Color.Red; // Default to red for bearish
            LineLength = 30; // Default line length in bars - increased for better visibility
        }

        protected override void Create()
        {
            // Create an invisible plot that doesn't affect the chart
            m_Plot = AddPlot(new PlotAttributes("Projection", EPlotShapes.Line, Color.Transparent));
            m_NeedToUpdate = false;
        }

        protected override void StartCalc()
        {
            ClearLines();
            
            // Initialize with the most recent bar data
            if (Bars.CurrentBar > 0)
            {
                // Get the last completed bar
                m_LastLow = Bars.Low[0];
                m_LastHigh = Bars.High[0];
                m_LastBarTime = Bars.Time[0];
                
                // Set flag to draw on first calculation
                m_NeedToUpdate = true;
                
                Output.WriteLine("Initialized with existing bar data");
            }
            else
            {
                // Not enough bars yet
                m_NeedToUpdate = false;
                m_LastLow = 0;
                m_LastHigh = 0;
            }
        }

        // Track the current bar index to detect new bars
        private int m_LastBarIndex = -1;
        
        protected override void CalcBar()
        {
            // Keep indicator active with a constant value that won't affect the chart
            m_Plot.Set(0);
            
            // Check if this is a new bar
            bool isNewBar = (Bars.CurrentBar != m_LastBarIndex);
            
            // Always update with the current developing bar's data
            // For the current developing bar, we need to use Bars.Close[0] for the latest price
            m_LastLow = Bars.Status == EBarState.Close ? Bars.Low[0] : Math.Min(Bars.Low[0], Bars.Close[0]);
            m_LastHigh = Bars.Status == EBarState.Close ? Bars.High[0] : Math.Max(Bars.High[0], Bars.Close[0]);
            m_LastBarTime = Bars.Time[0];
            m_LastBarIndex = Bars.CurrentBar;
            
            // Output debug info
            Output.WriteLine("Current bar status: " + Bars.Status + ", Low: " + m_LastLow + ", High: " + m_LastHigh);
            
            // If this is a new bar, clear old projections first
            if (isNewBar)
            {
                ClearLines();
                Output.WriteLine("New bar detected - Cleared old projections");
            }
            
            // Always draw the projections to update in real-time as the bar develops
            DrawProjections();
        }

        private void DrawProjections()
        {
            try
            {
                // Clear previous lines
                ClearLines();
                
                // Calculate the tick size
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                
                // Calculate projection prices
                double bullishProjection = m_LastLow + (TickOffset * tickSize);
                double bearishProjection = m_LastHigh - (TickOffset * tickSize);
                
                // Calculate start and end points for the trend lines
                // Start at the current bar
                DateTime startTime = Bars.Time[0];
                
                // Make the lines extend based on the LineLength parameter
                // This makes them more visible on the chart
                DateTime endTime = startTime.AddSeconds(LineLength);
                
                Output.WriteLine("Drawing projections - Bullish: " + bullishProjection + ", Bearish: " + bearishProjection);
                
                try
                {
                    // Draw bullish projection (horizontal line above the low)
                    ChartPoint bullishStart = new ChartPoint(startTime, bullishProjection);
                    ChartPoint bullishEnd = new ChartPoint(endTime, bullishProjection);
                    
                    m_BullishLine = DrwTrendLine.Create(bullishStart, bullishEnd);
                    m_BullishLine.Color = BullishColor;
                    
                    // Draw bearish projection (horizontal line below the high)
                    ChartPoint bearishStart = new ChartPoint(startTime, bearishProjection);
                    ChartPoint bearishEnd = new ChartPoint(endTime, bearishProjection);
                    
                    m_BearishLine = DrwTrendLine.Create(bearishStart, bearishEnd);
                    m_BearishLine.Color = BearishColor;
                    
                    Output.WriteLine("Projections drawn successfully");
                }
                catch (Exception ex)
                {
                    Output.WriteLine("Error drawing projections: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in DrawProjections method: " + ex.Message);
            }
        }

        private void ClearLines()
        {
            try
            {
                if (m_BullishLine != null)
                {
                    m_BullishLine.Delete();
                    m_BullishLine = null;
                }
                
                if (m_BearishLine != null)
                {
                    m_BearishLine.Delete();
                    m_BearishLine = null;
                }
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }
    }
}
