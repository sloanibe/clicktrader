using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using PowerLanguage;
using PowerLanguage.Function;
using System.Collections.Generic;

namespace PowerLanguage.Strategy
{
    [IOGMode(IOGMode.Enabled)]
    [MouseEvents(true)]
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    // Safety interlock: MultiCharts must have Auto Trading explicitly enabled
    // before this signal is permitted to transmit an order.
    public class RangeBarTradingV3 : SignalObject
    {
        private enum EEntrySetup { None, PinBar, Ema24Bounce, ShiftProjection }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        // Every live instance of this same signal class shares this registry.
        // Each chart contributes its existing session P&L calculation, and the
        // HUD on every chart shows the combined value.
        private static readonly object s_GlobalPnLLock = new object();
        private static readonly Dictionary<RangeBarTradingV3, double> s_GlobalPnLContributors =
            new Dictionary<RangeBarTradingV3, double>();

        // The only user-facing strategy settings.
        [Input] public int RangeSizeTicks { get; set; }
        [Input] public int ProtectiveStopLossTicks { get; set; }
        [Input] public int ProfitTargetTicks { get; set; }
        [Input] public bool AutoProtectiveStopOn1BarProfit { get; set; }
        [Input] public bool EnablePinBarTrading { get; set; }
        [Input] public bool Enable24EMABounceTrading { get; set; }

        // Fixed internal behavior; these are intentionally not exposed in the
        // Strategy Properties dialog.
        private const int OrderQuantity = 1;
        private const int EntryOffsetTicks = 1;
        private const int ProximityTicks = 5;
        private const bool ShowHUD = true;
        private const int PinBarRangeTicks = 5;
        private const int PinBarMinTailTicks = 3;
        private const int EmaBounceEntryOffsetTicks = 6;
        private const int MasterTrendPeriod = 60;
        private const int MinExpansionTicks = 25;
        private const int MinBreadth_15_60 = 5;
        private const int MinBreadth_5_15 = 4;

        private IOrderPriced m_BuyStop;
        private IOrderPriced m_SellStop;
        private IOrderPriced m_BuyExitStop;
        private IOrderPriced m_SellExitStop;
        private IOrderPriced m_BuyExitLimit;
        private IOrderPriced m_SellExitLimit;
        private IOrderMarket m_CloseLongNextBar;
        private IOrderMarket m_CloseShortNextBar;

        private XAverage m_FastEMA;
        private XAverage m_SlowEMA;
        private XAverage m_MasterEMA;

        private double m_StopPrice = 0;
        private double m_ProtectiveStopPrice = 0;
        private double m_ProfitTargetPrice = 0;
        private double m_LastSentPrice = 0;
        // Snapshot the realized strategy P&L when a position opens.  When it
        // closes, the delta is the result of that one completed trade.
        private double m_ClosedEquityAtEntry = 0;
        private bool m_AutoProtectiveStopMoved = false;
        
        private int m_LastMarketPosition = 0;

        private bool m_BuyOrderActive = false;
        private bool m_SellOrderActive = false;
        private bool m_AutoEntryArmed = false;
        private int m_ArmedDirection = 0;
        private int m_PinProjectionBar = -1;
        private int m_PinProjectionDirection = 0;
        private bool m_PinProjectionTailReached = false;
        private bool m_PinProjectionBroken = false;
        private int m_PinProjectionTailTicks = PinBarMinTailTicks;
        private bool m_PinProjectionOpenAligned = false;
        private int m_PinBarOrderBar = -1;
        private int m_EmaBounceProjectionBar = -1;
        private int m_EmaBounceProjectionDirection = 0;
        private int m_EmaBounceOrderBar = -1;
        private bool m_PinEntryCandidateValid = false;
        private int m_PinEntryCandidateDirection = 0;
        private double m_PinEntryCandidatePrice = 0;
        private bool m_EmaEntryCandidateValid = false;
        private int m_EmaEntryCandidateDirection = 0;
        private double m_EmaEntryCandidatePrice = 0;
        private bool m_ShiftProjectionActive = false;
        private int m_ShiftProjectionBar = -1;
        private EEntrySetup m_ActiveEntrySetup = EEntrySetup.None;
        private bool m_FlattenRequested = false;
        // Persistent kill mode entered by Ctrl-click while armed or in a
        // position. It suppresses every entry/exit order and flattens any open
        // or late-arriving fill until the user explicitly arms again.
        // Start locked.  A newly loaded/recalculated signal must never arm or
        // transmit an entry without a deliberate manual action by the trader.
        private bool m_KillModeActive = true;
        private bool m_StartupOrderCancellationRequested = false;
        private bool m_DraggingTarget = false;
        private bool m_DraggingStop = false;
        private double m_AutoRangeTicks = 0;
        private DateTime m_EmergencyMessageExpiresAt = DateTime.MinValue;
        private readonly List<int> m_EmergencyCancelOrderIds = new List<int>();
        private bool m_EmergencyCancellationPending = false;
        private string m_StrategyBrokerProfile = string.Empty;
        private string m_StrategyBrokerAccount = string.Empty;
        private string m_StrategyBrokerSymbol = string.Empty;
        
        private ITrendLineObject m_TargetLine;
        private ITrendLineObject m_StopLine;
        private ITrendLineObject m_ProjectedEntryLine;
        private ITextObject m_ProjectedEntryLabel;
        private ITrendLineObject m_ShiftLowerLine;
        private ITrendLineObject m_ShiftUpperLine;
        private ITextObject m_ShiftCompletionLabel;
        private ITrendLineObject m_PinBarLowerLine;
        private ITrendLineObject m_PinBarUpperLine;
        private ITextObject m_PinBarLabel;
        private ITrendLineObject m_EmaBounceLowerLine;
        private ITrendLineObject m_EmaBounceUpperLine;
        private ITextObject m_EmaBounceLabel;
        private ITrendLineObject m_GoSignalMarker;
        private ITextObject m_HUDLabel;
        private ITextObject m_BrokerStatusLabel;
        private ITextObject m_EmergencyLabel;
        // Filled-trade annotations are retained after the position closes so
        // the chart keeps a clean record of executed entries.
        private readonly List<IDrawObject> m_TradeEntryMarkers = new List<IDrawObject>();
        private IArrowObject m_ActiveTradeEntryArrow;

