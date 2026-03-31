    using System;
    using System.Drawing;
    using System.Collections.Generic;
    using PowerLanguage;
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
            public int ManualLevelTicks { get; set; } // 0 = Auto-detect

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
            private double m_LastClosePrice;
            private double m_LastOpenPrice;
            private bool m_LastBarWasUp;
            private double m_DetectedBrickSize;
            private bool m_NeedToUpdate;
            private int m_LastBarIndex = -1;
            private bool m_IsFirstRealTimeTick = true;

            public projected_future_renko_horz(object ctx) : base(ctx)
            {
                ManualLevelTicks = 0; // Default to 'Auto-Detect'
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
                m_MasterAnchorSet = false;
            }

            private bool m_MasterAnchorSet = false;

            protected override void CalcBar()
            {
                if (!Environment.IsRealTimeCalc)
                {
                    // History: Track the latest closed bar data
                    if (Bars.Status == EBarState.Close)
                    {
                        m_LastClosePrice = Bars.Close[0];
                        m_LastOpenPrice = Bars.Open[0];
                        m_LastBarWasUp = (Bars.Close[0] >= Bars.Open[0]);
                        m_LastBarIndex = Bars.CurrentBar;
                        
                        // Measure the brick body size
                        m_DetectedBrickSize = Math.Abs(Bars.Close[0] - Bars.Open[0]);
                    }
                    return;
                }

                if (m_IsFirstRealTimeTick)
                {
                    m_NeedToUpdate = true;
                    m_IsFirstRealTimeTick = false;
                }

                if (Bars.Status == EBarState.Close)
                {
                    if (Bars.CurrentBar != m_LastBarIndex)
                    {
                        m_LastClosePrice = Bars.Close[0];
                        m_LastOpenPrice = Bars.Open[0];
                        m_LastBarWasUp = (Bars.Close[0] >= Bars.Open[0]);
                        m_LastBarIndex = Bars.CurrentBar;
                        m_DetectedBrickSize = Math.Abs(Bars.Close[0] - Bars.Open[0]);

                        ClearLines();
                        m_NeedToUpdate = true;
                    }
                }

                if (m_NeedToUpdate)
                {
                    DrawHorizontalProjections();
                    m_NeedToUpdate = false;
                }
            }

            private void DrawHorizontalProjections()
            {
                ClearLines();

                double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
                int numLevels = Math.Max(1, NumberOfLevels);
                DateTime startTime = Bars.Time[0];
                DateTime endTime = startTime.AddMinutes(5); 

                // RESOLUTION LOGIC: Prioritize manual input, otherwise use auto-detected brick size
                double activePointShift = 0;
                if (ManualLevelTicks > 0)
                {
                    activePointShift = ManualLevelTicks * tickSize;
                }
                else
                {
                    activePointShift = m_DetectedBrickSize;
                }

                // If both fail (brand new chart), use a safe 20-tick fallback for MNQ
                if (activePointShift <= 0) activePointShift = 20 * tickSize;

                if (m_LastBarWasUp)
                {
                    // Project Continuation levels above the close
                    for (int i = 1; i <= numLevels; i++)
                    {
                        double proj = m_LastClosePrice + (activePointShift * i);
                        var line = DrwTrendLine.Create(new ChartPoint(startTime, proj), new ChartPoint(endTime, proj));
                        line.Color = BullishColor; line.ExtRight = true; line.Size = 2;
                        m_DirectionLines.Add(line);
                    }

                    // Project Reversal levels below the open
                    if (ShowOppositeDirectionLevels)
                    {
                        for (int i = 1; i <= numLevels; i++)
                        {
                            double proj = m_LastOpenPrice - (activePointShift * i);
                            var line = DrwTrendLine.Create(new ChartPoint(startTime, proj), new ChartPoint(endTime, proj));
                            line.Color = OppositeDirectionColor; line.ExtRight = true; line.Size = 2;
                            m_OppositeDirectionLines.Add(line);
                        }
                    }
                }
                else
                {
                    // Project Continuation levels below the close
                    for (int i = 1; i <= numLevels; i++)
                    {
                        double proj = m_LastClosePrice - (activePointShift * i);
                        var line = DrwTrendLine.Create(new ChartPoint(startTime, proj), new ChartPoint(endTime, proj));
                        line.Color = BearishColor; line.ExtRight = true; line.Size = 2;
                        m_DirectionLines.Add(line);
                    }

                    // Project Reversal levels above the open
                    if (ShowOppositeDirectionLevels)
                    {
                        for (int i = 1; i <= numLevels; i++)
                        {
                            double proj = m_LastOpenPrice + (activePointShift * i);
                            var line = DrwTrendLine.Create(new ChartPoint(startTime, proj), new ChartPoint(endTime, proj));
                            line.Color = OppositeDirectionColor; line.ExtRight = true; line.Size = 2;
                            m_OppositeDirectionLines.Add(line);
                        }
                    }
                }

                Output.WriteLine("[RenkoProjection] Auto-Sync Active. Projected Distance: {0} points", activePointShift);
            }

            private void ClearLines()
            {
                foreach (var line in m_DirectionLines) if (line != null) line.Delete();
                m_DirectionLines.Clear();
                foreach (var line in m_OppositeDirectionLines) if (line != null) line.Delete();
                m_OppositeDirectionLines.Clear();
            }
        }
    }
