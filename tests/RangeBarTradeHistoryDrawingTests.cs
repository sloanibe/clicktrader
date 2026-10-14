using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage;
using PowerLanguage.Indicator;

// Exercises the production custom drawer with real SDK structs and GDI+.
class RangeBarTradeHistoryDrawingTests {
    static bool ConnectorHasColor(Bitmap bitmap, int x, int y, bool profitable) {
        // GDI+ antialiasing blends sloped lines with the background.
        for (int dx = -2; dx <= 2; dx++) for (int dy = -2; dy <= 2; dy++) {
            Color pixel = bitmap.GetPixel(x + dx, y + dy);
            if (profitable ? pixel.G > pixel.R + 40 && pixel.G > pixel.B + 40
                           : pixel.R > pixel.G + 40 && pixel.R > pixel.B + 40) return true;
        }
        return false;
    }
    class DrawBars : IDrawSymbolBars {
        public Bar[] Data;
        public bool LookupPreviousBar;
        public uint Count { get { return (uint)Data.Length; } }
        public uint PureCount { get { return Count; } }
        public Bar this[uint index] { get { return Data[index]; } }
        public Bar[] All { get { return Data; } }
        public void Get(DateTime from, DateTime to, out Bar[] bars) { bars = Data; }
        public void Get(uint from, out Bar[] bars) { Get(from, Count, out bars); }
        public void Get(uint from, uint to, out Bar[] bars) {
            int length = (int)Math.Max(0, (long)Math.Min(to, Count) - from);
            bars = new Bar[length];
            Array.Copy(Data, (int)from, bars, 0, length);
        }
        public uint GetIndexByTime(DateTime time) {
            uint result = 0;
            for (uint i = 0; i < Count; i++) if (Data[i].Time <= time) result = i;
            return LookupPreviousBar && result > 0 ? result - 1 : result;
        }
    }
    class DrawEnvironment : IDrawDataEnviroment {
        public DrawBars Data;
        public int Spacing = 150, CloseDistance = 10;
        public double PriceAnchor = 100;
        public IDrawSymbolBars Bars { get { return Data; } }
        public IStatusLine StatusLine { get { return null; } }
        public int ChartBarCloseDistance { get { return CloseDistance; } }
        public int BarIndex2PointX(uint index) { return 500 + (int)index * Spacing; }
        public uint PointX2BarIndex(int x) { return (uint)Math.Max(0, (x - 500) / Spacing); }
        public ChartPoint Point2ChartPoint(PointF p) { return new ChartPoint(Data[PointX2BarIndex((int)p.X)].Time, 100); }
        public PointF ChartPoint2Point(ChartPoint p) {
            // Native timestamp drawings snap to bar centers in this fixture.
            return new PointF(BarIndex2PointX(Data.GetIndexByTime(p.Time)), 220 - (float)(p.Price - PriceAnchor) * 20);
        }
    }
    static void Main(string[] args) {
        var day = new DateTime(2026, 10, 14, 9, 0, 0);
        var environment = new DrawEnvironment { Data = new DrawBars { Data = new[] {
            new Bar { Time = day, Open = 103, High = 103, Low = 99, Close = 99 },
            new Bar { Time = day.AddSeconds(10), Open = 99, High = 103, Low = 99, Close = 103 },
            new Bar { Time = day.AddSeconds(20), Open = 103, High = 103, Low = 99, Close = 99 }
        } } };
        var entry = new HistoryExecution { Key = "1", Scope = "A", OrderId = 1, Side = -1, Quantity = 1,
                                           Time = day.AddSeconds(2.5), Price = 102, Name = "pin bar" };
        var exit = new HistoryExecution { Key = "2", Scope = "A", OrderId = 2, Side = 1, Quantity = 1,
                                          Time = day.AddSeconds(7.5), Price = 100 };
        var frame = new HistoryFrame {
            View = HistoryMatching.Build(new List<HistoryExecution> {entry, exit}, day.Date, new Dictionary<string, int>()),
            Status = "RangeBar-TradeHistory | renderer fixture\nTracker order average / last-update time | FIFO pairing",
            ShowLabels = false, ShowConnections = true, ArrowLength = 22, FontSize = 9,
            CandleEdgeOffset = 10, CandleBodyWidthPercent = 70, ChartNow = day.AddSeconds(30)
        };
        var drawer = new HistoryDrawer(); drawer.Publish(frame);
        using (var bitmap = new Bitmap(1100, 380))
        using (var graphics = Graphics.FromImage(bitmap)) {
            graphics.Clear(Color.White);
            // Borrow a bitmap Graphics without constructing native chart HDCs.
            // DrawContext's production fields are readonly; populate this test
            // instance through reflection and never call its native Free().
            var context = (DrawContext)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(DrawContext));
            typeof(DrawContext).GetField("graphics").SetValue(context, graphics);
            typeof(DrawContext).GetField("Environment").SetValue(context, environment);
            typeof(DrawContext).GetField("DrawRect").SetValue(context, new RectangleF(0, 0, 1100, 380));
            typeof(DrawContext).GetField("FullRect").SetValue(context, new RectangleF(0, 0, 1100, 380));
            typeof(DrawContext).GetField("BackGround").SetValue(context, Color.White);
            drawer.Draw(context, EDrawPhases.Final);
            if (bitmap.GetPixel(488, 180).ToArgb() != Color.Black.ToArgb())
                throw new Exception("entry arrowhead must touch left candle edge at reported price");
            if (bitmap.GetPixel(512, 220).ToArgb() != Color.Black.ToArgb())
                throw new Exception("exit arrowhead must touch right candle edge at reported price");
            if (bitmap.GetPixel(500, 180).ToArgb() != Color.White.ToArgb())
                throw new Exception("entry must remain outside candle center");
            if (bitmap.GetPixel(480, 180).ToArgb() != Color.Black.ToArgb() ||
                bitmap.GetPixel(520, 220).ToArgb() != Color.Black.ToArgb())
                throw new Exception("entry arrow must point right and exit arrow left, including short trades");
            if (bitmap.GetPixel(467, 180).ToArgb() != Color.White.ToArgb() ||
                bitmap.GetPixel(533, 220).ToArgb() != Color.White.ToArgb())
                throw new Exception("horizontal arrowheads must not have shafts");
            if (bitmap.GetPixel(462, 180).ToArgb() != Color.White.ToArgb() ||
                bitmap.GetPixel(537, 220).ToArgb() != Color.White.ToArgb())
                throw new Exception("markers must use candle edges instead of intrabar time displacement");
            environment.Spacing = 14; environment.CloseDistance = 100; frame.CandleEdgeOffset = 0;
            graphics.Clear(Color.White); drawer.Publish(frame); drawer.Draw(context, EDrawPhases.Final);
            if (bitmap.GetPixel(493, 180).ToArgb() != Color.Black.ToArgb() ||
                bitmap.GetPixel(507, 220).ToArgb() != Color.Black.ToArgb())
                throw new Exception("automatic candle width must follow center spacing, not close-tick extent");
            environment.Spacing = 150; environment.CloseDistance = 10; frame.CandleEdgeOffset = 10;
            graphics.Clear(Color.White); drawer.Publish(frame); drawer.Draw(context, EDrawPhases.Final);
            if (!ConnectorHasColor(bitmap, 500, 200, true))
                throw new Exception("profitable short connector must be green");
            exit.Price = 104;
            frame.View = HistoryMatching.Build(new List<HistoryExecution> {entry, exit}, day.Date, new Dictionary<string, int>());
            graphics.Clear(Color.White); drawer.Publish(frame); drawer.Draw(context, EDrawPhases.Final);
            if (!ConnectorHasColor(bitmap, 500, 160, false))
                throw new Exception("losing short connector must be red");
            exit.Price = 102;
            frame.View = HistoryMatching.Build(new List<HistoryExecution> {entry, exit}, day.Date, new Dictionary<string, int>());
            graphics.Clear(Color.White); drawer.Publish(frame); drawer.Draw(context, EDrawPhases.Final);
            if (!ConnectorHasColor(bitmap, 500, 180, true))
                throw new Exception("break-even connector must be green");
            frame.ShowEntryBarArrows = true;
            foreach (int side in new[] {1, -1}) foreach (bool hasTail in new[] {false, true}) {
                entry.Side = side; exit.Side = -side;
                environment.Data.Data[0] = new Bar {
                    Time = day, High = 103, Low = 99,
                    Open = hasTail ? 101 : side > 0 ? 99 : 103,
                    Close = side > 0 ? 103 : 99
                };
                frame.View = HistoryMatching.Build(new List<HistoryExecution> {entry, exit}, day.Date, new Dictionary<string, int>());
                graphics.Clear(Color.White); drawer.Publish(frame); drawer.Draw(context, EDrawPhases.Final);
                int tipY = side > 0 ? 240 : 160;
                int shaftY = tipY + side * 10;
                if (bitmap.GetPixel(500, tipY).ToArgb() != Color.Black.ToArgb() ||
                    bitmap.GetPixel(500, shaftY).ToArgb() != Color.Black.ToArgb())
                    throw new Exception("entry-bar arrow must point up at buy low or down at sell high, with or without tail");
            }
            frame.View = HistoryMatching.Build(new List<HistoryExecution> {exit}, day.Date,
                new Dictionary<string, int> { {exit.Scope, -exit.Side} });
            graphics.Clear(Color.White); drawer.Publish(frame); drawer.Draw(context, EDrawPhases.Final);
            if (bitmap.GetPixel(500, 160).ToArgb() != Color.White.ToArgb() ||
                bitmap.GetPixel(500, 240).ToArgb() != Color.White.ToArgb())
                throw new Exception("exit-only order must not get an entry-bar extreme arrow");
            // Regression: a PB1 has one body tick and a three-tick lower tail.
            // A fill between the previous close and this close must mark this
            // pin bar, not the previous bar selected by opening-time mode.
            Bar[] originalBars = environment.Data.Data;
            environment.Data.Data = new[] {
                new Bar {Time = day, Open = 100, High = 100.25, Low = 99.25, Close = 99.75, State = EBarState.Close},
                new Bar {Time = day.AddSeconds(10), Open = 99.75, High = 100, Low = 99, Close = 100, State = EBarState.Close},
                new Bar {Time = day.AddSeconds(20), Open = 100, High = 101, Low = 100, Close = 101, State = EBarState.Close}
            };
            entry.Side = 1; entry.Price = 100; entry.Time = day.AddSeconds(5);
            exit.Side = -1; exit.Price = 100.5; exit.Time = day.AddSeconds(15);
            frame.BarTimestampIsClose = true;
            environment.Data.LookupPreviousBar = true;
            frame.ShowLabels = false;
            frame.View = HistoryMatching.Build(new List<HistoryExecution> {entry, exit}, day.Date, new Dictionary<string, int>());
            graphics.Clear(Color.White); drawer.Publish(frame); drawer.Draw(context, EDrawPhases.Final);
            if (bitmap.GetPixel(650, 240).ToArgb() != Color.Black.ToArgb() ||
                bitmap.GetPixel(650, 250).ToArgb() != Color.Black.ToArgb())
                throw new Exception("PB1 upward arrow must be at actual pin bar low");
            if (bitmap.GetPixel(638, 220).ToArgb() != Color.Black.ToArgb())
                throw new Exception("PB1 horizontal entry must share actual pin bar assignment");
            if (bitmap.GetPixel(500, 235).ToArgb() != Color.White.ToArgb() ||
                bitmap.GetPixel(488, 220).ToArgb() != Color.White.ToArgb())
                throw new Exception("previous bar must not carry PB1 entry markers");
            // A doji/full-tail pin and a larger-body manual entry must also
            // stay on their selected native index when time lookup is earlier.
            foreach (bool manualEntry in new[] {false, true}) {
                environment.Data.Data[1].Open = manualEntry ? 99.25 : 100;
                entry.Name = manualEntry ? "manual" : "pin bar";
                frame.View = HistoryMatching.Build(new List<HistoryExecution> {entry}, day.Date, new Dictionary<string, int>());
                graphics.Clear(Color.White); drawer.Publish(frame); drawer.Draw(context, EDrawPhases.Final);
                if (bitmap.GetPixel(650, 240).ToArgb() != Color.Black.ToArgb() ||
                    bitmap.GetPixel(500, 235).ToArgb() != Color.White.ToArgb())
                    throw new Exception("entry-bar assignment must apply to every tail and entry type");
            }
            // A fresh live fill remains on the forming bar until completion.
            environment.Data.Data[2].State = EBarState.Open;
            entry.Time = day.AddSeconds(22); entry.Price = 100.75;
            frame.View = HistoryMatching.Build(new List<HistoryExecution> {entry}, day.Date, new Dictionary<string, int>());
            graphics.Clear(Color.White); drawer.Publish(frame); drawer.Draw(context, EDrawPhases.Final);
            if (bitmap.GetPixel(800, 220).ToArgb() != Color.Black.ToArgb())
                throw new Exception("current forming-bar entry must not disappear while awaiting close time");
            // User's recorded first-trade price/time, paired with SYNTHETIC
            // neighboring bars; actual chart OHLC/times were not supplied.
            DateTime recordedMinute = day.Date.AddHours(6).AddMinutes(31);
            environment.Data.Data = new[] {
                new Bar {Time = recordedMinute.AddSeconds(26), Open = 7823.75, High = 7824.75, Low = 7823.75, Close = 7824.75},
                new Bar {Time = recordedMinute.AddSeconds(28), Open = 7825, High = 7825.25, Low = 7824.25, Close = 7825.25},
                new Bar {Time = recordedMinute.AddSeconds(30), Open = 7825.25, High = 7826.25, Low = 7825.25, Close = 7826.25}
            };
            environment.PriceAnchor = 7825;
            entry.Side = 1; entry.Price = 7825.25;
            entry.Time = recordedMinute.AddSeconds(27).AddMilliseconds(94);
            frame.BarTimestampIsClose = false;
            frame.View = HistoryMatching.Build(new List<HistoryExecution> {entry}, day.Date, new Dictionary<string, int>());
            graphics.Clear(Color.White); drawer.Publish(frame); drawer.Draw(context, EDrawPhases.Final);
            if (bitmap.GetPixel(650, 235).ToArgb() != Color.Black.ToArgb() ||
                bitmap.GetPixel(500, 245).ToArgb() != Color.White.ToArgb())
                throw new Exception("vertical marker must use unique neighbor containing the executed price");
            if (bitmap.GetPixel(488, 215).ToArgb() != Color.Black.ToArgb())
                throw new Exception("vertical correction must preserve horizontal marker position");
            var resolve = typeof(HistoryDrawer).GetMethod("ResolveVerticalEntryBar",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var ambiguousBars = new[] {
                new Bar {Low = 100, High = 101}, new Bar {Low = 98, High = 99}, new Bar {Low = 100, High = 101}
            };
            if ((int)resolve.Invoke(null, new object[] {ambiguousBars, 1, 100.5}) != 1)
                throw new Exception("vertical locator must not guess between two matching neighbors");
            if ((int)resolve.Invoke(null, new object[] {ambiguousBars, 1, 98.5}) != 1)
                throw new Exception("valid time-selected bar must be preserved");
            environment.PriceAnchor = 100;
            environment.Data.Data = originalBars;
            environment.Data.LookupPreviousBar = false;
            frame.BarTimestampIsClose = false;
            entry.Name = "pin bar";
            entry.Price = 102; entry.Time = day.AddSeconds(2.5); exit.Time = day.AddSeconds(7.5);
            entry.Side = -1; exit.Side = 1;
            exit.Price = 100;
            frame.View = HistoryMatching.Build(new List<HistoryExecution> {entry, exit}, day.Date, new Dictionary<string, int>());
            graphics.Clear(Color.White);
            frame.ShowLabels = true; drawer.Publish(frame);
            drawer.Draw(context, EDrawPhases.Final);
            int top = 380, bottom = -1, right = -1;
            for (int x = 390; x <= 475; x++) for (int y = 160; y <= 198; y++) {
                if (bitmap.GetPixel(x, y).ToArgb() != Color.Black.ToArgb()) continue;
                top = Math.Min(top, y); bottom = Math.Max(bottom, y); right = Math.Max(right, x);
            }
            if (bottom < 0 || Math.Abs((top + bottom) / 2.0 - 180) > 4 || right < 465)
                throw new Exception("entry label must be vertically centered and immediately left of arrowhead");
            // Preview includes a synthetic candle so edge contact is visible.
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Gainsboro, 490, 160, 20, 80);
            graphics.DrawRectangle(Pens.Gray, 490, 160, 20, 80);
            drawer.Draw(context, EDrawPhases.Final);
            if (args.Length > 0) bitmap.Save(args[0], System.Drawing.Imaging.ImageFormat.Png);
        }
        Console.WriteLine("PASS: production drawer verifies candle-edge arrowheads, centered labels, result colors and entry-bar extremes");
    }
}
