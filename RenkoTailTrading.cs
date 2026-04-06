using System;
using System.Drawing;
using System.Windows.Forms;
using PowerLanguage;
using PowerLanguage.Function;
using PowerLanguage.Strategy;

namespace PowerLanguage.Strategy
{
    /// <summary>
    /// Renko Tail Trading Signal
    ///
    /// WORKFLOW:
    ///   1. User sees a counter-trend tail forming on a Renko bar.
    ///   2. User Control-Clicks anywhere on the chart.
    ///   3. Signal reads 10 EMA slope → determines Long or Short stalking direction.
    ///   4. Waits for price to pierce the previous bar's Open (the "proper tail").
    ///   5. On tail confirmation, arms entry stop ONE BRICK beyond last closed brick.
    ///   6. Protective stop placed beyond the tail extreme + ProtectiveStopTicks.
    ///   7. Shift-Click cancels everything and returns to STALKING OFF.
    /// </summary>
    [IOGMode(IOGMode.Enabled)]
    [MouseEvents(true)]
    [RecoverDrawings(false)]
    public class RenkoTailTrading : SignalObject
    {
        // ─── INPUTS ───────────────────────────────────────────────────────────────
        [Input] public int MAPeriod           { get; set; }
        [Input] public int ProtectiveStopTicks { get; set; }

        // ─── STATE ────────────────────────────────────────────────────────────────
        private bool   m_SignalActive  = false;
        private bool   m_IsLong       = true;
        private double m_EntryStop    = 0;
        private double m_ProtectStop  = 0;
        private double m_TargetPrice  = 0;
        private int    m_ActivationBar = -1;  // Bar when stalking was armed (Ctrl-Click)
        private int    m_SignalBar     = -1;  // Bar when tail was detected and stop was placed
        private EMarketPositionSide m_LastSide = EMarketPositionSide.Flat;

        private enum EStalkMode { None, Long, Short }
        private EStalkMode m_StalkMode = EStalkMode.None;

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
        // HUD objects — owned exclusively by RefreshHUD(), never touched elsewhere
        private ITrendLineObject m_StalkLine;
        private ITextObject      m_StatusLabel;

        // Trade level objects — owned by DrawTradeLevels / ClearTradeLevelDrawings
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

            m_BuyStopOrder  = OrderCreator.Stop(new SOrderParameters(
                Contracts.Default, "RenkoTail_Long",  EOrderAction.Buy));
            m_SellStopOrder = OrderCreator.Stop(new SOrderParameters(
                Contracts.Default, "RenkoTail_Short", EOrderAction.SellShort));

            m_LongProtectStop  = OrderCreator.Stop(new SOrderParameters(
                Contracts.Default, "RenkoTail_LongProtect",  EOrderAction.Sell));
            m_ShortProtectStop = OrderCreator.Stop(new SOrderParameters(
                Contracts.Default, "RenkoTail_ShortProtect", EOrderAction.BuyToCover));

            m_LongTargetLimit  = OrderCreator.Limit(new SOrderParameters(
                Contracts.Default, "RenkoTail_LongTarget",  EOrderAction.Sell));
            m_ShortTargetLimit = OrderCreator.Limit(new SOrderParameters(
                Contracts.Default, "RenkoTail_ShortTarget", EOrderAction.BuyToCover));

            m_CloseLong  = OrderCreator.MarketNextBar(new SOrderParameters(
                Contracts.Default, "RenkoTail_FlattenLong",  EOrderAction.Sell, OrderExit.FromAll));
            m_CloseShort = OrderCreator.MarketNextBar(new SOrderParameters(
                Contracts.Default, "RenkoTail_FlattenShort", EOrderAction.BuyToCover, OrderExit.FromAll));
        }

        protected override void StartCalc()
        {
            // Reset HUD references on every recalculation.
            // When ExecControl.Recalculate() fires, MultiCharts deletes all drawings
            // internally but our C# references still point at the dead objects.
            // Nulling here forces RefreshHUD() to cleanly recreate them.
            m_StalkLine   = null;
            m_StatusLabel = null;
        }

