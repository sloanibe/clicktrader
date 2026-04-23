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

        // ─── EMERGENCY FLATTEN FLAG ───────────────────────────────────────────────
        // Set by Shift-Click. Bypasses ALL state machine logic until position is flat.
        private bool m_FlattenRequested = false;

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
        private ITextObject      m_EntryLabel;
        private ITextObject      m_ProtectLabel;
        private ITextObject      m_TargetLabel;

        // ─── EMA ──────────────────────────────────────────────────────────────────
        private double[] m_EMAArray;
        private const int MaxBars = 1000000; // Increased to 1M bars for Renko safety

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

            // ─── CRITICAL FLATTEN ARCHITECTURE ───────────────────────────────────────
            // 1. Do NOT use OrderExit.FromAll. It causes silent order failures if not strictly tied to an entry name.
            // 2. Do NOT use MarketThisBar. It waits for the Renko brick to close. MarketNextBar fires instantly on the next IOG tick.
            m_CloseLong  = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "Tail_FlatL", EOrderAction.Sell));
            m_CloseShort = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "Tail_FlatS", EOrderAction.BuyToCover));
        }

        private double m_Alpha;

        protected override void StartCalc()
        {
            m_Brain.ProtectiveStopTicks = ProtectiveStopTicks;
            m_Brain.TickSize            = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            m_Alpha                     = 2.0 / (MAPeriod + 1.0);

            m_StalkLine   = null;
            m_StatusLabel = null;
        }

        // ─── CALC BAR ─────────────────────────────────────────────────────────────
        protected override void CalcBar()
        {
            int idx = Bars.CurrentBar - 1;
            if (idx >= MaxBars) return;

            // ══════════════════════════════════════════════════════════════════════
            // RULE #1 — NUCLEAR FLATTEN (highest priority, overrides everything)
            // If Shift-Click was pressed, hammer close orders every tick on EVERY bar
            // until the platform confirms the position is fully flat.
            // ══════════════════════════════════════════════════════════════════════
            bool isLong  = CurrentPosition.Side == EMarketPositionSide.Long;
            bool isShort = CurrentPosition.Side == EMarketPositionSide.Short;

            if (m_FlattenRequested)
            {
                // CRITICAL: Do not wrap these orders in history checks. The strategy must 
                // execute entirely in live mode on the next incoming IOG data tick.
                if (Bars.LastBarOnChart)
                {
                    // Live Mode Execution
                    if (isLong)  m_CloseLong.Send();
                    if (isShort) m_CloseShort.Send();

                    if (!isLong && !isShort) 
                    {
                        m_FlattenRequested = false;
                        m_Brain.OnShiftClick(); 
                        RefreshHUD(EStrategyState.Inactive);
                        UpdateTradeLevelDrawings(EStrategyState.Inactive);
                    }
                    else
                    {
                        UpdateTradeLevelDrawings(m_Brain.State);
                    }
                }
                return; // Bypass all other logic until flat
            }

            if (idx == 0) m_EMAArray[0] = Bars.Close[0];
            else m_EMAArray[idx] = (Bars.Close[0] - m_EMAArray[idx - 1]) * m_Alpha + m_EMAArray[idx - 1];

            var data = new BarData();
            data.BarIndex  = Bars.CurrentBar;
            data.Open      = Bars.Open[0];
            data.High      = Bars.High[0];
            data.Low       = Bars.Low[0];
            data.Close     = Bars.Close[0];
            data.PrevOpen  = Bars.Open[1];
            data.PrevClose = Bars.Close[1];
            data.Status    = (Bars.Status == EBarState.Close) ? EBarStatus.Close : EBarStatus.Open;

            var result = m_Brain.ProcessBar(data, isLong, isShort); 

            if (result.State != m_LastState || !string.IsNullOrEmpty(result.TransitionLog))
            {
#if DEBUG
                string logStr = string.IsNullOrEmpty(result.TransitionLog) ? "VIA_MOUSE" : result.TransitionLog;
                Output.WriteLine("[RenkoState] {0} -> {1} ({2}) at Bar {3}", m_LastState, result.State, logStr, Bars.CurrentBar);
#endif
                m_LastState = result.State;
            }



            // ── ALL OTHER ORDERS: only on the live bar ───────────────────────────
            // This prevents historical replay from placing real orders.
            if (Bars.LastBarOnChart)
            {
                foreach (var cmd in result.Orders)
                {
                    switch (cmd.Type)
                    {
                        case EOrderType.BuyStop:           
#if DEBUG
                            Output.WriteLine("[Order] BuyStop @ {0}", cmd.Price); 
#endif
                            m_BuyStopOrder.Send(cmd.Price); break;
                        case EOrderType.SellStop:          
#if DEBUG
                            Output.WriteLine("[Order] SellStop @ {0}", cmd.Price); 
#endif
                            m_SellStopOrder.Send(cmd.Price); break;
                        case EOrderType.LongProtectStop:   m_LongProtectStop.Send(cmd.Price); break;
                        case EOrderType.LongTargetLimit:   m_LongTargetLimit.Send(cmd.Price); break;
                        case EOrderType.ShortProtectStop:  m_ShortProtectStop.Send(cmd.Price); break;
                        case EOrderType.ShortTargetLimit:  m_ShortTargetLimit.Send(cmd.Price); break;
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
            // Anchor 3 bars back from the current forming bar
            DateTime rightEnd = Bars.Time[Math.Min(3, Bars.CurrentBar - 1)];

            int lb = Math.Min(13, Bars.CurrentBar - 1);
            DateTime leftAnchor = Bars.Time[lb];

            if (m_StalkLine == null)
            {
                m_StalkLine = DrwTrendLine.Create(new ChartPoint(leftAnchor, price), new ChartPoint(rightEnd, price));
                m_StalkLine.Color = Color.Cyan; m_StalkLine.Style = ETLStyle.ToolDashed; m_StalkLine.ExtLeft = true;
                m_StalkLine.Size = 2;
            }
            else { m_StalkLine.Begin = new ChartPoint(leftAnchor, price); m_StalkLine.End = new ChartPoint(rightEnd,   price); }

            if (m_StatusLabel == null)
            {
                m_StatusLabel = DrwText.Create(new ChartPoint(rightEnd, price), "STALKING OFF");
                m_StatusLabel.HStyle = ETextStyleH.Left; m_StatusLabel.VStyle = ETextStyleV.Above;
                m_StatusLabel.Size = 12; m_StatusLabel.Color = Color.Yellow;
            }
            // Anchor the text label at the 10-bar-back mark
            m_StatusLabel.Location = new ChartPoint(rightEnd, price);

            switch (state)
            {
                case EStrategyState.Inactive:
                    m_StatusLabel.Color = Color.White; m_StatusLabel.Text = "STALKING OFF";
                    m_StalkLine.Color = Color.SlateGray;
                    break;
                case EStrategyState.ScanningLong:
                    m_StatusLabel.Color = Color.White; m_StatusLabel.Text = "STALKING LONG";
                    m_StalkLine.Color = Color.Cyan;
                    break;
                case EStrategyState.ScanningShort:
                    m_StatusLabel.Color = Color.White; m_StatusLabel.Text = "STALKING SHORT";
                    m_StalkLine.Color = Color.Orange;
                    break;
                case EStrategyState.ArmedLong:
                case EStrategyState.ArmedShort:
                    bool isLong = state == EStrategyState.ArmedLong;
                    m_StatusLabel.Color = Color.White;
                    m_StatusLabel.Text  = string.Format("ARMED {0}", isLong ? "LONG" : "SHORT");
                    m_StalkLine.Color = isLong ? Color.Cyan : Color.Orange;
                    break;
                case EStrategyState.LongActive:
                case EStrategyState.ShortActive:
                    m_StatusLabel.Color = Color.Lime; m_StatusLabel.Text = "POSITION ACTIVE";
                    m_StalkLine.Color = Color.Lime;
                    break;
            }
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            if (arg.buttons != MouseButtons.Left) return;
            bool ctrl  = (arg.keys & Keys.Control) == Keys.Control;
            bool shift = (arg.keys & Keys.Shift)   == Keys.Shift;

            if (shift) 
            { 
#if DEBUG
                Output.WriteLine("[MouseLog] SHIFT-CLICK detected. Awaiting live tick for Flatten.");
#endif
                // CRITICAL: Do NOT use ExecControl.Recalculate() here. 
                // Forcing a recalculation causes history-replay lag and breaks the live order sequence.
                m_FlattenRequested = true;
                return; 
            }
            if (ctrl)
            {
#if DEBUG
                Output.WriteLine("[MouseLog] CTRL-CLICK detected. Starting Scan.");
#endif
                int clickedBar = Math.Max(2, Math.Min(arg.bar_number, Bars.CurrentBar));
                int clickIdx   = Math.Max(1, Math.Min(clickedBar - 1, MaxBars - 1));
                double emaSlope = m_EMAArray[clickIdx] - m_EMAArray[clickIdx - 1];
                bool isLong = emaSlope > 0 || (emaSlope == 0 && arg.point.Price > Bars.Close[0]);
                m_Brain.OnCtrlClick(isLong, Bars.CurrentBar);
                ExecControl.Recalculate();
            }
        }

        private void ClearTradeLevelDrawings()
        {
            if (m_EntryLine    != null) { m_EntryLine.Delete();    m_EntryLine    = null; }
            if (m_ProtectLine  != null) { m_ProtectLine.Delete();  m_ProtectLine  = null; }
            if (m_TargetLine   != null) { m_TargetLine.Delete();   m_TargetLine   = null; }
            if (m_EntryLabel   != null) { m_EntryLabel.Delete();   m_EntryLabel   = null; }
            if (m_ProtectLabel != null) { m_ProtectLabel.Delete(); m_ProtectLabel = null; }
            if (m_TargetLabel  != null) { m_TargetLabel.Delete();  m_TargetLabel  = null; }
        }

        private void DrawTradeLevels(DateTime fromTime, double entryPrice, double protectPrice, double targetPrice, bool isLong)
        {
            DateTime rightEdge  = Bars.Time[0].AddMinutes(5);
            DateTime labelAnchor = Bars.Time[Math.Min(3, Bars.CurrentBar - 1)];
            Color entryColor   = isLong ? Color.DodgerBlue : Color.OrangeRed;
            Color protectColor = isLong ? Color.Salmon      : Color.Yellow;

            // ── Entry line + label ──────────────────────────────────────────────
            if (m_EntryLine == null) { m_EntryLine = DrwTrendLine.Create(new ChartPoint(fromTime, entryPrice), new ChartPoint(rightEdge, entryPrice)); m_EntryLine.Color = entryColor; m_EntryLine.Size = 2; m_EntryLine.ExtRight = true; }
            else { m_EntryLine.Begin = new ChartPoint(fromTime, entryPrice); m_EntryLine.End = new ChartPoint(rightEdge, entryPrice); m_EntryLine.Color = entryColor; }

            if (m_EntryLabel == null) { m_EntryLabel = DrwText.Create(new ChartPoint(labelAnchor, entryPrice), "ENTRY"); m_EntryLabel.HStyle = ETextStyleH.Left; m_EntryLabel.VStyle = ETextStyleV.Above; m_EntryLabel.Size = 11; m_EntryLabel.Color = Color.White; }
            else { m_EntryLabel.Location = new ChartPoint(labelAnchor, entryPrice); }

            // ── Protect (stop loss) line + label ────────────────────────────────
            if (m_ProtectLine == null) { m_ProtectLine = DrwTrendLine.Create(new ChartPoint(fromTime, protectPrice), new ChartPoint(rightEdge, protectPrice)); m_ProtectLine.Color = protectColor; m_ProtectLine.Size = 1; m_ProtectLine.Style = ETLStyle.ToolDashed; m_ProtectLine.ExtRight = true; }
            else { m_ProtectLine.Begin = new ChartPoint(fromTime, protectPrice); m_ProtectLine.End = new ChartPoint(rightEdge, protectPrice); m_ProtectLine.Color = protectColor; }

            if (m_ProtectLabel == null) { m_ProtectLabel = DrwText.Create(new ChartPoint(labelAnchor, protectPrice), "STOP"); m_ProtectLabel.HStyle = ETextStyleH.Left; m_ProtectLabel.VStyle = ETextStyleV.Above; m_ProtectLabel.Size = 11; m_ProtectLabel.Color = Color.White; }
            else { m_ProtectLabel.Location = new ChartPoint(labelAnchor, protectPrice); }

            // ── Target (take profit) line + label ───────────────────────────────
            if (m_TargetLine == null) { m_TargetLine = DrwTrendLine.Create(new ChartPoint(fromTime, targetPrice), new ChartPoint(rightEdge, targetPrice)); m_TargetLine.Color = Color.LimeGreen; m_TargetLine.Size = 2; m_TargetLine.ExtRight = true; }
            else { m_TargetLine.Begin = new ChartPoint(fromTime, targetPrice); m_TargetLine.End = new ChartPoint(rightEdge, targetPrice); }

            if (m_TargetLabel == null) { m_TargetLabel = DrwText.Create(new ChartPoint(labelAnchor, targetPrice), "PROFIT"); m_TargetLabel.HStyle = ETextStyleH.Left; m_TargetLabel.VStyle = ETextStyleV.Above; m_TargetLabel.Size = 11; m_TargetLabel.Color = Color.White; }
            else { m_TargetLabel.Location = new ChartPoint(labelAnchor, targetPrice); }
        }

        protected override void Destroy()
        {
            ClearTradeLevelDrawings();
            if (m_StalkLine   != null) m_StalkLine.Delete();
            if (m_StatusLabel != null) m_StatusLabel.Delete();
            // Labels inside ClearTradeLevelDrawings already handle the trade labels
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

        private BarResult m_Result;

        public RenkoStateMachine()
        {
            m_State = EStrategyState.Inactive;
            m_SignalBar = -1;
            m_TickSize = 0.25;
            m_ProtectiveStopTicks = 2;
            m_Result = new BarResult();
        }

        public BarResult ProcessBar(BarData bar, bool positionIsLong, bool positionIsShort)
        {
            m_Result.Orders.Clear();
            m_Result.TransitionLog = null;
            m_Result.State = m_State;
            BarResult result = m_Result;

            if (m_State == EStrategyState.Inactive) return result;

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
                    else if (bar.Status == EBarStatus.Close && IsBearishBar(bar)) { Transition(EStrategyState.Inactive, "Rejection Failed", result); ResetPrices(bar.BarIndex, true); }
                    else if (!positionIsShort) 
                    {
                        // Dynamically update stop to follow the lowest point of the tail
                        double potentialStop = bar.Low - (m_ProtectiveStopTicks * m_TickSize);
                        if (potentialStop < m_ProtectStop) 
                        {
                            m_ProtectStop = potentialStop;
                            result.TransitionLog = string.Format("Stop Trail (Long): {0:F2}", m_ProtectStop);
                        }
                        
                        result.Orders.Add(CreateOrder(EOrderType.BuyStop, m_EntryStop));
                    }
                    break;

                case EStrategyState.ArmedShort:
                    if (positionIsShort) Transition(EStrategyState.ShortActive, "Filled", result);
                    else if (bar.Status == EBarStatus.Close && IsBullishBar(bar)) { Transition(EStrategyState.Inactive, "Rejection Failed", result); ResetPrices(bar.BarIndex, true); }
                    else if (!positionIsLong) 
                    {
                        // Dynamically update stop to follow the highest point of the tail
                        double potentialStop = bar.High + (m_ProtectiveStopTicks * m_TickSize);
                        if (potentialStop > m_ProtectStop) 
                        {
                            m_ProtectStop = potentialStop;
                            result.TransitionLog = string.Format("Stop Trail (Short): {0:F2}", m_ProtectStop);
                        }
                        
                        result.Orders.Add(CreateOrder(EOrderType.SellStop, m_EntryStop));
                    }
                    break;

                case EStrategyState.LongActive:
                    if (!positionIsLong) { Transition(EStrategyState.Inactive, "Trade Concluded", result); ResetPrices(bar.BarIndex, true); }
                    else
                    {
                        result.Orders.Add(CreateOrder(EOrderType.LongProtectStop, m_ProtectStop));
                        result.Orders.Add(CreateOrder(EOrderType.LongTargetLimit, m_TargetPrice));
                    }
                    break;

                case EStrategyState.ShortActive:
                    if (!positionIsShort) { Transition(EStrategyState.Inactive, "Trade Concluded", result); ResetPrices(bar.BarIndex, true); }
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

        public void OnCtrlClick(bool isLong, int currentBar) 
        { 
            m_State = isLong ? EStrategyState.ScanningLong : EStrategyState.ScanningShort;
            // Only scan from this bar forward — ignore all historical bars
            m_SignalBar = currentBar - 1;
        }
        public void OnRightClick(bool isLong, int currentBar) 
        { 
            m_State = isLong ? EStrategyState.ScanningLong : EStrategyState.ScanningShort;
            m_SignalBar = currentBar - 1;
        }
        public void OnShiftClick() { m_State = EStrategyState.Inactive; ResetPrices(); }

        private void Transition(EStrategyState next, string log, BarResult res) 
        { 
            res.TransitionLog = string.Format("{0} -> {1} ({2})", m_State, next, log); 
            m_State = next; 
        }

        private void ResetPrices(int currentBar, bool waitOneFullBar) 
        { 
            m_EntryStop = 0; 
            m_ProtectStop = 0; 
            m_TargetPrice = 0; 
            if (waitOneFullBar) m_SignalBar = currentBar + 1; 
        } 

        private void ResetPrices() { ResetPrices(-1, false); }

        private bool IsLongHammer(BarData b) { return b.Low < b.PrevOpen; }
        private bool IsShortStar(BarData b)  { return b.High > b.PrevOpen; }
        private bool IsBearishBar(BarData b) { return b.Close < b.Open; }
        private bool IsBullishBar(BarData b) { return b.Close > b.Open; }

        private void ArmLong(BarData b) 
        { 
            m_SignalBar = b.BarIndex; 
            double brick = Math.Abs(b.PrevClose - b.PrevOpen); 
            if (brick == 0) brick = 4.0; 
            // If prev bar was counter-trend (red in uptrend), need 2 bricks to clear it
            bool prevWasCounterTrend = b.PrevClose < b.PrevOpen;
            m_EntryStop  = b.PrevClose + brick * (prevWasCounterTrend ? 2.0 : 1.0);
            m_ProtectStop = b.Low - (m_ProtectiveStopTicks * m_TickSize); 
            m_TargetPrice = m_EntryStop + brick;
        }

        private void ArmShort(BarData b) 
        { 
            m_SignalBar = b.BarIndex; 
            double brick = Math.Abs(b.PrevClose - b.PrevOpen); 
            if (brick == 0) brick = 4.0; 
            // If prev bar was counter-trend (blue in downtrend), need 2 bricks to clear it
            bool prevWasCounterTrend = b.PrevClose > b.PrevOpen;
            m_EntryStop  = b.PrevClose - brick * (prevWasCounterTrend ? 2.0 : 1.0);
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
