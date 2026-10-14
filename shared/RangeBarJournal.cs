    // BEGIN RANGE BAR JOURNAL -- generated into each standalone study by tools/sync_range_bar_journal.py.
    // Version 2: labeled text blocks; version 1 TSV remains readable. Confirmed fills and bar updates, grouped by durable trade ID.
    internal sealed class RangeBarJournalRecord {
        public string EventId = Guid.NewGuid().ToString("N"), TradeId = "", Kind = "", Target = "";
        public string Setup = "unknown", Profile = "", Account = "", Symbol = "", Stream = "default", Role = "";
        public DateTime Observed, BarTime, FillBarTime, PreviousBarTime;
        public int Side, Quantity, BarNumber, FillBarNumber;
        public double Price, Open, High, Low, Close, RangeTicks, TickSize;
        public uint TickId, FillTickId;
        public double SubmittedPrice, PreviousOpen, PreviousHigh, PreviousLow, PreviousClose;
        public bool FinalBar;
        public RangeBarJournalRecord Copy() { return (RangeBarJournalRecord)MemberwiseClone(); }
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
            return DecodeLegacy(string.Join("\t", parts));
        }
        public static string Hash(string value) {
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }
        public static string FileName(string directory, string scope) {
            return System.IO.Path.Combine(directory, "rangebar-" + Hash(scope).Substring(0, 24) + ".txt");
        }
        public static string[] HistoryFiles(string directory) {
            var files = new List<string>();
            if (!System.IO.Directory.Exists(directory)) return files.ToArray();
            foreach (string path in System.IO.Directory.GetFiles(directory, "rangebar-*")) {
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
            // New .txt streams retain their old .rbj history without rewriting an active file.
            if (string.Equals(System.IO.Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
                ReadFile(System.IO.Path.ChangeExtension(path, ".rbj"), result, seen);
            ReadFile(path, result, seen);
            return result;
        }
        private static void ReadFile(string path, List<RangeBarJournalRecord> result, HashSet<string> seen) {
            if (!System.IO.File.Exists(path)) return;
            string contents;
            try {
                using (var file = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                using (var reader = new System.IO.StreamReader(file, System.Text.Encoding.UTF8)) contents = reader.ReadToEnd();
            } catch (System.IO.FileNotFoundException) { return; }
              catch (System.IO.DirectoryNotFoundException) { return; }
            // Only newline-terminated rows/blocks are committed. A later BEGIN abandons a torn block.
            int end = contents.LastIndexOf('\n');
            if (end < 0) return;
            System.Text.StringBuilder block = null;
            foreach (string row in contents.Substring(0, end).Split('\n')) {
                string line = row.TrimEnd('\r');
                if (line == Begin) { block = new System.Text.StringBuilder(line); continue; }
                if (line == End && block != null) {
                    block.Append('\n').Append(line);
                    AddDecoded(block.ToString(), result, seen); block = null;
                } else if (line.StartsWith("RBJ1\t", StringComparison.Ordinal)) {
                    block = null; AddDecoded(line, result, seen);
                } else if (block != null) block.Append('\n').Append(line);
            }
        }
        private static void AddDecoded(string text, List<RangeBarJournalRecord> result, HashSet<string> seen) {
            try { var r = Decode(text); if (seen.Add(r.EventId)) result.Add(r); }
            catch (FormatException) { }
            catch (OverflowException) { }
        }
        public static List<RangeBarJournalRecord> Fills(List<RangeBarJournalRecord> events) {
            var fills = new List<RangeBarJournalRecord>();
            var byId = new Dictionary<string, RangeBarJournalRecord>();
            foreach (var r in events) {
                if (r.Kind == "bar") {
                    RangeBarJournalRecord target;
                    if (byId.TryGetValue(r.Target, out target) && target.TradeId == r.TradeId) {
                        target.BarTime = r.BarTime; target.TickId = r.TickId; target.Open = r.Open; target.High = r.High;
                        target.Low = r.Low; target.Close = r.Close; target.FinalBar = r.FinalBar;
                    }
                } else { var copy = r.Copy(); fills.Add(copy); byId[r.EventId] = copy; }
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
        private static string GroupedText(List<RangeBarJournalRecord> history) {
            const string rule = "================================================================================";
            var order = new List<string>();
            var groups = new Dictionary<string, List<RangeBarJournalRecord>>(StringComparer.Ordinal);
            foreach (var r in history) {
                List<RangeBarJournalRecord> group;
                if (!groups.TryGetValue(r.TradeId, out group)) {
                    group = new List<RangeBarJournalRecord>(); groups[r.TradeId] = group; order.Add(r.TradeId);
                }
                group.Add(r);
            }
            var text = new System.Text.StringBuilder("RangeBar trade history\r\n" +
                "Confirmed strategy fills and bar updates. Times marked Confirmation received are receipt times.\r\n\r\n");
            foreach (string id in order) {
                var group = groups[id];
                var fills = Fills(group);
                RangeBarJournalRecord entry = null;
                int remaining = 0;
                bool finalized = true;
                foreach (var fill in fills) {
                    if (fill.Kind == "entry") { if (entry == null) entry = fill; remaining += fill.Quantity; }
                    else remaining -= fill.Quantity;
                    finalized &= fill.FinalBar;
                }
                string status = entry == null ? "ENTRY UNKNOWN" : remaining > 0 ? "OPEN" :
                    finalized ? "COMPLETE" : "CLOSED - BAR UPDATES PENDING";
                string setup = entry != null ? entry.Setup : group[0].Setup;
                string direction = entry == null ? "UNKNOWN ENTRY" : entry.Side > 0 ? "LONG" : "SHORT";
                text.Append(rule).Append("\r\nTRADE ").Append(Quote(id)).Append(" | ")
                    .Append(Quote(setup)).Append(" | ").Append(direction).Append("\r\n").Append(rule).Append("\r\n\r\n");
                foreach (var r in group) text.Append(Encode(r)).Append("\r\n\r\n");
                text.Append(rule).Append("\r\nEND TRADE ").Append(Quote(id)).Append(" | ")
                    .Append(status).Append("\r\n").Append(rule).Append("\r\n\r\n");
            }
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
        public static void Append(string path, RangeBarJournalRecord record) {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            using (var mutex = new System.Threading.Mutex(false, "Local\\RangeBarJournal-" + Hash(System.IO.Path.GetFullPath(path).ToUpperInvariant()))) {
                bool locked = false;
                try {
                    try { locked = mutex.WaitOne(1000); } catch (System.Threading.AbandonedMutexException) { locked = true; }
                    if (!locked) throw new System.IO.IOException("Journal writer busy");
                    bool exists = System.IO.File.Exists(path);
                    var history = new List<RangeBarJournalRecord>();
                    if (exists) ReadFile(path, history, new HashSet<string>(StringComparer.Ordinal));
                    else history = Read(path);
                    bool duplicate = false;
                    foreach (var prior in history) if (prior.EventId == record.EventId) { duplicate = true; break; }
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
