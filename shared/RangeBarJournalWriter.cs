    // BEGIN RANGE BAR JOURNAL WRITER -- generated into the signal; never accesses MultiCharts study objects.
    internal sealed class RangeBarDayPnl {
        public DateTime Day, Updated;
        public double RealizedPoints;
        public long Revision, Version;
    }
    internal sealed class RangeBarPnlTotal {
        public bool Ready;
        public double Realized, Open;
        public double Total { get { return Realized + Open; } }
    }
    internal sealed class RangeBarPnlBook {
        private sealed class Contribution {
            public string Group, Position;
            public DateTime Day;
            public long Order;
            public bool IsOpen;
            public double Open;
        }
        private readonly object gate = new object();
        private readonly Dictionary<object, Contribution> live = new Dictionary<object, Contribution>();
        private readonly Dictionary<string, RangeBarDayPnl> realized = new Dictionary<string, RangeBarDayPnl>();
        private long sequence;
        public RangeBarPnlTotal Update(object owner, string group, string position, DateTime day,
            bool isOpen, double open, double pointValue, RangeBarDayPnl snapshot) {
            lock (gate) {
                live[owner] = new Contribution { Group = group, Position = position, Day = day,
                    Order = ++sequence, IsOpen = isOpen, Open = open };
                RangeBarDayPnl saved;
                realized.TryGetValue(group, out saved);
                if (snapshot != null && snapshot.Day == day && (saved == null || saved.Day != day || snapshot.Version >= saved.Version))
                    realized[group] = saved = snapshot;
                var result = new RangeBarPnlTotal { Ready = saved != null && saved.Day == day };
                if (result.Ready) result.Realized = saved.RealizedPoints * pointValue;
                var positions = new Dictionary<string, Contribution>(StringComparer.Ordinal);
                foreach (var item in live.Values) if (item.Group == group && item.Day == day && item.IsOpen) {
                    Contribution prior;
                    if (!positions.TryGetValue(item.Position, out prior) || item.Order >= prior.Order) positions[item.Position] = item;
                }
                foreach (var item in positions.Values) result.Open += item.Open;
                return result;
            }
        }
        public void Remove(object owner) { lock (gate) live.Remove(owner); }
    }
    internal sealed class RangeBarJournalWriter {
        private sealed class PnlQuery {
            public bool Pending;
            public DateTime Next;
            public RangeBarDayPnl Result;
        }
        private sealed class Work {
            public string Directory;
            public RangeBarJournalRecord Record;
            public long Revision;
        }
        private sealed class StreamState {
            public RangeBarJournalRecord Open;
            public int Remaining;
            public readonly Dictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly List<RangeBarJournalRecord> Active = new List<RangeBarJournalRecord>();
            public readonly Dictionary<string, RangeBarJournalRecord> Fills = new Dictionary<string, RangeBarJournalRecord>();
        }
        private readonly object gate = new object();
        private readonly Queue<Work> queue = new Queue<Work>();
        private readonly Queue<string> errors = new Queue<string>();
        private readonly Dictionary<string, StreamState> streams = new Dictionary<string, StreamState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PnlQuery> pnl = new Dictionary<string, PnlQuery>(StringComparer.Ordinal);
        private bool running;
        private long pnlRevision;
        private static long pnlVersion;
        private System.Threading.Timer retry;
        // The injectable sink makes a blocked/failed disk write testable without touching a broker.
        private readonly Action<string, RangeBarJournalRecord> append;
        public RangeBarJournalWriter() : this(RangeBarJournal.Append) { }
        internal RangeBarJournalWriter(Action<string, RangeBarJournalRecord> sink) { append = sink; }
        internal static string PnlKey(string directory, RangeBarJournalRecord r) {
            return directory + "|" + Uri.EscapeDataString(r.Profile) + "|" + Uri.EscapeDataString(r.Account) + "|" +
                Uri.EscapeDataString(r.Symbol) + "|" + Uri.EscapeDataString(r.Stream) + "|" + r.Observed.Date.ToString("yyyy-MM-dd");
        }
        public RangeBarDayPnl RequestDayPnl(string directory, string profile, string account, string symbol, string stream, DateTime day) {
            if (string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(account)) return null;
            var request = new RangeBarJournalRecord {Kind = "pnl", Profile = profile, Account = account,
                Symbol = symbol, Stream = stream, Observed = day.Date};
            string key = PnlKey(directory, request);
            lock (gate) {
                PnlQuery query;
                if (!pnl.TryGetValue(key, out query)) { query = new PnlQuery(); pnl[key] = query; }
                if (!query.Pending && DateTime.UtcNow >= query.Next) {
                    query.Pending = true; query.Next = DateTime.UtcNow.AddMilliseconds(500);
                    Enqueue(directory, request);
                }
                return query.Result != null && query.Result.Revision == pnlRevision ? query.Result : null;
            }
        }
        internal static double RealizedPoints(List<RangeBarJournalRecord> history, RangeBarJournalRecord filter) {
            var fills = RangeBarJournal.Fills(history);
            fills.Sort(delegate(RangeBarJournalRecord a, RangeBarJournalRecord b) {
                int byTime = a.Observed.CompareTo(b.Observed);
                if (byTime != 0) return byTime;
                if (a.TradeId == b.TradeId && a.Kind != b.Kind) return a.Kind == "entry" ? -1 : 1;
                return string.CompareOrdinal(a.EventId, b.EventId);
            });
            var lots = new Dictionary<string, Queue<RangeBarJournalRecord>>(StringComparer.Ordinal);
            double result = 0;
            foreach (var fill in fills) {
                if (fill.Profile != filter.Profile || fill.Account != filter.Account || fill.Symbol != filter.Symbol || fill.Stream != filter.Stream) continue;
                Queue<RangeBarJournalRecord> queue;
                if (!lots.TryGetValue(fill.TradeId, out queue)) { queue = new Queue<RangeBarJournalRecord>(); lots[fill.TradeId] = queue; }
                if (fill.Kind == "entry") queue.Enqueue(fill.Copy());
                else if (fill.Kind == "exit") {
                    int remaining = fill.Quantity;
                    while (remaining > 0 && queue.Count > 0) {
                        var entry = queue.Peek(); if (entry.Side == fill.Side) break;
                        int closed = Math.Min(remaining, entry.Quantity);
                        DateTime time = fill.Observed.Kind == DateTimeKind.Utc ? fill.Observed.ToLocalTime() : fill.Observed;
                        if (time.Date == filter.Observed.Date) result += entry.Side * (fill.Price - entry.Price) * closed;
                        remaining -= closed; entry.Quantity -= closed;
                        if (entry.Quantity == 0) queue.Dequeue();
                    }
                }
            }
            return result;
        }
        public void Enqueue(string directory, RangeBarJournalRecord record) {
            var item = new Work { Directory = directory, Record = record.Copy() };
            lock (gate) {
                if (record.Kind == "entry" || record.Kind == "exit") {
                    pnlRevision++;
                    foreach (var query in pnl.Values) query.Next = DateTime.MinValue;
                }
                item.Revision = pnlRevision;
                queue.Enqueue(item);
                if (!running) { running = true; System.Threading.ThreadPool.QueueUserWorkItem(Drain); }
            }
        }
        public string TakeError() { lock (gate) return errors.Count > 0 ? errors.Dequeue() : null; }
        internal int PendingCount { get { lock (gate) return queue.Count; } }
        internal bool WaitForIdle(int milliseconds) {
            lock (gate) {
                var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
                while (running || queue.Count > 0) {
                    int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remaining <= 0 || !System.Threading.Monitor.Wait(gate, remaining)) return false;
                }
                return true;
            }
        }
        private void Error(string message) {
            lock (gate) {
                if (errors.Count < 8) errors.Enqueue(message);
            }
        }
        private void Drain(object ignored) {
            for (;;) {
                Work item;
                lock (gate) {
                    if (queue.Count == 0) { running = false; System.Threading.Monitor.PulseAll(gate); return; }
                    item = queue.Peek();
                }
                try { Write(item); }
                catch (System.IO.IOException ex) { Retry(ex.Message); return; }
                catch (UnauthorizedAccessException ex) { Retry(ex.Message); return; }
                catch (Exception ex) {
                    // Unknown association/configuration is reported; it never touches trading state.
                    Error(ex.Message);
                }
                lock (gate) { queue.Dequeue(); }
            }
        }
        private void Retry(string message) {
            Error(message);
            lock (gate) {
                retry = new System.Threading.Timer(delegate(object ignored) {
                    lock (gate) { retry.Dispose(); retry = null; }
                    Drain(null);
                }, null, 1000, System.Threading.Timeout.Infinite);
            }
        }
        private void Write(Work work) {
            var r = work.Record.Copy();
            if (string.IsNullOrWhiteSpace(r.Profile) || string.IsNullOrWhiteSpace(r.Account))
                throw new InvalidOperationException("Trade history requires a trading profile and account.");
            if (r.Kind == "pnl") {
                var history = new List<RangeBarJournalRecord>();
                foreach (string file in RangeBarJournal.HistoryFiles(work.Directory)) history.AddRange(RangeBarJournal.Read(file));
                var result = new RangeBarDayPnl {Day = r.Observed.Date, Updated = DateTime.UtcNow,
                    Revision = work.Revision, RealizedPoints = RealizedPoints(history, r),
                    Version = System.Threading.Interlocked.Increment(ref pnlVersion)};
                lock (gate) { var query = pnl[PnlKey(work.Directory, r)]; query.Result = result; query.Pending = false; }
                return;
            }
            // A fill or migration invalidates cached totals. A later chart call
            // schedules the refresh; no study thread ever reads the files.
            lock (gate) foreach (var query in pnl.Values) query.Next = DateTime.MinValue;
            string scope = RangeBarJournal.ScopeKey(r);
            string streamPath = RangeBarJournal.FileName(work.Directory, scope);
            if (r.Kind == "consolidate") { RangeBarJournal.SplitDaily(streamPath); return; }
            StreamState state;
            if (!streams.TryGetValue(streamPath, out state)) {
                RangeBarJournal.SplitDaily(streamPath);
                var events = new List<RangeBarJournalRecord>();
                state = new StreamState();
                foreach (string file in RangeBarJournal.StreamFiles(streamPath))
                    foreach (var fill in RangeBarJournal.Fills(RangeBarJournal.Read(file))) {
                        if (RangeBarJournal.ScopeKey(fill) != scope || state.Paths.ContainsKey(fill.EventId)) continue;
                        state.Paths[fill.EventId] = file; events.Add(fill);
                    }
                state.Open = RangeBarJournal.OpenTrade(events, out state.Remaining);
                foreach (var fill in RangeBarJournal.Fills(events)) {
                    state.Fills[fill.EventId] = fill;
                    if (state.Open != null && fill.TradeId == state.Open.TradeId) state.Active.Add(fill.Copy());
                }
                streams[streamPath] = state;
            }
            string path;
            if (r.Kind == "bar") {
                if (!state.Paths.TryGetValue(r.Target, out path))
                    throw new InvalidOperationException("Bar annotation has no confirmed journal execution.");
            } else if (state.Open != null) path = state.Paths[state.Open.EventId];
            else path = RangeBarJournal.DailyFileName(streamPath, r.Observed);
            // A cross-midnight trade and all its updates stay on its entry day.
            // Recreate a deleted daily file from only the still-open trade.
            bool missing = !System.IO.File.Exists(path);
            // Another chart may already have recreated the shared daily file.
            // Restore missing own-trade entries even when that file exists again.
            if (state.Active.Count > 0) {
                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (var saved in RangeBarJournal.Read(path)) present.Add(saved.EventId);
                foreach (var active in state.Active)
                    if (state.Paths[active.EventId] == path && !present.Contains(active.EventId)) append(path, active);
            }
            if (r.Kind == "bar") {
                RangeBarJournalRecord original;
                if (!state.Fills.TryGetValue(r.Target, out original))
                    throw new InvalidOperationException("Bar annotation has no confirmed journal execution.");
                r.TradeId = original.TradeId; r.Setup = original.Setup; r.Side = original.Side; r.Quantity = original.Quantity;
                append(path, r);
                foreach (var active in state.Active) if (active.EventId == r.Target) {
                    active.BarTime = r.BarTime; active.TickId = r.TickId; active.Open = r.Open; active.High = r.High;
                    active.Low = r.Low; active.Close = r.Close; active.FinalBar = r.FinalBar;
                    active.FillAnchor = r.FillAnchor == null ? active.FillAnchor : r.FillAnchor.Copy();
                }
                return;
            }
            if (r.Kind == "entry") {
                if (state.Open != null && state.Open.Side != r.Side)
                    throw new InvalidOperationException("Entry conflicts with recorded open trade; no association was guessed.");
                if (state.Open != null) { r.TradeId = state.Open.TradeId; r.Setup = state.Open.Setup; }
                append(path, r); // State advances only after success, including idempotent retry after an interrupted flush.
                state.Open = r.Copy(); state.Remaining += r.Quantity; state.Fills[r.EventId] = r; state.Paths[r.EventId] = path; state.Active.Add(r.Copy());
            } else if (r.Kind == "exit") {
                if (state.Open == null && (missing || state.Fills.Count == 0)) {
                    // After deletion plus restart there is no recoverable entry. Preserve
                    // the real exit as unknown, with no fabricated entry or connection.
                    r.Setup = "unknown"; append(path, r); state.Fills[r.EventId] = r; state.Paths[r.EventId] = path;
                    return;
                }
                if (state.Open == null || state.Open.Side == r.Side || r.Quantity > state.Remaining)
                    throw new InvalidOperationException("Exit has no matching recorded open quantity; no association was guessed.");
                r.TradeId = state.Open.TradeId; r.Setup = state.Open.Setup;
                append(path, r);
                state.Remaining -= r.Quantity; state.Fills[r.EventId] = r; state.Paths[r.EventId] = path; state.Active.Add(r.Copy());
                if (state.Remaining == 0) { state.Open = null; state.Active.Clear(); }
            } else throw new InvalidOperationException("Unsupported history event.");
        }
    }
    // END RANGE BAR JOURNAL WRITER
