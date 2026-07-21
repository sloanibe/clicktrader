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
        [Input] public bool EnablePinBarTrading { get; set; }

        // Fixed internal behavior; these are intentionally not exposed in the
        // Strategy Properties dialog.
        private const int OrderQuantity = 1;
        private const int EntryOffsetTicks = 1;
        private const int ProximityTicks = 5;
        private const bool ShowHUD = true;
        private const int PinBarRangeTicks = 5;
        private const int PinBarTailTicks = 4;
        private const int PinBarBodyTicks = 1;
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
        
        private int m_LastMarketPosition = 0;

        private bool m_BuyOrderActive = false;
        private bool m_SellOrderActive = false;
        private bool m_PinBarArmed = false;
        private int m_PinBarDirection = 0;
        private int m_PinProjectionBar = -1;
        private int m_PinProjectionDirection = 0;
        private bool m_PinProjectionTailReached = false;
        private bool m_PinProjectionBroken = false;
        private int m_LastPinBarEntryBar = -1;
        private bool m_FlattenRequested = false;
        // Persistent kill mode entered by Ctrl/Shift-click while armed or in a
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
        
        private ITrendLineObject m_TargetLine;
        private ITrendLineObject m_StopLine;
        private ITrendLineObject m_ProjectedEntryLine;
        private ITrendLineObject m_PinBarLowerLine;
        private ITrendLineObject m_PinBarUpperLine;
        private ITrendLineObject m_GoSignalMarker;
        private ITextObject m_HUDLabel;
        private ITextObject m_EmergencyLabel;
        // Filled-trade annotations are retained after the position closes so
        // the chart keeps a clean record of executed entries.
        private readonly List<IDrawObject> m_TradeEntryMarkers = new List<IDrawObject>();

        public RangeBarTradingV3(object ctx) : base(ctx)
        {
            RangeSizeTicks = 5;
            ProtectiveStopLossTicks = 12;
            ProfitTargetTicks = 5;
            EnablePinBarTrading = true;
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
            if (m_TargetLine != null) { m_TargetLine.Delete(); m_TargetLine = null; }
            if (m_StopLine != null) { m_StopLine.Delete(); m_StopLine = null; }
            if (m_EmergencyLabel != null) { m_EmergencyLabel.Delete(); m_EmergencyLabel = null; }
            ClearProjectedEntryLine();
            ClearPinBarProjectionLines();
            if (m_GoSignalMarker != null) { m_GoSignalMarker.Delete(); m_GoSignalMarker = null; }
        }

        protected override void CalcBar()
        {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            if (tickSize <= 0) tickSize = 0.25;

            if (Bars.Status == EBarState.Close || m_AutoRangeTicks <= 0) m_AutoRangeTicks = Math.Abs(Bars.High[0] - Bars.Low[0]) / tickSize;
            if (!Environment.IsRealTimeCalc) return;

            RefreshEmergencyCancellationStatus();
            if (m_EmergencyLabel != null && DateTime.Now >= m_EmergencyMessageExpiresAt)
                ClearEmergencyIndicator();

            int currentPosition = StrategyInfo.MarketPosition;

            if (!EnablePinBarTrading && currentPosition == 0) {
                m_PinBarArmed = false;
                m_PinBarDirection = 0;
                m_PinProjectionBar = -1;
                m_PinProjectionDirection = 0;
                m_PinProjectionTailReached = false;
                m_PinProjectionBroken = false;
                m_BuyOrderActive = m_SellOrderActive = false;
                m_StopPrice = m_LastSentPrice = 0;
                ClearProjectedEntryLine();
            }

            // Pin-bar projections are informational even while the strategy is
            // unarmed.  An open position hides them so the trade-management
            // controls remain visually distinct.
            UpdatePinBarProjection(tickSize, currentPosition);

            // Highest-priority execution path, modeled after RenkoTailTrading's
            // nuclear flatten. Do not emit entry, stop-loss, or target orders
            // while kill mode is active. Re-send only the market close until the
            // platform confirms that the position is flat.
            if (m_KillModeActive) {
                m_BuyOrderActive = m_SellOrderActive = false;
                m_StopPrice = m_LastSentPrice = 0;
                m_ProtectiveStopPrice = m_ProfitTargetPrice = 0;
                m_FlattenRequested = currentPosition != 0;

                // A prior version could leave a working native order behind
                // after a chart reload.  On first real-time calculation, ask
                // the order tracker to cancel every working order owned by
                // this signal before doing anything else.
                if (!m_StartupOrderCancellationRequested) {
                    m_StartupOrderCancellationRequested = true;
                    RequestTrackerOrderCancellations();
                }

                if (Bars.LastBarOnChart) {
                    if (currentPosition > 0) m_CloseLongNextBar.Send();
                    else if (currentPosition < 0) m_CloseShortNextBar.Send();
                }

                m_LastMarketPosition = currentPosition;
                if (ShowHUD) UpdateHUD();
                return;
            }

            if (currentPosition != 0 && m_LastMarketPosition == 0) {
                double entryPrice = StrategyInfo.AvgEntryPrice != 0 ? StrategyInfo.AvgEntryPrice : Bars.Close[0];
                double stopDist = ProtectiveStopLossTicks * tickSize;
                double targetDist = ProfitTargetTicks * tickSize;

                if (currentPosition > 0) {
                    m_ProtectiveStopPrice = ProtectiveStopLossTicks > 0 ? entryPrice - stopDist : 0;
                    m_ProfitTargetPrice = ProfitTargetTicks > 0 ? entryPrice + targetDist : 0;
                } else {
                    m_ProtectiveStopPrice = ProtectiveStopLossTicks > 0 ? entryPrice + stopDist : 0;
                    m_ProfitTargetPrice = ProfitTargetTicks > 0 ? entryPrice - targetDist : 0;
                }
                m_BuyOrderActive = m_SellOrderActive = false; m_StopPrice = m_LastSentPrice = 0;
                if (m_GoSignalMarker != null) m_GoSignalMarker.Delete();
                ClearProjectedEntryLine(); UpdateTargetLine(); UpdateStopLine();
                DrawFilledEntryMarkers(currentPosition, entryPrice, tickSize);
            }

            // Ctrl-click arms a direction, but no entry order exists until a
            // completed bar exactly matches the hard-coded 4-tick tail and
            // 1-tick body. Rejected bars are simply skipped while arming
            // persists into the next bar.
            if (currentPosition == 0 && m_LastMarketPosition == 0 &&
                EnablePinBarTrading && m_PinBarArmed &&
                !m_BuyOrderActive && !m_SellOrderActive &&
                Bars.Status == EBarState.Close &&
                Bars.CurrentBar != m_LastPinBarEntryBar &&
                IsCompletedPinBar(m_PinBarDirection, tickSize)) {
                ArmEntryFromCompletedPinBar(m_PinBarDirection, tickSize);
            }

            if (currentPosition == 0) {
                int currentQty = OrderQuantity;
                if (m_BuyOrderActive && m_StopPrice > 0) {
                    // Re-send on every IOG calculation so the native order remains
                    // active between ticks. The pin bar fixes this price at one
                    // tick above its completed high.
                    if (Bars.LastBarOnChart) { m_BuyStop.Send(m_StopPrice, currentQty); m_LastSentPrice = m_StopPrice; }
                    UpdateProjectedEntryLine();
                } else if (m_SellOrderActive && m_StopPrice > 0) {
                    // Re-send on every IOG calculation so the native order remains
                    // active between ticks. The pin bar fixes this price at one
                    // tick below its completed low.
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
                m_ProtectiveStopPrice = m_ProfitTargetPrice = 0; 
                m_BuyOrderActive = m_SellOrderActive = false; 
                m_LastSentPrice = 0; 
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
                ActivateEmergencyFlatten();
                return;
            }

            if (arg.buttons != MouseButtons.Left) return;
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale; if (tickSize <= 0) tickSize = 0.25;
            if ((arg.keys & Keys.Control) == Keys.Control) {
                int currentPosition = StrategyInfo.MarketPosition;
                if (currentPosition != 0 || m_PinBarArmed || m_BuyOrderActive || m_SellOrderActive) {
                    // If anything is working or filled, Ctrl-click is an
                    // unconditional cancel-and-flatten request.
                    ActivateKillMode(currentPosition);
                } else if (EnablePinBarTrading) {
                    // Flat and unarmed: latch the 24 EMA direction and begin
                    // waiting persistently for a completed pin bar.
                    ArmPinBarMode(tickSize);
                }
                if (ShowHUD) UpdateHUD();
            }
            else if ((arg.keys & Keys.Shift) == Keys.Shift) {
                ActivateKillMode(StrategyInfo.MarketPosition);
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

        private void ArmPinBarMode(double tickSize) {
            ClearEmergencyIndicator();
            m_KillModeActive = false;
            m_FlattenRequested = false;
            m_PinBarArmed = true;
            // Preserve the existing semantics: flat/rising 24 EMA is bullish;
            // falling 24 EMA is bearish. The direction remains latched until
            // the trader disarms with Ctrl/Shift-click.
            m_PinBarDirection = m_SlowEMA[0] >= m_SlowEMA[1] ? 1 : -1;
            m_PinProjectionBar = Bars.CurrentBar;
            m_PinProjectionDirection = m_PinBarDirection;
            m_PinProjectionTailReached = false;
            m_PinProjectionBroken = false;
            m_BuyOrderActive = m_SellOrderActive = false;
            m_StopPrice = m_LastSentPrice = 0;
            ClearProjectedEntryLine();
            UpdatePinBarProjection(tickSize, StrategyInfo.MarketPosition);
        }

        private void ActivateKillMode(int currentPosition) {
            m_KillModeActive = true;
            m_FlattenRequested = currentPosition != 0;
            m_PinBarArmed = false;
            m_PinBarDirection = 0;
            m_PinProjectionBar = -1;
            m_PinProjectionDirection = 0;
            m_PinProjectionTailReached = false;
            m_PinProjectionBroken = false;
            m_BuyOrderActive = m_SellOrderActive = false;
            m_StopPrice = m_LastSentPrice = 0;
            m_ProtectiveStopPrice = m_ProfitTargetPrice = 0;
            m_DraggingTarget = m_DraggingStop = false;
            ClearTradingDrawings();
        }

        private void ActivateEmergencyFlatten() {
            int currentPosition = StrategyInfo.MarketPosition;
            ActivateKillMode(currentPosition);
            ShowEmergencyIndicator(RequestTrackerOrderCancellations());

            // MarketNextBar executes on the next IOG tick. CalcBar keeps
            // sending it until the position is confirmed flat.
            if (currentPosition > 0) m_CloseLongNextBar.Send();
            else if (currentPosition < 0) m_CloseShortNextBar.Send();
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
            if (!EnablePinBarTrading || currentPosition != 0) {
                ClearPinBarProjectionLines();
                return;
            }

            // An armed direction is deliberately persistent. While unarmed,
            // choose one informational projection per new bar from the same
            // 24 EMA slope rule so the chart never shows four competing lines.
            if (m_PinProjectionBar != Bars.CurrentBar) {
                m_PinProjectionBar = Bars.CurrentBar;
                m_PinProjectionDirection = m_PinBarArmed
                    ? m_PinBarDirection
                    : GetSlowEmaDirection();
                m_PinProjectionTailReached = false;
                m_PinProjectionBroken = false;
            }

            int direction = m_PinBarArmed ? m_PinBarDirection : m_PinProjectionDirection;
            if (direction == 0) {
                ClearPinBarProjectionLines();
                return;
            }

            double projectedLow;
            double projectedHigh;
            GetPinBarProjectionPrices(direction, tickSize, out projectedLow, out projectedHigh);
            UpdatePinBarFormationState(direction, projectedLow, projectedHigh, tickSize);
            if (m_PinProjectionBroken || !CanStillFormPinBar(direction, tickSize)) {
                m_PinProjectionBroken = true;
                ClearPinBarProjectionLines();
                return;
            }

            UpdatePinBarProjectionLine(ref m_PinBarLowerLine, projectedLow, direction);
            UpdatePinBarProjectionLine(ref m_PinBarUpperLine, projectedHigh, direction);
        }

        private int GetSlowEmaDirection() {
            if (Bars.CurrentBar < 2) return 1;
            return m_SlowEMA[0] >= m_SlowEMA[1] ? 1 : -1;
        }

        private void GetPinBarProjectionPrices(int direction, double tickSize,
                                               out double projectedLow, out double projectedHigh) {
            double open = RoundToTick(Bars.Open[0], tickSize);
            if (direction > 0) {
                projectedLow = open - (PinBarTailTicks * tickSize);
                projectedHigh = open + (PinBarBodyTicks * tickSize);
            } else {
                projectedLow = open - (PinBarBodyTicks * tickSize);
                projectedHigh = open + (PinBarTailTicks * tickSize);
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

        private bool CanStillFormPinBar(int direction, double tickSize) {
            double projectedLow;
            double projectedHigh;
            GetPinBarProjectionPrices(direction, tickSize, out projectedLow, out projectedHigh);
            double tolerance = tickSize * 0.1;
            return Bars.Low[0] >= projectedLow - tolerance &&
                   Bars.High[0] <= projectedHigh + tolerance;
        }

        private bool IsCompletedPinBar(int direction, double tickSize) {
            if (direction == 0) return false;

            double projectedLow;
            double projectedHigh;
            GetPinBarProjectionPrices(direction, tickSize, out projectedLow, out projectedHigh);
            double tolerance = tickSize * 0.1;
            bool exactRange = Math.Abs((Bars.High[0] - Bars.Low[0]) -
                                       (PinBarRangeTicks * tickSize)) <= tolerance;
            if (!exactRange ||
                Math.Abs(Bars.Low[0] - projectedLow) > tolerance ||
                Math.Abs(Bars.High[0] - projectedHigh) > tolerance)
                return false;

            // With no wick on the body side, a bullish pin closes at its high
            // and a bearish pin closes at its low.
            double expectedClose = direction > 0 ? projectedHigh : projectedLow;
            return Math.Abs(Bars.Close[0] - expectedClose) <= tolerance;
        }

        private void ArmEntryFromCompletedPinBar(int direction, double tickSize) {
            m_LastPinBarEntryBar = Bars.CurrentBar;
            m_BuyOrderActive = direction > 0;
            m_SellOrderActive = direction < 0;
            m_StopPrice = direction > 0
                ? RoundToTick(Bars.High[0] + (EntryOffsetTicks * tickSize), tickSize)
                : RoundToTick(Bars.Low[0] - (EntryOffsetTicks * tickSize), tickSize);
            m_LastSentPrice = 0;
        }

        private double RoundToTick(double price, double tickSize) {
            return Math.Round(price / tickSize) * tickSize;
        }

        private void UpdatePinBarProjectionLine(ref ITrendLineObject line,
                                                double price, int direction) {
            ChartPoint begin = new ChartPoint(Bars.Time[0], price);
            ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), price);
            if (line == null) {
                line = DrwTrendLine.Create(begin, end);
                line.ExtRight = false;
            } else {
                line.Begin = begin;
                line.End = end;
            }
            line.Color = direction > 0 ? Color.DodgerBlue : Color.OrangeRed;
            line.Style = ETLStyle.ToolDashed;
            line.Size = 2;
        }

        private void ClearPinBarProjectionLines() {
            if (m_PinBarLowerLine != null) { m_PinBarLowerLine.Delete(); m_PinBarLowerLine = null; }
            if (m_PinBarUpperLine != null) { m_PinBarUpperLine.Delete(); m_PinBarUpperLine = null; }
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

        private void DrawFilledEntryMarkers(int currentPosition, double entryPrice, double tickSize) {
            // Direction marker: below a long entry bar and above a short entry
            // bar, so it points at the bar tail without covering the candle.
            double tailOffset = 3 * tickSize;
            double directionPrice = currentPosition > 0
                ? Bars.Low[0] - tailOffset
                : Bars.High[0] + tailOffset;
            IArrowObject directionMarker = DrwArrow.Create(
                new ChartPoint(Bars.Time[0], directionPrice), currentPosition < 0);
            directionMarker.Color = currentPosition > 0 ? Color.DodgerBlue : Color.Red;
            directionMarker.Size = 5;
            m_TradeEntryMarkers.Add(directionMarker);

            // A plain ASCII chevron is used here because it renders reliably
            // in MultiCharts chart fonts. The prior bar's time places it just
            // to the left of the fill bar at the actual average execution price.
            int barsBack = Bars.CurrentBar > 1 ? 1 : 0;
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
        }

        private void ClearProjectedEntryLine() {
            if (m_ProjectedEntryLine != null) { m_ProjectedEntryLine.Delete(); m_ProjectedEntryLine = null; }
        }

        private void UpdateHUD() {
            // Keep the established per-signal/session calculation intact, but
            // publish it so the bid and ask chart instances display one total.
            double pnl = UpdateAndGetGlobalPnL(StrategyInfo.OpenEquity);
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale; if (tickSize <= 0) tickSize = 0.25;
            string status = EnablePinBarTrading ? "PIN WATCH" : "IDLE";
            if (m_PinBarArmed)
                status = m_PinBarDirection > 0 ? "PIN ARMED BUY" : "PIN ARMED SELL";
            if (m_BuyOrderActive) status = "PIN ENTRY BUY";
            if (m_SellOrderActive) status = "PIN ENTRY SELL";
            if (StrategyInfo.MarketPosition != 0) status = "IN TRADE";
            if (m_KillModeActive) status = m_FlattenRequested ? "FLATTENING" : "UNARMED";
            string text = string.Format("{0} | Session PnL: {1:C2}", status, pnl);
            if (m_HUDLabel == null) { m_HUDLabel = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.High[0]), text); m_HUDLabel.Size = 14; }
            m_HUDLabel.Text = text; m_HUDLabel.Color = Color.Black;
            m_HUDLabel.Location = new ChartPoint(Bars.Time[0], Bars.High[0] + (12 * tickSize));
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
