using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using PowerLanguage;
using PowerLanguage.TradeManager;
using TrackerOrder = PowerLanguage.TradeManager.Order;

namespace PowerLanguage.Indicator {
    // Self-contained: paste this entire file into a PowerLanguage .NET indicator.
    [SameAsSymbol(true)]
    [UpdateOnEveryTick(true)]
    public class RangeBarTradeHistory : IndicatorObject {
        [Input] public bool UseSignalHistory { get; set; }
        [Input] public string TradeHistoryDirectory { get; set; }
        [Input] public string TradeHistoryStream { get; set; }
        [Input] public string BrokerProfile { get; set; }
        [Input] public string AccountFilter { get; set; }
        [Input] public string InstrumentOverride { get; set; }
        [Input] public string HistoryDate { get; set; }
        [Input] public int HistoryLookbackDays { get; set; }
        [Input] public int TrackerToChartOffsetMinutes { get; set; }
        [Input] public bool BarTimestampIsClose { get; set; }
        [Input] public bool ShowConnections { get; set; }
        [Input] public bool ShowLabels { get; set; }
        [Input] public bool ShowEntryBarArrows { get; set; }
        [Input] public bool ShowInfoOverlay { get; set; }
        [Input] public int ArrowLengthPixels { get; set; }
        [Input] public int CandleEdgeOffsetPixels { get; set; }
        [Input] public int CandleBodyWidthPercent { get; set; }
        [Input] public int LabelSize { get; set; }
        [Input] public int RefreshMilliseconds { get; set; }

        private HistoryDrawer m_Drawer;
        private DateTime m_LastRefreshUtc;
        private DateTime m_FilterDay;
        private IOrders m_Orders;
        private bool m_Registered;

        public RangeBarTradeHistory(object context) : base(context) {
            BrokerProfile = AccountFilter = InstrumentOverride = HistoryDate = "";
            UseSignalHistory = true;
            TradeHistoryDirectory = @"C:\rangebar_entries";
            TradeHistoryStream = "default";
            HistoryLookbackDays = 7;
            TrackerToChartOffsetMinutes = 0;
            // MultiCharts bar timestamps represent completion. Treating them
            // as starts places an intrabar fill on the preceding range bar.
            BarTimestampIsClose = true;
            ShowConnections = ShowLabels = true;
            ShowEntryBarArrows = true;
            ShowInfoOverlay = false;
            ArrowLengthPixels = 22;
            CandleEdgeOffsetPixels = 0;
            CandleBodyWidthPercent = 70;
            LabelSize = 9;
            RefreshMilliseconds = 500;
        }

        protected override void Create() { m_Drawer = new HistoryDrawer(); }

        protected override void StartCalc() {
            m_LastRefreshUtc = m_FilterDay = DateTime.MinValue;
            m_Orders = null;
            if (!m_Registered) {
                ChartCustomDraw.Register(m_Drawer);
                m_Registered = true;
            }
            Publish(new HistoryView(), UseSignalHistory ? "RangeBar-TradeHistory | waiting for signal journal" : "RangeBar-TradeHistory | waiting for tracker");
        }

        protected override void CalcBar() {
            if (!Environment.IsRealTimeCalc || !Bars.LastBarOnChart) return;
            int refresh = Math.Max(100, RefreshMilliseconds);
            // Also refresh when no market ticks arrive. No broker operations
            // or study properties are accessed from the UI drawing thread.
            ExecControl.RecalcLastBarAfter(TimeSpan.FromMilliseconds(refresh));
            DateTime now = DateTime.UtcNow;
            if ((now - m_LastRefreshUtc).TotalMilliseconds < refresh) return;
            m_LastRefreshUtc = now;
            try { RefreshHistory(); }
            catch (Exception ex) {
                Publish(new HistoryView(), "RangeBar-TradeHistory | history error: " + ex.Message);
                Output.WriteLine("RangeBar-TradeHistory: " + ex.Message);
            }
        }

        private void RefreshHistory() {
            DateTime day;
            if (string.IsNullOrWhiteSpace(HistoryDate)) {
                day = DateTime.Now.AddMinutes(UseSignalHistory ? 0 : TrackerToChartOffsetMinutes).Date;
            } else if (!DateTime.TryParseExact(HistoryDate.Trim(), "yyyy-MM-dd",
                       CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) {
                Publish(new HistoryView(), "RangeBar-TradeHistory | HistoryDate must be yyyy-MM-dd");
                return;
            }
            if (UseSignalHistory) { RefreshSignalHistory(day, string.IsNullOrWhiteSpace(HistoryDate)); return; }
            var manager = TradeManager;
            if (manager == null || manager.TradingData == null || manager.TradingData.Orders == null) {
                Publish(new HistoryView(), "RangeBar-TradeHistory | order tracker unavailable");
                return;
            }
            IOrders orders = manager.TradingData.Orders;
            int lookback = Math.Max(1, Math.Min(365, HistoryLookbackDays));
            DateTime from = day.AddDays(-lookback).AddMinutes(-TrackerToChartOffsetMinutes);
            DateTime to = day.AddDays(1).AddMinutes(-TrackerToChartOffsetMinutes).AddTicks(-1);
            if (!ReferenceEquals(orders, m_Orders) || m_FilterDay != day) {
                m_Orders = orders;
                m_FilterDay = day;
                // The study's tracker collection loads older records
                // asynchronously. Never query it during historical CalcBar.
                orders.SuspendFilterApply();
                try {
                    orders.ProfileFltr.CurrentValue = null;
                    orders.AccountFltr.CurrentValue = null;
                    orders.SymbolFltr.CurrentValue = null;
                    // Zero is the installed API's default/all-states sentinel.
                    orders.StatesFilter.CurrentValue = (ATCenterProxy.interop.ETM_FilterOrdersStates)0;
                    orders.IntervalFltr.Set(from, to);
                } finally { orders.ResumeFilterApply(); }
            }
            manager.ProcessEvents();

            string symbol = string.IsNullOrWhiteSpace(InstrumentOverride)
                ? Bars.Info.Name : InstrumentOverride.Trim();
            int unavailable = 0;
            var recordsById = new Dictionary<string, HistoryExecution>(StringComparer.Ordinal);
            TrackerOrder[] items = orders.Items;
            if (items != null) foreach (TrackerOrder order in items) {
                if (!HistoryMatching.ScopeMatches(order.Profile, order.Account,
                    order.MCSymbol, order.BrokerSymbol, BrokerProfile, AccountFilter, symbol)) continue;
                // ExecPrice is the tracker average, NOT StopPrice/LimitPrice.
                // FinalDT is the reported last update, NOT GeneratedDT.
                // Never invent a fill time from the order placement time.
                if (order.FilledContracts <= 0) continue;
                if (!order.ExecPrice.HasValue || !order.FinalDT.HasValue ||
                    double.IsNaN(order.ExecPrice.Value) || double.IsInfinity(order.ExecPrice.Value)) {
                    unavailable++;
                    continue;
                }
                int side = (int)order.Action == 1 ? 1 : (int)order.Action == 2 ? -1 : 0;
                if (side == 0) continue;
                DateTime time = order.FinalDT.Value.AddMinutes(TrackerToChartOffsetMinutes);
                if (time < day.AddDays(-lookback) || time >= day.AddDays(1)) continue;
                var record = new HistoryExecution {
                    Key = HistoryMatching.OrderKey(order.Profile, order.Account, order.OrderID),
                    Scope = HistoryMatching.ScopeKey(order.Profile, order.Account, symbol),
                    Profile = order.Profile, Account = order.Account, OrderId = order.OrderID,
                    Time = time, Side = side, Quantity = order.FilledContracts,
                    Price = order.ExecPrice.Value, Name = order.Name
                };
                // One marker per reported order, even after a partial fill
                // updates its quantity/average or symbol mapping duplicates it.
                recordsById[record.Key] = record;
            }

            // Rebuild exclusively from the current MultiCharts snapshot.
            // No strategy state, synthetic fills or separately persisted history.
            var records = new List<HistoryExecution>(recordsById.Values);
            Dictionary<string, int> openingPositions = new Dictionary<string, int>();
            // If today's broker position can be reconciled, carry-in lots are
            // represented as UNKNOWN, never fabricated as today's entries.
            if (day == DateTime.Now.AddMinutes(TrackerToChartOffsetMinutes).Date &&
                manager.TradingData.Positions != null) {
                var totals = new Dictionary<string, int>();
                foreach (var record in records) {
                    int total;
                    totals.TryGetValue(record.Scope, out total);
                    totals[record.Scope] = total + record.Side * record.Quantity;
                }
                var positions = manager.TradingData.Positions.Items;
                if (positions != null) foreach (var position in positions) {
                    if (!HistoryMatching.ScopeMatches(position.Profile, position.Account,
                        position.MCSymbol, position.BrokerSymbol, BrokerProfile, AccountFilter, symbol)) continue;
                    string scope = HistoryMatching.ScopeKey(position.Profile, position.Account, symbol);
                    int total;
                    totals.TryGetValue(scope, out total);
                    openingPositions[scope] = position.Value - total;
                }
            }
            HistoryView view = HistoryMatching.Build(records, day, openingPositions);
            string status = string.Format(CultureInfo.InvariantCulture,
                "RangeBar-TradeHistory | {0:yyyy-MM-dd} | {1} | {2} markers, {3} matched closes\n" +
                "Tracker order average / last-update time | FIFO pairing | {4}",
                day, symbol, view.Markers.Count, view.Pairs.Count,
                BarTimestampIsClose ? "bar end timestamps" : "bar start timestamps");
            if (unavailable > 0) status += " | " + unavailable + " records lack price/time";
            if (view.TimestampTies > 0) status += " | same-time buy/sell ordering uncertain";
            if (!orders.StatesFilter.CurrentIsAll) status += " | tracker state filter active";
            if (view.Markers.Count == 0) status += "\nNo executions returned; check account, symbol and tracker history filters.";
            Publish(view, status);
        }

        private void RefreshSignalHistory(DateTime day, bool allChartDays) {
            var events = new List<RangeBarJournalRecord>();
            string symbol = string.IsNullOrWhiteSpace(InstrumentOverride) ? Bars.Info.Name : InstrumentOverride.Trim();
            if (System.IO.Directory.Exists(TradeHistoryDirectory)) {
                foreach (string file in RangeBarJournal.HistoryFiles(TradeHistoryDirectory))
                    foreach (var record in RangeBarJournal.Read(file)) {
                        if (!HistoryMatching.ScopeMatches(record.Profile, record.Account, record.Symbol, record.Symbol,
                            BrokerProfile, AccountFilter, symbol) ||
                            !string.Equals(record.Stream, TradeHistoryStream ?? "default", StringComparison.OrdinalIgnoreCase)) continue;
                        events.Add(record);
                    }
            }
            // A blank date loads all daily files. The renderer matches saved
            // bar identities against the loaded chart and clips to its viewport.
            // Read preceding-day files too: they may hold cross-midnight exits.
            HistoryView view = allChartDays
                ? HistoryMatching.BuildJournal(RangeBarJournal.Fills(events), DateTime.MinValue, DateTime.MaxValue)
                : HistoryMatching.BuildJournal(RangeBarJournal.Fills(events), day);
            Publish(view, "RangeBar-TradeHistory | signal journal | " + view.Markers.Count + " markers");
        }

        private void Publish(HistoryView view, string status) {
            m_Drawer.Publish(new HistoryFrame {
                View = view, Status = status, ShowConnections = ShowConnections,
                ShowLabels = ShowLabels, BarTimestampIsClose = BarTimestampIsClose,
                ShowEntryBarArrows = ShowEntryBarArrows,
                ShowInfoOverlay = ShowInfoOverlay,
                TickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale,
                ArrowLength = Math.Max(8, Math.Min(80, ArrowLengthPixels)),
                CandleEdgeOffset = Math.Max(0, CandleEdgeOffsetPixels),
                CandleBodyWidthPercent = Math.Max(1, Math.Min(100, CandleBodyWidthPercent)),
                FontSize = Math.Max(6, Math.Min(18, LabelSize)),
                ChartNow = DateTime.Now.AddMinutes(UseSignalHistory ? 0 : TrackerToChartOffsetMinutes)
            });
            if (m_Registered) ChartCustomDraw.ReDraw();
        }

        protected override void Destroy() {
            if (m_Registered) {
                ChartCustomDraw.Unregister(m_Drawer);
                m_Registered = false;
            }
        }
    }

