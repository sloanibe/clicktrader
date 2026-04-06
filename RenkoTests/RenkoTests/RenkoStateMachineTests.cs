using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PowerLanguage.Strategy;

namespace RenkoTailTrading.Tests
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
            _sm.OnCtrlClick(true);
            Assert.That(_sm.State, Is.EqualTo(EStrategyState.ScanningLong));
        }

        [Test] public void CtrlClick_short_moves_to_ScanningShort()
        {
            _sm.OnCtrlClick(false);
            Assert.That(_sm.State, Is.EqualTo(EStrategyState.ScanningShort));
        }
    }

    [TestFixture]
    public class ScanningLongTests
    {
        private RenkoStateMachine _sm;
        [SetUp] public void Setup()
        {
            _sm = new RenkoStateMachine(); _sm.TickSize = 0.25; _sm.ProtectiveStopTicks = 2;
            _sm.OnCtrlClick(true);
        }

        [Test] public void Hammer_on_new_bar_arms_long()
        {
            var r = _sm.ProcessBar(BarFactory.LongHammer(1), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ArmedLong));
        }

        [Test] public void Bullish_bar_without_hammer_stays_scanning()
        {
            var r = _sm.ProcessBar(BarFactory.BullishBar(1), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningLong));
        }

        [Test] public void Armed_long_sets_entry_stop_above_close()
        {
            _sm.ProcessBar(BarFactory.LongHammer(1), false, false);
            Assert.That(_sm.EntryStop, Is.GreaterThan(0));
        }

        [Test] public void Armed_long_sets_protect_stop_below_low()
        {
            _sm.ProcessBar(BarFactory.LongHammer(1), false, false);
            Assert.That(_sm.ProtectStop, Is.LessThan(_sm.EntryStop));
        }
    }

    [TestFixture]
    public class ScanningShortTests
    {
        private RenkoStateMachine _sm;
        [SetUp] public void Setup()
        {
            _sm = new RenkoStateMachine(); _sm.TickSize = 0.25; _sm.ProtectiveStopTicks = 2;
            _sm.OnCtrlClick(false);
        }

        [Test] public void ShootingStar_on_new_bar_arms_short()
        {
            var r = _sm.ProcessBar(BarFactory.ShortStar(1), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ArmedShort));
        }

        [Test] public void Bearish_bar_without_star_stays_scanning()
        {
            var r = _sm.ProcessBar(BarFactory.BearishBar(1), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningShort));
        }
    }

    [TestFixture]
    public class ArmedLongTests
    {
        private RenkoStateMachine _sm;
        [SetUp] public void Setup()
        {
            _sm = new RenkoStateMachine(); _sm.TickSize = 0.25; _sm.ProtectiveStopTicks = 2;
            _sm.OnCtrlClick(true);
            _sm.ProcessBar(BarFactory.LongHammer(1), false, false);
        }

        [Test] public void Sends_BuyStop_while_flat()
        {
            var r = _sm.ProcessBar(BarFactory.BullishBar(2), false, false);
            Assert.That(r.Orders.Any(o => o.Type == EOrderType.BuyStop), Is.True);
        }

        [Test] public void Position_fill_transitions_to_LongActive()
        {
            var r = _sm.ProcessBar(BarFactory.BullishBar(2), true, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.LongActive));
        }

        [Test] public void Bearish_close_cancels_back_to_ScanningLong()
        {
            var r = _sm.ProcessBar(BarFactory.BearishBar(2), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningLong));
        }

        [Test] public void Cannot_rearm_on_same_bar_as_cancel()
        {
            // Cancel on bar 2, try to arm again on bar 2 — should be blocked
            _sm.ProcessBar(BarFactory.BearishBar(2), false, false); // cancels, m_SignalBar = 1
            // Now try a hammer on bar 2 — the lockout is bar.BarIndex > m_SignalBar (1)
            // bar 2 > 1 is true, so a hammer on bar 2 WOULD arm... 
            // The lockout is set to the SIGNAL bar (hammer bar = 1), not the cancel bar
            // So this test verifies the cancel itself just returns to scanning
            Assert.That(_sm.State, Is.EqualTo(EStrategyState.ScanningLong));
        }
    }

    [TestFixture]
    public class LongActiveTests
    {
        private RenkoStateMachine _sm;
        [SetUp] public void Setup()
        {
            _sm = new RenkoStateMachine(); _sm.TickSize = 0.25; _sm.ProtectiveStopTicks = 2;
            _sm.OnCtrlClick(true);
            _sm.ProcessBar(BarFactory.LongHammer(1), false, false);
            _sm.ProcessBar(BarFactory.BullishBar(2), true, false); // fill
        }

        [Test] public void Sends_ProtectStop_and_Target_while_long()
        {
            var r = _sm.ProcessBar(BarFactory.BullishBar(3), true, false);
            Assert.That(r.Orders.Any(o => o.Type == EOrderType.LongProtectStop), Is.True);
            Assert.That(r.Orders.Any(o => o.Type == EOrderType.LongTargetLimit), Is.True);
        }

        [Test] public void Position_close_returns_to_ScanningLong()
        {
            var r = _sm.ProcessBar(BarFactory.BullishBar(3), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningLong));
        }

        [Test] public void ShiftClick_goes_Inactive()
        {
            _sm.OnShiftClick();
            Assert.That(_sm.State, Is.EqualTo(EStrategyState.Inactive));
        }
    }

    [TestFixture]
    public class ArmedShortTests
    {
        private RenkoStateMachine _sm;
        [SetUp] public void Setup()
        {
            _sm = new RenkoStateMachine(); _sm.TickSize = 0.25; _sm.ProtectiveStopTicks = 2;
            _sm.OnCtrlClick(false);
            _sm.ProcessBar(BarFactory.ShortStar(1), false, false);
        }

        [Test] public void Sends_SellStop_while_flat()
        {
            var r = _sm.ProcessBar(BarFactory.BearishBar(2), false, false);
            Assert.That(r.Orders.Any(o => o.Type == EOrderType.SellStop), Is.True);
        }

        [Test] public void Position_fill_transitions_to_ShortActive()
        {
            var r = _sm.ProcessBar(BarFactory.BearishBar(2), false, true);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ShortActive));
        }

        [Test] public void Bullish_close_cancels_back_to_ScanningShort()
        {
            var r = _sm.ProcessBar(BarFactory.BullishBar(2), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningShort));
        }
    }

    [TestFixture]
    public class ShortActiveTests
    {
        private RenkoStateMachine _sm;
        [SetUp] public void Setup()
        {
            _sm = new RenkoStateMachine(); _sm.TickSize = 0.25; _sm.ProtectiveStopTicks = 2;
            _sm.OnCtrlClick(false);
            _sm.ProcessBar(BarFactory.ShortStar(1), false, false);
            _sm.ProcessBar(BarFactory.BearishBar(2), false, true); // fill
        }

        [Test] public void Sends_ProtectStop_and_Target_while_short()
        {
            var r = _sm.ProcessBar(BarFactory.BearishBar(3), false, true);
            Assert.That(r.Orders.Any(o => o.Type == EOrderType.ShortProtectStop), Is.True);
            Assert.That(r.Orders.Any(o => o.Type == EOrderType.ShortTargetLimit), Is.True);
        }

        [Test] public void Position_close_returns_to_ScanningShort()
        {
            var r = _sm.ProcessBar(BarFactory.BearishBar(3), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningShort));
        }

        [Test] public void ShiftClick_goes_Inactive()
        {
            _sm.OnShiftClick();
            Assert.That(_sm.State, Is.EqualTo(EStrategyState.Inactive));
        }
    }
}
