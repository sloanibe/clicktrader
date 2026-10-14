using System;
using System.Collections.Generic;

class NamedEntryOrderTests {
    enum EEntrySetup { None, PinBar, ShiftProjection, Ema24Bounce, Ema8Bounce }
    const int OrderQuantity = 1;
    EEntrySetup m_ActiveEntrySetup;
    bool IsAskChart = true;
    double m_StopPrice = 100.25;
    class Order {
        public string Name;
        public EOrderAction Action;
        public int Sends;
        public double Price;
        public void Send(double price, int quantity) { Sends++; Price = price; }
    }
    enum EOrderAction { Buy, SellShort }
    static class Contracts { public static readonly object Default = new object(); }
    class SOrderParameters {
        public string Name; public EOrderAction Action;
        public SOrderParameters(object lots, string name, EOrderAction action) { Name = name; Action = action; }
    }
    class Creator {
        public readonly HashSet<string> Names = new HashSet<string>();
        public Order Stop(SOrderParameters parameters) {
            if (!Names.Add(parameters.Name)) throw new Exception("duplicate order name");
            return new Order {Name = parameters.Name, Action = parameters.Action};
        }
    }
    Creator OrderCreator = new Creator();
    Order m_BuyStop = new Order {Name = "RangeBuy", Action = EOrderAction.Buy};
    Order m_SellStop = new Order {Name = "RangeSell", Action = EOrderAction.SellShort};
    Order m_PinBuyStop, m_PinSellStop, m_ManualBuyStop, m_ManualSellStop;
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

    void CaptureTradeHistoryIntent(int direction) { }

    // PRODUCTION_METHODS

    static int checks;
    static void Check(bool condition, string name) {
        checks++; if (!condition) throw new Exception(name);
    }
    static void Main() {
        foreach (int side in new[] {1, -1}) foreach (EEntrySetup setup in Enum.GetValues(typeof(EEntrySetup))) {
            var signal = new NamedEntryOrderTests { m_ActiveEntrySetup = setup };
            signal.CreateNamedEntryOrders(); // Created while the constructor default is ASK.
            signal.IsAskChart = side > 0; // Saved BID setting arrives later; order actions must still be correct.
            signal.SendEntryStop(side);
            Order selected = setup == EEntrySetup.PinBar ? (side > 0 ? signal.m_PinBuyStop : signal.m_PinSellStop)
                : setup == EEntrySetup.ShiftProjection ? (side > 0 ? signal.m_ManualBuyStop : signal.m_ManualSellStop)
                : side > 0 ? signal.m_BuyStop : signal.m_SellStop;
            Check(selected.Action == (side > 0 ? EOrderAction.Buy : EOrderAction.SellShort), "submitted action agrees with saved chart role");
            Check(signal.OrderCreator.Names.Count == 4, "all named entry objects have distinct names");
            Check(selected.Sends == 1 && selected.Price == 100.25, "entry routes through correct named order");
            Check(signal.m_PinBuyStop.Sends + signal.m_PinSellStop.Sends +
                signal.m_ManualBuyStop.Sends + signal.m_ManualSellStop.Sends +
                signal.m_BuyStop.Sends + signal.m_SellStop.Sends == 1, "only selected entry is sent");
            signal.SendEntryStop(-side);
            Check(selected.Sends == 1, "wrong chart direction sends no entry");
        }
        var switchRole = new NamedEntryOrderTests {m_ActiveEntrySetup = EEntrySetup.ShiftProjection};
        switchRole.CreateNamedEntryOrders();
        switchRole.SendEntryStop(1); switchRole.IsAskChart = false; switchRole.SendEntryStop(-1);
        Check(switchRole.m_ManualBuyStop.Sends == 1 && switchRole.m_ManualSellStop.Sends == 1,
            "changing chart role reuses correctly directed objects without reconstruction");
        var cancellation = new NamedEntryOrderTests();
        var names = new[] {"pin bar", "manual", "RangeBuy", "RangeSell", "pin bar buy", "pin bar sell", "manual buy", "manual sell", "ProtectLong", "ProfitLong", "Other"};
        var items = new List<TrackerOrder>();
        for (int i = 0; i < names.Length; i++) items.Add(new TrackerOrder {Name = names[i], OrderID = i});
        items.Add(new TrackerOrder {Name = "pin bar", OrderID = 100, State = 3});
        items.Add(new TrackerOrder {Name = "manual", OrderID = 101, StrategyName = "AnotherStrategy"});
        cancellation.TradeManager.TradingData.Orders.Items = items.ToArray();
        cancellation.CancelWorkingEntryOrders();
        var cancelled = cancellation.TradeManager.TradingProfiles[0].Cancelled;
        Check(cancelled.Count == 8 && cancelled.Contains(0) && cancelled.Contains(1) &&
              cancelled.Contains(2) && cancelled.Contains(3) && cancelled.Contains(4) && cancelled.Contains(5) &&
              cancelled.Contains(6) && cancelled.Contains(7), "new and legacy working entries cancel");
        Check(!cancelled.Contains(8) && !cancelled.Contains(9) && !cancelled.Contains(100) && !cancelled.Contains(101),
              "protective exits, filled orders and other strategies are preserved");
        foreach (string name in new[] {"pin bar", "manual", "pin bar sell", "manual sell", "pin bar buy", "manual buy", "ColorTrailShort", "ProfitLong"})
            Check(cancellation.IsThisStrategyOrder("NamedEntryOrderTests", name), "emergency handling recognizes own named orders");
        Console.WriteLine("PASS: " + checks + " named-entry routing/cancellation assertions");
    }
}
