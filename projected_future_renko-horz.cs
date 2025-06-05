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
        public int NumberOfLevels { get; set; }

        [Input]
        public Color BullishColor { get; set; }

        [Input]
        public Color BearishColor { get; set; }

        [Input]
        public bool ShowOppositeDirectionLevels { get; set; }

        [Input]
        public Color OppositeDirectionColor { get; set; }

        private IPlotObject m_Plot;
        private List<ITrendLineObject> m_DirectionLines;
        private List<ITrendLineObject> m_OppositeDirectionLines;
        private double m_LastClosePrice;
        private double m_LastOpenPrice;
        private DateTime m_LastCloseTime;
        private bool m_LastBarWasUp;
        private double m_BoxSize;
        private bool m_NeedToUpdate;

        public projected_future_renko_horz(object ctx) : base(ctx)
        {
            Level1 = 15; // Default to 15 ticks
            NumberOfLevels = 2; // Default to 2 levels
            BullishColor = Color.Green; // Default to green for bullish
            BearishColor = Color.Red; // Default to red for bearish
            ShowOppositeDirectionLevels = true; // Enable opposite direction projections by default
            OppositeDirectionColor = Color.Yellow; // Default to yellow for opposite direction
        }

        protected override void Create()
        {
            // Create an invisible plot that doesn't affect the chart
            m_Plot = AddPlot(new PlotAttributes("Projection", EPlotShapes.Line, Color.Transparent));
            m_DirectionLines = new List<ITrendLineObject>();
            m_OppositeDirectionLines = new List<ITrendLineObject>();
            m_NeedToUpdate = false;
        }

        protected override void StartCalc()
        {
            ClearLines();

            // Skip historical calculation - only calculate for real-time data
            if (!Environment.IsRealTimeCalc)
            {
                return;
            }

            // Initialize with the most recent bar data
            if (Bars.CurrentBar > 1) // Check if we have at least 2 bars
            {
                // Get the last completed bar
                double currentClose = Bars.Close[0];
                double previousClose = Bars.Close[1];

                // For Renko bars, we need to calculate the open based on the box size
                double currentOpen = Bars.Open[0];

                // Determine bar direction (up or down)
                bool isUpBar = currentClose > previousClose;

                // Calculate the box size for Renko bars
                m_BoxSize = Math.Abs(currentClose - previousClose);

                // Store the values
                m_LastClosePrice = currentClose;
                m_LastOpenPrice = currentOpen;
                m_LastCloseTime = Bars.Time[0];
                m_LastBarWasUp = isUpBar;

                // Set flag to draw on first calculation
                m_NeedToUpdate = true;
            }
            else
            {
                // Not enough bars yet
                m_NeedToUpdate = false;
                m_LastClosePrice = 0;
                m_LastOpenPrice = 0;
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
                    double currentOpen = Bars.Open[0];

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
                    m_LastOpenPrice = currentOpen;
                    m_LastCloseTime = Bars.Time[0];
                    m_LastBarWasUp = isUpBar;
                    m_LastBarIndex = Bars.CurrentBar;

                    // Clear previous lines
                    ClearLines();
                    m_NeedToUpdate = true;
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

                // Ensure NumberOfLevels is at least 1
                int numLevels = Math.Max(1, NumberOfLevels);

                // Calculate start and end points for the trend lines
                // Use the current bar time as the start
                DateTime startTime = Bars.Time[0];

                // For the end time, use a very large offset that will extend far beyond the visible chart
                // This ensures the line always extends to the right edge of the chart
                DateTime endTime = startTime.AddDays(365);  // Extend a year into the future

                if (m_LastBarWasUp)
                {
                    // For up bars, project levels above the close
                    try
                    {
                        // Draw projections in the direction of the close
                        for (int i = 1; i <= numLevels; i++)
                        {
                            // Calculate the projection level (Level1 * i)
                            double levelProjection = m_LastClosePrice + (Level1 * i * tickSize);

                            // Draw the projection line
                            ChartPoint levelStart = new ChartPoint(startTime, levelProjection);
                            ChartPoint levelEnd = new ChartPoint(endTime, levelProjection);

                            ITrendLineObject line = DrwTrendLine.Create(levelStart, levelEnd);
                            line.Color = BullishColor;

                            m_DirectionLines.Add(line);
                        }

                        // Draw opposite direction projections if enabled
                        if (ShowOppositeDirectionLevels)
                        {
                            for (int i = 1; i <= numLevels; i++)
                            {
                                // Calculate the opposite projection level (Level1 * i below the open)
                                double oppLevelProjection = m_LastOpenPrice - (Level1 * i * tickSize);

                                // Draw the opposite projection line
                                ChartPoint oppLevelStart = new ChartPoint(startTime, oppLevelProjection);
                                ChartPoint oppLevelEnd = new ChartPoint(endTime, oppLevelProjection);

                                ITrendLineObject oppLine = DrwTrendLine.Create(oppLevelStart, oppLevelEnd);
                                oppLine.Color = OppositeDirectionColor;

                                m_OppositeDirectionLines.Add(oppLine);
                            }
                        }
                    }
                    catch
                    {
                        throw;
                    }
                }
                else
                {
                    // For down bars, project levels below the close
                    try
                    {
                        // Draw projections in the direction of the close
                        for (int i = 1; i <= numLevels; i++)
                        {
                            // Calculate the projection level (Level1 * i)
                            double levelProjection = m_LastClosePrice - (Level1 * i * tickSize);

                            // Draw the projection line
                            ChartPoint levelStart = new ChartPoint(startTime, levelProjection);
                            ChartPoint levelEnd = new ChartPoint(endTime, levelProjection);

                            ITrendLineObject line = DrwTrendLine.Create(levelStart, levelEnd);
                            line.Color = BearishColor;

                            m_DirectionLines.Add(line);
                        }

                        // Draw opposite direction projections if enabled
                        if (ShowOppositeDirectionLevels)
                        {
                            for (int i = 1; i <= numLevels; i++)
                            {
                                // Calculate the opposite projection level (Level1 * i above the open)
                                double oppLevelProjection = m_LastOpenPrice + (Level1 * i * tickSize);

                                // Draw the opposite projection line
                                ChartPoint oppLevelStart = new ChartPoint(startTime, oppLevelProjection);
                                ChartPoint oppLevelEnd = new ChartPoint(endTime, oppLevelProjection);

                                ITrendLineObject oppLine = DrwTrendLine.Create(oppLevelStart, oppLevelEnd);
                                oppLine.Color = OppositeDirectionColor;

                                m_OppositeDirectionLines.Add(oppLine);
                            }
                        }
                    }
                    catch
                    {
                        throw;
                    }
                }
            }
            catch
            {
                throw;
            }
        }

        private void ClearLines()
        {
            try
            {
                // Clear direction lines
                if (m_DirectionLines != null)
                {
                    foreach (var line in m_DirectionLines)
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
                    m_DirectionLines.Clear();
                }

                // Clear opposite direction lines
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
            catch
            {
                // Ignore errors during cleanup
            }
        }
    }
}
