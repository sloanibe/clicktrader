using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PowerLanguage;

namespace PowerLanguage.Indicator
{
    // Chart-side manual trade study.  It does not submit broker orders.
    // Hold Up + left-click for a long, or Down + left-click for a short.
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    [MouseEvents(true)]
    [UpdateOnEveryTick(true)]
    public class ManualTrading : IndicatorObject
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [Input] public int SessionStartHour { get; set; }
        [Input] public int SessionStartMinute { get; set; }
        [Input] public int ProfitTargetTicks { get; set; }
        [Input] public int MaximumLossTicks { get; set; }
        [Input] public double RoundTripCostDollars { get; set; }

        private sealed class BarValue
        {
            public int Number;
            public DateTime Time;
            public double Open;
            public double High;
            public double Low;
            public double Close;
        }

        private sealed class ManualTrade
        {
            public int EntryBar;
            public DateTime EntryTime;
            public DateTime SessionDate;
            public int Direction;
            public double EntryPrice;
            public double EntryHigh;
            public double EntryLow;
            public bool UseProfitTarget;
            public double TargetPrice;
            public double StopPrice;
            public int LastClosedBarProcessed;
            public bool IsClosed;
            public int ExitBar;
            public DateTime ExitTime;
            public double ExitPrice;
            public IArrowObject Arrow;
            public ITextObject DailyTotalLabel;
        }

        private readonly List<ManualTrade> m_Trades = new List<ManualTrade>();
        private readonly Dictionary<DateTime, double> m_DailyNetDollars =
            new Dictionary<DateTime, double>();
        // CalcBar captures every completed bar while the study is calculated.
        // Mouse callbacks must replay these snapshots: FullSymbolData.Current
        // is a calculation cursor and is not a reliable "last completed bar"
        // for a click on a historical bar.
        private readonly Dictionary<int, BarValue> m_CompletedBars =
            new Dictionary<int, BarValue>();
        private int m_FirstCompletedBar = Int32.MaxValue;
        private int m_LastCompletedBar = -1;
        private ManualTrade m_ActiveTrade;
        private ITextObject m_StatusMessage;
        private double m_TickSize;
        private double m_BigPointValue;

        public ManualTrading(object ctx) : base(ctx)
        {
            SessionStartHour = 6;
            SessionStartMinute = 30;
            ProfitTargetTicks = 8;
            MaximumLossTicks = 16;
            RoundTripCostDollars = 0;
        }

