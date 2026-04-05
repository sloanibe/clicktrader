using System;
using System.Drawing;
using System.Windows.Forms;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [IOGMode(IOGMode.Enabled)]
    [MouseEvents(true)]
    [RecoverDrawings(true)]
    public class RenkoTailSimulation : IndicatorObject
    {
        // --- INPUTS ---
        [Input] public int    MAPeriod              { get; set; } // EMA period (default 10)
        [Input] public int    MinBarsBetweenEntries { get; set; } // Clustering filter: min bars between signals
        [Input] public int    MaxTailPierceTicks    { get; set; } // Max ticks tail may pierce through the MA
        [Input] public double MaxSlopeAngle         { get; set; } // Reserved for future automated use
        [Input] public double MinBodySeparationBricks { get; set; } // Min bricks between body and MA (trend conviction)

        private DateTime m_StalkingStartTime = DateTime.MinValue;
        private bool m_FoundEntry    = false;
        private bool m_SearchBullish = true;
        private int  m_LastEntryBar  = -1;   // Clustering: absolute bar number of last taken entry
        private IArrowObject     m_ExecutionMarker;
        private ITrendLineObject m_HistoricalClickLine;
        private ITextObject      m_DiagnosticLabel;

        // Plain array EMA - 100% reliable historical lookup at any depth
        private double[] m_EMAArray;
        private const int MaxBars = 10000;

        public RenkoTailSimulation(object ctx) : base(ctx) 
        { 
            MAPeriod                = 10;
            MinBarsBetweenEntries   = 4;
            MaxTailPierceTicks      = 5;
            MaxSlopeAngle           = 78.0; // Reserved for future automated use
            MinBodySeparationBricks = 1.0;  // Body must be at least 1 brick above/below MA
            //
            // STEEPNESS REFERENCE TABLE (brick-normalized):
            //   ~27° off vertical = EMA moves 2.0 bricks/bar  (very steep)
            //   45° off vertical  = EMA moves 1.0 bricks/bar  (steep diagonal)
            //   60° off vertical  = EMA moves 0.58 bricks/bar
            //   72° off vertical  = EMA moves 0.32 bricks/bar (new default threshold)
            //   75° off vertical  = EMA moves 0.27 bricks/bar (moderate/flat)
            //   84° off vertical  = EMA moves 0.10 bricks/bar (nearly flat — no trade)
        }

        protected override void Create() 
        {
            m_EMAArray = new double[MaxBars];
        }

        protected override void StartCalc() { }

        protected override void CalcBar()
        {
            // Store EMA in a plain array at the absolute bar index
            int idx = Bars.CurrentBar - 1;
            if (idx >= MaxBars) return;

            if (idx == 0)
            {
                m_EMAArray[0] = Bars.Close[0];
            }
            else
            {
                double alpha = 2.0 / (MAPeriod + 1.0);
                m_EMAArray[idx] = (Bars.Close[0] - m_EMAArray[idx - 1]) * alpha + m_EMAArray[idx - 1];
            }

            if (Bars.CurrentBar <= MAPeriod) return;
            if (m_StalkingStartTime == DateTime.MinValue || m_FoundEntry) return;
            if (Bars.Time[0] < m_StalkingStartTime) return;

            CheckForSetup(0);
        }
        private void CheckForSetup(int barsAgo)
        {
            if (barsAgo + 1 > Bars.CurrentBar) return;

            double precision      = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            double currentHigh    = Math.Round(Bars.High[barsAgo],      6);
            double currentLow     = Math.Round(Bars.Low[barsAgo],       6);
            double currentOpen    = Math.Round(Bars.Open[barsAgo],      6);
            double currentClose   = Math.Round(Bars.Close[barsAgo],     6);
            double prevHigh       = Math.Round(Bars.High[barsAgo + 1],  6);
            double prevLow        = Math.Round(Bars.Low[barsAgo + 1],   6);

            // --- EMA value at this bar ---
            int barIdx = Bars.CurrentBar - 1 - barsAgo;
            if (barIdx < 1 || barIdx >= MaxBars) return;
            double emaHere  = m_EMAArray[barIdx];
            double emaPrior = m_EMAArray[barIdx - 1];

            // NOTE: Steepness filter removed — the user's Control-Click IS the steepness judgment.
            // The body-above/below MA check below provides the quality gate.
            double brickSize = GetBrickSize();

            // --- RULE: Clustering Filter ---
            int absBar = Bars.CurrentBar - barsAgo;
            if (m_LastEntryBar > 0 && (absBar - m_LastEntryBar) < MinBarsBetweenEntries) return;

            double maxPierce = precision * MaxTailPierceTicks;

            if (m_SearchBullish)
            {
                bool isBlue = currentClose > currentOpen;
                if (!isBlue) return;

                // Deep Pierce: tail reaches or pierces prior bar's Low
                if (currentLow > prevLow + (precision * 0.1)) return;

                // MA Proximity: body must be ABOVE the EMA
                double bodyBottom = Math.Min(currentOpen, currentClose);
                if (bodyBottom < emaHere) return;

                // Separation: body must have meaningful daylight above MA (prevents flat/transition zone false positives)
                if ((bodyBottom - emaHere) / brickSize < MinBodySeparationBricks) return;

                // MA Proximity: tail must NOT pierce too far BELOW the EMA
                if (currentLow < (emaHere - maxPierce)) return;

                MarkEntry("LONG DEEP PIERCE", true, barsAgo, -1);
            }
            else
            {
                bool isRed = currentClose < currentOpen;
                if (!isRed) return;

                // Deep Pierce: tail (upper wick) reaches or pierces prior bar's High
                if (currentHigh < prevHigh - (precision * 0.1)) return;

                // MA Proximity: BODY must be BELOW the EMA
                double bodyTop = Math.Max(currentOpen, currentClose);
                if (bodyTop > emaHere) return;

                // Separation: body must have meaningful daylight below MA (prevents flat/transition zone false positives)
                if ((emaHere - bodyTop) / brickSize < MinBodySeparationBricks) return;

                // MA Proximity: tail must NOT pierce too far ABOVE the EMA
                if (currentHigh > (emaHere + maxPierce)) return;

                MarkEntry("SHORT DEEP PIERCE", false, barsAgo, -1);
            }
        }

        private void MarkEntry(string label, bool isLong, int barsAgo, int clickedBar)
        {
            m_FoundEntry    = true;
            m_LastEntryBar  = Bars.CurrentBar - barsAgo; // Record for clustering filter

            double arrowPrice = isLong ? Bars.Low[barsAgo] : Bars.High[barsAgo];
            Color  entryColor = isLong ? Color.DodgerBlue : Color.OrangeRed;
            double brickSize  = GetBrickSize();

            m_ExecutionMarker       = DrwArrow.Create(new ChartPoint(Bars.Time[barsAgo], arrowPrice), isLong);
            m_ExecutionMarker.Color = entryColor;

            double textPrice = isLong
                ? Bars.Low[barsAgo]  - (brickSize * 2)
                : Bars.High[barsAgo] + (brickSize * 2);

            ITextObject text = DrwText.Create(new ChartPoint(Bars.Time[barsAgo], textPrice), label);
            text.Color = entryColor;
            text.Size  = 12;

            if (clickedBar != -1 && m_DiagnosticLabel != null)
            {
                int delay = (Bars.CurrentBar - barsAgo) - clickedBar;
                m_DiagnosticLabel.Text = string.Format("REGIME: {0} | {1} @ {2:HH:mm:ss} ({3} bars later)", 
                    m_SearchBullish ? "UPTREND" : "DOWNTREND",
                    label, Bars.Time[barsAgo], delay);
            }
        }

        private double GetBrickSize()
        {
            double brick = Math.Abs(Bars.Close[0] - Bars.Open[0]);
            if (brick <= 0) brick = 5 * ((double)Bars.Info.MinMove / Bars.Info.PriceScale);
            return brick;
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            if (m_DiagnosticLabel == null)
            {
                m_DiagnosticLabel = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.Close[0]), "STALKER ACTIVE");
                m_DiagnosticLabel.Locked = true;
                m_DiagnosticLabel.Color = Color.White;
                m_DiagnosticLabel.Size = 14;
            }
            m_DiagnosticLabel.Location = new ChartPoint(Bars.Time[0], Bars.Close[0]);

            if (arg.buttons != MouseButtons.Left || (arg.keys & Keys.Control) != Keys.Control)
                return;

            // 1. RESET STATE FOR FRESH SCAN
            int clickedBar = Math.Min(arg.bar_number, Bars.CurrentBar - 2);
            clickedBar = Math.Max(clickedBar, 1);

            // 2. DETECT REGIME: Read EMA slope at the EXACT bar the user clicked
            int clickIdx = clickedBar - 1;
            clickIdx = Math.Max(1, Math.Min(clickIdx, MaxBars - 2));
            double maCurrent = m_EMAArray[clickIdx];
            double maPrior   = m_EMAArray[clickIdx - 1];
            m_SearchBullish  = maCurrent >= maPrior;

            m_StalkingStartTime = arg.point.Time;
            m_FoundEntry        = false;
            m_LastEntryBar      = -1; // Reset clustering state so prior scans don't bleed into new ones
            
            if (m_HistoricalClickLine != null) m_HistoricalClickLine.Delete();
            if (m_ExecutionMarker != null) m_ExecutionMarker.Delete();

            double brickSize = GetBrickSize();
            m_HistoricalClickLine = DrwTrendLine.Create(
                new ChartPoint(arg.point.Time, arg.point.Price - (brickSize * 25)),
                new ChartPoint(arg.point.Time, arg.point.Price + (brickSize * 25)));
            m_HistoricalClickLine.Color = m_SearchBullish ? Color.Lime : Color.OrangeRed;
            m_HistoricalClickLine.Style = ETLStyle.ToolDashed;

            m_DiagnosticLabel.Text = string.Format("STALKING {0} FROM {1:HH:mm:ss}", 
                m_SearchBullish ? "LONGS (UPTREND)" : "SHORTS (DOWNTREND)", 
                arg.point.Time);

            // 2. RUN THE DIRECTIONAL SCAN forward from clicked bar
            int currentBar = Bars.CurrentBar;
            for (int i = clickedBar; i <= currentBar; i++)
            {
                int barsAgo = currentBar - i;
                CheckForHistoricalScan(barsAgo, clickedBar);
                if (m_FoundEntry) break;
            }
            
            if (!m_FoundEntry)
                m_DiagnosticLabel.Text = "NO " + (m_SearchBullish ? "LONG" : "SHORT") + " SETUP FOUND";

            ExecControl.Recalculate();
        }

        private void CheckForHistoricalScan(int barsAgo, int clickedBar)
        {
            CheckForSetup(barsAgo);
        }
    }
}
