    // BEGIN RANGE BAR JOURNAL WRITER -- generated into the signal; never accesses MultiCharts study objects.
    internal sealed class RangeBarJournalWriter {
        private sealed class Work {
            public string Directory;
            public RangeBarJournalRecord Record;
        }
        private sealed class StreamState {
            public RangeBarJournalRecord Open;
            public int Remaining;
            public readonly List<RangeBarJournalRecord> Active = new List<RangeBarJournalRecord>();
            public readonly Dictionary<string, RangeBarJournalRecord> Fills = new Dictionary<string, RangeBarJournalRecord>();
        }
        private readonly object gate = new object();
        private readonly Queue<Work> queue = new Queue<Work>();
        private readonly Queue<string> errors = new Queue<string>();
        private readonly Dictionary<string, StreamState> streams = new Dictionary<string, StreamState>(StringComparer.OrdinalIgnoreCase);
        private bool running;
        private System.Threading.Timer retry;
        // The injectable sink makes a blocked/failed disk write testable without touching a broker.
        private readonly Action<string, RangeBarJournalRecord> append;
        public RangeBarJournalWriter() : this(RangeBarJournal.Append) { }
        internal RangeBarJournalWriter(Action<string, RangeBarJournalRecord> sink) { append = sink; }
        public void Enqueue(string directory, RangeBarJournalRecord record) {
            var item = new Work { Directory = directory, Record = record.Copy() };
            lock (gate) {
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
            string scope = Uri.EscapeDataString(r.Profile) + "|" + Uri.EscapeDataString(r.Account) + "|" +
                Uri.EscapeDataString(r.Symbol) + "|" + Uri.EscapeDataString(r.Stream) + "|" + r.Role + "|" +
                r.RangeTicks.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            string path = RangeBarJournal.FileName(work.Directory, scope);
            if (r.Kind == "consolidate") { RangeBarJournal.Consolidate(path); return; }
            StreamState state;
            if (!streams.TryGetValue(path, out state)) {
                var events = RangeBarJournal.Read(path);
                state = new StreamState();
                state.Open = RangeBarJournal.OpenTrade(events, out state.Remaining);
                foreach (var fill in RangeBarJournal.Fills(events)) {
                    state.Fills[fill.EventId] = fill;
                    if (state.Open != null && fill.TradeId == state.Open.TradeId) state.Active.Add(fill.Copy());
                }
                streams[path] = state;
            }
            // Recreate a deleted file on the worker. Keep only the still-open trade's
            // confirmed entries and partial exits so its eventual exit remains paired.
            bool missing = !System.IO.File.Exists(path);
            if (missing) foreach (var active in state.Active) append(path, active);
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
                state.Open = r.Copy(); state.Remaining += r.Quantity; state.Fills[r.EventId] = r; state.Active.Add(r.Copy());
            } else if (r.Kind == "exit") {
                if (state.Open == null && missing) {
                    // After deletion plus restart there is no recoverable entry. Preserve
                    // the real exit as unknown, with no fabricated entry or connection.
                    r.Setup = "unknown"; append(path, r); state.Fills[r.EventId] = r;
                    return;
                }
                if (state.Open == null || state.Open.Side == r.Side || r.Quantity > state.Remaining)
                    throw new InvalidOperationException("Exit has no matching recorded open quantity; no association was guessed.");
                r.TradeId = state.Open.TradeId; r.Setup = state.Open.Setup;
                append(path, r);
                state.Remaining -= r.Quantity; state.Fills[r.EventId] = r; state.Active.Add(r.Copy());
                if (state.Remaining == 0) { state.Open = null; state.Active.Clear(); }
            } else throw new InvalidOperationException("Unsupported history event.");
        }
    }
    // END RANGE BAR JOURNAL WRITER
