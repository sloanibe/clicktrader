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
        private DateTime m_StalkingStartTime = DateTime.MinValue;
        private bool m_FoundEntry = false;
        private IArrowObject m_ExecutionMarker;
        private ITrendLineObject m_HistoricalClickLine;
        private ITextObject m_DiagnosticLabel;

        public RenkoTailSimulation(object ctx) : base(ctx) { }

        protected override void Create() { }

        protected override void StartCalc()
        {
            // Removed automatic reset to allow stalking points to persist during refreshes
        }

        protected override void CalcBar()
        {
            // Optional: Live Bars still check the logic if stalking is active
            if (Bars.CurrentBar <= 10) return;
            if (m_StalkingStartTime == DateTime.MinValue || m_FoundEntry) return;

            if (Bars.Time[0] < m_StalkingStartTime) return;

            CheckForSetup(0);
        }

        private void CheckForSetup(int barsAgo)
        {
            if (barsAgo + 1 > Bars.CurrentBar) return;

            bool isBlue = Bars.Close[barsAgo] > Bars.Open[barsAgo];
            double currentLow = Math.Round(Bars.Low[barsAgo], 6);
            double previousLow = Math.Round(Bars.Low[barsAgo + 1], 6);
            double precision = (double)Bars.Info.MinMove / Bars.Info.PriceScale;

            // --- LONG ONLY: THE 'DEEP PIERCE' SETUP ---
            if (isBlue && currentLow <= (previousLow + (precision * 0.1)))
            {
                MarkEntry("LONG DEEP PIERCE", true, barsAgo, -1);
            }
        }

        private void MarkEntry(string label, bool isLong, int barsAgo, int clickedBar)
        {
            m_FoundEntry = true;
            double arrowPrice = isLong ? Bars.Low[barsAgo] : Bars.High[barsAgo];
            Color  entryColor = isLong ? Color.DodgerBlue : Color.Red;
            double brickSize  = GetBrickSize();

            m_ExecutionMarker = DrwArrow.Create(new ChartPoint(Bars.Time[barsAgo], arrowPrice), isLong);
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
                m_DiagnosticLabel.Text = string.Format("FOUND: {0} @ {1:HH:mm:ss} ({2} bars later)", 
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
            // Update Diagnostic Label
            if (m_DiagnosticLabel == null)
            {
                m_DiagnosticLabel = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.Close[0]), "STALKER ACTIVE");
                m_DiagnosticLabel.Locked = true;
                m_DiagnosticLabel.Color = Color.White;
                m_DiagnosticLabel.Size = 14;
            }
            m_DiagnosticLabel.Location = new ChartPoint(Bars.Time[0], Bars.Close[0]);

            // INTERACTION: You can change this to Right button if you prefer
            if (arg.buttons != MouseButtons.Left) return;
            if ((arg.keys & Keys.Control) != Keys.Control)
            {
                m_DiagnosticLabel.Text = string.Format("CLICK @ {0:HH:mm:ss} (Hold CTRL to Start Scan)", arg.point.Time);
                return;
            }

            // 1. SET THE STARTING POINT
            m_StalkingStartTime = arg.point.Time;
            m_FoundEntry = false;
            
            // 2. CLEAR OLD DRAWINGS
            if (m_HistoricalClickLine != null) m_HistoricalClickLine.Delete();
            if (m_ExecutionMarker != null) m_ExecutionMarker.Delete();

            // 3. MARK THE 'CONTEXT' POINT (Where you clicked)
            double brickSize = GetBrickSize();
            m_HistoricalClickLine = DrwTrendLine.Create(
                new ChartPoint(arg.point.Time, arg.point.Price - (brickSize * 25)),
                new ChartPoint(arg.point.Time, arg.point.Price + (brickSize * 25)));
            m_HistoricalClickLine.Color = Color.Gold;
            m_HistoricalClickLine.Style = ETLStyle.ToolDashed;

            m_DiagnosticLabel.Text = "SCANNIG FROM " + arg.point.Time.ToString("HH:mm:ss");

            // 4. THE FORWARD SCAN (Looking for the FIRST valid entry)
            int clickedBar = arg.bar_number;
            int currentBar = Bars.CurrentBar;

            // Start at the CLICKED bar (i = clickedBar) to ensure we don't skip it!
            for (int i = clickedBar; i <= currentBar; i++)
            {
                int barsAgo = currentBar - i;
                CheckForHistoricalScan(barsAgo, clickedBar);
                
                if (m_FoundEntry) 
                {
                    // Success! We found the first one. Breaking the loop.
                    break;
                }
            }
            
            if (!m_FoundEntry)
                m_DiagnosticLabel.Text = "NO ENTRY FOUND IN RECENT HISTORY";

            // THE SLEDGEHAMMER: Force MultiCharts to recalculate and refresh the 
            // chart immediately, even on the weekend when there are no ticks!
            ExecControl.Recalculate();
        }

        private void CheckForHistoricalScan(int barsAgo, int clickedBar)
        {
            if (barsAgo + 1 > Bars.CurrentBar) return;

            bool isBlue = Bars.Close[barsAgo] > Bars.Open[barsAgo];
            if (!isBlue) return; // We only care about Long (Blue) signals

            double currentLow = Math.Round(Bars.Low[barsAgo], 6);
            double previousLow = Math.Round(Bars.Low[barsAgo + 1], 6);
            double precision = (double)Bars.Info.MinMove / Bars.Info.PriceScale;

            // --- DEEP PIERCE LOGIC (Any Preceding Color) ---
            if (currentLow <= (previousLow + (precision * 0.1)))
            {
                MarkEntry("LONG DEEP PIERCE", true, barsAgo, clickedBar);
            }
        }
    }
}
