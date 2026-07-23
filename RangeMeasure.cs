using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    // Two F2+left-click chart measurement tool. Mouse movement is not exposed
    // by the PowerLanguage .NET chart API, so the measurement is finalized on
    // the second selected point.
    [SameAsSymbol(true), MouseEvents(true), UpdateOnEveryTick(true), RecoverDrawings(false)]
    public class RangeMeasure : IndicatorObject
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [Input]
        public int ContractQuantity { get; set; }

        private double m_TickSize;
        private bool m_HasFirstPoint;
        private bool m_HasCompletedMeasurement;
        private bool m_F2WasMissingOnLastClick;
        private double m_FirstPrice;
        private DateTime m_FirstTime;
        private double m_SecondPrice;
        private DateTime m_SecondTime;

        private ITextObject m_ModeLabel;
        private ITrendLineObject m_MeasureLine;
        private ITextObject m_MeasureLabel;
        private ITextObject m_FirstPointLabel;

        public RangeMeasure(object ctx) : base(ctx)
        {
            ContractQuantity = 1;
        }

        protected override void Create()
        {
            // This is a drawing-only indicator.
        }

        protected override void StartCalc()
        {
            m_TickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (m_TickSize <= 0) m_TickSize = 0.25;
        }

        protected override void CalcBar()
        {
            if (m_TickSize <= 0) {
                m_TickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
                if (m_TickSize <= 0) m_TickSize = 0.25;
            }
            if (m_HasCompletedMeasurement) RenderCompletedMeasurement();
            UpdateModeLabel();
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            // F2 + right-click is an explicit clear/reset gesture.  It keeps
            // the indicator loaded while removing the selected points, line,
            // and measurement text.
            if (arg.buttons == MouseButtons.Right && IsF2Held(arg.keys))
            {
                m_HasFirstPoint = false;
                m_F2WasMissingOnLastClick = false;
                ClearMeasurementDrawings();
                UpdateModeLabel();
                return;
            }

            if (arg.buttons != MouseButtons.Left) return;
            if (!IsF2Held(arg.keys)) {
                m_F2WasMissingOnLastClick = true;
                UpdateModeLabel();
                return;
            }
            m_F2WasMissingOnLastClick = false;

            double clickedPrice = RoundToTick(arg.point.Price);
            // MouseClickArgs already supplies the exact chart time under the
            // pointer.  Do not derive it from bar_number: that value varies
            // for empty chart space and caused selected points to be discarded.
            DateTime clickedTime = arg.point.Time;
            if (!m_HasFirstPoint) {
                // The first F2+left-click begins a fresh measurement.
                m_HasFirstPoint = false;
                ClearMeasurementDrawings();
                m_FirstPrice = clickedPrice;
                m_FirstTime = clickedTime;
                m_HasFirstPoint = true;
                DrawFirstPoint();
                UpdateModeLabel();
                return;
            }

            DrawMeasurement(clickedPrice, clickedTime);
            m_HasFirstPoint = false;
            UpdateModeLabel();
        }

        private bool IsF2Held(Keys eventKeys)
        {
            // This mirrors the RangeBarTrading F12 safety-click check. Some
            // MultiCharts layouts omit function keys from MouseClickArgs, so
            // also query the physical Windows key state at click time.
            if ((eventKeys & Keys.KeyCode) == Keys.F2) return true;
            try {
                return (GetAsyncKeyState((int)Keys.F2) & 0x8000) != 0;
            } catch {
                return false;
            }
        }

        private void UpdateModeLabel()
        {
            if (Bars.CurrentBar < 0) return;
            string text;
            if (m_F2WasMissingOnLastClick)
                text = "RANGE MEASURE: F2 WAS NOT DETECTED";
            else if (m_HasFirstPoint)
                text = "RANGE MEASURE: F2+LEFT-CLICK SECOND POINT";
            else
                text = "RANGE MEASURE: F2+LEFT-CLICK FIRST POINT";

            SetModeLabel(text, m_F2WasMissingOnLastClick ? Color.Maroon :
                                m_HasFirstPoint ? Color.Navy : Color.Black);
        }

        private void SetModeLabel(string text, Color color)
        {
            double modeLabelPrice = Bars.High[0] + (18 * m_TickSize);
            ChartPoint point = new ChartPoint(Bars.Time[0], modeLabelPrice);
            if (m_ModeLabel == null) {
                m_ModeLabel = DrwText.Create(point, text);
                m_ModeLabel.Size = 10;
                m_ModeLabel.HStyle = ETextStyleH.Right;
                m_ModeLabel.VStyle = ETextStyleV.Above;
            }
            m_ModeLabel.Location = point;
            m_ModeLabel.Text = text;
            m_ModeLabel.Color = color;
        }

        private void DrawFirstPoint()
        {
            if (m_FirstPointLabel != null) m_FirstPointLabel.Delete();
            m_FirstPointLabel = DrwText.Create(
                new ChartPoint(m_FirstTime, m_FirstPrice), "1");
            m_FirstPointLabel.Color = Color.Navy;
            m_FirstPointLabel.Size = 12;
            m_FirstPointLabel.HStyle = ETextStyleH.Center;
            m_FirstPointLabel.VStyle = ETextStyleV.Above;
        }

        private void DrawMeasurement(double secondPrice, DateTime secondTime)
        {
            ClearMeasurementDrawings();
            m_SecondPrice = secondPrice;
            m_SecondTime = secondTime;
            m_HasCompletedMeasurement = true;
            RenderCompletedMeasurement();
        }

        private void RenderCompletedMeasurement()
        {
            ChartPoint firstPoint = new ChartPoint(m_FirstTime, m_FirstPrice);
            ChartPoint secondPoint = new ChartPoint(m_SecondTime, m_SecondPrice);
            if (m_MeasureLine == null || !m_MeasureLine.Exist) {
                m_MeasureLine = DrwTrendLine.Create(firstPoint, secondPoint);
                m_MeasureLine.Color = Color.Navy;
                m_MeasureLine.Style = ETLStyle.ToolSolid;
                m_MeasureLine.Size = 2;
                m_MeasureLine.ExtRight = false;
            } else {
                m_MeasureLine.Begin = firstPoint;
                m_MeasureLine.End = secondPoint;
            }

            double tickDifference = (m_SecondPrice - m_FirstPrice) / m_TickSize;
            double dollarPerTick = m_TickSize * Bars.Info.BigPointValue;
            double dollarDifference = tickDifference * dollarPerTick * Math.Max(1, ContractQuantity);
            double seconds = Math.Abs((m_SecondTime - m_FirstTime).TotalSeconds);
            string direction = tickDifference > 0 ? "UP" :
                               tickDifference < 0 ? "DOWN" : "FLAT";
            string text = string.Format(
                "{0} {1:0.##} ticks | {2:C2} ({3} ctr) | {4:0} sec",
                direction, Math.Abs(tickDifference), Math.Abs(dollarDifference),
                Math.Max(1, ContractQuantity), seconds);

            if (m_MeasureLabel == null || !m_MeasureLabel.Exist) {
                m_MeasureLabel = DrwText.Create(secondPoint, text);
                m_MeasureLabel.Color = Color.Navy;
                m_MeasureLabel.Size = 11;
                m_MeasureLabel.HStyle = ETextStyleH.Left;
            }
            m_MeasureLabel.Location = secondPoint;
            m_MeasureLabel.Text = text;
            m_MeasureLabel.VStyle = tickDifference >= 0
                ? ETextStyleV.Above
                : ETextStyleV.Below;
        }

        private void ClearMeasurementDrawings()
        {
            m_HasCompletedMeasurement = false;
            if (m_MeasureLine != null) { m_MeasureLine.Delete(); m_MeasureLine = null; }
            if (m_MeasureLabel != null) { m_MeasureLabel.Delete(); m_MeasureLabel = null; }
            if (m_FirstPointLabel != null) { m_FirstPointLabel.Delete(); m_FirstPointLabel = null; }
        }

        private double RoundToTick(double price)
        {
            return Math.Round(price / m_TickSize) * m_TickSize;
        }

        protected override void Destroy()
        {
            ClearMeasurementDrawings();
            if (m_ModeLabel != null) { m_ModeLabel.Delete(); m_ModeLabel = null; }
        }
    }
}