        // ─── CALC BAR ─────────────────────────────────────────────────────────────
        protected override void CalcBar()
        {
            int idx = Bars.CurrentBar - 1;
            if (idx >= MaxBars) return;

            // ── 0. DETECT POSITION CLOSURE ──
            if (m_LastSide != EMarketPositionSide.Flat && CurrentPosition.Side == EMarketPositionSide.Flat)
            {
                m_SignalActive  = false;
                m_ActivationBar = Bars.CurrentBar;
                m_TargetPrice   = 0;
                ClearTradeLevelDrawings();
                // StalkMode stays ON — persistent stalking auto-resumes
            }
            m_LastSide = CurrentPosition.Side;

            // ── 1. EMA ──
            if (idx == 0)
                m_EMAArray[0] = Bars.Close[0];
            else
            {
                double alpha = 2.0 / (MAPeriod + 1.0);
                m_EMAArray[idx] = (Bars.Close[0] - m_EMAArray[idx - 1]) * alpha + m_EMAArray[idx - 1];
            }

            // ── 2. TAIL DETECTION ──
            // A 'proper tail' requires TWO confirmations:
            //   1. Price PIERCES the previous bar's open (the deep poke)
            //   2. The bar CLOSES BACK INSIDE that level (rejection confirmation)
            // This prevents arming during normal trend moves where every bar
            // naturally trades through the prior open.
            if (CurrentPosition.Side == EMarketPositionSide.Flat &&
                m_StalkMode != EStalkMode.None &&
                Bars.CurrentBar >= m_ActivationBar)
            {
                double tick = (double)Bars.Info.MinMove / Bars.Info.PriceScale;

                // LONG tail: bar poked BELOW Open[1] AND closed BULLISH (rejection hammer)
                // Compare close to bar's OWN open (Close > Open[0]), not the prior bar's open.
                if (m_StalkMode == EStalkMode.Long &&
                    Bars.Low[0]   < Bars.Open[1] &&
                    Bars.Close[0] > Bars.Open[0])
                {
                    m_IsLong      = true;
                    m_EntryStop   = GetNextBrickPrice(true);
                    m_ProtectStop = Bars.Low[0] - (ProtectiveStopTicks * tick);
                    if (!m_SignalActive)
                    {
                        m_SignalBar = Bars.CurrentBar;
                        Output.WriteLine("[TailTrading] LONG hammer tail confirmed. Arming on bar {0}.", m_SignalBar);
                    }
                    m_SignalActive = true;
                    DrawTradeLevels(Bars.Time[0], m_EntryStop, m_ProtectStop, true);
                }
                // SHORT tail: bar poked ABOVE Open[1] AND closed BEARISH (rejection shooting star)
                // Compare close to bar's OWN open (Close < Open[0]), not the prior bar's open.
                else if (m_StalkMode == EStalkMode.Short &&
                         Bars.High[0]  > Bars.Open[1] &&
                         Bars.Close[0] < Bars.Open[0])
                {
                    m_IsLong      = false;
                    m_EntryStop   = GetNextBrickPrice(false);
                    m_ProtectStop = Bars.High[0] + (ProtectiveStopTicks * tick);
                    if (!m_SignalActive)
                    {
                        m_SignalBar = Bars.CurrentBar;
                        Output.WriteLine("[TailTrading] SHORT shooting star confirmed. Arming on bar {0}.", m_SignalBar);
                    }
                    m_SignalActive = true;
                    DrawTradeLevels(Bars.Time[0], m_EntryStop, m_ProtectStop, false);
                }
            }

            // ── 3. CANCEL CHECKS (while flat and armed) ──
            if (CurrentPosition.Side == EMarketPositionSide.Flat &&
                m_SignalActive &&
                Bars.Status == EBarState.Close)
            {
                // 3a. One-bar validity: cancel if the tail bar has fully closed without a fill
                if (Bars.CurrentBar > m_SignalBar)
                {
                    m_SignalActive = false;
                    ClearTradeLevelDrawings();
                    Output.WriteLine("[TailTrading] Cancelled: Tail bar closed without fill. Back to scanning.");
                }
                // 3b. Counter-trend brick safety cancel (only on same signal bar)
                else if (Bars.CurrentBar > m_ActivationBar)
                {
                    bool up   = Bars.Close[0] > Bars.Open[0];
                    bool down = Bars.Close[0] < Bars.Open[0];
                    if ((m_IsLong && down) || (!m_IsLong && up))
                    {
                        m_SignalActive = false;
                        ClearTradeLevelDrawings();
                        Output.WriteLine("[TailTrading] Cancelled: Counter-trend brick closed.");
                    }
                }
            }

            // ── 4. REFRESH HUD — only on the live (last) bar to avoid ghost objects ──
            if (Bars.LastBarOnChart)
                RefreshHUD();

            // ── 5. ORDER ROUTING ──
            if (!m_SignalActive) return;

            if (CurrentPosition.Side == EMarketPositionSide.Flat)
            {
                m_TargetPrice = 0;
                if (m_IsLong) m_BuyStopOrder.Send(m_EntryStop);
                else          m_SellStopOrder.Send(m_EntryStop);
            }
            else
            {
                if (m_TargetPrice == 0)
                {
                    double entry = StrategyInfo.AvgEntryPrice;
                    if (entry == 0) entry = Bars.Close[0];
                    double brickSize = GetBrickSize();
                    m_TargetPrice = CurrentPosition.Side == EMarketPositionSide.Long
                        ? entry + brickSize : entry - brickSize;
                    DrawTradeLevels(Bars.Time[0], m_EntryStop, m_ProtectStop,
                        CurrentPosition.Side == EMarketPositionSide.Long);
                }

                if (CurrentPosition.Side == EMarketPositionSide.Long)
                {
                    m_LongProtectStop.Send(m_ProtectStop);
                    m_LongTargetLimit.Send(m_TargetPrice);
                }
                else
                {
                    m_ShortProtectStop.Send(m_ProtectStop);
                    m_ShortTargetLimit.Send(m_TargetPrice);
                }
            }
        }

