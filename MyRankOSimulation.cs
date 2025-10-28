using System;
using System.Drawing;
using System.Collections.Generic;
using System.Windows.Forms;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true), MouseEvents(true), UpdateOnEveryTick(true), RecoverDrawings(false)]
    public class MyRankOSimulation : IndicatorObject
    {
        private enum ClickAction
        {
            None,
            Buy,
            Sell
        }

        private struct ClickRequest
        {
            public int BarNumber;
            public DateTime BarTime;
            public double ClickPrice;
            public ClickAction Action;

            public ClickRequest(int barNumber, DateTime barTime, double clickPrice, ClickAction action)
            {
                BarNumber = barNumber;
                BarTime = barTime;
                ClickPrice = clickPrice;
                Action = action;
            }
        }

        private readonly Queue<ClickRequest> m_PendingClicks = new Queue<ClickRequest>();
        private readonly Dictionary<DateTime, ITextObject> m_BuyMarkers = new Dictionary<DateTime, ITextObject>();
        private readonly Dictionary<DateTime, ITextObject> m_SellMarkers = new Dictionary<DateTime, ITextObject>();
        private readonly HashSet<DateTime> m_BuyMarkerTimes = new HashSet<DateTime>();
        private readonly HashSet<DateTime> m_SellMarkerTimes = new HashSet<DateTime>();

        public MyRankOSimulation(object ctx) : base(ctx)
        {
        }

        protected override void Create()
        {
        }

        protected override void StartCalc()
        {
        }

        protected override void CalcBar()
        {
            while (m_PendingClicks.Count > 0)
            {
                var click = m_PendingClicks.Dequeue();
                ProcessClick(click.BarNumber, click.BarTime, click.ClickPrice, click.Action, "calc");
            }

            // Refresh markers in case MultiCharts removed them during redraw
            try
            {
                foreach (var time in m_BuyMarkerTimes)
                {
                    if (!m_BuyMarkers.ContainsKey(time))
                        RecreateMarker(ClickAction.Buy, time);
                }

                foreach (var time in m_SellMarkerTimes)
                {
                    if (!m_SellMarkers.ContainsKey(time))
                        RecreateMarker(ClickAction.Sell, time);
                }
            }
            catch (Exception refreshEx)
            {
                Output.WriteLine("Marker refresh error: " + refreshEx.Message);
            }
        }

        private bool ProcessClick(int barNumber, DateTime barTime, double clickPrice, ClickAction action, string source)
        {
            if (action == ClickAction.None)
            {
                Output.WriteLine(string.Format(
                    "CLICK[{0}] | Bar={1}, Time={2:yyyy-MM-dd HH:mm:ss}, Price={3} | No action (Shift=Buy, Ctrl=Sell)",
                    source, barNumber, barTime, clickPrice));
                return false;
            }

            if (Bars.CurrentBar <= 0)
            {
                Output.WriteLine(string.Format(
                    "CLICK[{0}] | Bar={1}, Time={2:yyyy-MM-dd HH:mm:ss}, Price={3} | No bars loaded",
                    source, barNumber, barTime, clickPrice));
                return false;
            }

            int barsAgo = -1;
            int candidate = Bars.CurrentBar - barNumber;
            if (candidate >= 1 && candidate < Bars.CurrentBar)
                barsAgo = candidate;

            if (barsAgo < 0)
            {
                int maxSearch = Bars.CurrentBar;
                for (int i = 1; i < maxSearch; i++)
                {
                    TimeSpan dt = Bars.Time[i] - barTime;
                    if (dt.Duration().TotalSeconds <= 1)
                    {
                        barsAgo = i;
                        break;
                    }
                }
            }

            if (barsAgo >= 1 && barsAgo < Bars.CurrentBar)
            {
                double o = Bars.Open[barsAgo];
                double h = Bars.High[barsAgo];
                double l = Bars.Low[barsAgo];
                double c = Bars.Close[barsAgo];
                Output.WriteLine(string.Format(
                    "CLICK[{0}] | Bar={1}, Time={2:yyyy-MM-dd HH:mm:ss}, Price={3} | barsAgo={4}, OHLC={5:F2}/{6:F2}/{7:F2}/{8:F2}",
                    source, barNumber, barTime, clickPrice, barsAgo, o, h, l, c));

                try
                {
                    DrawMarker(action, Bars.Time[barsAgo], h, l);
                }
                catch (Exception drawEx)
                {
                    Output.WriteLine(string.Format(
                        "DRAW ERROR | Bar={0}, Time={1:yyyy-MM-dd HH:mm:ss} | {2}",
                        barNumber, barTime, drawEx.Message));
                }

                return true;
            }

            if (barsAgo == 0)
            {
                Output.WriteLine(string.Format(
                    "CLICK[{0}] | Bar={1}, Time={2:yyyy-MM-dd HH:mm:ss}, Price={3} | Skipped (current forming bar)",
                    source, barNumber, barTime, clickPrice));
                return false;
            }

            Output.WriteLine(string.Format(
                "CLICK[{0}] | Bar={1}, Time={2:yyyy-MM-dd HH:mm:ss}, Price={3} | Could not resolve to historical bar (curBar={4})",
                source, barNumber, barTime, clickPrice, Bars.CurrentBar));
            return false;
        }

        private ClickAction ResolveAction(Keys keys)
        {
            bool shift = (keys & Keys.Shift) == Keys.Shift;
            bool ctrl = (keys & Keys.Control) == Keys.Control;

            if (shift && ctrl)
                return ClickAction.None; // ambiguous combination
            if (shift)
                return ClickAction.Buy;
            if (ctrl)
                return ClickAction.Sell;
            return ClickAction.None;
        }

        private void DrawMarker(ClickAction action, DateTime barTime, double high, double low)
        {
            double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
            ChartPoint point;
            string label;
            Color color;
            Dictionary<DateTime, ITextObject> store;
            HashSet<DateTime> storeTimes;

            if (action == ClickAction.Buy)
            {
                double price = low - (4 * tickSize);
                point = new ChartPoint(barTime, price);
                label = "B";
                color = Color.Blue;
                store = m_BuyMarkers;
                storeTimes = m_BuyMarkerTimes;
            }
            else
            {
                double price = high + (4 * tickSize);
                point = new ChartPoint(barTime, price);
                label = "S";
                color = Color.Red;
                store = m_SellMarkers;
                storeTimes = m_SellMarkerTimes;
            }

            ITextObject existing;
            if (store.TryGetValue(barTime, out existing))
            {
                existing.Text = label;
                existing.Color = color;
                existing.Location = point;
                return;
            }

            var textObj = DrwText.Create(point, label);
            textObj.Color = color;
            textObj.VStyle = action == ClickAction.Buy ? ETextStyleV.Below : ETextStyleV.Above;
            textObj.HStyle = ETextStyleH.Center;
            textObj.Size = 12;

            store[barTime] = textObj;
            storeTimes.Add(barTime);
        }

        private void RecreateMarker(ClickAction action, DateTime barTime)
        {
            int barsAgo = GetBarsAgoFromTime(barTime);
            if (barsAgo < 1)
                return;

            double high = Bars.High[barsAgo];
            double low = Bars.Low[barsAgo];
            DrawMarker(action, barTime, high, low);
        }

        private int GetBarsAgoFromTime(DateTime barTime)
        {
            int maxSearch = Bars.CurrentBar;
            for (int i = 1; i < maxSearch; i++)
            {
                if (Bars.Time[i] == barTime)
                    return i;
            }
            return -1;
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            try
            {
                int barNumber = arg.bar_number;
                DateTime barTime = arg.point.Time;
                double clickPrice = arg.point.Price;

                ClickAction action = ResolveAction(arg.keys);
                if (action == ClickAction.None)
                {
                    Output.WriteLine(string.Format(
                        "CLICK[mouse] | Bar={0}, Time={1:yyyy-MM-dd HH:mm:ss}, Price={2} | Ignored (no modifier)",
                        barNumber, barTime, clickPrice));
                    return;
                }

                if (Bars.Status == EBarState.Inside)
                {
                    m_PendingClicks.Enqueue(new ClickRequest(barNumber, barTime, clickPrice, action));
                    Output.WriteLine(string.Format(
                        "CLICK[mouse] | Bar={0}, Time={1:yyyy-MM-dd HH:mm:ss}, Price={2}, Action={3} | Queued (status={4})",
                        barNumber, barTime, clickPrice, action, Bars.Status));
                    return;
                }

                ProcessClick(barNumber, barTime, clickPrice, action, "mouse");
            }
            catch (Exception ex)
            {
                try
                {
                    Output.WriteLine("Error handling mouse click: " + ex.GetType().FullName + ": " + ex.Message);
                    if (ex.InnerException != null)
                    {
                        Output.WriteLine("Inner: " + ex.InnerException.GetType().FullName + ": " + ex.InnerException.Message);
                    }
                    Output.WriteLine(ex.StackTrace ?? "<no stack>");
                }
                catch
                {
                    Output.WriteLine("Error handling mouse click: " + ex.Message);
                }
            }
        }
    }
}
