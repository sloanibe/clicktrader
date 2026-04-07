using System;
using System.Collections.Generic;

namespace RenkoTests
{
    public enum EStrategyState { Inactive, ScanningLong, ScanningShort, ArmedLong, ArmedShort, LongActive, ShortActive }
    public enum EOrderType { BuyStop, SellStop, LongProtectStop, LongTargetLimit, ShortProtectStop, ShortTargetLimit, CloseLong, CloseShort }
    public enum EBarStatus { Open, Close }

    public struct BarData
    {
        public int BarIndex;
        public double Open, High, Low, Close;
        public double PrevOpen, PrevClose;
        public EBarStatus Status;
    }

    public struct OrderInstruction { public EOrderType Type; public double Price; }

    public class BarResult
    {
        public EStrategyState State;
        public List<OrderInstruction> Orders;
        public string TransitionLog;

        public BarResult() { Orders = new List<OrderInstruction>(); }
    }

    public class RenkoStateMachine
    {
        private EStrategyState m_State;
        private double m_EntryStop;
        private double m_ProtectStop;
        private double m_TargetPrice;
        private int    m_SignalBar;
        private double m_TickSize;
        private int    m_ProtectiveStopTicks;

        public EStrategyState State { get { return m_State; } }
        public double EntryStop     { get { return m_EntryStop; } }
        public double ProtectStop   { get { return m_ProtectStop; } }
        public double TargetPrice   { get { return m_TargetPrice; } }
        public double TickSize               { get { return m_TickSize; } set { m_TickSize = value; } }
        public int    ProtectiveStopTicks    { get { return m_ProtectiveStopTicks; } set { m_ProtectiveStopTicks = value; } }

        public RenkoStateMachine()
        {
            m_State = EStrategyState.Inactive;
            m_SignalBar = -1;
            m_TickSize = 0.25;
            m_ProtectiveStopTicks = 2;
        }

        public BarResult ProcessBar(BarData bar, bool positionIsLong, bool positionIsShort)
        {
            BarResult result = new BarResult();
            result.State = m_State;

            if (m_State == EStrategyState.Inactive)
            {
                if (positionIsLong)  result.Orders.Add(CreateOrder(EOrderType.CloseLong, 0));
                if (positionIsShort) result.Orders.Add(CreateOrder(EOrderType.CloseShort, 0));
                return result;
            }

            switch (m_State)
            {
                case EStrategyState.ScanningLong:
                    if (bar.BarIndex > m_SignalBar && IsLongHammer(bar)) 
                    { 
                        Transition(EStrategyState.ArmedLong, "Hammer", result); 
                        ArmLong(bar); 
                        result.Orders.Add(CreateOrder(EOrderType.BuyStop, m_EntryStop)); // fire immediately
                    }
                    break;

                case EStrategyState.ScanningShort:
                    if (bar.BarIndex > m_SignalBar && IsShortStar(bar)) 
                    { 
                        Transition(EStrategyState.ArmedShort, "ShootingStar", result); 
                        ArmShort(bar); 
                        result.Orders.Add(CreateOrder(EOrderType.SellStop, m_EntryStop)); // fire immediately
                    }
                    break;

                case EStrategyState.ArmedLong:
                    if (positionIsLong) Transition(EStrategyState.LongActive, "Filled", result);
                    else if (bar.Status == EBarStatus.Close && IsBearishBar(bar)) { Transition(EStrategyState.Inactive, "Rejection Failed", result); ResetPrices(bar.BarIndex, true); }
                    else if (!positionIsShort) result.Orders.Add(CreateOrder(EOrderType.BuyStop, m_EntryStop));
                    break;

                case EStrategyState.ArmedShort:
                    if (positionIsShort) Transition(EStrategyState.ShortActive, "Filled", result);
                    else if (bar.Status == EBarStatus.Close && IsBullishBar(bar)) { Transition(EStrategyState.Inactive, "Rejection Failed", result); ResetPrices(bar.BarIndex, true); }
                    else if (!positionIsLong) result.Orders.Add(CreateOrder(EOrderType.SellStop, m_EntryStop));
                    break;

                case EStrategyState.LongActive:
                    if (!positionIsLong) { Transition(EStrategyState.Inactive, "Trade Concluded", result); ResetPrices(bar.BarIndex, true); }
                    else
                    {
                        result.Orders.Add(CreateOrder(EOrderType.LongProtectStop, m_ProtectStop));
                        result.Orders.Add(CreateOrder(EOrderType.LongTargetLimit, m_TargetPrice));
                    }
                    break;

                case EStrategyState.ShortActive:
                    if (!positionIsShort) { Transition(EStrategyState.Inactive, "Trade Concluded", result); ResetPrices(bar.BarIndex, true); }
                    else
                    {
                        result.Orders.Add(CreateOrder(EOrderType.ShortProtectStop, m_ProtectStop));
                        result.Orders.Add(CreateOrder(EOrderType.ShortTargetLimit, m_TargetPrice));
                    }
                    break;
            }

            result.State = m_State;
            return result;
        }

