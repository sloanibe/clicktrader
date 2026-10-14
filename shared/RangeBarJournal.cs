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
            // Consolidated .txt snapshots are authoritative; retain any additional legacy events.
            ReadFile(path, result, seen);
            if (string.Equals(System.IO.Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
                ReadFile(System.IO.Path.ChangeExtension(path, ".rbj"), result, seen);
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
                if (line == Begin || line == TradeBegin || line == CompactBegin) { block = new System.Text.StringBuilder(line); continue; }
                if ((line == End || line == TradeEnd) && block != null) {
                    block.Append('\n').Append(line);
                    AddDecoded(block.ToString(), result, seen); block = null;
                } else if (line.StartsWith("RBJ1\t", StringComparison.Ordinal)) {
                    block = null; AddDecoded(line, result, seen);
                } else if (block != null) block.Append('\n').Append(line);
            }
        }
        private static void AddDecoded(string text, List<RangeBarJournalRecord> result, HashSet<string> seen) {
            try {
                if (text.StartsWith(CompactBegin, StringComparison.Ordinal)) {
                    foreach (var r in DecodeCompact(text)) if (seen.Add(r.EventId)) result.Add(r);
                } else if (text.StartsWith(TradeBegin, StringComparison.Ordinal)) {
                    foreach (var r in DecodeTrade(text)) if (seen.Add(r.EventId)) result.Add(r);
                } else { var r = Decode(text); if (seen.Add(r.EventId)) result.Add(r); }
            }
            catch (FormatException) { }
            catch (OverflowException) { }
        }
        private static bool HasUpdate(RangeBarJournalRecord record, string id) {
            foreach (string saved in (record.AppliedUpdates ?? "").Split(',')) if (saved == id) return true;
            return false;
        }
        private static void FoldUpdate(RangeBarJournalRecord target, RangeBarJournalRecord update) {
            if (HasUpdate(target, update.EventId)) return;
            target.AppliedUpdates = string.IsNullOrEmpty(target.AppliedUpdates) ? update.EventId : target.AppliedUpdates + "," + update.EventId;
            // A stale observation must not replace a newer/finalized bar snapshot.
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
                }
            }
            return text.Append(TradeEnd).ToString();
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
            foreach (string token in metadata.Split(';')) {
                int equals = token.IndexOf('=');
                if (equals <= 0) throw new FormatException("Invalid compact metadata");
                string key = token.Substring(0, equals), value = token.Substring(equals + 1);
                if (key == "in" || key == "out") fillTokens.Add(token);
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
                if (kind == "in") {
                    if (direction != 0 && direction != fill.Side) throw new FormatException("Conflicting entries");
                    direction = fill.Side; entered += fill.Quantity;
                } else exited += fill.Quantity;
                result.Add(fill);
            }
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
                    fill.AppliedUpdates = Unquote(updates); result.Add(fill);
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
