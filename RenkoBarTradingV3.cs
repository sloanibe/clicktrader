using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using PowerLanguage;
using PowerLanguage.Function;

namespace PowerLanguage.Strategy
{
    // Renko counterpart to RangeBarTradingV3.  This is deliberately a new
    // signal; the older RenkoBarTrading remains available as a legacy/manual
    // strategy.
    [IOGMode(IOGMode.Enabled)]
    [MouseEvents(true)]
    [SameAsSymbol(true)]
    [RecoverDrawings(false)]
    public class RenkoBarTradingV3 : SignalObject
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        private static readonly object s_GlobalPnLLock = new object();
        private static readonly Dictionary<RenkoBarTradingV3, double> s_GlobalPnL =
            new Dictionary<RenkoBarTradingV3, double>();
        private static RenkoBarTradingV3 s_LastActiveChart;

        // A zero brick size measures the completed Renko brick currently on
        // the chart.  Supplying the size explicitly is useful for charts whose
        // current bar is still forming when the signal is armed.
        [Input] public int RenkoBrickSizeTicks { get; set; }
        [Input] public int ProtectiveStopLossTicks { get; set; }
        [Input] public int ProfitTargetTicks { get; set; }
        [Input] public bool IsAskChart { get; set; }
        [Input] public bool UseOppositeColorExitForProfits { get; set; }

        private const int OrderQuantity = 1;
        private const int ProximityTicks = 5;
        private const int HudRefreshMilliseconds = 100;
        private const int OppositeColorExitMinimumProfitTicks = 5;

        private IOrderPriced m_BuyStop;
        private IOrderPriced m_SellStop;
        private IOrderPriced m_LongExitStop;
        private IOrderPriced m_ShortExitStop;
        private IOrderPriced m_LongExitLimit;
        private IOrderPriced m_ShortExitLimit;
        private IOrderMarket m_CloseLong;
        private IOrderMarket m_CloseShort;

        private XAverage m_Ema8;
        private XAverage m_Ema24;
        private XAverage m_Ema50;

        private bool m_AutoEntryArmed = false;
        // Safety default: a loaded/recalculated signal does not submit an
        // entry until the trader deliberately Ctrl-clicks it.
        private bool m_KillModeActive = true;
        private int m_ArmedDirection = 0;
        private bool m_BuyOrderActive = false;
        private bool m_SellOrderActive = false;
        private int m_EntryOrderBar = -1;
        private double m_StopPrice = 0;
        private double m_LastSentPrice = 0;
        private double m_ProtectiveStopPrice = 0;
        private double m_ProfitTargetPrice = 0;
        private double m_OppositeColorStopPrice = 0;
        private bool m_OppositeColorExitArmed = false;
        private bool m_FlattenRequested = false;
        private bool m_DraggingTarget = false;
        private int m_LastMarketPosition = 0;
        private int m_ActiveStopLossTicks = 12;
        private bool m_StopLossInitialized = false;
        private double m_AutoBrickTicks = 0;

        private bool m_HudDisplayEnabled = true;
        private DateTime m_LastHudRefreshAt = DateTime.MinValue;
        private int m_HudAnchorBar = -1;
        private int m_HudLayoutBar = -1;
        private DateTime m_HudAnchorTime = DateTime.MinValue;
        private double m_HudAnchorHigh = 0;
        private double m_HudAnchorLow = 0;
        private string m_LastHudText = null;
        private string m_LastBrokerText = null;

        private ITrendLineObject m_ProjectedEntryLine;
        private ITextObject m_ProjectedEntryLabel;
        private ITrendLineObject m_TargetLine;
        private ITrendLineObject m_StopLine;
        private ITrendLineObject m_ColorExitLine;
        private ITextObject m_HudLabel;
        private ITextObject m_BrokerLabel;
        private ITextObject m_ControlsLabel;
        private readonly List<IDrawObject> m_TradeMarkers = new List<IDrawObject>();
        private IArrowObject m_ActiveEntryMarker;
        private double m_ClosedEquityAtEntry = 0;

