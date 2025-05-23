using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    public class projected_future_renko : IndicatorObject
    {
        [Input]
        public int BarWidthPixels { get; set; }

        [Input]
        public Color SameDirectionColor { get; set; }

        [Input]
        public Color OppositeDirectionColor { get; set; }

        [Input]
        public bool ShowOppositeProjection { get; set; }

        private IPlotObject m_Plot;
        private List<ITrendLineObject> m_SameDirectionLines;
        private List<ITrendLineObject> m_OppositeDirectionLines;
        private bool m_NeedToUpdate;
        private double m_LastClosePrice;
        private double m_SameDirectionPrice;
        private double m_OppositeDirectionPrice;
        private DateTime m_LastCloseTime;
        private bool m_LastBarWasUp;
        private double m_BoxSize;

        public projected_future_renko(object ctx) : base(ctx)
        {
            BarWidthPixels = 5; // Default to 5 pixels width
            SameDirectionColor = Color.Yellow; // Default to yellow for same direction
            OppositeDirectionColor = Color.Red; // Default to red for opposite direction
            ShowOppositeProjection = true; // Enable opposite direction projection by default
        }

        protected override void Create()
        {
            // Create an invisible plot that doesn't affect the chart
            m_Plot = AddPlot(new PlotAttributes("Projection", EPlotShapes.Line, Color.Transparent));
            m_SameDirectionLines = new List<ITrendLineObject>();
            m_OppositeDirectionLines = new List<ITrendLineObject>();
            m_NeedToUpdate = false;
        }

        protected override void StartCalc()
        {
            ClearAllLines();
            
            // Initialize with the most recent bar data so we don't have to wait for a new bar
            if (Bars.CurrentBar > 1) // Check if we have at least 2 bars
            {
                // Get the last completed bar
                double currentClose = Bars.Close[0];
                double previousClose = Bars.Close[1];
                
                // Determine bar direction (up or down)
                bool isUpBar = currentClose > previousClose;
                
                // Detect if this is a color change bar (direction changed from previous bar)
                m_IsColorChangeBar = false;
                if (Bars.CurrentBar > 2) // Need at least 3 bars to detect color change
                {
                    double priorClose = Bars.Close[2]; // Two bars ago
                    bool priorBarWasUp = previousClose > priorClose;
                    m_IsColorChangeBar = (isUpBar != priorBarWasUp);
                }
                
                Output.WriteLine("Initial bar direction: " + (isUpBar ? "UP" : "DOWN") + ", Color change: " + (m_IsColorChangeBar ? "YES" : "NO"));
                
                // Calculate the box size
                m_BoxSize = Math.Abs(currentClose - previousClose);
                
                // Store the values
                m_LastClosePrice = currentClose;
                m_LastCloseTime = Bars.Time[0];
                m_LastBarWasUp = isUpBar;
                
                // Calculate projection prices
                if (isUpBar)
                {
                    // For up bars, project one box size up from close
                    m_SameDirectionPrice = currentClose + m_BoxSize;
                    // For opposite direction, project one box size down from close
                    m_OppositeDirectionPrice = currentClose - m_BoxSize;
                }
                else
                {
                    // For down bars, project one box size down from close
                    m_SameDirectionPrice = currentClose - m_BoxSize;
                    // For opposite direction, project one box size up from close
                    m_OppositeDirectionPrice = currentClose + m_BoxSize;
                }
                
                // Draw projections immediately instead of waiting for CalcBar
                DrawSameDirectionProjection(m_IsColorChangeBar);
                
                if (ShowOppositeProjection)
                {
                    DrawOppositeDirectionProjection(m_IsColorChangeBar);
                }
                
                // No need to set flag since we already drew the projections
                m_NeedToUpdate = false;
                
                Output.WriteLine("Initialized and drew projections with existing bar data");
            }
            else
            {
                // Not enough bars yet
                m_NeedToUpdate = false;
                m_LastClosePrice = 0;
                m_SameDirectionPrice = 0;
                m_OppositeDirectionPrice = 0;
                m_BoxSize = 0;
                m_LastBarWasUp = false;
            }
        }

        // Class-level variable to track color change
        private bool m_IsColorChangeBar = false;
        
        protected override void CalcBar()
        {
            // Keep indicator active with a constant value that won't affect the chart
            m_Plot.Set(0);

            // Only process on bar close
            if (Bars.Status == EBarState.Close)
            {
                // Determine if this is a new bar close
                bool isNewBar = false;

                // Check if this is a new bar or the first bar
                if (m_LastClosePrice == 0)
                {
                    isNewBar = true;
                }
                else if (Bars.Close[0] != m_LastClosePrice)
                {
                    isNewBar = true;
                }

                if (isNewBar)
                {
                    // Store the current bar's information
                    double currentClose = Bars.Close[0];
                    double previousClose = m_LastClosePrice;

                    // Determine bar direction (up or down)
                    bool isUpBar = (currentClose > previousClose) || (previousClose == 0 && currentClose > Bars.Open[0]);
                
                    // Detect if this is a color change bar (direction changed from previous bar)
                    m_IsColorChangeBar = false;
                    if (Bars.CurrentBar > 2) // Need at least 3 bars to detect color change
                    {
                        double priorClose = Bars.Close[2]; // Two bars ago
                        bool priorBarWasUp = previousClose > priorClose;
                        m_IsColorChangeBar = (isUpBar != priorBarWasUp);
                    }
                
                    Output.WriteLine("Bar direction: " + (isUpBar ? "UP" : "DOWN") + ", Color change: " + (m_IsColorChangeBar ? "YES" : "NO"));

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

                    // Project the next bar in the same direction
                    if (isUpBar)
                    {
                        // For up bars, project one box size up from close
                        m_SameDirectionPrice = currentClose + m_BoxSize;
                        // For opposite direction, project one box size down from close
                        m_OppositeDirectionPrice = currentClose - m_BoxSize;
                    }
                    else
                    {
                        // For down bars, project one box size down from close
                        m_SameDirectionPrice = currentClose - m_BoxSize;
                        // For opposite direction, project one box size up from close
                        m_OppositeDirectionPrice = currentClose + m_BoxSize;
                    }

                    // Clear previous projections
                    ClearAllLines();
                    m_NeedToUpdate = true;

                    Output.WriteLine("New bar detected - Last bar was " + (isUpBar ? "UP" : "DOWN"));
                    Output.WriteLine("Same direction projection: " + m_SameDirectionPrice);
                    Output.WriteLine("Opposite direction projection: " + m_OppositeDirectionPrice);
                }
            }

            // Draw the projections if needed
            if (m_NeedToUpdate)
            {
                // Always draw same direction projection
                DrawSameDirectionProjection(m_IsColorChangeBar);

                // Only draw opposite direction if enabled
                if (ShowOppositeProjection)
                {
                    DrawOppositeDirectionProjection(m_IsColorChangeBar);
                }

                m_NeedToUpdate = false;
            }
        }

        private void DrawSameDirectionProjection(bool isColorChangeBar = false)
        {
            try
            {
                // Calculate the Renko box coordinates
                DateTime leftTime = m_LastCloseTime;

                // Use a small time increment for width
                DateTime rightTime = leftTime.AddMilliseconds(BarWidthPixels * 10); // Scale factor of 10ms per pixel

                // Determine the bottom and top prices based on direction
                double bottomPrice, topPrice;
                
                // For Renko bars, we need to ensure the projection is exactly one box size
                // For color change bars, we need special handling
                if (m_LastBarWasUp)
                {
                    if (isColorChangeBar)
                    {
                        // For color change up bars, we need to use half the box size
                        bottomPrice = m_LastClosePrice;
                        topPrice = bottomPrice + (m_BoxSize / 2); // Half box size for color change bars
                        Output.WriteLine("Color change UP bar - Same direction projection height: " + (topPrice - bottomPrice));
                    }
                    else
                    {
                        // For regular up bars, bottom is last close, top is exactly one box size higher
                        bottomPrice = m_LastClosePrice;
                        topPrice = bottomPrice + m_BoxSize; // Exactly one box size
                    }
                }
                else
                {
                    if (isColorChangeBar)
                    {
                        // For color change down bars, we need to use half the box size
                        topPrice = m_LastClosePrice;
                        bottomPrice = topPrice - (m_BoxSize / 2); // Half box size for color change bars
                        Output.WriteLine("Color change DOWN bar - Same direction projection height: " + (topPrice - bottomPrice));
                    }
                    else
                    {
                        // For regular down bars, top is last close, bottom is exactly one box size lower
                        topPrice = m_LastClosePrice;
                        bottomPrice = topPrice - m_BoxSize; // Exactly one box size
                    }
                }

                Output.WriteLine("Drawing same direction projection from " + bottomPrice + " to " + topPrice);

                try
                {
                    // Create points for the rectangle
                    ChartPoint bottomLeft = new ChartPoint(leftTime, bottomPrice);
                    ChartPoint bottomRight = new ChartPoint(rightTime, bottomPrice);
                    ChartPoint topLeft = new ChartPoint(leftTime, topPrice);
                    ChartPoint topRight = new ChartPoint(rightTime, topPrice);

                    // Draw bottom line
                    ITrendLineObject bottomLine = DrwTrendLine.Create(bottomLeft, bottomRight);
                    bottomLine.Color = SameDirectionColor;
                    m_SameDirectionLines.Add(bottomLine);

                    // Draw top line
                    ITrendLineObject topLine = DrwTrendLine.Create(topLeft, topRight);
                    topLine.Color = SameDirectionColor;
                    m_SameDirectionLines.Add(topLine);

                    // Draw left line
                    ITrendLineObject leftLine = DrwTrendLine.Create(bottomLeft, topLeft);
                    leftLine.Color = SameDirectionColor;
                    m_SameDirectionLines.Add(leftLine);

                    // Draw right line
                    ITrendLineObject rightLine = DrwTrendLine.Create(bottomRight, topRight);
                    rightLine.Color = SameDirectionColor;
                    m_SameDirectionLines.Add(rightLine);

                    Output.WriteLine("Same direction projection complete");
                }
                catch (Exception ex)
                {
                    Output.WriteLine("Error creating same direction projection: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in DrawSameDirectionProjection method: " + ex.Message);
            }
        }

        private void DrawOppositeDirectionProjection(bool isColorChangeBar = false)
        {
            try
            {
                // Calculate the Renko box coordinates
                DateTime leftTime = m_LastCloseTime;

                // Use a small time increment for width
                DateTime rightTime = leftTime.AddMilliseconds(BarWidthPixels * 10); // Scale factor of 10ms per pixel

                // For opposite direction projection, we need to start at the open of the next potential bar
                double bottomPrice, topPrice;

                // For opposite direction projection, we need special handling for color change bars
                if (m_LastBarWasUp)
                {
                    if (isColorChangeBar)
                    {
                        // For color change up bars, the opposite projection should start at the close
                        // and project one box size down
                        topPrice = m_LastClosePrice;
                        bottomPrice = topPrice - m_BoxSize; // Exactly one box size
                        Output.WriteLine("Color change UP bar - Opposite direction projection height: " + (topPrice - bottomPrice));
                    }
                    else
                    {
                        // For regular up bars, start at the open and project down
                        // For an up bar, the open is at the bottom
                        double openPrice = m_LastClosePrice - m_BoxSize;
                        
                        // Project one box size down from the open
                        topPrice = openPrice;
                        bottomPrice = openPrice - m_BoxSize; // Exactly one box size
                    }
                }
                else
                {
                    if (isColorChangeBar)
                    {
                        // For color change down bars, the opposite projection should start at the close
                        // and project one box size up
                        bottomPrice = m_LastClosePrice;
                        topPrice = bottomPrice + m_BoxSize; // Exactly one box size
                        Output.WriteLine("Color change DOWN bar - Opposite direction projection height: " + (topPrice - bottomPrice));
                    }
                    else
                    {
                        // For regular down bars, start at the open and project up
                        // For a down bar, the open is at the top
                        double openPrice = m_LastClosePrice + m_BoxSize;
                        
                        // Project one box size up from the open
                        bottomPrice = openPrice;
                        topPrice = openPrice + m_BoxSize; // Exactly one box size
                    }
                }

                Output.WriteLine("Drawing opposite direction projection from " + bottomPrice + " to " + topPrice);

                try
                {
                    // Create points for the rectangle
                    ChartPoint bottomLeft = new ChartPoint(leftTime, bottomPrice);
                    ChartPoint bottomRight = new ChartPoint(rightTime, bottomPrice);
                    ChartPoint topLeft = new ChartPoint(leftTime, topPrice);
                    ChartPoint topRight = new ChartPoint(rightTime, topPrice);

                    // Draw bottom line
                    ITrendLineObject bottomLine = DrwTrendLine.Create(bottomLeft, bottomRight);
                    bottomLine.Color = OppositeDirectionColor;
                    m_OppositeDirectionLines.Add(bottomLine);

                    // Draw top line
                    ITrendLineObject topLine = DrwTrendLine.Create(topLeft, topRight);
                    topLine.Color = OppositeDirectionColor;
                    m_OppositeDirectionLines.Add(topLine);

                    // Draw left line
                    ITrendLineObject leftLine = DrwTrendLine.Create(bottomLeft, topLeft);
                    leftLine.Color = OppositeDirectionColor;
                    m_OppositeDirectionLines.Add(leftLine);

                    // Draw right line
                    ITrendLineObject rightLine = DrwTrendLine.Create(bottomRight, topRight);
                    rightLine.Color = OppositeDirectionColor;
                    m_OppositeDirectionLines.Add(rightLine);

                    Output.WriteLine("Opposite direction projection complete");
                }
                catch (Exception ex)
                {
                    Output.WriteLine("Error creating opposite direction projection: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in DrawOppositeDirectionProjection method: " + ex.Message);
            }
        }

        // Helper method to clear all lines
        private void ClearAllLines()
        {
            ClearSameDirectionLines();
            ClearOppositeDirectionLines();
        }

        // Helper method to clear same direction lines
        private void ClearSameDirectionLines()
        {
            if (m_SameDirectionLines != null)
            {
                foreach (var line in m_SameDirectionLines)
                {
                    try
                    {
                        line.Delete();
                    }
                    catch
                    {
                        // Ignore errors during cleanup
                    }
                }

                m_SameDirectionLines.Clear();
            }
        }

        // Helper method to clear opposite direction lines
        private void ClearOppositeDirectionLines()
        {
            if (m_OppositeDirectionLines != null)
            {
                foreach (var line in m_OppositeDirectionLines)
                {
                    try
                    {
                        line.Delete();
                    }
                    catch
                    {
                        // Ignore errors during cleanup
                    }
                }

                m_OppositeDirectionLines.Clear();
            }
        }
    }
}
