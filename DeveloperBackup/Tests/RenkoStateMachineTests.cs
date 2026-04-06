using System;
using System.Linq;
using NUnit.Framework;
using RenkoTailTrading.Core;

namespace RenkoTailTrading.Tests
{
    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    internal static class BarFactory
    {
        /// <summary>Bullish Renko hammer: Low pierces prevOpen, closes bullish.</summary>
        public static BarData LongHammer(int idx, double prevOpen = 100.0, double brickSize = 4.0) => new BarData
        {
            BarIndex = idx,
            Open     = prevOpen + brickSize,          // e.g. 104 — a new bullish brick opened
            High     = prevOpen + brickSize,
            Low      = prevOpen - 1.0,                // pierces the prior open
            Close    = prevOpen + brickSize,           // closes bullish (full brick)
            PrevOpen  = prevOpen,
            PrevClose = prevOpen + brickSize,
            Status   = EBarStatus.Close
        };

        /// <summary>Bearish Renko bar — no tail.</summary>
        public static BarData BearishBar(int idx, double prevOpen = 100.0, double brickSize = 4.0) => new BarData
        {
            BarIndex  = idx,
            Open      = prevOpen,
            High      = prevOpen,
            Low       = prevOpen - brickSize,
            Close     = prevOpen - brickSize,          // closes bearish
            PrevOpen  = prevOpen + brickSize,
            PrevClose = prevOpen,
            Status    = EBarStatus.Close
        };

        /// <summary>Bullish Renko bar — no tail.</summary>
        public static BarData BullishBar(int idx, double prevOpen = 100.0, double brickSize = 4.0) => new BarData
        {
            BarIndex  = idx,
            Open      = prevOpen,
            High      = prevOpen + brickSize,
            Low       = prevOpen,
            Close     = prevOpen + brickSize,
            PrevOpen  = prevOpen - brickSize,
            PrevClose = prevOpen,
            Status    = EBarStatus.Close
        };

        /// <summary>Short shooting star: High pierces prevOpen, closes bearish.</summary>
        public static BarData ShortStar(int idx, double prevOpen = 100.0, double brickSize = 4.0) => new BarData
        {
            BarIndex  = idx,
            Open      = prevOpen - brickSize,
            High      = prevOpen + 1.0,               // pierces prior open
            Low       = prevOpen - brickSize,
            Close     = prevOpen - brickSize,          // closes bearish
            PrevOpen  = prevOpen,
            PrevClose = prevOpen - brickSize,
            Status    = EBarStatus.Close
        };

        /// <summary>A bar that looks like a hammer intra-tick but closes bearish (false hammer).</summary>
        public static BarData FalseHammer(int idx, double prevOpen = 100.0, double brickSize = 4.0) => new BarData
        {
            BarIndex  = idx,
            Open      = prevOpen + brickSize,
            High      = prevOpen + brickSize,
            Low       = prevOpen - 1.0,               // did pierce prior open
            Close     = prevOpen - brickSize,          // but closed BEARISH — false alarm
            PrevOpen  = prevOpen,
            PrevClose = prevOpen + brickSize,
            Status    = EBarStatus.Close
        };

        /// <summary>Neutral/doji bar — no clear direction.</summary>
        public static BarData NeutralBar(int idx, double prevOpen = 100.0) => new BarData
        {
            BarIndex  = idx,
            Open      = prevOpen,
            High      = prevOpen + 0.5,
            Low       = prevOpen - 0.5,
            Close     = prevOpen,                     // closes at open = doji
            PrevOpen  = prevOpen,
            PrevClose = prevOpen,
            Status    = EBarStatus.Close
        };
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SUITE 1 — INACTIVE state
    // ──────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class InactiveStateTests
    {
        private RenkoStateMachine _sm;

        [SetUp] public void Setup() => _sm = new RenkoStateMachine { TickSize = 0.25, ProtectiveStopTicks = 2 };

        [Test]
        public void System_starts_in_Inactive()
        {
            Assert.That(_sm.State, Is.EqualTo(EStrategyState.Inactive));
        }

        [Test]
        public void Inactive_CtrlClick_long_transitions_to_ScanningLong()
        {
            _sm.OnCtrlClick(isLong: true);
            var r = _sm.ProcessBar(BarFactory.BullishBar(1), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningLong));
        }

        [Test]
        public void Inactive_CtrlClick_short_transitions_to_ScanningShort()
        {
            _sm.OnCtrlClick(isLong: false);
            var r = _sm.ProcessBar(BarFactory.BearishBar(1), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningShort));
        }