        protected override void StartCalc()
        {
            // A full recalculation builds a fresh completed-bar timeline.
            // Old click marks cannot safely be replayed against changed data.
            ClearTradeDrawings();
            m_Trades.Clear();
            m_DailyNetDollars.Clear();
            m_ActiveTrade = null;
            ClearStatus();
            m_CompletedBars.Clear();
            m_FirstCompletedBar = Int32.MaxValue;
            m_LastCompletedBar = -1;
            m_TickSize = Bars.Info.PriceScale == 0
                ? 0
                : (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            m_BigPointValue = Bars.Info.BigPointValue;
            if (m_TickSize <= 0) m_TickSize = 1;
            if (m_BigPointValue <= 0) m_BigPointValue = 1;
        }

        protected override void CalcBar()
        {
            // FullSymbolData.Current is one-based; MouseClickArgs.bar_number is
            // an absolute, zero-based chart bar number.
            int currentBar = Bars.FullSymbolData.Current - 1;
            if (Bars.Status == EBarState.Close)
            {
                BarValue completed = ReadCurrentBar(currentBar);
                m_CompletedBars[currentBar] = completed;
                if (currentBar < m_FirstCompletedBar) m_FirstCompletedBar = currentBar;
                if (currentBar > m_LastCompletedBar) m_LastCompletedBar = currentBar;
            }

            if (m_ActiveTrade == null || m_ActiveTrade.IsClosed) return;
            if (currentBar <= m_ActiveTrade.EntryBar) return;

            // On a live/forming bar use the last price, so whichever protective
            // level actually trades first wins.  Completed historical bars have
            // only OHLC; their stop/target tie is resolved stop-first below.
            if (Bars.Status == EBarState.Inside)
            {
                double price = Bars.Close[0];
                if (StopReached(m_ActiveTrade, price))
                    CloseTrade(m_ActiveTrade, currentBar, Bars.Time[0],
                        StopFill(m_ActiveTrade, price), "16-tick stop");
                else if (TargetReached(m_ActiveTrade, price))
                    CloseTrade(m_ActiveTrade, currentBar, Bars.Time[0],
                        m_ActiveTrade.TargetPrice, "8-tick target");
                return;
            }

            if (Bars.Status == EBarState.Close &&
                currentBar > m_ActiveTrade.LastClosedBarProcessed)
            {
                BarValue bar;
                if (m_CompletedBars.TryGetValue(currentBar, out bar))
                    ProcessCompletedBar(m_ActiveTrade, bar);
            }
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            try
            {
                HandleClick(arg);
            }
            catch (Exception error)
            {
                Output.WriteLine("Manual Trading click error: " + error.Message);
            }
        }

        private void HandleClick(MouseClickArgs arg)
        {
            if (arg.buttons != MouseButtons.Left) return;

            bool up = IsHeld(arg.keys, Keys.Up);
            bool down = IsHeld(arg.keys, Keys.Down);
            if (up == down) return;

            if (ProfitTargetTicks <= 0 || MaximumLossTicks <= 0 ||
                RoundTripCostDollars < 0)
            {
                ShowStatus(arg.point, "CHECK TARGET/STOP/COST INPUTS");
                return;
            }

            BarValue bar;
            if (!TryFindClickedBar(arg.bar_number, arg.point.Time,
                                   arg.point.Price, out bar))
            {
                ShowStatus(arg.point, "NO COMPLETED BAR");
                return;
            }

            int latestCompleted = m_LastCompletedBar;
            if (bar.Number > latestCompleted)
            {
                ShowStatus(arg.point, "WAIT FOR BAR CLOSE");
                return;
            }

            DateTime sessionDate = GetSessionDate(bar.Time);
            foreach (ManualTrade prior in m_Trades)
            {
                if (prior.EntryBar == bar.Number)
                {
                    ShowStatus(arg.point, "BAR ALREADY MARKED");
                    return;
                }
                // Clicks within a session must be entered in bar order: an
                // earlier insertion would change the later trade's P&L mode.
                if (prior.SessionDate == sessionDate &&
                    prior.EntryBar > bar.Number)
                {
                    ShowStatus(arg.point, "CLICK THIS DAY IN BAR ORDER");
                    return;
                }
                if (prior.EntryBar < bar.Number &&
                    (!prior.IsClosed || prior.ExitBar >= bar.Number))
                {
                    ShowStatus(arg.point, "TRADE STILL OPEN HERE");
                    return;
                }
            }

            int direction = up ? 1 : -1;
            double dailyNet = GetDailyNet(sessionDate);
            ManualTrade trade = new ManualTrade();
            trade.EntryBar = bar.Number;
            trade.EntryTime = bar.Time;
            trade.SessionDate = sessionDate;
            trade.Direction = direction;
            trade.EntryPrice = bar.Close;
            trade.EntryHigh = bar.High;
            trade.EntryLow = bar.Low;
            trade.UseProfitTarget = dailyNet >= -0.005;
            trade.TargetPrice = bar.Close + direction * ProfitTargetTicks * m_TickSize;
            trade.StopPrice = bar.Close - direction * MaximumLossTicks * m_TickSize;
            trade.LastClosedBarProcessed = bar.Number;

            // Resolve old clicked bars against the bars already on the chart.
            // The active trade, if any, is subsequently followed tick by tick.
            for (int number = bar.Number + 1; number <= latestCompleted; number++)
            {
                BarValue subsequent;
                if (!m_CompletedBars.TryGetValue(number, out subsequent))
                {
                    Output.WriteLine(string.Format(
                        "Manual Trading: replay stopped at missing bar {0} of {1}",
                        number, latestCompleted));
                    break;
                }
                ProcessCompletedBar(trade, subsequent);
                if (trade.IsClosed) break;
            }

            if (!trade.IsClosed && m_ActiveTrade != null &&
                !m_ActiveTrade.IsClosed)
            {
                ShowStatus(arg.point, "ANOTHER TRADE IS OPEN");
                return;
            }

            // A historical trade must not overlap a later mark, either.
            foreach (ManualTrade later in m_Trades)
            {
                if (later.EntryBar <= bar.Number) continue;
                if (!trade.IsClosed || trade.ExitBar >= later.EntryBar)
                {
                    ShowStatus(arg.point, "OVERLAPS A LATER TRADE");
                    return;
                }
            }

            m_Trades.Add(trade);
            DrawEntryArrow(trade);
            if (trade.IsClosed)
                TallyAndLabel(trade);
            else
            {
                m_ActiveTrade = trade;
                // If the click was on an older completed bar, the current
                // forming bar may already have traded through a level before
                // this mouse event.  Resolve its known high/low conservatively.
                if (Bars.Status == EBarState.Inside &&
                    Bars.FullSymbolData.Current - 1 > trade.EntryBar)
                {
                    BarValue partial = ReadCurrentBar(Bars.FullSymbolData.Current - 1);
                    ProcessPartialBar(trade, partial);
                }
            }
            ClearStatus();

            Output.WriteLine(string.Format(
                "Manual Trading: {0} @ {1:F2} on {2:yyyy-MM-dd HH:mm:ss.fff}; " +
                "daily net at entry {3:C2}; {4}; replayed through bar {5}; {6}",
                direction > 0 ? "LONG" : "SHORT", bar.Close, bar.Time,
                dailyNet, trade.UseProfitTarget ? "+8/-16" :
                "opposing close/-16", trade.LastClosedBarProcessed,
                trade.IsClosed ? "closed and tallied" : "still open"));
        }

        private void ProcessCompletedBar(ManualTrade trade, BarValue bar)
        {
            if (trade.IsClosed || bar.Number <= trade.EntryBar ||
                bar.Number <= trade.LastClosedBarProcessed) return;
            trade.LastClosedBarProcessed = bar.Number;

            // An open beyond the stop is a gap: use that worse open.  For a
            // completed bar whose high and low touch both exits, assume stop
            // first because OHLC cannot establish the intrabar order.
            if (StopReached(trade, bar.Open))
            {
                CloseTrade(trade, bar.Number, bar.Time,
                    StopFill(trade, bar.Open), "16-tick stop gap");
                return;
            }
            if (StopTouched(trade, bar))
            {
                CloseTrade(trade, bar.Number, bar.Time,
                    trade.StopPrice, "16-tick stop");
                return;
            }
            if (trade.UseProfitTarget && TargetTouched(trade, bar))
            {
                CloseTrade(trade, bar.Number, bar.Time,
                    trade.TargetPrice, "8-tick target");
                return;
            }
            if (!trade.UseProfitTarget &&
                ((trade.Direction > 0 && bar.Close < bar.Open) ||
                 (trade.Direction < 0 && bar.Close > bar.Open)))
            {
                CloseTrade(trade, bar.Number, bar.Time,
                    bar.Close, "opposing-color close");
            }
        }

        private void ProcessPartialBar(ManualTrade trade, BarValue bar)
        {
            if (trade.IsClosed) return;
            if (StopReached(trade, bar.Open))
                CloseTrade(trade, bar.Number, bar.Time,
                    StopFill(trade, bar.Open), "16-tick stop gap");
            else if (StopTouched(trade, bar))
                CloseTrade(trade, bar.Number, bar.Time,
                    trade.StopPrice, "16-tick stop");
            else if (trade.UseProfitTarget && TargetTouched(trade, bar))
                CloseTrade(trade, bar.Number, bar.Time,
                    trade.TargetPrice, "8-tick target");
        }

        private bool StopReached(ManualTrade trade, double price)
        {
            return trade.Direction > 0
                ? price <= trade.StopPrice
                : price >= trade.StopPrice;
        }

        private bool TargetReached(ManualTrade trade, double price)
        {
            return trade.UseProfitTarget && (trade.Direction > 0
                ? price >= trade.TargetPrice
                : price <= trade.TargetPrice);
        }

        private bool StopTouched(ManualTrade trade, BarValue bar)
        {
            return StopReached(trade, trade.Direction > 0 ? bar.Low : bar.High);
        }

        private bool TargetTouched(ManualTrade trade, BarValue bar)
        {
            return TargetReached(trade, trade.Direction > 0 ? bar.High : bar.Low);
        }

        private double StopFill(ManualTrade trade, double price)
        {
            return trade.Direction > 0
                ? Math.Min(price, trade.StopPrice)
                : Math.Max(price, trade.StopPrice);
        }

        private void CloseTrade(ManualTrade trade, int barNumber,
                                DateTime time, double price, string reason)
        {
            if (trade.IsClosed) return;
            trade.IsClosed = true;
            trade.ExitBar = barNumber;
            trade.ExitTime = time;
            trade.ExitPrice = price;
            if (m_ActiveTrade == trade) m_ActiveTrade = null;

            // A trade being replayed on a historical click is not in m_Trades
            // yet; OnMouseEvent tallies it only after overlap checks pass.
            if (m_Trades.Contains(trade)) TallyAndLabel(trade);

            Output.WriteLine(string.Format(
                "Manual Trading: exit {0} @ {1:F2} on {2:yyyy-MM-dd HH:mm:ss.fff}",
                reason, price, time));
        }

        private void TallyAndLabel(ManualTrade trade)
        {
            double grossDollars = trade.Direction *
                (trade.ExitPrice - trade.EntryPrice) * m_BigPointValue;
            double netDollars = grossDollars - RoundTripCostDollars;
            double total = Math.Round(GetDailyNet(trade.SessionDate) + netDollars,
                2, MidpointRounding.AwayFromZero);
            m_DailyNetDollars[trade.SessionDate] = total;

            double labelPrice = trade.Direction > 0
                ? trade.EntryLow - 6 * m_TickSize
                : trade.EntryHigh + 6 * m_TickSize;
            string label = total > 0 ? "+$" + total.ToString("F2", CultureInfo.InvariantCulture)
                : total < 0 ? "-$" + Math.Abs(total).ToString("F2", CultureInfo.InvariantCulture)
                : "$0.00";
            ITextObject text = DrwText.Create(
                new ChartPoint(trade.EntryTime, labelPrice), label);
            if (text != null)
            {
                text.Color = total >= 0 ? Color.DarkGreen : Color.DarkRed;
                text.Size = 10;
                text.HStyle = ETextStyleH.Center;
                text.VStyle = trade.Direction > 0
                    ? ETextStyleV.Below : ETextStyleV.Above;
                trade.DailyTotalLabel = text;
            }

            Output.WriteLine(string.Format(
                "Manual Trading: trade net {0:C2}; {1:yyyy-MM-dd} " +
                "cumulative {2:C2}", netDollars, trade.SessionDate, total));
        }

        private void DrawEntryArrow(ManualTrade trade)
        {
            double arrowPrice = trade.Direction > 0
                ? trade.EntryLow - 2 * m_TickSize
                : trade.EntryHigh + 2 * m_TickSize;
            IArrowObject arrow = DrwArrow.Create(
                new ChartPoint(trade.EntryTime, arrowPrice),
                trade.Direction < 0);
            if (arrow == null) return;
            arrow.Color = trade.Direction > 0 ? Color.Blue : Color.Red;
            arrow.Size = 5;
            trade.Arrow = arrow;
        }

        private DateTime GetSessionDate(DateTime time)
        {
            int hour = Math.Max(0, Math.Min(23, SessionStartHour));
            int minute = Math.Max(0, Math.Min(59, SessionStartMinute));
            DateTime boundary = time.Date.AddHours(hour).AddMinutes(minute);
            return time >= boundary ? time.Date : time.Date.AddDays(-1);
        }

        private double GetDailyNet(DateTime sessionDate)
        {
            double value;
            return m_DailyNetDollars.TryGetValue(sessionDate, out value)
                ? value : 0;
        }

        private bool IsHeld(Keys eventKeys, Keys key)
        {
            if ((eventKeys & Keys.KeyCode) == key) return true;
            try { return (GetAsyncKeyState((int)key) & 0x8000) != 0; }
            catch { return false; }
        }

        private BarValue ReadCurrentBar(int number)
        {
            return new BarValue
            {
                Number = number,
                Time = Bars.Time[0],
                Open = Bars.Open[0],
                High = Bars.High[0],
                Low = Bars.Low[0],
                Close = Bars.Close[0]
            };
        }

        private bool TryFindClickedBar(int suggestedNumber, DateTime time,
                                       double price, out BarValue bar)
        {
            BarValue suggested;
            if (m_CompletedBars.TryGetValue(suggestedNumber, out suggested) &&
                suggested.Time == time)
            {
                bar = suggested;
                return true;
            }

            bar = null;
            double bestDistance = Double.MaxValue;
            for (int number = m_LastCompletedBar;
                 number >= m_FirstCompletedBar; number--)
            {
                BarValue candidate;
                if (!m_CompletedBars.TryGetValue(number, out candidate)) continue;
                if (candidate.Time < time) break;
                if (candidate.Time != time) continue;
                double distance = price < candidate.Low
                    ? candidate.Low - price
                    : price > candidate.High ? price - candidate.High : 0;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bar = candidate;
                }
            }
            return bar != null;
        }