    internal sealed class HistoryExecution {
        public string Key, Scope, Profile, Account, Name;
        public RangeBarJournalRecord JournalBar;
        public int OrderId, Side, Quantity;
        public double Price;
        public DateTime Time;
    }
    internal sealed class HistoryPair {
        public int TradeId, Quantity;
        public HistoryExecution Entry, Exit;
    }
    internal sealed class HistoryMarker {
        public HistoryExecution Execution;
        public string Label;
        public int TradeId;
        public bool IsEntry;
    }
    internal sealed class HistoryView {
        public readonly List<HistoryPair> Pairs = new List<HistoryPair>();
        public readonly List<HistoryMarker> Markers = new List<HistoryMarker>();
        public int TimestampTies;
    }

    // BEGIN HISTORY MODEL -- also compiled by the local test harness.
    internal static class HistoryMatching {
        private sealed class Lot {
            public HistoryExecution Entry;
            public int Side, Remaining, TradeId;
        }
        private static string Part(string value) {
            value = (value ?? "").Trim().ToUpperInvariant();
            return value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
        }
        public static string ScopeKey(string profile, string account, string symbol) {
            return Part(profile) + Part(account) + Part(symbol);
        }
        public static string OrderKey(string profile, string account, int id) {
            return Part(profile) + Part(account) + id.ToString(CultureInfo.InvariantCulture);
        }
        private static bool Equal(string a, string b) {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }
        public static bool ScopeMatches(string profile, string account, string mcSymbol,
            string brokerSymbol, string profileFilter, string accountFilter, string symbol) {
            return (string.IsNullOrWhiteSpace(profileFilter) || Equal(profile, profileFilter)) &&
                   (string.IsNullOrWhiteSpace(accountFilter) || Equal(account, accountFilter)) &&
                   (Equal(mcSymbol, symbol) || Equal(brokerSymbol, symbol));
        }
        public static double PriceResult(HistoryPair pair) {
            return pair.Entry.Side * (pair.Exit.Price - pair.Entry.Price) * pair.Quantity;
        }
        private static string ShortLabel(int id, bool entry, double price, string entryName) {
            string prefix = Equal(entryName, "pin bar") || Equal(entryName, "pin bar buy") || Equal(entryName, "pin bar sell") ? "P" :
                Equal(entryName, "manual") || Equal(entryName, "manual buy") || Equal(entryName, "manual sell") ? "M" : "T";
            return (id > 0 ? prefix + id : "T?") + (entry ? " in " : " out ") +
                price.ToString("0.########", CultureInfo.InvariantCulture);
        }
        public static HistoryView Build(List<HistoryExecution> input, DateTime day,
                                        Dictionary<string, int> openingPositions) {
            var result = new HistoryView();
            var unique = new Dictionary<string, HistoryExecution>();
            foreach (var fill in input) {
                if (fill.Quantity > 0 && (fill.Side == 1 || fill.Side == -1) &&
                    !double.IsNaN(fill.Price) && !double.IsInfinity(fill.Price)) unique[fill.Key] = fill;
            }
            var fills = new List<HistoryExecution>(unique.Values);
            fills.Sort(delegate(HistoryExecution a, HistoryExecution b) {
                int byTime = a.Time.CompareTo(b.Time);
                if (byTime != 0) return byTime;
                int byId = a.OrderId.CompareTo(b.OrderId);
                return byId != 0 ? byId : string.CompareOrdinal(a.Key, b.Key);
            });
            var lots = new Dictionary<string, Queue<Lot>>();
            foreach (var opening in openingPositions) {
                if (opening.Value == 0) continue;
                var queue = new Queue<Lot>();
                queue.Enqueue(new Lot { Side = Math.Sign(opening.Value), Remaining = Math.Abs(opening.Value) });
                lots[opening.Key] = queue;
            }
            int nextTradeId = 0;
            var previous = new Dictionary<string, HistoryExecution>();
            foreach (var fill in fills) {
                Queue<Lot> queue;
                if (!lots.TryGetValue(fill.Scope, out queue)) {
                    queue = new Queue<Lot>(); lots[fill.Scope] = queue;
                }
                HistoryExecution prior;
                if (previous.TryGetValue(fill.Scope, out prior) && prior.Time == fill.Time && prior.Side != fill.Side)
                    result.TimestampTies++;
                previous[fill.Scope] = fill;
                int remaining = fill.Quantity, markerId = 0;
                var exitRoles = new List<string>();
                int entryId = 0;
                while (remaining > 0 && queue.Count > 0 && queue.Peek().Side != fill.Side) {
                    Lot lot = queue.Peek();
                    int closed = Math.Min(remaining, lot.Remaining);
                    if (lot.Entry == null) {
                        exitRoles.Add(ShortLabel(0, false, fill.Price, null));
                    } else {
                        exitRoles.Add(ShortLabel(lot.TradeId, false, fill.Price, lot.Entry.Name));
                        if (markerId == 0) markerId = lot.TradeId;
                        if (fill.Time >= day && fill.Time < day.AddDays(1))
                            result.Pairs.Add(new HistoryPair {
                                TradeId = lot.TradeId, Entry = lot.Entry, Exit = fill, Quantity = closed
                            });
                    }
                    remaining -= closed; lot.Remaining -= closed;
                    if (lot.Remaining == 0) queue.Dequeue();
                }
                if (remaining > 0) {
                    int id = ++nextTradeId;
                    queue.Enqueue(new Lot { Entry = fill, Side = fill.Side, Remaining = remaining, TradeId = id });
                    entryId = id;
                    if (markerId == 0) markerId = id;
                }
                if (fill.Time >= day && fill.Time < day.AddDays(1)) {
                    if (exitRoles.Count > 0) result.Markers.Add(new HistoryMarker {
                        Execution = fill, TradeId = markerId, IsEntry = false,
                        Label = string.Join(" / ", exitRoles.ToArray())
                    });
                    if (entryId > 0) result.Markers.Add(new HistoryMarker {
                        Execution = fill, TradeId = entryId, IsEntry = true,
                        Label = ShortLabel(entryId, true, fill.Price, fill.Name)
                    });
                }
            }
            // Include a prior-day entry only if it closes on the displayed day.
            var shown = new HashSet<string>();
            foreach (var marker in result.Markers) shown.Add(marker.Execution.Key);
            foreach (var pair in result.Pairs) if (shown.Add(pair.Entry.Key))
                result.Markers.Add(new HistoryMarker {
                    Execution = pair.Entry, TradeId = pair.TradeId, IsEntry = true,
                    Label = ShortLabel(pair.TradeId, true, pair.Entry.Price, pair.Entry.Name)
                });
            return result;
        }

        // Journal fills are paired by the signal's durable trade ID, never across unrelated trades.
        public static HistoryView BuildJournal(List<RangeBarJournalRecord> fills, DateTime day) {
            return BuildJournal(fills, day, day.AddDays(1));
        }

