using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Function;
using System.Windows.Forms;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    [MouseEvents(true)]
    public class project_renko_bar : IndicatorObject
    {
        [Input]
        public int TickHeight { get; set; }

        [Input]
        public int BarWidthPixels { get; set; }

        private IPlotObject m_Plot;
        private List<ITrendLineObject> m_Lines;
        private double m_ClickPrice;
        private DateTime m_ClickTime;
        private bool m_NeedToDraw;

        public project_renko_bar(object ctx) : base(ctx)
        {
            TickHeight = 10;
            BarWidthPixels = 5; // Default to 5 pixels width
        }

        protected override void Create()
        {
            // Create an invisible plot that doesn't affect the chart
            // Use Line shape with transparent color
            m_Plot = AddPlot(new PlotAttributes("Renko", EPlotShapes.Line, Color.Transparent));
            m_Lines = new List<ITrendLineObject>();
            m_NeedToDraw = false;
        }

        protected override void StartCalc()
        {
            ClearLines();
            m_NeedToDraw = false;
        }

        protected override void CalcBar()
        {
            // Keep indicator active with a constant value that won't affect the chart
            m_Plot.Set(0);

            if (m_NeedToDraw)
            {
                DrawRectangle();
                m_NeedToDraw = false;
            }
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            // Output debug info for any mouse event
            Output.WriteLine("Mouse event received - Buttons: " + arg.buttons + ", Keys: " + arg.keys);

            // Check for Shift+Left click to draw a rectangle
            if (arg.buttons == MouseButtons.Left && (arg.keys & Keys.Shift) == Keys.Shift)
            {
                // Clear any existing rectangles first
                ClearLines();

                // Store both price and time from the click point
                m_ClickPrice = arg.point.Price;
                m_ClickTime = arg.point.Time;
                m_NeedToDraw = true;
                Output.WriteLine("SHIFT+LEFT CLICK detected at price: " + m_ClickPrice + ", time: " + m_ClickTime);
            }
            // Check for regular Left click to clear rectangles if they exist
            else if (arg.buttons == MouseButtons.Left && m_Lines.Count > 0)
            {
                // Clear any existing rectangles
                ClearLines();
                Output.WriteLine("LEFT CLICK detected - Cleared existing rectangles");
            }
            else
            {
                Output.WriteLine("No action taken for this mouse event.");
            }
        }

        private void DrawRectangle()
        {
            try
            {
                // Calculate top price
                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                double topPrice = m_ClickPrice + (TickHeight * tickSize);

                Output.WriteLine("Drawing rectangle at price range: " + m_ClickPrice + " to " + topPrice);
                Output.WriteLine("Click time: " + m_ClickTime);

                // Use a very small time increment to approximate the desired pixel width
                // The smaller the time increment, the narrower the rectangle will appear
                DateTime leftTime = m_ClickTime;

                // Use a small time increment - this will appear as approximately the desired pixel width
                // We use milliseconds to get a very narrow rectangle
                DateTime rightTime = leftTime.AddMilliseconds(BarWidthPixels * 10); // Scale factor of 10ms per pixel

                Output.WriteLine("Using bar width in pixels: " + BarWidthPixels);

                Output.WriteLine("Rectangle time range: " + leftTime + " to " + rightTime);

                try
                {
                    // Create points
                    ChartPoint bottomLeft = new ChartPoint(leftTime, m_ClickPrice);
                    Output.WriteLine("Created bottomLeft point: " + bottomLeft.Time + ", " + bottomLeft.Price);

                    ChartPoint bottomRight = new ChartPoint(rightTime, m_ClickPrice);
                    Output.WriteLine("Created bottomRight point: " + bottomRight.Time + ", " + bottomRight.Price);

                    ChartPoint topLeft = new ChartPoint(leftTime, topPrice);
                    Output.WriteLine("Created topLeft point: " + topLeft.Time + ", " + topLeft.Price);

                    ChartPoint topRight = new ChartPoint(rightTime, topPrice);
                    Output.WriteLine("Created topRight point: " + topRight.Time + ", " + topRight.Price);

                    // Try to draw each line separately with error handling
                    try
                    {
                        // Draw bottom line
                        ITrendLineObject bottomLine = DrwTrendLine.Create(bottomLeft, bottomRight);
                        bottomLine.Color = Color.Red;
                        m_Lines.Add(bottomLine);
                        Output.WriteLine("Bottom line drawn successfully");
                    }
                    catch (Exception lineEx)
                    {
                        Output.WriteLine("Error drawing bottom line: " + lineEx.Message);
                    }

                    try
                    {
                        // Draw top line
                        ITrendLineObject topLine = DrwTrendLine.Create(topLeft, topRight);
                        topLine.Color = Color.Red;
                        m_Lines.Add(topLine);
                        Output.WriteLine("Top line drawn successfully");
                    }
                    catch (Exception lineEx)
                    {
                        Output.WriteLine("Error drawing top line: " + lineEx.Message);
                    }

                    try
                    {
                        // Draw left line
                        ITrendLineObject leftLine = DrwTrendLine.Create(bottomLeft, topLeft);
                        leftLine.Color = Color.Red;
                        m_Lines.Add(leftLine);
                        Output.WriteLine("Left line drawn successfully");
                    }
                    catch (Exception lineEx)
                    {
                        Output.WriteLine("Error drawing left line: " + lineEx.Message);
                    }

                    try
                    {
                        // Draw right line
                        ITrendLineObject rightLine = DrwTrendLine.Create(bottomRight, topRight);
                        rightLine.Color = Color.Red;
                        m_Lines.Add(rightLine);
                        Output.WriteLine("Right line drawn successfully");
                    }
                    catch (Exception lineEx)
                    {
                        Output.WriteLine("Error drawing right line: " + lineEx.Message);
                    }

                    Output.WriteLine("Rectangle drawing complete");
                }
                catch (Exception pointEx)
                {
                    Output.WriteLine("Error creating chart points: " + pointEx.Message);
                    if (pointEx.StackTrace != null)
                    {
                        Output.WriteLine("Stack trace: " + pointEx.StackTrace);
                    }
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine("Error in DrawRectangle method: " + ex.Message);
                if (ex.StackTrace != null)
                {
                    Output.WriteLine("Stack trace: " + ex.StackTrace);
                }
            }
        }

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