        public RangeBarTradingV3(object ctx) : base(ctx)
        {
            RangeSizeTicks = 5;
            ProtectiveStopLossTicks = 12;
            ProfitTargetTicks = 5;
            AutoProtectiveStopOn1BarProfit = true;
            EnablePinBarTrading = true;
            Enable24EMABounceTrading = true;
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this); m_SlowEMA = new XAverage(this); m_MasterEMA = new XAverage(this);
            m_BuyStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "RangeBuy", EOrderAction.Buy));
            m_SellStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "RangeSell", EOrderAction.SellShort));
            // Match the proven RenkoTailTrading exit-order construction. These
            // actions are only emitted while the matching position is active.
            m_BuyExitStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectLong", EOrderAction.Sell));
            m_SellExitStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectShort", EOrderAction.BuyToCover));
            m_BuyExitLimit = OrderCreator.Limit(new SOrderParameters(Contracts.Default, "ProfitLong", EOrderAction.Sell));
            m_SellExitLimit = OrderCreator.Limit(new SOrderParameters(Contracts.Default, "ProfitShort", EOrderAction.BuyToCover));
            m_CloseLongNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "EmergLong", EOrderAction.Sell));
            m_CloseShortNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "EmergShort", EOrderAction.BuyToCover));
        }

        protected override void StartCalc()
        {
            m_FastEMA.Length = 8; m_FastEMA.Price = Bars.Close;
            m_SlowEMA.Length = 24; m_SlowEMA.Price = Bars.Close;
            m_MasterEMA.Length = MasterTrendPeriod; m_MasterEMA.Price = Bars.Close;
            // Do not reset live execution state here. MultiCharts may call
            // StartCalc again during a broker/order-triggered recalculation. The
            // RenkoTail strategy preserves its state across those recalculations;
            // clearing these flags here would make an armed order disappear on
            // the next tick. Field initializers provide clean state for a newly
            // created strategy instance, and Destroy handles drawing cleanup.
        }

        private void ClearTradingDrawings() {
            if (m_HUDLabel != null) { m_HUDLabel.Delete(); m_HUDLabel = null; }
            if (m_BrokerStatusLabel != null) { m_BrokerStatusLabel.Delete(); m_BrokerStatusLabel = null; }
            if (m_TargetLine != null) { m_TargetLine.Delete(); m_TargetLine = null; }
            if (m_StopLine != null) { m_StopLine.Delete(); m_StopLine = null; }
            if (m_EmergencyLabel != null) { m_EmergencyLabel.Delete(); m_EmergencyLabel = null; }
            ClearProjectedEntryLine();
            ClearPinBarProjectionLines();
            ClearEmaBounceProjectionLines();
            if (m_GoSignalMarker != null) { m_GoSignalMarker.Delete(); m_GoSignalMarker = null; }
        }

        protected override void CalcBar()
        {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;

            if (Bars.Status == EBarState.Close || m_AutoRangeTicks <= 0) m_AutoRangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            // Do not create drawings or query the broker during historical
            // calculation.  MultiCharts can remain stuck in "Calculating" if
            // the order tracker is touched in that pass.
            if (!Environment.IsRealTimeCalc) return;

            RefreshEmergencyCancellationStatus();
            if (m_EmergencyLabel != null && DateTime.Now >= m_EmergencyMessageExpiresAt)
                ClearEmergencyIndicator();

            int currentPosition = StrategyInfo.MarketPosition;

            if (!EnablePinBarTrading) {
                m_PinProjectionBar = -1;
                m_PinProjectionDirection = 0;
                m_PinProjectionTailReached = false;
                m_PinProjectionBroken = false;
                m_PinProjectionTailTicks = PinBarMinTailTicks;
                m_PinProjectionOpenAligned = false;
                ClearPinBarProjectionLines();
                if (m_ActiveEntrySetup == EEntrySetup.PinBar && currentPosition == 0)
                    ClearPinBarEntryIfActive();
            }

            if (!Enable24EMABounceTrading) {
                ResetEmaBounceProjection();
                if (m_ActiveEntrySetup == EEntrySetup.Ema24Bounce && currentPosition == 0)
                    ClearEmaBounceEntryIfActive();
            }

            if (!EnablePinBarTrading && !Enable24EMABounceTrading &&
                currentPosition == 0) {
                m_AutoEntryArmed = false;
                m_ArmedDirection = 0;
            }

            // An EMA-bounce order belongs only to the bar that created it. If
            // that bar ended without a fill, cancel its order before assessing
            // the next bar as a completely fresh bounce candidate.
            if (m_ActiveEntrySetup == EEntrySetup.Ema24Bounce &&
                currentPosition == 0 && m_EmaBounceOrderBar != Bars.CurrentBar)
                ClearEmaBounceEntryIfActive();

            // A staged pin order is also valid only for the bar that formed its
            // tail. A new bar starts a new, independent pin-bar evaluation.
            if (m_ActiveEntrySetup == EEntrySetup.PinBar &&
                currentPosition == 0 && m_PinBarOrderBar != Bars.CurrentBar)
                ClearPinBarEntryIfActive();

            // A Shift projection belongs to the bar from which it was created.
            // An unfilled order is removed before the next bar is evaluated.
            if (m_ShiftProjectionActive && currentPosition == 0 &&
                m_ShiftProjectionBar != Bars.CurrentBar)
                ClearShiftProjectionEntry();

            // Setup projections are informational even while the strategy is
            // unarmed. An open position hides them so the trade-management
            // controls remain visually distinct.
            ResetAutomaticEntryCandidates();
            UpdatePinBarProjection(tickSize, currentPosition);
            UpdateEmaBounceProjection(tickSize, currentPosition);
            ReconcileAutomaticEntryCandidates(tickSize, currentPosition);
            ApplyProjectionDisplayPriority();
            UpdateShiftProjectionEntry(tickSize, currentPosition);

            // Highest-priority execution path, modeled after RenkoTailTrading's
            // nuclear flatten. Do not emit entry, stop-loss, or target orders
            // while kill mode is active. Re-send only the market close until the
            // platform confirms that the position is flat.
            if (m_KillModeActive) {
                m_BuyOrderActive = m_SellOrderActive = false;
                m_ActiveEntrySetup = EEntrySetup.None;
                m_EmaBounceOrderBar = -1;
                m_PinBarOrderBar = -1;
                m_ShiftProjectionActive = false;
                m_ShiftProjectionBar = -1;
                m_StopPrice = m_LastSentPrice = 0;
                m_ProtectiveStopPrice = m_ProfitTargetPrice = 0;
                bool brokerPositionAvailable;
                int brokerPosition = GetBrokerPositionForStrategy(out brokerPositionAvailable);
                int positionToFlatten = brokerPositionAvailable
                    ? brokerPosition
                    : currentPosition;
                m_FlattenRequested = positionToFlatten != 0;

                // A prior version could leave a working native order behind
                // after a chart reload.  On first real-time calculation, ask
                // the order tracker to cancel every working order owned by
                // this signal before doing anything else.
                if (!m_StartupOrderCancellationRequested) {
                    m_StartupOrderCancellationRequested = true;
                    RequestTrackerOrderCancellations();
                }

                if (Bars.LastBarOnChart) {
                    if (positionToFlatten > 0) m_CloseLongNextBar.Send();
                    else if (positionToFlatten < 0) m_CloseShortNextBar.Send();
                }

                m_LastMarketPosition = currentPosition;
                if (ShowHUD) UpdateHUD();
                return;
            }

            if (currentPosition != 0 && m_LastMarketPosition == 0) {
                double entryPrice = StrategyInfo.AvgEntryPrice != 0 ? StrategyInfo.AvgEntryPrice : Bars.Close[0];
                double stopDist = ProtectiveStopLossTicks * tickSize;
                double targetDist = ProfitTargetTicks * tickSize;
                EEntrySetup filledEntrySetup = m_ActiveEntrySetup;
                m_ClosedEquityAtEntry = StrategyInfo.ClosedEquity;

                if (currentPosition > 0) {
                    m_ProtectiveStopPrice = ProtectiveStopLossTicks > 0 ? entryPrice - stopDist : 0;
                    m_ProfitTargetPrice = ProfitTargetTicks > 0 ? entryPrice + targetDist : 0;
                } else {
                    m_ProtectiveStopPrice = ProtectiveStopLossTicks > 0 ? entryPrice + stopDist : 0;
                    m_ProfitTargetPrice = ProfitTargetTicks > 0 ? entryPrice - targetDist : 0;
                }
                m_BuyOrderActive = m_SellOrderActive = false; m_StopPrice = m_LastSentPrice = 0;
                m_AutoProtectiveStopMoved = false;
                m_ActiveEntrySetup = EEntrySetup.None;
                m_EmaBounceOrderBar = -1;
                m_PinBarOrderBar = -1;
                m_ShiftProjectionActive = false;
                m_ShiftProjectionBar = -1;
                if (m_GoSignalMarker != null) m_GoSignalMarker.Delete();
                ClearProjectedEntryLine(); UpdateTargetLine(); UpdateStopLine();
                DrawFilledEntryMarkers(currentPosition, entryPrice, tickSize,
                                       filledEntrySetup);
            }

            UpdateAutoProtectiveStopOnOneBarProfit(currentPosition, tickSize);

            if (currentPosition == 0) {
                int currentQty = OrderQuantity;
                if (m_BuyOrderActive && m_StopPrice > 0) {
                    // Re-send on every IOG calculation so the native order remains
                    // active between ticks. A pin uses its completed-bar price;
                    // an EMA bounce updates its stop from the live projection.
                    if (Bars.LastBarOnChart) { m_BuyStop.Send(m_StopPrice, currentQty); m_LastSentPrice = m_StopPrice; }
                    UpdateProjectedEntryLine();
                } else if (m_SellOrderActive && m_StopPrice > 0) {
                    // Re-send on every IOG calculation so the native order remains
                    // active between ticks. A pin uses its completed-bar price;
                    // an EMA bounce updates its stop from the live projection.
                    if (Bars.LastBarOnChart) { m_SellStop.Send(m_StopPrice, currentQty); m_LastSentPrice = m_StopPrice; }
                    UpdateProjectedEntryLine();
                } else {
                    ClearProjectedEntryLine();
                }
            }

            if (Bars.LastBarOnChart) {
                // Keep the strategy-owned exit controls visible and separate
                // from MultiCharts' native-order badges.
                if (currentPosition != 0) {
                    UpdateTargetLine();
                    UpdateStopLine();
                }
                SubmitActiveExitOrders(currentPosition);
            }

            if (currentPosition == 0 && m_LastMarketPosition != 0) { 
                FinalizeActiveTradeEntryMarker();
                // Require a deliberate re-arm after every completed trade.
                // This applies equally to profit targets, protective stops,
                // and any other path that takes the strategy flat.
                m_KillModeActive = true;
                m_FlattenRequested = false;
                m_AutoEntryArmed = false;
                m_ArmedDirection = 0;
                m_ProtectiveStopPrice = m_ProfitTargetPrice = 0; 
                m_BuyOrderActive = m_SellOrderActive = false; 
                m_ActiveEntrySetup = EEntrySetup.None;
                m_EmaBounceOrderBar = -1;
                m_PinBarOrderBar = -1;
                m_ShiftProjectionActive = false;
                m_ShiftProjectionBar = -1;
                m_LastSentPrice = 0; 
                m_AutoProtectiveStopMoved = false;
                ClearTradingDrawings(); 
            }
            m_LastMarketPosition = currentPosition; if (ShowHUD) UpdateHUD();
        }

        private void CheckForHiddenPierceSignals(double tickSize) {
            double activeTicks = GetActiveRangeTicks(tickSize);
            if (activeTicks <= 0) activeTicks = 7;
            double alpha5 = 2.0 / 9.0; double alpha15 = 2.0 / 25.0;
            
            double highest10 = Bars.High.Highest(10);
            double lowest10 = Bars.Low.Lowest(10);
            double range10 = (highest10 - lowest10) / tickSize;
            bool expansionValid = range10 >= MinExpansionTicks;

            // PROJECT BULLISH
            double projCloseBull = Bars.Low[0] + (activeTicks * tickSize);
            double projEma5Bull = m_FastEMA[0] + alpha5 * (projCloseBull - m_FastEMA[0]);
            double projEma15Bull = m_SlowEMA[0] + alpha15 * (projCloseBull - m_SlowEMA[0]);

            // CALCULATE ANGLES (BULLISH)
            double a60 = GetAngle(m_MasterEMA[0], m_MasterEMA[3], 3, tickSize);
            double a15 = GetAngle(m_SlowEMA[0], m_SlowEMA[3], 3, tickSize);
            double a5  = GetAngle(m_FastEMA[0], m_FastEMA[3], 3, tickSize);
            
            bool fanStackBull = m_FastEMA[0] > m_SlowEMA[0] && m_SlowEMA[0] > m_MasterEMA[0]; 
            bool breadth15_60B = (m_SlowEMA[0] - m_MasterEMA[0]) >= (MinBreadth_15_60 * tickSize);
            bool breadth5_15B = (m_FastEMA[0] - m_SlowEMA[0]) >= (MinBreadth_5_15 * tickSize);
            
            bool angleValidBull = a60 >= 45 && a15 >= 45 && a5 >= 45;

            if (expansionValid && fanStackBull && breadth15_60B && breadth5_15B && angleValidBull) {
                if (projEma15Bull > Bars.Low[0]) { 
                    m_BuyOrderActive = true;
                    if (m_GoSignalMarker == null) {
                        m_GoSignalMarker = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], Bars.Low[0] - (3 * tickSize)), new ChartPoint(Bars.Time[0].AddMinutes(0), Bars.Low[0] - (3 * tickSize)));
                        m_GoSignalMarker.Color = Color.RoyalBlue; m_GoSignalMarker.Size = 12;
                    }
                } else if (m_FastEMA[0] > m_FastEMA[1] && projEma5Bull > Bars.Low[0]) { 
                    m_BuyOrderActive = true;
                    if (m_GoSignalMarker == null) {
                        m_GoSignalMarker = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], Bars.Low[0] - (3 * tickSize)), new ChartPoint(Bars.Time[0].AddMinutes(0), Bars.Low[0] - (3 * tickSize)));
                        m_GoSignalMarker.Color = Color.Cyan; m_GoSignalMarker.Size = 10;
                    }
                } 
            } else {
                // PROJECT BEARISH
                double projCloseBear = Bars.High[0] - (activeTicks * tickSize);
                double projEma5Bear = m_FastEMA[0] + alpha5 * (projCloseBear - m_FastEMA[0]);
                double projEma15Bear = m_SlowEMA[0] + alpha15 * (projCloseBear - m_SlowEMA[0]);
                
                // CALCULATE ANGLES (BEARISH)
                double a60S = GetAngle(m_MasterEMA[0], m_MasterEMA[3], 3, tickSize);
                double a15S = GetAngle(m_SlowEMA[0], m_SlowEMA[3], 3, tickSize);
                double a5S  = GetAngle(m_FastEMA[0], m_FastEMA[3], 3, tickSize);

                bool fanStackBear = m_FastEMA[0] < m_SlowEMA[0] && m_SlowEMA[0] < m_MasterEMA[0]; 
                bool breadth15_60S = (m_MasterEMA[0] - m_SlowEMA[0]) >= (MinBreadth_15_60 * tickSize);
                bool breadth5_15S = (m_SlowEMA[0] - m_FastEMA[0]) >= (MinBreadth_5_15 * tickSize);
                
                bool angleValidBear = a60S <= -45 && a15S <= -45 && a5S <= -45;

                if (expansionValid && fanStackBear && breadth15_60S && breadth5_15S && angleValidBear) {
                    if (projEma15Bear < Bars.High[0]) { 
                        m_SellOrderActive = true;
                        if (m_GoSignalMarker == null) {
                            m_GoSignalMarker = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], Bars.High[0] + (3 * tickSize)), new ChartPoint(Bars.Time[0].AddMinutes(0), Bars.High[0] + (3 * tickSize)));
                            m_GoSignalMarker.Color = Color.DeepPink; m_GoSignalMarker.Size = 12;
                        }
                    } else if (m_FastEMA[0] < m_FastEMA[1] && projEma5Bear < Bars.High[0]) { 
                        m_SellOrderActive = true;
                        if (m_GoSignalMarker == null) {
                            m_GoSignalMarker = DrwTrendLine.Create(new ChartPoint(Bars.Time[0], Bars.High[0] + (3 * tickSize)), new ChartPoint(Bars.Time[0].AddMinutes(0), Bars.High[0] + (3 * tickSize)));
                            m_GoSignalMarker.Color = Color.Magenta; m_GoSignalMarker.Size = 10;
                        }
                    }
                } else if (m_GoSignalMarker != null) { m_GoSignalMarker.Delete(); m_GoSignalMarker = null; }
            }
        }

        protected override void OnMouseEvent(MouseClickArgs arg) {
            // Some MultiCharts chart configurations omit F12 from arg.keys,
            // so also check its physical Windows key state.
            if (arg.buttons == MouseButtons.Left &&
                IsF12Held(arg.keys)) {
                ActivateEmergencyFlatten(true);
                return;
            }

            if (arg.buttons != MouseButtons.Left) return;
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale; if (tickSize <= 0) tickSize = 0.25;
            if ((arg.keys & Keys.Control) == Keys.Control) {
                int currentPosition = StrategyInfo.MarketPosition;
                if (currentPosition != 0 || HasWorkingStrategyOrders()) {
                    // With a position or any broker-side working strategy
                    // order, Ctrl-click uses the full emergency path: cancel
                    // all strategy orders, then flatten until confirmed flat.
                    ActivateEmergencyFlatten(false);
                } else if (m_AutoEntryArmed) {
                    // No working order exists, so this is only a disarm.
                    ActivateKillMode(currentPosition);
                } else if (EnablePinBarTrading || Enable24EMABounceTrading) {
                    // Flat and unarmed: latch the 24 EMA direction and begin
                    // waiting persistently for an enabled automated setup.
                    ArmAutomatedEntryMode(tickSize);
                }
                if (ShowHUD) UpdateHUD();
            }
            else if (IsShiftClick(arg.keys)) {
                StartShiftProjectionEntry(tickSize);
                if (ShowHUD) UpdateHUD();
            }
            else if (IsAltClick(arg.keys)) {
                AdvanceProfitTargetOneRange(tickSize);
            }
            else if (m_DraggingTarget) {
                m_ProfitTargetPrice = Math.Round(arg.point.Price / tickSize) * tickSize;
                m_DraggingTarget = false;
                UpdateTargetLine();
                // Re-submit immediately at the price selected on our strategy
                // line. This is the authoritative target price, unlike moving
                // MultiCharts' broker-order badge directly.
                SubmitActiveExitOrders(StrategyInfo.MarketPosition);
            }
            else if (m_DraggingStop) {
                m_ProtectiveStopPrice = Math.Round(arg.point.Price / tickSize) * tickSize;
                m_DraggingStop = false;
                UpdateStopLine();
                SubmitActiveExitOrders(StrategyInfo.MarketPosition);
            }
            else if (m_ProfitTargetPrice > 0 && Math.Abs(arg.point.Price - m_ProfitTargetPrice) <= (ProximityTicks * tickSize)) {
                m_DraggingTarget = true;
                SetTargetLineSelected(true);
            }
            else if (m_ProtectiveStopPrice > 0 && Math.Abs(arg.point.Price - m_ProtectiveStopPrice) <= (ProximityTicks * tickSize)) {
                m_DraggingStop = true;
                SetStopLineSelected(true);
            }
        }

        private void ArmAutomatedEntryMode(double tickSize) {
            ClearEmergencyIndicator();
            m_KillModeActive = false;
            m_FlattenRequested = false;
            m_AutoEntryArmed = true;
            // Preserve the existing semantics: flat/rising 24 EMA is bullish;
            // falling 24 EMA is bearish. The direction remains latched until
            // the trader disarms with Ctrl-click.
            m_ArmedDirection = m_SlowEMA[0] >= m_SlowEMA[1] ? 1 : -1;
            m_PinProjectionBar = Bars.CurrentBar;
            m_PinProjectionDirection = m_ArmedDirection;
            m_PinProjectionTailReached = false;
            m_PinProjectionBroken = false;
            m_PinProjectionTailTicks = PinBarMinTailTicks;
            m_PinProjectionOpenAligned = IsPinBarOpenOnCorrectEmaSide(
                m_PinProjectionDirection, tickSize);
            m_BuyOrderActive = m_SellOrderActive = false;
            m_ActiveEntrySetup = EEntrySetup.None;
            m_EmaBounceOrderBar = -1;
            m_PinBarOrderBar = -1;
            m_ShiftProjectionActive = false;
            m_ShiftProjectionBar = -1;
            m_StopPrice = m_LastSentPrice = 0;
            ClearProjectedEntryLine();

            // Arming can occur between normal CalcBar calls.  Build and
            // arbitrate both candidates here as well, otherwise both drawings
            // can briefly exist and a later pin invalidation appears to have
            // removed the EMA setup that was merely hidden.
            ResetAutomaticEntryCandidates();
            UpdatePinBarProjection(tickSize, StrategyInfo.MarketPosition);
            UpdateEmaBounceProjection(tickSize, StrategyInfo.MarketPosition);
            ReconcileAutomaticEntryCandidates(tickSize, StrategyInfo.MarketPosition);
            ApplyProjectionDisplayPriority();
        }

        private void ActivateKillMode(int currentPosition) {
            m_KillModeActive = true;
            m_FlattenRequested = currentPosition != 0;
            m_AutoEntryArmed = false;
            m_ArmedDirection = 0;
            m_PinProjectionBar = -1;
            m_PinProjectionDirection = 0;
            m_PinProjectionTailReached = false;
            m_PinProjectionBroken = false;
            m_PinProjectionTailTicks = PinBarMinTailTicks;
            m_PinProjectionOpenAligned = false;
            m_EmaBounceProjectionBar = -1;
            m_EmaBounceProjectionDirection = 0;
            m_BuyOrderActive = m_SellOrderActive = false;
            m_ActiveEntrySetup = EEntrySetup.None;
            m_EmaBounceOrderBar = -1;
            m_PinBarOrderBar = -1;
            m_ShiftProjectionActive = false;
            m_ShiftProjectionBar = -1;
            m_StopPrice = m_LastSentPrice = 0;
            m_ProtectiveStopPrice = m_ProfitTargetPrice = 0;
            m_AutoProtectiveStopMoved = false;
            m_DraggingTarget = m_DraggingStop = false;
            ClearTradingDrawings();
        }

        private void ActivateEmergencyFlatten(bool showEmergencyMessage) {
            int currentPosition = StrategyInfo.MarketPosition;
            ActivateKillMode(currentPosition);
            string cancellationStatus = RequestTrackerOrderCancellations();
            if (showEmergencyMessage) ShowEmergencyIndicator(cancellationStatus);
            else ClearEmergencyIndicator();

            // Prefer the broker-reported side immediately; CalcBar continues
            // the request until that same broker position is confirmed flat.
            bool brokerPositionAvailable;
            int brokerPosition = GetBrokerPositionForStrategy(out brokerPositionAvailable);
            int positionToFlatten = brokerPositionAvailable
                ? brokerPosition
                : currentPosition;
            if (positionToFlatten > 0) m_CloseLongNextBar.Send();
            else if (positionToFlatten < 0) m_CloseShortNextBar.Send();
        }

        private string RequestTrackerOrderCancellations() {
            m_EmergencyCancelOrderIds.Clear();
            m_EmergencyCancellationPending = false;

            var tradeManager = TradeManager;
            if (tradeManager == null || tradeManager.TradingData == null ||
                tradeManager.TradingData.Orders == null)
                return "EMERGENCY: ORDER TRACKER UNAVAILABLE";

            try {
                tradeManager.ProcessEvents();
                var orders = tradeManager.TradingData.Orders.Items;
                if (orders == null) return "EMERGENCY: NO WORKING STRATEGY ORDERS";

                List<string> requested = new List<string>();
                foreach (var order in orders) {
                    if (!IsThisStrategyOrder(order.StrategyName, order.Name) || !IsWorkingOrder((int)order.State)) continue;
                    RememberStrategyBrokerScope(order.Profile, order.Account,
                                                GetTrackerSymbol(order));

                    // ITradingProfile is exposed by MultiCharts as the objects
                    // in TradingProfiles; use its documented order ID rather
                    // than sending a synthetic priced order.
                    foreach (var tradingProfile in tradeManager.TradingProfiles) {
                        if (!string.Equals(tradingProfile.Name, order.Profile, StringComparison.OrdinalIgnoreCase)) continue;
                        tradingProfile.CancelOrder(order.OrderID);
                        m_EmergencyCancelOrderIds.Add(order.OrderID);
                        requested.Add(DescribeOrder(order.Name, order.Contracts, order.StopPrice, order.LimitPrice));
                        break;
                    }
                }

                if (requested.Count == 0) return "EMERGENCY: NO WORKING STRATEGY ORDERS";

                m_EmergencyCancellationPending = true;
                return "EMERGENCY: CANCEL REQUESTED " + string.Join(", ", requested.ToArray());
            } catch (Exception ex) {
                Output.WriteLine("RangeBarTrading tracker cancel error: " + ex.Message);
                return "EMERGENCY: ORDER CANCEL ERROR";
            }
        }

        private bool HasWorkingStrategyOrders() {
            if (m_BuyOrderActive || m_SellOrderActive) return true;

            var tradeManager = TradeManager;
            if (tradeManager == null || tradeManager.TradingData == null ||
                tradeManager.TradingData.Orders == null) return false;

            try {
                tradeManager.ProcessEvents();
                var orders = tradeManager.TradingData.Orders.Items;
                if (orders == null) return false;
                foreach (var order in orders) {
                    if (IsThisStrategyOrder(order.StrategyName, order.Name))
                        RememberStrategyBrokerScope(order.Profile, order.Account,
                                                    GetTrackerSymbol(order));
                    if (IsThisStrategyOrder(order.StrategyName, order.Name) &&
                        IsWorkingOrder((int)order.State)) return true;
                }
            } catch (Exception ex) {
                Output.WriteLine("RangeBarTrading working-order check error: " + ex.Message);
            }
            return false;
        }

        private void RememberStrategyBrokerScope(string profile, string account,
                                                 string symbol) {
            if (string.IsNullOrEmpty(profile) || string.IsNullOrEmpty(account)) return;
            m_StrategyBrokerProfile = profile;
            m_StrategyBrokerAccount = account;
            if (!string.IsNullOrEmpty(symbol)) m_StrategyBrokerSymbol = symbol;
        }

        private int GetBrokerPositionForStrategy(out bool isAvailable) {
            isAvailable = false;
            var tradeManager = TradeManager;
            if (tradeManager == null || tradeManager.TradingData == null ||
                tradeManager.TradingData.Positions == null) return 0;

            try {
                tradeManager.ProcessEvents();
                // If this instance has not yet recorded its profile/account,
                // recover it from the strategy's tracked orders first.
                if (string.IsNullOrEmpty(m_StrategyBrokerProfile) ||
                    string.IsNullOrEmpty(m_StrategyBrokerAccount) ||
                    string.IsNullOrEmpty(m_StrategyBrokerSymbol)) {
                    var orders = tradeManager.TradingData.Orders.Items;
                    if (orders != null) {
                        foreach (var order in orders) {
                            if (IsThisStrategyOrder(order.StrategyName, order.Name)) {
                                RememberStrategyBrokerScope(order.Profile, order.Account,
                                                            GetTrackerSymbol(order));
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(m_StrategyBrokerProfile) ||
                    string.IsNullOrEmpty(m_StrategyBrokerAccount) ||
                    string.IsNullOrEmpty(m_StrategyBrokerSymbol)) return 0;

                var positions = tradeManager.TradingData.Positions.Items;
                if (positions == null) return 0;
                isAvailable = true;
                int brokerPosition = 0;
                foreach (var position in positions) {
                    if (!string.Equals(position.Profile, m_StrategyBrokerProfile,
                                       StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(position.Account, m_StrategyBrokerAccount,
                                       StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(GetTrackerSymbol(position),
                                       m_StrategyBrokerSymbol,
                                       StringComparison.OrdinalIgnoreCase)) continue;
                    brokerPosition += position.Value;
                }
                return brokerPosition;
            } catch (Exception ex) {
                Output.WriteLine("RangeBarTrading broker-position check error: " + ex.Message);
                isAvailable = false;
                return 0;
            }
        }

        private string GetTrackerSymbol(object trackerItem) {
            if (trackerItem == null) return string.Empty;
            string[] propertyNames = {
                "Symbol", "SymbolName", "Instrument", "InstrumentName"
            };
            Type itemType = trackerItem.GetType();
            foreach (string propertyName in propertyNames) {
                var property = itemType.GetProperty(propertyName);
                if (property == null) continue;
                object value = property.GetValue(trackerItem, null);
                if (value != null && !string.IsNullOrEmpty(value.ToString()))
                    return value.ToString();
            }
            return string.Empty;
        }

        private bool IsThisStrategyOrder(string strategyName, string orderName) {
            if (!string.Equals(strategyName, GetType().Name, StringComparison.OrdinalIgnoreCase)) return false;
            return orderName == "RangeBuy" || orderName == "RangeSell" ||
                   orderName == "ProtectLong" || orderName == "ProtectShort" ||
                   orderName == "ProfitLong" || orderName == "ProfitShort";
        }

        private bool IsWorkingOrder(int state) {
            // Order & Position Tracker state values: PreSubmitted (0),
            // Submitted (1), Sent (5), PartiallyFilled (7), and PreChanged
            // (8) can still be active at the broker or Paper Trader.
            return state == 0 || state == 1 || state == 5 ||
                   state == 7 || state == 8;
        }

        private string DescribeOrder(string orderName, int contracts, double? stopPrice, double? limitPrice) {
            double? price = stopPrice.HasValue ? stopPrice : limitPrice;
            return orderName + " x" + contracts +
                   (price.HasValue ? " @ " + price.Value.ToString("0.00") : "");
        }

        private void RefreshEmergencyCancellationStatus() {
            if (!m_EmergencyCancellationPending || m_EmergencyCancelOrderIds.Count == 0) return;

            try {
                var tradeManager = TradeManager;
                if (tradeManager == null || tradeManager.TradingData == null ||
                    tradeManager.TradingData.Orders == null) return;

                tradeManager.ProcessEvents();
                var orders = tradeManager.TradingData.Orders.Items;
                if (orders == null) return;

                List<string> cancelled = new List<string>();
                foreach (var order in orders) {
                    if (!m_EmergencyCancelOrderIds.Contains(order.OrderID)) continue;
                    // Order & Position Tracker's Cancelled state is 2.
                    if ((int)order.State != 2) return;
                    cancelled.Add(DescribeOrder(order.Name, order.Contracts, order.StopPrice, order.LimitPrice));
                }

                if (cancelled.Count != m_EmergencyCancelOrderIds.Count) return;
                m_EmergencyCancellationPending = false;
                ShowEmergencyIndicator("EMERGENCY: CANCELLED " + string.Join(", ", cancelled.ToArray()));
            } catch (Exception ex) {
                Output.WriteLine("RangeBarTrading tracker status error: " + ex.Message);
            }
        }

        private void ShowEmergencyIndicator(string text) {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;
            ChartPoint point = new ChartPoint(Bars.Time[0], Bars.High[0] + (20 * tickSize));
            if (m_EmergencyLabel == null) {
                m_EmergencyLabel = DrwText.Create(point, text);
                m_EmergencyLabel.Size = 16;
                m_EmergencyLabel.HStyle = ETextStyleH.Left;
                m_EmergencyLabel.VStyle = ETextStyleV.Above;
            }
            m_EmergencyLabel.Location = point;
            m_EmergencyLabel.Text = text;
            m_EmergencyLabel.Color = Color.Red;
            m_EmergencyMessageExpiresAt = DateTime.Now.AddSeconds(2);
        }

        private void ClearEmergencyIndicator() {
            if (m_EmergencyLabel != null) { m_EmergencyLabel.Delete(); m_EmergencyLabel = null; }
            m_EmergencyMessageExpiresAt = DateTime.MinValue;
        }

        private void UpdatePinBarProjection(double tickSize, int currentPosition) {
            if (!EnablePinBarTrading || currentPosition != 0 ||
                m_ShiftProjectionActive) {
                ClearPinBarProjectionLines();
                return;
            }

            // An armed direction is deliberately persistent. While unarmed,
            // choose one informational projection per new bar from the same
            // 24 EMA slope rule so the chart never shows four competing lines.
            if (m_PinProjectionBar != Bars.CurrentBar) {
                m_PinProjectionBar = Bars.CurrentBar;
                m_PinProjectionDirection = m_AutoEntryArmed
                    ? m_ArmedDirection
                    : GetSlowEmaDirection();
                m_PinProjectionTailReached = false;
                m_PinProjectionBroken = false;
                m_PinProjectionTailTicks = PinBarMinTailTicks;
                m_PinProjectionOpenAligned = IsPinBarOpenOnCorrectEmaSide(
                    m_PinProjectionDirection, tickSize);
            }

            int direction = m_AutoEntryArmed ? m_ArmedDirection : m_PinProjectionDirection;
            if (direction == 0) {
                ClearPinBarProjectionLines();
                ClearPinBarEntryIfActive();
                return;
            }

            if (!m_PinProjectionOpenAligned) {
                ClearPinBarProjectionLines();
                ClearPinBarEntryIfActive();
                return;
            }

            double projectedLow;
            double projectedHigh;
            int bodyTicks = PinBarRangeTicks - m_PinProjectionTailTicks;
            GetPinBarProjectionPrices(direction, m_PinProjectionTailTicks, bodyTicks,
                                       tickSize, out projectedLow, out projectedHigh);
            if (m_PinProjectionTailTicks < PinBarRangeTicks)
                UpdatePinBarFormationState(direction, projectedLow, projectedHigh, tickSize);

            if (m_PinProjectionBroken ||
                !CanStillFormPinBar(direction, m_PinProjectionTailTicks, bodyTicks,
                                     tickSize)) {
                // A 3/2 pin can extend to 4/1, then to a 5/0 all-tail pin.
                // Advance to the next valid shape while preserving the same
                // live bar and staged order.
                bool foundNextShape = false;
                for (int nextTail = m_PinProjectionTailTicks + 1;
                     nextTail <= PinBarRangeTicks;
                     nextTail++) {
                    int nextBody = PinBarRangeTicks - nextTail;
                    if (!CanStillFormPinBar(direction, nextTail, nextBody, tickSize))
                        continue;

                    m_PinProjectionTailTicks = nextTail;
                    m_PinProjectionBroken = false;
                    GetPinBarProjectionPrices(direction, nextTail, nextBody, tickSize,
                                               out projectedLow, out projectedHigh);
                    m_PinProjectionTailReached = HasReachedPinBarTail(
                        direction, projectedLow, projectedHigh, tickSize);
                    foundNextShape = true;
                    break;
                }

                if (!foundNextShape) {
                    m_PinProjectionBroken = true;
                    ClearPinBarProjectionLines();
                    ClearPinBarEntryIfActive();
                    return;
                }
            }

            UpdatePinBarProjectionLine(ref m_PinBarLowerLine, projectedLow, direction,
                                       m_PinProjectionTailReached);
            UpdatePinBarProjectionLine(ref m_PinBarUpperLine, projectedHigh, direction,
                                       m_PinProjectionTailReached);
            UpdatePinBarProjectionLabel(projectedHigh, direction,
                                        m_PinProjectionTailReached);

            // A pin becomes an actionable entry candidate only after its tail
            // is reached. Until then it cannot displace an EMA order.
            if (m_PinProjectionTailReached) {
                m_PinEntryCandidateValid = true;
                m_PinEntryCandidateDirection = direction;
                m_PinEntryCandidatePrice = direction > 0
                    ? RoundToTick(projectedHigh + (EntryOffsetTicks * tickSize), tickSize)
                    : RoundToTick(projectedLow - (EntryOffsetTicks * tickSize), tickSize);
            } else {
                ClearPinBarEntryIfActive();
            }
        }

        private bool IsPinBarOpenOnCorrectEmaSide(int direction, double tickSize) {
            double open = RoundToTick(Bars.Open[0], tickSize);
            return direction > 0 ? open > m_SlowEMA[0] : open < m_SlowEMA[0];
        }

        private bool HasReachedPinBarTail(int direction, double projectedLow,
                                          double projectedHigh, double tickSize) {
            double tolerance = tickSize * 0.1;
            return direction > 0
                ? Bars.Low[0] <= projectedLow + tolerance
                : Bars.High[0] >= projectedHigh - tolerance;
        }

        private int GetSlowEmaDirection() {
            if (Bars.CurrentBar < 2) return 1;
            return m_SlowEMA[0] >= m_SlowEMA[1] ? 1 : -1;
        }

        private void GetPinBarProjectionPrices(int direction, int tailTicks, int bodyTicks,
                                               double tickSize, out double projectedLow,
                                               out double projectedHigh) {
            double open = RoundToTick(Bars.Open[0], tickSize);
            if (direction > 0) {
                projectedLow = open - (tailTicks * tickSize);
                projectedHigh = open + (bodyTicks * tickSize);
            } else {
                projectedLow = open - (bodyTicks * tickSize);
                projectedHigh = open + (tailTicks * tickSize);
            }
            projectedLow = RoundToTick(projectedLow, tickSize);
            projectedHigh = RoundToTick(projectedHigh, tickSize);
        }

        private void UpdatePinBarFormationState(int direction, double projectedLow,
                                                double projectedHigh, double tickSize) {
            if (m_PinProjectionBroken || m_PinProjectionTailReached) return;

            double tolerance = tickSize * 0.1;
            bool tailTouched = direction > 0
                ? Bars.Low[0] <= projectedLow + tolerance
                : Bars.High[0] >= projectedHigh - tolerance;
            bool bodySideTouched = direction > 0
                ? Bars.High[0] >= projectedHigh - tolerance
                : Bars.Low[0] <= projectedLow + tolerance;

            if (tailTouched && bodySideTouched) {
                // On a completed range bar, the close tells us which boundary
                // was touched last. A valid pin reaches its tail first and
                // finishes at the body-side boundary.
                double bodySidePrice = direction > 0 ? projectedHigh : projectedLow;
                m_PinProjectionTailReached =
                    Math.Abs(Bars.Close[0] - bodySidePrice) <= tolerance;
                m_PinProjectionBroken = !m_PinProjectionTailReached;
            } else if (tailTouched) {
                m_PinProjectionTailReached = true;
            } else if (bodySideTouched) {
                m_PinProjectionBroken = true;
            }
        }

        private bool CanStillFormPinBar(int direction, int tailTicks, int bodyTicks,
                                        double tickSize) {
            double projectedLow;
            double projectedHigh;
            GetPinBarProjectionPrices(direction, tailTicks, bodyTicks, tickSize,
                                       out projectedLow, out projectedHigh);
            return CanStillFormPinBar(projectedLow, projectedHigh, tickSize);
        }

        private bool CanStillFormPinBar(double projectedLow, double projectedHigh,
                                        double tickSize) {
            double tolerance = tickSize * 0.1;
            return Bars.Low[0] >= projectedLow - tolerance &&
                   Bars.High[0] <= projectedHigh + tolerance;
        }

        private void ArmOrUpdateProjectedPinBarEntry(int direction, double projectedLow,
                                                     double projectedHigh, double tickSize) {
            if (!m_AutoEntryArmed || StrategyInfo.MarketPosition != 0 ||
                direction == 0) return;

            // EMA Bounce owns the single entry order whenever both setups are
            // available. A pin can only create or update its own pending order.
            if (m_ActiveEntrySetup != EEntrySetup.None &&
                m_ActiveEntrySetup != EEntrySetup.PinBar) return;

            m_ActiveEntrySetup = EEntrySetup.PinBar;
            m_PinBarOrderBar = Bars.CurrentBar;
            m_BuyOrderActive = direction > 0;
            m_SellOrderActive = direction < 0;
            m_StopPrice = direction > 0
                ? RoundToTick(projectedHigh + (EntryOffsetTicks * tickSize), tickSize)
                : RoundToTick(projectedLow - (EntryOffsetTicks * tickSize), tickSize);
            m_LastSentPrice = 0;
        }

        private void ClearPinBarEntryIfActive() {
            if (m_ActiveEntrySetup == EEntrySetup.PinBar &&
                StrategyInfo.MarketPosition == 0) {
                ClearPendingEntry();
                m_PinBarOrderBar = -1;
            }
        }

        private void UpdateEmaBounceProjection(double tickSize, int currentPosition) {
            if (!Enable24EMABounceTrading || currentPosition != 0 ||
                m_ShiftProjectionActive) {
                ClearEmaBounceProjectionLines();
                return;
            }

            if (m_EmaBounceProjectionBar != Bars.CurrentBar) {
                m_EmaBounceProjectionBar = Bars.CurrentBar;
                m_EmaBounceProjectionDirection = 0;
            }

            int qualifyingDirection = GetEmaBounceDirection();
            if (qualifyingDirection == 0) {
                m_EmaBounceProjectionDirection = 0;
                ClearEmaBounceProjectionLines();
                ClearEmaBounceEntryIfActive();
                return;
            }
            m_EmaBounceProjectionDirection = qualifyingDirection;

            double projectedLow;
            double projectedHigh;
            if (!TryGetEmaBounceProjectionPrices(m_EmaBounceProjectionDirection,
                                                  tickSize,
                                                  out projectedLow,
                                                  out projectedHigh)) {
                // Geometry can recover before the range bar completes, so do
                // not latch this as a permanent failure for the current bar.
                ClearEmaBounceProjectionLines();
                ClearEmaBounceEntryIfActive();
                return;
            }

            bool emaBoundaryReached = HasReachedEmaBounceBoundary(
                m_EmaBounceProjectionDirection, projectedLow, projectedHigh, tickSize);
            UpdateEmaBounceProjectionLine(ref m_EmaBounceLowerLine, projectedLow,
                                          m_EmaBounceProjectionDirection,
                                          emaBoundaryReached);
            UpdateEmaBounceProjectionLine(ref m_EmaBounceUpperLine, projectedHigh,
                                          m_EmaBounceProjectionDirection,
                                          emaBoundaryReached);
            UpdateEmaBounceProjectionLabel(
                m_EmaBounceProjectionDirection > 0 ? projectedHigh : projectedLow,
                m_EmaBounceProjectionDirection, emaBoundaryReached);

            // A displayed projection is only a possible bounce.  Do not put a
            // native stop order on the chart until price has actually reached
            // the EMA-side boundary of that projected range bar.  This is the
            // same gate used by pin bars: show the setup first, stage the
            // entry only after its required boundary has been touched.
            if (!emaBoundaryReached) {
                ClearEmaBounceEntryIfActive();
                return;
            }

            m_EmaEntryCandidateValid = true;
            m_EmaEntryCandidateDirection = m_EmaBounceProjectionDirection;
            m_EmaEntryCandidatePrice = m_EmaBounceProjectionDirection > 0
                ? RoundToTick(projectedLow + (EmaBounceEntryOffsetTicks * tickSize), tickSize)
                : RoundToTick(projectedHigh - (EmaBounceEntryOffsetTicks * tickSize), tickSize);
        }

        private bool HasReachedEmaBounceBoundary(int direction, double projectedLow,
                                                  double projectedHigh, double tickSize) {
            double tolerance = tickSize * 0.1;
            return direction > 0
                ? Bars.Low[0] <= projectedLow + tolerance
                : Bars.High[0] >= projectedHigh - tolerance;
        }

        private int GetEmaBounceDirection() {
            // Arming is the trader's slope/angle decision. A live bounce only
            // needs the 8/24 stack to identify its long or short direction.
            if (m_FastEMA[0] > m_SlowEMA[0])
                return 1;
            if (m_FastEMA[0] < m_SlowEMA[0])
                return -1;
            return 0;
        }

        private void ResetAutomaticEntryCandidates() {
            m_PinEntryCandidateValid = false;
            m_PinEntryCandidateDirection = 0;
            m_PinEntryCandidatePrice = 0;
            m_EmaEntryCandidateValid = false;
            m_EmaEntryCandidateDirection = 0;
            m_EmaEntryCandidatePrice = 0;
        }

        private void ReconcileAutomaticEntryCandidates(double tickSize,
                                                        int currentPosition) {
            if (currentPosition != 0 || !m_AutoEntryArmed ||
                m_ShiftProjectionActive ||
                m_ActiveEntrySetup == EEntrySetup.ShiftProjection) return;

            EEntrySetup selectedSetup = EEntrySetup.None;
            int selectedDirection = 0;
            double selectedPrice = 0;
            if (m_PinEntryCandidateValid && m_EmaEntryCandidateValid) {
                double pinDistance = Math.Abs(m_PinEntryCandidatePrice - Bars.Close[0]);
                double emaDistance = Math.Abs(m_EmaEntryCandidatePrice - Bars.Close[0]);
                // A tie uses the EMA setup for a stable, deterministic result.
                if (emaDistance <= pinDistance) {
                    selectedSetup = EEntrySetup.Ema24Bounce;
                    selectedDirection = m_EmaEntryCandidateDirection;
                    selectedPrice = m_EmaEntryCandidatePrice;
                } else {
                    selectedSetup = EEntrySetup.PinBar;
                    selectedDirection = m_PinEntryCandidateDirection;
                    selectedPrice = m_PinEntryCandidatePrice;
                }
            } else if (m_EmaEntryCandidateValid) {
                selectedSetup = EEntrySetup.Ema24Bounce;
                selectedDirection = m_EmaEntryCandidateDirection;
                selectedPrice = m_EmaEntryCandidatePrice;
            } else if (m_PinEntryCandidateValid) {
                selectedSetup = EEntrySetup.PinBar;
                selectedDirection = m_PinEntryCandidateDirection;
                selectedPrice = m_PinEntryCandidatePrice;
            }

            if (selectedSetup == EEntrySetup.None) {
                if (m_ActiveEntrySetup == EEntrySetup.PinBar ||
                    m_ActiveEntrySetup == EEntrySetup.Ema24Bounce) {
                    CancelWorkingEntryOrders();
                    ClearPendingEntry();
                }
                return;
            }

            bool setupChanged = m_ActiveEntrySetup != EEntrySetup.None &&
                                m_ActiveEntrySetup != selectedSetup;
            bool directionChanged = (m_BuyOrderActive && selectedDirection < 0) ||
                                    (m_SellOrderActive && selectedDirection > 0);
            if (setupChanged || directionChanged) {
                CancelWorkingEntryOrders();
                ClearPendingEntry();
            }

            m_ActiveEntrySetup = selectedSetup;
            m_BuyOrderActive = selectedDirection > 0;
            m_SellOrderActive = selectedDirection < 0;
            m_StopPrice = selectedPrice;
            m_LastSentPrice = 0;
            if (selectedSetup == EEntrySetup.PinBar)
                m_PinBarOrderBar = Bars.CurrentBar;
            else
                m_EmaBounceOrderBar = Bars.CurrentBar;
        }

        private void ApplyProjectionDisplayPriority() {
            if (m_PinEntryCandidateValid && m_EmaEntryCandidateValid) {
                double pinDistance = Math.Abs(m_PinEntryCandidatePrice - Bars.Close[0]);
                double emaDistance = Math.Abs(m_EmaEntryCandidatePrice - Bars.Close[0]);
                if (emaDistance <= pinDistance)
                    ClearPinBarProjectionLines();
                else
                    ClearEmaBounceProjectionLines();
            } else if (m_EmaEntryCandidateValid) {
                ClearPinBarProjectionLines();
            } else if (m_PinEntryCandidateValid) {
                ClearEmaBounceProjectionLines();
            }
        }

        private bool TryGetEmaBounceProjectionPrices(int direction, double tickSize,
                                                      out double projectedLow,
                                                      out double projectedHigh) {
            double range = GetActiveRangeTicks(tickSize) * tickSize;
            double currentEma = m_SlowEMA[0];
            double tolerance = tickSize * 0.1;
            projectedLow = projectedHigh = 0;
            if (direction == 0 || range <= 0) return false;

            if (direction > 0) {
                // Setup recognition is based solely on whether the current or
                // still-possible range bar can reach the live 24 EMA. The
                // six-tick entry distance must not disqualify the projection.
                double projectedTouchLow = currentEma;
                double lowestPossibleLow = Math.Max(Bars.High[0] - range,
                                                     currentEma - range);
                double highestPossibleLow = Math.Min(Bars.Low[0],
                                                      projectedTouchLow);
                if (lowestPossibleLow > highestPossibleLow + tolerance) return false;
                projectedLow = RoundDownToTick(highestPossibleLow, tickSize);
                if (projectedLow < lowestPossibleLow - tolerance) return false;
                projectedHigh = projectedLow + range;
            } else {
                // Bearish mirror: the current or still-possible bar only needs
                // to reach the live 24 EMA; entry pricing is handled separately.
                double projectedTouchHigh = currentEma;
                double lowestPossibleHigh = Math.Max(Bars.High[0],
                                                      projectedTouchHigh);
                double highestPossibleHigh = Math.Min(Bars.Low[0] + range,
                                                      currentEma + range);
                if (lowestPossibleHigh > highestPossibleHigh + tolerance) return false;
                projectedHigh = RoundUpToTick(lowestPossibleHigh, tickSize);
                if (projectedHigh > highestPossibleHigh + tolerance) return false;
                projectedLow = projectedHigh - range;
            }

            projectedLow = RoundToTick(projectedLow, tickSize);
            projectedHigh = RoundToTick(projectedHigh, tickSize);
            return true;
        }

        private void ArmOrUpdateProjectedEmaBounceEntry(int direction,
                                                        double projectedLow,
                                                        double projectedHigh,
                                                        double tickSize) {
            if (!m_AutoEntryArmed || StrategyInfo.MarketPosition != 0 ||
                direction == 0) return;

            // The strategy owns one pending entry at a time. An EMA bounce has
            // priority, so it replaces a still-working pin-bar entry; a current
            // position was already rejected above and therefore cannot receive
            // another entry order.
            if (m_ActiveEntrySetup == EEntrySetup.PinBar) {
                ClearPendingEntry();
                m_PinBarOrderBar = -1;
            } else if (m_ActiveEntrySetup != EEntrySetup.None &&
                       m_ActiveEntrySetup != EEntrySetup.Ema24Bounce)
                return;

            m_ActiveEntrySetup = EEntrySetup.Ema24Bounce;
            m_EmaBounceOrderBar = Bars.CurrentBar;
            m_BuyOrderActive = direction > 0;
            m_SellOrderActive = direction < 0;
            m_StopPrice = direction > 0
                ? RoundToTick(projectedLow + (EmaBounceEntryOffsetTicks * tickSize), tickSize)
                : RoundToTick(projectedHigh - (EmaBounceEntryOffsetTicks * tickSize), tickSize);
            m_LastSentPrice = 0;
        }

        private void ClearEmaBounceEntryIfActive() {
            if (m_ActiveEntrySetup == EEntrySetup.Ema24Bounce &&
                StrategyInfo.MarketPosition == 0) {
                ClearPendingEntry();
                m_EmaBounceOrderBar = -1;
            }
        }

        private void UpdateEmaBounceProjectionLine(ref ITrendLineObject line,
                                                   double price, int direction,
                                                   bool active) {
            ChartPoint begin = new ChartPoint(Bars.Time[0], price);
            ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), price);
            if (line == null) {
                line = DrwTrendLine.Create(begin, end);
                line.ExtRight = false;
            } else {
                line.Begin = begin;
                line.End = end;
            }
            line.Color = active
                ? (direction > 0 ? Color.MediumSeaGreen : Color.DarkViolet)
                : Color.Gray;
            line.Style = ETLStyle.ToolDashed;
            line.Size = 2;
        }

        private void UpdateEmaBounceProjectionLabel(double price, int direction,
                                                    bool active) {
            // Anchor on the live bar and align into the chart. A future-time
            // anchor can fall outside the visible pane until the user expands it.
            ChartPoint point = new ChartPoint(Bars.Time[0], price);
            if (m_EmaBounceLabel == null) {
                m_EmaBounceLabel = DrwText.Create(point, "24 EMA Bounce");
                m_EmaBounceLabel.Size = 10;
                m_EmaBounceLabel.HStyle = ETextStyleH.Right;
            }
            m_EmaBounceLabel.Location = point;
            m_EmaBounceLabel.Text = "24 EMA Bounce";
            m_EmaBounceLabel.Color = active
                ? (direction > 0 ? Color.MediumSeaGreen : Color.DarkViolet)
                : Color.Gray;
            m_EmaBounceLabel.VStyle = direction > 0 ? ETextStyleV.Above : ETextStyleV.Below;
        }

        private void ClearEmaBounceProjectionLines() {
            if (m_EmaBounceLowerLine != null) { m_EmaBounceLowerLine.Delete(); m_EmaBounceLowerLine = null; }
            if (m_EmaBounceUpperLine != null) { m_EmaBounceUpperLine.Delete(); m_EmaBounceUpperLine = null; }
            if (m_EmaBounceLabel != null) { m_EmaBounceLabel.Delete(); m_EmaBounceLabel = null; }
        }

        private void ResetEmaBounceProjection() {
            m_EmaBounceProjectionBar = -1;
            m_EmaBounceProjectionDirection = 0;
            ClearEmaBounceProjectionLines();
        }

        private void ClearPendingEntry() {
            m_BuyOrderActive = m_SellOrderActive = false;
            m_ActiveEntrySetup = EEntrySetup.None;
            m_StopPrice = m_LastSentPrice = 0;
            ClearProjectedEntryLine();
        }

        private void StartShiftProjectionEntry(double tickSize) {
            if (StrategyInfo.MarketPosition != 0) return;

            // Shift replaces a pending entry with a single manually requested
            // projection. It does not arm the automatic pin/EMA setups.
            ClearEmergencyIndicator();
            m_KillModeActive = false;
            m_FlattenRequested = false;
            CancelWorkingEntryOrders();
            ClearPendingEntry();
            ClearPinBarProjectionLines();
            ClearEmaBounceProjectionLines();
            m_ShiftProjectionActive = true;
            m_ShiftProjectionBar = Bars.CurrentBar;
            UpdateShiftProjectionEntry(tickSize, StrategyInfo.MarketPosition);
            UpdateProjectedEntryLine();
        }

        private void UpdateShiftProjectionEntry(double tickSize, int currentPosition) {
            if (!m_ShiftProjectionActive) return;
            if (currentPosition != 0 || m_ShiftProjectionBar != Bars.CurrentBar) {
                ClearShiftProjectionEntry();
                return;
            }

            int direction = m_FastEMA[0] > m_SlowEMA[0] ? 1 :
                            m_FastEMA[0] < m_SlowEMA[0] ? -1 : 0;
            if (direction == 0) {
                if (m_ActiveEntrySetup == EEntrySetup.ShiftProjection) {
                    CancelWorkingEntryOrders();
                    ClearPendingEntry();
                }
                return;
            }

            double rangeTicks = GetActiveRangeTicks(tickSize);
            double range = rangeTicks * tickSize;
            // Anchor the projected range to the live extreme in the intended
            // direction. This starts at the bar open, then moves with the
            // forming bar just like a pin projection instead of staying fixed
            // at the original open price.
            double projectedLow;
            double projectedHigh;
            if (direction > 0) {
                projectedLow = RoundToTick(Bars.Low[0], tickSize);
                projectedHigh = RoundToTick(projectedLow + range, tickSize);
            } else {
                projectedHigh = RoundToTick(Bars.High[0], tickSize);
                projectedLow = RoundToTick(projectedHigh - range, tickSize);
            }
            double entryPrice = direction > 0
                ? projectedHigh + (EntryOffsetTicks * tickSize)
                : projectedLow - (EntryOffsetTicks * tickSize);

            bool buyDirection = direction > 0;
            if (m_ActiveEntrySetup == EEntrySetup.ShiftProjection &&
                ((m_BuyOrderActive && !buyDirection) ||
                 (m_SellOrderActive && buyDirection)))
                CancelWorkingEntryOrders();

            m_ActiveEntrySetup = EEntrySetup.ShiftProjection;
            m_BuyOrderActive = buyDirection;
            m_SellOrderActive = !buyDirection;
            m_StopPrice = RoundToTick(entryPrice, tickSize);
            m_LastSentPrice = 0;
            UpdateShiftRangeLines(projectedLow, projectedHigh, direction);
        }

        private void ClearShiftProjectionEntry() {
            if (m_ActiveEntrySetup == EEntrySetup.ShiftProjection &&
                StrategyInfo.MarketPosition == 0) {
                CancelWorkingEntryOrders();
                ClearPendingEntry();
            }
            m_ShiftProjectionActive = false;
            m_ShiftProjectionBar = -1;
        }

        private void CancelWorkingEntryOrders() {
            var tradeManager = TradeManager;
            if (tradeManager == null || tradeManager.TradingData == null ||
                tradeManager.TradingData.Orders == null) return;

            try {
                tradeManager.ProcessEvents();
                var orders = tradeManager.TradingData.Orders.Items;
                if (orders == null) return;

                foreach (var order in orders) {
                    if (!IsThisStrategyOrder(order.StrategyName, order.Name) ||
                        (order.Name != "RangeBuy" && order.Name != "RangeSell") ||
                        !IsWorkingOrder((int)order.State)) continue;

                    foreach (var tradingProfile in tradeManager.TradingProfiles) {
                        if (!string.Equals(tradingProfile.Name, order.Profile,
                                           StringComparison.OrdinalIgnoreCase)) continue;
                        tradingProfile.CancelOrder(order.OrderID);
                        break;
                    }
                }
            } catch (Exception ex) {
                Output.WriteLine("RangeBarTrading entry cancel error: " + ex.Message);
            }
        }

        private double RoundToTick(double price, double tickSize) {
            return Math.Round(price / tickSize) * tickSize;
        }

        private double RoundDownToTick(double price, double tickSize) {
            return Math.Floor(price / tickSize) * tickSize;
        }

        private double RoundUpToTick(double price, double tickSize) {
            return Math.Ceiling(price / tickSize) * tickSize;
        }

        private void UpdatePinBarProjectionLine(ref ITrendLineObject line,
                                                double price, int direction,
                                                bool active) {
            ChartPoint begin = new ChartPoint(Bars.Time[0], price);
            ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), price);
            if (line == null) {
                line = DrwTrendLine.Create(begin, end);
                line.ExtRight = false;
            } else {
                line.Begin = begin;
                line.End = end;
            }
            line.Color = active
                ? (direction > 0 ? Color.DodgerBlue : Color.OrangeRed)
                : Color.Gray;
            line.Style = ETLStyle.ToolDashed;
            line.Size = 2;
        }

        private void ClearPinBarProjectionLines() {
            if (m_PinBarLowerLine != null) { m_PinBarLowerLine.Delete(); m_PinBarLowerLine = null; }
            if (m_PinBarUpperLine != null) { m_PinBarUpperLine.Delete(); m_PinBarUpperLine = null; }
            if (m_PinBarLabel != null) { m_PinBarLabel.Delete(); m_PinBarLabel = null; }
        }

        private void UpdatePinBarProjectionLabel(double price, int direction,
                                                 bool active) {
            // Keep the label inside the visible pane by extending its text left
            // from the current bar rather than placing it at a future time.
            ChartPoint point = new ChartPoint(Bars.Time[0], price);
            if (m_PinBarLabel == null) {
                m_PinBarLabel = DrwText.Create(point, "pinbar");
                m_PinBarLabel.Size = 10;
                m_PinBarLabel.HStyle = ETextStyleH.Right;
                m_PinBarLabel.VStyle = ETextStyleV.Above;
            }
            m_PinBarLabel.Location = point;
            m_PinBarLabel.Text = "pinbar";
            m_PinBarLabel.Color = active
                ? (direction > 0 ? Color.DodgerBlue : Color.OrangeRed)
                : Color.Gray;
        }

        private bool IsF12Held(Keys eventKeys) {
            if ((eventKeys & Keys.KeyCode) == Keys.F12) return true;
            try {
                return (GetAsyncKeyState((int)Keys.F12) & 0x8000) != 0;
            } catch {
                return false;
            }
        }

        private void AdvanceProfitTargetOneRange(double tickSize) {
            int currentPosition = StrategyInfo.MarketPosition;
            if (currentPosition == 0 || m_ProfitTargetPrice <= 0) return;

            double rangeTicks = GetActiveRangeTicks(tickSize);
            if (rangeTicks <= 0) return;

            // Advance farther in the profitable direction: up for a long and
            // down for a short. With the default five-tick range, each Alt-click
            // moves the target exactly five ticks.
            double direction = currentPosition > 0 ? 1.0 : -1.0;
            m_ProfitTargetPrice = Math.Round((m_ProfitTargetPrice + (direction * rangeTicks * tickSize)) / tickSize) * tickSize;
            m_DraggingTarget = false;
            UpdateTargetLine();
            SubmitActiveExitOrders(currentPosition);
        }

        private void UpdateAutoProtectiveStopOnOneBarProfit(int currentPosition,
                                                             double tickSize) {
            if (!AutoProtectiveStopOn1BarProfit || m_AutoProtectiveStopMoved ||
                currentPosition == 0) return;

            double entryPrice = StrategyInfo.AvgEntryPrice != 0
                ? StrategyInfo.AvgEntryPrice
                : Bars.Close[0];
            double confirmationTicks = GetActiveRangeTicks(tickSize) + 1;
            double confirmationDistance = confirmationTicks * tickSize;
            double tolerance = tickSize * 0.1;
            bool oneBarProfitConfirmed = currentPosition > 0
                ? Bars.High[0] >= entryPrice + confirmationDistance - tolerance
                : Bars.Low[0] <= entryPrice - confirmationDistance + tolerance;
            if (!oneBarProfitConfirmed) return;

            // For range-bar trading, the "break-even" stop intentionally
            // allows one tick of loss. Do not loosen a stop the trader has
            // already moved to a more favorable price.
            double breakEvenStop = currentPosition > 0
                ? RoundToTick(entryPrice - tickSize, tickSize)
                : RoundToTick(entryPrice + tickSize, tickSize);
            if (currentPosition > 0) {
                if (m_ProtectiveStopPrice <= 0 ||
                    m_ProtectiveStopPrice < breakEvenStop)
                    m_ProtectiveStopPrice = breakEvenStop;
            } else {
                if (m_ProtectiveStopPrice <= 0 ||
                    m_ProtectiveStopPrice > breakEvenStop)
                    m_ProtectiveStopPrice = breakEvenStop;
            }

            m_AutoProtectiveStopMoved = true;
            UpdateStopLine();
        }

        private void DrawFilledEntryMarkers(int currentPosition, double entryPrice,
                                            double tickSize, EEntrySetup entrySetup) {
            // MultiCharts confirms the fill on the calculation following the
            // actual fill bar.  Use that completed bar for both markers rather
            // than Bars[0], which may already be a new forming range bar.
            int barsBack = Bars.CurrentBar > 1 ? 1 : 0;

            // Leave one tick of white space beyond the tail.  This keeps the
            // arrow close to, but never inside, the entry candle.
            double tailOffset = tickSize;
            double directionPrice = currentPosition > 0
                ? Bars.Low[barsBack] - tailOffset
                : Bars.High[barsBack] + tailOffset;
            IArrowObject directionMarker = DrwArrow.Create(
                new ChartPoint(Bars.Time[barsBack], directionPrice), currentPosition < 0);
            // The result is not known until the position closes.  It is then
            // changed to green for profit or red for a loss/break-even trade.
            directionMarker.Color = Color.DimGray;
            directionMarker.Size = 5;
            m_TradeEntryMarkers.Add(directionMarker);
            m_ActiveTradeEntryArrow = directionMarker;

            string entryType = entrySetup == EEntrySetup.PinBar ? "PB" :
                               entrySetup == EEntrySetup.Ema24Bounce ? "B" :
                               entrySetup == EEntrySetup.ShiftProjection ? "M" :
                               "M";
            // "Below" means directly underneath the arrow for both long and
            // short entries, rather than above a short-entry arrow.
            double entryTypePrice = directionPrice - (2 * tickSize);
            ITextObject entryTypeMarker = DrwText.Create(
                new ChartPoint(Bars.Time[barsBack], entryTypePrice), entryType);
            entryTypeMarker.Color = Color.Black;
            entryTypeMarker.Size = 6;
            entryTypeMarker.HStyle = ETextStyleH.Center;
            entryTypeMarker.VStyle = ETextStyleV.Below;
            m_TradeEntryMarkers.Add(entryTypeMarker);

            // A plain ASCII chevron is used here because it renders reliably
            // in MultiCharts chart fonts. The prior bar's time places it just
            // to the left of the fill bar at the actual average execution price.
            ITextObject fillMarker = DrwText.Create(
                new ChartPoint(Bars.Time[barsBack], entryPrice), ">");
            fillMarker.Color = Color.Black;
            fillMarker.HStyle = ETextStyleH.Right;
            fillMarker.Size = 12;
            m_TradeEntryMarkers.Add(fillMarker);
        }

        private void ClearFilledEntryMarkers() {
            foreach (IDrawObject marker in m_TradeEntryMarkers) {
                if (marker != null) marker.Delete();
            }
            m_TradeEntryMarkers.Clear();
            m_ActiveTradeEntryArrow = null;
        }

        private void FinalizeActiveTradeEntryMarker() {
            if (m_ActiveTradeEntryArrow == null) return;

            // A positive closed-equity change is a profitable completed
            // trade.  Break-even is intentionally shown as red, per the
            // requested successful/unsuccessful classification.
            double realizedTradeProfit = StrategyInfo.ClosedEquity - m_ClosedEquityAtEntry;
            m_ActiveTradeEntryArrow.Color = realizedTradeProfit > 0
                ? Color.LimeGreen
                : Color.Red;
            m_ActiveTradeEntryArrow = null;
        }

        private bool IsAltClick(Keys keys) {
            // MultiCharts can report a left/right Alt click as LMenu/RMenu
            // rather than setting the generic Alt modifier bit. Some chart
            // drawing clicks report no modifier in arg.keys at all, so also
            // read the live WinForms modifier state.
            Keys liveModifiers = System.Windows.Forms.Control.ModifierKeys;
            Keys keyCode = keys & Keys.KeyCode;
            Keys liveKeyCode = liveModifiers & Keys.KeyCode;
            return ((keys | liveModifiers) & Keys.Alt) == Keys.Alt ||
                   keyCode == Keys.Menu ||
                   keyCode == Keys.LMenu ||
                   keyCode == Keys.RMenu ||
                   liveKeyCode == Keys.Menu ||
                   liveKeyCode == Keys.LMenu ||
                   liveKeyCode == Keys.RMenu;
        }

        private bool IsShiftClick(Keys keys) {
            // MultiCharts does not always include Shift in arg.keys, so use
            // the live Windows modifier state as a fallback.
            Keys liveModifiers = System.Windows.Forms.Control.ModifierKeys;
            Keys keyCode = keys & Keys.KeyCode;
            Keys liveKeyCode = liveModifiers & Keys.KeyCode;
            return ((keys | liveModifiers) & Keys.Shift) == Keys.Shift ||
                   keyCode == Keys.ShiftKey ||
                   liveKeyCode == Keys.ShiftKey;
        }

        private void UpdateTargetLine() {
            if (m_ProfitTargetPrice <= 0) return;

            // This is deliberately a short, thick control line beside the
            // current price action—not an extension into the chart's right edge
            // where MultiCharts draws its native target-order badge.
            ChartPoint begin = new ChartPoint(GetTradeControlStartTime(), m_ProfitTargetPrice);
            ChartPoint end = new ChartPoint(Bars.Time[0], m_ProfitTargetPrice);
            if (m_TargetLine == null) {
                m_TargetLine = DrwTrendLine.Create(begin, end);
                m_TargetLine.ExtRight = false;
            } else {
                m_TargetLine.Begin = begin;
                m_TargetLine.End = end;
            }
            m_TargetLine.Color = Color.LimeGreen;
            m_TargetLine.Style = ETLStyle.ToolSolid;
            m_TargetLine.Size = 4;

        }

        private void UpdateStopLine() {
            if (m_ProtectiveStopPrice <= 0) return;

            ChartPoint begin = new ChartPoint(GetTradeControlStartTime(), m_ProtectiveStopPrice);
            ChartPoint end = new ChartPoint(Bars.Time[0], m_ProtectiveStopPrice);
            if (m_StopLine == null) {
                m_StopLine = DrwTrendLine.Create(begin, end);
                m_StopLine.ExtRight = false;
            } else {
                m_StopLine.Begin = begin;
                m_StopLine.End = end;
            }
            m_StopLine.Color = Color.Red;
            m_StopLine.Style = ETLStyle.ToolSolid;
            m_StopLine.Size = 4;

        }

        private void SubmitActiveExitOrders(int currentPosition) {
            if (currentPosition > 0) {
                if (m_ProtectiveStopPrice > 0) m_BuyExitStop.Send(m_ProtectiveStopPrice);
                if (m_ProfitTargetPrice > 0) m_BuyExitLimit.Send(m_ProfitTargetPrice);
            } else if (currentPosition < 0) {
                if (m_ProtectiveStopPrice > 0) m_SellExitStop.Send(m_ProtectiveStopPrice);
                if (m_ProfitTargetPrice > 0) m_SellExitLimit.Send(m_ProfitTargetPrice);
            }
        }

        private DateTime GetTradeControlStartTime() {
            int barsBack = Math.Min(6, Math.Max(0, Bars.CurrentBar - 1));
            return Bars.Time[barsBack];
        }

        private void SetTargetLineSelected(bool selected) {
            if (m_TargetLine != null) m_TargetLine.Color = selected ? Color.Orange : Color.LimeGreen;
        }

        private void SetStopLineSelected(bool selected) {
            if (m_StopLine != null) m_StopLine.Color = selected ? Color.Orange : Color.Red;
        }

        private double GetActiveRangeTicks(double tickSize) {
            if (RangeSizeTicks > 0) return RangeSizeTicks;
            return m_AutoRangeTicks > 0 ? m_AutoRangeTicks : 7;
        }

        private void UpdateProjectedEntryLine() {
            if (m_StopPrice <= 0 || (!m_BuyOrderActive && !m_SellOrderActive)) return;
            ChartPoint begin = new ChartPoint(Bars.Time[0], m_StopPrice);
            ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), m_StopPrice);
            if (m_ProjectedEntryLine == null) {
                m_ProjectedEntryLine = DrwTrendLine.Create(begin, end);
                m_ProjectedEntryLine.Color = Color.DodgerBlue;
                m_ProjectedEntryLine.Style = ETLStyle.ToolDashed;
                m_ProjectedEntryLine.Size = 2;
                m_ProjectedEntryLine.ExtRight = true;
            } else {
                // Move the existing object instead of deleting/recreating it on
                // every tick. This keeps chart rendering out of the order path.
                m_ProjectedEntryLine.Begin = begin;
                m_ProjectedEntryLine.End = end;
            }

            // Shift-click intentionally works while automatic entry is
            // unarmed.  Put that fact directly on its projected order line so
            // a manual order cannot be mistaken for an armed auto entry.
            bool isUnarmedShiftEntry = m_ActiveEntrySetup == EEntrySetup.ShiftProjection &&
                                       !m_AutoEntryArmed;
            if (isUnarmedShiftEntry) {
                string labelText = m_BuyOrderActive
                    ? "UNARMED SHIFT BUY"
                    : "UNARMED SHIFT SELL";
                if (m_ProjectedEntryLabel == null) {
                    m_ProjectedEntryLabel = DrwText.Create(begin, labelText);
                    m_ProjectedEntryLabel.Size = 10;
                    m_ProjectedEntryLabel.HStyle = ETextStyleH.Right;
                }
                m_ProjectedEntryLabel.Location = begin;
                m_ProjectedEntryLabel.Text = labelText;
                m_ProjectedEntryLabel.Color = Color.Black;
                m_ProjectedEntryLabel.VStyle = m_BuyOrderActive
                    ? ETextStyleV.Above
                    : ETextStyleV.Below;
            } else if (m_ProjectedEntryLabel != null) {
                m_ProjectedEntryLabel.Delete();
                m_ProjectedEntryLabel = null;
            }
        }

        private void ClearProjectedEntryLine() {
            if (m_ProjectedEntryLine != null) { m_ProjectedEntryLine.Delete(); m_ProjectedEntryLine = null; }
            if (m_ProjectedEntryLabel != null) { m_ProjectedEntryLabel.Delete(); m_ProjectedEntryLabel = null; }
            ClearShiftCompletionLine();
        }

        private void UpdateShiftRangeLines(double projectedLow, double projectedHigh,
                                           int direction) {
            ChartPoint lowerBegin = new ChartPoint(Bars.Time[0], projectedLow);
            ChartPoint lowerEnd = new ChartPoint(Bars.Time[0].AddMinutes(5), projectedLow);
            if (m_ShiftLowerLine == null) {
                m_ShiftLowerLine = DrwTrendLine.Create(lowerBegin, lowerEnd);
                m_ShiftLowerLine.ExtRight = false;
                m_ShiftLowerLine.Color = Color.DarkGray;
                m_ShiftLowerLine.Style = ETLStyle.ToolDashed;
                m_ShiftLowerLine.Size = 2;
            } else {
                m_ShiftLowerLine.Begin = lowerBegin;
                m_ShiftLowerLine.End = lowerEnd;
            }

            ChartPoint upperBegin = new ChartPoint(Bars.Time[0], projectedHigh);
            ChartPoint upperEnd = new ChartPoint(Bars.Time[0].AddMinutes(5), projectedHigh);
            if (m_ShiftUpperLine == null) {
                m_ShiftUpperLine = DrwTrendLine.Create(upperBegin, upperEnd);
                m_ShiftUpperLine.ExtRight = false;
                m_ShiftUpperLine.Color = Color.DarkGray;
                m_ShiftUpperLine.Style = ETLStyle.ToolDashed;
                m_ShiftUpperLine.Size = 2;
            } else {
                m_ShiftUpperLine.Begin = upperBegin;
                m_ShiftUpperLine.End = upperEnd;
            }

            ChartPoint completionPoint = direction > 0 ? upperBegin : lowerBegin;
            if (m_ShiftCompletionLabel == null) {
                m_ShiftCompletionLabel = DrwText.Create(completionPoint, "SHIFT COMPLETE");
                m_ShiftCompletionLabel.Size = 9;
                m_ShiftCompletionLabel.HStyle = ETextStyleH.Right;
                m_ShiftCompletionLabel.Color = Color.Black;
            }
            m_ShiftCompletionLabel.Location = completionPoint;
            m_ShiftCompletionLabel.Text = "SHIFT COMPLETE";
            m_ShiftCompletionLabel.VStyle = direction > 0
                ? ETextStyleV.Below
                : ETextStyleV.Above;
        }

        private void ClearShiftCompletionLine() {
            if (m_ShiftLowerLine != null) {
                m_ShiftLowerLine.Delete();
                m_ShiftLowerLine = null;
            }
            if (m_ShiftUpperLine != null) {
                m_ShiftUpperLine.Delete();
                m_ShiftUpperLine = null;
            }
            if (m_ShiftCompletionLabel != null) {
                m_ShiftCompletionLabel.Delete();
                m_ShiftCompletionLabel = null;
            }
        }

        private void UpdateHUD() {
            // Keep the established per-signal/session calculation intact, but
            // publish it so the bid and ask chart instances display one total.
            double pnl = UpdateAndGetGlobalPnL(StrategyInfo.OpenEquity);
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale; if (tickSize <= 0) tickSize = 0.25;
            string setupScope = EnablePinBarTrading && Enable24EMABounceTrading
                ? "PIN + 24 EMA WATCH"
                : EnablePinBarTrading
                    ? "PIN WATCH"
                    : Enable24EMABounceTrading ? "24 EMA WATCH" : "IDLE";
            // Always state the automation state explicitly.  The prior
            // watch-only text was ambiguous: it did not tell the trader that
            // automatic entries were still unarmed.
            string status = "UNARMED | " + setupScope;
            if (m_AutoEntryArmed)
                status = m_ArmedDirection > 0 ? "ARMED BUY" : "ARMED SELL";
            if (m_BuyOrderActive)
                status = m_ActiveEntrySetup == EEntrySetup.Ema24Bounce ? "24 EMA ENTRY BUY" :
                    m_ActiveEntrySetup == EEntrySetup.ShiftProjection
                        ? (m_AutoEntryArmed ? "SHIFT ENTRY BUY" : "UNARMED SHIFT ENTRY BUY")
                        : "PIN ENTRY BUY";
            if (m_SellOrderActive)
                status = m_ActiveEntrySetup == EEntrySetup.Ema24Bounce ? "24 EMA ENTRY SELL" :
                    m_ActiveEntrySetup == EEntrySetup.ShiftProjection
                        ? (m_AutoEntryArmed ? "SHIFT ENTRY SELL" : "UNARMED SHIFT ENTRY SELL")
                        : "PIN ENTRY SELL";
            if (StrategyInfo.MarketPosition != 0) status = "IN TRADE";
            if (m_KillModeActive) status = m_FlattenRequested ? "FLATTENING" : "UNARMED";
            string text = string.Format("{0} | Session PnL: {1:C2}", status, pnl);
            // Keep the session line immediately below the broker line as one
            // compact, unobtrusive status block.
            ChartPoint hudPoint = GetStatusLabelPoint(tickSize, 4);
            if (m_HUDLabel == null) {
                m_HUDLabel = DrwText.Create(hudPoint, text);
            }
            m_HUDLabel.Size = 11;
            // In MultiCharts text drawings, Right keeps the visible left edge
            // at the shared chart point; Left aligns the right edges instead.
            m_HUDLabel.HStyle = ETextStyleH.Right;
            m_HUDLabel.VStyle = ETextStyleV.Above;
            m_HUDLabel.Text = text; m_HUDLabel.Color = Color.Black;
            m_HUDLabel.Location = hudPoint;
            UpdateBrokerStatusLabel(tickSize);
        }

        private ChartPoint GetStatusLabelPoint(double tickSize, int offsetTicks) {
            // Anchor directly to the live bar. A trailing highest-high anchor
            // follows an advance immediately but remains stranded above an old
            // high during a decline. The live high keeps this compact status
            // block moving with price in either direction.
            return new ChartPoint(Bars.Time[0],
                                  Bars.High[0] + (offsetTicks * tickSize));
        }

        private void UpdateBrokerStatusLabel(double tickSize) {
            string text;
            Color color;
            int workingOrders = 0;
            string brokerName = GetBrokerStatusName();
            var tradeManager = TradeManager;
            if (tradeManager == null || tradeManager.TradingData == null ||
                tradeManager.TradingData.Orders == null) {
                text = brokerName + ": TRACKER UNAVAILABLE";
                color = Color.DarkOrange;
            } else {
                try {
                    tradeManager.ProcessEvents();
                    var orders = tradeManager.TradingData.Orders.Items;
                    if (orders != null) {
                        foreach (var order in orders) {
                            if (!IsThisStrategyOrder(order.StrategyName, order.Name)) continue;
                            RememberStrategyBrokerScope(order.Profile, order.Account,
                                                        GetTrackerSymbol(order));
                            if (IsWorkingOrder((int)order.State)) workingOrders++;
                        }
                    }

                    bool brokerPositionAvailable;
                    int brokerPosition = GetBrokerPositionForStrategy(
                        out brokerPositionAvailable);
                    if (!brokerPositionAvailable) {
                        int strategyPosition = StrategyInfo.MarketPosition;
                        if (strategyPosition > 0) {
                            text = string.Format(
                                "{0}: LONG {1} FILLED (SIGNAL) | {2} WORKING | SCOPE PENDING",
                                brokerName, strategyPosition, workingOrders);
                            color = Color.Navy;
                        } else if (strategyPosition < 0) {
                            text = string.Format(
                                "{0}: SHORT {1} FILLED (SIGNAL) | {2} WORKING | SCOPE PENDING",
                                brokerName, Math.Abs(strategyPosition), workingOrders);
                            color = Color.Maroon;
                        } else {
                            text = string.Format("{0}: SCOPE PENDING | {1} WORKING",
                                                 brokerName, workingOrders);
                            color = Color.Black;
                        }
                    } else if (brokerPosition > 0) {
                        text = string.Format("{0}: LONG {1} | {2} WORKING",
                                             brokerName, brokerPosition, workingOrders);
                        color = Color.Navy;
                    } else if (brokerPosition < 0) {
                        text = string.Format("{0}: SHORT {1} | {2} WORKING",
                                             brokerName, Math.Abs(brokerPosition), workingOrders);
                        color = Color.Maroon;
                    } else {
                        text = string.Format("{0}: FLAT | {1} WORKING",
                                             brokerName, workingOrders);
                        color = Color.Black;
                    }
                } catch (Exception ex) {
                    Output.WriteLine("RangeBarTrading broker-status error: " + ex.Message);
                    text = brokerName + ": TRACKER ERROR";
                    color = Color.Red;
                }
            }

            ChartPoint point = GetStatusLabelPoint(tickSize, 7);
            if (m_BrokerStatusLabel == null) {
                m_BrokerStatusLabel = DrwText.Create(point, text);
            }
            // Match the HUD so the two lines read as a single status block.
            m_BrokerStatusLabel.Size = 11;
            m_BrokerStatusLabel.HStyle = ETextStyleH.Right;
            m_BrokerStatusLabel.VStyle = ETextStyleV.Above;
            m_BrokerStatusLabel.Location = point;
            m_BrokerStatusLabel.Text = text;
            m_BrokerStatusLabel.Color = color;
        }

        private string GetBrokerStatusName() {
            if (!string.IsNullOrEmpty(m_StrategyBrokerProfile))
                return m_StrategyBrokerProfile.ToUpperInvariant();

            var tradeManager = TradeManager;
            if (tradeManager == null || tradeManager.TradingProfiles == null)
                return "BROKER";
            foreach (var tradingProfile in tradeManager.TradingProfiles) {
                if (tradingProfile == null || string.IsNullOrEmpty(tradingProfile.Name))
                    continue;
                if (tradingProfile.Name.IndexOf("paper", StringComparison.OrdinalIgnoreCase) >= 0)
                    return tradingProfile.Name.ToUpperInvariant();
            }
            return tradeManager.TradingProfiles.Length == 1 &&
                   !string.IsNullOrEmpty(tradeManager.TradingProfiles[0].Name)
                ? tradeManager.TradingProfiles[0].Name.ToUpperInvariant()
                : "BROKER";
        }

        private double GetAngle(double valCurrent, double valOld, int barsBack, double tickSize) {
            double rise = valCurrent - valOld;
            double run = (double)barsBack * tickSize; 
            return Math.Atan2(rise, run) * (180.0 / Math.PI);
        }

        protected override void Destroy() {
            RemoveGlobalPnLContributor();
            ClearTradingDrawings();
            ClearFilledEntryMarkers();
        }

        private double UpdateAndGetGlobalPnL(double localPnL) {
            lock (s_GlobalPnLLock) {
                s_GlobalPnLContributors[this] = localPnL;

                double totalPnL = 0;
                foreach (double contributorPnL in s_GlobalPnLContributors.Values)
                    totalPnL += contributorPnL;
                return totalPnL;
            }
        }

        private void RemoveGlobalPnLContributor() {
            lock (s_GlobalPnLLock) {
                s_GlobalPnLContributors.Remove(this);
            }
        }
    }
}
