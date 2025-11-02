# MyRankOSimulation Simulation Logic

## Session management
- Data set: ~20 trading days of tick-level Renko data.
- Daily session start: 06:00 (instrument timezone).
- First click at or after 06:00 triggers a new session:
  - Log to console: `SESSION START | yyyy-MM-dd | 06:00`.
  - Draw a horizontal demarcation line (e.g., cyan) across the chart at the first closed brick ≥ 06:00. Lines persist until removed manually by the user.
- When a click’s bar date differs from the current session date, automatically close out the prior session (log cumulative P&L), then start the new session as above.

## Trade lifecycle overview
- Every click represents the intent to enter a position at the close of the clicked brick.
- Entry price: close of the clicked brick.
- Trade direction is determined as follows:
  1. **Ctrl only** ⇒ auto-resolve using clicked brick direction.
     - Blue/increasing brick → Buy.
     - Red/decreasing brick → Sell.
     - If the brick is neutral (rare), fall back to tail dominance (longer tail determines direction).
  2. **Shift only** ⇒ force Buy.
  3. **Ctrl + Shift** ⇒ force Sell.
  4. No modifier ⇒ ignore the click.
- Each trade is appended to an in-memory journal with:
  - Timestamp & bar number of entry.
  - Entry price.
  - Direction (Buy/Sell).
  - State (`Open`, `Closed`, etc.).

## Long trade rules (Sell rules are symmetric)
1. **Entry** at close of clicked brick.
2. Track the number of bricks gained relative to entry (`currentBricks = floor((Close - EntryPrice) / BrickSize)`).
3. Maintain `maxBricks` (peak bricks-in-favor) while trade is open.
4. Exit conditions:
   - **Immediate loss**: first brick after entry is red (no positive bricks). Result: `-2` bricks (hard stop).
   - **Partial run**: trade reaches at least `+1` brick but never hits `+2`, and a red brick prints. Result: `-1` brick.
   - **Break-even**: trade reaches `+2` bricks, then a red brick forms that brings price back to entry ± tick. Result: `0` bricks.
   - **Profit take after run**: trade reaches `+N` bricks (`N ≥ 2`). On the first red brick that closes, exit with `maxBricks - 2` bricks (give back exactly two bricks from peak).
   - Trades are always closed on the **close** of the red brick triggering the exit.
5. When a trade closes:
   - Compute tick P&L: `bricksResult * BrickSize`.
   - Convert to dollars via instrument tick value if provided (future enhancement).
   - Append log entry and update running session totals.

## Short trade rules (mirrored)
- Entry logic identical but based on red bricks.
- Reverse color references:
  - Immediate loss: first green brick (give up 2 bricks).
  - Partial run: reaches −1 (in favor) but not −2, then green brick → `-1` brick.
  - Profit run: after hitting at least −2, first green brick exit returns `maxAbsBricks - 2` bricks.
- Exit price is the close of the green brick.

## Session P&L tracking
- Maintain cumulative brick P&L and dollar P&L for current day.
- After each trade closes:
  - Log: `TRADE CLOSE | Direction | EntryTime → ExitTime | Bricks Δ | Session Bricks | Session $`.
  - If session P&L ≥ +2 bricks (or user-defined threshold), optionally mark session as satisfied (future enhancement).
- On day roll or manual reset:
  - Log summary: `SESSION END | yyyy-MM-dd | Trades: X | Bricks: Y | $: Z`.

## Visual markers
- Buy marker: blue "B" rendered below brick tail.
- Sell marker: red "S" rendered above brick tail.
- Daily divider: horizontal line drawn at first session brick 
  (color suggestion: cyan, label with date/time).
- All drawings persist because `[RecoverDrawings(false)]` attribute is applied and objects are tracked in dictionaries.

## Key data structures (planned)
- `SessionState`
  - Current date, running P&L, list of trades.
  - Reference to daily divider drawing object.
- `TradeState`
  - Unique ID, direction, entry bar/time/price.
  - Current status (Open/Closed).
  - `maxBricks`, `resultBricks`, exit info.

## Expected console messages
- Session start/end notifications.
- Trade open/close events with P&L details.
- Auto-resolve direction notes.
- Daily divider creation message.

## Next implementation tasks
1. Implement session tracking (date detection, logging, divider drawing).
2. Add trade manager to handle open position state and update on each new brick.
3. Integrate P&L calculations and session totals.
4. Extend logging with summary tables or CSV export (future).
