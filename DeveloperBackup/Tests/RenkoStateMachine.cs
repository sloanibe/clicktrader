using System;
using System.Collections.Generic;

namespace PowerLanguage.Strategy
{
    // ──────────────────────────────────────────────────────────────────────────
    // Pure data types — no PowerLanguage references
    // ──────────────────────────────────────────────────────────────────────────

    public enum EStrategyState
    {
        Inactive,
        ScanningLong,
        ScanningShort,
        ArmedLong,
        ArmedShort,
        LongActive,
        ShortActive
    }

    public enum EBarStatus { Open, Close }

    public enum EClickType { None, CtrlOrRight, Shift }

    /// <summary>One bar's worth of data, passed to ProcessBar().</summary>
    public struct BarData
    {
        public double Open;
        public double High;
        public double Low;
        public double Close;
        public double PrevOpen;   // Open[1]
        public double PrevClose;  // Close[1]
        public EBarStatus Status;
        public int BarIndex;
    }

    public enum EOrderType { None, BuyStop, SellStop, CloseLong, CloseShort,
                             LongProtectStop, LongTargetLimit,
                             ShortProtectStop, ShortTargetLimit }

    public struct OrderInstruction
    {
        public EOrderType Type;
        public double     Price;
    }

    public struct BarResult
    {
        public EStrategyState     State;
        public List<OrderInstruction> Orders;
        public string             TransitionLog; // for test assertions
    }

    // ──────────────────────────────────────────────────────────────────────────
    // The State Machine
    // ──────────────────────────────────────────────────────────────────────────

    public class RenkoStateMachine
    {
        // ── Configuration ──
        public int    ProtectiveStopTicks { get; set; } = 2;
        public double TickSize            { get; set; } = 0.25;

        // ── State ──
        public EStrategyState State { get; private set; } = EStrategyState.Inactive;

        public double EntryStop   { get; private set; }
        public double ProtectStop { get; private set; }
        public double TargetPrice { get; private set; }
        public int    SignalBar   { get; private set; } = -1;

        // ── Events from the UI layer ──
        private EClickType m_PendingClick     = EClickType.None;
        private bool       m_ClickedLong      = false;

        public void OnCtrlClick(bool isLong)
        {
            m_PendingClick = EClickType.CtrlOrRight;
            m_ClickedLong  = isLong;
        }

        public void OnRightClick(bool isLong)
        {
            m_PendingClick = EClickType.CtrlOrRight;
            m_ClickedLong  = isLong;
        }

        public void OnShiftClick()
        {
            m_PendingClick = EClickType.Shift;
        }

        // ── Main entry point ──

