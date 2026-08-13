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
        private enum EEntrySetup { None, PinBar, Ema24Bounce, Ema8Bounce, ShiftProjection }

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
        [Input] public bool Enable8EMABounceTrading { get; set; }
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
        private const int OneBarProfitTargetTicks = 5;
        private const int HudRefreshMilliseconds = 100;
        private const bool ShowHUD = true;
        // PB shapes scale from the original five-tick definitions.  For
        // example, PB2/PB1/PB0 are 3/2, 4/1, 5/0 on MES 5-tick bars and
        // 5/3, 6/2, 8/0 on MYM/M2K 8-tick bars.
        private const int BasePinBarRangeTicks = 5;
        private const int PinBarMinEmaSeparationTicks = 3;
        private const double PinBarMinFastEmaSlopeDegrees = 20.0;
        private const double StrongContinuationFastSlopeDegrees = 40.0;
        private const double StrongContinuationSlowSlopeDegrees = 15.0;
        private const double StrongContinuationSeparationTicks = 3.0;
        private const int MasterTrendPeriod = 60;
        private const int MinExpansionTicks = 25;
        private const int MinBreadth_15_60 = 5;
        private const int MinBreadth_5_15 = 4;
        // Mirrors RangeEMA8Bounce's provisional visual detector.
        private const double Ema8MinSeparationTicks = 4.5;
        private const double Ema8MinFastSlopeDegrees = 40.0;
        private const double Ema8MinSlowSlopeDegrees = 40.0;
        private const double Ema8MinPenetrationTicks = 1.0;
        // A two-bar pullback may probe four and a half ticks through the 8
        // EMA before rejecting; tighter caps excluded otherwise clean MYM
        // eight-tick-bar examples.
        private const double Ema8MaxPenetrationTicks = 4.5;
        private const double Ema8MinLocalDisplacementTicks = 1.0;
        // Match RangeEMA8Bounce's half-tick penetration comparison.
        private const double Ema8PenetrationRoundingAllowanceTicks = 0.25;
        private const double Ema24MinCurrentSeparationTicks = 1.5;
        private const double Ema24MinBestSeparationTicks = 2.0;
        private const double Ema24MinSlowSlopeDegrees = 20.0;
        // Keep the live 24-EMA setup recognition aligned with
        // RangeEMA24Bounce: a completed/rejecting bar may stop within
        // three-quarters of a tick of the EMA rather than touching it exactly.
        private const double Ema24TouchToleranceTicks = 0.75;
        // A trade is allowed only inside a persistent, fully aligned 8/24/50
        // EMA profile. These values are the initial settings derived from the
        // transition diagnostic and can be refined from future samples.
        private const int ProfileEmaLength = 50;
        private const int ProfileSlopeBars = 3;
        private const int ProfilePersistenceBars = 4;
        private const int PinBarSlowEmaPersistenceBars = 4;
        private const double ProfileMinFastSlowSeparationTicks = 3.0;
        private const double ProfileMinSlowTrendSeparationTicks = 2.0;
        private const double ProfileMinFastTrendSeparationTicks = 5.0;
        private const double ProfileMinFastSlopeDegrees = 20.0;
        private const double ProfileMinSlowSlopeDegrees = 20.0;
        private const double ProfileMinTrendSlopeDegrees = 10.0;
        private const double ProfileMaximumCompressionTicks = 1.0;

        private IOrderPriced m_BuyStop;
        private IOrderPriced m_SellStop;
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
        private XAverage m_ProfileEMA;
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
        private int m_ActiveStopLossTicks = 12;
        private bool m_StopLossSettingInitialized = false;
        private int m_PinProjectionBar = -1;
        private int m_PinProjectionDirection = 0;
        private bool m_PinProjectionTailReached = false;
        private bool m_PinProjectionBroken = false;
        private int m_PinProjectionTailTicks = 3;
        private bool m_PinProjectionOpenAligned = false;
        private int m_PinBarOrderBar = -1;
        private int m_EmaBounceProjectionBar = -1;
        private int m_EmaBounceProjectionDirection = 0;
        private int m_EmaBounceOrderBar = -1;
        private int m_Ema8BounceOrderBar = -1;
        private bool m_PinEntryCandidateValid = false;
        private int m_PinEntryCandidateDirection = 0;
        private double m_PinEntryCandidatePrice = 0;
        private int m_PinEntryCandidateBodyTicks = 0;
        private int m_StagedPinBodyTicks = 0;
        private bool m_EmaEntryCandidateValid = false;
        private int m_EmaEntryCandidateDirection = 0;
        private double m_EmaEntryCandidatePrice = 0;
        private bool m_Ema8EntryCandidateValid = false;
        private int m_Ema8EntryCandidateDirection = 0;
        private double m_Ema8EntryCandidatePrice = 0;
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
        private DateTime m_LastHudRefreshAt = DateTime.MinValue;
        private int m_HudAnchorBar = -1;
        private int m_HudLayoutBar = -1;
        private DateTime m_HudAnchorTime = DateTime.MinValue;
        private double m_HudAnchorHigh = 0;
        private double m_HudAnchorLow = 0;
        private string m_LastHudText = null;
        private Color m_LastHudColor = Color.Empty;
        private string m_LastBrokerStatusText = null;
        private Color m_LastBrokerStatusColor = Color.Empty;
        private string m_LastControlsActionText = null;
        private double m_AutoRangeTicks = 0;
        private DateTime m_EmergencyMessageExpiresAt = DateTime.MinValue;
        private readonly List<int> m_EmergencyCancelOrderIds = new List<int>();
        private bool m_EmergencyCancellationPending = false;
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
        private ITrendLineObject m_Ema8BounceTailLine;
        private ITrendLineObject m_Ema8BounceCompletionLine;
        private ITextObject m_Ema8BounceLabel;
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
            Enable8EMABounceTrading = true;
            IsAskChart = true;
            UseOppositeColorExitForProfits = true;
        }

        protected override void Create()
        {
            m_FastEMA = new XAverage(this); m_SlowEMA = new XAverage(this);
            m_ProfileEMA = new XAverage(this); m_MasterEMA = new XAverage(this);
            m_BuyStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "RangeBuy", EOrderAction.Buy));
            m_SellStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "RangeSell", EOrderAction.SellShort));
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
            m_ProfileEMA.Length = ProfileEmaLength; m_ProfileEMA.Price = Bars.Close;
            m_MasterEMA.Length = MasterTrendPeriod; m_MasterEMA.Price = Bars.Close;
            if (!m_StopLossSettingInitialized) {
                m_ActiveStopLossTicks = ProtectiveStopLossTicks == 7 ? 7 : 12;
                m_StopLossSettingInitialized = true;
            }
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
            ClearEma8BounceProjectionLines();
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
                m_PinProjectionTailTicks = 3;
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

            if (!Enable8EMABounceTrading) {
                ClearEma8BounceProjectionLines();
                if (m_ActiveEntrySetup == EEntrySetup.Ema8Bounce && currentPosition == 0) {
                    CancelWorkingEntryOrders();
                    ClearPendingEntry();
                }
            }

            if (!EnablePinBarTrading && !Enable24EMABounceTrading && !Enable8EMABounceTrading &&
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

            if (m_ActiveEntrySetup == EEntrySetup.Ema8Bounce &&
                currentPosition == 0 && m_Ema8BounceOrderBar != Bars.CurrentBar) {
                CancelWorkingEntryOrders();
                ClearPendingEntry();
            }

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
            UpdateEma8BounceProjection(tickSize, currentPosition);
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
                double stopDist = m_ActiveStopLossTicks * tickSize;
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
                    m_ProtectiveStopPrice = m_ActiveStopLossTicks > 0 ? entryPrice - stopDist : 0;
                    m_ProfitTargetPrice = activeProfitTargetTicks > 0 ? entryPrice + targetDist : 0;
                } else {
                    m_ProtectiveStopPrice = m_ActiveStopLossTicks > 0 ? entryPrice + stopDist : 0;
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
                    // Entry handling is identical in both profit modes: once
                    // a setup's tail/bounce is valid, stage a native buy stop
                    // at its projected range-bar completion price.
                    if (Bars.LastBarOnChart) {
                        m_BuyStop.Send(m_StopPrice, OrderQuantity);
                        m_LastSentPrice = m_StopPrice;
                    }
                    UpdateProjectedEntryLine();
                } else if (m_SellOrderActive && m_StopPrice > 0 &&
                           IsEntryDirectionAllowed(-1)) {
                    // Mirror the long path with a native sell stop at the
                    // short setup's projected completion price.
                    if (Bars.LastBarOnChart) {
                        m_SellStop.Send(m_StopPrice, OrderQuantity);
                        m_LastSentPrice = m_StopPrice;
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
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale; if (tickSize <= 0) tickSize = 0.25;

            // Escape plus left-click is an alternate emergency control for
            // chart configurations that consume the middle-wheel click.
            if (arg.buttons == MouseButtons.Left &&
                IsEscapeHeld(arg.keys)) {
                m_HudDisplayEnabled = true;
                ActivateEmergencyFlatten(true);
                if (ShowHUD) UpdateHUD(true);
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
                if (ShowHUD) UpdateHUD(true);
                return;
            }

            if (arg.buttons == MouseButtons.Left && IsF11Held(arg.keys)) {
                ToggleHudDisplay();
                return;
            }

            if (arg.buttons == MouseButtons.Left && IsF1Held(arg.keys)) {
                TogglePinBarTrading(tickSize);
                if (ShowHUD && m_HudDisplayEnabled) UpdateHUD(true);
                return;
            }

            if (arg.buttons == MouseButtons.Left && IsF2Held(arg.keys)) {
                Toggle24EmaBounceTrading(tickSize);
                if (ShowHUD && m_HudDisplayEnabled) UpdateHUD(true);
                return;
            }

            if (arg.buttons == MouseButtons.Left && IsF3Held(arg.keys)) {
                Toggle8EmaBounceTrading(tickSize);
                if (ShowHUD && m_HudDisplayEnabled) UpdateHUD(true);
                return;
            }

            if (arg.buttons == MouseButtons.Left && IsF4Held(arg.keys)) {
                ToggleProfitManagementMode(tickSize);
                if (ShowHUD && m_HudDisplayEnabled) UpdateHUD(true);
                return;
            }

            if (arg.buttons == MouseButtons.Left && IsF5Held(arg.keys)) {
                ToggleStopLossMode(tickSize);
                if (ShowHUD && m_HudDisplayEnabled) UpdateHUD(true);
                return;
            }

            if (arg.buttons != MouseButtons.Left) return;
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
                } else if (EnablePinBarTrading || Enable24EMABounceTrading || Enable8EMABounceTrading) {
                    // Flat and unarmed: latch the 24 EMA direction and begin
                    // waiting persistently for an enabled automated setup.
                    ArmAutomatedEntryMode(tickSize);
                }
                if (ShowHUD && m_HudDisplayEnabled) UpdateHUD(true);
            }
            else if (IsShiftClick(arg.keys)) {
                if (m_ShiftProjectionActive)
                    ClearShiftProjectionEntry();
                else
                    StartShiftProjectionEntry(tickSize);
                if (ShowHUD && m_HudDisplayEnabled) UpdateHUD(true);
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

        private void ArmAutomatedEntryMode(double tickSize,
                                           int requestedDirection = 0) {
            ClearEmergencyIndicator();
            m_KillModeActive = false;
            m_FlattenRequested = false;
            m_AutoEntryArmed = true;
            // Preserve the existing semantics for a normal Ctrl-click:
            // flat/rising 24 EMA is bullish; falling 24 EMA is bearish. A
            // Shift projection already has an explicit, permitted direction,
            // so use that direction when it also transitions from unarmed to
            // armed.
            m_ArmedDirection = requestedDirection != 0
                ? requestedDirection
                : m_SlowEMA[0] >= m_SlowEMA[1] ? 1 : -1;
            if (!IsEntryDirectionAllowed(m_ArmedDirection)) {
                m_AutoEntryArmed = false;
                m_ArmedDirection = 0;
                return;
            }
            m_PinProjectionBar = Bars.CurrentBar;
            m_PinProjectionDirection = m_ArmedDirection;
            m_PinProjectionTailReached = false;
            m_PinProjectionBroken = false;
            m_PinProjectionTailTicks = GetInitialPinBarTailTicks();
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
            UpdateEma8BounceProjection(tickSize, StrategyInfo.MarketPosition);
            ReconcileAutomaticEntryCandidates(tickSize, StrategyInfo.MarketPosition);
        }

        private void TogglePinBarTrading(double tickSize) {
            EnablePinBarTrading = !EnablePinBarTrading;
            if (m_ActiveEntrySetup == EEntrySetup.PinBar &&
                StrategyInfo.MarketPosition == 0)
                ClearPinBarEntryIfActive();
            m_PinProjectionBar = -1;
            m_PinProjectionDirection = 0;
            m_PinProjectionTailReached = false;
            m_PinProjectionBroken = false;
            m_PinProjectionTailTicks = GetInitialPinBarTailTicks();
            m_PinProjectionOpenAligned = false;
            ClearPinBarProjectionLines();

            ResetAutomaticEntryCandidates();
            UpdatePinBarProjection(tickSize, StrategyInfo.MarketPosition);
            UpdateEmaBounceProjection(tickSize, StrategyInfo.MarketPosition);
            UpdateEma8BounceProjection(tickSize, StrategyInfo.MarketPosition);
            ReconcileAutomaticEntryCandidates(tickSize, StrategyInfo.MarketPosition);
        }

        private void Toggle24EmaBounceTrading(double tickSize) {
            Enable24EMABounceTrading = !Enable24EMABounceTrading;
            if (m_ActiveEntrySetup == EEntrySetup.Ema24Bounce &&
                StrategyInfo.MarketPosition == 0)
                ClearEmaBounceEntryIfActive();
            ResetEmaBounceProjection();
            RefreshSetupCandidates(tickSize);
        }

        private void Toggle8EmaBounceTrading(double tickSize) {
            Enable8EMABounceTrading = !Enable8EMABounceTrading;
            if (m_ActiveEntrySetup == EEntrySetup.Ema8Bounce &&
                StrategyInfo.MarketPosition == 0) {
                CancelWorkingEntryOrders();
                ClearPendingEntry();
            }
            ClearEma8BounceProjectionLines();
            RefreshSetupCandidates(tickSize);
        }

        private void RefreshSetupCandidates(double tickSize) {
            if (!EnablePinBarTrading && !Enable24EMABounceTrading &&
                !Enable8EMABounceTrading && StrategyInfo.MarketPosition == 0) {
                m_AutoEntryArmed = false;
                m_ArmedDirection = 0;
            }
            ResetAutomaticEntryCandidates();
            UpdatePinBarProjection(tickSize, StrategyInfo.MarketPosition);
            UpdateEmaBounceProjection(tickSize, StrategyInfo.MarketPosition);
            UpdateEma8BounceProjection(tickSize, StrategyInfo.MarketPosition);
            ReconcileAutomaticEntryCandidates(tickSize, StrategyInfo.MarketPosition);
        }

        private int GetInitialPinBarTailTicks() {
            int rangeTicks = GetActivePinBarRangeTicks();
            return rangeTicks - GetPinBarPB2BodyTicks(rangeTicks);
        }

        private int GetActivePinBarRangeTicks() {
            // Prefer the configured ATIC/range-bar size. When the strategy is
            // set to auto-detect, use the live measured range instead.
            return Math.Max(1, (int)Math.Round(GetActiveRangeTicks(0.25)));
        }

        private int GetPinBarPB2BodyTicks(int rangeTicks) {
            return Math.Max(1, (int)Math.Round(rangeTicks * 2.0 /
                                                BasePinBarRangeTicks));
        }

        private int GetPinBarPB1BodyTicks(int rangeTicks) {
            return Math.Max(1, (int)Math.Round(rangeTicks * 1.0 /
                                                BasePinBarRangeTicks));
        }

        private bool IsSupportedPinBarShape(int rangeTicks, int bodyTicks) {
            return bodyTicks == GetPinBarPB2BodyTicks(rangeTicks) ||
                   bodyTicks == GetPinBarPB1BodyTicks(rangeTicks) ||
                   bodyTicks == 0;
        }

        private int GetPinBarDisplayLevel(int rangeTicks, int bodyTicks) {
            if (bodyTicks == 0) return 0;
            if (bodyTicks == GetPinBarPB1BodyTicks(rangeTicks)) return 1;
            return 2;
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
            m_PinProjectionTailTicks = 3;
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
            // MultiCharts can briefly rebuild function instances while the
            // chart is recalculating. Skip this visual/setup pass until both
            // EMA functions are available again instead of dereferencing a
            // transient null and aborting the entire strategy calculation.
            if (m_FastEMA == null || m_SlowEMA == null) return;

            // An armed direction is deliberately persistent. While unarmed,
            // choose one informational pin shape per new bar from the same
            // 24 EMA slope rule.
            if (m_PinProjectionBar != Bars.CurrentBar) {
                m_PinProjectionBar = Bars.CurrentBar;
                m_PinProjectionDirection = m_AutoEntryArmed
                    ? m_ArmedDirection
                    : GetUnarmedPinBarProjectionDirection(tickSize);
                m_PinProjectionTailReached = false;
                m_PinProjectionBroken = false;
                m_PinProjectionTailTicks = GetInitialPinBarTailTicks();
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

            double projectedLow;
            double projectedHigh;
            int pinBarRangeTicks = GetActivePinBarRangeTicks();
            int bodyTicks = pinBarRangeTicks - m_PinProjectionTailTicks;
            GetPinBarProjectionPrices(direction, m_PinProjectionTailTicks, bodyTicks,
                                       tickSize, out projectedLow, out projectedHigh);
            if (m_PinProjectionTailTicks < pinBarRangeTicks)
                UpdatePinBarFormationState(direction, projectedLow, projectedHigh, tickSize);

            if (m_PinProjectionBroken ||
                !CanStillFormPinBar(direction, m_PinProjectionTailTicks, bodyTicks,
                                     tickSize)) {
                // A PB2 can extend to PB1, then PB0. Advance only through
                // those valid RangePBBounce shapes on the same live bar.
                bool foundNextShape = false;
                for (int nextTail = m_PinProjectionTailTicks + 1;
                     nextTail <= pinBarRangeTicks;
                     nextTail++) {
                    int nextBody = pinBarRangeTicks - nextTail;
                    if (!IsSupportedPinBarShape(pinBarRangeTicks, nextBody))
                        continue;
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

            // The PB drawing is a live projection, not an order-status
            // indicator.  Keep it on every IOG update as long as this range
            // bar can still complete as PB2, PB1, or PB0.  The qualification
            // checks below use live EMA/close values and can legitimately
            // flicker while the bar forms; allowing them to clear this line
            // left the trader without the projected stop/entry level until
            // the final ticks of an otherwise viable bar.
            UpdatePinBarTailProjectionLine(tailPrice);
            UpdatePinBarProjectionLine(ref m_PinBarCompletionLine,
                                       completionPrice, false);
            UpdatePinBarProjectionLabel(completionPrice, direction, false,
                                        bodyTicks);

            bool isCompactPB = bodyTicks <=
                GetPinBarPB1BodyTicks(pinBarRangeTicks) && direction != 0;
            // Every PB shape must have a prior bar in the trade direction,
            // a tail on the trend side of the 8 EMA, and three consecutive
            // 8-EMA moves in that same direction.
            bool hasPriorTrendConfirmation = isCompactPB
                ? HasThreeBarPinBarCloseBreakout(direction)
                : HasTwoPriorPinBarTrendCloses(direction);
            bool hasPriorBarConfirmation = isCompactPB
                ? HasCompactPinBarPriorTrendConfirmation(direction, tickSize)
                : HasPinBarPriorBarDirection(direction);
            if (!hasPriorTrendConfirmation ||
                !hasPriorBarConfirmation ||
                !HasRequiredEmaFan(direction, tickSize) ||
                !HasDirectionalFastEmaSlopeForThreeBars(direction) ||
                !IsPinBarTailOnFastEmaSide(direction, projectedLow,
                                             projectedHigh)) {
                ClearPinBarEntryIfActive();
                return;
            }
            bool trendFilterPass = isCompactPB
                ? HasSharplyMovingFastEma(direction, tickSize)
                : IsPinBarTrendFilterValid(direction, tickSize);
            if (!trendFilterPass) {
                ClearPinBarEntryIfActive();
                return;
            }

            bool pinSetupEligible = isCompactPB
                ? IsCompactPinBarContinuationValid(direction, projectedLow,
                                                    projectedHigh, tickSize)
                : m_PinProjectionOpenAligned &&
                  (HasPinBarLeadInStructure(direction) ||
                   IsStrongPinBarContinuationValid(direction, projectedLow,
                                                    projectedHigh, tickSize));
            // Clearance is intentionally disabled while PB projections are
            // being retuned. A touched, otherwise-valid projected PB may
            // highlight and arm regardless of bars to its left.
            bool rangeClearancePass = true;
            bool projectionActive = m_PinProjectionTailReached &&
                                    pinSetupEligible && rangeClearancePass;
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
            if (m_PinProjectionTailReached && pinSetupEligible &&
                rangeClearancePass) {
                m_PinEntryCandidateValid = true;
                m_PinEntryCandidateDirection = direction;
                m_PinEntryCandidatePrice = entryPrice;
                m_PinEntryCandidateBodyTicks = bodyTicks;
            } else {
                ClearPinBarEntryIfActive();
            }
        }

        private bool IsPinBarTrendFilterValid(int direction, double tickSize) {
            if (Bars.CurrentBar < 8 || direction == 0) return false;

            double minimumSeparation = PinBarMinEmaSeparationTicks * tickSize;
            double emaSeparation = direction > 0
                ? m_FastEMA[0] - m_SlowEMA[0]
                : m_SlowEMA[0] - m_FastEMA[0];
            if (emaSeparation < minimumSeparation) return false;

            double fastEmaSlope = GetAngle(m_FastEMA[0], m_FastEMA[3], 3, tickSize);
            return direction > 0
                ? fastEmaSlope >= PinBarMinFastEmaSlopeDegrees
                : fastEmaSlope <= -PinBarMinFastEmaSlopeDegrees;
        }

        private bool HasPinBarRangeClearance(int direction,
                                             double projectedCompletionPrice,
                                             int lookbackBars) {
            for (int barsBack = 1; barsBack <= lookbackBars; barsBack++) {
                if (direction > 0 && projectedCompletionPrice <= Bars.High[barsBack])
                    return false;
                if (direction < 0 && projectedCompletionPrice >= Bars.Low[barsBack])
                    return false;
            }
            return true;
        }

        private bool IsCompactPinBarContinuationValid(int direction,
                                                        double projectedLow,
                                                        double projectedHigh,
                                                        double tickSize) {
            return direction > 0
                ? HasDirectional824PinBarTrendContext(direction, tickSize) &&
                  HasMinimumPinBarFastSlowSeparation(tickSize) &&
                  HasSharplyMovingFastEma(direction, tickSize) &&
                  HasThreeBarPinBarCloseBreakout(direction) &&
                  HasCompactPinBarPriorTrendConfirmation(direction, tickSize) &&
                  projectedLow >= m_FastEMA[0]
                : direction < 0 &&
                  HasDirectional824PinBarTrendContext(direction, tickSize) &&
                  HasMinimumPinBarFastSlowSeparation(tickSize) &&
                  HasSharplyMovingFastEma(direction, tickSize) &&
                  HasThreeBarPinBarCloseBreakout(direction) &&
                  HasCompactPinBarPriorTrendConfirmation(direction, tickSize) &&
                  projectedHigh <= m_FastEMA[0];
        }

        // PB1/PB0 continuations can have a rapidly turning 8 EMA, but their
        // direction must still agree with the 8/24 order and 24-EMA slope.
        // The 50 EMA is intentionally excluded from this PB rule.
        private bool HasDirectional824PinBarTrendContext(int direction,
                                                           double tickSize) {
            return direction > 0
                ? m_FastEMA[0] > m_SlowEMA[0] &&
                  HasPersistentPinBarSlowEmaDirection(1)
                : direction < 0 && m_FastEMA[0] < m_SlowEMA[0] &&
                  HasPersistentPinBarSlowEmaDirection(-1);
        }

        private bool HasPersistentPinBarSlowEmaDirection(int direction) {
            if (Bars.CurrentBar < PinBarSlowEmaPersistenceBars) return false;
            for (int barsBack = 0; barsBack < PinBarSlowEmaPersistenceBars;
                 barsBack++) {
                if (direction > 0 && m_SlowEMA[barsBack] <= m_SlowEMA[barsBack + 1])
                    return false;
                if (direction < 0 && m_SlowEMA[barsBack] >= m_SlowEMA[barsBack + 1])
                    return false;
            }
            return true;
        }

        private bool HasMinimumPinBarFastSlowSeparation(double tickSize) {
            return Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) >=
                   PinBarMinEmaSeparationTicks * tickSize;
        }

        private bool HasSharplyMovingFastEma(int direction, double tickSize) {
            if (Bars.CurrentBar < 3) return false;
            double slope = GetAngle(m_FastEMA[0], m_FastEMA[3], 3, tickSize);
            return direction > 0
                ? slope >= PinBarMinFastEmaSlopeDegrees
                : direction < 0 && slope <= -PinBarMinFastEmaSlopeDegrees;
        }

        private bool HasDirectionalFastEmaSlopeForThreeBars(int direction) {
            if (Bars.CurrentBar < 3 || direction == 0) return false;
            return direction > 0
                ? m_FastEMA[0] > m_FastEMA[1] &&
                  m_FastEMA[1] > m_FastEMA[2] &&
                  m_FastEMA[2] > m_FastEMA[3]
                : m_FastEMA[0] < m_FastEMA[1] &&
                  m_FastEMA[1] < m_FastEMA[2] &&
                  m_FastEMA[2] < m_FastEMA[3];
        }

        private bool HasPinBarPriorBarDirection(int direction) {
            return direction > 0
                ? Bars.Close[1] > Bars.Open[1]
                : direction < 0 && Bars.Close[1] < Bars.Open[1];
        }

        // A completed PB0 is a zero-body directional pin bar: its open and
        // close are both at the completion extreme.  Accept it as the prior
        // compact-PB confirmation, while retaining the strict body-color
        // requirement for every other pin-bar setup.
        private bool HasCompactPinBarPriorTrendConfirmation(int direction,
                                                              double tickSize) {
            return HasPinBarPriorBarDirection(direction) ||
                   IsPriorDirectionalPB0(direction, tickSize);
        }

        private bool IsPriorDirectionalPB0(int direction, double tickSize) {
            double tolerance = tickSize * 0.1;
            double open = RoundToTick(Bars.Open[1], tickSize);
            double high = RoundToTick(Bars.High[1], tickSize);
            double low = RoundToTick(Bars.Low[1], tickSize);
            double close = RoundToTick(Bars.Close[1], tickSize);

            return direction > 0
                ? Math.Abs(open - high) <= tolerance &&
                  Math.Abs(close - high) <= tolerance &&
                  (int)Math.Round((open - low) / tickSize) == GetActivePinBarRangeTicks()
                : direction < 0 &&
                  Math.Abs(open - low) <= tolerance &&
                  Math.Abs(close - low) <= tolerance &&
                  (int)Math.Round((high - open) / tickSize) == GetActivePinBarRangeTicks();
        }

        // PB continuation entries require two completed bars of directional
        // follow-through immediately before the current pin bar.
        private bool HasTwoPriorPinBarTrendCloses(int direction) {
            if (Bars.CurrentBar < 2) return false;
            for (int barsBack = 1; barsBack <= 2; barsBack++) {
                bool closesWithTrend = direction > 0
                    ? Bars.Close[barsBack] > Bars.Open[barsBack]
                    : Bars.Close[barsBack] < Bars.Open[barsBack];
                if (!closesWithTrend) return false;
            }
            return true;
        }

        private bool HasThreeBarPinBarCloseBreakout(int direction) {
            if (Bars.CurrentBar < 3) return false;
            for (int barsBack = 1; barsBack <= 3; barsBack++) {
                if (direction > 0 && Bars.Close[0] <= Bars.Close[barsBack])
                    return false;
                if (direction < 0 && Bars.Close[0] >= Bars.Close[barsBack])
                    return false;
            }
            return true;
        }

        private bool IsPinBarTailOnFastEmaSide(int direction,
                                                 double projectedLow,
                                                 double projectedHigh) {
            return direction > 0
                ? projectedLow >= m_FastEMA[0]
                : direction < 0 && projectedHigh <= m_FastEMA[0];
        }

        private int GetUnarmedPinBarProjectionDirection(double tickSize) {
            if (HasSharplyMovingFastEma(1, tickSize)) return 1;
            if (HasSharplyMovingFastEma(-1, tickSize)) return -1;
            return GetSlowEmaDirection();
        }

        // Keep the live PB projection aligned with the display rule.  The two
        // completed lead-in bars must step in the trade direction; the live
        // PB candidate itself may form its normal rejection tail.
        private bool HasPinBarLeadInStructure(int direction) {
            for (int barsBack = 1; barsBack <= 2; barsBack++) {
                int priorBar = barsBack + 1;
                bool passes = direction > 0
                    ? Bars.Low[barsBack] >= Bars.Low[priorBar] &&
                      Bars.Close[barsBack] > Bars.Open[barsBack] &&
                      Bars.Close[barsBack] > Bars.Close[priorBar]
                    : Bars.High[barsBack] <= Bars.High[priorBar] &&
                      Bars.Close[barsBack] < Bars.Open[barsBack] &&
                      Bars.Close[barsBack] < Bars.Close[priorBar];
                if (!passes)
                    return false;
            }
            return true;
        }

        private bool IsStrongPinBarContinuationValid(int direction,
                                                       double projectedLow,
                                                       double projectedHigh,
                                                       double tickSize) {
            double fastSlope = GetAngle(m_FastEMA[0], m_FastEMA[3], 3, tickSize);
            double slowSlope = GetAngle(m_SlowEMA[0], m_SlowEMA[3], 3, tickSize);
            double separation = Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) / tickSize;
            return direction > 0
                ? Bars.Close[1] > Bars.Open[1] && projectedLow >= m_FastEMA[0] &&
                  separation >= StrongContinuationSeparationTicks &&
                  fastSlope >= StrongContinuationFastSlopeDegrees &&
                  slowSlope >= StrongContinuationSlowSlopeDegrees
                : Bars.Close[1] < Bars.Open[1] && projectedHigh <= m_FastEMA[0] &&
                  separation >= StrongContinuationSeparationTicks &&
                  fastSlope <= -StrongContinuationFastSlopeDegrees &&
                  slowSlope <= -StrongContinuationSlowSlopeDegrees;
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
                                             GetActivePinBarRangeTicks() -
                                             m_PinProjectionTailTicks);
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
            // Match RangeEMA24Bounce: a completed range may finish within
            // one quarter tick of the 24 EMA without crossing it exactly.
            double tolerance = 0.25 * tickSize;
            return direction > 0
                ? Bars.Low[0] <= m_SlowEMA[0] + tolerance
                : Bars.High[0] >= m_SlowEMA[0] - tolerance;
        }

        // Live counterpart of RangeEMA8Bounce: project the range bar that
        // could finish as a qualifying 8 EMA bounce, then stage its breakout
        // only after the projected tail has actually been reached.
        private void UpdateEma8BounceProjection(double tickSize, int currentPosition) {
            if (!Enable8EMABounceTrading || currentPosition != 0 ||
                m_ShiftProjectionActive) {
                ClearEma8BounceProjectionLines();
                return;
            }
            int direction = GetEma8BounceDirection(tickSize);
            if (direction == 0 || !IsEntryDirectionAllowed(direction)) {
                ClearEma8BounceProjectionLines();
                return;
            }
            double projectedLow, projectedHigh;
            if (!TryGetEma8BounceProjectionPrices(direction, tickSize,
                                                    out projectedLow, out projectedHigh)) {
                ClearEma8BounceProjectionLines();
                return;
            }
            bool tailReached = direction > 0
                ? Bars.Low[0] <= projectedLow + tickSize * 0.1
                : Bars.High[0] >= projectedHigh - tickSize * 0.1;
            double tail = direction > 0 ? projectedLow : projectedHigh;
            double completion = direction > 0 ? projectedHigh : projectedLow;
            // A forming 8-EMA bounce is already a competing chart setup.
            // Keep its possible completion in the display arbitration before
            // the tail is reached; only the Valid flag below controls whether
            // it may replace the working entry order.
            m_Ema8EntryCandidateDirection = direction;
            m_Ema8EntryCandidatePrice = RoundToTick(completion, tickSize);
            UpdateEma8BounceLine(ref m_Ema8BounceTailLine, tail, Color.Gray,
                                  ETLStyle.ToolDashed, 1);
            UpdateEma8BounceLine(ref m_Ema8BounceCompletionLine, completion,
                                  tailReached ? Color.MediumSeaGreen : Color.Gray,
                                  ETLStyle.ToolSolid, 2);
            UpdateEma8BounceLabel(completion, direction, tailReached);
            if (!tailReached) return;
            m_Ema8EntryCandidateValid = true;
        }

        private int GetEma8BounceDirection(double tickSize) {
            if (Bars.CurrentBar < 9) return 0;
            int direction = m_FastEMA[0] > m_SlowEMA[0] ? 1 :
                            m_FastEMA[0] < m_SlowEMA[0] ? -1 : 0;
            if (direction == 0 || Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) / tickSize < Ema8MinSeparationTicks)
                return 0;
            if (!HasRequiredEmaFan(direction, tickSize)) return 0;
            // An 8-EMA bounce must be a genuine two-bar pullback into the
            // EMA. A one-bar touch is no longer an 8E setup.
            if (!HasTwoBarCounterTrendEma8Pullback(direction))
                return 0;
            double fastBest = GetBestDirectionalEmaSlope(m_FastEMA, direction, tickSize);
            double slowBest = GetBestDirectionalEmaSlope(m_SlowEMA, direction, tickSize);
            bool slopes = direction > 0
                ? fastBest >= Ema8MinFastSlopeDegrees && slowBest >= Ema8MinSlowSlopeDegrees
                : fastBest <= -Ema8MinFastSlopeDegrees && slowBest <= -Ema8MinSlowSlopeDegrees;
            return slopes ? direction : 0;
        }

        private double GetBestDirectionalEmaSlope(XAverage ema, int direction, double tickSize) {
            double best = direction > 0 ? Double.NegativeInfinity : Double.PositiveInfinity;
            for (int barsBack = 0; barsBack <= 6; barsBack++) {
                double angle = GetAngle(ema[barsBack], ema[barsBack + 3], 3, tickSize);
                best = direction > 0 ? Math.Max(best, angle) : Math.Min(best, angle);
            }
            return best;
        }

        private double GetBestPriorDirectionalEmaSlope(XAverage ema, int direction,
                                                        double tickSize) {
            double best = direction > 0 ? Double.NegativeInfinity : Double.PositiveInfinity;
            for (int barsBack = 1; barsBack <= 6; barsBack++) {
                double angle = GetAngle(ema[barsBack], ema[barsBack + 3], 3, tickSize);
                best = direction > 0 ? Math.Max(best, angle) : Math.Min(best, angle);
            }
            return best;
        }

        private bool TryGetEma8BounceProjectionPrices(int direction, double tickSize,
                                                        out double projectedLow, out double projectedHigh) {
            double range = GetActiveRangeTicks(tickSize) * tickSize;
            double ema = m_FastEMA[0];
            double localReference = direction > 0 ? Math.Min(Bars.Low[1], Bars.Low[2]) :
                                                    Math.Max(Bars.High[1], Bars.High[2]);
            projectedLow = projectedHigh = 0;
            if (direction > 0) {
                double maximumPenetration = Ema8MaxPenetrationTicks +
                    Ema8PenetrationRoundingAllowanceTicks - 0.000001;
                // Two counter-trend bars are now mandatory, so either a
                // shallow touch or a normal penetration may complete setup.
                double minimumPenetration = 0;
                double lower = Math.Max(Bars.High[0] - range,
                                        ema - maximumPenetration * tickSize);
                double upper = Math.Min(Bars.Low[0],
                                        ema - minimumPenetration * tickSize);
                upper = Math.Min(upper, localReference - Ema8MinLocalDisplacementTicks * tickSize);
                if (lower > upper) return false;
                projectedLow = RoundDownToTick(upper, tickSize);
                projectedHigh = projectedLow + range;
                return projectedHigh > Bars.Open[0] && projectedHigh >= ema;
            }
            double minimumPenetrationShort = 0;
            double maximumPenetrationShort = Ema8MaxPenetrationTicks +
                Ema8PenetrationRoundingAllowanceTicks - 0.000001;
            double highLower = Math.Max(Bars.High[0], ema + minimumPenetrationShort * tickSize);
            highLower = Math.Max(highLower, localReference + Ema8MinLocalDisplacementTicks * tickSize);
            double highUpper = Math.Min(Bars.Low[0] + range,
                                        ema + maximumPenetrationShort * tickSize);
            if (highLower > highUpper) return false;
            projectedHigh = RoundUpToTick(highLower, tickSize);
            projectedLow = projectedHigh - range;
            return projectedLow < Bars.Open[0] && projectedLow <= ema;
        }

        private bool HasTwoBarCounterTrendEma8Pullback(int direction) {
            return direction > 0
                ? Bars.Close[1] < Bars.Open[1] && Bars.Close[2] < Bars.Open[2]
                : direction < 0 && Bars.Close[1] > Bars.Open[1] &&
                  Bars.Close[2] > Bars.Open[2];
        }

        private void UpdateEma8BounceLine(ref ITrendLineObject line, double price, Color color,
                                          ETLStyle style, int size) {
            ChartPoint begin = new ChartPoint(Bars.Time[0], price);
            ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), price);
            if (line == null) line = DrwTrendLine.Create(begin, end);
            if (line == null) return;
            line.Begin = begin; line.End = end; line.ExtRight = true;
            line.Color = color; line.Style = style; line.Size = size;
        }

        private void UpdateEma8BounceLabel(double price, int direction, bool active) {
            ChartPoint point = new ChartPoint(Bars.Time[0], price);
            if (m_Ema8BounceLabel == null) { m_Ema8BounceLabel = DrwText.Create(point, "8E"); m_Ema8BounceLabel.Size = 9; m_Ema8BounceLabel.HStyle = ETextStyleH.Center; }
            m_Ema8BounceLabel.Location = point; m_Ema8BounceLabel.Text = "8E";
            m_Ema8BounceLabel.Color = active ? Color.MediumSeaGreen : Color.Gray;
            m_Ema8BounceLabel.VStyle = direction > 0 ? ETextStyleV.Above : ETextStyleV.Below;
        }

        private void ClearEma8BounceProjectionLines() {
            if (m_Ema8BounceTailLine != null) { m_Ema8BounceTailLine.Delete(); m_Ema8BounceTailLine = null; }
            if (m_Ema8BounceCompletionLine != null) { m_Ema8BounceCompletionLine.Delete(); m_Ema8BounceCompletionLine = null; }
            if (m_Ema8BounceLabel != null) { m_Ema8BounceLabel.Delete(); m_Ema8BounceLabel = null; }
        }

        private int GetEmaBounceDirection() {
            if (Bars.CurrentBar < 9) return 0;
            int direction = m_FastEMA[0] > m_SlowEMA[0] ? 1 :
                            m_FastEMA[0] < m_SlowEMA[0] ? -1 : 0;
            if (direction == 0) return 0;

            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;
            double currentSeparation = Math.Abs(m_FastEMA[0] - m_SlowEMA[0]) /
                                       tickSize;
            if (currentSeparation < Ema24MinCurrentSeparationTicks ||
                GetBestDirectionalEmaSeparation(direction) <
                    Ema24MinBestSeparationTicks)
                return 0;
            if (!HasRequiredEmaFan(direction, tickSize)) return 0;

            double bestSlowSlope = GetBestDirectionalEmaSlope(m_SlowEMA,
                                                               direction, tickSize);
            bool slopePass = direction > 0
                ? bestSlowSlope >= Ema24MinSlowSlopeDegrees
                : bestSlowSlope <= -Ema24MinSlowSlopeDegrees;
            return slopePass ? direction : 0;
        }

        private double GetBestDirectionalEmaSeparation(int direction) {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;
            double best = Double.NegativeInfinity;
            for (int barsBack = 0; barsBack <= 6; barsBack++) {
                double separation = direction > 0
                    ? m_FastEMA[barsBack] - m_SlowEMA[barsBack]
                    : m_SlowEMA[barsBack] - m_FastEMA[barsBack];
                best = Math.Max(best, separation / tickSize);
            }
            return best;
        }

        // Every PB, 8-EMA, and 24-EMA entry needs a properly fanned EMA
        // stack. The 8/24 and 24/50 gaps must each span at least half a bar
        // (2.5 ticks on 5-tick bars; 4 ticks on 8-tick bars).
        private bool HasRequiredEmaFan(int direction, double tickSize) {
            double minimumGap = GetActiveRangeTicks(tickSize) * 0.5;
            return direction > 0
                ? m_FastEMA[0] > m_SlowEMA[0] &&
                  m_SlowEMA[0] > m_ProfileEMA[0] &&
                  (m_FastEMA[0] - m_SlowEMA[0]) / tickSize >= minimumGap &&
                  (m_SlowEMA[0] - m_ProfileEMA[0]) / tickSize >= minimumGap
                : direction < 0 && m_FastEMA[0] < m_SlowEMA[0] &&
                  m_SlowEMA[0] < m_ProfileEMA[0] &&
                  (m_SlowEMA[0] - m_FastEMA[0]) / tickSize >= minimumGap &&
                  (m_ProfileEMA[0] - m_SlowEMA[0]) / tickSize >= minimumGap;
        }

        // Shared gate for PB, 8-EMA, and 24-EMA signals. A profile must hold
        // through four consecutive bars and must not be compressing as it does
        // when a formerly directional market transitions into congestion.
        private int GetTradeProfileDirection(double tickSize) {
            return GetTradeProfileDirectionAt(0, tickSize);
        }

        // A 24-EMA bounce is a pullback in an established slower trend. The
        // fast 8 EMA can legitimately curl against that trend while price
        // tests the 24, so this version deliberately requires only the 24
        // and 50 EMA slopes in addition to the normal alignment and spread.
        private int Get24EmaTradeProfileDirection(double tickSize) {
            double fast = m_FastEMA[0];
            double slow = m_SlowEMA[0];
            double trend = m_ProfileEMA[0];
            int direction = fast > slow && slow > trend ? 1 :
                            fast < slow && slow < trend ? -1 : 0;
            if (direction == 0) return 0;
            double fastSlow = Math.Abs(fast - slow) / tickSize;
            double slowTrend = Math.Abs(slow - trend) / tickSize;
            double fastTrend = Math.Abs(fast - trend) / tickSize;
            if (fastSlow < ProfileMinFastSlowSeparationTicks ||
                slowTrend < ProfileMinSlowTrendSeparationTicks ||
                fastTrend < ProfileMinFastTrendSeparationTicks)
                return 0;
            double slowSlope = GetBestPriorDirectionalEmaSlope(m_SlowEMA, direction,
                                                                 tickSize);
            double trendSlope = GetAngle(m_ProfileEMA[0],
                                         m_ProfileEMA[ProfileSlopeBars],
                                         ProfileSlopeBars, tickSize);
            return direction > 0
                ? slowSlope >= ProfileMinSlowSlopeDegrees &&
                  trendSlope >= ProfileMinTrendSlopeDegrees ? 1 : 0
                : slowSlope <= -ProfileMinSlowSlopeDegrees &&
                  trendSlope <= -ProfileMinTrendSlopeDegrees ? -1 : 0;
        }

        private int GetTradeProfileDirectionAt(int barsBack, double tickSize) {
            double fast = m_FastEMA[barsBack];
            double slow = m_SlowEMA[barsBack];
            double trend = m_ProfileEMA[barsBack];
            int direction = fast > slow && slow > trend ? 1 :
                            fast < slow && slow < trend ? -1 : 0;
            if (direction == 0) return 0;

            double fastSlowSeparation = Math.Abs(fast - slow) / tickSize;
            double slowTrendSeparation = Math.Abs(slow - trend) / tickSize;
            double fastTrendSeparation = Math.Abs(fast - trend) / tickSize;
            if (fastSlowSeparation < ProfileMinFastSlowSeparationTicks ||
                slowTrendSeparation < ProfileMinSlowTrendSeparationTicks ||
                fastTrendSeparation < ProfileMinFastTrendSeparationTicks)
                return 0;

            double fastSlope = GetAngle(m_FastEMA[barsBack],
                                        m_FastEMA[barsBack + ProfileSlopeBars],
                                        ProfileSlopeBars, tickSize);
            double slowSlope = GetAngle(m_SlowEMA[barsBack],
                                        m_SlowEMA[barsBack + ProfileSlopeBars],
                                        ProfileSlopeBars, tickSize);
            double trendSlope = GetAngle(m_ProfileEMA[barsBack],
                                         m_ProfileEMA[barsBack + ProfileSlopeBars],
                                         ProfileSlopeBars, tickSize);
            if (direction > 0)
                return fastSlope >= ProfileMinFastSlopeDegrees &&
                       slowSlope >= ProfileMinSlowSlopeDegrees &&
                       trendSlope >= ProfileMinTrendSlopeDegrees ? 1 : 0;
            return fastSlope <= -ProfileMinFastSlopeDegrees &&
                   slowSlope <= -ProfileMinSlowSlopeDegrees &&
                   trendSlope <= -ProfileMinTrendSlopeDegrees ? -1 : 0;
        }

        private void ResetAutomaticEntryCandidates() {
            m_PinEntryCandidateValid = false;
            m_PinEntryCandidateDirection = 0;
            m_PinEntryCandidatePrice = 0;
            m_PinEntryCandidateBodyTicks = 0;
            m_EmaEntryCandidateValid = false;
            m_EmaEntryCandidateDirection = 0;
            m_EmaEntryCandidatePrice = 0;
            m_Ema8EntryCandidateValid = false;
            m_Ema8EntryCandidateDirection = 0;
            m_Ema8EntryCandidatePrice = 0;
        }

        private void ReconcileAutomaticEntryCandidates(double tickSize,
                                                        int currentPosition) {
            ApplyProjectionDisplayPriority();
            if (currentPosition != 0 || !m_AutoEntryArmed ||
                m_ShiftProjectionActive ||
                m_ActiveEntrySetup == EEntrySetup.ShiftProjection) return;

            EEntrySetup selectedSetup = EEntrySetup.None;
            int selectedDirection = 0;
            double selectedPrice = 0;
            // Use the same nearest-target rule for execution as for the
            // drawing.  Earlier code gave 8-EMA setups absolute priority,
            // which could leave a farther stop working while a nearer valid
            // setup was displayed.
            SelectCloserEntryCandidate(m_Ema8EntryCandidateValid,
                                       EEntrySetup.Ema8Bounce,
                                       m_Ema8EntryCandidateDirection,
                                       m_Ema8EntryCandidatePrice,
                                       ref selectedSetup, ref selectedDirection,
                                       ref selectedPrice);
            SelectCloserEntryCandidate(m_EmaEntryCandidateValid,
                                       EEntrySetup.Ema24Bounce,
                                       m_EmaEntryCandidateDirection,
                                       m_EmaEntryCandidatePrice,
                                       ref selectedSetup, ref selectedDirection,
                                       ref selectedPrice);
            SelectCloserEntryCandidate(m_PinEntryCandidateValid,
                                       EEntrySetup.PinBar,
                                       m_PinEntryCandidateDirection,
                                       m_PinEntryCandidatePrice,
                                       ref selectedSetup, ref selectedDirection,
                                       ref selectedPrice);

            if (selectedSetup == EEntrySetup.None) {
                if (m_ActiveEntrySetup == EEntrySetup.PinBar ||
                    m_ActiveEntrySetup == EEntrySetup.Ema24Bounce ||
                    m_ActiveEntrySetup == EEntrySetup.Ema8Bounce) {
                    CancelWorkingEntryOrders();
                    ClearPendingEntry();
                }
                return;
            }

            if (!IsEntryDirectionAllowed(selectedDirection)) {
                if (m_ActiveEntrySetup == EEntrySetup.PinBar ||
                    m_ActiveEntrySetup == EEntrySetup.Ema24Bounce ||
                    m_ActiveEntrySetup == EEntrySetup.Ema8Bounce) {
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
            else if (selectedSetup == EEntrySetup.Ema24Bounce) {
                m_EmaBounceOrderBar = Bars.CurrentBar;
                m_StagedPinBodyTicks = 0;
            } else {
                m_Ema8BounceOrderBar = Bars.CurrentBar;
                m_StagedPinBodyTicks = 0;
            }
        }

        private void SelectCloserEntryCandidate(bool candidateValid,
                                                EEntrySetup candidateSetup,
                                                int candidateDirection,
                                                double candidatePrice,
                                                ref EEntrySetup selectedSetup,
                                                ref int selectedDirection,
                                                ref double selectedPrice) {
            if (!candidateValid || candidateDirection == 0 || candidatePrice <= 0)
                return;
            if (selectedSetup == EEntrySetup.None ||
                Math.Abs(candidatePrice - Bars.Close[0]) <
                Math.Abs(selectedPrice - Bars.Close[0])) {
                selectedSetup = candidateSetup;
                selectedDirection = candidateDirection;
                selectedPrice = candidatePrice;
            }
        }

        // Each recognizer evaluates independently, but only one live setup
        // should occupy the chart. Keep the completion/target that is nearest
        // to the current price; the others remain evaluated and can take over
        // immediately if their target becomes closer or the winner fails.
        private void ApplyProjectionDisplayPriority() {
            EEntrySetup visibleSetup = EEntrySetup.None;
            double visiblePrice = 0;
            int projectedSetupCount = 0;

            SelectCloserProjection(m_PinEntryCandidateDirection,
                                    m_PinEntryCandidatePrice,
                                    EEntrySetup.PinBar, ref projectedSetupCount,
                                    ref visibleSetup, ref visiblePrice);
            SelectCloserProjection(m_EmaEntryCandidateDirection,
                                    m_EmaEntryCandidatePrice,
                                    EEntrySetup.Ema24Bounce, ref projectedSetupCount,
                                    ref visibleSetup, ref visiblePrice);
            SelectCloserProjection(m_Ema8EntryCandidateDirection,
                                    m_Ema8EntryCandidatePrice,
                                    EEntrySetup.Ema8Bounce, ref projectedSetupCount,
                                    ref visibleSetup, ref visiblePrice);

            // Do not suppress a single tentative projection: it is the early
            // PB line that must remain visible while the bar can still form.
            if (projectedSetupCount < 2) return;

            if (visibleSetup != EEntrySetup.PinBar)
                ClearPinBarProjectionLines();
            if (visibleSetup != EEntrySetup.Ema24Bounce)
                ClearEmaBounceProjectionLines();
            if (visibleSetup != EEntrySetup.Ema8Bounce)
                ClearEma8BounceProjectionLines();
        }

        private void SelectCloserProjection(int candidateDirection,
                                             double candidatePrice,
                                             EEntrySetup candidateSetup,
                                             ref int projectedSetupCount,
                                             ref EEntrySetup visibleSetup,
                                             ref double visiblePrice) {
            if (candidateDirection == 0 || candidatePrice <= 0) return;
            projectedSetupCount++;
            if (visibleSetup == EEntrySetup.None ||
                Math.Abs(candidatePrice - Bars.Close[0]) <
                Math.Abs(visiblePrice - Bars.Close[0])) {
                visibleSetup = candidateSetup;
                visiblePrice = candidatePrice;
            }
        }

        private bool TryGetEmaBounceProjectionPrices(int direction, double tickSize,
                                                      out double projectedLow,
                                                      out double projectedHigh) {
            double range = GetActiveRangeTicks(tickSize) * tickSize;
            double currentEma = m_SlowEMA[0];
            double roundingTolerance = tickSize * 0.1;
            double nearTouchAllowance = Ema24TouchToleranceTicks * tickSize;
            projectedLow = projectedHigh = 0;
            if (direction == 0 || range <= 0) return false;

            if (direction > 0) {
                // A live/finished range bar may reject just above the 24 EMA.
                // Treat the same 0.75-tick near-touch used by the visual
                // indicator as a valid low-side contact.
                double projectedTouchLow = currentEma + nearTouchAllowance;
                double lowestPossibleLow = Math.Max(Bars.High[0] - range,
                                                     projectedTouchLow - range);
                double highestPossibleLow = Math.Min(Bars.Low[0],
                                                      projectedTouchLow);
                if (lowestPossibleLow > highestPossibleLow + roundingTolerance) return false;
                projectedLow = RoundDownToTick(highestPossibleLow, tickSize);
                if (projectedLow < lowestPossibleLow - roundingTolerance) return false;
                projectedHigh = projectedLow + range;
            } else {
                // Bearish mirror: accept a high-side rejection within the
                // same near-touch allowance below the live 24 EMA.
                double projectedTouchHigh = currentEma - nearTouchAllowance;
                double lowestPossibleHigh = Math.Max(Bars.High[0],
                                                      projectedTouchHigh);
                double highestPossibleHigh = Math.Min(Bars.Low[0] + range,
                                                      projectedTouchHigh + range);
                if (lowestPossibleHigh > highestPossibleHigh + roundingTolerance) return false;
                projectedHigh = RoundUpToTick(lowestPossibleHigh, tickSize);
                if (projectedHigh > highestPossibleHigh + roundingTolerance) return false;
                projectedLow = projectedHigh - range;
            }

            projectedLow = RoundToTick(projectedLow, tickSize);
            projectedHigh = RoundToTick(projectedHigh, tickSize);
            // The projected completion is the close of the finished range
            // bar. Require the same close-side and bar-color relationship as
            // RangeEMA24Bounce before a breakout order can be staged.
            return direction > 0
                ? projectedHigh > m_SlowEMA[0] && projectedHigh > Bars.Open[0]
                : projectedLow < m_SlowEMA[0] && projectedLow <= Bars.Open[0];
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
            Color color = active ? Color.DarkViolet : Color.Gray;
            UpdateEmaBounceLine(ref m_EmaBounceCompletionLine, price, color,
                                ETLStyle.ToolSolid, 2);
        }

        private void UpdateEmaBounceLine(ref ITrendLineObject line, double price,
                                         Color color, ETLStyle style, int size) {
            ChartPoint begin = new ChartPoint(Bars.Time[0], price);
            ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), price);
            if (line == null) {
                line = DrwTrendLine.Create(begin, end);
                if (line == null) return;
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
                m_EmaBounceLabel = DrwText.Create(point, "24E");
                m_EmaBounceLabel.Size = 9;
                m_EmaBounceLabel.HStyle = ETextStyleH.Center;
            }
            m_EmaBounceLabel.Location = point;
            m_EmaBounceLabel.Text = "24E";
            m_EmaBounceLabel.Color = active ? Color.DarkViolet : Color.Gray;
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

        private void StartShiftProjectionEntry(double tickSize) {
            if (StrategyInfo.MarketPosition != 0) return;

            int direction = m_FastEMA[0] > m_SlowEMA[0] ? 1 :
                            m_FastEMA[0] < m_SlowEMA[0] ? -1 : 0;
            // Reject a wrong-side manual request before it can cancel an
            // already-working permitted entry.
            if (direction != 0 && !IsEntryDirectionAllowed(direction)) return;

            // Shift replaces a pending entry with a single manually requested
            // projection. If it begins from UNARMED, it also latches the
            // projection's permitted direction for the automatic PB/EMA
            // setup modes that follow.
            if (!m_AutoEntryArmed)
                ArmAutomatedEntryMode(tickSize, direction);

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
                                                double price, bool active) {
            try {
                ChartPoint begin = new ChartPoint(Bars.Time[0], price);
                ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), price);
                ITrendLineObject target = line;
                if (target == null) {
                    target = DrwTrendLine.Create(begin, end);
                    if (target == null) return;
                    line = target;
                } else {
                    target.Begin = begin;
                    target.End = end;
                }
                // Completion is also the actionable entry level. Range bars do
                // not have predictable future timestamps, so extend the line.
                target.ExtRight = true;
                target.Color = active ? Color.DodgerBlue : Color.Gray;
                target.Style = ETLStyle.ToolSolid;
                target.Size = 2;
            } catch (NullReferenceException) {
                line = null;
            }
        }

        private void UpdatePinBarTailProjectionLine(double price) {
            try {
                ChartPoint begin = new ChartPoint(Bars.Time[0], price);
                ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), price);
                ITrendLineObject target = m_PinBarTailLine;
                if (target == null) {
                    target = DrwTrendLine.Create(begin, end);
                    if (target == null) return;
                    m_PinBarTailLine = target;
                } else {
                    target.Begin = begin;
                    target.End = end;
                }
                target.ExtRight = true;
                target.Color = Color.Gray;
                target.Style = ETLStyle.ToolDashed;
                target.Size = 1;
            } catch (NullReferenceException) {
                m_PinBarTailLine = null;
            }
        }

        private void ClearPinBarProjectionLines() {
            if (m_PinBarTailLine != null) { m_PinBarTailLine.Delete(); m_PinBarTailLine = null; }
            if (m_PinBarCompletionLine != null) { m_PinBarCompletionLine.Delete(); m_PinBarCompletionLine = null; }
            if (m_PinBarLabel != null) { m_PinBarLabel.Delete(); m_PinBarLabel = null; }
        }

        private void UpdatePinBarProjectionLabel(double price, int direction,
                                                 bool active, int bodyTicks) {
            try {
                // Keep the label inside the visible pane by extending its text
                // left from the current bar rather than placing it at a future
                // time. MultiCharts can dispose/rebuild a drawing between
                // ticks, so use a local reference and treat that as transient.
                ChartPoint point = new ChartPoint(Bars.Time[0], price);
                string text = "PB" + GetPinBarDisplayLevel(
                    GetActivePinBarRangeTicks(), bodyTicks);
                ITextObject label = m_PinBarLabel;
                if (label == null) {
                    label = DrwText.Create(point, text);
                    if (label == null) return;
                    m_PinBarLabel = label;
                    label.Size = 10;
                    label.HStyle = ETextStyleH.Center;
                }
                label.Location = point;
                label.Text = text;
                label.VStyle = direction > 0 ? ETextStyleV.Above : ETextStyleV.Below;
                label.Color = active ? Color.DodgerBlue : Color.Gray;
            } catch (NullReferenceException) {
                // A drawing may disappear while the chart is being rebuilt.
                // Retry creation on the next calculation instead of stopping
                // the strategy's order-management loop.
                m_PinBarLabel = null;
            }
        }


        private bool IsF12Held(Keys eventKeys) {
            if ((eventKeys & Keys.KeyCode) == Keys.F12) return true;
            try {
                return (GetAsyncKeyState((int)Keys.F12) & 0x8000) != 0;
            } catch {
                return false;
            }
        }

        private bool IsF1Held(Keys eventKeys) {
            Keys keyCode = eventKeys & Keys.KeyCode;
            if (keyCode == Keys.F1) return true;
            try {
                return (GetAsyncKeyState((int)Keys.F1) & 0x8000) != 0;
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

        private bool IsF2Held(Keys eventKeys) {
            if ((eventKeys & Keys.KeyCode) == Keys.F2) return true;
            try {
                return (GetAsyncKeyState((int)Keys.F2) & 0x8000) != 0;
            } catch {
                return false;
            }
        }

        private bool IsF3Held(Keys eventKeys) {
            if ((eventKeys & Keys.KeyCode) == Keys.F3) return true;
            try {
                return (GetAsyncKeyState((int)Keys.F3) & 0x8000) != 0;
            } catch {
                return false;
            }
        }

        private bool IsF4Held(Keys eventKeys) {
            if ((eventKeys & Keys.KeyCode) == Keys.F4) return true;
            try {
                return (GetAsyncKeyState((int)Keys.F4) & 0x8000) != 0;
            } catch {
                return false;
            }
        }

        private bool IsF5Held(Keys eventKeys) {
            if ((eventKeys & Keys.KeyCode) == Keys.F5) return true;
            try {
                return (GetAsyncKeyState((int)Keys.F5) & 0x8000) != 0;
            } catch {
                return false;
            }
        }

        private void ToggleStopLossMode(double tickSize) {
            m_ActiveStopLossTicks = m_ActiveStopLossTicks == 7 ? 12 : 7;

            // Apply the new protective distance immediately to an open trade;
            // future trades use the same active setting through the normal
            // fill-initialization path.
            if (StrategyInfo.MarketPosition != 0) {
                double entryPrice = StrategyInfo.AvgEntryPrice != 0
                    ? StrategyInfo.AvgEntryPrice
                    : Bars.Close[0];
                double stopDistance = m_ActiveStopLossTicks * tickSize;
                m_ProtectiveStopPrice = StrategyInfo.MarketPosition > 0
                    ? RoundToTick(entryPrice - stopDistance, tickSize)
                    : RoundToTick(entryPrice + stopDistance, tickSize);
                UpdateStopLine();
                SubmitActiveExitOrders(StrategyInfo.MarketPosition);
            }
        }

        private void ToggleProfitManagementMode(double tickSize) {
            UseOppositeColorExitForProfits = !UseOppositeColorExitForProfits;
            // A pending entry may have been submitted as a native stop in
            // five-tick mode. Re-stage it on the next calculation using the
            // newly selected entry behavior.
            CancelWorkingEntryOrders();

            if (StrategyInfo.MarketPosition == 0) return;

            if (UseOppositeColorExitForProfits) {
                // Let-it-run mode: remove the fixed target and let the
                // opposite-color trailing stop arm from live price action.
                m_ProfitTargetPrice = 0;
                if (m_TargetLine != null) {
                    m_TargetLine.Delete();
                    m_TargetLine = null;
                }
                m_OppositeColorProfitArmed = false;
                m_OppositeColorStopPrice = 0;
                ClearRecoveryColorStopProjection();
            } else {
                // Five-tick mode: remove the running stop and install a fixed
                // five-tick target immediately for the open trade.
                m_OppositeColorProfitArmed = false;
                m_OppositeColorStopPrice = 0;
                ClearRecoveryColorStopProjection();
                double entryPrice = StrategyInfo.AvgEntryPrice != 0
                    ? StrategyInfo.AvgEntryPrice
                    : Bars.Close[0];
                m_ProfitTargetPrice = StrategyInfo.MarketPosition > 0
                    ? RoundToTick(entryPrice + OneBarProfitTargetTicks * tickSize, tickSize)
                    : RoundToTick(entryPrice - OneBarProfitTargetTicks * tickSize, tickSize);
                UpdateTargetLine();
            }

            SubmitActiveExitOrders(StrategyInfo.MarketPosition);
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
                UpdateHUD(true);
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
            return OneBarProfitTargetTicks;
        }

        private void UpdateOppositeColorExitManagement(int currentPosition,
                                                       double tickSize) {
            bool colorCloseExitActive = UseOppositeColorExitForProfits;
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
            // Stage the native reversal stop one tick beyond the projected
            // opposite-color completion, so a merely completed reversal bar
            // does not trigger the exit until price trades through it.
            double stagedReversalStop = currentPosition > 0
                ? RoundToTick(projectedOppositeClose - tickSize, tickSize)
                : RoundToTick(projectedOppositeClose + tickSize, tickSize);
            if (currentPosition > 0) {
                if (m_OppositeColorStopPrice <= 0 ||
                    stagedReversalStop > m_OppositeColorStopPrice)
                    m_OppositeColorStopPrice = stagedReversalStop;
            } else {
                if (m_OppositeColorStopPrice <= 0 ||
                    stagedReversalStop < m_OppositeColorStopPrice)
                    m_OppositeColorStopPrice = stagedReversalStop;
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

            // This is a visual trade-result marker, not the broker fill
            // marker. Keep it outside the entry bar so a red/green result
            // arrow never obscures the candle: below the tail for longs and
            // above the tail for shorts.
            double directionPrice = currentPosition > 0
                ? Bars.Low[barsBack] - tickSize
                : Bars.High[barsBack] + tickSize;
            IArrowObject directionMarker = DrwArrow.Create(
                new ChartPoint(Bars.Time[barsBack], directionPrice), currentPosition < 0);
            // The result is not known until the position closes.  It is then
            // changed to green for profit or red for a loss/break-even trade.
            directionMarker.Color = Color.DimGray;
            directionMarker.Size = 5;
            m_TradeEntryMarkers.Add(directionMarker);
            m_ActiveTradeEntryArrow = directionMarker;

            string entryType = entrySetup == EEntrySetup.PinBar
                ? "P" + GetPinBarDisplayLevel(GetActivePinBarRangeTicks(),
                                                Math.Max(0, pinBodyTicks))
                :
                               entrySetup == EEntrySetup.Ema24Bounce ? "B" :
                               entrySetup == EEntrySetup.Ema8Bounce ? "B8" :
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
            // The completed bar reveals the actual ATIC/range-bar size. Use
            // it once available so a strategy copied from a 5-tick MES chart
            // automatically follows an 8-tick MYM/M2K chart without relying
            // on a contract-name exception. The input remains the startup
            // fallback before any completed bar has been observed.
            if (m_AutoRangeTicks > 0) return m_AutoRangeTicks;
            return RangeSizeTicks > 0 ? RangeSizeTicks : 7;
        }

        private void UpdateProjectedEntryLine() {
            // Pin bars and EMA bounces own a dedicated combined
            // completion/entry line. Do not cover it with the generic pending
            // entry drawing when the native stop order is staged.
            if (m_ActiveEntrySetup == EEntrySetup.PinBar ||
                m_ActiveEntrySetup == EEntrySetup.Ema24Bounce ||
                m_ActiveEntrySetup == EEntrySetup.Ema8Bounce) {
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
            try {
                ChartPoint begin = new ChartPoint(Bars.Time[0], price);
                ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), price);
                ITrendLineObject target = line;
                if (target == null)
                    target = DrwTrendLine.Create(begin, end);
                else {
                    target.Begin = begin;
                    target.End = end;
                }
                if (target == null) return;
                line = target;
                target.ExtRight = true;
                target.Color = color;
                target.Style = style;
                target.Size = size;
            } catch (NullReferenceException) {
                line = null;
            }
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

        private void UpdateHUD(bool force = false) {
            DateTime now = DateTime.UtcNow;
            bool drawingsReady = m_HUDLabel != null &&
                                 m_BrokerStatusLabel != null &&
                                 m_ControlsHintLabel != null &&
                                 m_ControlsActionHintLabel != null;
            if (!force && drawingsReady &&
                (now - m_LastHudRefreshAt).TotalMilliseconds < HudRefreshMilliseconds)
                return;
            m_LastHudRefreshAt = now;

            // Keep the established per-signal/session calculation intact, but
            // publish it so the bid and ask chart instances display one total.
            double pnl = UpdateAndGetGlobalPnL(StrategyInfo.OpenEquity);
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale; if (tickSize <= 0) tickSize = 0.25;
            string setupScope = GetEnabledSetupScope();
            // Always state the automation state explicitly.  The prior
            // watch-only text was ambiguous: it did not tell the trader that
            // automatic entries were still unarmed.
            string status = "UNARMED | " + setupScope;
            if (m_AutoEntryArmed)
                status = m_ArmedDirection > 0 ? "ARMED BUY" : "ARMED SELL";
            if (m_BuyOrderActive)
                status = m_ActiveEntrySetup == EEntrySetup.Ema24Bounce
                    ? (m_AutoEntryArmed ? "ARMED | 24 EMA ENTRY BUY" : "24 EMA ENTRY BUY") :
                    m_ActiveEntrySetup == EEntrySetup.Ema8Bounce
                        ? (m_AutoEntryArmed ? "ARMED | 8 EMA ENTRY BUY" : "8 EMA ENTRY BUY") :
                    m_ActiveEntrySetup == EEntrySetup.ShiftProjection
                        ? (m_AutoEntryArmed
                            ? (m_ArmedDirection > 0
                                ? "ARMED BUY | SHIFT ENTRY BUY"
                                : "ARMED SELL | SHIFT ENTRY BUY")
                            : "UNARMED SHIFT ENTRY BUY")
                        : (m_AutoEntryArmed ? "ARMED | PIN ENTRY BUY" : "PIN ENTRY BUY");
            if (m_SellOrderActive)
                status = m_ActiveEntrySetup == EEntrySetup.Ema24Bounce
                    ? (m_AutoEntryArmed ? "ARMED | 24 EMA ENTRY SELL" : "24 EMA ENTRY SELL") :
                    m_ActiveEntrySetup == EEntrySetup.Ema8Bounce
                        ? (m_AutoEntryArmed ? "ARMED | 8 EMA ENTRY SELL" : "8 EMA ENTRY SELL") :
                    m_ActiveEntrySetup == EEntrySetup.ShiftProjection
                        ? (m_AutoEntryArmed
                            ? (m_ArmedDirection > 0
                                ? "ARMED BUY | SHIFT ENTRY SELL"
                                : "ARMED SELL | SHIFT ENTRY SELL")
                            : "UNARMED SHIFT ENTRY SELL")
                        : (m_AutoEntryArmed ? "ARMED | PIN ENTRY SELL" : "PIN ENTRY SELL");
            if (StrategyInfo.MarketPosition != 0) {
                status = UseOppositeColorExitForProfits
                    ? "IN TRADE | LET PROFITS RUN"
                    : "IN TRADE | 5-TICK PROFIT";
            }
            if (m_KillModeActive)
                status = m_FlattenRequested
                    ? "FLATTENING"
                    : "UNARMED";
            status += " | STOP " + m_ActiveStopLossTicks + "T";
            string chartRole = IsAskChart ? "ASK / BUY ONLY" : "BID / SELL ONLY";
            string profitMode = UseOppositeColorExitForProfits
                ? "LET RUN"
                : "5T PROFIT";
            string text = string.Format("{0} | {1} | {2} | Session PnL: {3:C2}",
                                        status, chartRole, profitMode, pnl);
            // Keep the session line immediately below the broker line as one
            // compact, unobtrusive status block.
            // Keep the status block clear of live pin/EMA projection labels.
            // GetStatusLabelPoint places this above ask/buy bars and below
            // bid/sell bars, so the same larger offset works on both sides.
            ChartPoint hudPoint = GetStatusLabelPoint(tickSize, 12);
            bool layoutChanged = m_HudLayoutBar != Bars.CurrentBar;
            // MultiCharts can leave the previous text rasterized when a text
            // drawing is mutated during a live redraw. Replace the object on
            // actual status changes so an old status cannot overlap the new
            // one and appear as garbled HUD text.
            if (m_HUDLabel != null && m_LastHudText != null &&
                m_LastHudText != text) {
                m_HUDLabel.Delete();
                m_HUDLabel = null;
            }
            if (m_HUDLabel == null) {
                m_HUDLabel = DrwText.Create(hudPoint, text);
            }
            if (m_HUDLabel == null) return;
            m_HUDLabel.Size = 11;
            // In MultiCharts text drawings, Right keeps the visible left edge
            // at the shared chart point; Left aligns the right edges instead.
            m_HUDLabel.HStyle = ETextStyleH.Right;
            m_HUDLabel.VStyle = GetStatusLabelVerticalStyle();
            Color hudColor = m_AutoEntryArmed ? Color.Green : Color.Black;
            m_HUDLabel.Text = text;
            m_LastHudText = text;
            // Reapply the color every refresh. MultiCharts can recreate a
            // drawing object while retaining this strategy instance's cache;
            // relying on m_LastHudColor then leaves the new object at its
            // default light-blue color instead of the intended black/green.
            m_HUDLabel.Color = hudColor;
            m_LastHudColor = hudColor;
            if (layoutChanged) m_HUDLabel.Location = hudPoint;
            UpdateBrokerStatusLabel(tickSize, layoutChanged);
            UpdateControlsHintLabel(tickSize, layoutChanged);
            m_HudLayoutBar = Bars.CurrentBar;
        }

        private string GetEnabledSetupScope() {
            string scope = "";
            if (EnablePinBarTrading) scope = "PB";
            if (Enable24EMABounceTrading)
                scope += (scope.Length > 0 ? " + " : "") + "24 EMA";
            if (Enable8EMABounceTrading)
                scope += (scope.Length > 0 ? " + " : "") + "8 EMA";
            return scope.Length > 0 ? scope + " WATCH" : "IDLE";
        }

        private ChartPoint GetStatusLabelPoint(double tickSize, int offsetTicks) {
            // Capture the anchor once per forming bar. Moving every text
            // drawing on every new high/low overwhelms MultiCharts' renderer
            // in fast markets and creates the visible smeared afterimage.
            if (m_HudAnchorBar != Bars.CurrentBar) {
                m_HudAnchorBar = Bars.CurrentBar;
                m_HudAnchorTime = Bars.Time[0];
                m_HudAnchorHigh = Bars.High[0];
                m_HudAnchorLow = Bars.Low[0];
            }
            double price = IsAskChart
                ? m_HudAnchorHigh + (offsetTicks * tickSize)
                : m_HudAnchorLow - (offsetTicks * tickSize);
            return new ChartPoint(m_HudAnchorTime, price);
        }

        private ETextStyleV GetStatusLabelVerticalStyle() {
            return IsAskChart ? ETextStyleV.Above : ETextStyleV.Below;
        }

        private void UpdateBrokerStatusLabel(double tickSize, bool layoutChanged) {
            try {
                UpdateBrokerStatusLabelCore(tickSize, layoutChanged);
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

        private void UpdateBrokerStatusLabelCore(double tickSize, bool layoutChanged) {
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

            ChartPoint point = GetStatusLabelPoint(tickSize, 15);
            if (m_BrokerStatusLabel != null && m_LastBrokerStatusText != null &&
                m_LastBrokerStatusText != text) {
                m_BrokerStatusLabel.Delete();
                m_BrokerStatusLabel = null;
            }
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
            if (layoutChanged) m_BrokerStatusLabel.Location = point;
            m_BrokerStatusLabel.Text = text;
            m_LastBrokerStatusText = text;
            // The broker-status drawing is subject to the same MultiCharts
            // recreation behavior as the main HUD label.
            m_BrokerStatusLabel.Color = color;
            m_LastBrokerStatusColor = color;
        }

        private void UpdateControlsHintLabel(double tickSize, bool layoutChanged) {
            const string controlText = "L-click stop marker: Break-even\nF1+Click:\nF2+Click:\nF3+Click:\nF4+Click:\nF5+Click:\nShift+click:\nCtrl+click:\nF11+click:\nEsc+click:";
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
                                actionPadding + (EnablePinBarTrading ? "PB ON" : "PB OFF") + "\n" +
                                actionPadding + (Enable24EMABounceTrading ? "24 EMA ON" : "24 EMA OFF") + "\n" +
                                actionPadding + (Enable8EMABounceTrading ? "8 EMA ON" : "8 EMA OFF") + "\n" +
                                actionPadding + "Toggle Profit: " +
                                (UseOppositeColorExitForProfits ? "LET RUN" : "5T PROFIT") + "\n" +
                                actionPadding + "STOP " + m_ActiveStopLossTicks + "T\n" +
                                actionPadding + "Manual\n" +
                                actionPadding + "Arm/Disarm\n" +
                                actionPadding + "Toggle HUD\n" +
                                actionPadding + "Flatten";
            ChartPoint point = GetStatusLabelPoint(tickSize, 18);
            if (m_ControlsHintLabel == null) {
                m_ControlsHintLabel = DrwText.Create(point, controlText);
            }
            if (m_ControlsHintLabel == null) return;
            m_ControlsHintLabel.Size = 8;
            // Right alignment in MultiCharts places the visible left edge at
            // the chart point, matching the paper-trader status label.
            m_ControlsHintLabel.HStyle = ETextStyleH.Right;
            m_ControlsHintLabel.VStyle = GetStatusLabelVerticalStyle();
            if (layoutChanged) m_ControlsHintLabel.Location = point;
            m_ControlsHintLabel.Color = Color.DarkSlateGray;

            if (m_ControlsActionHintLabel == null) {
                m_ControlsActionHintLabel = DrwText.Create(point, actionText);
            }
            if (m_ControlsActionHintLabel == null) return;
            m_ControlsActionHintLabel.Size = 8;
            m_ControlsActionHintLabel.HStyle = ETextStyleH.Right;
            m_ControlsActionHintLabel.VStyle = GetStatusLabelVerticalStyle();
            if (layoutChanged) m_ControlsActionHintLabel.Location = point;
            if (m_LastControlsActionText != actionText) {
                m_ControlsActionHintLabel.Text = actionText;
                m_LastControlsActionText = actionText;
            }
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
