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
        public Color ProjectionColor { get; set; }

        private IPlotObject m_Plot;
        private List<ITrendLineObject> m_Lines;
        private bool m_NeedToUpdate;
        private double m_LastClosePrice;
        private double m_ProjectedPrice;
        private DateTime m_LastCloseTime;
        private bool m_LastBarWasUp;

        public projected_future_renko(object ctx) : base(ctx)
        {
            BarWidthPixels = 5; // Default to 5 pixels width
            ProjectionColor = Color.Green; // Default to green projection
        }

        protected override void Create()
        {
            // Create an invisible plot that doesn't affect the chart
            m_Plot = AddPlot(new PlotAttributes("Projection", EPlotShapes.Line, Color.Transparent));
            m_Lines = new List<ITrendLineObject>();
            m_NeedToUpdate = false;
        }

        protected override void StartCalc()
        {
            ClearLines();
            m_NeedToUpdate = false;
            m_LastClosePrice = 0;
            m_ProjectedPrice = 0;
            m_LastBarWasUp = false;
        }

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
                    
                    // Calculate the Renko box size based on the current bar
                    double boxSize = 0;
                    
                    if (previousClose > 0)
                    {
                        boxSize = Math.Abs(currentClose - previousClose);
                    }
                    else
                    {
                        // For the first bar, use the difference between open and close
                        boxSize = Math.Abs(currentClose - Bars.Open[0]);
                    }
                    
                    // Store the current values for next comparison
                    m_LastClosePrice = currentClose;
                    m_LastCloseTime = Bars.Time[0];
                    m_LastBarWasUp = isUpBar;
                    
                    // Project the next bar in the same direction
                    if (isUpBar)
                    {
                        m_ProjectedPrice = currentClose + boxSize;
                    }
                    else
                    {
                        m_ProjectedPrice = currentClose - boxSize;
                    }
                    
                    // Clear previous projections and draw a new one
                    ClearLines();
                    m_NeedToUpdate = true;
                    
                    Output.WriteLine("New bar detected - Projecting next Renko at price: " + m_ProjectedPrice);
                }
            }
            
            // Draw the projection if needed
            if (m_NeedToUpdate)
            {
                DrawProjection();
                m_NeedToUpdate = false;
            }
        }

        private void DrawProjection()
        {
            try
            {
                // Calculate the Renko box coordinates
                DateTime leftTime = m_LastCloseTime;
                
                // Use a small time increment for width
                DateTime rightTime = leftTime.AddMilliseconds(BarWidthPixels * 10); // Scale factor of 10ms per pixel
                
                // Determine the bottom and top prices based on direction
                double bottomPrice, topPrice;
                
                if (m_LastBarWasUp)
                {
                    // For up bars, bottom is last close, top is projected price
                    bottomPrice = m_LastClosePrice;
                    topPrice = m_ProjectedPrice;
                }
                else
                {
                    // For down bars, bottom is projected price, top is last close
                    bottomPrice = m_ProjectedPrice;
                    topPrice = m_LastClosePrice;
                }
                
                Output.WriteLine("Drawing projection from " + bottomPrice + " to " + topPrice);
                
                try
                {
                    // Create points for the rectangle
                    ChartPoint bottomLeft = new ChartPoint(leftTime, bottomPrice);
                    ChartPoint bottomRight = new ChartPoint(rightTime, bottomPrice);
                    ChartPoint topLeft = new ChartPoint(leftTime, topPrice);
                    ChartPoint topRight = new ChartPoint(rightTime, topPrice);
                    
                    // Draw bottom line
                    ITrendLineObject bottomLine = DrwTrendLine.Create(bottomLeft, bottomRight);
                    bottomLine.Color = ProjectionColor;
                    m_Lines.Add(bottomLine);
                    
                    // Draw top line
                    ITrendLineObject topLine = DrwTrendLine.Create(topLeft, topRight);
                    topLine.Color = ProjectionColor;
                    m_Lines.Add(topLine);
                    
                    // Draw left line
                    ITrendLineObject leftLine = DrwTrendLine.Create(bottomLeft, topLeft);
                    leftLine.Color = ProjectionColor;
                    m_Lines.Add(leftLine);
                    
                    // Draw right line
                    ITrendLineObject rightLine = DrwTrendLine.Create(bottomRight, topRight);
                    rightLine.Color = ProjectionColor;
                    m_Lines.Add(rightLine);
                    
                    Output.WriteLine("Projection drawing complete");
                }
                catch (Exception ex)
                {
                    Output.WriteLine("Error creating projection: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in DrawProjection method: " + ex.Message);
            }
        }

        // Helper method to clear all lines
        private void ClearLines()
        {
            if (m_Lines != null)
            {
                foreach (var line in m_Lines)
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
                
                m_Lines.Clear();
            }
        }
    }
}
