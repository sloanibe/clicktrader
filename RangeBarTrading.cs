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
        private static RangeBarTradingV3 s_LastActiveChart;

        // The only user-facing strategy settings.
        [Input] public int RangeSizeTicks { get; set; }
        [Input] public int ProtectiveStopLossTicks { get; set; }
        [Input] public int ProfitTargetTicks { get; set; }
        // Retained in its original input slot to preserve saved MultiCharts
        // settings. Automatic break-even is intentionally no longer used.
        [Input] public bool AutoProtectiveStopOn1BarProfit { get; set; }
        [Input] public bool EnablePinBarTrading { get; set; }
        [Input] public bool Enable24EMABounceTrading { get; set; }
        // Set true on the ask chart and false on the bid chart. Entry routing
        // is restricted to the corresponding buy-only or sell-only direction.
        [Input] public bool IsAskChart { get; set; }
        // When enabled, every trade exits on a completed opposite-color bar
        // instead of using either the normal or recovery profit target.
        [Input] public bool UseOppositeColorExitForProfits { get; set; }

        // Fixed internal behavior; these are intentionally not exposed in the
        // Strategy Properties dialog.
        private const int OrderQuantity = 1;
        private const int ProximityTicks = 5;
        private const int RecoverySessionProfitGoalTicks = 5;
        private const int OppositeColorExitMinimumProfitTicks = 5;
        private const bool ShowHUD = true;
        private const int PinBarRangeTicks = 5;
        private const int PinBarMinTailTicks = 2;
        private const int PinBarMinEmaSeparationTicks = 3;
        private const double PinBarMinFastEmaSlopeDegrees = 20.0;
        private const int MasterTrendPeriod = 60;
        private const int MinExpansionTicks = 25;
        private const int MinBreadth_15_60 = 5;
        private const int MinBreadth_5_15 = 4;

        private IOrderPriced m_BuyStop;
        private IOrderPriced m_SellStop;
        private IOrderMarket m_BuyEntryThisBar;
        private IOrderMarket m_SellEntryThisBar;
        private IOrderPriced m_BuyExitStop;
        private IOrderPriced m_SellExitStop;
        private IOrderPriced m_ColorTrailLongStop;
        private IOrderPriced m_ColorTrailShortStop;
        private IOrderPriced m_BuyExitLimit;
        private IOrderPriced m_SellExitLimit;
        private IOrderMarket m_CloseLongNextBar;
        private IOrderMarket m_CloseShortNextBar;
        private IOrderMarket m_ColorCloseLongNextBar;
        private IOrderMarket m_ColorCloseShortNextBar;

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
        private bool m_RecoveryModeActive = false;
        private bool m_ColorCloseExitRequested = false;
        private bool m_OppositeColorProfitArmed = false;
        private double m_RecoverySessionPnlAtEntry = 0;
        private double m_OppositeColorStopPrice = 0;
        private bool m_HasFlatSessionPnlSnapshot = false;
        private double m_FlatSessionPnlSnapshot = 0;
        
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
        private int m_PinEntryCandidateBodyTicks = 0;
        private int m_StagedPinBodyTicks = 0;
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
        private bool m_HudDisplayEnabled = true;
        private double m_AutoRangeTicks = 0;
        private DateTime m_EmergencyMessageExpiresAt = DateTime.MinValue;
        private readonly List<int> m_EmergencyCancelOrderIds = new List<int>();
        private bool m_EmergencyCancellationPending = false;
        private int m_EntryMarketOrderBar = -1;
        private string m_StrategyBrokerProfile = string.Empty;
        private string m_StrategyBrokerAccount = string.Empty;
        private string m_StrategyBrokerSymbol = string.Empty;
        
        private ITrendLineObject m_TargetLine;
        private ITrendLineObject m_StopLine;
        private ITrendLineObject m_RecoveryColorStopLine;
        private ITextObject m_RecoveryColorStopLabel;
        private ITrendLineObject m_ProjectedEntryLine;
        private ITextObject m_ProjectedEntryLabel;
        private ITrendLineObject m_ShiftTailLine;
        private ITrendLineObject m_ShiftCompletionLine;
        private ITrendLineObject m_PinBarTailLine;
        private ITrendLineObject m_PinBarCompletionLine;
        private ITextObject m_PinBarLabel;
        private ITrendLineObject m_EmaBounceTailLine;
        private ITrendLineObject m_EmaBounceCompletionLine;
        private ITextObject m_EmaBounceLabel;
        private ITrendLineObject m_GoSignalMarker;
        private ITextObject m_HUDLabel;
        private ITextObject m_BrokerStatusLabel;
        private ITextObject m_ControlsHintLabel;
        private ITextObject m_ControlsActionHintLabel;
        private ITextObject m_EmergencyLabel;
        // Filled-trade annotations are retained after the position closes so
        // the chart keeps a clean record of executed entries.
        private readonly List<IDrawObject> m_TradeEntryMarkers = new List<IDrawObject>();
        private IArrowObject m_ActiveTradeEntryArrow;

        public RangeBarTradingV3(object ctx) : base(ctx)
        {
            RangeSizeTicks = 5;
            ProtectiveStopLossTicks = 12;
            ProfitTargetTicks = 0;
            AutoProtectiveStopOn1BarProfit = true;
            EnablePinBarTrading = true;
            Enable24EMABounceTrading = true;
            IsAskChart = true;
            UseOppositeColorExitForProfits = true;
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this); m_SlowEMA = new XAverage(this); m_MasterEMA = new XAverage(this);
            m_BuyStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "RangeBuy", EOrderAction.Buy));
            m_SellStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "RangeSell", EOrderAction.SellShort));
            m_BuyEntryThisBar = OrderCreator.MarketThisBar(new SOrderParameters(Contracts.Default, "RangeBuyClose", EOrderAction.Buy));
            m_SellEntryThisBar = OrderCreator.MarketThisBar(new SOrderParameters(Contracts.Default, "RangeSellClose", EOrderAction.SellShort));
            // Match the proven RenkoTailTrading exit-order construction. These
            // actions are only emitted while the matching position is active.
            m_BuyExitStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectLong", EOrderAction.Sell));
            m_SellExitStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ProtectShort", EOrderAction.BuyToCover));
            m_ColorTrailLongStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ColorTrailLong", EOrderAction.Sell));
            m_ColorTrailShortStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ColorTrailShort", EOrderAction.BuyToCover));
            m_BuyExitLimit = OrderCreator.Limit(new SOrderParameters(Contracts.Default, "ProfitLong", EOrderAction.Sell));
            m_SellExitLimit = OrderCreator.Limit(new SOrderParameters(Contracts.Default, "ProfitShort", EOrderAction.BuyToCover));
            m_CloseLongNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "EmergLong", EOrderAction.Sell));
            m_CloseShortNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "EmergShort", EOrderAction.BuyToCover));
            m_ColorCloseLongNextBar = OrderCreator.MarketThisBar(new SOrderParameters(Contracts.Default, "ColorCloseLong", EOrderAction.Sell));
            m_ColorCloseShortNextBar = OrderCreator.MarketThisBar(new SOrderParameters(Contracts.Default, "ColorCloseShort", EOrderAction.BuyToCover));
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
            if (m_ControlsHintLabel != null) { m_ControlsHintLabel.Delete(); m_ControlsHintLabel = null; }
            if (m_ControlsActionHintLabel != null) { m_ControlsActionHintLabel.Delete(); m_ControlsActionHintLabel = null; }
            if (m_TargetLine != null) { m_TargetLine.Delete(); m_TargetLine = null; }
            if (m_StopLine != null) { m_StopLine.Delete(); m_StopLine = null; }
            ClearRecoveryColorStopProjection();
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

            if (m_EntryMarketOrderBar != Bars.CurrentBar)
                m_EntryMarketOrderBar = -1;

            if (Bars.Status == EBarState.Close || m_AutoRangeTicks <= 0) m_AutoRangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            // Do not create drawings or query the broker during historical
            // calculation.  MultiCharts can remain stuck in "Calculating" if
            // the order tracker is touched in that pass.
            if (!Environment.IsRealTimeCalc) return;

            RefreshEmergencyCancellationStatus();
            if (m_EmergencyLabel != null && DateTime.Now >= m_EmergencyMessageExpiresAt)
                ClearEmergencyIndicator();

            int currentPosition = StrategyInfo.MarketPosition;
            if (currentPosition == 0) {
                // Preserve the shared cumulative result before an entry fills.
                // Reading OpenEquity after the fill can include the spread and
                // incorrectly classify a flat/positive session as recovery.
                m_FlatSessionPnlSnapshot =
                    UpdateAndGetGlobalPnL(StrategyInfo.OpenEquity);
                m_HasFlatSessionPnlSnapshot = true;
            }

            // A chart-role change must also cancel an already-staged entry
            // that is no longer permitted before it can be transmitted.
            if (currentPosition == 0 &&
                ((m_BuyOrderActive && !IsEntryDirectionAllowed(1)) ||
                 (m_SellOrderActive && !IsEntryDirectionAllowed(-1)))) {
                CancelWorkingEntryOrders();
                ClearPendingEntry();
            }

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
                if (ShowHUD && m_HudDisplayEnabled) UpdateHUD();
                return;
            }

            if (currentPosition != 0 && m_LastMarketPosition == 0) {
                double entryPrice = StrategyInfo.AvgEntryPrice != 0 ? StrategyInfo.AvgEntryPrice : Bars.Close[0];
                double stopDist = ProtectiveStopLossTicks * tickSize;
                double sessionPnlAtEntry = m_HasFlatSessionPnlSnapshot
                    ? m_FlatSessionPnlSnapshot
                    : UpdateAndGetGlobalPnL(StrategyInfo.OpenEquity);
                int activeProfitTargetTicks = GetProfitTargetTicksForNewTrade(
                    sessionPnlAtEntry, tickSize);
                double targetDist = activeProfitTargetTicks * tickSize;
                EEntrySetup filledEntrySetup = m_ActiveEntrySetup;
                int filledPinBodyTicks = m_StagedPinBodyTicks;
                m_ClosedEquityAtEntry = StrategyInfo.ClosedEquity;
                m_RecoverySessionPnlAtEntry = sessionPnlAtEntry;
                m_RecoveryModeActive = sessionPnlAtEntry < 0;
                m_OppositeColorStopPrice = 0;
                ClearRecoveryColorStopProjection();

                if (currentPosition > 0) {
                    m_ProtectiveStopPrice = ProtectiveStopLossTicks > 0 ? entryPrice - stopDist : 0;
                    m_ProfitTargetPrice = activeProfitTargetTicks > 0 ? entryPrice + targetDist : 0;
                } else {
                    m_ProtectiveStopPrice = ProtectiveStopLossTicks > 0 ? entryPrice + stopDist : 0;
                    m_ProfitTargetPrice = activeProfitTargetTicks > 0 ? entryPrice - targetDist : 0;
                }
                m_BuyOrderActive = m_SellOrderActive = false; m_StopPrice = m_LastSentPrice = 0;
                // A confirmed fill consumes the armed state immediately. Do
                // not enter kill mode here: the open trade and all of its exit
                // management must continue normally, but no subsequent entry
                // may be staged without another deliberate arm action.
                m_AutoEntryArmed = false;
                m_ArmedDirection = 0;
                m_ColorCloseExitRequested = false;
                m_OppositeColorProfitArmed = false;
                m_ActiveEntrySetup = EEntrySetup.None;
                m_EmaBounceOrderBar = -1;
                m_PinBarOrderBar = -1;
                m_ShiftProjectionActive = false;
                m_ShiftProjectionBar = -1;
                if (m_GoSignalMarker != null) m_GoSignalMarker.Delete();
                ClearProjectedEntryLine(); UpdateTargetLine(); UpdateStopLine();
                DrawFilledEntryMarkers(currentPosition, entryPrice, tickSize,
                                       filledEntrySetup, filledPinBodyTicks);
                m_StagedPinBodyTicks = 0;
            }

            UpdateOppositeColorExitManagement(currentPosition, tickSize);

            if (currentPosition == 0) {
                if (m_BuyOrderActive && m_StopPrice > 0 &&
                    IsEntryDirectionAllowed(1)) {
                    if (Bars.Status == EBarState.Close &&
                        m_EntryMarketOrderBar != Bars.CurrentBar &&
                        HasReachedEntryCompletion(1, tickSize)) {
                        CancelWorkingEntryOrders();
                        m_BuyEntryThisBar.Send();
                        m_EntryMarketOrderBar = Bars.CurrentBar;
                        m_BuyOrderActive = m_SellOrderActive = false;
                        m_ActiveEntrySetup = EEntrySetup.None;
                        m_StopPrice = m_LastSentPrice = 0;
                    }
                    UpdateProjectedEntryLine();
                } else if (m_SellOrderActive && m_StopPrice > 0 &&
                           IsEntryDirectionAllowed(-1)) {
                    if (Bars.Status == EBarState.Close &&
                        m_EntryMarketOrderBar != Bars.CurrentBar &&
                        HasReachedEntryCompletion(-1, tickSize)) {
                        CancelWorkingEntryOrders();
                        m_SellEntryThisBar.Send();
                        m_EntryMarketOrderBar = Bars.CurrentBar;
                        m_BuyOrderActive = m_SellOrderActive = false;
                        m_ActiveEntrySetup = EEntrySetup.None;
                        m_StopPrice = m_LastSentPrice = 0;
                    }
                    UpdateProjectedEntryLine();
                } else if (m_BuyOrderActive || m_SellOrderActive) {
                    // Final transmission guard: no future entry path can send
                    // a stop on the chart's prohibited side.
                    CancelWorkingEntryOrders();
                    ClearPendingEntry();
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
                m_RecoveryModeActive = false;
                m_ColorCloseExitRequested = false;
                m_OppositeColorProfitArmed = false;
                m_RecoverySessionPnlAtEntry = 0;
                m_OppositeColorStopPrice = 0;
                ClearTradingDrawings(); 
            }
            m_LastMarketPosition = currentPosition;
            if (ShowHUD && m_HudDisplayEnabled) UpdateHUD();
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
            MarkChartActive();

            // Escape plus left-click is an alternate emergency control for
            // chart configurations that consume the middle-wheel click.
            if (arg.buttons == MouseButtons.Left &&
                IsEscapeHeld(arg.keys)) {
                m_HudDisplayEnabled = true;
                ActivateEmergencyFlatten(true);
                if (ShowHUD) UpdateHUD();
                return;
            }

            // Some MultiCharts chart configurations omit F12 from arg.keys,
            // so also check its physical Windows key state.
            if (arg.buttons == MouseButtons.Left &&
                IsF12Held(arg.keys)) {
                // Emergency flatten is also the explicit safety reset: make
                // the HUD visible again so the resulting UNARMED state is
                // immediately observable.
                m_HudDisplayEnabled = true;
                ActivateEmergencyFlatten(true);
                if (ShowHUD) UpdateHUD();
                return;
            }

            if (arg.buttons == MouseButtons.Left && IsF11Held(arg.keys)) {
                ToggleHudDisplay();
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
                if (ShowHUD && m_HudDisplayEnabled) UpdateHUD();
            }
            else if (IsShiftClick(arg.keys)) {
                StartShiftProjectionEntry(tickSize);
                if (ShowHUD && m_HudDisplayEnabled) UpdateHUD();
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
            else if (m_ProfitTargetPrice > 0 && Math.Abs(arg.point.Price - m_ProfitTargetPrice) <= (ProximityTicks * tickSize)) {
                m_DraggingTarget = true;
                SetTargetLineSelected(true);
            }
            else if (StrategyInfo.MarketPosition != 0 && m_ProtectiveStopPrice > 0 &&
                     Math.Abs(arg.point.Price - m_ProtectiveStopPrice) <= (ProximityTicks * tickSize)) {
                MoveProtectiveStopToBreakEven(StrategyInfo.MarketPosition, tickSize);
            }
        }

        private void MarkChartActive() {
            RangeBarTradingV3 previous = s_LastActiveChart;
            if (previous == this) return;

            s_LastActiveChart = this;
            if (previous != null) previous.DisarmForChartSwitch();
        }

        private void DisarmForChartSwitch() {
            // Switching chart focus must not flatten an existing position. It
            // only removes a pending entry and prevents the next automatic
            // entry from being sent by the chart that was left behind.
            m_AutoEntryArmed = false;
            m_ArmedDirection = 0;
            m_BuyOrderActive = m_SellOrderActive = false;
            m_ActiveEntrySetup = EEntrySetup.None;
            m_EmaBounceOrderBar = -1;
            m_PinBarOrderBar = -1;
            m_ShiftProjectionActive = false;
            m_ShiftProjectionBar = -1;
            m_StopPrice = m_LastSentPrice = 0;
            CancelWorkingEntryOrders();
            ClearPendingEntry();

            if (StrategyInfo.MarketPosition == 0) {
                m_KillModeActive = true;
                m_FlattenRequested = false;
                ClearTradingDrawings();
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
            if (!IsEntryDirectionAllowed(m_ArmedDirection)) {
                m_AutoEntryArmed = false;
                m_ArmedDirection = 0;
                return;
            }
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
            m_RecoveryModeActive = false;
            m_ColorCloseExitRequested = false;
            m_RecoverySessionPnlAtEntry = 0;
            m_DraggingTarget = false;
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
                   orderName == "RangeBuyClose" || orderName == "RangeSellClose" ||
                   orderName == "ProtectLong" || orderName == "ProtectShort" ||
                   orderName == "ColorTrailLong" || orderName == "ColorTrailShort" ||
                   orderName == "ProfitLong" || orderName == "ProfitShort" ||
                   orderName == "ColorCloseLong" ||
                   orderName == "ColorCloseShort";
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
            // choose one informational pin shape per new bar from the same
            // 24 EMA slope rule.
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

            // Each chart is a one-sided workspace: ask charts display only
            // buy setups and bid charts display only sell setups.
            if (!IsEntryDirectionAllowed(direction)) {
                ClearPinBarProjectionLines();
                ClearPinBarEntryIfActive();
                return;
            }

            // A pin-bar projection is only meaningful when the fast EMA is
            // visibly separated from the 24 EMA and is sloping in the trade
            // direction.  Treat this as a visualization and order-eligibility
            // gate so weak/flat EMA conditions do not present a valid-looking
            // pin setup.
            if (!IsPinBarTrendFilterValid(direction, tickSize)) {
                ClearPinBarProjectionLines();
                ClearPinBarEntryIfActive();
                return;
            }

            // Open alignment is an order-eligibility rule, not a visualization
            // rule. Keep showing the geometric projection after arming even if
            // the EMA-side gate prevents this bar from staging an entry.
            bool pinSetupEligible = m_PinProjectionOpenAligned;

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
                // A 2/3 pin can extend to 3/2, then to 4/1 or a 5/0 all-tail pin.
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
                    bodyTicks = nextBody;
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

            double tailPrice = direction > 0 ? projectedLow : projectedHigh;
            double completionPrice = direction > 0 ? projectedHigh : projectedLow;
            double entryPrice = RoundToTick(completionPrice, tickSize);
            bool projectionActive = m_PinProjectionTailReached && pinSetupEligible;
            UpdatePinBarTailProjectionLine(tailPrice);
            UpdatePinBarProjectionLine(ref m_PinBarCompletionLine, completionPrice,
                                       projectionActive);
            UpdatePinBarProjectionLabel(completionPrice, direction,
                                        projectionActive, bodyTicks);

            // Keep the projected entry price available for setup arbitration
            // even before the pin tail is reached. Order staging still uses
            // m_PinEntryCandidateValid below, so this does not arm early.
            m_PinEntryCandidateDirection = direction;
            m_PinEntryCandidatePrice = entryPrice;
            m_PinEntryCandidateBodyTicks = bodyTicks;

            // A pin becomes an actionable entry candidate only after its tail
            // is reached. Until then it cannot displace an EMA order.
            if (m_PinProjectionTailReached && pinSetupEligible) {
                m_PinEntryCandidateValid = true;
                m_PinEntryCandidateDirection = direction;
                m_PinEntryCandidatePrice = entryPrice;
                m_PinEntryCandidateBodyTicks = bodyTicks;
            } else {
                ClearPinBarEntryIfActive();
            }
        }

        private bool IsPinBarTrendFilterValid(int direction, double tickSize) {
            if (Bars.CurrentBar < 3 || direction == 0) return false;

            double minimumSeparation = PinBarMinEmaSeparationTicks * tickSize;
            double emaSeparation = direction > 0
                ? m_FastEMA[0] - m_SlowEMA[0]
                : m_SlowEMA[0] - m_FastEMA[0];
            if (emaSeparation < minimumSeparation) return false;

            double fastEmaSlope = GetAngle(m_FastEMA[0], m_FastEMA[3], 3, tickSize);
            return direction > 0
                ? fastEmaSlope > PinBarMinFastEmaSlopeDegrees
                : fastEmaSlope < -PinBarMinFastEmaSlopeDegrees;
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

        private bool IsEntryDirectionAllowed(int direction) {
            return direction > 0 ? IsAskChart : direction < 0 && !IsAskChart;
        }

        private void ArmOrUpdateProjectedPinBarEntry(int direction, double projectedLow,
                                                     double projectedHigh, double tickSize) {
            if (!m_AutoEntryArmed || StrategyInfo.MarketPosition != 0 ||
                direction == 0 || !IsEntryDirectionAllowed(direction)) return;

            // EMA Bounce owns the single entry order whenever both setups are
            // available. A pin can only create or update its own pending order.
            if (m_ActiveEntrySetup != EEntrySetup.None &&
                m_ActiveEntrySetup != EEntrySetup.PinBar) return;

            m_ActiveEntrySetup = EEntrySetup.PinBar;
            m_PinBarOrderBar = Bars.CurrentBar;
            m_StagedPinBodyTicks = Math.Max(0,
                                             PinBarRangeTicks - m_PinProjectionTailTicks);
            m_BuyOrderActive = direction > 0;
            m_SellOrderActive = direction < 0;
            m_StopPrice = RoundToTick(
                direction > 0 ? projectedHigh : projectedLow, tickSize);
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
            if (!IsEntryDirectionAllowed(qualifyingDirection)) {
                ClearEmaBounceProjectionLines();
                ClearEmaBounceEntryIfActive();
                return;
            }

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
            double tailPrice = m_EmaBounceProjectionDirection > 0
                ? projectedLow
                : projectedHigh;
            double completionPrice = m_EmaBounceProjectionDirection > 0
                ? projectedHigh
                : projectedLow;
            double entryPrice = RoundToTick(completionPrice, tickSize);
            UpdateEmaBounceTailLine(tailPrice);
            UpdateEmaBounceCompletionLine(completionPrice,
                                          emaBoundaryReached);
            UpdateEmaBounceProjectionLabel(completionPrice,
                                           m_EmaBounceProjectionDirection,
                                           emaBoundaryReached);

            // As with pin bars, retain the possible entry price for choosing
            // which projection to display.  The validity flag below remains
            // the gate for actual order staging.
            m_EmaEntryCandidateDirection = m_EmaBounceProjectionDirection;
            m_EmaEntryCandidatePrice = entryPrice;

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
            m_PinEntryCandidateBodyTicks = 0;
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

            if (!IsEntryDirectionAllowed(selectedDirection)) {
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
            {
                m_PinBarOrderBar = Bars.CurrentBar;
                m_StagedPinBodyTicks = m_PinEntryCandidateBodyTicks;
            }
            else {
                m_EmaBounceOrderBar = Bars.CurrentBar;
                m_StagedPinBodyTicks = 0;
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
                // still-possible range bar can reach the live 24 EMA.
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
                direction == 0 || !IsEntryDirectionAllowed(direction)) return;

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
            double completionPrice = direction > 0 ? projectedHigh : projectedLow;
            m_StopPrice = RoundToTick(completionPrice, tickSize);
            m_LastSentPrice = 0;
        }

        private void ClearEmaBounceEntryIfActive() {
            if (m_ActiveEntrySetup == EEntrySetup.Ema24Bounce &&
                StrategyInfo.MarketPosition == 0) {
                ClearPendingEntry();
                m_EmaBounceOrderBar = -1;
            }
        }

        private void UpdateEmaBounceTailLine(double price) {
            UpdateEmaBounceLine(ref m_EmaBounceTailLine, price, Color.Gray,
                                ETLStyle.ToolDashed, 1);
        }

        private void UpdateEmaBounceCompletionLine(double price, bool active) {
            Color color = active ? Color.Green : Color.Gray;
            UpdateEmaBounceLine(ref m_EmaBounceCompletionLine, price, color,
                                ETLStyle.ToolSolid, 2);
        }

        private void UpdateEmaBounceLine(ref ITrendLineObject line, double price,
                                         Color color, ETLStyle style, int size) {
            ChartPoint begin = new ChartPoint(Bars.Time[0], price);
            ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), price);
            if (line == null) {
                line = DrwTrendLine.Create(begin, end);
            } else {
                line.Begin = begin;
                line.End = end;
            }
            line.ExtRight = true;
            line.Color = color;
            line.Style = style;
            line.Size = size;
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
            if (m_EmaBounceTailLine != null) { m_EmaBounceTailLine.Delete(); m_EmaBounceTailLine = null; }
            if (m_EmaBounceCompletionLine != null) { m_EmaBounceCompletionLine.Delete(); m_EmaBounceCompletionLine = null; }
            if (m_EmaBounceLabel != null) { m_EmaBounceLabel.Delete(); m_EmaBounceLabel = null; }
        }

        private void ResetEmaBounceProjection() {
            m_EmaBounceProjectionBar = -1;
            m_EmaBounceProjectionDirection = 0;
            ClearEmaBounceProjectionLines();
        }

        private void ClearPendingEntry() {
            if (m_ActiveEntrySetup == EEntrySetup.PinBar)
                m_StagedPinBodyTicks = 0;
            m_BuyOrderActive = m_SellOrderActive = false;
            m_ActiveEntrySetup = EEntrySetup.None;
            m_StopPrice = m_LastSentPrice = 0;
            ClearProjectedEntryLine();
        }

        private bool HasReachedEntryCompletion(int direction, double tickSize) {
            double tolerance = tickSize * 0.1;
            return direction > 0
                ? Bars.High[0] >= m_StopPrice - tolerance
                : Bars.Low[0] <= m_StopPrice + tolerance;
        }

        private void StartShiftProjectionEntry(double tickSize) {
            if (StrategyInfo.MarketPosition != 0) return;

            int direction = m_FastEMA[0] > m_SlowEMA[0] ? 1 :
                            m_FastEMA[0] < m_SlowEMA[0] ? -1 : 0;
            // Reject a wrong-side manual request before it can cancel an
            // already-working permitted entry.
            if (direction != 0 && !IsEntryDirectionAllowed(direction)) return;

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
            if (!IsEntryDirectionAllowed(direction)) {
                ClearShiftProjectionEntry();
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
            double completionPrice = direction > 0 ? projectedHigh : projectedLow;

            bool buyDirection = direction > 0;
            if (m_ActiveEntrySetup == EEntrySetup.ShiftProjection &&
                ((m_BuyOrderActive && !buyDirection) ||
                 (m_SellOrderActive && buyDirection)))
                CancelWorkingEntryOrders();

            m_ActiveEntrySetup = EEntrySetup.ShiftProjection;
            m_BuyOrderActive = buyDirection;
            m_SellOrderActive = !buyDirection;
            m_StopPrice = RoundToTick(completionPrice, tickSize);
            m_LastSentPrice = 0;
            double tailPrice = direction > 0 ? projectedLow : projectedHigh;
            UpdateShiftProjectionLines(tailPrice, completionPrice, direction);
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
                        (order.Name != "RangeBuy" && order.Name != "RangeSell" &&
                         order.Name != "RangeBuyClose" && order.Name != "RangeSellClose") ||
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
                                                double price, bool active) {
            ChartPoint begin = new ChartPoint(Bars.Time[0], price);
            ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), price);
            if (line == null) {
                line = DrwTrendLine.Create(begin, end);
            } else {
                line.Begin = begin;
                line.End = end;
            }
            // Completion is also the actionable entry level. Range bars do not
            // have predictable future timestamps, so extend the single line.
            line.ExtRight = true;
            line.Color = active ? Color.Green : Color.Gray;
            line.Style = ETLStyle.ToolSolid;
            line.Size = 2;
        }

        private void UpdatePinBarTailProjectionLine(double price) {
            ChartPoint begin = new ChartPoint(Bars.Time[0], price);
            ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), price);
            if (m_PinBarTailLine == null) {
                m_PinBarTailLine = DrwTrendLine.Create(begin, end);
            } else {
                m_PinBarTailLine.Begin = begin;
                m_PinBarTailLine.End = end;
            }
            m_PinBarTailLine.ExtRight = true;
            m_PinBarTailLine.Color = Color.Gray;
            m_PinBarTailLine.Style = ETLStyle.ToolDashed;
            m_PinBarTailLine.Size = 1;
        }

        private void ClearPinBarProjectionLines() {
            if (m_PinBarTailLine != null) { m_PinBarTailLine.Delete(); m_PinBarTailLine = null; }
            if (m_PinBarCompletionLine != null) { m_PinBarCompletionLine.Delete(); m_PinBarCompletionLine = null; }
            if (m_PinBarLabel != null) { m_PinBarLabel.Delete(); m_PinBarLabel = null; }
        }

        private void UpdatePinBarProjectionLabel(double price, int direction,
                                                 bool active, int bodyTicks) {
            // Keep the label inside the visible pane by extending its text left
            // from the current bar rather than placing it at a future time.
            ChartPoint point = new ChartPoint(Bars.Time[0], price);
            string text = "Pin bar " + bodyTicks;
            if (m_PinBarLabel == null) {
                m_PinBarLabel = DrwText.Create(point, text);
                m_PinBarLabel.Size = 10;
                m_PinBarLabel.HStyle = ETextStyleH.Right;
                m_PinBarLabel.VStyle = ETextStyleV.Above;
            }
            m_PinBarLabel.Location = point;
            m_PinBarLabel.Text = text;
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

        private bool IsEscapeHeld(Keys eventKeys) {
            if ((eventKeys & Keys.KeyCode) == Keys.Escape) return true;
            try {
                return (GetAsyncKeyState((int)Keys.Escape) & 0x8000) != 0;
            } catch {
                return false;
            }
        }

        private bool IsF11Held(Keys eventKeys) {
            if ((eventKeys & Keys.KeyCode) == Keys.F11) return true;
            try {
                return (GetAsyncKeyState((int)Keys.F11) & 0x8000) != 0;
            } catch {
                return false;
            }
        }

        private void ToggleHudDisplay() {
            m_HudDisplayEnabled = !m_HudDisplayEnabled;
            if (!m_HudDisplayEnabled) {
                if (m_HUDLabel != null) { m_HUDLabel.Delete(); m_HUDLabel = null; }
                if (m_BrokerStatusLabel != null) {
                    m_BrokerStatusLabel.Delete();
                    m_BrokerStatusLabel = null;
                }
                if (m_ControlsHintLabel != null) {
                    m_ControlsHintLabel.Delete();
                    m_ControlsHintLabel = null;
                }
                if (m_ControlsActionHintLabel != null) {
                    m_ControlsActionHintLabel.Delete();
                    m_ControlsActionHintLabel = null;
                }
            } else if (ShowHUD) {
                UpdateHUD();
            }
        }

        private void MoveProtectiveStopToBreakEven(int currentPosition, double tickSize) {
            double entryPrice = StrategyInfo.AvgEntryPrice != 0
                ? StrategyInfo.AvgEntryPrice
                : Bars.Close[0];

            // Match the existing automatic break-even convention: protect at
            // entry with one tick of permitted loss on range-bar trades.
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

            UpdateStopLine();
            SubmitActiveExitOrders(currentPosition);
        }

        private int GetProfitTargetTicksForNewTrade(double sessionPnlAtEntry,
                                                     double tickSize) {
            if (UseOppositeColorExitForProfits) return 0;
            if (ProfitTargetTicks <= 0 || sessionPnlAtEntry >= 0)
                return ProfitTargetTicks;

            // In recovery mode, let the trade run far enough to erase the
            // session deficit and finish five ticks positive.  Round upward so
            // the target never leaves the combined result short of that goal.
            double tickValue = tickSize * Bars.Info.BigPointValue * OrderQuantity;
            if (tickValue <= 0) return ProfitTargetTicks;

            double desiredSessionPnl = RecoverySessionProfitGoalTicks * tickValue;
            double requiredTradeProfit = desiredSessionPnl - sessionPnlAtEntry;
            int requiredTicks = (int)Math.Ceiling(requiredTradeProfit / tickValue);
            return Math.Max(ProfitTargetTicks, requiredTicks);
        }

        private void UpdateOppositeColorExitManagement(int currentPosition,
                                                       double tickSize) {
            bool colorCloseExitActive = m_RecoveryModeActive ||
                                        UseOppositeColorExitForProfits;
            if (!colorCloseExitActive || currentPosition == 0) {
                m_OppositeColorStopPrice = 0;
                ClearRecoveryColorStopProjection();
                return;
            }

            double entryPrice = StrategyInfo.AvgEntryPrice != 0
                ? StrategyInfo.AvgEntryPrice
                : Bars.Close[0];
            double favorableExtremeProfit = currentPosition > 0
                ? Bars.High[0] - entryPrice
                : entryPrice - Bars.Low[0];
            double tolerance = tickSize * 0.1;
            if (!m_OppositeColorProfitArmed &&
                favorableExtremeProfit >=
                (OppositeColorExitMinimumProfitTicks * tickSize) - tolerance) {
                m_OppositeColorProfitArmed = true;
            }
            if (!m_OppositeColorProfitArmed) {
                m_OppositeColorStopPrice = 0;
                ClearRecoveryColorStopProjection();
                return;
            }

            // Project the price where the forming range bar could complete
            // after reversing into the opposite direction. This price is
            // also submitted as the native trailing stop below.
            double range = GetActiveRangeTicks(tickSize) * tickSize;
            double projectedOppositeClose = currentPosition > 0
                ? RoundToTick(Bars.High[0] - range, tickSize)
                : RoundToTick(Bars.Low[0] + range, tickSize);
            if (currentPosition > 0) {
                if (m_OppositeColorStopPrice <= 0 ||
                    projectedOppositeClose > m_OppositeColorStopPrice)
                    m_OppositeColorStopPrice = projectedOppositeClose;
            } else {
                if (m_OppositeColorStopPrice <= 0 ||
                    projectedOppositeClose < m_OppositeColorStopPrice)
                    m_OppositeColorStopPrice = projectedOppositeClose;
            }
            UpdateRecoveryColorStopProjection(m_OppositeColorStopPrice,
                                               currentPosition);
        }

        private void DrawFilledEntryMarkers(int currentPosition, double entryPrice,
                                            double tickSize, EEntrySetup entrySetup,
                                            int pinBodyTicks) {
            // With intrabar order generation, the position transition is
            // observed on the fill calculation of the live range bar. Anchor
            // every marker to that bar, rather than placing pin and manual
            // entries one completed bar early.
            const int barsBack = 0;

            // Put the entry arrow at the actual fill price on the completing
            // range bar. The old version offset short entries one tick above
            // the bar high (and longs one tick below the low), which made the
            // marker look like it belonged to the next price level.
            double directionPrice = RoundToTick(entryPrice, tickSize);
            IArrowObject directionMarker = DrwArrow.Create(
                new ChartPoint(Bars.Time[barsBack], directionPrice), currentPosition < 0);
            // The result is not known until the position closes.  It is then
            // changed to green for profit or red for a loss/break-even trade.
            directionMarker.Color = Color.DimGray;
            directionMarker.Size = 5;
            m_TradeEntryMarkers.Add(directionMarker);
            m_ActiveTradeEntryArrow = directionMarker;

            string entryType = entrySetup == EEntrySetup.PinBar
                ? "P" + Math.Max(0, pinBodyTicks)
                :
                               entrySetup == EEntrySetup.Ema24Bounce ? "B" :
                               entrySetup == EEntrySetup.ShiftProjection ? "M" :
                               "?";
            // Pin annotations are two ticks outside the tail itself. Other
            // entry types retain their existing two-tick spacing from arrow.
            double entryTypePrice = entrySetup == EEntrySetup.PinBar
                ? (currentPosition > 0
                    ? Bars.Low[barsBack] - (2 * tickSize)
                    : Bars.High[barsBack] + (2 * tickSize))
                : (currentPosition > 0
                    ? directionPrice - (2 * tickSize)
                    : directionPrice + (2 * tickSize));
            ITextObject entryTypeMarker = DrwText.Create(
                new ChartPoint(Bars.Time[barsBack], entryTypePrice), entryType);
            entryTypeMarker.Color = Color.Black;
            entryTypeMarker.Size = 6;
            entryTypeMarker.HStyle = ETextStyleH.Center;
            entryTypeMarker.VStyle = currentPosition > 0
                ? ETextStyleV.Below
                : ETextStyleV.Above;
            m_TradeEntryMarkers.Add(entryTypeMarker);

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

        private void UpdateRecoveryColorStopProjection(double price,
                                                       int currentPosition) {
            ChartPoint begin = new ChartPoint(Bars.Time[0], price);
            ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), price);
            if (m_RecoveryColorStopLine == null) {
                m_RecoveryColorStopLine = DrwTrendLine.Create(begin, end);
                m_RecoveryColorStopLine.ExtRight = true;
            } else {
                m_RecoveryColorStopLine.Begin = begin;
                m_RecoveryColorStopLine.End = end;
            }
            m_RecoveryColorStopLine.Color = Color.DodgerBlue;
            m_RecoveryColorStopLine.Style = ETLStyle.ToolDashed;
            m_RecoveryColorStopLine.Size = 2;

            ChartPoint labelPoint = new ChartPoint(Bars.Time[0], price);
            if (m_RecoveryColorStopLabel == null) {
                m_RecoveryColorStopLabel = DrwText.Create(
                    labelPoint, "PROJECTED OPPOSITE CLOSE >= 5T");
                m_RecoveryColorStopLabel.Size = 9;
                m_RecoveryColorStopLabel.HStyle = ETextStyleH.Right;
                m_RecoveryColorStopLabel.Color = Color.DodgerBlue;
            }
            m_RecoveryColorStopLabel.Location = labelPoint;
            m_RecoveryColorStopLabel.Text = "PROJECTED OPPOSITE CLOSE >= 5T";
            m_RecoveryColorStopLabel.VStyle = currentPosition > 0
                ? ETextStyleV.Above
                : ETextStyleV.Below;
        }

        private void ClearRecoveryColorStopProjection() {
            if (m_RecoveryColorStopLine != null) {
                m_RecoveryColorStopLine.Delete();
                m_RecoveryColorStopLine = null;
            }
            if (m_RecoveryColorStopLabel != null) {
                m_RecoveryColorStopLabel.Delete();
                m_RecoveryColorStopLabel = null;
            }
        }

        private void SubmitActiveExitOrders(int currentPosition) {
            if (currentPosition > 0) {
                if (m_ProtectiveStopPrice > 0) m_BuyExitStop.Send(m_ProtectiveStopPrice);
                if (m_ProfitTargetPrice > 0) m_BuyExitLimit.Send(m_ProfitTargetPrice);
                if (m_OppositeColorProfitArmed && m_OppositeColorStopPrice > 0)
                    m_ColorTrailLongStop.Send(m_OppositeColorStopPrice);
            } else if (currentPosition < 0) {
                if (m_ProtectiveStopPrice > 0) m_SellExitStop.Send(m_ProtectiveStopPrice);
                if (m_ProfitTargetPrice > 0) m_SellExitLimit.Send(m_ProfitTargetPrice);
                if (m_OppositeColorProfitArmed && m_OppositeColorStopPrice > 0)
                    m_ColorTrailShortStop.Send(m_OppositeColorStopPrice);
            }
        }

        private DateTime GetTradeControlStartTime() {
            int barsBack = Math.Min(6, Math.Max(0, Bars.CurrentBar - 1));
            return Bars.Time[barsBack];
        }

        private void SetTargetLineSelected(bool selected) {
            if (m_TargetLine != null) m_TargetLine.Color = selected ? Color.Orange : Color.LimeGreen;
        }

        private double GetActiveRangeTicks(double tickSize) {
            if (RangeSizeTicks > 0) return RangeSizeTicks;
            return m_AutoRangeTicks > 0 ? m_AutoRangeTicks : 7;
        }

        private void UpdateProjectedEntryLine() {
            // Pin bars and EMA bounces own a dedicated combined
            // completion/entry line. Do not cover it with the generic pending
            // entry drawing when the native stop order is staged.
            if (m_ActiveEntrySetup == EEntrySetup.PinBar ||
                m_ActiveEntrySetup == EEntrySetup.Ema24Bounce) {
                ClearProjectedEntryLine();
                return;
            }
            // Shift-click owns its own live tail and combined completion/entry
            // line, updated immediately before this generic drawing pass.
            if (m_ActiveEntrySetup == EEntrySetup.ShiftProjection) return;
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

        }

        private void ClearProjectedEntryLine() {
            if (m_ProjectedEntryLine != null) { m_ProjectedEntryLine.Delete(); m_ProjectedEntryLine = null; }
            if (m_ProjectedEntryLabel != null) { m_ProjectedEntryLabel.Delete(); m_ProjectedEntryLabel = null; }
            ClearShiftProjectionLines();
        }

        private void UpdateShiftProjectionLines(double tailPrice,
                                                double completionPrice,
                                                int direction) {
            UpdateShiftProjectionLine(ref m_ShiftTailLine, tailPrice,
                                      Color.DarkGray, ETLStyle.ToolDashed, 1);
            UpdateShiftProjectionLine(ref m_ShiftCompletionLine, completionPrice,
                                      Color.Green, ETLStyle.ToolSolid, 2);

            string entryLabelText = m_AutoEntryArmed
                ? (direction > 0 ? "SHIFT BUY" : "SHIFT SELL")
                : (direction > 0 ? "UNARMED SHIFT BUY" : "UNARMED SHIFT SELL");
            ChartPoint entryPoint = new ChartPoint(Bars.Time[0], completionPrice);
            if (m_ProjectedEntryLabel == null) {
                m_ProjectedEntryLabel = DrwText.Create(entryPoint, entryLabelText);
                m_ProjectedEntryLabel.Size = 10;
                m_ProjectedEntryLabel.HStyle = ETextStyleH.Right;
            }
            m_ProjectedEntryLabel.Location = entryPoint;
            m_ProjectedEntryLabel.Text = entryLabelText;
            m_ProjectedEntryLabel.Color = Color.Black;
            m_ProjectedEntryLabel.VStyle = direction > 0
                ? ETextStyleV.Above
                : ETextStyleV.Below;
        }

        private void UpdateShiftProjectionLine(ref ITrendLineObject line,
                                               double price, Color color,
                                               ETLStyle style, int size) {
            ChartPoint begin = new ChartPoint(Bars.Time[0], price);
            ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), price);
            if (line == null)
                line = DrwTrendLine.Create(begin, end);
            else {
                line.Begin = begin;
                line.End = end;
            }
            line.ExtRight = true;
            line.Color = color;
            line.Style = style;
            line.Size = size;
        }

        private void ClearShiftProjectionLines() {
            if (m_ShiftTailLine != null) {
                m_ShiftTailLine.Delete();
                m_ShiftTailLine = null;
            }
            if (m_ShiftCompletionLine != null) {
                m_ShiftCompletionLine.Delete();
                m_ShiftCompletionLine = null;
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
                status = m_ActiveEntrySetup == EEntrySetup.Ema24Bounce
                    ? (m_AutoEntryArmed ? "ARMED | 24 EMA ENTRY BUY" : "24 EMA ENTRY BUY") :
                    m_ActiveEntrySetup == EEntrySetup.ShiftProjection
                        ? (m_AutoEntryArmed ? "SHIFT ENTRY BUY" : "UNARMED SHIFT ENTRY BUY")
                        : (m_AutoEntryArmed ? "ARMED | PIN ENTRY BUY" : "PIN ENTRY BUY");
            if (m_SellOrderActive)
                status = m_ActiveEntrySetup == EEntrySetup.Ema24Bounce
                    ? (m_AutoEntryArmed ? "ARMED | 24 EMA ENTRY SELL" : "24 EMA ENTRY SELL") :
                    m_ActiveEntrySetup == EEntrySetup.ShiftProjection
                        ? (m_AutoEntryArmed ? "SHIFT ENTRY SELL" : "UNARMED SHIFT ENTRY SELL")
                        : (m_AutoEntryArmed ? "ARMED | PIN ENTRY SELL" : "PIN ENTRY SELL");
            if (StrategyInfo.MarketPosition != 0) {
                status = m_ColorCloseExitRequested
                    ? "EXITING | COLOR CLOSE"
                    : UseOppositeColorExitForProfits
                        ? "IN TRADE | COLOR CLOSE EXIT"
                        : m_RecoveryModeActive
                            ? "IN TRADE | RECOVERY"
                            : "IN TRADE";
            }
            if (m_KillModeActive) status = m_FlattenRequested ? "FLATTENING" : "UNARMED";
            string chartRole = IsAskChart ? "ASK / BUY ONLY" : "BID / SELL ONLY";
            string text = string.Format("{0} | {1} | Session PnL: {2:C2}",
                                        status, chartRole, pnl);
            // Keep the session line immediately below the broker line as one
            // compact, unobtrusive status block.
            ChartPoint hudPoint = GetStatusLabelPoint(tickSize, 4);
            if (m_HUDLabel == null) {
                m_HUDLabel = DrwText.Create(hudPoint, text);
            }
            if (m_HUDLabel == null) return;
            m_HUDLabel.Size = 11;
            // In MultiCharts text drawings, Right keeps the visible left edge
            // at the shared chart point; Left aligns the right edges instead.
            m_HUDLabel.HStyle = ETextStyleH.Right;
            m_HUDLabel.VStyle = GetStatusLabelVerticalStyle();
            m_HUDLabel.Text = text;
            m_HUDLabel.Color = m_AutoEntryArmed ? Color.Green : Color.Black;
            m_HUDLabel.Location = hudPoint;
            UpdateBrokerStatusLabel(tickSize);
            UpdateControlsHintLabel(tickSize);
        }

        private ChartPoint GetStatusLabelPoint(double tickSize, int offsetTicks) {
            // Keep each chart role on one deterministic side of its live bar.
            // This prevents MultiCharts from visually flipping the BID status
            // block above and below the developing candle as the scale moves.
            double price = IsAskChart
                ? Bars.High[0] + (offsetTicks * tickSize)
                : Bars.Low[0] - (offsetTicks * tickSize);
            return new ChartPoint(Bars.Time[0], price);
        }

        private ETextStyleV GetStatusLabelVerticalStyle() {
            return IsAskChart ? ETextStyleV.Above : ETextStyleV.Below;
        }

        private void UpdateBrokerStatusLabel(double tickSize) {
            try {
                UpdateBrokerStatusLabelCore(tickSize);
            } catch (Exception ex) {
                // Both the broker tracker and chart drawings can be rebuilt by
                // MultiCharts between IOG calculations.  Treat a transient
                // null/disposed object as a skipped HUD refresh, not a fatal
                // strategy error; the next tick recreates the label.
                Output.WriteLine("RangeBarTrading broker HUD refresh error: " +
                                 ex.Message);
                m_BrokerStatusLabel = null;
            }
        }

        private void UpdateBrokerStatusLabelCore(double tickSize) {
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
            // MultiCharts can temporarily decline to create a drawing while a
            // chart is loading or redrawing. Skip this HUD refresh and retry
            // on the next calculation rather than dereferencing a null object.
            if (m_BrokerStatusLabel == null) return;
            // Match the HUD so the two lines read as a single status block.
            m_BrokerStatusLabel.Size = 11;
            m_BrokerStatusLabel.HStyle = ETextStyleH.Right;
            m_BrokerStatusLabel.VStyle = GetStatusLabelVerticalStyle();
            m_BrokerStatusLabel.Location = point;
            m_BrokerStatusLabel.Text = text;
            m_BrokerStatusLabel.Color = color;
        }

        private void UpdateControlsHintLabel(double tickSize) {
            const string controlText = "L-click stop marker: Break-even\nShift+click:\nCtrl+click:\nF11+click:\nEsc+click:\nTrades run:";
            // MultiCharts trims ordinary leading spaces. Non-breaking spaces
            // preserve the action-column offset and keep every action aligned.
            const string actionPadding =
                "\u00A0\u00A0\u00A0\u00A0\u00A0\u00A0\u00A0\u00A0" +
                "\u00A0\u00A0\u00A0\u00A0\u00A0\u00A0\u00A0\u00A0" +
                "\u00A0\u00A0\u00A0\u00A0\u00A0\u00A0\u00A0\u00A0" +
                "\u00A0\u00A0\u00A0\u00A0";
            // The first row is intentionally self-contained in controlText.
            // The remaining rows retain their shared action-column alignment.
            string actionText = actionPadding + "\n" +
                                actionPadding + "Manual\n" +
                                actionPadding + "Arm/Disarm\n" +
                                actionPadding + "Toggle HUD\n" +
                                actionPadding + "Flatten\n" +
                                actionPadding +
                                (UseOppositeColorExitForProfits ? "True" : "False");
            ChartPoint point = GetStatusLabelPoint(tickSize, 9);
            if (m_ControlsHintLabel == null) {
                m_ControlsHintLabel = DrwText.Create(point, controlText);
            }
            if (m_ControlsHintLabel == null) return;
            m_ControlsHintLabel.Size = 8;
            // Right alignment in MultiCharts places the visible left edge at
            // the chart point, matching the paper-trader status label.
            m_ControlsHintLabel.HStyle = ETextStyleH.Right;
            m_ControlsHintLabel.VStyle = GetStatusLabelVerticalStyle();
            m_ControlsHintLabel.Location = point;
            m_ControlsHintLabel.Text = controlText;
            m_ControlsHintLabel.Color = Color.DarkSlateGray;

            if (m_ControlsActionHintLabel == null) {
                m_ControlsActionHintLabel = DrwText.Create(point, actionText);
            }
            if (m_ControlsActionHintLabel == null) return;
            m_ControlsActionHintLabel.Size = 8;
            m_ControlsActionHintLabel.HStyle = ETextStyleH.Right;
            m_ControlsActionHintLabel.VStyle = GetStatusLabelVerticalStyle();
            m_ControlsActionHintLabel.Location = point;
            m_ControlsActionHintLabel.Text = actionText;
            m_ControlsActionHintLabel.Color = Color.DarkSlateGray;
        }

        private string GetBrokerStatusName() {
            if (!string.IsNullOrEmpty(m_StrategyBrokerProfile))
                return m_StrategyBrokerProfile.ToUpperInvariant();

            try {
                var tradeManager = TradeManager;
                if (tradeManager == null || tradeManager.TradingProfiles == null)
                    return "BROKER";
                var tradingProfiles = tradeManager.TradingProfiles;
                foreach (var tradingProfile in tradingProfiles) {
                    if (tradingProfile == null || string.IsNullOrEmpty(tradingProfile.Name))
                        continue;
                    if (tradingProfile.Name.IndexOf("paper", StringComparison.OrdinalIgnoreCase) >= 0)
                        return tradingProfile.Name.ToUpperInvariant();
                }
                if (tradingProfiles.Length == 1 && tradingProfiles[0] != null &&
                    !string.IsNullOrEmpty(tradingProfiles[0].Name))
                    return tradingProfiles[0].Name.ToUpperInvariant();
            } catch (Exception ex) {
                Output.WriteLine("RangeBarTrading broker-name refresh error: " +
                                 ex.Message);
            }
            return "BROKER";
        }

        private double GetAngle(double valCurrent, double valOld, int barsBack, double tickSize) {
            double rise = valCurrent - valOld;
            double run = (double)barsBack * tickSize; 
            return Math.Atan2(rise, run) * (180.0 / Math.PI);
        }

        protected override void Destroy() {
            if (s_LastActiveChart == this) s_LastActiveChart = null;
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