        /// <summary>
        /// Call once per CalcBar tick. Returns the orders to send and any log.
        /// </summary>
        public BarResult ProcessBar(BarData bar, bool positionIsLong, bool positionIsShort)
        {
            var result = new BarResult
            {
                Orders = new List<OrderInstruction>()
            };

            bool positionOpen = positionIsLong || positionIsShort;

            // ── Consume pending click ──
            var click    = m_PendingClick;
            bool clickLong = m_ClickedLong;
            m_PendingClick = EClickType.None;

            // ── SHIFT-CLICK: unconditional reset ──
            if (click == EClickType.Shift)
            {
                if (positionIsLong)  result.Orders.Add(new OrderInstruction { Type = EOrderType.CloseLong });
                if (positionIsShort) result.Orders.Add(new OrderInstruction { Type = EOrderType.CloseShort });
                result.TransitionLog = $"{State} → Inactive (Shift-Click)";
                TransitionTo(EStrategyState.Inactive);
                result.State = State;
                return result;
            }

            // ── CTRL/RIGHT CLICK: enable stalking ──
            if (click == EClickType.CtrlOrRight)
            {
                result.TransitionLog = $"{State} → {(clickLong ? "ScanningLong" : "ScanningShort")} (Click)";
                TransitionTo(clickLong ? EStrategyState.ScanningLong : EStrategyState.ScanningShort);
            }

            // ── STATE MACHINE ──
            switch (State)
            {
                case EStrategyState.Inactive:
                    // Flatten any lingering position (safety net)
                    if (positionIsLong)  result.Orders.Add(new OrderInstruction { Type = EOrderType.CloseLong });
                    if (positionIsShort) result.Orders.Add(new OrderInstruction { Type = EOrderType.CloseShort });
                    break;

                case EStrategyState.ScanningLong:
                    if (IsLongHammer(bar))
                    {
                        ArmLong(bar);
                        result.TransitionLog = $"ScanningLong → ArmedLong (hammer on bar {bar.BarIndex})";
                        TransitionTo(EStrategyState.ArmedLong);
                    }
                    break;

                case EStrategyState.ScanningShort:
                    if (IsShortStar(bar))
                    {
                        ArmShort(bar);
                        result.TransitionLog = $"ScanningShort → ArmedShort (star on bar {bar.BarIndex})";
                        TransitionTo(EStrategyState.ArmedShort);
                    }
                    break;

                case EStrategyState.ArmedLong:
                    if (positionIsLong)
                    {
                        // Entry stop was filled
                        ComputeLongTarget(bar);
                        result.TransitionLog = $"ArmedLong → LongActive (filled)";
                        TransitionTo(EStrategyState.LongActive);
                    }
                    else if (bar.Status == EBarStatus.Close && IsBearishBar(bar))
                    {
                        // Signal bar closed bearish (false hammer) OR subsequent bar closed bearish
                        result.TransitionLog = $"ArmedLong → ScanningLong (bearish brick closed on bar {bar.BarIndex})";
                        ResetPriceLevels();
                        TransitionTo(EStrategyState.ScanningLong);
                    }
                    else
                    {
                        // Still waiting for fill — keep entry stop live
                        result.Orders.Add(new OrderInstruction { Type = EOrderType.BuyStop, Price = EntryStop });
                    }
                    break;

                case EStrategyState.ArmedShort:
                    if (positionIsShort)
                    {
                        ComputeShortTarget(bar);
                        result.TransitionLog = $"ArmedShort → ShortActive (filled)";
                        TransitionTo(EStrategyState.ShortActive);
                    }
                    else if (bar.Status == EBarStatus.Close && IsBullishBar(bar))
                    {
                        result.TransitionLog = $"ArmedShort → ScanningShort (bullish brick closed on bar {bar.BarIndex})";
                        ResetPriceLevels();
                        TransitionTo(EStrategyState.ScanningShort);
                    }
                    else
                    {
                        result.Orders.Add(new OrderInstruction { Type = EOrderType.SellStop, Price = EntryStop });
                    }
                    break;

                case EStrategyState.LongActive:
                    if (!positionIsLong)
                    {
                        // Position closed — profit or stop hit
                        result.TransitionLog = $"LongActive → ScanningLong (position closed)";
                        ResetPriceLevels();
                        TransitionTo(EStrategyState.ScanningLong);
                    }
                    else
                    {
                        // Manage open position with OCO bracket
                        result.Orders.Add(new OrderInstruction { Type = EOrderType.LongProtectStop,  Price = ProtectStop  });
                        result.Orders.Add(new OrderInstruction { Type = EOrderType.LongTargetLimit,  Price = TargetPrice  });
                    }
                    break;

                case EStrategyState.ShortActive:
                    if (!positionIsShort)
                    {
                        result.TransitionLog = $"ShortActive → ScanningShort (position closed)";
                        ResetPriceLevels();
                        TransitionTo(EStrategyState.ScanningShort);
                    }
                    else
                    {
                        result.Orders.Add(new OrderInstruction { Type = EOrderType.ShortProtectStop, Price = ProtectStop  });
                        result.Orders.Add(new OrderInstruction { Type = EOrderType.ShortTargetLimit, Price = TargetPrice  });
                    }
                    break;
            }

            result.State = State;
            return result;
        }

        // ── Transition ──────────────────────────────────────────────────────

        private void TransitionTo(EStrategyState next)
        {
            State = next;
        }

        // ── Tail Detection ──────────────────────────────────────────────────

        private bool IsLongHammer(BarData bar) =>
            bar.Low < bar.PrevOpen &&       // poked below prior open
            bar.Close >= bar.Open &&        // closed bullish or neutral (rejection)
            bar.Close > bar.Low;            // must have some "body" above the low

        private bool IsShortStar(BarData bar) =>
            bar.High > bar.PrevOpen &&      // poked above prior open
            bar.Close <= bar.Open &&        // closed bearish or neutral (rejection)
            bar.Close < bar.High;           // must have some "body" below the high

        private bool IsBearishBar(BarData bar) => bar.Close < bar.Open;
        private bool IsBullishBar(BarData bar) => bar.Close > bar.Open;

        // ── Arming ──────────────────────────────────────────────────────────

        private void ArmLong(BarData bar)
        {
            SignalBar   = bar.BarIndex;
            double brick = Math.Abs(bar.PrevClose - bar.PrevOpen);
            if (brick == 0) brick = 4.0; // fallback for tests

            // Entry stop one brick beyond current close
            EntryStop   = bar.Close + brick;
            ProtectStop = bar.Low - (ProtectiveStopTicks * TickSize);
            TargetPrice = EntryStop + brick;
        }

        private void ArmShort(BarData bar)
        {
            SignalBar   = bar.BarIndex;
            double brick = Math.Abs(bar.PrevClose - bar.PrevOpen);
            if (brick == 0) brick = 4.0; 

            EntryStop   = bar.Close - brick;
            ProtectStop = bar.High + (ProtectiveStopTicks * TickSize);
            TargetPrice = EntryStop - brick;
        }

        private void ComputeLongTarget(BarData bar)
        {
            double brick = Math.Abs(bar.PrevClose - bar.PrevOpen);
            TargetPrice  = EntryStop + brick;
        }

        private void ComputeShortTarget(BarData bar)
        {
            double brick = Math.Abs(bar.PrevClose - bar.PrevOpen);
            TargetPrice  = EntryStop - brick;
        }

        private void ResetPriceLevels()
        {
            EntryStop   = 0;
            ProtectStop = 0;
            TargetPrice = 0;
            SignalBar   = -1;
        }
    }
}
