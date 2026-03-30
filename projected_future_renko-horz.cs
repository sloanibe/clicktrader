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

            private List<ITrendLineObject> m_DirectionLines;
            private List<ITrendLineObject> m_OppositeDirectionLines;
            private List<ITextObject> m_StatusLabels;
            private double m_LastClosePrice;
            private double m_LastOpenPrice;
            private DateTime m_LastCloseTime;
            private bool m_LastBarWasUp;
            private double m_BoxSize;
            private bool m_NeedToUpdate;

            public projected_future_renko_horz(object ctx) : base(ctx)
            {
                Level1 = 0; // Default to 'Auto-Detect' (0)
                NumberOfLevels = 1;
                BullishColor = Color.Green;
                BearishColor = Color.Red;
                ShowOppositeDirectionLevels = true;
                OppositeDirectionColor = Color.Yellow;
            }

            protected override void Create()
            {
                m_DirectionLines = new List<ITrendLineObject>();
                m_OppositeDirectionLines = new List<ITrendLineObject>();
                m_NeedToUpdate = false;
            }

            protected override void StartCalc()
            {
                ClearLines();
            }

            private int m_LastBarIndex = -1;
            private bool m_IsFirstRealTimeTick = true;


            protected override void CalcBar()
            {
                // Track historical bars to ensure we have the exact previous bar data ready
                if (!Environment.IsRealTimeCalc)
                {
                    if (Bars.Status == EBarState.Close)
                    {
                        m_LastClosePrice = Bars.Close[0];
                        m_LastOpenPrice = Bars.Open[0];
                        m_LastCloseTime = Bars.Time[0];
                        
                        if (Bars.CurrentBar > 1) 
                        {
                            m_LastBarWasUp = Bars.Close[0] > Bars.Close[1];
                        } 
                        else 
                        {
                            m_LastBarWasUp = Bars.Close[0] >= Bars.Open[0];
                        }
                        
                        m_LastBarIndex = Bars.CurrentBar;
                    }
                    return;
                }

                // Force the projection to be drawn immediately on the first real-time tick
                if (m_IsFirstRealTimeTick)
                {
                    m_NeedToUpdate = true;
                    m_IsFirstRealTimeTick = false;
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

                    // Use a short 5-minute offset for the anchor point, and use ExtRight 
                    // to extend it forever without breaking the chart auto-scaling.
                    DateTime endTime = startTime.AddMinutes(5); 

                    if (m_LastBarWasUp)
                    {
                        // For up bars, project levels above the close
                        try
                        {
                            // Draw projections in the direction of the close
                            for (int i = 1; i <= numLevels; i++)
                            {
                                // Calculate the projection level (Level1 * i ticks)
                                double levelProjection = m_LastClosePrice + (Level1 * i * tickSize);

                                // Draw the projection line
                                ChartPoint levelStart = new ChartPoint(startTime, levelProjection);
                                ChartPoint levelEnd = new ChartPoint(endTime, levelProjection);

                                ITrendLineObject line = DrwTrendLine.Create(levelStart, levelEnd);
                                line.Color = BullishColor;
                                line.ExtRight = true; // Key: This prevents the line from being used in scaling math

                                m_DirectionLines.Add(line);
                            }

                            // Draw opposite direction projections if enabled
                            if (ShowOppositeDirectionLevels)
                            {
                                for (int i = 1; i <= numLevels; i++)
                                {
                                    // Calculate the opposite projection level (Level1 * i ticks below the open)
                                    double oppLevelProjection = m_LastOpenPrice - (Level1 * i * tickSize);

                                    // Draw the opposite projection line
                                    ChartPoint oppLevelStart = new ChartPoint(startTime, oppLevelProjection);
                                    ChartPoint oppLevelEnd = new ChartPoint(endTime, oppLevelProjection);

                                    ITrendLineObject oppLine = DrwTrendLine.Create(oppLevelStart, oppLevelEnd);
                                    oppLine.Color = OppositeDirectionColor;
                                    oppLine.ExtRight = true;

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
                                // Calculate the projection level (Level1 * i ticks)
                                double levelProjection = m_LastClosePrice - (Level1 * i * tickSize);

                                // Draw the projection line
                                ChartPoint levelStart = new ChartPoint(startTime, levelProjection);
                                ChartPoint levelEnd = new ChartPoint(endTime, levelProjection);

                                ITrendLineObject line = DrwTrendLine.Create(levelStart, levelEnd);
                                line.Color = BearishColor;
                                line.ExtRight = true; // Key: This prevents the line from being used in scaling math

                                m_DirectionLines.Add(line);
                            }

                            // Draw opposite direction projections if enabled
                            if (ShowOppositeDirectionLevels)
                            {
                                for (int i = 1; i <= numLevels; i++)
                                {
                                    // Calculate the opposite projection level (Level1 * i ticks above the open)
                                    double oppLevelProjection = m_LastOpenPrice + (Level1 * i * tickSize);

                                    // Draw the opposite projection line
                                    ChartPoint oppLevelStart = new ChartPoint(startTime, oppLevelProjection);
                                    ChartPoint oppLevelEnd = new ChartPoint(endTime, oppLevelProjection);

                                    ITrendLineObject oppLine = DrwTrendLine.Create(oppLevelStart, oppLevelEnd);
                                    oppLine.Color = OppositeDirectionColor;
                                    oppLine.ExtRight = true;

                                    m_OppositeDirectionLines.Add(oppLine);
                                }
                            }
                        }
                        catch
                        {
                            throw;
                        }
                    }

                    // Just echo to the Output window so the value is easily verified
                    Output.WriteLine(string.Format("[RenkoProjection] Drawn => Level1 = {0} Ticks", Level1));
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

                    // (Status labels removed)
                }
                catch
                {
                    // Ignore errors during cleanup
                }
            }
        }
    }
