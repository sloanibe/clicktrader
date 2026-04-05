using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage;
using PowerLanguage.Function;
using System.Windows.Forms;

namespace PowerLanguage.Strategy
{
    [IOGMode(IOGMode.Enabled)]
    [MouseEvents(true)]
    [SameAsSymbol(true)]
    [AllowSendOrdersAlways]
    public class RenkoBoxTrader : SignalObject
    {
        [Input] public int BarSize { get; set; } // 0 = Auto-Detect
        [Input] public int MaxPullbackBricks { get; set; }
        [Input] public int OrderQuantity { get; set; }
        
        private IOrderPriced m_BuyStop;
        private IOrderPriced m_SellStop;
        private IOrderMarket m_CloseLongNextBar;
        private IOrderMarket m_CloseShortNextBar;
        
        private double m_BrickSize = 0;
        private double m_TickSize = 0.25;
        private bool m_IsArmed = false;
        private int m_PullbackCount = 0;
        private int m_LastBarIndex = -1;
        
        private ITrendLineObject m_TrapLine;
        
        private bool m_SetupLong = false;
        private bool m_SetupShort = false;
        private bool m_FlattenRequested = false;

        public RenkoBoxTrader(object ctx) : base(ctx)
        {
            BarSize = 0; MaxPullbackBricks = 3; OrderQuantity = 1;
        }

        protected override void Create()
        {
            // Use Contracts.Default here, override quantity in .Send() later
            m_BuyStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "LongTrap", EOrderAction.Buy));
            m_SellStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "ShortTrap", EOrderAction.SellShort));
            
            m_CloseLongNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "CloseLong", EOrderAction.Sell, OrderExit.FromAll));
            m_CloseShortNextBar = OrderCreator.MarketNextBar(new SOrderParameters(Contracts.Default, "CloseShort", EOrderAction.BuyToCover, OrderExit.FromAll));
        }

        protected override void StartCalc()
        {
            m_TickSize = (double)Bars.Info.MinMove / Bars.Info.PriceScale; if (m_TickSize <= 0) m_TickSize = 0.25;
            m_IsArmed = false;
            m_PullbackCount = 0;
            m_SetupLong = m_SetupShort = false;
        }

        protected override void CalcBar()
        {
            if (Bars.CurrentBar <= 10) return;
            int currentPosition = StrategyInfo.MarketPosition;
            bool isNewBar = (Bars.CurrentBar != m_LastBarIndex);
            m_LastBarIndex = Bars.CurrentBar;
            
            bool isBlue = Bars.Close[0] > Bars.Open[0];
            bool isRed = Bars.Close[0] < Bars.Open[0];

            // AUTO-BRICK DETECTION
            if (BarSize == 0) m_BrickSize = Math.Abs(Bars.High[0] - Bars.Low[0]);
            else m_BrickSize = BarSize * m_TickSize;

            UpdateVisuals(currentPosition, isRed, isBlue);

            // 1. MONITORING PULLBACK FOR ARMED TRAP (NOISE-FILTERED DETECTION)
            if (m_IsArmed && currentPosition == 0) {
                // DETECT START OF PULLBACK (Wait for the first counter-trend bar to CLOSE)
                if (!m_SetupShort && !m_SetupLong) {
                    if (isNewBar) {
                        if (Bars.Close[1] < Bars.Open[1] && Bars.Close[0] > Bars.Open[0]) { // CONFIRMED BLUE AFTER RED
                            m_SetupShort = true; m_SetupLong = false; m_PullbackCount = 1;
                        } else if (Bars.Close[1] > Bars.Open[1] && Bars.Close[0] < Bars.Open[0]) { // CONFIRMED RED AFTER BLUE
                            m_SetupLong = true; m_SetupShort = false; m_PullbackCount = 1;
                        }
                    }
                } else if (isNewBar) {
                    // CONTINUE TRAILING ON SUBSEQUENT COMPLETED BRICKS
                    if (m_SetupShort && isBlue) m_PullbackCount++;
                    else if (m_SetupLong && isRed) m_PullbackCount++;
                }

                // 2. CANCELLATION CHECK (Safety Kill)
                if (m_PullbackCount > MaxPullbackBricks) {
                    m_SetupShort = false; m_SetupLong = false; m_IsArmed = false; m_PullbackCount = 0;
                }
            }

            // 2. EXECUTION: SENDING AND TRAILING THE STOP ORDERS
            if (m_IsArmed && currentPosition == 0) {
                if (m_SetupShort && isBlue) {
                    m_SellStop.Send(Bars.Open[0], OrderQuantity);
                } else if (m_SetupLong && isRed) {
                    m_BuyStop.Send(Bars.Open[0], OrderQuantity);
                }
            }
            
            // 3. FLATTEN EXECUTION
            if (m_FlattenRequested) {
                if (currentPosition > 0) m_CloseLongNextBar.Send();
                else if (currentPosition < 0) m_CloseShortNextBar.Send();
                m_FlattenRequested = false;
                m_IsArmed = false; m_SetupShort = m_SetupLong = false;
            }

            // RESET ON POSITION ENTRY
            if (currentPosition != 0) {
                m_IsArmed = false; m_SetupShort = false; m_SetupLong = false; m_PullbackCount = 0;
                if (m_TrapLine != null) m_TrapLine.Delete();
            }
        }

        protected override void OnMouseEvent(MouseClickArgs arg)
        {
            if (arg.buttons != MouseButtons.Left) return;
            bool ctrl = (arg.keys & Keys.Control) == Keys.Control;
            bool shift = (arg.keys & Keys.Shift) == Keys.Shift;

            if (shift) {
                if (StrategyInfo.MarketPosition != 0) m_FlattenRequested = true;
                m_IsArmed = false; 
                m_SetupShort = m_SetupLong = false; 
                m_PullbackCount = 0;
                if (m_TrapLine != null) { m_TrapLine.Delete(); m_TrapLine = null; }
            } else if (ctrl) {
                m_IsArmed = !m_IsArmed;
                UpdateVisuals(StrategyInfo.MarketPosition, Bars.Close[0] < Bars.Open[0], Bars.Close[0] > Bars.Open[0]);
            }
        }
        
        private void UpdateVisuals(int currentPosition, bool isRed, bool isBlue)
        {
            if (m_IsArmed && currentPosition == 0) {
               double linePrice = 0;
               Color lineColor = Color.Gold; // BETTER CONTRAST THAN YELLOW
               if (m_SetupShort) lineColor = Color.Red;
               else if (m_SetupLong) lineColor = Color.DodgerBlue;
               
               if (m_SetupShort || m_SetupLong) linePrice = Bars.Open[0]; // The Hinge
               else linePrice = isRed ? Bars.Low[0] : Bars.High[0]; // The Leading Edge
               
               // FORCE REDRAW ON EACH TICK FOR MAXIMUM VISIBILITY IN SLOW MARKETS
               if (m_TrapLine != null) m_TrapLine.Delete();
               
               // START LINE 10 MINS BACK SO IT OVERLAPS EVERYTHING CLEARLY
               m_TrapLine = DrwTrendLine.Create(new ChartPoint(Bars.Time[0].AddMinutes(-10), linePrice), new ChartPoint(Bars.Time[0].AddMinutes(5), linePrice));
               m_TrapLine.Color = lineColor; m_TrapLine.Size = 2; m_TrapLine.ExtRight = true;
            } else if (m_TrapLine != null) {
               m_TrapLine.Delete(); m_TrapLine = null;
            }
        }
    }
}
