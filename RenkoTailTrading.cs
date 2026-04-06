using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using PowerLanguage;
using PowerLanguage.Function;
using PowerLanguage.Strategy;

namespace PowerLanguage.Strategy
{
    [IOGMode(IOGMode.Enabled)]
    [MouseEvents(true)]
    [RecoverDrawings(false)]
    public class RenkoTailTrading : SignalObject
    {
        // ─── INPUTS ───────────────────────────────────────────────────────────────
        [Input] public int MAPeriod           { get; set; }
        [Input] public int ProtectiveStopTicks { get; set; }

        // ─── STATE (The Brain) ────────────────────────────────────────────────────
        private RenkoStateMachine m_Brain;
        private EStrategyState    m_LastState = EStrategyState.Inactive;

        // ─── ORDERS ───────────────────────────────────────────────────────────────
        private IOrderPriced m_BuyStopOrder;
        private IOrderPriced m_SellStopOrder;
        private IOrderPriced m_LongProtectStop;
        private IOrderPriced m_ShortProtectStop;
        private IOrderPriced m_LongTargetLimit;
        private IOrderPriced m_ShortTargetLimit;
        private IOrderMarket m_CloseLong;
        private IOrderMarket m_CloseShort;

        // ─── DRAWINGS ─────────────────────────────────────────────────────────────
        private ITrendLineObject m_StalkLine;
        private ITextObject      m_StatusLabel;
        private ITrendLineObject m_EntryLine;
        private ITrendLineObject m_ProtectLine;
        private ITrendLineObject m_TargetLine;

        // ─── EMA ──────────────────────────────────────────────────────────────────
        private double[] m_EMAArray;
        private const int MaxBars = 100000;

        // ─── CONSTRUCTOR ──────────────────────────────────────────────────────────
        public RenkoTailTrading(object ctx) : base(ctx)
        {
            MAPeriod            = 10;
            ProtectiveStopTicks = 2;
        }