        // ─── HUD (single owner of m_StalkLine + m_StatusLabel) ───────────────────
        /// <summary>
        /// Called once per CalcBar tick. NEVER called from OnMouseEvent.
        /// Creates objects on first call; thereafter only mutates existing ones.
        /// This prevents MultiCharts thread-conflict crashes.
        /// </summary>
        private void RefreshHUD()
        {
            if (Bars.CurrentBar < 10) return;

            double   price    = Bars.Close[0];
            DateTime rightEnd = Bars.Time[0];

            // Find the left anchor: first bar with a strictly older timestamp, then +8 bars
            int lb = 1;
            while (lb < Bars.CurrentBar - 1 && Bars.Time[lb] >= rightEnd)
                lb++;
            lb = Math.Min(lb + 8, Bars.CurrentBar - 1);
            DateTime leftAnchor = Bars.Time[lb];

            // Dashed line is ALWAYS drawn regardless of state
            // (shows STALKING OFF when idle, STALKING ON when armed)
            if (m_StalkLine == null)
            {
                m_StalkLine         = DrwTrendLine.Create(new ChartPoint(leftAnchor, price), new ChartPoint(rightEnd, price));
                m_StalkLine.Color   = Color.DimGray;
                m_StalkLine.Size    = 1;
                m_StalkLine.Style   = ETLStyle.ToolDashed;
                m_StalkLine.ExtLeft = true;
            }
            else
            {
                m_StalkLine.Begin = new ChartPoint(leftAnchor, price);
                m_StalkLine.End   = new ChartPoint(rightEnd,   price);
            }

            // ── Status label (always present, only text/color changes) ──
            if (m_StatusLabel == null)
            {
                m_StatusLabel        = DrwText.Create(new ChartPoint(leftAnchor, price), "STALKING OFF");
                m_StatusLabel.HStyle = ETextStyleH.Right;
                m_StatusLabel.VStyle = ETextStyleV.Above;
                m_StatusLabel.Size   = 11;
                m_StatusLabel.Color  = Color.DimGray;
            }

            m_StatusLabel.Location = new ChartPoint(leftAnchor, price);

            if (m_SignalActive)
            {
                m_StatusLabel.Color = m_IsLong ? Color.DodgerBlue : Color.OrangeRed;
                m_StatusLabel.Text  = string.Format("STALKING ON  |  {0} @ {1:F2}  SL {2:F2}",
                    m_IsLong ? "BUY" : "SELL", m_EntryStop, m_ProtectStop);
            }
            else if (m_StalkMode != EStalkMode.None)
            {
                m_StatusLabel.Color = m_StalkMode == EStalkMode.Long ? Color.DodgerBlue : Color.OrangeRed;
                m_StatusLabel.Text  = "STALKING ON";
            }
            else
            {
                m_StatusLabel.Color = Color.DimGray;
                m_StatusLabel.Text  = "STALKING OFF";
            }
        }

