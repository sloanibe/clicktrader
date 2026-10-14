using System;
using System.Collections.Generic;

class NamedEntryOrderTests {
    enum EEntrySetup { None, PinBar, ShiftProjection, Ema24Bounce, Ema8Bounce }
    const int OrderQuantity = 1;
    EEntrySetup m_ActiveEntrySetup;
    bool ask = true;
    double m_StopPrice = 100.25;
    class Order {
        public int Sends;
        public double Price;
        public void Send(double price, int quantity) { Sends++; Price = price; }
    }
    Order m_BuyStop = new Order(), m_SellStop = new Order();
    Order m_PinEntryStop = new Order(), m_ManualEntryStop = new Order();
    bool IsEntryDirectionAllowed(int side) { return side == (ask ? 1 : -1); }
    class TrackerOrder {
        public string StrategyName = "NamedEntryOrderTests", Name, Profile = "Paper";
        public int State = 1, OrderID;
    }
    class Orders { public TrackerOrder[] Items; }
    class Data { public Orders Orders = new Orders(); }
    class Profile {
        public string Name = "Paper";
        public List<int> Cancelled = new List<int>();
        public void CancelOrder(int id) { Cancelled.Add(id); }
    }
    class Manager {
        public Data TradingData = new Data();
        public Profile[] TradingProfiles = {new Profile()};
        public void ProcessEvents() { }
    }
    class Logger { public void WriteLine(string text) { throw new Exception(text); } }
    Manager TradeManager = new Manager();
    Logger Output = new Logger();

    // PRODUCTION_METHODS

    static int checks;
    static void Check(bool condition, string name) {
        checks++; if (!condition) throw new Exception(name);
    }
    static void Main() {
        foreach (int side in new[] {1, -1}) foreach (EEntrySetup setup in Enum.GetValues(typeof(EEntrySetup))) {
            var signal = new NamedEntryOrderTests { ask = side > 0, m_ActiveEntrySetup = setup };
            signal.SendEntryStop(side);
            Order selected = setup == EEntrySetup.PinBar ? signal.m_PinEntryStop
                : setup == EEntrySetup.ShiftProjection ? signal.m_ManualEntryStop
                : side > 0 ? signal.m_BuyStop : signal.m_SellStop;
            Check(selected.Sends == 1 && selected.Price == 100.25, "entry routes through correct named order");
            Check(signal.m_PinEntryStop.Sends + signal.m_ManualEntryStop.Sends +
                signal.m_BuyStop.Sends + signal.m_SellStop.Sends == 1, "only selected entry is sent");
            signal.SendEntryStop(-side);
            Check(selected.Sends == 1, "wrong chart direction sends no entry");
        }
        var cancellation = new NamedEntryOrderTests();
        var names = new[] {"pin bar", "manual", "RangeBuy", "RangeSell", "ProtectLong", "ProfitLong", "Other"};
        var items = new List<TrackerOrder>();
        for (int i = 0; i < names.Length; i++) items.Add(new TrackerOrder {Name = names[i], OrderID = i});
        items.Add(new TrackerOrder {Name = "pin bar", OrderID = 100, State = 3});
        items.Add(new TrackerOrder {Name = "manual", OrderID = 101, StrategyName = "AnotherStrategy"});
        cancellation.TradeManager.TradingData.Orders.Items = items.ToArray();
        cancellation.CancelWorkingEntryOrders();
        var cancelled = cancellation.TradeManager.TradingProfiles[0].Cancelled;
        Check(cancelled.Count == 4 && cancelled.Contains(0) && cancelled.Contains(1) &&
              cancelled.Contains(2) && cancelled.Contains(3), "new and legacy working entries cancel");
        Check(!cancelled.Contains(4) && !cancelled.Contains(5) && !cancelled.Contains(100) && !cancelled.Contains(101),
              "protective exits, filled orders and other strategies are preserved");
        foreach (string name in new[] {"pin bar", "manual", "ColorTrailShort", "ProfitLong"})
            Check(cancellation.IsThisStrategyOrder("NamedEntryOrderTests", name), "emergency handling recognizes own named orders");
        Console.WriteLine("PASS: " + checks + " named-entry routing/cancellation assertions");
    }
}