        // ─── CREATE ───────────────────────────────────────────────────────────────
        protected override void Create()
        {
            m_EMAArray = new double[MaxBars];
            m_Brain    = new RenkoStateMachine();

            m_BuyStopOrder  = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "Tail_Long",  EOrderAction.Buy));
            m_SellStopOrder = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "Tail_Short", EOrderAction.SellShort));

            m_LongProtectStop  = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "Tail_L_SL",  EOrderAction.Sell));
            m_ShortProtectStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "Tail_S_SL", EOrderAction.BuyToCover));

            m_LongTargetLimit  = OrderCreator.Limit(new SOrderParameters(Contracts.Default, "Tail_L_TP",  EOrderAction.Sell));
            m_ShortTargetLimit = OrderCreator.Limit(new SOrderParameters(Contracts.Default, "Tail_S_TP", EOrderAction.BuyToCover));

            m_CloseLong  = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "Tail_FlatL", EOrderAction.Sell, OrderExit.FromAll));
            m_CloseShort = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "Tail_FlatS", EOrderAction.BuyToCover, OrderExit.FromAll));
        }

        protected override void StartCalc()
        {
            m_Brain.ProtectiveStopTicks = ProtectiveStopTicks;
            m_Brain.TickSize            = (double)Bars.Info.MinMove / Bars.Info.PriceScale;

            m_StalkLine   = null;
            m_StatusLabel = null;
        }

        // ─── CALC BAR ─────────────────────────────────────────────────────────────
        protected override void CalcBar()
        {
            int idx = Bars.CurrentBar - 1;
            if (idx >= MaxBars) return;

            if (idx == 0) m_EMAArray[0] = Bars.Close[0];
            else
            {
                double alpha = 2.0 / (MAPeriod + 1.0);
                m_EMAArray[idx] = (Bars.Close[0] - m_EMAArray[idx - 1]) * alpha + m_EMAArray[idx - 1];
            }

            var data = new BarData();
            data.BarIndex  = Bars.CurrentBar;
            data.Open      = Bars.Open[0];
            data.High      = Bars.High[0];
            data.Low       = Bars.Low[0];
            data.Close     = Bars.Close[0];
            data.PrevOpen  = Bars.Open[1];
            data.PrevClose = Bars.Close[1];
            data.Status    = (Bars.Status == EBarState.Close) ? EBarStatus.Close : EBarStatus.Open;

            var result = m_Brain.ProcessBar(data, 
                CurrentPosition.Side == EMarketPositionSide.Long,
                CurrentPosition.Side == EMarketPositionSide.Short);

            if (result.State != m_LastState && !string.IsNullOrEmpty(result.TransitionLog))
            {
                Output.WriteLine("[RenkoState] {0}", result.TransitionLog);
                m_LastState = result.State;
            }

            // ── FLATTEN: re-send every tick until position is gone ──────────────
            // This must run even on historical bars so any orphaned position is closed.
            bool needsFlatten = result.State == EStrategyState.Inactive;
            if (needsFlatten && CurrentPosition.Side == EMarketPositionSide.Long)  m_CloseLong.Send();
            if (needsFlatten && CurrentPosition.Side == EMarketPositionSide.Short) m_CloseShort.Send();

            // ── ALL OTHER ORDERS: only on the live bar ───────────────────────────
            // This prevents historical replay from placing real orders.
            if (Bars.LastBarOnChart)
            {
                foreach (var cmd in result.Orders)
                {
                    switch (cmd.Type)
                    {
                        case EOrderType.BuyStop:           m_BuyStopOrder.Send(cmd.Price); break;
                        case EOrderType.SellStop:          m_SellStopOrder.Send(cmd.Price); break;
                        case EOrderType.LongProtectStop:   m_LongProtectStop.Send(cmd.Price); break;
                        case EOrderType.LongTargetLimit:   m_LongTargetLimit.Send(cmd.Price); break;
                        case EOrderType.ShortProtectStop:  m_ShortProtectStop.Send(cmd.Price); break;
                        case EOrderType.ShortTargetLimit:  m_ShortTargetLimit.Send(cmd.Price); break;
                        // CloseLong / CloseShort handled above, every tick
                    }
                }
            }

            UpdateTradeLevelDrawings(result.State);

            if (Bars.LastBarOnChart)
                RefreshHUD(result.State);
        }

        private void UpdateTradeLevelDrawings(EStrategyState state)
        {
            bool isArmed  = state == EStrategyState.ArmedLong || state == EStrategyState.ArmedShort;
            bool isActive = state == EStrategyState.LongActive || state == EStrategyState.ShortActive;

            if (isArmed || isActive)
            {
                bool isLong = (state == EStrategyState.ArmedLong || state == EStrategyState.LongActive);
                DrawTradeLevels(Bars.Time[0], m_Brain.EntryStop, m_Brain.ProtectStop, m_Brain.TargetPrice, isLong);
            }
            else
            {
                ClearTradeLevelDrawings();
            }
        }

        private void RefreshHUD(EStrategyState state)
        {
            if (Bars.CurrentBar < 10) return;

            double   price    = Bars.Close[0];
            DateTime rightEnd = Bars.Time[0];

            int lb = 1;
            while (lb < Bars.CurrentBar - 1 && Bars.Time[lb] >= rightEnd) lb++;
            lb = Math.Min(lb + 8, Bars.CurrentBar - 1);
            DateTime leftAnchor = Bars.Time[lb];

            if (m_StalkLine == null)
            {
                m_StalkLine = DrwTrendLine.Create(new ChartPoint(leftAnchor, price), new ChartPoint(rightEnd, price));
                m_StalkLine.Color = Color.DimGray; m_StalkLine.Style = ETLStyle.ToolDashed; m_StalkLine.ExtLeft = true;
            }
            else { m_StalkLine.Begin = new ChartPoint(leftAnchor, price); m_StalkLine.End = new ChartPoint(rightEnd,   price); }

            if (m_StatusLabel == null)
            {
                m_StatusLabel = DrwText.Create(new ChartPoint(leftAnchor, price), "STALKING OFF");
                m_StatusLabel.HStyle = ETextStyleH.Right; m_StatusLabel.VStyle = ETextStyleV.Above;
                m_StatusLabel.Size = 11; m_StatusLabel.Color = Color.DimGray;
            }
            m_StatusLabel.Location = new ChartPoint(leftAnchor, price);

            switch (state)
            {
                case EStrategyState.Inactive:
                    m_StatusLabel.Color = Color.DimGray; m_StatusLabel.Text = "STALKING OFF";
                    break;
                case EStrategyState.ScanningLong:
                    m_StatusLabel.Color = Color.DodgerBlue; m_StatusLabel.Text = "STALKING LONG";
                    break;
                case EStrategyState.ScanningShort:
                    m_StatusLabel.Color = Color.OrangeRed; m_StatusLabel.Text = "STALKING SHORT";
                    break;
                case EStrategyState.ArmedLong:
                case EStrategyState.ArmedShort:
                    bool isLong = state == EStrategyState.ArmedLong;
                    m_StatusLabel.Color = isLong ? Color.DodgerBlue : Color.OrangeRed;
                    m_StatusLabel.Text  = string.Format("ARMED {0}  |  @ {1:F2}  SL {2:F2}", isLong ? "LONG" : "SHORT", m_Brain.EntryStop, m_Brain.ProtectStop);
                    break;
                case EStrategyState.LongActive:
                case EStrategyState.ShortActive:
                    m_StatusLabel.Color = Color.LimeGreen; m_StatusLabel.Text = "POSITION ACTIVE";
                    break;
            }
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            if (arg.buttons != MouseButtons.Left && arg.buttons != MouseButtons.Right) return;
            bool ctrl  = (arg.keys & Keys.Control) == Keys.Control;
            bool shift = (arg.keys & Keys.Shift)   == Keys.Shift;

            if (shift) { m_Brain.OnShiftClick(); ExecControl.Recalculate(); return; }
            if (arg.buttons == MouseButtons.Right) { m_Brain.OnRightClick(arg.point.Price > Bars.Close[0]); ExecControl.Recalculate(); return; }
            if (ctrl)
            {
                int clickedBar = Math.Max(2, Math.Min(arg.bar_number, Bars.CurrentBar));
                int clickIdx   = Math.Max(1, Math.Min(clickedBar - 1, MaxBars - 1));
                double emaSlope = m_EMAArray[clickIdx] - m_EMAArray[clickIdx - 1];
                bool isLong = emaSlope > 0 || (emaSlope == 0 && arg.point.Price > Bars.Close[0]);
                m_Brain.OnCtrlClick(isLong);
                ExecControl.Recalculate();
            }
        }

        private void ClearTradeLevelDrawings()
        {
            if (m_EntryLine   != null) { m_EntryLine.Delete();   m_EntryLine   = null; }
            if (m_ProtectLine != null) { m_ProtectLine.Delete(); m_ProtectLine = null; }
            if (m_TargetLine  != null) { m_TargetLine.Delete();  m_TargetLine  = null; }
        }

        private void DrawTradeLevels(DateTime fromTime, double entryPrice, double protectPrice, double targetPrice, bool isLong)
        {
            DateTime rightEdge = Bars.Time[0].AddMinutes(5);
            Color entryColor   = isLong ? Color.DodgerBlue : Color.OrangeRed;
            Color protectColor = isLong ? Color.Salmon      : Color.Yellow;

            if (m_EntryLine == null) { m_EntryLine = DrwTrendLine.Create(new ChartPoint(fromTime, entryPrice), new ChartPoint(rightEdge, entryPrice)); m_EntryLine.Color = entryColor; m_EntryLine.Size = 2; m_EntryLine.ExtRight = true; }
            else { m_EntryLine.Begin = new ChartPoint(fromTime, entryPrice); m_EntryLine.End = new ChartPoint(rightEdge, entryPrice); m_EntryLine.Color = entryColor; }

            if (m_ProtectLine == null) { m_ProtectLine = DrwTrendLine.Create(new ChartPoint(fromTime, protectPrice), new ChartPoint(rightEdge, protectPrice)); m_ProtectLine.Color = protectColor; m_ProtectLine.Size = 1; m_ProtectLine.Style = ETLStyle.ToolDashed; m_ProtectLine.ExtRight = true; }
            else { m_ProtectLine.Begin = new ChartPoint(fromTime, protectPrice); m_ProtectLine.End = new ChartPoint(rightEdge, protectPrice); m_ProtectLine.Color = protectColor; }

            if (m_TargetLine == null) { m_TargetLine = DrwTrendLine.Create(new ChartPoint(fromTime, targetPrice), new ChartPoint(rightEdge, targetPrice)); m_TargetLine.Color = Color.LimeGreen; m_TargetLine.Size = 2; m_TargetLine.ExtRight = true; }
            else { m_TargetLine.Begin = new ChartPoint(fromTime, targetPrice); m_TargetLine.End = new ChartPoint(rightEdge, targetPrice); }
        }

        protected override void Destroy()
        {
            ClearTradeLevelDrawings();
            if (m_StalkLine   != null) m_StalkLine.Delete();
            if (m_StatusLabel != null) m_StatusLabel.Delete();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // THE BRAINS (State Machine)
    // ──────────────────────────────────────────────────────────────────────────

    public enum EStrategyState { Inactive, ScanningLong, ScanningShort, ArmedLong, ArmedShort, LongActive, ShortActive }
    public enum EOrderType { BuyStop, SellStop, LongProtectStop, LongTargetLimit, ShortProtectStop, ShortTargetLimit, CloseLong, CloseShort }
    public enum EBarStatus { Open, Close }

    public struct BarData
    {
        public int BarIndex;
        public double Open, High, Low, Close;
        public double PrevOpen, PrevClose;
        public EBarStatus Status;
    }

    public struct OrderInstruction { public EOrderType Type; public double Price; }

    public class BarResult
    {
        public EStrategyState State;
        public List<OrderInstruction> Orders;
        public string TransitionLog;

        public BarResult() { Orders = new List<OrderInstruction>(); }
    }

    public class RenkoStateMachine
    {
        private EStrategyState m_State;
        private double m_EntryStop;
        private double m_ProtectStop;
        private double m_TargetPrice;
        private int    m_SignalBar;
        private double m_TickSize;
        private int    m_ProtectiveStopTicks;

        public EStrategyState State { get { return m_State; } }
        public double EntryStop     { get { return m_EntryStop; } }
        public double ProtectStop   { get { return m_ProtectStop; } }
        public double TargetPrice   { get { return m_TargetPrice; } }
        public double TickSize               { get { return m_TickSize; } set { m_TickSize = value; } }
        public int    ProtectiveStopTicks    { get { return m_ProtectiveStopTicks; } set { m_ProtectiveStopTicks = value; } }

        public RenkoStateMachine()
        {
            m_State = EStrategyState.Inactive;
            m_SignalBar = -1;
            m_TickSize = 0.25;
            m_ProtectiveStopTicks = 2;
        }

        public BarResult ProcessBar(BarData bar, bool positionIsLong, bool positionIsShort)
        {
            BarResult result = new BarResult();
            result.State = m_State;

            if (m_State == EStrategyState.Inactive)
            {
                if (positionIsLong)  result.Orders.Add(CreateOrder(EOrderType.CloseLong));
                if (positionIsShort) result.Orders.Add(CreateOrder(EOrderType.CloseShort));
                return result;
            }

            switch (m_State)
            {
                case EStrategyState.ScanningLong:
                    if (bar.BarIndex > m_SignalBar && IsLongHammer(bar)) 
                    { 
                        Transition(EStrategyState.ArmedLong, "Hammer", result); 
                        ArmLong(bar); 
                        result.Orders.Add(CreateOrder(EOrderType.BuyStop, m_EntryStop)); // fire immediately
                    }
                    break;

                case EStrategyState.ScanningShort:
                    if (bar.BarIndex > m_SignalBar && IsShortStar(bar)) 
                    { 
                        Transition(EStrategyState.ArmedShort, "ShootingStar", result); 
                        ArmShort(bar); 
                        result.Orders.Add(CreateOrder(EOrderType.SellStop, m_EntryStop)); // fire immediately
                    }
                    break;

                case EStrategyState.ArmedLong:
                    if (positionIsLong) Transition(EStrategyState.LongActive, "Filled", result);
                    else if (bar.Status == EBarStatus.Close && IsBearishBar(bar)) { Transition(EStrategyState.ScanningLong, "Cancelled", result); ResetPrices(); }
                    else if (!positionIsShort) result.Orders.Add(CreateOrder(EOrderType.BuyStop, m_EntryStop));
                    break;

                case EStrategyState.ArmedShort:
                    if (positionIsShort) Transition(EStrategyState.ShortActive, "Filled", result);
                    else if (bar.Status == EBarStatus.Close && IsBullishBar(bar)) { Transition(EStrategyState.ScanningShort, "Cancelled", result); ResetPrices(); }
                    else if (!positionIsLong) result.Orders.Add(CreateOrder(EOrderType.SellStop, m_EntryStop));
                    break;

                case EStrategyState.LongActive:
                    if (!positionIsLong) { Transition(EStrategyState.ScanningLong, "Position Closed", result); ResetPrices(); }
                    else
                    {
                        result.Orders.Add(CreateOrder(EOrderType.LongProtectStop, m_ProtectStop));
                        result.Orders.Add(CreateOrder(EOrderType.LongTargetLimit, m_TargetPrice));
                    }
                    break;

                case EStrategyState.ShortActive:
                    if (!positionIsShort) { Transition(EStrategyState.ScanningShort, "Position Closed", result); ResetPrices(); }
                    else
                    {
                        result.Orders.Add(CreateOrder(EOrderType.ShortProtectStop, m_ProtectStop));
                        result.Orders.Add(CreateOrder(EOrderType.ShortTargetLimit, m_TargetPrice));
                    }
                    break;
            }

            result.State = m_State;
            return result;
        }

        public void OnCtrlClick(bool isLong) { m_State = isLong ? EStrategyState.ScanningLong : EStrategyState.ScanningShort; }
        public void OnRightClick(bool isLong) { m_State = isLong ? EStrategyState.ScanningLong : EStrategyState.ScanningShort; }
        public void OnShiftClick() { m_State = EStrategyState.Inactive; ResetPrices(); }

        private void Transition(EStrategyState next, string log, BarResult res) 
        { 
            res.TransitionLog = string.Format("{0} -> {1} ({2})", m_State, next, log); 
            m_State = next; 
        }

        private void ResetPrices() { m_EntryStop = 0; m_ProtectStop = 0; m_TargetPrice = 0; } // Keep m_SignalBar for lockout check

        private bool IsLongHammer(BarData b) { return b.Low < b.PrevOpen && b.Close >= b.Open && b.Close > b.Low; }
        private bool IsShortStar(BarData b) { return b.High > b.PrevOpen && b.Close <= b.Open && b.Close < b.High; }
        private bool IsBearishBar(BarData b) { return b.Close < b.Open; }
        private bool IsBullishBar(BarData b) { return b.Close > b.Open; }

        private void ArmLong(BarData b) 
        { 
            m_SignalBar = b.BarIndex; 
            double brick = Math.Abs(b.PrevClose - b.PrevOpen); 
            if (brick == 0) brick = 4.0; 
            m_EntryStop = b.Close + brick; 
            m_ProtectStop = b.Low - (m_ProtectiveStopTicks * m_TickSize); 
            m_TargetPrice = m_EntryStop + brick; 
        }

        private void ArmShort(BarData b) 
        { 
            m_SignalBar = b.BarIndex; 
            double brick = Math.Abs(b.PrevClose - b.PrevOpen); 
            if (brick == 0) brick = 4.0; 
            m_EntryStop = b.Close - brick; 
            m_ProtectStop = b.High + (m_ProtectiveStopTicks * m_TickSize); 
            m_TargetPrice = m_EntryStop - brick; 
        }

        private OrderInstruction CreateOrder(EOrderType type, double price) 
        { 
            OrderInstruction o = new OrderInstruction(); 
            o.Type = type; 
            o.Price = price; 
            return o; 
        }
        private OrderInstruction CreateOrder(EOrderType type) { return CreateOrder(type, 0); }
    }
}