        public static HistoryView BuildJournal(List<RangeBarJournalRecord> fills, DateTime from, DateTime to) {
            var view = new HistoryView();
            fills.Sort(delegate(RangeBarJournalRecord a, RangeBarJournalRecord b) {
                int order = a.Observed.CompareTo(b.Observed);
                if (order != 0) return order;
                if (a.TradeId == b.TradeId && a.Kind != b.Kind) return a.Kind == "entry" ? -1 : 1;
                return string.CompareOrdinal(a.EventId, b.EventId);
            });
            var lots = new Dictionary<string, Queue<Lot>>();
            var numbers = new Dictionary<string, int>();
            var seen = new HashSet<string>();
            foreach (var record in fills) {
                if (!seen.Add(record.EventId)) continue;
                int number;
                if (!numbers.TryGetValue(record.TradeId, out number)) { number = numbers.Count + 1; numbers[record.TradeId] = number; }
                var fill = new HistoryExecution {
                    Key = record.EventId, Scope = record.TradeId, Profile = record.Profile, Account = record.Account,
                    Name = record.Setup, Time = record.Observed, Side = record.Side, Quantity = record.Quantity,
                    Price = record.Price, JournalBar = record
                };
                bool inRange = fill.Time >= from && fill.Time < to;
                Queue<Lot> queue;
                if (!lots.TryGetValue(record.TradeId, out queue)) { queue = new Queue<Lot>(); lots[record.TradeId] = queue; }
                if (record.Kind == "entry") {
                    queue.Enqueue(new Lot { Entry = fill, Side = fill.Side, Remaining = fill.Quantity, TradeId = number });
                } else if (record.Kind == "exit") {
                    int remaining = fill.Quantity;
                    while (remaining > 0 && queue.Count > 0) {
                        Lot lot = queue.Peek();
                        if (lot.Side == fill.Side) break;
                        int closed = Math.Min(remaining, lot.Remaining);
                        if (inRange) view.Pairs.Add(new HistoryPair { TradeId = number, Quantity = closed, Entry = lot.Entry, Exit = fill });
                        lot.Remaining -= closed; remaining -= closed;
                        if (lot.Remaining == 0) queue.Dequeue();
                    }
                }
                if (inRange) view.Markers.Add(new HistoryMarker { Execution = fill, TradeId = number,
                    IsEntry = record.Kind == "entry", Label = ShortLabel(number, record.Kind == "entry", fill.Price, fill.Name) });
            }
            var shown = new HashSet<string>();
            foreach (var marker in view.Markers) shown.Add(marker.Execution.Key);
            foreach (var pair in view.Pairs) if (shown.Add(pair.Entry.Key)) view.Markers.Add(new HistoryMarker {
                Execution = pair.Entry, TradeId = pair.TradeId, IsEntry = true,
                Label = ShortLabel(pair.TradeId, true, pair.Entry.Price, pair.Entry.Name)
            });
            return view;
        }

        // Fraction within the containing bar's visual slot. A range chart has
        // no linear time axis, so use timestamps rather than bar open/close prices.
        public static double TimeFraction(DateTime time, DateTime start, DateTime end) {
            if (end <= start) return double.NaN;
            return Math.Max(0, Math.Min(1, (time - start).Ticks / (double)(end - start).Ticks));
        }
        public static bool LocateBar(DateTime[] times, DateTime time, bool timestampsAreClose,
            DateTime chartNow, out int index, out double fraction, out bool uncertain) {
            index = -1; fraction = 0; uncertain = false;
            if (times.Length == 0) return false;
            int low = 0, high = times.Length;
            while (low < high) {
                int mid = low + (high - low) / 2;
                bool before = timestampsAreClose ? times[mid] < time : times[mid] <= time;
                if (before) low = mid + 1; else high = mid;
            }
            index = timestampsAreClose ? low : low - 1;
            if (index < 0 || index >= times.Length) return false;
            DateTime start = timestampsAreClose
                ? (index > 0 ? times[index - 1] : times[index]) : times[index];
            DateTime end = timestampsAreClose ? times[index]
                : (index + 1 < times.Length ? times[index + 1] : chartNow);
            if (time < start || time > end) return false;
            uncertain = (index > 0 && times[index - 1] == times[index]) ||
                        (index + 1 < times.Length && times[index + 1] == times[index]);
            fraction = TimeFraction(time, start, end);
            if (double.IsNaN(fraction)) { fraction = .5; uncertain = true; }
            return true;
        }
    }
    // END HISTORY MODEL

    internal sealed class HistoryFrame {
        public HistoryView View;
        public string Status;
        public bool ShowConnections, ShowLabels, ShowEntryBarArrows, ShowInfoOverlay, BarTimestampIsClose;
        public int ArrowLength, FontSize, CandleEdgeOffset, CandleBodyWidthPercent;
        public double TickSize;
        public DateTime ChartNow;
    }

    internal sealed class HistoryDrawer : IChartCustomDrawer {
        private sealed class EntryBarArrow {
            public PointF Tip;
            public int Side;
        }
        private readonly object m_Lock = new object();
        private HistoryFrame m_Frame;
        public void Publish(HistoryFrame frame) { lock (m_Lock) m_Frame = frame; }
        private static string PointKey(HistoryExecution execution, bool entry) {
            return execution.Key + (entry ? ":in" : ":out");
        }

        public void Draw(DrawContext context, EDrawPhases phase) {
            if (phase != EDrawPhases.Final || context.graphics == null) return;
            HistoryFrame frame;
            lock (m_Lock) frame = m_Frame;
            if (frame == null) return;
            var graphics = context.graphics;
            GraphicsState saved = graphics.Save();
            try {
                graphics.SetClip(context.DrawRect, CombineMode.Intersect);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var font = new Font("Segoe UI", frame.FontSize)) {
                    var points = new Dictionary<string, PointF>();
                    var entryBarArrows = new Dictionary<string, EntryBarArrow>();
                    var approximate = new HashSet<string>();
                    int visibleMarkers = 0;
                    Bar[] sourceBars = null;
                    uint firstChartIndex = 0;
                    if (frame.View.Markers.Count > 0) {
                        DateTime first = frame.View.Markers[0].Execution.JournalBar != null ?
                            frame.View.Markers[0].Execution.JournalBar.BarTime : frame.View.Markers[0].Execution.Time;
                        DateTime last = first;
                        foreach (var marker in frame.View.Markers) {
                            DateTime time = marker.Execution.JournalBar != null ? marker.Execution.JournalBar.BarTime : marker.Execution.Time;
                            if (time < first) first = time;
                            if (time > last) last = time;
                            var anchor = marker.Execution.JournalBar == null ? null : marker.Execution.JournalBar.FillAnchor;
                            if (anchor != null) { if (anchor.BarTime < first) first = anchor.BarTime; if (anchor.BarTime > last) last = anchor.BarTime; }
                        }
                        // Use time only to bound the snapshot. Get's numeric
                        // range is [from, to), preserving each original chart
                        // index even when multiple range bars share a time.
                        uint count = context.Environment.Bars.Count;
                        if (count > 0) {
                            firstChartIndex = Math.Min(count - 1,
                                context.Environment.Bars.GetIndexByTime(first.Date.AddDays(-1)));
                            uint lastChartIndex = Math.Min(count - 1,
                                context.Environment.Bars.GetIndexByTime(last.Date.AddDays(1)));
                            uint end = (uint)Math.Min((ulong)count, (ulong)lastChartIndex + 2);
                            if (end > firstChartIndex)
                                context.Environment.Bars.Get(firstChartIndex, end, out sourceBars);
                        }
                    }
                    Bar[] all = sourceBars == null ? null : (Bar[])sourceBars.Clone();
                    DateTime[] times = null;
                    if (all != null && all.Length > 0) {
                        // Keep the native order: sorting by time would lose
                        // the identity of bars with duplicate timestamps.
                        times = new DateTime[all.Length];
                        for (int i = 0; i < all.Length; i++) times[i] = all[i].Time;
                        foreach (var marker in frame.View.Markers) {
                            PointF point; bool uncertain; int barIndex;
                            if (TryLocate(context, all, times, firstChartIndex, marker.Execution, marker.IsEntry, frame, out point, out uncertain, out barIndex)) {
                                points[PointKey(marker.Execution, marker.IsEntry)] = point;
                                visibleMarkers++;
                                if (uncertain) approximate.Add(marker.Execution.Key);
                            }
                            if (frame.ShowEntryBarArrows && marker.IsEntry) {
                                int setupIndex = marker.Execution.JournalBar != null
                                    ? LocateJournalBar(all, marker.Execution.JournalBar) : barIndex;
                                if (setupIndex < 0) continue;
                                Bar bar = all[setupIndex]; uint chartIndex = firstChartIndex + (uint)setupIndex;
                                int side = marker.Execution.Side;
                                double tickSize = frame.TickSize;
                                double extreme = (side > 0 ? bar.Low : bar.High) - side * tickSize;
                                PointF native = context.Environment.ChartPoint2Point(new ChartPoint(bar.Time, extreme));
                                string key = chartIndex.ToString(CultureInfo.InvariantCulture) + ":" + side;
                                entryBarArrows[key] = new EntryBarArrow {
                                    Side = side, Tip = new PointF(context.Environment.BarIndex2PointX(chartIndex), native.Y)
                                };
                            }
                        }
                        if (frame.ShowConnections) foreach (var pair in frame.View.Pairs) {
                            PointF entry, exit;
                            if (!points.TryGetValue(PointKey(pair.Entry, true), out entry) ||
                                !points.TryGetValue(PointKey(pair.Exit, false), out exit)) continue;
                            using (var pen = new Pen(HistoryMatching.PriceResult(pair) >= 0 ? Color.Green : Color.Red, 1.5f)) {
                                graphics.DrawLine(pen, entry, exit);
                            }
                        }
                        foreach (var arrow in entryBarArrows.Values) {
                            // Tips sit one price tick outside the bar. Screen Y
                            // grows downward: buys point up and sells point down.
                            PointF tail = new PointF(arrow.Tip.X, arrow.Tip.Y + arrow.Side * frame.ArrowLength);
                            DrawWhiteArrow(graphics, tail, arrow.Tip);
                        }
                        foreach (var marker in frame.View.Markers) {
                            PointF point;
                            if (!points.TryGetValue(PointKey(marker.Execution, marker.IsEntry), out point)) continue;
                            // Entry points right, close points left, for both
                            // longs and shorts. Reversals have both roles.
                            int direction = marker.IsEntry ? 1 : -1;
                            float headWidth = Math.Max(8, Math.Min(20, frame.ArrowLength * .6f));
                            float headHeight = Math.Max(8, Math.Min(20, frame.ArrowLength * .65f));
                            float baseX = point.X - direction * headWidth;
                            // Only a solid arrowhead: no shaft or endpoint dot.
                            using (var brush = new SolidBrush(Color.White))
                                graphics.FillPolygon(brush, new[] {
                                    point, new PointF(baseX, point.Y - headHeight / 2),
                                    new PointF(baseX, point.Y + headHeight / 2)
                                });
                            if (frame.ShowLabels) {
                                string label = marker.Label;
                                SizeF size = graphics.MeasureString(label, font);
                                float x = marker.IsEntry ? baseX - size.Width - 2 : baseX + 2;
                                // Stay attached to the arrow and vertically
                                // centered at its price, even in dense regions.
                                RectangleF bounds = new RectangleF(x, point.Y - size.Height / 2, size.Width, size.Height);
                                using (var brush = new SolidBrush(Color.Black)) {
                                    graphics.DrawString(label, font, brush, bounds.Location);
                                }
                            }
                        }
                    }
                    if (frame.ShowInfoOverlay) {
                        string status = frame.Status;
                        status += "\nArrowheads at candle edges; timestamps identify the containing bar.";
                        if (visibleMarkers < frame.View.Markers.Count)
                            status += "\n" + (frame.View.Markers.Count - visibleMarkers) + " markers outside loaded chart time range";
                        if (approximate.Count > 0) status += "\n" + approximate.Count + " order positions uncertain/provisional";
                        SizeF statusSize = graphics.MeasureString(status, font);
                        var location = new PointF(context.DrawRect.Left + 6, context.DrawRect.Top + 6);
                        using (var background = new SolidBrush(Color.FromArgb(230, context.BackGround)))
                        using (var brush = new SolidBrush(context.BackGround.GetBrightness() < .5 ? Color.WhiteSmoke : Color.Black)) {
                            graphics.FillRectangle(background, location.X, location.Y, statusSize.Width, statusSize.Height);
                            graphics.DrawString(status, font, brush, location);
                        }
                    }
                }
            } finally { graphics.Restore(saved); }
        }

