using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    [UpdateOnEveryTick(true)] // Update on every tick for real-time projections
    public class projected_future_renko_horz : IndicatorObject
    {
        [Input]
        public int TickOffset { get; set; }

        [Input]
        public Color BullishColor { get; set; }

        [Input]
        public Color BearishColor { get; set; }

        [Input]
        public int LineLength { get; set; }

        private IPlotObject m_Plot;
        private ITrendLineObject m_BullishLine;
        private ITrendLineObject m_BearishLine;
        private double m_LastClosePrice;
        private DateTime m_LastCloseTime;
        private bool m_LastBarWasUp;
        private double m_BoxSize;
        private bool m_NeedToUpdate;

        public projected_future_renko_horz(object ctx) : base(ctx)
        {
            TickOffset = 15; // Default to 15 ticks
            BullishColor = Color.Green; // Default to green for bullish
            BearishColor = Color.Red; // Default to red for bearish
            LineLength = 30; // Default line length in bars
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

            // Skip historical calculation - only calculate for real-time data
            if (!Environment.IsRealTimeCalc)
            {
                Output.WriteLine("Skipping historical calculation - indicator only works in real-time");
                return;
            }

            // Initialize with the most recent bar data
            if (Bars.CurrentBar > 1) // Check if we have at least 2 bars
            {
                // Get the last completed bar
                double currentClose = Bars.Close[0];
                double previousClose = Bars.Close[1];

                // Determine bar direction (up or down)
                bool isUpBar = currentClose > previousClose;

                // Calculate the box size for Renko bars
                m_BoxSize = Math.Abs(currentClose - previousClose);

                // Store the values
                m_LastClosePrice = currentClose;
                m_LastCloseTime = Bars.Time[0];
                m_LastBarWasUp = isUpBar;

                // Set flag to draw on first calculation
                m_NeedToUpdate = true;

                Output.WriteLine("Initialized with existing bar data");
            }
            else
            {
                // Not enough bars yet
                m_NeedToUpdate = false;
                m_LastClosePrice = 0;
                m_BoxSize = 0;
            }
        }

        // Track the current bar index to detect new bars
        private int m_LastBarIndex = -1;

        protected override void CalcBar()
        {
            // Keep indicator active with a constant value that won't affect the chart
            m_Plot.Set(0);

            // Skip historical calculation - only calculate for real-time data
            if (!Environment.IsRealTimeCalc)
            {
                return;
            }

            // Only process on bar close
            if (Bars.Status == EBarState.Close)
            {
                // Check if this is a new bar
                bool isNewBar = (Bars.CurrentBar != m_LastBarIndex);

                if (isNewBar)
                {
                    // Store the current bar's information
                    double currentClose = Bars.Close[0];
                    double previousClose = m_LastClosePrice;

                    // Determine bar direction (up or down)
                    bool isUpBar = currentClose > previousClose;

                    // Calculate the Renko box size based on the current bar
                    if (previousClose > 0)
                    {
                        m_BoxSize = Math.Abs(currentClose - previousClose);
                    }
                    else
                    {
                        // For the first bar, use the difference between open and close
                        m_BoxSize = Math.Abs(currentClose - Bars.Open[0]);
                    }

                    // Store the current values for next comparison
                    m_LastClosePrice = currentClose;
                    m_LastCloseTime = Bars.Time[0];
                    m_LastBarWasUp = isUpBar;
                    m_LastBarIndex = Bars.CurrentBar;

                    // Clear previous lines
                    ClearLines();
                    m_NeedToUpdate = true;

                    Output.WriteLine("New bar detected - Last bar was " + (isUpBar ? "UP" : "DOWN"));
                }
            }

            // Draw the projections if needed
            if (m_NeedToUpdate)
            {
                DrawHorizontalProjections();
                m_NeedToUpdate = false;
            }
        }

        private void DrawHorizontalProjections()
        {
            try
            {
                // Clear previous lines
                ClearLines();

                // Calculate the tick size
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;

                // Calculate projection prices based on the direction of the last bar
                double bullishProjection, bearishProjection;

                if (m_LastBarWasUp)
                {
                    // For up bars, project TickOffset ticks above the close
                    bullishProjection = m_LastClosePrice + (TickOffset * tickSize);
                    // For down projection, use the same offset below
                    bearishProjection = m_LastClosePrice - (TickOffset * tickSize);
                }
                else
                {
                    // For down bars, project TickOffset ticks below the close
                    bearishProjection = m_LastClosePrice - (TickOffset * tickSize);
                    // For up projection, use the same offset above
                    bullishProjection = m_LastClosePrice + (TickOffset * tickSize);
                }

                // Calculate start and end points for the trend lines
                // Start at the current bar
                DateTime startTime = Bars.Time[0];

                // Make the lines extend based on the LineLength parameter
                DateTime endTime = startTime.AddSeconds(LineLength);

                Output.WriteLine("Drawing horizontal projections - Bullish: " + bullishProjection + ", Bearish: " + bearishProjection);

                try
                {
                    // Draw bullish projection (horizontal line above the close)
                    ChartPoint bullishStart = new ChartPoint(startTime, bullishProjection);
                    ChartPoint bullishEnd = new ChartPoint(endTime, bullishProjection);

                    m_BullishLine = DrwTrendLine.Create(bullishStart, bullishEnd);
                    m_BullishLine.Color = BullishColor;

                    // Draw bearish projection (horizontal line below the close)
                    ChartPoint bearishStart = new ChartPoint(startTime, bearishProjection);
                    ChartPoint bearishEnd = new ChartPoint(endTime, bearishProjection);

                    m_BearishLine = DrwTrendLine.Create(bearishStart, bearishEnd);
                    m_BearishLine.Color = BearishColor;

                    Output.WriteLine("Horizontal projections drawn successfully");
                }
                catch (Exception ex)
                {
                    Output.WriteLine("Error drawing projections: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in DrawHorizontalProjections method: " + ex.Message);
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