        // ─── LIFECYCLE ────────────────────────────────────────────────────────────
        protected override void Destroy()
        {
            ClearTradeLevelDrawings();
            if (m_StalkLine   != null) { m_StalkLine.Delete();   m_StalkLine   = null; }
            if (m_StatusLabel != null) { m_StatusLabel.Delete(); m_StatusLabel = null; }
        }

        /// <summary>
        /// Deletes only trade-level lines (Entry, Protect, Target).
        /// Never touches HUD objects (m_StalkLine, m_StatusLabel).
        /// Safe to call from OnMouseEvent.
        /// </summary>
        private void ClearTradeLevelDrawings()
        {
            if (m_EntryLine   != null) { m_EntryLine.Delete();   m_EntryLine   = null; }
            if (m_ProtectLine != null) { m_ProtectLine.Delete(); m_ProtectLine = null; }
            if (m_TargetLine  != null) { m_TargetLine.Delete();  m_TargetLine  = null; }
        }

        // ─── MOUSE EVENT ──────────────────────────────────────────────────────────
        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            if (arg.buttons != MouseButtons.Left && arg.buttons != MouseButtons.Right) return;

            bool ctrl  = (arg.keys & Keys.Control) == Keys.Control;
            bool shift = (arg.keys & Keys.Shift)   == Keys.Shift;

            // SHIFT-CLICK: cancel everything, return to STALKING OFF
            if (shift)
            {
                m_SignalActive  = false;
                m_StalkMode     = EStalkMode.None;
                m_ActivationBar = -1;
                ClearTradeLevelDrawings();

                if (CurrentPosition.Side == EMarketPositionSide.Long)       m_CloseLong.Send();
                else if (CurrentPosition.Side == EMarketPositionSide.Short) m_CloseShort.Send();

                Output.WriteLine("[TailTrading] SHIFT-CLICK: All cancelled.");
                ExecControl.Recalculate();
                return;
            }

            // RIGHT-CLICK: arm stalking by click position
            if (arg.buttons == MouseButtons.Right)
            {
                m_StalkMode = (arg.point.Price > Bars.Close[0]) ? EStalkMode.Long : EStalkMode.Short;
                if (CurrentPosition.Side == EMarketPositionSide.Flat)
                {
                    m_SignalActive  = false;
                    m_ActivationBar = Bars.CurrentBar;
                }
                Output.WriteLine("[TailTrading] RIGHT-CLICK: Stalking {0}.", m_StalkMode);
                ExecControl.Recalculate();
                return;
            }

            // CTRL-CLICK: arm stalking using EMA slope to determine direction
            if (!ctrl) return;

            int clickedBar = Math.Max(2, Math.Min(arg.bar_number, Bars.CurrentBar));
            int clickIdx   = Math.Max(1, Math.Min(clickedBar - 1, MaxBars - 1));

            double emaSlope    = m_EMAArray[clickIdx] - m_EMAArray[clickIdx - 1];
            double brickSize   = GetBrickSize();
            double steepThresh = brickSize * 0.20;
            bool   isSteepLong  = emaSlope >  steepThresh;
            bool   isSteepShort = emaSlope < -steepThresh;

            if      (isSteepLong)  m_IsLong = true;
            else if (isSteepShort) m_IsLong = false;
            else                   m_IsLong = (arg.point.Price > Bars.Close[0]);

