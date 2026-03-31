# Project Memory: Renko and Range Trading System

This document serves as a "Memory Bank" and Roadmap for the synchronized trading system built for MultiCharts.NET.

## 1. Implemented Features (The Core Signals)

### RenkoBarTrading.cs (The Discrete Signal)
- **Snap-to-Level Logic**: Entry orders (Control-Click) automatically snap to the mathematically correct Renko projection levels, matching the `projected_future_renko-horz` indicator exactly.
- **One-Bar Expiration**: Pending entry orders automatically expire and clear from the chart if a new Renko brick completes before they are filled.
- **Visuals**: Thick dashed Cyan/Magenta lines for the entry price.

### RangeBarTrading.cs (The Fluid Signal)
- **Continuous "Glide" Logic**: Entry orders "chase" the developing tail of the range bar in real-time until they are filled, providing the most efficient possible entry on high-transparency bars.
- **Cross-Bar Persistence**: Unlike Renko, the Range signal maintains its pursuit across multiple bar completions since range bars are inherently more fluid.

### Shared Features (Active in Both)
- **20-Tick Profit Target**: Both default to a 5-point (20-tick) "Take Profit" Limit order upon entry.
- **Protective Stop**: An automatic Stop order is placed under the relevant bar tails on entry.
- **Draggable Exits ("Pick-and-Move")**: 
    - Click the **Gold** (Profit) or **Red** (Stop) line once to "Grab" it (line turns White).
    - Click the chart at a new price level to "Drop" it. Broker orders are instantly updated.
- **Visual Brick/Range Grid**: Upon entry, a subtle grid of 5 dashed DimGray lines appears, spaced exactly at the brick/range size. This acts as a visual "ruler" for measuring profit in units of bars.
- **Hard Order Cleanup**: All lines and pending exit orders are explicitly wiped from the broker and chart accurately upon trade closure (Win or Stop-out).

---

## 2. Future Concepts (The Command Center)

### Dashboard Architecture (Next.js + Node.js)
- **The "Relay Hub"**: A local Node.js server acting as the bridge between MultiCharts and the UI.
- **The Dashboard**: A premium, "Fintech" style web app built in **Next.js** for a secondary monitor.
- **Real-Time HUD**: Displays live Ticks PnL, "Win-Loss" milestones, and "System Heartbeat" diagnostics.
- **Remote Controls**: One-click "Flatten All" or "Adjust Offset" buttons directly from the web interface.

### AI Co-Pilot Integration
- **Real-Time Context**: Passing trade context (Renko bar history, PnL, EMA Slope) to an AI in real-time via the Relay Hub.
- **Expert Verdicts**: Text-based or Voice-based insights (e.g., "Volality compression suggests a reversal ahead").
- **Dynamic Optimization**: AI-suggested setting tweaks (e.g., "Market speeding up—increase Level1 to 8 ticks for better stability").

---

## 3. High Performance Defaults
- **Compiler**: Use **RELEASE** mode for all live/simulated action to ensure sub-millisecond responsiveness.
- **Trading Hours**: Optimized for ES/MES (4-tick = 1 point) but configurable for MNQ/MYM.