        private static void DrawWhiteArrow(Graphics graphics, PointF tail, PointF tip) {
            using (var pen = new Pen(Color.White, 3))
            using (var cap = new AdjustableArrowCap(4, 5, true)) {
                pen.CustomEndCap = cap;
                graphics.DrawLine(pen, tail, tip);
            }
            using (var brush = new SolidBrush(Color.White))
                graphics.FillEllipse(brush, tip.X - 2, tip.Y - 2, 4, 4);
        }

        private static bool ContainsPrice(Bar bar, double price) {
            double tolerance = Math.Max(1e-8, Math.Abs(price) * 1e-10);
            return price >= bar.Low - tolerance && price <= bar.High + tolerance;
        }

        private static int ResolveEntryBar(Bar[] bars, int timeIndex, double entryPrice) {
            if (ContainsPrice(bars[timeIndex], entryPrice)) return timeIndex;
            int candidate = -1;
            foreach (int neighbor in new[] {timeIndex - 1, timeIndex + 1}) {
                if (neighbor < 0 || neighbor >= bars.Length || !ContainsPrice(bars[neighbor], entryPrice)) continue;
                if (candidate >= 0) return timeIndex; // Ambiguous: retain the time-based assignment.
                candidate = neighbor;
            }
            return candidate >= 0 ? candidate : timeIndex;
        }

        internal static int LocateJournalBar(Bar[] bars, RangeBarJournalRecord record) {
            int found = -1;
            for (int i = 0; i < bars.Length; i++) {
                Bar bar = bars[i];
                bool match = record.FinalBar
                    ? bar.Time == record.BarTime && bar.Open == record.Open && bar.High == record.High &&
                        bar.Low == record.Low && bar.Close == record.Close &&
                        (record.TickId == 0 || (bar.TickID & 0x7fffffff) == record.TickId)
                    : i > 0 && record.PreviousBarTime != DateTime.MinValue &&
                        bars[i - 1].Time == record.PreviousBarTime && bars[i - 1].Open == record.PreviousOpen &&
                        bars[i - 1].High == record.PreviousHigh && bars[i - 1].Low == record.PreviousLow &&
                        bars[i - 1].Close == record.PreviousClose && bar.Open == record.Open &&
                        bar.High >= record.High && bar.Low <= record.Low;
                if (!match) continue;
                if (found >= 0) return -1;
                found = i;
            }
            return found;
        }

        internal static int LocateHorizontalJournalBar(Bar[] bars, RangeBarJournalRecord record) {
            if (record.FillAnchor != null) return LocateJournalBar(bars, record.FillAnchor);
            int setup = LocateJournalBar(bars, record);
            // Earlier detailed files retained both original calculation bar numbers.
            // Their relative offset remains usable after chart history is reloaded.
            if (setup >= 0 && record.FillBarTime != DateTime.MinValue) {
                long fill = (long)setup + record.FillBarNumber - record.BarNumber;
                if (fill >= 0 && fill < bars.Length) return (int)fill;
            }
            // Old compact files saved only the intended setup bar. A stop fill
            // beyond that bar's range can belong to the following range bar.
            // Restrict this fallback to entries and the immediate next bar:
            // price alone cannot establish a fill farther away in chart history.
            if (setup >= 0 && record.Kind == "entry" &&
                !ContainsPrice(bars[setup], record.Price) && setup + 1 < bars.Length &&
                ContainsPrice(bars[setup + 1], record.Price)) return setup + 1;
            return setup;
        }

