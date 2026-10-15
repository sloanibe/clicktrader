using System;

// Actual production control hit tests with chart/drawing coordinates stubbed.
class TradeControlClickTests {
    class ChartPoint { public double Price; public DateTime Time; }
    class MouseClickArgs { public ChartPoint point; public int bar_number, data_number = 1; }
    class ITrendLineObject { public ChartPoint Begin, End; }
    class FullData { public int Current = 11; }
    class BarData { public int CurrentBar = 10; public FullData FullSymbolData = new FullData(); }
    class EnvironmentData { public int BaseDataNumber = 1; }
    BarData Bars = new BarData(); EnvironmentData ExecInfo = new EnvironmentData();
    // PRODUCTION_METHODS
    static int checks;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    static void Main() {
        DateTime day = new DateTime(2026, 10, 14, 9, 0, 0);
        var chart = new TradeControlClickTests();
        var line = new ITrendLineObject {
            Begin = new ChartPoint {Time = day.AddSeconds(4), Price = 100},
            End = new ChartPoint {Time = day.AddSeconds(10), Price = 100}
        };
        var click = new MouseClickArgs {point = new ChartPoint {Time = day.AddSeconds(7), Price = 100}, bar_number = 7};
        foreach (double tick in new[] {.25, .5, 1.0}) {
            foreach (double offset in new[] {-5.0, -1.0, -.26, -.25, 0, .25, .26, 1.0, 5.0}) {
                click.point.Price = 100 + offset * tick;
                Check(chart.IsTradeControlLineHit(click, line, 100, tick) == (Math.Abs(offset) <= .25),
                    "only nearly-on-line prices activate control");
            }
        }
        click.point.Price = 100;
        click.point.Time = day.AddSeconds(3); click.bar_number = 3;
        Check(!chart.IsTradeControlLineHit(click, line, 100, .25), "same-price click left of short line cannot activate");
        click.point.Time = day.AddSeconds(11); click.bar_number = 11;
        Check(!chart.IsTradeControlLineHit(click, line, 100, .25), "empty space right of line cannot activate");
        click.point.Time = line.End.Time; click.bar_number = 11;
        Check(!chart.IsTradeControlLineHit(click, line, 100, .25), "same-timestamp bar outside segment cannot activate");
        click.bar_number = 10;
        Check(chart.IsTradeControlLineHit(click, line, 100, .25), "right endpoint remains clickable");
        click.point.Time = line.Begin.Time; click.bar_number = 4;
        Check(chart.IsTradeControlLineHit(click, line, 100, .25), "left endpoint remains clickable");
        click.data_number = 2;
        Check(!chart.IsTradeControlLineHit(click, line, 100, .25), "other data stream cannot activate control");
        chart.ExecInfo.BaseDataNumber = 2;
        Check(chart.IsTradeControlLineHit(click, line, 100, .25), "uses actual base data stream rather than assuming Data1");
        Check(!chart.IsTradeControlLineHit(click, null, 100, .25), "invisible control cannot activate");
        Check(!chart.IsTradeControlLineHit(click, line, 99, .25), "stale drawing at another stop price cannot activate");
        Check(!chart.IsTradeControlLineHit(click, line, 100, 0), "invalid tick size cannot activate");
        Console.WriteLine("PASS: " + checks + " trade-control click assertions");
    }
}
