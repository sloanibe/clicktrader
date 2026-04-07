using System;
using System.Drawing;
using PowerLanguage;
using PowerLanguage.Indicator;

namespace PowerLanguage.Indicator
{
    /// <summary>
    /// DynamicRenkoBrickSize
    ///
    /// Runs on a 1-minute MES candlestick chart. Computes the optimal Renko brick
    /// size based on the last ATR_Length minutes of True Range, then recommends
    /// which brick size to use on your Renko trading chart.
    ///
    /// Hysteresis prevents the recommendation from flip-flopping on boundary values.
    /// An alert fires only when the recommendation has genuinely shifted and held
    /// for Hysteresis_Bars consecutive bars.
    ///
    /// Plots:
    ///   Panel 1 — RecommendedBrick (integer step histogram, cyan)
    ///   Panel 2 — RawATR_Ticks     (raw ATR in ticks, gray reference line)
    /// </summary>
    [SameAsSymbol(false)]
    public class DynamicRenkoBrickSize : IndicatorObject
    {
        // ─── INPUTS ───────────────────────────────────────────────────────────────
        /// <summary>Number of 1-minute bars used for the ATR lookback (default = 10 min).</summary>
        [Input] public int    ATR_Length      { get; set; }

        /// <summary>Fraction of ATR used to derive brick size. 0.5 = half a swing.</summary>
        [Input] public double ATR_Multiplier  { get; set; }

        /// <summary>Minimum allowable recommended brick size in ticks.</summary>
        [Input] public int    Min_Brick       { get; set; }

        /// <summary>Maximum allowable recommended brick size in ticks.</summary>
        [Input] public int    Max_Brick       { get; set; }

        /// <summary>Number of consecutive bars the new value must hold before confirming a change.</summary>
        [Input] public int    Hysteresis_Bars { get; set; }

        /// <summary>Fire a chart alert when the confirmed brick size changes.</summary>
        [Input] public bool   Enable_Alert    { get; set; }

        // ─── PLOTS ────────────────────────────────────────────────────────────────
        private IPlotObject m_PlotBrick;   // confirmed brick size — step histogram
        private IPlotObject m_PlotATR;     // raw ATR in ticks — reference line

        // ─── STATE ────────────────────────────────────────────────────────────────
        private double[] m_TrBuffer;       // circular buffer storing the last ATR_Length TR values
        private int      m_BufHead;        // write position in the circular buffer
        private int      m_BufCount;       // number of valid entries currently in the buffer

        private int      m_CurrentBrick;   // confirmed recommendation shown to the trader
        private int      m_CandidateBrick; // proposed new brick size not yet confirmed
        private int      m_CandidateCount; // consecutive bars the candidate has been stable

        private double   m_TickSize;       // points per tick (0.25 for MES)

        // ─── CONSTRUCTOR ──────────────────────────────────────────────────────────
        public DynamicRenkoBrickSize(object ctx) : base(ctx)
        {
            ATR_Length      = 10;
            ATR_Multiplier  = 0.5;
            Min_Brick       = 3;
            Max_Brick       = 10;
            Hysteresis_Bars = 5;
            Enable_Alert    = true;
        }

        // ─── CREATE ───────────────────────────────────────────────────────────────
        protected override void Create()
        {
            // Brick size: thick cyan step histogram — easy to read at a glance
            m_PlotBrick = AddPlot(new PlotAttributes("BrickSize", EPlotShapes.BarHistogram, Color.Cyan,   Color.Transparent, 4, 0, true));
            // Raw ATR:   thin gray line — reference only
            m_PlotATR   = AddPlot(new PlotAttributes("ATR_Ticks", EPlotShapes.Line,         Color.DimGray, Color.Transparent, 1, 0, true));
        }

        // ─── START CALC ───────────────────────────────────────────────────────────
        protected override void StartCalc()
        {
            // Derive tick size from instrument metadata (0.25 for MES)
            m_TickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale;

            // Allocate circular buffer sized to the ATR lookback
            m_TrBuffer = new double[ATR_Length];
            m_BufHead  = 0;
            m_BufCount = 0;

            // Initialise state
            m_CurrentBrick   = Min_Brick;
            m_CandidateBrick = Min_Brick;
            m_CandidateCount = 0;
        }

        // ─── CALC BAR ─────────────────────────────────────────────────────────────
        protected override void CalcBar()
        {
            // Need previous close to compute True Range
            if (Bars.CurrentBar < 2) return;

            // ── Step 1: True Range ────────────────────────────────────────────────
            double prevClose = Bars.Close[1];
            double high      = Bars.High[0];
            double low       = Bars.Low[0];

            double tr = Math.Max(
                high - low,
                Math.Max(
                    Math.Abs(high - prevClose),
                    Math.Abs(low  - prevClose)
                )
            );

            // ── Step 2: Store in circular buffer ──────────────────────────────────
            m_TrBuffer[m_BufHead] = tr;
            m_BufHead = (m_BufHead + 1) % ATR_Length;
            if (m_BufCount < ATR_Length) m_BufCount++;

            // Wait until the buffer is full before making recommendations
            if (m_BufCount < ATR_Length) return;

            // ── Step 3: ATR = Simple Moving Average of TR (equal weight) ─────────
            double sum = 0;
            for (int i = 0; i < ATR_Length; i++) sum += m_TrBuffer[i];
            double atr = sum / ATR_Length;

            // ── Step 4: Convert ATR from points to ticks ──────────────────────────
            double atrTicks = atr / m_TickSize;

            // ── Step 5: Derive raw brick size ─────────────────────────────────────
            double raw = atrTicks * ATR_Multiplier;

            // ── Step 6: Round and clamp to allowed range ──────────────────────────
            int proposed = (int)Math.Round(raw);
            proposed = Math.Max(Min_Brick, Math.Min(Max_Brick, proposed));

            // ── Step 7: Hysteresis gate ───────────────────────────────────────────
            // Only change the confirmed recommendation when the proposed value has
            // been consistently different for Hysteresis_Bars consecutive bars.
            // This prevents rapid flip-flopping on boundary values.
            if (proposed == m_CandidateBrick)
            {
                m_CandidateCount++;
            }
            else
            {
                // New candidate — reset the countdown
                m_CandidateBrick = proposed;
                m_CandidateCount = 1;
            }

            if (m_CandidateCount >= Hysteresis_Bars && m_CandidateBrick != m_CurrentBrick)
            {
                int prev = m_CurrentBrick;
                m_CurrentBrick   = m_CandidateBrick;
                m_CandidateCount = 0;

                if (Enable_Alert && Bars.LastBarOnChart)
                {
                    Alert(string.Format(
                        "Brick size changed: {0} ticks → {1} ticks  (ATR = {2:F1} ticks)",
                        prev, m_CurrentBrick, atrTicks
                    ));
                }
            }

            // ── Step 8: Plot ──────────────────────────────────────────────────────
            // Colour-code the histogram by brick size zone for quick visual read
            Color brickColor;
            if (m_CurrentBrick <= 4)
                brickColor = Color.DeepSkyBlue;   // low volatility — small bricks
            else if (m_CurrentBrick <= 7)
                brickColor = Color.Cyan;           // normal range — ideal for strategy
            else
                brickColor = Color.Orange;         // high volatility — large bricks

            m_PlotBrick.Set(m_CurrentBrick, brickColor);
            m_PlotATR.Set(atrTicks);
        }
    }
}