        private static bool TryLocate(DrawContext context, Bar[] bars, DateTime[] times, uint firstChartIndex, HistoryExecution fill, bool isEntry,
            HistoryFrame frame, out PointF point, out bool uncertain, out int barIndex) {
            point = PointF.Empty; uncertain = false; barIndex = -1;
            int index; double fraction;
            if (fill.JournalBar != null) {
                index = LocateHorizontalJournalBar(bars, fill.JournalBar);
                if (index < 0) return false; // Missing or ambiguous explicit bar: never shift to a guessed neighbor.
            } else {
            if (!HistoryMatching.LocateBar(times, fill.Time, frame.BarTimestampIsClose,
                frame.ChartNow, out index, out fraction, out uncertain)) {
                // A forming bar has no final completion timestamp yet. A fill
                // after its latest update still belongs to that current bar.
                int last = bars.Length - 1;
                if (!frame.BarTimestampIsClose || last < 0 ||
                    bars[last].State == EBarState.Close || fill.Time < times[last] ||
                    fill.Time > frame.ChartNow) return false;
                index = last;
                uncertain = true;
            }
            if (isEntry) index = ResolveEntryBar(bars, index, fill.Price);
            }
            barIndex = index;
            if (index == bars.Length - 1 &&
                (!frame.BarTimestampIsClose || bars[index].State != EBarState.Close)) uncertain = true;
            // Preserve the exact index of the selected snapshot bar instead
            // of resolving its timestamp a second time to a neighboring bar.
            uint chartIndex = firstChartIndex + (uint)index;
            float center = context.Environment.BarIndex2PointX(chartIndex);
            // Close-tick distance is not the candle body's half-width. Estimate
            // the body from actual center spacing, with a pixel override for
            // matching a particular chart style exactly.
            float spacing = index + 1 < bars.Length
                ? Math.Abs(context.Environment.BarIndex2PointX(chartIndex + 1) - center)
                : index > 0 ? Math.Abs(center - context.Environment.BarIndex2PointX(chartIndex - 1)) : 8;
            if (spacing <= 0) spacing = 8;
            float width = (float)Math.Max(1, Math.Round(spacing * Math.Max(1, frame.CandleBodyWidthPercent) / 100.0));
            float edge = frame.CandleEdgeOffset > 0 ? frame.CandleEdgeOffset : width / 2;
            PointF native = context.Environment.ChartPoint2Point(new ChartPoint(bars[index].Time, fill.Price));
            point = new PointF(center + (isEntry ? -edge : edge), native.Y);
            return !float.IsNaN(point.X) && !float.IsNaN(point.Y) &&
                   !float.IsInfinity(point.X) && !float.IsInfinity(point.Y);
        }
    }
    // BEGIN RANGE BAR JOURNAL -- generated into each standalone study by tools/sync_range_bar_journal.py.
    // Closed trades use compact version 4; unfinished version 3 and legacy versions 1/2 remain readable.
    internal sealed class RangeBarJournalRecord {
        public string EventId = Guid.NewGuid().ToString("N"), TradeId = "", Kind = "", Target = "";
        public string Setup = "unknown", Profile = "", Account = "", Symbol = "", Stream = "default", Role = "";
        public DateTime Observed, BarTime, FillBarTime, PreviousBarTime;
        public int Side, Quantity, BarNumber, FillBarNumber;
        public double Price, Open, High, Low, Close, RangeTicks, TickSize;
        public uint TickId, FillTickId;
        public double SubmittedPrice, PreviousOpen, PreviousHigh, PreviousLow, PreviousClose;
        public bool FinalBar;
        public string AppliedUpdates = "";
        public RangeBarJournalRecord FillAnchor;
        public RangeBarJournalRecord Copy() {
            var copy = (RangeBarJournalRecord)MemberwiseClone();
            copy.FillAnchor = FillAnchor == null ? null : FillAnchor.Copy();
            return copy;
        }
    }
    internal static class RangeBarJournal {
        private static readonly System.Globalization.CultureInfo CI = System.Globalization.CultureInfo.InvariantCulture;
        private static string S(string value) { return Uri.EscapeDataString(value ?? ""); }
        private static string D(double value) { return value.ToString("R", CI); }
        private static string EncodeLegacy(RangeBarJournalRecord r) {
            return string.Join("\t", new[] {"RBJ1", S(r.EventId), S(r.TradeId), S(r.Kind), S(r.Target), S(r.Setup),
                S(r.Profile), S(r.Account), S(r.Symbol), S(r.Stream), S(r.Role),
                r.Observed.ToString("o", CI), r.BarTime.ToString("o", CI), r.Side.ToString(CI), r.Quantity.ToString(CI),
                D(r.Price), r.BarNumber.ToString(CI), r.TickId.ToString(CI), D(r.Open), D(r.High), D(r.Low), D(r.Close),
                D(r.RangeTicks), D(r.TickSize), r.FinalBar ? "1" : "0", r.FillBarTime.ToString("o", CI), r.FillBarNumber.ToString(CI),
                r.FillTickId.ToString(CI), D(r.SubmittedPrice), r.PreviousBarTime.ToString("o", CI), D(r.PreviousOpen),
                D(r.PreviousHigh), D(r.PreviousLow), D(r.PreviousClose)});
        }
        private static RangeBarJournalRecord DecodeLegacy(string line) {
            string[] p = line.Split('\t');
            if (p.Length != 34 || p[0] != "RBJ1") throw new FormatException("Unsupported journal row");
            var r = new RangeBarJournalRecord {
                EventId = Uri.UnescapeDataString(p[1]), TradeId = Uri.UnescapeDataString(p[2]), Kind = Uri.UnescapeDataString(p[3]),
                Target = Uri.UnescapeDataString(p[4]), Setup = Uri.UnescapeDataString(p[5]), Profile = Uri.UnescapeDataString(p[6]),
                Account = Uri.UnescapeDataString(p[7]), Symbol = Uri.UnescapeDataString(p[8]), Stream = Uri.UnescapeDataString(p[9]), Role = Uri.UnescapeDataString(p[10]),
                Observed = DateTime.Parse(p[11], CI, System.Globalization.DateTimeStyles.RoundtripKind),
                BarTime = DateTime.Parse(p[12], CI, System.Globalization.DateTimeStyles.RoundtripKind),
                Side = int.Parse(p[13], CI), Quantity = int.Parse(p[14], CI), Price = double.Parse(p[15], CI),
                BarNumber = int.Parse(p[16], CI), TickId = uint.Parse(p[17], CI), Open = double.Parse(p[18], CI),
                High = double.Parse(p[19], CI), Low = double.Parse(p[20], CI), Close = double.Parse(p[21], CI),
                RangeTicks = double.Parse(p[22], CI), TickSize = double.Parse(p[23], CI), FinalBar = p[24] == "1", FillBarTime = DateTime.Parse(p[25], CI, System.Globalization.DateTimeStyles.RoundtripKind),
                FillBarNumber = int.Parse(p[26], CI), FillTickId = uint.Parse(p[27], CI), SubmittedPrice = double.Parse(p[28], CI), PreviousBarTime = DateTime.Parse(p[29], CI, System.Globalization.DateTimeStyles.RoundtripKind),
                PreviousOpen = double.Parse(p[30], CI), PreviousHigh = double.Parse(p[31], CI),
                PreviousLow = double.Parse(p[32], CI), PreviousClose = double.Parse(p[33], CI)
            };
            if (string.IsNullOrEmpty(r.EventId) || string.IsNullOrEmpty(r.TradeId) ||
                (r.Kind != "entry" && r.Kind != "exit" && r.Kind != "bar") ||
                (r.Side != 1 && r.Side != -1) || r.Quantity <= 0 || double.IsNaN(r.Price) || double.IsInfinity(r.Price))
                throw new FormatException("Invalid journal values");
            return r;
        }
        private const string CompactBegin = "BEGIN RANGE BAR TRADE (RBJ4)";
        private const string TradeBegin = "BEGIN RANGE BAR TRADE (RBJ3)";
        private const string TradeEnd = "END RANGE BAR TRADE";
        private const string Begin = "BEGIN RANGE BAR EVENT (RBJ2)";
        private const string End = "END RANGE BAR EVENT";
        private static readonly string[] Fields = {
            "Event ID", "Trade ID", "Event", "Annotation target ID", "Trade type", "Broker profile", "Account", "Instrument",
            "Stream", "Chart", "Confirmation received", "Annotation bar time", "Action", "Quantity", "Confirmed price",
            "Annotation bar number", "Annotation tick ID", "Bar open", "Bar high", "Bar low", "Bar close", "Range ticks",
            "Tick size", "Bar finalized", "Fill observation bar time", "Fill observation bar number", "Fill observation tick ID",
            "Submitted entry stop price", "Previous bar time", "Previous bar open", "Previous bar high", "Previous bar low", "Previous bar close"
        };
        private static string Quote(string value) {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r")
                .Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        }
        private static string Unquote(string value) {
            if (value.Length < 2 || value[0] != '"' || value[value.Length - 1] != '"')
                throw new FormatException("Expected quoted journal text");
            var result = new System.Text.StringBuilder();
            for (int i = 1; i < value.Length - 1; i++) {
                char c = value[i];
                if (c == '\\') {
                    if (++i >= value.Length - 1) throw new FormatException("Incomplete text escape");
                    c = value[i];
                    if (c == 'n') c = '\n'; else if (c == 'r') c = '\r'; else if (c == 't') c = '\t';
                    else if (c != '\\' && c != '"') throw new FormatException("Unknown text escape");
                } else if (c == '"') throw new FormatException("Unescaped quotation mark");
                result.Append(c);
            }
            return result.ToString();
        }
        public static string Encode(RangeBarJournalRecord record) {
            string[] values = EncodeLegacy(record).Split('\t');
            var text = new System.Text.StringBuilder(Begin + "\r\n");
            for (int i = 1; i < values.Length; i++) {
                string value = values[i];
                if (i <= 10) value = Quote(Uri.UnescapeDataString(value));
                if (i == 3) value = record.Kind == "bar" ? "BAR UPDATE" : record.Kind.ToUpperInvariant();
                if (i == 13) value = record.Side > 0 ? "BUY" : "SELL";
                if (i == 24) value = record.FinalBar ? "yes" : "no";
                text.Append("  ").Append(Fields[i - 1]).Append(": ").Append(value).Append("\r\n");
            }
            if (record.FillAnchor != null) Field(text, "Fill bar anchor", Quote(EncodeAnchor(record.FillAnchor)));
            return text.Append(End).ToString();
        }
        public static RangeBarJournalRecord Decode(string text) {
            if (text.StartsWith("RBJ1\t", StringComparison.Ordinal)) return DecodeLegacy(text);
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length < 2 || lines[0] != Begin || lines[lines.Length - 1] != End)
                throw new FormatException("Incomplete journal event");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < lines.Length - 1; i++) {
                string line = lines[i].Trim();
                int colon = line.IndexOf(':');
                if (colon <= 0) throw new FormatException("Invalid journal field");
                string key = line.Substring(0, colon);
                if (values.ContainsKey(key)) throw new FormatException("Duplicate journal field");
                values[key] = line.Substring(colon + 1).Trim();
            }
            string anchorText;
            values.TryGetValue("Fill bar anchor", out anchorText);
            values.Remove("Fill bar anchor");
            if (values.Count != Fields.Length) throw new FormatException("Incomplete journal fields");
            var parts = new string[Fields.Length + 1]; parts[0] = "RBJ1";
            for (int i = 1; i < parts.Length; i++) {
                string value;
                if (!values.TryGetValue(Fields[i - 1], out value)) throw new FormatException("Missing journal field");
                if (i == 3) {
                    if (value == "ENTRY") value = "entry"; else if (value == "EXIT") value = "exit";
                    else if (value == "BAR UPDATE") value = "bar"; else throw new FormatException("Invalid event kind");
                    value = S(value);
                } else if (i <= 10) value = S(Unquote(value));
                if (i == 13) {
                    if (value == "BUY") value = "1"; else if (value == "SELL") value = "-1";
                    else throw new FormatException("Invalid action");
                }
                if (i == 24) {
                    if (value == "yes") value = "1"; else if (value == "no") value = "0";
                    else throw new FormatException("Invalid finalization flag");
                }
                parts[i] = value;
            }
            var decoded = DecodeLegacy(string.Join("\t", parts));
            if (anchorText != null) decoded.FillAnchor = DecodeAnchor(decoded, Unquote(anchorText));
            return decoded;
        }
        public static string Hash(string value) {
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }
        public static string FileName(string directory, string scope) {
            return System.IO.Path.Combine(directory, "rangebar-" + Hash(scope).Substring(0, 24) + ".txt");
        }
        public static string ScopeKey(RangeBarJournalRecord r) {
            return Uri.EscapeDataString(r.Profile) + "|" + Uri.EscapeDataString(r.Account) + "|" +
                Uri.EscapeDataString(r.Symbol) + "|" + Uri.EscapeDataString(r.Stream) + "|" + r.Role + "|" + r.RangeTicks.ToString("R", CI);
        }
        public static bool IsDailyFile(string path) {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            DateTime day;
            return name.Length > 11 && name[name.Length - 11] == '-' && DateTime.TryParseExact(
                name.Substring(name.Length - 10), "yyyy-MM-dd", CI, System.Globalization.DateTimeStyles.None, out day);
        }
        public static string DailyFileName(string streamPath, DateTime entryTime) {
            if (entryTime == DateTime.MinValue) throw new InvalidOperationException("History entry has no date.");
            if (entryTime.Kind == DateTimeKind.Utc) entryTime = entryTime.ToLocalTime();
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(streamPath),
                "RangeBar--history-" + entryTime.ToString("yyyy-MM-dd", CI) + ".txt");
        }
        public static string[] StreamFiles(string streamPath) {
            var files = new List<string>();
            string directory = System.IO.Path.GetDirectoryName(streamPath);
            if (!System.IO.Directory.Exists(directory)) return files.ToArray();
            string prefix = System.IO.Path.GetFileNameWithoutExtension(streamPath) + "-";
            foreach (string path in HistoryFiles(directory))
                if (IsDailyFile(path) && (System.IO.Path.GetFileName(path).StartsWith("RangeBar--history-", StringComparison.OrdinalIgnoreCase) ||
                    System.IO.Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) files.Add(path);
            files.Sort(StringComparer.OrdinalIgnoreCase);
            if (System.IO.File.Exists(streamPath) || System.IO.File.Exists(System.IO.Path.ChangeExtension(streamPath, ".rbj")))
                files.Add(streamPath);
            return files.ToArray();
        }
        public static void SplitDaily(string streamPath) {
            if (IsDailyFile(streamPath) && System.IO.Path.GetFileName(streamPath).StartsWith("RangeBar--history-", StringComparison.OrdinalIgnoreCase)) {
                Consolidate(streamPath); return;
            }
            if (!IsDailyFile(streamPath)) {
                string directory = System.IO.Path.GetDirectoryName(streamPath);
                if (System.IO.Directory.Exists(directory)) foreach (string file in System.IO.Directory.GetFiles(directory,
                    System.IO.Path.GetFileNameWithoutExtension(streamPath) + "-*.txt"))
                    if (IsDailyFile(file)) SplitDaily(file);
            }
            string legacy = System.IO.Path.ChangeExtension(streamPath, ".rbj");
            if (!System.IO.File.Exists(streamPath) && !System.IO.File.Exists(legacy)) return;
            using (var mutex = new System.Threading.Mutex(false, "Local\\RangeBarJournal-" + Hash(System.IO.Path.GetFullPath(streamPath).ToUpperInvariant()))) {
                bool locked = false;
                try {
                    try { locked = mutex.WaitOne(1000); } catch (System.Threading.AbandonedMutexException) { locked = true; }
                    if (!locked) throw new System.IO.IOException("Journal writer busy");
                    var history = new List<RangeBarJournalRecord>(); var seen = new HashSet<string>(StringComparer.Ordinal);
                    ReadFile(streamPath, history, seen, true); ReadFile(legacy, history, seen, true);
                    var fills = Fills(history);
                    if (fills.Count == 0) return;
                    var dates = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                    foreach (var fill in fills) if (fill.Kind == "entry") {
                        DateTime date;
                        if (!dates.TryGetValue(fill.TradeId, out date) || fill.Observed < date) dates[fill.TradeId] = fill.Observed;
                    }
                    var groups = new Dictionary<string, List<RangeBarJournalRecord>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var fill in fills) {
                        DateTime date;
                        if (!dates.TryGetValue(fill.TradeId, out date)) date = fill.Observed;
                        string path = DailyFileName(streamPath, date);
                        List<RangeBarJournalRecord> group;
                        if (!groups.TryGetValue(path, out group)) { group = new List<RangeBarJournalRecord>(); groups[path] = group; }
                        group.Add(fill);
                    }
                    foreach (var group in groups) MergeDaily(group.Key, group.Value);
                    // Only remove the legacy files after every dated file was durably saved.
                    // A partial migration is safe to retry: event IDs deduplicate the copies.
                    if (System.IO.File.Exists(streamPath)) System.IO.File.Delete(streamPath);
                    if (System.IO.File.Exists(legacy)) System.IO.File.Delete(legacy);
                } finally { if (locked) mutex.ReleaseMutex(); }
            }
        }
        private static void MergeDaily(string path, List<RangeBarJournalRecord> records) {
            using (var mutex = new System.Threading.Mutex(false, "Local\\RangeBarJournal-" + Hash(System.IO.Path.GetFullPath(path).ToUpperInvariant()))) {
                bool locked = false;
                try {
                    try { locked = mutex.WaitOne(1000); } catch (System.Threading.AbandonedMutexException) { locked = true; }
                    if (!locked) throw new System.IO.IOException("Daily journal writer busy");
                    var history = new List<RangeBarJournalRecord>(); var seen = new HashSet<string>(StringComparer.Ordinal);
                    ReadFile(path, history, seen, true);
                    foreach (var r in records) if (seen.Add(r.EventId)) history.Add(r.Copy());
                    SaveGrouped(path, history);
                } finally { if (locked) mutex.ReleaseMutex(); }
            }
        }
        public static string[] HistoryFiles(string directory) {
            var files = new List<string>();
            if (!System.IO.Directory.Exists(directory)) return files.ToArray();
            foreach (string path in System.IO.Directory.GetFiles(directory)) {
                if (!System.IO.Path.GetFileName(path).StartsWith("rangebar-", StringComparison.OrdinalIgnoreCase)) continue;
                string extension = System.IO.Path.GetExtension(path);
                if (string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase)) files.Add(path);
                else if (string.Equals(extension, ".rbj", StringComparison.OrdinalIgnoreCase) &&
                    !System.IO.File.Exists(System.IO.Path.ChangeExtension(path, ".txt"))) files.Add(path);
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files.ToArray();
        }
        public static List<RangeBarJournalRecord> Read(string path) {
            var result = new List<RangeBarJournalRecord>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Consolidated .txt snapshots are authoritative; retain any additional legacy events.
            ReadFile(path, result, seen);
            if (string.Equals(System.IO.Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
                ReadFile(System.IO.Path.ChangeExtension(path, ".rbj"), result, seen);
            return result;
        }
        private static void ReadFile(string path, List<RangeBarJournalRecord> result, HashSet<string> seen, bool strict = false) {
            if (!System.IO.File.Exists(path)) return;
            string contents;
            try {
                using (var file = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                using (var reader = new System.IO.StreamReader(file, System.Text.Encoding.UTF8)) contents = reader.ReadToEnd();
            } catch (System.IO.FileNotFoundException) { return; }
              catch (System.IO.DirectoryNotFoundException) { return; }
            // Only newline-terminated rows/blocks are committed. A later BEGIN abandons a torn block.
            int end = contents.LastIndexOf('\n');
            if (strict && (end < 0 || contents.Substring(end + 1).Trim().Length > 0))
                throw new System.IO.IOException("History has an incomplete final line; original preserved.");
            if (end < 0) return;
            System.Text.StringBuilder block = null;
            foreach (string row in contents.Substring(0, end).Split('\n')) {
                string line = row.TrimEnd('\r');
                if (line == Begin || line == TradeBegin || line == CompactBegin) {
                    if (strict && block != null) throw new System.IO.IOException("History has an incomplete block; original preserved.");
                    block = new System.Text.StringBuilder(line); continue; }
                if ((line == End || line == TradeEnd) && block != null) {
                    block.Append('\n').Append(line);
                    AddDecoded(block.ToString(), result, seen, strict); block = null;
                } else if (line.StartsWith("RBJ1\t", StringComparison.Ordinal)) {
                    block = null; AddDecoded(line, result, seen, strict);
                } else if (block != null) block.Append('\n').Append(line);
                else if (strict && (line.StartsWith("BEGIN RANGE BAR", StringComparison.Ordinal) || line.StartsWith("RBJ", StringComparison.Ordinal)))
                    throw new System.IO.IOException("Unrecognized history version; original preserved.");
            }
            if (strict && block != null) throw new System.IO.IOException("History has an incomplete block; original preserved.");
        }
        private static void AddDecoded(string text, List<RangeBarJournalRecord> result, HashSet<string> seen, bool strict = false) {
            try {
                if (text.StartsWith(CompactBegin, StringComparison.Ordinal)) {
                    foreach (var r in DecodeCompact(text)) if (seen.Add(r.EventId)) result.Add(r);
                } else if (text.StartsWith(TradeBegin, StringComparison.Ordinal)) {
                    foreach (var r in DecodeTrade(text)) if (seen.Add(r.EventId)) result.Add(r);
                } else { var r = Decode(text); if (seen.Add(r.EventId)) result.Add(r); }
            }
            catch (FormatException ex) { if (strict) throw new System.IO.IOException("Invalid history record; original preserved.", ex); }
            catch (OverflowException ex) { if (strict) throw new System.IO.IOException("Invalid history values; original preserved.", ex); }
        }
        private static bool HasUpdate(RangeBarJournalRecord record, string id) {
            foreach (string saved in (record.AppliedUpdates ?? "").Split(',')) if (saved == id) return true;
            return false;
        }
        private static void FoldUpdate(RangeBarJournalRecord target, RangeBarJournalRecord update) {
            if (HasUpdate(target, update.EventId)) return;
            target.AppliedUpdates = string.IsNullOrEmpty(target.AppliedUpdates) ? update.EventId : target.AppliedUpdates + "," + update.EventId;
            // A stale observation must not replace a newer/finalized bar snapshot.
            if (update.FillAnchor != null && (target.FillAnchor == null ||
                ((!target.FillAnchor.FinalBar || update.FillAnchor.FinalBar) && update.FillAnchor.BarTime >= target.FillAnchor.BarTime)))
                target.FillAnchor = update.FillAnchor.Copy();
            if ((target.FinalBar && !update.FinalBar) || update.BarTime < target.BarTime) return;
            target.BarTime = update.BarTime; target.TickId = update.TickId; target.Open = update.Open; target.High = update.High;
            target.Low = update.Low; target.Close = update.Close; target.FinalBar = update.FinalBar;
            target.PreviousBarTime = update.PreviousBarTime; target.PreviousOpen = update.PreviousOpen;
            target.PreviousHigh = update.PreviousHigh; target.PreviousLow = update.PreviousLow; target.PreviousClose = update.PreviousClose;
        }
        public static List<RangeBarJournalRecord> Fills(List<RangeBarJournalRecord> events) {
            var fills = new List<RangeBarJournalRecord>();
            var byId = new Dictionary<string, RangeBarJournalRecord>();
            foreach (var r in events) if (r.Kind == "entry" || r.Kind == "exit") {
                if (byId.ContainsKey(r.EventId)) continue;
                var copy = r.Copy(); fills.Add(copy); byId[r.EventId] = copy;
            }
            // Resolve targets independently of enumeration order, including updates before an entry row.
            foreach (var r in events) if (r.Kind == "bar") {
                RangeBarJournalRecord target;
                if (byId.TryGetValue(r.Target, out target) && target.TradeId == r.TradeId) FoldUpdate(target, r);
            }
            return fills;
        }
        public static RangeBarJournalRecord OpenTrade(List<RangeBarJournalRecord> events, out int remaining) {
            var entries = new Dictionary<string, RangeBarJournalRecord>();
            var quantities = new Dictionary<string, int>();
            foreach (var r in Fills(events)) {
                int quantity; quantities.TryGetValue(r.TradeId, out quantity);
                if (r.Kind == "entry") { entries[r.TradeId] = r; quantity += r.Quantity; }
                else quantity -= r.Quantity;
                quantities[r.TradeId] = quantity;
            }
            RangeBarJournalRecord open = null; remaining = 0;
            foreach (var pair in quantities) if (pair.Value > 0 && entries.ContainsKey(pair.Key)) {
                if (open != null) throw new InvalidOperationException("Multiple open journal trades: use a unique TradeHistoryStream per signal instance.");
                open = entries[pair.Key]; remaining = pair.Value;
            }
            return open;
        }
        private static void Field(System.Text.StringBuilder text, string name, string value) {
            text.Append("  ").Append(name).Append(": ").Append(value).Append("\r\n");
        }
        private static string TimeText(DateTime time) { return time.ToString("o", CI); }
        private static string TradeText(List<RangeBarJournalRecord> group) {
            var entry = new List<RangeBarJournalRecord>(); var exit = new List<RangeBarJournalRecord>();
            int entered = 0, exited = 0; double entryCost = 0, exitCost = 0;
            bool finalized = true;
            foreach (var fill in group) {
                if (fill.Kind == "entry") { entry.Add(fill); entered += fill.Quantity; entryCost += fill.Quantity * fill.Price; }
                else { exit.Add(fill); exited += fill.Quantity; exitCost += fill.Quantity * fill.Price; }
                finalized &= fill.FinalBar;
            }
            var common = entry.Count > 0 ? entry[0] : group[0];
            string status = entry.Count == 0 ? "ENTRY UNKNOWN" : exited == 0 ? "OPEN" :
                exited < entered ? "PARTIALLY CLOSED" : "CLOSED";
            if (exited > entered && entry.Count > 0) status = "QUANTITY MISMATCH";
            var text = new System.Text.StringBuilder(TradeBegin + "\r\n");
            Field(text, "Trade ID", Quote(common.TradeId)); Field(text, "Status", status);
            Field(text, "Trade type", Quote(common.Setup));
            Field(text, "Direction", entry.Count == 0 ? "UNKNOWN ENTRY" : common.Side > 0 ? "LONG" : "SHORT");
            Field(text, "Broker profile", Quote(common.Profile)); Field(text, "Account", Quote(common.Account));
            Field(text, "Instrument", Quote(common.Symbol)); Field(text, "Stream", Quote(common.Stream)); Field(text, "Chart", Quote(common.Role));
            Field(text, "Entry time", entry.Count > 0 ? TimeText(entry[0].Observed) : "unknown");
            Field(text, "Entry price", entered > 0 ? D(entryCost / entered) : "unknown");
            Field(text, "Entry quantity", entered.ToString(CI));
            Field(text, "Exit time", exit.Count > 0 && (status == "CLOSED" || entry.Count == 0) ? TimeText(exit[exit.Count - 1].Observed) : "pending");
            Field(text, "Exit price", exited > 0 ? D(exitCost / exited) : "pending"); Field(text, "Exit quantity", exited.ToString(CI));
            Field(text, "Remaining quantity", entry.Count > 0 ? Math.Max(0, entered - exited).ToString(CI) : "unknown");
            Field(text, "Bar details", finalized ? "FINAL" : "PENDING");
            Field(text, "Range ticks", D(common.RangeTicks)); Field(text, "Tick size", D(common.TickSize));
            // FIFO only within this known trade ID; report realized price movement before fees.
            int lot = 0, used = 0, matched = 0; double result = 0;
            foreach (var close in exit) {
                int remaining = close.Quantity;
                while (remaining > 0 && lot < entry.Count) {
                    var open = entry[lot];
                    if (open.Side == close.Side) break;
                    int quantity = Math.Min(remaining, open.Quantity - used);
                    result += open.Side * (close.Price - open.Price) * quantity;
                    matched += quantity; remaining -= quantity; used += quantity;
                    if (used == open.Quantity) { lot++; used = 0; }
                }
            }
            if (status == "CLOSED" && matched == exited && common.TickSize > 0)
                return CompactText(common, entry, exit, entered, entryCost / entered, exitCost / exited, result / common.TickSize);
            Field(text, "Realized result (price points)", matched > 0 ? D(result) : "pending");
            Field(text, "Realized result (ticks)", matched > 0 && common.TickSize > 0 ? D(result / common.TickSize) : "pending");
            Field(text, "Entry fills", entry.Count.ToString(CI)); Field(text, "Exit fills", exit.Count.ToString(CI));
            foreach (string kind in new[] {"Entry", "Exit"}) {
                var fills = kind == "Entry" ? entry : exit;
                for (int count = 0; count < fills.Count; count++) {
                    var fill = fills[count]; string prefix = kind + " " + (count + 1).ToString(CI) + " ";
                    string[] values = EncodeLegacy(fill).Split('\t');
                    // Persist each confirmed fill's identity/bar/price inside the single trade record.
                    // Shared scope/type fields appear once; updates are folded into these snapshots.
                    for (int i = 1; i < values.Length; i++) {
                        if (i >= 2 && i <= 10) continue;
                        string value = i == 1 ? Quote(fill.EventId) : values[i];
                        if (i == 13) value = fill.Side > 0 ? "BUY" : "SELL";
                        if (i == 24) value = fill.FinalBar ? "yes" : "no";
                        Field(text, prefix + Fields[i - 1], value);
                    }
                    Field(text, prefix + "Applied update IDs", Quote(fill.AppliedUpdates ?? ""));
                    if (fill.FillAnchor != null) Field(text, prefix + "Fill bar anchor", Quote(EncodeAnchor(fill.FillAnchor)));
                }
            }
            return text.Append(TradeEnd).ToString();
        }
        private static string EncodeAnchor(RangeBarJournalRecord anchor) {
            var fields = new List<string> { TimeText(anchor.BarTime), anchor.BarNumber.ToString(CI), anchor.TickId.ToString(CI),
                D(anchor.Open), D(anchor.High), D(anchor.Low), D(anchor.Close), anchor.FinalBar ? "1" : "0" };
            if (!anchor.FinalBar) fields.AddRange(new[] {TimeText(anchor.PreviousBarTime), D(anchor.PreviousOpen),
                D(anchor.PreviousHigh), D(anchor.PreviousLow), D(anchor.PreviousClose)});
            return string.Join("|", fields.ToArray());
        }
        private static RangeBarJournalRecord DecodeAnchor(RangeBarJournalRecord owner, string text) {
            string[] p = text.Split('|');
            if (p.Length != 8 && p.Length != 13) throw new FormatException("Incomplete fill-bar anchor");
            var anchor = owner.Copy(); anchor.FillAnchor = null;
            anchor.BarTime = DateTime.Parse(p[0], CI, System.Globalization.DateTimeStyles.RoundtripKind);
            anchor.BarNumber = int.Parse(p[1], CI); anchor.TickId = uint.Parse(p[2], CI);
            anchor.Open = double.Parse(p[3], CI); anchor.High = double.Parse(p[4], CI);
            anchor.Low = double.Parse(p[5], CI); anchor.Close = double.Parse(p[6], CI);
            if (p[7] != "1" && p[7] != "0") throw new FormatException("Invalid fill-bar state");
            anchor.FinalBar = p[7] == "1";
            if (anchor.BarTime == DateTime.MinValue || anchor.BarNumber < 0 ||
                double.IsNaN(anchor.Open) || double.IsInfinity(anchor.Open) || double.IsNaN(anchor.High) || double.IsInfinity(anchor.High) ||
                double.IsNaN(anchor.Low) || double.IsInfinity(anchor.Low) || double.IsNaN(anchor.Close) || double.IsInfinity(anchor.Close) || anchor.High < anchor.Low)
                throw new FormatException("Invalid fill-bar values");
            anchor.PreviousBarTime = DateTime.MinValue; anchor.PreviousOpen = anchor.PreviousHigh = anchor.PreviousLow = anchor.PreviousClose = 0;
            if (p.Length == 13) {
                anchor.PreviousBarTime = DateTime.Parse(p[8], CI, System.Globalization.DateTimeStyles.RoundtripKind);
                anchor.PreviousOpen = double.Parse(p[9], CI); anchor.PreviousHigh = double.Parse(p[10], CI);
                anchor.PreviousLow = double.Parse(p[11], CI); anchor.PreviousClose = double.Parse(p[12], CI);
            } else if (!anchor.FinalBar) throw new FormatException("Missing forming fill-bar context");
            return anchor;
        }
        private static readonly int[] CompactFields = {1, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 24};
        private static string CompactText(RangeBarJournalRecord common, List<RangeBarJournalRecord> entry,
            List<RangeBarJournalRecord> exit, int quantity, double entryPrice, double exitPrice, double ticks) {
            var text = new System.Text.StringBuilder(CompactBegin + "\r\n");
            Field(text, "Trade", CI.TextInfo.ToTitleCase(common.Setup));
            Field(text, "Position", (common.Side > 0 ? "LONG" : "SHORT") + " - " + quantity.ToString(CI) + " " + common.Symbol);
            Field(text, "Entry", entry[0].Observed.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CI) + " @ " + entryPrice.ToString("0.00########", CI));
            Field(text, "Exit", exit[exit.Count - 1].Observed.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CI) + " @ " + exitPrice.ToString("0.00########", CI));
            Field(text, "Profit", ticks.ToString("+0.########;-0.########;0", CI) + " ticks (before fees)");
            // Same file, compact textual metadata: identity, scope and exact chart anchors.
            // Finalized closed bars no longer need previous-bar or fill-observation diagnostics.
            var metadata = new System.Text.StringBuilder("id=" + S(common.TradeId) + ";type=" + S(common.Setup) +
                ";profile=" + S(common.Profile) + ";account=" + S(common.Account) + ";symbol=" + S(common.Symbol) +
                ";stream=" + S(common.Stream) + ";chart=" + S(common.Role) + ";range=" + D(common.RangeTicks) + ";tick=" + D(common.TickSize));
            foreach (string kind in new[] {"in", "out"}) foreach (var fill in kind == "in" ? entry : exit) {
                string[] original = EncodeLegacy(fill).Split('\t'); var parts = new List<string>();
                foreach (int index in CompactFields) parts.Add(original[index]);
                parts.Add(S(fill.AppliedUpdates));
                if (!fill.FinalBar) for (int i = 29; i <= 33; i++) parts.Add(original[i]);
                metadata.Append(';').Append(kind).Append('=').Append(string.Join("|", parts.ToArray()));
                if (fill.FillAnchor != null) metadata.Append(";at=").Append(S(fill.EventId)).Append('|').Append(EncodeAnchor(fill.FillAnchor));
            }
            Field(text, "Chart metadata", metadata.ToString());
            return text.Append(TradeEnd).ToString();
        }
        private static List<RangeBarJournalRecord> DecodeCompact(string block) {
            string[] lines = block.Replace("\r\n", "\n").Split('\n');
            if (lines[0] != CompactBegin || lines[lines.Length - 1] != TradeEnd) throw new FormatException("Incomplete compact trade");
            string metadata = null;
            for (int i = 1; i < lines.Length - 1; i++) {
                string line = lines[i].Trim();
                if (!line.StartsWith("Chart metadata: ", StringComparison.Ordinal)) continue;
                if (metadata != null) throw new FormatException("Duplicate compact metadata");
                metadata = line.Substring("Chart metadata: ".Length);
            }
            if (metadata == null) throw new FormatException("Missing chart metadata");
            var common = new Dictionary<string, string>(StringComparer.Ordinal);
            var fillTokens = new List<string>();
            var anchors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string token in metadata.Split(';')) {
                int equals = token.IndexOf('=');
                if (equals <= 0) throw new FormatException("Invalid compact metadata");
                string key = token.Substring(0, equals), value = token.Substring(equals + 1);
                if (key == "in" || key == "out") fillTokens.Add(token);
                else if (key == "at") {
                    int divider = value.IndexOf('|');
                    if (divider <= 0) throw new FormatException("Invalid anchor owner");
                    string owner = Uri.UnescapeDataString(value.Substring(0, divider));
                    if (anchors.ContainsKey(owner)) throw new FormatException("Duplicate fill anchor");
                    anchors[owner] = value.Substring(divider + 1);
                }
                else { if (common.ContainsKey(key)) throw new FormatException("Duplicate compact scope"); common[key] = value; }
            }
            foreach (string key in new[] {"id", "type", "profile", "account", "symbol", "stream", "chart", "range", "tick"})
                if (!common.ContainsKey(key)) throw new FormatException("Missing compact scope");
            var result = new List<RangeBarJournalRecord>(); var seen = new HashSet<string>();
            int entered = 0, exited = 0, direction = 0;
            foreach (string token in fillTokens) {
                int equals = token.IndexOf('='); string kind = token.Substring(0, equals);
                string[] compact = token.Substring(equals + 1).Split('|');
                if (compact.Length != 14 && compact.Length != 19) throw new FormatException("Incomplete chart anchor");
                var parts = new string[34]; parts[0] = "RBJ1";
                parts[2] = common["id"]; parts[3] = kind == "in" ? "entry" : "exit"; parts[4] = "";
                parts[5] = common["type"]; parts[6] = common["profile"]; parts[7] = common["account"];
                parts[8] = common["symbol"]; parts[9] = common["stream"]; parts[10] = common["chart"];
                for (int i = 0; i < CompactFields.Length; i++) parts[CompactFields[i]] = compact[i];
                parts[22] = common["range"]; parts[23] = common["tick"];
                parts[25] = DateTime.MinValue.ToString("o", CI); parts[26] = parts[27] = parts[28] = "0";
                parts[29] = DateTime.MinValue.ToString("o", CI); parts[30] = parts[31] = parts[32] = parts[33] = "0";
                if (compact.Length == 19) for (int i = 29; i <= 33; i++) parts[i] = compact[i - 15];
                else if (parts[24] != "1") throw new FormatException("Missing forming-bar identity");
                var fill = DecodeLegacy(string.Join("\t", parts)); fill.AppliedUpdates = Uri.UnescapeDataString(compact[13]);
                if (!seen.Add(fill.EventId)) throw new FormatException("Duplicate compact fill");
                string anchor;
                if (anchors.TryGetValue(fill.EventId, out anchor)) fill.FillAnchor = DecodeAnchor(fill, anchor);
                if (kind == "in") {
                    if (direction != 0 && direction != fill.Side) throw new FormatException("Conflicting entries");
                    direction = fill.Side; entered += fill.Quantity;
                } else exited += fill.Quantity;
                result.Add(fill);
            }
            foreach (string owner in anchors.Keys) if (!seen.Contains(owner)) throw new FormatException("Unknown anchor owner");
            if (entered <= 0 || entered != exited) throw new FormatException("Compact record is not fully closed");
            foreach (var fill in result) if (fill.Side != (fill.Kind == "entry" ? direction : -direction))
                throw new FormatException("Compact close has wrong direction");
            return result;
        }
        private static List<RangeBarJournalRecord> DecodeTrade(string block) {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] lines = block.Replace("\r\n", "\n").Split('\n');
            if (lines[0] != TradeBegin || lines[lines.Length - 1] != TradeEnd) throw new FormatException("Incomplete trade record");
            for (int i = 1; i < lines.Length - 1; i++) {
                string line = lines[i].Trim(); int colon = line.IndexOf(':');
                if (colon <= 0) throw new FormatException("Invalid trade field");
                string key = line.Substring(0, colon);
                if (values.ContainsKey(key)) throw new FormatException("Duplicate trade field");
                values[key] = line.Substring(colon + 1).Trim();
            }
            string[] shared = {"Trade ID", "Trade type", "Broker profile", "Account", "Instrument", "Stream", "Chart"};
            foreach (string name in shared) if (!values.ContainsKey(name)) throw new FormatException("Missing trade scope");
            var result = new List<RangeBarJournalRecord>();
            foreach (string kind in new[] {"Entry", "Exit"}) {
                string countText;
                if (!values.TryGetValue(kind + " fills", out countText)) throw new FormatException("Missing fill count");
                int count = int.Parse(countText, CI);
                if (count < 0 || count > values.Count) throw new FormatException("Invalid fill count");
                for (int number = 1; number <= count; number++) {
                    string prefix = kind + " " + number.ToString(CI) + " ";
                    var parts = new string[Fields.Length + 1]; parts[0] = "RBJ1";
                    parts[2] = S(Unquote(values["Trade ID"])); parts[3] = kind == "Entry" ? "entry" : "exit"; parts[4] = "";
                    for (int i = 5; i <= 10; i++) parts[i] = S(Unquote(values[Fields[i - 1]]));
                    for (int i = 1; i < parts.Length; i++) {
                        if (i >= 2 && i <= 10) continue;
                        string value;
                        if (!values.TryGetValue(prefix + Fields[i - 1], out value)) throw new FormatException("Missing fill field");
                        if (i == 1) value = S(Unquote(value));
                        if (i == 13) { if (value == "BUY") value = "1"; else if (value == "SELL") value = "-1"; else throw new FormatException("Invalid side"); }
                        if (i == 24) { if (value == "yes") value = "1"; else if (value == "no") value = "0"; else throw new FormatException("Invalid finalized flag"); }
                        parts[i] = value;
                    }
                    var fill = DecodeLegacy(string.Join("\t", parts));
                    string updates;
                    if (!values.TryGetValue(prefix + "Applied update IDs", out updates)) throw new FormatException("Missing update identities");
                    fill.AppliedUpdates = Unquote(updates);
                    string anchor;
                    if (values.TryGetValue(prefix + "Fill bar anchor", out anchor)) fill.FillAnchor = DecodeAnchor(fill, Unquote(anchor));
                    result.Add(fill);
                }
            }
            if (result.Count == 0) throw new FormatException("Empty trade record");
            return result;
        }
        private static string GroupedText(List<RangeBarJournalRecord> history) {
            const string rule = "================================================================================";
            var order = new List<string>();
            var groups = new Dictionary<string, List<RangeBarJournalRecord>>(StringComparer.Ordinal);
            foreach (var r in Fills(history)) {
                List<RangeBarJournalRecord> group;
                if (!groups.TryGetValue(r.TradeId, out group)) { group = new List<RangeBarJournalRecord>(); groups[r.TradeId] = group; order.Add(r.TradeId); }
                group.Add(r);
            }
            var text = new System.Text.StringBuilder("RangeBar trade history\r\n" +
                "One record per trade. Entry and exit times are broker-confirmation receipt times.\r\n\r\n");
            foreach (string id in order) text.Append(rule).Append("\r\n").Append(TradeText(groups[id]))
                .Append("\r\n").Append(rule).Append("\r\n\r\n");
            return text.ToString();
        }
        private static void SaveGrouped(string path, List<RangeBarJournalRecord> history) {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(GroupedText(history));
                using (var file = new System.IO.FileStream(temporary, System.IO.FileMode.CreateNew,
                    System.IO.FileAccess.Write, System.IO.FileShare.None)) {
                    file.Write(bytes, 0, bytes.Length); file.Flush(true);
                }
                if (System.IO.File.Exists(path)) {
                    try { System.IO.File.Replace(temporary, path, null); }
                    catch (System.IO.FileNotFoundException) { System.IO.File.Move(temporary, path); }
                } else System.IO.File.Move(temporary, path);
            } finally {
                try { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
                catch (System.IO.IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        public static void Consolidate(string path) {
            if (!System.IO.File.Exists(path)) return;
            using (var mutex = new System.Threading.Mutex(false, "Local\\RangeBarJournal-" + Hash(System.IO.Path.GetFullPath(path).ToUpperInvariant()))) {
                bool locked = false;
                try {
                    try { locked = mutex.WaitOne(1000); } catch (System.Threading.AbandonedMutexException) { locked = true; }
                    if (!locked) throw new System.IO.IOException("Journal writer busy");
                    if (!System.IO.File.Exists(path)) return;
                    var history = Read(path);
                    if (Fills(history).Count > 0) SaveGrouped(path, history);
                } finally { if (locked) mutex.ReleaseMutex(); }
            }
        }
        public static void Append(string path, RangeBarJournalRecord record) {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            using (var mutex = new System.Threading.Mutex(false, "Local\\RangeBarJournal-" + Hash(System.IO.Path.GetFullPath(path).ToUpperInvariant()))) {
                bool locked = false;
                try {
                    try { locked = mutex.WaitOne(1000); } catch (System.Threading.AbandonedMutexException) { locked = true; }
                    if (!locked) throw new System.IO.IOException("Journal writer busy");
                    bool exists = System.IO.File.Exists(path);
                    var stored = new List<RangeBarJournalRecord>();
                    if (exists) ReadFile(path, stored, new HashSet<string>(StringComparer.Ordinal));
                    var history = Read(path);
                    bool duplicate = false;
                    foreach (var prior in stored) if (prior.EventId == record.EventId || HasUpdate(prior, record.EventId)) { duplicate = true; break; }
                    if (duplicate && exists) return;
                    if (!duplicate) history.Add(record.Copy());
                    // Preserve all events while moving late updates into their own trade group.
                    // The flushed temporary file atomically replaces the prior readable journal.
                    SaveGrouped(path, history);
                } finally { if (locked) mutex.ReleaseMutex(); }
            }
        }
    }
    // END RANGE BAR JOURNAL
}