        public RenkoBarTradingV3(object ctx) : base(ctx)
        {
            RenkoBrickSizeTicks = 0;
            ProtectiveStopLossTicks = 12;
            ProfitTargetTicks = 0;
            IsAskChart = true;
            UseOppositeColorExitForProfits = true;
        }

        protected override void Create()
        {
            m_Ema8 = new XAverage(this);
            m_Ema24 = new XAverage(this);
            m_Ema50 = new XAverage(this);
            m_BuyStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "RBV3Buy", EOrderAction.Buy));
            m_SellStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "RBV3Sell", EOrderAction.SellShort));
            m_LongExitStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "RBV3ProtectLong", EOrderAction.Sell));
            m_ShortExitStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "RBV3ProtectShort", EOrderAction.BuyToCover));
            m_LongExitLimit = OrderCreator.Limit(new SOrderParameters(Contracts.Default, "RBV3ProfitLong", EOrderAction.Sell));
            m_ShortExitLimit = OrderCreator.Limit(new SOrderParameters(Contracts.Default, "RBV3ProfitShort", EOrderAction.BuyToCover));
            m_CloseLong = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "RBV3FlattenLong", EOrderAction.Sell));
            m_CloseShort = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "RBV3FlattenShort", EOrderAction.BuyToCover));
        }

        protected override void StartCalc()
        {
            m_Ema8.Length = 8; m_Ema8.Price = Bars.Close;
            m_Ema24.Length = 24; m_Ema24.Price = Bars.Close;
            m_Ema50.Length = 50; m_Ema50.Price = Bars.Close;
            if (!m_StopLossInitialized) {
                m_ActiveStopLossTicks = ProtectiveStopLossTicks > 0 ? ProtectiveStopLossTicks : 12;
                m_StopLossInitialized = true;
            }
        }

        protected override void CalcBar()
        {
            double tickSize = GetTickSize();
            if (Bars.Status == EBarState.Close || m_AutoBrickTicks <= 0)
                m_AutoBrickTicks = Math.Abs(Bars.Close[0] - Bars.Open[0]) / tickSize;
            if (!Environment.IsRealTimeCalc) return;

            int currentPosition = StrategyInfo.MarketPosition;
            double brickTicks = GetBrickTicks(tickSize);

            // A projected Renko entry is valid for its forming brick only.
            // Once another brick prints, its tail/open relationship is a new
            // setup and the old native stop must not survive it.
            if (currentPosition == 0 && m_EntryOrderBar >= 0 &&
                m_EntryOrderBar != Bars.CurrentBar)
                ClearPendingEntry();

            if (m_KillModeActive) {
                ClearPendingEntry();
                m_ProtectiveStopPrice = m_ProfitTargetPrice = 0;
                m_OppositeColorStopPrice = 0;
                m_OppositeColorExitArmed = false;
                if (currentPosition != 0 && Bars.LastBarOnChart) {
                    if (currentPosition > 0) m_CloseLong.Send();
                    else m_CloseShort.Send();
                    m_FlattenRequested = true;
                } else m_FlattenRequested = false;
                m_LastMarketPosition = currentPosition;
                UpdateHUD();
                return;
            }

            if (currentPosition != 0 && m_LastMarketPosition == 0) {
                double entry = StrategyInfo.AvgEntryPrice != 0 ? StrategyInfo.AvgEntryPrice : Bars.Close[0];
                double stopDistance = m_ActiveStopLossTicks * tickSize;
                double targetDistance = ProfitTargetTicks * tickSize;
                m_ClosedEquityAtEntry = StrategyInfo.ClosedEquity;
                m_ProtectiveStopPrice = currentPosition > 0 ? entry - stopDistance : entry + stopDistance;
                m_ProfitTargetPrice = ProfitTargetTicks > 0
                    ? (currentPosition > 0 ? entry + targetDistance : entry - targetDistance) : 0;
                m_OppositeColorStopPrice = 0;
                m_OppositeColorExitArmed = false;
                m_AutoEntryArmed = false;
                m_ArmedDirection = 0;
                ClearPendingEntry();
                UpdateStopLine(); UpdateTargetLine();
                DrawEntryMarker(currentPosition, entry, tickSize);
            }

            if (currentPosition == 0 && m_AutoEntryArmed && brickTicks > 0)
                UpdateRenkoPullbackEntry(tickSize, brickTicks);

            if (currentPosition == 0) SubmitPendingEntry();
            else {
                UpdateProfitManagement(currentPosition, tickSize);
                if (Bars.LastBarOnChart) SubmitActiveExitOrders(currentPosition);
                UpdateStopLine(); UpdateTargetLine();
            }

            if (currentPosition == 0 && m_LastMarketPosition != 0) {
                FinalizeEntryMarker();
                // A completed trade always returns to the deliberate safety
                // default.  The trader must Ctrl-click to arm another setup.
                m_KillModeActive = true;
                m_AutoEntryArmed = false;
                m_ArmedDirection = 0;
                m_ProtectiveStopPrice = m_ProfitTargetPrice = 0;
                m_OppositeColorStopPrice = 0;
                m_OppositeColorExitArmed = false;
                ClearPendingEntry();
                ClearExitDrawings();
            }
            m_LastMarketPosition = currentPosition;
            UpdateHUD();
        }

        // The initial V3 setup: EMA order and all three slopes must agree;
        // then the developing counter-trend tail must reach the previous
        // completed brick's open.  The stop is placed at the price that would
        // complete a brick back in the aligned trend direction.
        private void UpdateRenkoPullbackEntry(double tickSize, double brickTicks)
        {
            int direction = GetAlignedTrendDirection();
            if (direction == 0 || direction != m_ArmedDirection || !IsEntryDirectionAllowed(direction)) {
                ClearPendingEntry();
                return;
            }
            if (Bars.CurrentBar < 2) return;

            bool tailReachedPriorOpen = direction > 0
                ? Bars.Low[0] <= Bars.Open[1]
                : Bars.High[0] >= Bars.Open[1];
            if (!tailReachedPriorOpen) {
                ClearPendingEntry();
                return;
            }

            double brickSize = brickTicks * tickSize;
            // A bullish completion is one brick above the current brick open;
            // a bearish completion is one brick below it.  This is kept
            // separate from bar colour because a forming Renko brick may not
            // yet have a stable close while IOG is evaluating the tail.
            double projectedCompletion = direction > 0
                ? RoundToTick(Bars.Open[0] + brickSize, tickSize)
                : RoundToTick(Bars.Open[0] - brickSize, tickSize);
            m_StopPrice = projectedCompletion;
            m_EntryOrderBar = Bars.CurrentBar;
            m_BuyOrderActive = direction > 0;
            m_SellOrderActive = direction < 0;
            UpdateProjectedEntryLine(direction);
        }

        private int GetAlignedTrendDirection()
        {
            if (Bars.CurrentBar < 51) return 0;
            bool bullish = m_Ema8[0] > m_Ema24[0] && m_Ema24[0] > m_Ema50[0] &&
                           m_Ema8[0] > m_Ema8[1] && m_Ema24[0] > m_Ema24[1] &&
                           m_Ema50[0] > m_Ema50[1];
            bool bearish = m_Ema8[0] < m_Ema24[0] && m_Ema24[0] < m_Ema50[0] &&
                           m_Ema8[0] < m_Ema8[1] && m_Ema24[0] < m_Ema24[1] &&
                           m_Ema50[0] < m_Ema50[1];
            return bullish ? 1 : (bearish ? -1 : 0);
        }

        private bool IsEntryDirectionAllowed(int direction)
        {
            return IsAskChart ? direction > 0 : direction < 0;
        }

        private void SubmitPendingEntry()
        {
            if (!Bars.LastBarOnChart || m_StopPrice <= 0) return;
            if (m_BuyOrderActive && IsEntryDirectionAllowed(1)) {
                m_BuyStop.Send(m_StopPrice, OrderQuantity);
                m_LastSentPrice = m_StopPrice;
            } else if (m_SellOrderActive && IsEntryDirectionAllowed(-1)) {
                m_SellStop.Send(m_StopPrice, OrderQuantity);
                m_LastSentPrice = m_StopPrice;
            }
        }

        private void UpdateProfitManagement(int position, double tickSize)
        {
            if (!UseOppositeColorExitForProfits || ProfitTargetTicks > 0) return;
            double entry = StrategyInfo.AvgEntryPrice != 0 ? StrategyInfo.AvgEntryPrice : Bars.Close[0];
            double profit = position > 0 ? Bars.High[0] - entry : entry - Bars.Low[0];
            if (!m_OppositeColorExitArmed && profit >= OppositeColorExitMinimumProfitTicks * tickSize)
                m_OppositeColorExitArmed = true;
            if (!m_OppositeColorExitArmed || Bars.Status != EBarState.Close) return;
            bool isUp = Bars.Close[0] > Bars.Open[0];
            if ((position > 0 && !isUp) || (position < 0 && isUp)) {
                m_OppositeColorStopPrice = position > 0 ? Bars.Low[0] : Bars.High[0];
                UpdateColorExitLine();
            }
        }

        private void SubmitActiveExitOrders(int position)
        {
            if (position > 0) {
                // A stop-order name represents one native order.  Send only
                // the tighter of the initial protection and the colour trail;
                // sending both would let the latter unintentionally replace a
                // safer protective price.
                double activeStop = Math.Max(m_ProtectiveStopPrice, m_OppositeColorStopPrice);
                if (activeStop > 0) m_LongExitStop.Send(activeStop);
                if (m_ProfitTargetPrice > 0) m_LongExitLimit.Send(m_ProfitTargetPrice);
            } else if (position < 0) {
                double activeStop = m_OppositeColorStopPrice > 0
                    ? Math.Min(m_ProtectiveStopPrice, m_OppositeColorStopPrice)
                    : m_ProtectiveStopPrice;
                if (activeStop > 0) m_ShortExitStop.Send(activeStop);
                if (m_ProfitTargetPrice > 0) m_ShortExitLimit.Send(m_ProfitTargetPrice);
            }
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            MarkChartActive();
            if (arg.buttons != MouseButtons.Left) return;
            double tickSize = GetTickSize();

            if (IsKeyHeld(arg.keys, Keys.Escape) || IsKeyHeld(arg.keys, Keys.F12)) {
                m_HudDisplayEnabled = true;
                ActivateEmergencyFlatten();
                UpdateHUD(true);
                return;
            }
            if (IsKeyHeld(arg.keys, Keys.F11)) { ToggleHud(); return; }
            if (IsKeyHeld(arg.keys, Keys.F4)) {
                UseOppositeColorExitForProfits = !UseOppositeColorExitForProfits;
                UpdateHUD(true); return;
            }
            if (IsKeyHeld(arg.keys, Keys.F5)) {
                m_ActiveStopLossTicks = m_ActiveStopLossTicks == 7 ? 12 : 7;
                RepriceProtectiveStop(tickSize);
                UpdateHUD(true); return;
            }
            if ((arg.keys & Keys.Control) == Keys.Control) {
                if (StrategyInfo.MarketPosition != 0 || m_BuyOrderActive || m_SellOrderActive)
                    ActivateEmergencyFlatten();
                else if (m_AutoEntryArmed)
                    ActivateKillMode();
                else
                    ArmAutomatedEntry();
                UpdateHUD(true);
                return;
            }
            if ((arg.keys & Keys.Shift) == Keys.Shift) {
                ActivateEmergencyFlatten();
                UpdateHUD(true);
                return;
            }
            if (m_DraggingTarget) {
                m_ProfitTargetPrice = RoundToTick(arg.point.Price, tickSize);
                m_DraggingTarget = false;
                UpdateTargetLine();
                SubmitActiveExitOrders(StrategyInfo.MarketPosition);
            } else if (m_ProfitTargetPrice > 0 && Math.Abs(arg.point.Price - m_ProfitTargetPrice) <= ProximityTicks * tickSize) {
                m_DraggingTarget = true;
                if (m_TargetLine != null) m_TargetLine.Color = Color.White;
            } else if (StrategyInfo.MarketPosition != 0 && m_ProtectiveStopPrice > 0 &&
                       Math.Abs(arg.point.Price - m_ProtectiveStopPrice) <= ProximityTicks * tickSize) {
                double entry = StrategyInfo.AvgEntryPrice != 0 ? StrategyInfo.AvgEntryPrice : Bars.Close[0];
                m_ProtectiveStopPrice = entry;
                UpdateStopLine(); SubmitActiveExitOrders(StrategyInfo.MarketPosition);
            }
        }

        private void ArmAutomatedEntry()
        {
            int direction = GetAlignedTrendDirection();
            m_KillModeActive = false;
            m_FlattenRequested = false;
            m_AutoEntryArmed = direction != 0 && IsEntryDirectionAllowed(direction);
            m_ArmedDirection = m_AutoEntryArmed ? direction : 0;
            ClearPendingEntry();
            if (m_AutoEntryArmed) UpdateRenkoPullbackEntry(GetTickSize(), GetBrickTicks(GetTickSize()));
        }

        private void ActivateKillMode()
        {
            m_KillModeActive = true;
            m_AutoEntryArmed = false;
            m_ArmedDirection = 0;
            ClearPendingEntry();
        }

        private void ActivateEmergencyFlatten()
        {
            ActivateKillMode();
            m_FlattenRequested = true;
            int position = StrategyInfo.MarketPosition;
            if (position > 0) m_CloseLong.Send();
            else if (position < 0) m_CloseShort.Send();
        }

        private void MarkChartActive()
        {
            RenkoBarTradingV3 prior = s_LastActiveChart;
            if (prior == this) return;
            s_LastActiveChart = this;
            if (prior != null) prior.DisarmForChartSwitch();
        }

        private void DisarmForChartSwitch()
        {
            if (StrategyInfo.MarketPosition != 0) return;
            ActivateKillMode();
            ClearExitDrawings();
        }

        private void ClearPendingEntry()
        {
            if (m_BuyOrderActive || m_SellOrderActive || m_LastSentPrice > 0)
                CancelWorkingEntryOrders();
            m_BuyOrderActive = m_SellOrderActive = false;
            m_StopPrice = m_LastSentPrice = 0;
            m_EntryOrderBar = -1;
            if (m_ProjectedEntryLine != null) { m_ProjectedEntryLine.Delete(); m_ProjectedEntryLine = null; }
            if (m_ProjectedEntryLabel != null) { m_ProjectedEntryLabel.Delete(); m_ProjectedEntryLabel = null; }
        }

        private void UpdateProjectedEntryLine(int direction)
        {
            ChartPoint begin = new ChartPoint(Bars.Time[0], m_StopPrice);
            ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), m_StopPrice);
            if (m_ProjectedEntryLine == null) m_ProjectedEntryLine = DrwTrendLine.Create(begin, end);
            if (m_ProjectedEntryLine != null) {
                m_ProjectedEntryLine.Begin = begin; m_ProjectedEntryLine.End = end;
                m_ProjectedEntryLine.ExtRight = true;
                m_ProjectedEntryLine.Color = Color.DodgerBlue;
                m_ProjectedEntryLine.Style = ETLStyle.ToolDashed;
                m_ProjectedEntryLine.Size = 2;
            }
            string text = direction > 0 ? "RENKO BUY ENTRY" : "RENKO SELL ENTRY";
            ChartPoint labelPoint = new ChartPoint(Bars.Time[0], m_StopPrice);
            if (m_ProjectedEntryLabel == null) m_ProjectedEntryLabel = DrwText.Create(labelPoint, text);
            if (m_ProjectedEntryLabel != null) {
                m_ProjectedEntryLabel.Location = labelPoint; m_ProjectedEntryLabel.Text = text;
                m_ProjectedEntryLabel.Color = Color.DodgerBlue; m_ProjectedEntryLabel.Size = 10;
                m_ProjectedEntryLabel.HStyle = ETextStyleH.Center;
                m_ProjectedEntryLabel.VStyle = direction > 0 ? ETextStyleV.Above : ETextStyleV.Below;
            }
        }

        private void UpdateStopLine() { UpdateLine(ref m_StopLine, m_ProtectiveStopPrice, Color.Red, ETLStyle.ToolDashed, 2); }
        private void UpdateTargetLine() { UpdateLine(ref m_TargetLine, m_ProfitTargetPrice, Color.Gold, ETLStyle.ToolDashed, 2); }
        private void UpdateColorExitLine() { UpdateLine(ref m_ColorExitLine, m_OppositeColorStopPrice, Color.OrangeRed, ETLStyle.ToolDashed, 2); }

        private void UpdateLine(ref ITrendLineObject line, double price, Color color, ETLStyle style, int size)
        {
            if (price <= 0) { if (line != null) { line.Delete(); line = null; } return; }
            ChartPoint begin = new ChartPoint(Bars.Time[0], price);
            ChartPoint end = new ChartPoint(Bars.Time[0].AddMinutes(5), price);
            if (line == null) line = DrwTrendLine.Create(begin, end);
            if (line == null) return;
            line.Begin = begin; line.End = end; line.ExtRight = true;
            line.Color = color; line.Style = style; line.Size = size;
        }

        private void ClearExitDrawings()
        {
            if (m_TargetLine != null) { m_TargetLine.Delete(); m_TargetLine = null; }
            if (m_StopLine != null) { m_StopLine.Delete(); m_StopLine = null; }
            if (m_ColorExitLine != null) { m_ColorExitLine.Delete(); m_ColorExitLine = null; }
        }

        private void DrawEntryMarker(int direction, double entry, double tickSize)
        {
            IArrowObject marker = DrwArrow.Create(
                new ChartPoint(Bars.Time[0], direction > 0 ? entry - 2 * tickSize : entry + 2 * tickSize),
                direction < 0);
            if (marker != null) {
                marker.Color = Color.DodgerBlue; marker.Size = 4;
                m_TradeMarkers.Add(marker); m_ActiveEntryMarker = marker;
            }
            ITextObject label = DrwText.Create(new ChartPoint(Bars.Time[0], entry), "R");
            if (label != null) { label.Color = Color.DodgerBlue; label.Size = 9; m_TradeMarkers.Add(label); }
        }

        private void FinalizeEntryMarker()
        {
            if (m_ActiveEntryMarker != null) {
                m_ActiveEntryMarker.Color = StrategyInfo.ClosedEquity > m_ClosedEquityAtEntry ? Color.Green : Color.Red;
                m_ActiveEntryMarker = null;
            }
        }

        private void RepriceProtectiveStop(double tickSize)
        {
            int position = StrategyInfo.MarketPosition;
            if (position == 0) return;
            double entry = StrategyInfo.AvgEntryPrice != 0 ? StrategyInfo.AvgEntryPrice : Bars.Close[0];
            m_ProtectiveStopPrice = position > 0 ? entry - m_ActiveStopLossTicks * tickSize : entry + m_ActiveStopLossTicks * tickSize;
            UpdateStopLine(); SubmitActiveExitOrders(position);
        }

        private void ToggleHud()
        {
            m_HudDisplayEnabled = !m_HudDisplayEnabled;
            if (!m_HudDisplayEnabled) {
                if (m_HudLabel != null) { m_HudLabel.Delete(); m_HudLabel = null; }
                if (m_BrokerLabel != null) { m_BrokerLabel.Delete(); m_BrokerLabel = null; }
                if (m_ControlsLabel != null) { m_ControlsLabel.Delete(); m_ControlsLabel = null; }
            } else UpdateHUD(true);
        }

        private void UpdateHUD(bool force = false)
        {
            if (!m_HudDisplayEnabled || !Environment.IsRealTimeCalc) return;
            DateTime now = DateTime.UtcNow;
            if (!force && (now - m_LastHudRefreshAt).TotalMilliseconds < HudRefreshMilliseconds) return;
            m_LastHudRefreshAt = now;
            double tickSize = GetTickSize();
            double pnl = UpdateAndGetGlobalPnL(StrategyInfo.OpenEquity);
            int trend = GetAlignedTrendDirection();
            string trendText = trend > 0 ? "EMA UP 8>24>50" : trend < 0 ? "EMA DOWN 8<24<50" : "EMA NOT ALIGNED";
            string status = m_KillModeActive ? (m_FlattenRequested ? "FLATTENING" : "UNARMED") :
                            m_AutoEntryArmed ? (m_ArmedDirection > 0 ? "ARMED BUY" : "ARMED SELL") : "WATCHING";
            if (m_BuyOrderActive) status = "RENKO BUY ENTRY";
            if (m_SellOrderActive) status = "RENKO SELL ENTRY";
            if (StrategyInfo.MarketPosition != 0) status = "IN TRADE";
            string text = string.Format("{0} | {1} | {2} | STOP {3}T | Session PnL: {4:C2}",
                status, IsAskChart ? "ASK / BUY ONLY" : "BID / SELL ONLY", trendText, m_ActiveStopLossTicks, pnl);
            bool layoutChanged = m_HudLayoutBar != Bars.CurrentBar;
            SetText(ref m_HudLabel, ref m_LastHudText, GetHudPoint(tickSize, 12), text, Color.Black, 11, layoutChanged);
            string broker = string.Format("SIGNAL: {0} | {1} WORKING",
                StrategyInfo.MarketPosition > 0 ? "LONG" : StrategyInfo.MarketPosition < 0 ? "SHORT" : "FLAT",
                (m_BuyOrderActive || m_SellOrderActive) ? 1 : 0);
            // Keep every control row in one drawing.  Independent text
            // drawings at the same point rasterize over each other on some
            // MultiCharts chart renderers (the garbled HUD symptom).
            SetText(ref m_BrokerLabel, ref m_LastBrokerText, GetHudPoint(tickSize, 20), broker, Color.DarkSlateGray, 10, layoutChanged);
            const string controls =
                "CONTROLS\n" +
                "L-click stop marker: Break-even\n" +
                "F4+click: Toggle profit mode\n" +
                "F5+click: Toggle 7T/12T stop\n" +
                "Shift+click: Flatten\n" +
                "Ctrl+click: Arm/Disarm\n" +
                "F11+click: Toggle HUD\n" +
                "Esc/F12+click: Emergency flatten";
            string ignored = null;
            SetText(ref m_ControlsLabel, ref ignored, GetHudPoint(tickSize, 46), controls, Color.DarkSlateGray, 8, layoutChanged);
            m_HudLayoutBar = Bars.CurrentBar;
        }

        private void SetText(ref ITextObject label, ref string lastText, ChartPoint point, string text, Color color, int size, bool layoutChanged)
        {
            if (label != null && lastText != null && lastText != text) { label.Delete(); label = null; }
            if (label == null) label = DrwText.Create(point, text);
            if (label == null) return;
            label.Text = text; label.Color = color; label.Size = size;
            label.HStyle = ETextStyleH.Right;
            label.VStyle = IsAskChart ? ETextStyleV.Above : ETextStyleV.Below;
            if (layoutChanged) label.Location = point;
            lastText = text;
        }

        private ChartPoint GetHudPoint(double tickSize, int offsetTicks)
        {
            if (m_HudAnchorBar != Bars.CurrentBar) {
                m_HudAnchorBar = Bars.CurrentBar; m_HudAnchorTime = Bars.Time[0];
                m_HudAnchorHigh = Bars.High[0]; m_HudAnchorLow = Bars.Low[0];
            }
            double price = IsAskChart ? m_HudAnchorHigh + offsetTicks * tickSize : m_HudAnchorLow - offsetTicks * tickSize;
            return new ChartPoint(m_HudAnchorTime, price);
        }

        private bool IsKeyHeld(Keys keys, Keys key)
        {
            if ((keys & Keys.KeyCode) == key) return true;
            try { return (GetAsyncKeyState((int)key) & 0x8000) != 0; }
            catch { return false; }
        }

        private double GetTickSize()
        {
            double tickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;
            return tickSize > 0 ? tickSize : 0.25;
        }

        private double GetBrickTicks(double tickSize)
        {
            double ticks = RenkoBrickSizeTicks > 0 ? RenkoBrickSizeTicks : m_AutoBrickTicks;
            return ticks > 0 ? ticks : 0;
        }

        private double RoundToTick(double price, double tickSize) { return Math.Round(price / tickSize) * tickSize; }

        private void CancelWorkingEntryOrders()
        {
            var tradeManager = TradeManager;
            if (tradeManager == null || tradeManager.TradingData == null ||
                tradeManager.TradingData.Orders == null) return;
            try {
                tradeManager.ProcessEvents();
                var orders = tradeManager.TradingData.Orders.Items;
                if (orders == null) return;
                foreach (var order in orders) {
                    bool isEntry = order.Name == "RBV3Buy" || order.Name == "RBV3Sell";
                    bool isOurs = string.Equals(order.StrategyName, GetType().Name,
                                                 StringComparison.OrdinalIgnoreCase);
                    if (!isOurs || !isEntry || !IsWorkingOrder((int)order.State)) continue;
                    foreach (var profile in tradeManager.TradingProfiles) {
                        if (!string.Equals(profile.Name, order.Profile,
                                           StringComparison.OrdinalIgnoreCase)) continue;
                        profile.CancelOrder(order.OrderID);
                        break;
                    }
                }
            } catch (Exception ex) {
                Output.WriteLine("RenkoBarTradingV3 entry cancel error: " + ex.Message);
            }
        }

        private bool IsWorkingOrder(int state)
        {
            return state == 0 || state == 1 || state == 5 || state == 7 || state == 8;
        }

        private double UpdateAndGetGlobalPnL(double localPnl)
        {
            lock (s_GlobalPnLLock) {
                s_GlobalPnL[this] = localPnl;
                double total = 0; foreach (double value in s_GlobalPnL.Values) total += value;
                return total;
            }
        }

        protected override void Destroy()
        {
            if (s_LastActiveChart == this) s_LastActiveChart = null;
            lock (s_GlobalPnLLock) s_GlobalPnL.Remove(this);
            ClearPendingEntry(); ClearExitDrawings();
            if (m_HudLabel != null) m_HudLabel.Delete();
            if (m_BrokerLabel != null) m_BrokerLabel.Delete();
            if (m_ControlsLabel != null) m_ControlsLabel.Delete();
            foreach (IDrawObject marker in m_TradeMarkers) marker.Delete();
            m_TradeMarkers.Clear();
        }
    }
}
