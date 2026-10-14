using System;
using System.Collections.Generic;
using PowerLanguage.Indicator;

class RangeBarTradeHistoryTests {
    static int checks;
    static readonly DateTime Day = new DateTime(2026, 10, 14);
    static void Check(bool condition, string name) {
        checks++;
        if (!condition) throw new Exception(name);
    }
    static HistoryExecution Fill(int id, int side, int quantity, double price, int seconds,
                                 string account = "A", string profile = "Paper") {
        return new HistoryExecution {
            Key = HistoryMatching.OrderKey(profile, account, id),
            Scope = HistoryMatching.ScopeKey(profile, account, "MESZ26"),
            Account = account, Profile = profile, OrderId = id,
            Side = side, Quantity = quantity, Price = price,
            Time = Day.AddHours(9).AddSeconds(seconds), Name = "Tracker order"
        };
    }
    static HistoryView Build(params HistoryExecution[] fills) {
        return HistoryMatching.Build(new List<HistoryExecution>(fills), Day, new Dictionary<string, int>());
    }
    static void Main() {
        RangeBarJournalTests.Run();
        RangeBarTradeConsolidationTests.Run();
        RangeBarSignalJournalTests.Run();
        foreach (int side in new[] {1, -1}) {
            var entry = Fill(1, side, 1, 100, 1);
            var exit = Fill(2, -side, 1, 100 + side, 2);
            var view = Build(exit, entry); // Tracker enumeration need not be chronological.
            Check(view.Pairs.Count == 1 && view.Markers.Count == 2, "one matched round trip");
            Check(view.Pairs[0].Entry == entry && view.Pairs[0].Exit == exit, "correct long/short entry and exit");
            Check(view.Markers[0].Label == "T1 in 100" && view.Markers[1].Label == "T1 out " + (100 + side),
                  "entry and exit share visible trade number");
            Check(view.Markers[0].IsEntry && !view.Markers[1].IsEntry,
                  "arrow direction follows entry/exit role for both longs and shorts");
            Check(HistoryMatching.PriceResult(view.Pairs[0]) == 1, "profitable long/short price result");
            exit.Price = 100;
            Check(HistoryMatching.PriceResult(view.Pairs[0]) == 0, "break-even long/short price result");
            exit.Price = 100 - side;
            Check(HistoryMatching.PriceResult(view.Pairs[0]) == -1, "losing long/short price result");

            var partial = Build(Fill(1, side, 3, 100, 1), Fill(2, -side, 1, 101, 2), Fill(3, -side, 2, 102, 3));
            Check(partial.Pairs.Count == 2 && partial.Pairs[0].Quantity == 1 && partial.Pairs[1].Quantity == 2,
                  "partial exits preserve quantity");
            Check(partial.Pairs[0].TradeId == partial.Pairs[1].TradeId, "partial closes retain same entry ID");

            var scale = Build(Fill(1, side, 1, 100, 1), Fill(2, side, 2, 101, 2), Fill(3, -side, 3, 102, 3));
            Check(scale.Pairs.Count == 2 && scale.Pairs[0].Entry.OrderId == 1 && scale.Pairs[1].Entry.OrderId == 2,
                  "FIFO matches scale-in lots");
            Check(scale.Markers[2].Label == "T1 out 102 / T2 out 102", "one close identifies both entries");

            var reversal = Build(Fill(1, side, 1, 100, 1), Fill(2, -side, 3, 101, 2), Fill(3, side, 2, 102, 3));
            Check(reversal.Pairs.Count == 2 && reversal.Pairs[1].Entry.OrderId == 2 && reversal.Pairs[1].Quantity == 2,
                  "reversal closes existing lot and opens residual");
            Check(reversal.Markers[1].Label == "T1 out 101" && !reversal.Markers[1].IsEntry &&
                  reversal.Markers[2].Label == "T2 in 101" && reversal.Markers[2].IsEntry,
                  "reversal has separate in/out arrows at same execution");

            var unknown = HistoryMatching.Build(new List<HistoryExecution> { Fill(1, -side, 2, 101, 1) }, Day,
                new Dictionary<string, int> { {entry.Scope, side} });
            Check(unknown.Pairs.Count == 0 && unknown.Markers[0].Label == "T? out 101",
                  "carry-in position never creates a fabricated entry");
            Check(unknown.Markers[1].Label == "T1 in 101" && unknown.Markers[1].IsEntry,
                  "unknown carry-in reversal retains new entry role");
        }
        var isolated = Build(Fill(1, 1, 1, 100, 1, "A"), Fill(2, -1, 1, 101, 2, "B"),
                             Fill(3, -1, 1, 101, 3, "A", "Other"));
        Check(isolated.Pairs.Count == 0, "accounts and broker profiles never cross-pair");
        foreach (string name in new[] {"pin bar buy", "pin bar sell", "manual buy", "manual sell"}) {
            int side = name.EndsWith("sell") ? -1 : 1;
            var directedEntry = Fill(1, side, 1, 100, 1); directedEntry.Name = name;
            var directedExit = Fill(2, -side, 1, 100 + side, 2);
            var directedView = Build(directedEntry, directedExit);
            string prefix = name.StartsWith("pin bar") ? "P1" : "M1";
            Check(directedView.Markers[0].Label.StartsWith(prefix + " in ") &&
                directedView.Markers[1].Label.StartsWith(prefix + " out "),
                "directional order names retain pin/manual prefixes on both markers");
        }
        var pin = Fill(1, 1, 1, 100, 1); pin.Name = "pin bar";
        var pinExit = Fill(2, -1, 1, 101, 2); pinExit.Name = "ProfitLong";
        var manual = Fill(3, -1, 1, 102, 3); manual.Name = "manual";
        var manualExit = Fill(4, 1, 1, 101, 4); manualExit.Name = "ColorTrailShort";
        var named = Build(pin, pinExit, manual, manualExit);
        Check(named.Markers[0].Label == "P1 in 100" && named.Markers[1].Label == "P1 out 101",
              "pin entry name supplies matching P prefix on entry and exit");
        Check(named.Markers[2].Label == "M2 in 102" && named.Markers[3].Label == "M2 out 101",
              "manual entry name supplies matching M prefix with continuous trade numbering");
        pin.Name = " PIN BAR ";
        Check(Build(pin).Markers[0].Label == "P1 in 100", "recorded entry name decoding is case insensitive");
        var pinPartial = Fill(1, 1, 2, 100, 1); pinPartial.Name = "pin bar";
        var namedPartials = Build(pinPartial, pinExit, manualExit);
        Check(namedPartials.Markers[1].Label == "P1 out 101", "partial close retains recorded entry type");
        var duplicate = Fill(1, 1, 1, 100, 1);
        var updated = Fill(1, 1, 2, 100.25, 1);
        var deduped = Build(duplicate, updated, Fill(2, -1, 2, 101, 2), Fill(3, 1, 0, 99, 3));
        Check(deduped.Markers.Count == 2 && deduped.Pairs[0].Quantity == 2,
              "order updates replace earlier aggregate; unfilled orders omitted");
        Check(deduped.Pairs[0].Entry.Price == 100.25, "latest tracker average retained");
        Check(HistoryMatching.ScopeMatches("paper", "A", "MESZ26", "MES DEC26", "Paper", "A", "MESZ26"),
              "exact case-insensitive MC symbol matching");
        Check(HistoryMatching.ScopeMatches("Paper", "A", "MESZ26", "MES DEC26", "", "", "MES DEC26"),
              "broker symbol override supported");
        Check(!HistoryMatching.ScopeMatches("Paper", "A", "MESZ26", "", "", "", "MES"),
              "different contracts never match by prefix");
        Check(HistoryMatching.ScopeKey("AB", "C", "D") != HistoryMatching.ScopeKey("A", "BC", "D"),
              "scope keys cannot collide by concatenation");
        var yesterdayEntry = Fill(1, 1, 1, 100, 1);
        yesterdayEntry.Time = yesterdayEntry.Time.AddDays(-1);
        var yesterdayUnrelated = Fill(9, 1, 1, 100, 1, "B");
        yesterdayUnrelated.Time = yesterdayUnrelated.Time.AddDays(-1);
        var overnight = Build(yesterdayEntry, yesterdayUnrelated, Fill(2, -1, 1, 101, 2));
        Check(overnight.Pairs.Count == 1 && overnight.Markers.Count == 2, "today's close includes its prior-day entry only");
        Check(overnight.Markers[1].Label == "T1 in 100" && overnight.Markers[1].IsEntry,
              "prior-day entry has matching compact label");
        var tomorrow = Fill(3, -1, 1, 101, 2); tomorrow.Time = tomorrow.Time.AddDays(1);
        Check(Build(tomorrow).Markers.Count == 0, "display excludes tomorrow");
        Check(Build(Fill(1, 1, 1, 100, 1), Fill(2, -1, 1, 101, 1)).TimestampTies == 1,
              "same-timestamp opposite fills report ordering uncertainty");

        DateTime start = Day.AddHours(9);
        DateTime[] times = {start, start.AddSeconds(10), start.AddSeconds(20)};
        int index; double fraction; bool uncertain;
        Check(HistoryMatching.LocateBar(times, start.AddSeconds(2.5), false, start.AddSeconds(30),
            out index, out fraction, out uncertain) && index == 0 && fraction == .25 && !uncertain,
            "entry at quarter of range bar is not snapped to bar boundary");
        Check(HistoryMatching.LocateBar(times, start.AddSeconds(7.5), false, start.AddSeconds(30),
            out index, out fraction, out uncertain) && index == 0 && fraction == .75,
            "exit in same bar has a distinct horizontal position");
        Check(HistoryMatching.LocateBar(times, start.AddSeconds(12.5), true, start.AddSeconds(30),
            out index, out fraction, out uncertain) && index == 2 && fraction == .25,
            "close timestamp mode uses previous completion as interval start");
        Check(HistoryMatching.LocateBar(times, start.AddSeconds(10), false, start.AddSeconds(30),
            out index, out fraction, out uncertain) && index == 1 && fraction == 0,
            "exact open timestamp belongs to new bar");
        Check(HistoryMatching.LocateBar(times, start.AddSeconds(10), true, start.AddSeconds(30),
            out index, out fraction, out uncertain) && index == 1 && fraction == 1,
            "exact close timestamp belongs to completed bar");
        Check(!HistoryMatching.LocateBar(times, start.AddSeconds(-1), false, start.AddSeconds(30),
            out index, out fraction, out uncertain), "out-of-chart history is not moved onto first bar");
        Check(!HistoryMatching.LocateBar(times, start.AddSeconds(31), false, start.AddSeconds(30),
            out index, out fraction, out uncertain), "future records are not moved onto latest bar");
        Check(HistoryMatching.LocateBar(new[] {start, start, start.AddSeconds(10)}, start, false,
            start.AddSeconds(30), out index, out fraction, out uncertain) && uncertain,
            "duplicate range timestamps flag ambiguous bar placement");
        Check(HistoryMatching.LocateBar(new[] {start}, start, false, start,
            out index, out fraction, out uncertain) && uncertain && fraction == .5,
            "zero-duration bar never divides by zero or claims precision");
        Console.WriteLine("PASS: " + checks + " trade-history assertions");
    }
}