        public void OnCtrlClick(bool isLong, int currentBar) 
        { 
            m_State = isLong ? EStrategyState.ScanningLong : EStrategyState.ScanningShort;
            m_SignalBar = currentBar - 1;
        }
        public void OnRightClick(bool isLong, int currentBar) 
        { 
            m_State = isLong ? EStrategyState.ScanningLong : EStrategyState.ScanningShort;
            m_SignalBar = currentBar - 1;
        }
        public void OnShiftClick() { m_State = EStrategyState.Inactive; ResetPrices(); }

        private void Transition(EStrategyState next, string log, BarResult res) 
        { 
            res.TransitionLog = string.Format("{0} -> {1} ({2})", m_State, next, log); 
            m_State = next; 
        }

        private void ResetPrices(int currentBar, bool waitOneFullBar) 
        { 
            m_EntryStop = 0; 
            m_ProtectStop = 0; 
            m_TargetPrice = 0; 
            if (waitOneFullBar) m_SignalBar = currentBar + 1; 
        } 

        private void ResetPrices() { ResetPrices(-1, false); }

        private bool IsLongHammer(BarData b) { return b.Low < b.PrevOpen; }
        private bool IsShortStar(BarData b)  { return b.High > b.PrevOpen; }
        private bool IsBearishBar(BarData b) { return b.Close < b.Open; }
        private bool IsBullishBar(BarData b) { return b.Close > b.Open; }

        private void ArmLong(BarData b) 
        { 
            m_SignalBar = b.BarIndex; 
            double brick = Math.Abs(b.PrevClose - b.PrevOpen); 
            if (brick == 0) brick = 4.0; 
            bool prevWasCounterTrend = b.PrevClose < b.PrevOpen;
            m_EntryStop  = b.PrevClose + brick * (prevWasCounterTrend ? 2.0 : 1.0);
            m_ProtectStop = b.Low - (m_ProtectiveStopTicks * m_TickSize); 
            m_TargetPrice = m_EntryStop + brick;
        }

        private void ArmShort(BarData b) 
        { 
            m_SignalBar = b.BarIndex; 
            double brick = Math.Abs(b.PrevClose - b.PrevOpen); 
            if (brick == 0) brick = 4.0; 
            bool prevWasCounterTrend = b.PrevClose > b.PrevOpen;
            m_EntryStop  = b.PrevClose - brick * (prevWasCounterTrend ? 2.0 : 1.0);
            m_ProtectStop = b.High + (m_ProtectiveStopTicks * m_TickSize); 
            m_TargetPrice = m_EntryStop - brick;
        }

        private OrderInstruction CreateOrder(EOrderType type, double price) 
        { 
            return new OrderInstruction { Type = type, Price = price }; 
        }
    }
}