        [Test]
        public void Inactive_stays_Inactive_without_a_click()
        {
            var r = _sm.ProcessBar(BarFactory.LongHammer(1), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.Inactive));
        }

        [Test]
        public void Inactive_with_open_long_position_sends_CloseLong()
        {
            // Safety net: if somehow a position is open while inactive, system flattens it
            var r = _sm.ProcessBar(BarFactory.BullishBar(1), positionIsLong: true, positionIsShort: false);
            Assert.That(r.Orders.Any(o => o.Type == EOrderType.CloseLong), Is.True);
        }

        [Test]
        public void Inactive_with_open_short_position_sends_CloseShort()
        {
            var r = _sm.ProcessBar(BarFactory.BearishBar(1), positionIsLong: false, positionIsShort: true);
            Assert.That(r.Orders.Any(o => o.Type == EOrderType.CloseShort), Is.True);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SUITE 2 — SCANNING_LONG state
    // ──────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class ScanningLongStateTests
    {
        private RenkoStateMachine _sm;

        [SetUp]
        public void Setup()
        {
            _sm = new RenkoStateMachine { TickSize = 0.25, ProtectiveStopTicks = 2 };
            _sm.OnCtrlClick(isLong: true);
            _sm.ProcessBar(BarFactory.BullishBar(1), false, false); // enter ScanningLong
        }

        [Test]
        public void ScanningLong_hammer_tail_transitions_to_ArmedLong()
        {
            var r = _sm.ProcessBar(BarFactory.LongHammer(2), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ArmedLong));
        }

        [Test]
        public void ScanningLong_hammer_sets_EntryStop_above_last_brick()
        {
            _sm.ProcessBar(BarFactory.LongHammer(2), false, false);
            Assert.That(_sm.EntryStop, Is.GreaterThan(0));
        }

        [Test]
        public void ScanningLong_hammer_sets_ProtectStop_below_tail_low()
        {
            var hammer = BarFactory.LongHammer(2, prevOpen: 100.0);
            _sm.ProcessBar(hammer, false, false);
            Assert.That(_sm.ProtectStop, Is.LessThan(hammer.Low));
        }

        [Test]
        public void ScanningLong_bearish_bar_stays_in_ScanningLong()
        {
            var r = _sm.ProcessBar(BarFactory.BearishBar(2), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningLong));
        }

        [Test]
        public void ScanningLong_bullish_bar_without_tail_stays_in_ScanningLong()
        {
            // A normal bullish bar that doesn't pierce prevOpen is NOT a hammer
            var normalBar = BarFactory.BullishBar(2);
            var r = _sm.ProcessBar(normalBar, false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningLong));
        }

        [Test]
        public void ScanningLong_ShiftClick_transitions_to_Inactive()
        {
            _sm.OnShiftClick();
            var r = _sm.ProcessBar(BarFactory.BullishBar(2), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.Inactive));
        }

        [Test]
        public void ScanningLong_multiple_bars_with_no_tail_stays_scanning()
        {
            for (int i = 2; i <= 10; i++)
                _sm.ProcessBar(BarFactory.BullishBar(i), false, false);
            Assert.That(_sm.State, Is.EqualTo(EStrategyState.ScanningLong));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SUITE 3 — ARMED_LONG state
    // ──────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class ArmedLongStateTests
    {
        private RenkoStateMachine _sm;

        [SetUp]
        public void Setup()
        {
            _sm = new RenkoStateMachine { TickSize = 0.25, ProtectiveStopTicks = 2 };
            // Enter ScanningLong
            _sm.OnCtrlClick(isLong: true);
            _sm.ProcessBar(BarFactory.BullishBar(1), false, false);
            // Confirm hammer → ArmedLong
            _sm.ProcessBar(BarFactory.LongHammer(2), false, false);
            Assert.That(_sm.State, Is.EqualTo(EStrategyState.ArmedLong));
        }

        [Test]
        public void ArmedLong_sends_BuyStop_every_tick_while_flat()
        {
            var r = _sm.ProcessBar(BarFactory.BullishBar(3), false, false);
            Assert.That(r.Orders.Any(o => o.Type == EOrderType.BuyStop), Is.True);
        }

        [Test]
        public void ArmedLong_BuyStop_price_equals_computed_EntryStop()
        {
            double expectedEntry = _sm.EntryStop;
            var r = _sm.ProcessBar(BarFactory.BullishBar(3), false, false);
            var order = r.Orders.First(o => o.Type == EOrderType.BuyStop);
            Assert.That(order.Price, Is.EqualTo(expectedEntry));
        }

        [Test]
        public void ArmedLong_position_opens_transitions_to_LongActive()
        {
            // Simulate entry stop fill: next tick we are LONG
            var r = _sm.ProcessBar(BarFactory.BullishBar(3), positionIsLong: true, positionIsShort: false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.LongActive));
        }

        [Test]
        public void ArmedLong_subsequent_bearish_bar_cancels_to_ScanningLong()
        {
            // Bar closes bearish after the signal bar — market moved wrong way
            var r = _sm.ProcessBar(BarFactory.BearishBar(3), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningLong));
        }

        [Test]
        public void ArmedLong_subsequent_bullish_bar_stays_ArmedLong()
        {
            // Same-direction bar while waiting — keep the buy stop alive
            var r = _sm.ProcessBar(BarFactory.BullishBar(3), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ArmedLong));
        }

        [Test]
        public void ArmedLong_neutral_bar_stays_ArmedLong()
        {
            var r = _sm.ProcessBar(BarFactory.NeutralBar(3), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ArmedLong));
        }

        [Test]
        public void ArmedLong_multiple_bullish_bars_keep_signal_alive()
        {
            // Three bullish bars pass — entry stop was never hit, but we're still waiting
            for (int i = 3; i <= 5; i++)
                _sm.ProcessBar(BarFactory.BullishBar(i), false, false);
            Assert.That(_sm.State, Is.EqualTo(EStrategyState.ArmedLong));
        }

        [Test]
        public void ArmedLong_false_hammer_same_bar_cancels_to_ScanningLong()
        {
            // The signal bar itself ultimately closes bearish (intra-bar IOG detected a temporary hammer)
            // We need to re-arm first with an IOG-style tick, then close the bar bearish
            var freshSM = new RenkoStateMachine { TickSize = 0.25, ProtectiveStopTicks = 2 };
            freshSM.OnCtrlClick(isLong: true);
            freshSM.ProcessBar(BarFactory.BullishBar(1), false, false);

            // IOG tick mid-bar — looks like a hammer (Close > Open temporarily)
            var intraTick = BarFactory.LongHammer(2);
            intraTick.Status = EBarStatus.Open;          // bar still open
            freshSM.ProcessBar(intraTick, false, false); // gets armed

            // Same bar now closes — but bearish (false hammer confirmed)
            var r = freshSM.ProcessBar(BarFactory.FalseHammer(2), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningLong));
        }

        [Test]
        public void ArmedLong_cancel_resets_all_price_levels()
        {
            _sm.ProcessBar(BarFactory.BearishBar(3), false, false); // cancel
            Assert.That(_sm.EntryStop,   Is.EqualTo(0));
            Assert.That(_sm.ProtectStop, Is.EqualTo(0));
            Assert.That(_sm.TargetPrice, Is.EqualTo(0));
            Assert.That(_sm.SignalBar,   Is.EqualTo(-1));
        }

        [Test]
        public void ArmedLong_after_cancel_new_hammer_re_arms()
        {
            _sm.ProcessBar(BarFactory.BearishBar(3), false, false); // cancel → ScanningLong
            var r = _sm.ProcessBar(BarFactory.LongHammer(4), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ArmedLong));
        }

        [Test]
        public void ArmedLong_ShiftClick_transitions_to_Inactive()
        {
            _sm.OnShiftClick();
            var r = _sm.ProcessBar(BarFactory.BullishBar(3), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.Inactive));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SUITE 4 — LONG_ACTIVE state
    // ──────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class LongActiveStateTests
    {
        private RenkoStateMachine _sm;

        [SetUp]
        public void Setup()
        {
            _sm = new RenkoStateMachine { TickSize = 0.25, ProtectiveStopTicks = 2 };
            _sm.OnCtrlClick(isLong: true);
            _sm.ProcessBar(BarFactory.BullishBar(1), false, false);
            _sm.ProcessBar(BarFactory.LongHammer(2), false, false);               // → ArmedLong
            _sm.ProcessBar(BarFactory.BullishBar(3), positionIsLong: true, positionIsShort: false); // → LongActive
            Assert.That(_sm.State, Is.EqualTo(EStrategyState.LongActive));
        }

        [Test]
        public void LongActive_sends_ProtectStop_every_tick()
        {
            var r = _sm.ProcessBar(BarFactory.BullishBar(4), positionIsLong: true, positionIsShort: false);
            Assert.That(r.Orders.Any(o => o.Type == EOrderType.LongProtectStop), Is.True);
        }

        [Test]
        public void LongActive_sends_TargetLimit_every_tick()
        {
            var r = _sm.ProcessBar(BarFactory.BullishBar(4), positionIsLong: true, positionIsShort: false);
            Assert.That(r.Orders.Any(o => o.Type == EOrderType.LongTargetLimit), Is.True);
        }

        [Test]
        public void LongActive_TargetLimit_is_above_EntryStop()
        {
            Assert.That(_sm.TargetPrice, Is.GreaterThan(_sm.EntryStop));
        }

        [Test]
        public void LongActive_ProtectStop_is_below_EntryStop()
        {
            Assert.That(_sm.ProtectStop, Is.LessThan(_sm.EntryStop));
        }

        [Test]
        public void LongActive_profit_target_hit_transitions_to_ScanningLong()
        {
            // Position closes (profit) — stalking should persist
            var r = _sm.ProcessBar(BarFactory.BullishBar(4), positionIsLong: false, positionIsShort: false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningLong));
        }

        [Test]
        public void LongActive_stop_loss_hit_transitions_to_ScanningLong()
        {
            // Position closes (loss) — stalking should still persist
            var r = _sm.ProcessBar(BarFactory.BearishBar(4), positionIsLong: false, positionIsShort: false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningLong));
        }

        [Test]
        public void LongActive_after_close_state_resets_price_levels()
        {
            _sm.ProcessBar(BarFactory.BullishBar(4), positionIsLong: false, positionIsShort: false);
            Assert.That(_sm.EntryStop,   Is.EqualTo(0));
            Assert.That(_sm.ProtectStop, Is.EqualTo(0));
            Assert.That(_sm.TargetPrice, Is.EqualTo(0));
        }

        [Test]
        public void LongActive_after_profit_new_hammer_re_arms()
        {
            // Trade closes → ScanningLong
            _sm.ProcessBar(BarFactory.BullishBar(4), positionIsLong: false, positionIsShort: false);
            // New hammer forms
            var r = _sm.ProcessBar(BarFactory.LongHammer(5), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ArmedLong));
        }

        [Test]
        public void LongActive_ShiftClick_sends_CloseLong_and_goes_Inactive()
        {
            _sm.OnShiftClick();
            var r = _sm.ProcessBar(BarFactory.BullishBar(4), positionIsLong: true, positionIsShort: false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.Inactive));
            Assert.That(r.Orders.Any(o => o.Type == EOrderType.CloseLong), Is.True);
        }

        [Test]
        public void LongActive_ShiftClick_does_not_resume_scanning()
        {
            _sm.OnShiftClick();
            _sm.ProcessBar(BarFactory.BullishBar(4), positionIsLong: false, positionIsShort: false);
            // Even after position closes, should stay Inactive (user said stop)
            var r = _sm.ProcessBar(BarFactory.LongHammer(5), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.Inactive));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SUITE 5 — Full trade cycle
    // ──────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class FullTradeCycleTests
    {
        [Test]
        public void Full_long_trade_cycle_with_stalking_persisting()
        {
            var sm = new RenkoStateMachine { TickSize = 0.25, ProtectiveStopTicks = 2 };
            sm.OnCtrlClick(isLong: true);

            // Bar 1: enter ScanningLong
            var r = sm.ProcessBar(BarFactory.BullishBar(1), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningLong));

            // Bar 2: hammer → ArmedLong
            r = sm.ProcessBar(BarFactory.LongHammer(2), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ArmedLong));

            // Bar 3: entry stop fills → LongActive
            r = sm.ProcessBar(BarFactory.BullishBar(3), positionIsLong: true, positionIsShort: false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.LongActive));

            // Bar 4: in trade, managing position
            r = sm.ProcessBar(BarFactory.BullishBar(4), positionIsLong: true, positionIsShort: false);
            Assert.That(r.Orders.Any(o => o.Type == EOrderType.LongProtectStop), Is.True);
            Assert.That(r.Orders.Any(o => o.Type == EOrderType.LongTargetLimit),  Is.True);

            // Bar 5: profit target hit — position closes
            r = sm.ProcessBar(BarFactory.BullishBar(5), positionIsLong: false, positionIsShort: false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningLong)); // stalking persists!

            // Bar 6+: new hammer immediately re-arms
            r = sm.ProcessBar(BarFactory.LongHammer(6), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ArmedLong));
        }

        [Test]
        public void Armed_cancel_then_immediate_rearm_on_next_hammer()
        {
            var sm = new RenkoStateMachine { TickSize = 0.25, ProtectiveStopTicks = 2 };
            sm.OnCtrlClick(isLong: true);
            sm.ProcessBar(BarFactory.BullishBar(1), false, false);  // Scanning
            sm.ProcessBar(BarFactory.LongHammer(2), false, false);  // Armed

            // Counter-trend bar → cancel back to scanning
            var r = sm.ProcessBar(BarFactory.BearishBar(3), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ScanningLong));

            // New hammer right after
            r = sm.ProcessBar(BarFactory.LongHammer(4), false, false);
            Assert.That(r.State, Is.EqualTo(EStrategyState.ArmedLong));
        }

        [Test]
        public void Two_full_trades_in_sequence_both_succeed()
        {
            var sm = new RenkoStateMachine { TickSize = 0.25, ProtectiveStopTicks = 2 };
            sm.OnCtrlClick(isLong: true);

            // Trade 1
            sm.ProcessBar(BarFactory.BullishBar(1), false, false);
            sm.ProcessBar(BarFactory.LongHammer(2), false, false);
            sm.ProcessBar(BarFactory.BullishBar(3), positionIsLong: true, positionIsShort: false);
            sm.ProcessBar(BarFactory.BullishBar(4), positionIsLong: false, positionIsShort: false); // closes

            Assert.That(sm.State, Is.EqualTo(EStrategyState.ScanningLong));

            // Trade 2
            sm.ProcessBar(BarFactory.LongHammer(5), false, false);
            Assert.That(sm.State, Is.EqualTo(EStrategyState.ArmedLong));

            sm.ProcessBar(BarFactory.BullishBar(6), positionIsLong: true, positionIsShort: false);
            Assert.That(sm.State, Is.EqualTo(EStrategyState.LongActive));

            sm.ProcessBar(BarFactory.BullishBar(7), positionIsLong: false, positionIsShort: false);
            Assert.That(sm.State, Is.EqualTo(EStrategyState.ScanningLong));
        }
    }
}
