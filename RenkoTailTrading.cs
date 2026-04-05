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
    ///   1. User sees a counter-trend tail forming on a Renko bar (bar popping against the trend).
    ///   2. User Control-Clicks anywhere on the chart.
    ///   3. Signal reads 10 EMA slope at click point → determines Long or Short regime.
    ///   4. Calculates entry stop ONE BRICK beyond last closed brick's close:
    ///        SHORT: Sell Stop = Bar[1].Close - brickSize
    ///        LONG:  Buy  Stop = Bar[1].Close + brickSize
    ///   5. Calculates protective stop from the TAIL of Bar[0] (the rejection bar):
    ///        SHORT: Protective stop = Bar[0].High + 2 ticks (above tail of counter-trend bar)
    ///        LONG:  Protective stop = Bar[0].Low  - 2 ticks (below tail of counter-trend bar)
    ///   6. While flat:    re-sends entry stop every tick to keep it live.
    ///      Once in trade: switches to re-sending protective stop every tick.
    ///   7. Click again to re-evaluate and replace the order.
    /// </summary>
    [IOGMode(IOGMode.Enabled)]
    [MouseEvents(true)]
    [RecoverDrawings(true)]
    public class RenkoTailTrading : SignalObject
    {
        // ─── INPUTS ───────────────────────────────────────────────────────────────
        [Input] public int MAPeriod           { get; set; } // EMA period for regime detection
        [Input] public int ProtectiveStopTicks { get; set; } // Extra ticks beyond tail for protective stop

        // ─── PRIVATE STATE ────────────────────────────────────────────────────────
        private bool   m_SignalActive   = false;
        private bool   m_IsLong        = true;
        private double m_EntryStop     = 0;   // Price where we want to enter
        private double m_ProtectStop   = 0;   // Price where we exit if wrong
        private double m_TargetPrice   = 0;   // Price where we take profit

        // ─── ENTRY ORDERS ─────────────────────────────────────────────────────────
        private IOrderPriced m_BuyStopOrder;
        private IOrderPriced m_SellStopOrder;

        // ─── EXIT (PROTECTIVE STOP) ORDERS ────────────────────────────────────────
        private IOrderPriced m_LongProtectStop;
        private IOrderPriced m_ShortProtectStop;

        // ─── EXIT (PROFIT TARGET) ORDERS ──────────────────────────────────────────
        private IOrderPriced m_LongTargetLimit;
        private IOrderPriced m_ShortTargetLimit;

        // ─── EMERGENCY FLATTEN ORDERS ─────────────────────────────────────────────
        private IOrderMarket m_CloseLong;
        private IOrderMarket m_CloseShort;

        // ─── DRAWINGS ─────────────────────────────────────────────────────────────
        private ITrendLineObject m_EntryLine;
        private ITrendLineObject m_ProtectLine;
        private ITextObject      m_StatusLabel;

        // ─── EMA ARRAY (plain array — reliable at any depth) ──────────────────────
        private double[] m_EMAArray;
        private const int MaxBars = 100000;

        // ─── CONSTRUCTOR ──────────────────────────────────────────────────────────
        public RenkoTailTrading(object ctx) : base(ctx)
        {
            MAPeriod            = 10;
            ProtectiveStopTicks = 2; // 2 ticks beyond the tail of the rejection bar
        }

        // ─── CREATE ───────────────────────────────────────────────────────────────
        protected override void Create()
        {
            m_EMAArray = new double[MaxBars];

            // Entry orders (get us into a position)
            m_BuyStopOrder  = OrderCreator.Stop(new SOrderParameters(
                Contracts.Default, "RenkoTail_Long",  EOrderAction.Buy));
            m_SellStopOrder = OrderCreator.Stop(new SOrderParameters(
                Contracts.Default, "RenkoTail_Short", EOrderAction.SellShort));

            // Protective stop orders (get us out if wrong)
            m_LongProtectStop  = OrderCreator.Stop(new SOrderParameters(
                Contracts.Default, "RenkoTail_LongProtect",  EOrderAction.Sell));
            m_ShortProtectStop = OrderCreator.Stop(new SOrderParameters(
                Contracts.Default, "RenkoTail_ShortProtect", EOrderAction.BuyToCover));

            // Profit target orders (Limit)
            m_LongTargetLimit  = OrderCreator.Limit(new SOrderParameters(
                Contracts.Default, "RenkoTail_LongTarget",  EOrderAction.Sell));
            m_ShortTargetLimit = OrderCreator.Limit(new SOrderParameters(
                Contracts.Default, "RenkoTail_ShortTarget", EOrderAction.BuyToCover));

            // Flatten orders (Market)
            m_CloseLong  = OrderCreator.MarketNextBar(new SOrderParameters(
                Contracts.Default, "RenkoTail_FlattenLong",  EOrderAction.Sell, OrderExit.FromAll));
            m_CloseShort = OrderCreator.MarketNextBar(new SOrderParameters(
                Contracts.Default, "RenkoTail_FlattenShort", EOrderAction.BuyToCover, OrderExit.FromAll));
        }

        protected override void StartCalc() { }

        // ─── CALC BAR ─────────────────────────────────────────────────────────────
        protected override void CalcBar()
        {
            // 1. Always maintain the EMA array
            int idx = Bars.CurrentBar - 1;
            if (idx >= MaxBars) return;

            if (idx == 0)
                m_EMAArray[0] = Bars.Close[0];
            else
            {
                double alpha = 2.0 / (MAPeriod + 1.0);
                m_EMAArray[idx] = (Bars.Close[0] - m_EMAArray[idx - 1]) * alpha + m_EMAArray[idx - 1];
            }

            if (!m_SignalActive) return;

            // 2. Route to entry stop or protective stop depending on position state
            if (CurrentPosition.Side == EMarketPositionSide.Flat)
            {
                m_TargetPrice = 0; // Reset target when flat

                // Not yet in — keep the entry stop live every tick
                if (m_IsLong)
                    m_BuyStopOrder.Send(m_EntryStop);
                else
                    m_SellStopOrder.Send(m_EntryStop);
            }
            else
            {
                // In a position — Ensure target is set based on ACTUAL entry price
                if (m_TargetPrice == 0)
                {
                    double brickSize = GetBrickSize();
                    double entryPrice = StrategyInfo.AvgEntryPrice;
                    if (entryPrice == 0) entryPrice = Bars.Close[0]; // Fallback

                    if (CurrentPosition.Side == EMarketPositionSide.Long)
                        m_TargetPrice = entryPrice + brickSize;
                    else
                        m_TargetPrice = entryPrice - brickSize;
                    
                    // Update drawings to show the target
                    DrawLevels(Bars.Time[0], m_EntryStop, m_ProtectStop, CurrentPosition.Side == EMarketPositionSide.Long);
                }

                // Keep both Protective Stop and Profit Target alive every tick
                if (CurrentPosition.Side == EMarketPositionSide.Long)
                {
                    m_LongProtectStop.Send(m_ProtectStop);
                    m_LongTargetLimit.Send(m_TargetPrice);
                }
                else if (CurrentPosition.Side == EMarketPositionSide.Short)
                {
                    m_ShortProtectStop.Send(m_ProtectStop);
                    m_ShortTargetLimit.Send(m_TargetPrice);
                }
            }
        }

        protected override void Destroy()
        {
            ClearDrawings();
        }

        private void ClearDrawings()
        {
            if (m_EntryLine != null)   { m_EntryLine.Delete();   m_EntryLine = null; }
            if (m_ProtectLine != null) { m_ProtectLine.Delete(); m_ProtectLine = null; }
            if (m_TargetLine != null)  { m_TargetLine.Delete();  m_TargetLine = null; }
            if (m_StatusLabel != null) { m_StatusLabel.Delete(); m_StatusLabel = null; }
        }

        private ITrendLineObject m_TargetLine;

        // ─── MOUSE EVENT ──────────────────────────────────────────────────────────
        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            if (arg.buttons != MouseButtons.Left) return;

            bool ctrl  = (arg.keys & Keys.Control) == Keys.Control;
            bool shift = (arg.keys & Keys.Shift)   == Keys.Shift;

            // ─── HANDLE SHIFT-CLICK (FLATTEN & CANCEL) ───
            if (shift)
            {
                m_SignalActive = false;
                ClearDrawings();

                if (CurrentPosition.Side == EMarketPositionSide.Long) m_CloseLong.Send();
                else if (CurrentPosition.Side == EMarketPositionSide.Short) m_CloseShort.Send();

                Output.WriteLine("[TailTrading] SHIFT-CLICK: Flattening position and cancelling orders.");
                ExecControl.Recalculate();
                return;
            }

            if (!ctrl) return;

            double tick = (double)Bars.Info.MinMove / Bars.Info.PriceScale; // one tick value

            // 1. READ REGIME & STEEPNESS
            int clickedBar = arg.bar_number;
            if (clickedBar < 2) clickedBar = 2;
            if (clickedBar > Bars.CurrentBar) clickedBar = Bars.CurrentBar;
            int clickIdx = clickedBar - 1;
            
            // STAY IN BOUNDS
            if (clickIdx >= MaxBars) clickIdx = MaxBars - 1;
            if (clickIdx < 1) clickIdx = 1;

            double emaSlope = m_EMAArray[clickIdx] - m_EMAArray[clickIdx - 1];
            double brickSize = GetBrickSize();
            double steepThreshold = brickSize * 0.20; // 20% of a brick per bar is a solid slope
            
            bool isSteepLong  = emaSlope >  steepThreshold;
            bool isSteepShort = emaSlope < -steepThreshold;

            double currentPrice = Bars.Close[0];
            string reason = "Click Location";

            // 2. DECIDE DIRECTION (Trend Priority > Click Location)
            if (isSteepLong)
            {
                m_IsLong = true;
                reason   = "Steep Uptrend Override";
            }
            else if (isSteepShort)
            {
                m_IsLong = false;
                reason   = "Steep Downtrend Override";
            }
            else
            {
                // Flat/Slow market — let the user define the direction via click location
                if (arg.point.Price > currentPrice)
                    m_IsLong = true;
                else if (arg.point.Price < currentPrice)
                    m_IsLong = false;
                else
                    m_IsLong = (emaSlope >= 0);
            }

            Output.WriteLine("[TailTrading] Direction: {0} ({1}). Slope: {2:F4} (Threshold: {3:F4})", 
                m_IsLong ? "LONG" : "SHORT", reason, emaSlope, steepThreshold);

            // 3. BRICK SIZE & ENTRY STOP — one brick beyond last CLOSED bar's close (Bar[1])
            //    SHORT scenario: Bar[1] was the last confirmed brick. We're seeing
            //    Bar[0] pop up as a counter-trend move. Entry triggers when price
            //    breaks one brick below Bar[1].Close (next bearish brick confirms).
            if (m_IsLong)
                m_EntryStop = Bars.Close[1] + brickSize;  // Buy Stop: next bull brick confirms
            else
                m_EntryStop = Bars.Close[1] - brickSize;  // Sell Stop: next bear brick confirms

            // 4. PROTECTIVE STOP — beyond the tail of Bar[0] (the rejection/counter-trend bar)
            //    This is the bar whose tail we are fading. The stop must clear its extreme.
            //    SHORT: stop goes ABOVE Bar[0].High + N ticks (above the upward tail)
            //    LONG:  stop goes BELOW Bar[0].Low  - N ticks (below the downward tail)
            if (m_IsLong)
                m_ProtectStop = Bars.Low[0]  - (ProtectiveStopTicks * tick);
            else
                m_ProtectStop = Bars.High[0] + (ProtectiveStopTicks * tick);

            m_SignalActive = true;

            // 5. DRAW BOTH LEVELS on the chart
            DrawLevels(arg.point.Time, m_EntryStop, m_ProtectStop, m_IsLong);

            ExecControl.Recalculate();
        }

        // ─── HELPERS ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the Renko brick size by averaging the body size of the last 5 closed bricks.
        /// Falls back to Bar[1] body size, then a tick-based estimate if needed.
        /// </summary>
        private double GetBrickSize()
        {
            double sum   = 0;
            int    count = 0;
            int    lookback = Math.Min(5, Bars.CurrentBar - 1);

            for (int i = 1; i <= lookback; i++)
            {
                double bodySize = Math.Abs(Bars.Close[i] - Bars.Open[i]);
                if (bodySize > 0) { sum += bodySize; count++; }
            }

            if (count > 0) return sum / count;

            // Fallback: current bar or tick-based estimate
            double current = Math.Abs(Bars.Close[0] - Bars.Open[0]);
            if (current > 0) return current;
            return (double)Bars.Info.MinMove / Bars.Info.PriceScale * 9; // e.g., 9-tick default
        }

        /// <summary>Draws the entry stop line and protective stop line on the chart.</summary>
        private void DrawLevels(DateTime fromTime, double entryPrice, double protectPrice, bool isLong)
        {
            Color entryColor   = isLong ? Color.DodgerBlue  : Color.OrangeRed;
            Color protectColor = isLong ? Color.Salmon       : Color.Yellow;

            // Entry line
            if (m_EntryLine != null) m_EntryLine.Delete();
            m_EntryLine = DrwTrendLine.Create(
                new ChartPoint(fromTime, entryPrice),
                new ChartPoint(Bars.Time[0].AddMinutes(1), entryPrice)); // Add offset for visibility
            m_EntryLine.Color    = entryColor;
            m_EntryLine.Size     = 2;
            m_EntryLine.Style    = ETLStyle.ToolSolid;
            m_EntryLine.ExtRight = true;

            // Protective stop line
            if (m_ProtectLine != null) m_ProtectLine.Delete();
            m_ProtectLine = DrwTrendLine.Create(
                new ChartPoint(fromTime, protectPrice),
                new ChartPoint(Bars.Time[0].AddMinutes(1), protectPrice)); // Add offset for visibility
            m_ProtectLine.Color    = protectColor;
            m_ProtectLine.Size     = 1;
            m_ProtectLine.Style    = ETLStyle.ToolDashed;
            m_ProtectLine.ExtRight = true;

            // Target line (Green)
            if (m_TargetPrice != 0)
            {
                if (m_TargetLine != null) m_TargetLine.Delete();
                m_TargetLine = DrwTrendLine.Create(
                    new ChartPoint(fromTime, m_TargetPrice),
                    new ChartPoint(Bars.Time[0].AddMinutes(1), m_TargetPrice));
                m_TargetLine.Color    = Color.LimeGreen;
                m_TargetLine.Size     = 2;
                m_TargetLine.Style    = ETLStyle.ToolSolid;
                m_TargetLine.ExtRight = true;
            }

            // Status label
            if (m_StatusLabel == null)
            {
                m_StatusLabel        = DrwText.Create(new ChartPoint(Bars.Time[0], Bars.Close[0]), "");
                m_StatusLabel.Locked = true;
                m_StatusLabel.Size   = 13;
            }
            m_StatusLabel.Location = new ChartPoint(Bars.Time[0], Bars.Close[0]);
            m_StatusLabel.Color    = entryColor;
            m_StatusLabel.Text     = string.Format(
                "{0} STOP @ {1:F2}  |  Protect @ {2:F2}  |  Target @ {3:F2}  ({4})",
                isLong ? "BUY" : "SELL",
                entryPrice,
                protectPrice,
                m_TargetPrice,
                isLong ? "UPTREND" : "DOWNTREND");
        }
    }
}
