using System;
using System.Drawing;
using System.Collections.Generic;
using System.Windows.Forms;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [SameAsSymbol(true), MouseEvents(true), UpdateOnEveryTick(true), RecoverDrawings(false)]
    public class MyRankOSimulation : IndicatorObject
    {
        private enum ClickAction
        {
            None,
            Buy,
            Sell,
            Auto
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
        private readonly Dictionary<DateTime, ITextObject> m_ResultMarkers = new Dictionary<DateTime, ITextObject>();
        private readonly Dictionary<DateTime, int> m_ResultMarkerScores = new Dictionary<DateTime, int>();
        private const int TIME_SEARCH_LIMIT = 20000;

        private class TradeState
        {
            public int SequenceId;
            public ClickAction Direction;
            public DateTime EntryTime;
            public int EntryBarNumber;
            public double EntryPrice;
            public double BrickSize;
            public double DollarsPerBrick;
            public double TicksPerBrick;
            public int BricksInFavor;
            public int MaxFavorBricks;
            public bool FirstBrickProcessed;
            public bool Active;
        }

        private class TradeRecord
        {
            public int SequenceId;
            public ClickAction Direction;
            public DateTime EntryTime;
            public double EntryPrice;
            public DateTime ExitTime;
            public double ExitPrice;
            public int ResultBricks;
            public double ResultDollars;
        }

        private readonly List<TradeRecord> m_CurrentSessionTrades = new List<TradeRecord>();
        private TradeState m_ActiveTrade;
        private bool m_SimulationActive;
        private DateTime m_CurrentSessionDate = DateTime.MinValue;
        private double m_SessionBrickPnL;
        private double m_SessionDollarPnL;
        private int m_SessionTradeCount;
        private int m_TotalTradeCount;
        private int m_LastProcessedBarNumber = -1;
        private readonly TimeSpan SESSION_START_TIME = TimeSpan.FromHours(6);
        private double m_TickSize;
        private double m_BigPointValue;

        public MyRankOSimulation(object ctx) : base(ctx)
        {
        }

        protected override void Create()
        {
        }

        protected override void StartCalc()
        {
            m_TickSize = Bars.Info.PriceScale == 0 ? 0 : Bars.Info.MinMove / Bars.Info.PriceScale;
            m_BigPointValue = Bars.Info.BigPointValue;
            m_LastProcessedBarNumber = -1;

            Output.WriteLine(string.Format(
                "CONFIG | MaxBarsBack reported as {0}",
                ExecInfo.MaxBarsBack));
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

                foreach (var kvp in m_ResultMarkerScores)
                {
                    if (!m_ResultMarkers.ContainsKey(kvp.Key))
                    {
                        DrawResultMarker(kvp.Key, kvp.Value, true);
                    }
                }
            }
            catch (Exception refreshEx)
            {
                Output.WriteLine("Marker refresh error: " + refreshEx.Message);
            }

            if (Bars.Status == EBarState.Close)
            {
                int barNumber = Bars.CurrentBar;
                if (barNumber != m_LastProcessedBarNumber)
                {
                    m_LastProcessedBarNumber = barNumber;
                    DateTime barTime = Bars.Time[0];
                    ProcessClosedBrick(barNumber, barTime, Bars.Open[0], Bars.High[0], Bars.Low[0], Bars.Close[0]);
                }
            }
        }

        private bool ProcessClick(int barNumber, DateTime barTime, double clickPrice, ClickAction action, string source)
        {
            if (action == ClickAction.None)
                return false;

            int offset;
            DateTime resolvedTime;
            double o;
            double h;
            double l;
            double c;

            if (!TryGetFullSeriesValues(barNumber, out offset, out resolvedTime, out o, out h, out l, out c))
            {
                Output.WriteLine(string.Format(
                    "CLICK[{0}] | Bar={1}, Time={2:yyyy-MM-dd HH:mm:ss}, Price={3} | Unable to access historical data",
                    source,
                    barNumber,
                    barTime,
                    clickPrice));
                return false;
            }

            Output.WriteLine(string.Format(
                "CLICK[{0}] | Bar={1}, Time={2:yyyy-MM-dd HH:mm:ss} -> offset={3} (FullCurrent={4}, Count={5})",
                source,
                barNumber,
                resolvedTime,
                offset,
                Bars.FullSymbolData.Current,
                Bars.FullSymbolData.Count));

            ClickAction resolvedAction = action;
            if (resolvedAction == ClickAction.Auto)
                resolvedAction = DetermineActionFromBar(o, c, h, l);

            if (resolvedAction == ClickAction.None)
                return false;

            try
            {
                DrawMarker(resolvedAction, resolvedTime, h, l);
            }
            catch (Exception drawEx)
            {
                Output.WriteLine(string.Format(
                    "DRAW ERROR | Bar={0}, Time={1:yyyy-MM-dd HH:mm:ss} | {2}",
                    barNumber, resolvedTime, drawEx.Message));
            }

            BeginTrade(resolvedAction, barNumber, resolvedTime, o, h, l, c);

            return true;
        }

        private int ResolveBarsAgo(int barNumber, DateTime barTime)
        {
            if (barNumber <= Bars.CurrentBar)
            {
                int candidate = Bars.CurrentBar - barNumber;
                Output.WriteLine(string.Format(
                    "RESOLVE | BarNumber={0}, CurrentBar={1}, Candidate={2}",
                    barNumber,
                    Bars.CurrentBar,
                    candidate));
                if (candidate >= 0 && candidate <= Bars.CurrentBar)
                    return candidate;
            }

            int barsAgo = GetBarsAgoFromTime(barTime);
            if (barsAgo >= 0)
            {
                Output.WriteLine(string.Format(
                    "RESOLVE | Time match for {0:yyyy-MM-dd HH:mm:ss} -> barsAgo={1} (current={2})",
                    barTime,
                    barsAgo,
                    Bars.CurrentBar));
                return barsAgo;
            }

            Output.WriteLine(string.Format(
                "RESOLVE | Failed for Bar={0}, Time={1:yyyy-MM-dd HH:mm:ss} (current={2})",
                barNumber,
                barTime,
                Bars.CurrentBar));

            return -1;
        }

        private bool TryGetFullSeriesValues(int barNumber, out int offset, out DateTime barTime, out double open, out double high, out double low, out double close)
        {
            offset = 0;
            barTime = DateTime.MinValue;
            open = high = low = close = double.NaN;

            int fullCount = Bars.FullSymbolData.Count;
            if (fullCount == 0)
                return false;

            int fullCurrent = Bars.FullSymbolData.Current;
            if (barNumber < 0 || barNumber >= fullCount)
            {
                Output.WriteLine(string.Format(
                    "FULL | Bar={0} outside loaded range (0..{1})",
                    barNumber,
                    fullCount - 1));
                return false;
            }

            offset = fullCurrent - barNumber;

            if (offset < 0)
            {
                Output.WriteLine(string.Format(
                    "FULL | Offset={0} < 0 for bar={1} (FullCurrent={2})",
                    offset,
                    barNumber,
                    fullCurrent));
                return false;
            }

            if (offset > fullCurrent)
            {
                Output.WriteLine(string.Format(
                    "FULL | Offset={0} exceeds FullCurrent={1} for bar={2}",
                    offset,
                    fullCurrent,
                    barNumber));
                return false;
            }

            try
            {
                barTime = Bars.FullSymbolData.Time[-offset];
                open = Bars.FullSymbolData.Open[-offset];
                high = Bars.FullSymbolData.High[-offset];
                low = Bars.FullSymbolData.Low[-offset];
                close = Bars.FullSymbolData.Close[-offset];
                return true;
            }
            catch (Exception ex)
            {
                Output.WriteLine(string.Format(
                    "FULL | Failed to read bar={0} using offset={1}: {2}",
                    barNumber,
                    offset,
                    ex.Message));
                return false;
            }
        }

        private bool EnsureSessionForClick(DateTime barTime, double price)
        {
            DateTime sessionDate = barTime.Date;
            bool sessionStarted = false;

            if (!m_SimulationActive || m_CurrentSessionDate != sessionDate)
            {
                if (m_SimulationActive && m_CurrentSessionDate != DateTime.MinValue && m_CurrentSessionTrades.Count > 0)
                {
                    LogSessionSummary("rollover", barTime);
                }

                StartNewSession(sessionDate, barTime, price);
                sessionStarted = true;
            }

            return sessionStarted;
        }

        private void StartNewSession(DateTime sessionDate, DateTime barTime, double price)
        {
            m_SimulationActive = true;
            m_CurrentSessionDate = sessionDate;
            m_SessionBrickPnL = 0;
            m_SessionDollarPnL = 0;
            m_SessionTradeCount = 0;
            m_CurrentSessionTrades.Clear();

            Output.WriteLine("------------------------------------------------------------");
            Output.WriteLine(string.Format(
                "SESSION START | {0:yyyy-MM-dd} | First trade @ {1:HH:mm:ss}",
                sessionDate,
                barTime));
        }

        private void LogSessionSummary(string reason, DateTime timestamp)
        {
            Output.WriteLine(string.Format(
                "SESSION END | {0:yyyy-MM-dd} | Trades={1}, Bricks={2}, $={3:F2} | {4} @ {5:HH:mm:ss}",
                m_CurrentSessionDate,
                m_SessionTradeCount,
                m_SessionBrickPnL,
                m_SessionDollarPnL,
                reason,
                timestamp));
            Output.WriteLine("------------------------------------------------------------");
        }

        private void BeginTrade(ClickAction direction, int barNumber, DateTime barTime, double open, double high, double low, double close)
        {
            if (direction != ClickAction.Buy && direction != ClickAction.Sell)
                return;

            if (m_ActiveTrade != null && m_ActiveTrade.Active)
                return;

            EnsureSessionForClick(barTime, close);

            double brickSize = Math.Abs(close - open);
            if (brickSize <= (m_TickSize > 0 ? m_TickSize * 0.5 : 0))
            {
                brickSize = m_TickSize > 0 ? m_TickSize : Math.Abs(high - low);
                if (brickSize == 0)
                {
                    brickSize = 1;
                }
            }

            double dollarsPerBrick = brickSize * (m_BigPointValue == 0 ? 1 : m_BigPointValue);
            double ticksPerBrick = m_TickSize == 0 ? brickSize : brickSize / m_TickSize;

            m_TotalTradeCount++;
            m_ActiveTrade = new TradeState
            {
                SequenceId = m_TotalTradeCount,
                Direction = direction,
                EntryTime = barTime,
                EntryBarNumber = barNumber,
                EntryPrice = close,
                BrickSize = brickSize,
                DollarsPerBrick = dollarsPerBrick,
                TicksPerBrick = ticksPerBrick,
                BricksInFavor = 0,
                MaxFavorBricks = 0,
                FirstBrickProcessed = false,
                Active = true
            };

            Output.WriteLine("----------------------------------------------------------------------------------");
            Output.WriteLine(string.Format(
                "TRADE #{0} ENTRY | {1} | {2:yyyy-MM-dd HH:mm:ss} @ {3:F2}",
                m_ActiveTrade.SequenceId,
                direction,
                barTime,
                close));
        }

        private void ProcessClosedBrick(int barNumber, DateTime barTime, double open, double high, double low, double close)
        {
            if (m_ActiveTrade == null || !m_ActiveTrade.Active)
                return;

            if (barTime <= m_ActiveTrade.EntryTime)
                return;

            double threshold = m_TickSize > 0 ? m_TickSize * 0.1 : 0;
            bool isUpBrick = close > open + threshold;
            bool isDownBrick = close < open - threshold;

            bool brickInFavor;
            if (m_ActiveTrade.Direction == ClickAction.Buy)
            {
                brickInFavor = isUpBrick || (!isUpBrick && !isDownBrick && close >= open);
            }
            else
            {
                brickInFavor = isDownBrick || (!isUpBrick && !isDownBrick && close <= open);
            }

            bool firstBrick = !m_ActiveTrade.FirstBrickProcessed;
            m_ActiveTrade.FirstBrickProcessed = true;

            if (brickInFavor)
            {
                m_ActiveTrade.BricksInFavor++;
                if (m_ActiveTrade.BricksInFavor > m_ActiveTrade.MaxFavorBricks)
                {
                    m_ActiveTrade.MaxFavorBricks = m_ActiveTrade.BricksInFavor;
                }
                return;
            }

            int resultBricks;
            if (firstBrick)
            {
                resultBricks = -2;
            }
            else if (m_ActiveTrade.MaxFavorBricks >= 2)
            {
                resultBricks = m_ActiveTrade.MaxFavorBricks - 2;
            }
            else if (m_ActiveTrade.MaxFavorBricks == 1)
            {
                resultBricks = -1;
            }
            else
            {
                resultBricks = -2;
            }

            CloseActiveTrade(resultBricks, close, barTime, barNumber);
        }

        private void CloseActiveTrade(int resultBricks, double exitPrice, DateTime exitTime, int exitBarNumber)
        {
            if (m_ActiveTrade == null || !m_ActiveTrade.Active)
                return;

            double resultDollars = resultBricks * m_ActiveTrade.DollarsPerBrick;
            double resultTicks = resultBricks * m_ActiveTrade.TicksPerBrick;

            m_SessionBrickPnL += resultBricks;
            m_SessionDollarPnL += resultDollars;
            m_SessionTradeCount++;

            var record = new TradeRecord
            {
                SequenceId = m_ActiveTrade.SequenceId,
                Direction = m_ActiveTrade.Direction,
                EntryTime = m_ActiveTrade.EntryTime,
                EntryPrice = m_ActiveTrade.EntryPrice,
                ExitTime = exitTime,
                ExitPrice = exitPrice,
                ResultBricks = resultBricks,
                ResultDollars = resultDollars
            };
            m_CurrentSessionTrades.Add(record);

            DrawResultMarker(exitTime, resultBricks);

            double durationSeconds = (exitTime - record.EntryTime).TotalSeconds;
            if (durationSeconds < 0) durationSeconds = 0;
            string bricksLabel = resultBricks > 0 ? "+" + resultBricks.ToString() : resultBricks.ToString();
            string dollarsLabel = resultDollars >= 0 ? "+" + resultDollars.ToString("F2") : resultDollars.ToString("F2");

            Output.WriteLine(string.Format(
                "TRADE #{0} | Entry {1:yyyy-MM-dd HH:mm:ss} @ {2:F2} | Exit {3:yyyy-MM-dd HH:mm:ss} (Bar={4}) @ {5:F2} | Bricks={6} | $={7} | Duration={8:F0}s",
                record.SequenceId,
                record.EntryTime,
                record.EntryPrice,
                exitTime,
                exitBarNumber,
                exitPrice,
                bricksLabel,
                dollarsLabel,
                durationSeconds));

            m_ActiveTrade.Active = false;
            m_ActiveTrade = null;
        }

        private void ResetSimulation()
        {
            Output.WriteLine("RESET | Clearing simulation PnL and trade history");
            if (m_ActiveTrade != null && m_ActiveTrade.Active)
            {
                Output.WriteLine("RESET | Discarding active trade without settlement");
            }

            m_ActiveTrade = null;
            m_SimulationActive = false;
            m_CurrentSessionDate = DateTime.MinValue;
            m_SessionBrickPnL = 0;
            m_SessionDollarPnL = 0;
            m_SessionTradeCount = 0;
            m_CurrentSessionTrades.Clear();
        }

        private ClickAction ResolveAction(Keys keys)
        {
            bool shift = (keys & Keys.Shift) == Keys.Shift;
            bool ctrl = (keys & Keys.Control) == Keys.Control;

            if (ctrl && !shift)
                return ClickAction.Auto;
            if (shift && !ctrl)
                return ClickAction.Buy;
            if (ctrl && shift)
                return ClickAction.Sell;
            return ClickAction.None;
        }

        private ClickAction DetermineActionFromBar(double open, double close, double high, double low)
        {
            if (close > open)
                return ClickAction.Buy;
            if (close < open)
                return ClickAction.Sell;

            double upperTail = high - Math.Max(open, close);
            double lowerTail = Math.Min(open, close) - low;
            if (lowerTail > upperTail)
                return ClickAction.Buy;
            if (upperTail > lowerTail)
                return ClickAction.Sell;

            return ClickAction.Buy;
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

        private void DrawResultMarker(DateTime barTime, int resultBricks, bool isRefresh = false)
        {
            int barsAgo = GetBarsAgoFromTime(barTime);
            if (barsAgo < 0)
                return;

            double low = Bars.Low[barsAgo];
            double high = Bars.High[barsAgo];
            double tickSize = m_TickSize > 0 ? m_TickSize : (Math.Abs(Bars.High[barsAgo] - Bars.Low[barsAgo]) / 10.0);
            if (tickSize <= 0)
                tickSize = 0.01;

            string sign = resultBricks > 0 ? "+" : string.Empty;
            string label = sign + resultBricks.ToString();

            Color color;
            if (resultBricks > 0)
                color = Color.LimeGreen;
            else if (resultBricks < 0)
                color = Color.OrangeRed;
            else
                color = Color.LightGray;

            double offset = 4 * tickSize;
            double price;
            ETextStyleV vStyle;
            if (resultBricks > 0)
            {
                price = high + offset;
                vStyle = ETextStyleV.Above;
            }
            else
            {
                price = low - offset;
                vStyle = ETextStyleV.Below;
            }

            var point = new ChartPoint(barTime, price);

            ITextObject existing;
            if (m_ResultMarkers.TryGetValue(barTime, out existing))
            {
                existing.Text = label;
                existing.Color = color;
                existing.Location = point;
                existing.VStyle = vStyle;
            }
            else
            {
                var textObj = DrwText.Create(point, label);
                textObj.Color = color;
                textObj.VStyle = vStyle;
                textObj.HStyle = ETextStyleH.Center;
                textObj.Size = 10;
                m_ResultMarkers[barTime] = textObj;
            }

            m_ResultMarkerScores[barTime] = resultBricks;

            if (!isRefresh)
            {
                Output.WriteLine(string.Format(
                    "TRADE MARKER | {0:yyyy-MM-dd HH:mm:ss} | Score={1}",
                    barTime,
                    label));
            }
        }

        private void RecreateMarker(ClickAction action, DateTime barTime)
        {
            int barsAgo = GetBarsAgoFromTime(barTime);
            if (barsAgo < 1)
                return;

            int offset;
            DateTime resolvedTime;
            double open;
            double high;
            double low;
            double close;

            if (TryGetFullSeriesValues(Bars.CurrentBar - barsAgo, out offset, out resolvedTime, out open, out high, out low, out close))
            {
                DrawMarker(action, resolvedTime, high, low);
            }
        }

        private int GetBarsAgoFromTime(DateTime barTime)
        {
            if (barTime == DateTime.MinValue)
                return -1;

            int maxAgo = Math.Min(Bars.CurrentBar, TIME_SEARCH_LIMIT);
            for (int i = 0; i <= maxAgo; i++)
            {
                DateTime candidate;
                try
                {
                    candidate = Bars.Time[i];
                }
                catch
                {
                    continue;
                }

                if (candidate == barTime)
                {
                    Output.WriteLine(string.Format(
                        "RESOLVE | Exact time match at barsAgo={0} for {1:yyyy-MM-dd HH:mm:ss}",
                        i,
                        barTime));
                    return i;
                }

                double delta = Math.Abs((candidate - barTime).TotalSeconds);
                if (delta <= 1.0)
                {
                    Output.WriteLine(string.Format(
                        "RESOLVE | Near time match ({0:F2}s) at barsAgo={1} for {2:yyyy-MM-dd HH:mm:ss}",
                        delta,
                        i,
                        barTime));
                    return i;
                }
            }

            Output.WriteLine(string.Format(
                "RESOLVE | Time search exhausted (limit={0}) without match for {1:yyyy-MM-dd HH:mm:ss}",
                maxAgo,
                barTime));
            return -1;
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            try
            {
                Output.WriteLine(string.Format(
                    "MOUSE | Buttons={0}, Keys={1}, Bar={2}, Time={3:yyyy-MM-dd HH:mm:ss}",
                    arg.buttons,
                    arg.keys,
                    arg.bar_number,
                    arg.point.Time));

                if (arg.buttons != MouseButtons.Left)
                {
                    Output.WriteLine("MOUSE | Ignored because click was not left button");
                    return;
                }

                Keys keys = arg.keys;
                if ((keys & Keys.Z) == Keys.Z)
                {
                    ResetSimulation();
                    return;
                }

                int barNumber = arg.bar_number;
                DateTime barTime = arg.point.Time;
                double clickPrice = arg.point.Price;

                ClickAction action = ResolveAction(keys);
                if (action == ClickAction.None)
                {
                    Output.WriteLine(string.Format(
                        "MOUSE | Ignored because keys did not resolve to action (keys={0})",
                        keys));
                    return;
                }

                bool isCurrentFormingBar = (barNumber == Bars.CurrentBar) && (Bars.Status == EBarState.Inside);

                if (isCurrentFormingBar)
                {
                    m_PendingClicks.Enqueue(new ClickRequest(barNumber, barTime, clickPrice, action));
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
