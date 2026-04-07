using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace RenkoTests
{
    internal static class BarFactory
    {
        public static BarData LongHammer(int idx, double prevOpen = 100.0, double brickSize = 4.0)
        {
            BarData b = new BarData();
            b.BarIndex  = idx;
            b.Open      = prevOpen + brickSize;
            b.High      = prevOpen + brickSize;
            b.Low       = prevOpen - 1.0;          // tail pierces below prev open
            b.Close     = prevOpen + brickSize;    // closes bullish/neutral
            b.PrevOpen  = prevOpen;
            b.PrevClose = prevOpen + brickSize;
            b.Status    = EBarStatus.Close;
            return b;
        }

        public static BarData ShortStar(int idx, double prevOpen = 100.0, double brickSize = 4.0)
        {
            BarData b = new BarData();
            b.BarIndex  = idx;
            b.Open      = prevOpen - brickSize;
            b.High      = prevOpen + 1.0;          // tail pierces above prev open
            b.Low       = prevOpen - brickSize;
            b.Close     = prevOpen - brickSize;    // closes bearish/neutral
            b.PrevOpen  = prevOpen;
            b.PrevClose = prevOpen - brickSize;
            b.Status    = EBarStatus.Close;
            return b;
        }

        public static BarData BullishBar(int idx, double prevOpen = 100.0, double brickSize = 4.0)
        {
            BarData b = new BarData();
            b.BarIndex  = idx;
            b.Open      = prevOpen;
            b.High      = prevOpen + brickSize;
            b.Low       = prevOpen;
            b.Close     = prevOpen + brickSize;
            b.PrevOpen  = prevOpen - brickSize;
            b.PrevClose = prevOpen;
            b.Status    = EBarStatus.Close;
            return b;
        }

        public static BarData BearishBar(int idx, double prevOpen = 100.0, double brickSize = 4.0)
        {
            BarData b = new BarData();
            b.BarIndex  = idx;
            b.Open      = prevOpen;
            b.High      = prevOpen;
            b.Low       = prevOpen - brickSize;
            b.Close     = prevOpen - brickSize;
            b.PrevOpen  = prevOpen + brickSize;
            b.PrevClose = prevOpen;
            b.Status    = EBarStatus.Close;
            return b;
        }
    }

    [TestFixture]
    public class InactiveStateTests
    {
        private RenkoStateMachine _sm;
        [SetUp] public void Setup() { _sm = new RenkoStateMachine(); _sm.TickSize = 0.25; _sm.ProtectiveStopTicks = 2; }

        [Test] public void System_starts_Inactive()
        {
            Assert.That(_sm.State, Is.EqualTo(EStrategyState.Inactive));
        }

        [Test] public void CtrlClick_long_moves_to_ScanningLong()
        {
            _sm.OnCtrlClick(true, 10);
            Assert.That(_sm.State, Is.EqualTo(EStrategyState.ScanningLong));
        }
    }

    [TestFixture]
    public class ScanningLongTests
    {
        private RenkoStateMachine _sm;
        [SetUp] public void Setup()
        {
            _sm = new RenkoStateMachine(); _sm.TickSize = 0.25; _sm.ProtectiveStopTicks = 2;
            _sm.OnCtrlClick(true, 10);
        }

        [Test] public void Hammer_on_new_bar_arms_long()
        {
            var r = _sm.ProcessBar(BarFactory.LongHammer(11), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ArmedLong));
        }

        [Test]
        public void Prev_CounterTrend_Red_Requires_2_Bricks_Entry()
        {
            // Bar 10 was a red bar in an uptrend (counter-trend)
            BarData b10 = BarFactory.BearishBar(10, 104, 4); // PrevOpen: 108, PrevClose: 104
            _sm.OnCtrlClick(true, 10);
            
            // Bar 11 must show it follows a red bar
            BarData b11 = BarFactory.LongHammer(11);
            b11.PrevOpen = 108;
            b11.PrevClose = 104;
            b11.Low = 90.0; // Deep pierce
            
            _sm.ProcessBar(b11, false, false);
            
            // Expected Entry = PrevClose (104) + 2 bricks (8) = 112
            Assert.That(_sm.EntryStop, Is.EqualTo(112.0).Within(0.01));
        }

        [Test] public void Prev_WithTrend_Blue_Requires_1_Brick_Entry()
        {
            // Bar 10 was a blue bar (with-trend)
            BarData b10 = BarFactory.BullishBar(10, 100, 4); // PrevOpen: 96, PrevClose: 100
            _sm.OnCtrlClick(true, 10);
            
            // Bar 11 follows a blue bar
            BarData b11 = BarFactory.LongHammer(11);
            b11.PrevOpen = 96;
            b11.PrevClose = 100;
            b11.Low = 90.0; // Deep pierce

            _sm.ProcessBar(b11, false, false);
            
            // Expected Entry = PrevClose (100) + 1 brick (4) = 104
            Assert.That(_sm.EntryStop, Is.EqualTo(104.0).Within(0.01));
        }
    }

    [TestFixture]
    public class ArmedLongTests
    {
        private RenkoStateMachine _sm;
        [SetUp] public void Setup()
        {
            _sm = new RenkoStateMachine(); _sm.TickSize = 0.25; _sm.ProtectiveStopTicks = 2;
            _sm.OnCtrlClick(true, 10);
            _sm.ProcessBar(BarFactory.LongHammer(11), false, false);
        }

        [Test] public void Bearish_close_cancels_to_Inactive_ZeroPersistence()
        {
            var r = _sm.ProcessBar(BarFactory.BearishBar(12), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.Inactive));
        }

        [Test] public void Position_fill_transitions_to_LongActive()
        {
            var r = _sm.ProcessBar(BarFactory.BullishBar(12), true, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.LongActive));
        }
    }

    [TestFixture]
    public class LongActiveTests
    {
        private RenkoStateMachine _sm;
        [SetUp] public void Setup()
        {
            _sm = new RenkoStateMachine(); _sm.TickSize = 0.25; _sm.ProtectiveStopTicks = 2;
            _sm.OnCtrlClick(true, 10);
            _sm.ProcessBar(BarFactory.LongHammer(11), false, false);
            _sm.ProcessBar(BarFactory.BullishBar(12), true, false); // fill
        }

        [Test] public void Position_close_returns_to_Inactive_OneAndDone()
        {
            var r = _sm.ProcessBar(BarFactory.BullishBar(13), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.Inactive));
        }
    }
}