            m_StalkMode     = m_IsLong ? EStalkMode.Long : EStalkMode.Short;
            m_SignalActive  = false;
            m_ActivationBar = Bars.CurrentBar;

            ClearTradeLevelDrawings();
            Output.WriteLine("[TailTrading] CTRL-CLICK: Stalking {0}.", m_StalkMode);
            ExecControl.Recalculate();
        }

        // ─── HELPERS ──────────────────────────────────────────────────────────────

        private double GetNextBrickPrice(bool isLong)
        {
            double brickSize = GetBrickSize();
            bool   lastWasUp = Bars.Close[1] > Bars.Open[1];
            if (isLong)
                return lastWasUp ? (Bars.Close[1] + brickSize) : (Bars.Open[1] + brickSize);
            else
                return !lastWasUp ? (Bars.Close[1] - brickSize) : (Bars.Open[1] - brickSize);
        }

        private double GetBrickSize()
        {
            double sum = 0; int count = 0;
            int lookback = Math.Min(5, Bars.CurrentBar - 1);
            for (int i = 1; i <= lookback; i++)
            {
                double body = Math.Abs(Bars.Close[i] - Bars.Open[i]);
                if (body > 0) { sum += body; count++; }
            }
            if (count > 0) return sum / count;
            double cur = Math.Abs(Bars.Close[0] - Bars.Open[0]);
            return cur > 0 ? cur : (double)Bars.Info.MinMove / Bars.Info.PriceScale * 9;
        }

        private void DrawTradeLevels(DateTime fromTime, double entryPrice, double protectPrice, bool isLong)
        {
            Color entryColor   = isLong ? Color.DodgerBlue : Color.OrangeRed;
            Color protectColor = isLong ? Color.Salmon      : Color.Yellow;

            if (m_TargetPrice == 0)
                m_TargetPrice = isLong
                    ? (m_EntryStop + GetBrickSize())
                    : (m_EntryStop - GetBrickSize());

            DateTime rightEdge = Bars.Time[0].AddMinutes(5);

            if (m_EntryLine == null)
            {
                m_EntryLine = DrwTrendLine.Create(new ChartPoint(fromTime, entryPrice), new ChartPoint(rightEdge, entryPrice));
                m_EntryLine.Color = entryColor; m_EntryLine.Size = 2;
                m_EntryLine.Style = ETLStyle.ToolSolid; m_EntryLine.ExtRight = true;
            }
            else
            {
                m_EntryLine.Begin = new ChartPoint(fromTime, entryPrice);
                m_EntryLine.End   = new ChartPoint(rightEdge, entryPrice);
                m_EntryLine.Color = entryColor;
            }

            if (m_ProtectLine == null)
            {
                m_ProtectLine = DrwTrendLine.Create(new ChartPoint(fromTime, protectPrice), new ChartPoint(rightEdge, protectPrice));
                m_ProtectLine.Color = protectColor; m_ProtectLine.Size = 1;
                m_ProtectLine.Style = ETLStyle.ToolDashed; m_ProtectLine.ExtRight = true;
            }
            else
            {
                m_ProtectLine.Begin = new ChartPoint(fromTime, protectPrice);
                m_ProtectLine.End   = new ChartPoint(rightEdge, protectPrice);
                m_ProtectLine.Color = protectColor;
            }

            if (m_TargetPrice != 0)
            {
                if (m_TargetLine == null)
                {
                    m_TargetLine = DrwTrendLine.Create(new ChartPoint(fromTime, m_TargetPrice), new ChartPoint(rightEdge, m_TargetPrice));
                    m_TargetLine.Color = Color.LimeGreen; m_TargetLine.Size = 2;
                    m_TargetLine.Style = ETLStyle.ToolSolid; m_TargetLine.ExtRight = true;
                }
                else
                {
                    m_TargetLine.Begin = new ChartPoint(fromTime, m_TargetPrice);
                    m_TargetLine.End   = new ChartPoint(rightEdge, m_TargetPrice);
                }
            }
        }
    }
}
