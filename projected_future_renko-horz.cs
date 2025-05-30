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
        public int Level1 { get; set; }

        [Input]
        public int Level2 { get; set; }

        [Input]
        public Color BullishColor { get; set; }

        [Input]
        public Color BearishColor { get; set; }

        [Input]
        public int LineLength { get; set; }

        private IPlotObject m_Plot;
        private ITrendLineObject m_Level1Line;
        private ITrendLineObject m_Level2Line;
        private double m_LastClosePrice;
        private DateTime m_LastCloseTime;
        private bool m_LastBarWasUp;
        private double m_BoxSize;
        private bool m_NeedToUpdate;

        public projected_future_renko_horz(object ctx) : base(ctx)
        {
            Level1 = 15; // Default to 15 ticks
            Level2 = 30; // Default to 30 ticks
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
                double level1Projection, level2Projection;

                if (m_LastBarWasUp)
                {
                    // For up bars, project both levels above the close
                    level1Projection = m_LastClosePrice + (Level1 * tickSize);
                    level2Projection = m_LastClosePrice + (Level2 * tickSize);

                    // Calculate start and end points for the trend lines
                    DateTime startTime = Bars.Time[0];
                    DateTime endTime = startTime.AddSeconds(LineLength);

                    Output.WriteLine("Drawing bullish projections - Level1: " + level1Projection + ", Level2: " + level2Projection);

                    try
                    {
                        // Draw Level1 projection (horizontal line above the close)
                        ChartPoint level1Start = new ChartPoint(startTime, level1Projection);
                        ChartPoint level1End = new ChartPoint(endTime, level1Projection);

                        m_Level1Line = DrwTrendLine.Create(level1Start, level1End);
                        m_Level1Line.Color = BullishColor;

                        // Draw Level2 projection (horizontal line further above the close)
                        ChartPoint level2Start = new ChartPoint(startTime, level2Projection);
                        ChartPoint level2End = new ChartPoint(endTime, level2Projection);

                        m_Level2Line = DrwTrendLine.Create(level2Start, level2End);
                        m_Level2Line.Color = BullishColor;

                        Output.WriteLine("Bullish projections drawn successfully");
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("Error drawing bullish projections: " + ex.Message);
                    }
                }
                else
                {
                    // For down bars, project both levels below the close
                    level1Projection = m_LastClosePrice - (Level1 * tickSize);
                    level2Projection = m_LastClosePrice - (Level2 * tickSize);

                    // Calculate start and end points for the trend lines
                    DateTime startTime = Bars.Time[0];
                    DateTime endTime = startTime.AddSeconds(LineLength);

                    Output.WriteLine("Drawing bearish projections - Level1: " + level1Projection + ", Level2: " + level2Projection);

                    try
                    {
                        // Draw Level1 projection (horizontal line below the close)
                        ChartPoint level1Start = new ChartPoint(startTime, level1Projection);
                        ChartPoint level1End = new ChartPoint(endTime, level1Projection);

                        m_Level1Line = DrwTrendLine.Create(level1Start, level1End);
                        m_Level1Line.Color = BearishColor;

                        // Draw Level2 projection (horizontal line further below the close)
                        ChartPoint level2Start = new ChartPoint(startTime, level2Projection);
                        ChartPoint level2End = new ChartPoint(endTime, level2Projection);

                        m_Level2Line = DrwTrendLine.Create(level2Start, level2End);
                        m_Level2Line.Color = BearishColor;

                        Output.WriteLine("Bearish projections drawn successfully");
                    }
                    catch (Exception ex)
                    {
                        Output.WriteLine("Error drawing bearish projections: " + ex.Message);
                    }
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
                if (m_Level1Line != null)
                {
                    m_Level1Line.Delete();
                    m_Level1Line = null;
                }

                if (m_Level2Line != null)
                {
                    m_Level2Line.Delete();
                    m_Level2Line = null;
                }
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }
    }
}
