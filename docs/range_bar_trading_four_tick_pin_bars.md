# Ctrl-click: 4-tick pin-bar watch

Ctrl+left-click arms a dedicated 4-tick pin-bar watch. The HUD shows
`ARMED | WAITING FOR 4-TICK PIN BARS`. A second Ctrl+left-click disarms and
cancels any pending entry. Ctrl-click during an open position leaves trade
management alone. Esc+left-click and F12+left-click retain emergency flatten.

This watch uses the existing chart roles: buys on the ask chart, sells on the
bid chart (`IsAskChart=true` for ask, `false` for bid). Ctrl-click selects that
chart's permitted direction only when the live EMA stack matches that side:
8 > 24 for ask buys, or 8 < 24 for bid sells. Equal or
out-of-order EMAs block arming and display a reason in the HUD. A rejected
click preserves any existing pending manual entry. Disarming remains
available regardless of the current EMA stack. Other arming paths reject a short on an ask
chart or a long on a bid chart before changing the current arming state.
It operates independently of the F1/F2/F3 setup switches and does
not apply the legacy PB slope or minimum EMA-separation filters. It does
require strict live EMA ordering: 8 > 24 for buys, and 8 < 24 for
sells. The 50 EMA filter is temporarily disabled for this Ctrl-click watch.
Equal EMAs fail this rule. The immediately preceding range bar must
not have the opposite color: bullish or doji for buys, bearish or doji for
sells. Both checks run on every update and use the same cancellation path as
an invalid tail, leaving the watch armed. EMA bounce entries are suppressed
while this dedicated watch is active.

A forming lower-tail buy setup projects an entry at the current low plus
4 ticks. A forming upper-tail sell setup projects an entry at the current
high minus 4 ticks. The projection appears when the tail reaches 3 ticks
from the open (1-tick body), and moves to the open if it reaches 4 ticks
(0-tick body). Its label is `PB1 BUY/SELL` or `PB0 BUY/SELL`.

On every update, the tail must stay within the preceding bar's low for buys
or high for sells, and on the permitted side of the live 8 EMA. Exact contact
is allowed; penetration is rejected, with only numerical roundoff tolerance.
The opposite extreme must fit inside the projected bar. A completed bar must
close at the projected reversal boundary, rather than at the tail.

An invalid setup removes both projection guides and cancels its pending
entry. The watch stays armed for subsequent valid setups. Unfilled entries
expire when their bar changes. A confirmed entry fill consumes the armed
state. Entries use the signal's existing native stop-order submission and
protective-stop settings. A moving dashed stop preview uses that same distance before entry.

Pin and Shift entries share a session-P&L profit rule: zero or positive P&L at fill gets a fixed 4-tick target from the actual average fill; negative P&L gets no fixed target and takes profit on a later confirmed opposing-color bar close only while the individual trade is profitable from its actual average fill. Losing and break-even closes do not request a color exit; loss exits are handled by the protective stop at two ranges plus one tick or farther. Pending profit market exits are resubmitted only while the trade remains profitable. The category is latched from the pre-fill shared session snapshot and does not change while that trade is open. The fixed-target preview follows the entry only while session P&L is nonnegative; otherwise it is removed. All pending pin guides are removed on invalidation or fill. The protective stop stays active in both profit categories.

All RangeBarTrading automatic loss exits allow at least nine ticks from the
actual average entry price. The default protective stop remains two ranges
plus one tick, with a nine-tick minimum; explicit stop settings may widen
that distance but cannot reduce it. EMA bounce opposite-color trailing stops use the same minimum
when their projected exit would lose money. Break-even and profitable trails
can still tighten normally. This measures price movement, not elapsed bars.
Manual break-even clicks and emergency flatten remain immediate overrides.

The active range must be 4 ticks, from the range input or completed-bar
measurement. Otherwise the watch remains armed with a HUD message explaining
that it requires a 4-tick range, and stages no pin entry.

Validation: `python3 tools/check_four_tick_pinbar.py` compiles the production
projection methods into a harness with recorded drawing/cancellation calls.
It covers both directions, changing tails, boundary contact, penetration,
invalid shapes, completed bars, continued watching after invalidation, the
nine-tick automatic loss boundary, and profitable trailing exits.