        private void ShowStatus(ChartPoint point, string message)
        {
            Output.WriteLine("Manual Trading: " + message);
            try
            {
                if (m_StatusMessage == null)
                    m_StatusMessage = DrwText.Create(point, message);
                if (m_StatusMessage == null) return;
                m_StatusMessage.Location = point;
                m_StatusMessage.Text = message;
                m_StatusMessage.Color = Color.DarkOrange;
                m_StatusMessage.Size = 11;
                m_StatusMessage.HStyle = ETextStyleH.Right;
                m_StatusMessage.VStyle = ETextStyleV.Above;
            }
            catch (NullReferenceException)
            {
                m_StatusMessage = null;
            }
        }

        private void ClearStatus()
        {
            if (m_StatusMessage == null) return;
            try { m_StatusMessage.Delete(); }
            catch { }
            m_StatusMessage = null;
        }

        protected override void Destroy()
        {
            ClearTradeDrawings();
            ClearStatus();
        }

        private void ClearTradeDrawings()
        {
            foreach (ManualTrade trade in m_Trades)
            {
                try { if (trade.Arrow != null) trade.Arrow.Delete(); }
                catch { }
                try
                {
                    if (trade.DailyTotalLabel != null)
                        trade.DailyTotalLabel.Delete();
                }
                catch { }
            }
        }
    }
}
