# Renko Tail Trading Strategy — Strategy Specification

## Core Concept
A professional, automated Renko trend-continuation strategy that enters on **Retracement Rejection (The Hook)**. The goal is to "stalk" a trend and enter when a pullback fails to form a counter-trend brick and instead rejects back into the trend direction.

---

## The State Machine (The Brain)

| Current State | Input / Event | New State | Action Taken |
| :--- | :--- | :--- | :--- |
| **Any State** | **Shift + Click** | `Inactive` | Send **Nuclear Flatten** Orders + Reset Prices |
| **Inactive** | **Ctrl + Click (Long)** | `ScanningLong` | Start watching for Pullback Pierce (Down) |
| **Inactive** | **Ctrl + Click (Short)** | `ScanningShort` | Start watching for Pullback Pierce (Up) |
| **ScanningLong** | **Down Pierce** ⬇️ | `ArmedLong` | **INSTANT ARM** while bar is red. Project **Buy Stop**. |
| **ScanningShort** | **Up Pierce** ⬆️ | `ArmedShort` | **INSTANT ARM** while bar is blue. Project **Sell Stop**. |
| **ArmedLong** | **Position Fills** (Long) | `LongActive` | Project **Profit Target** + **Stop Loss** |
| **ArmedLong** | **Bearish Close** 🔴 | `ScanningLong` | **CANCEL** (The counter-trend brick completed). |
| **ArmedShort** | **Position Fills** (Short) | `ShortActive` | Project **Profit Target** + **Stop Loss** |
| **ArmedShort** | **Bullish Close** 🔵 | `ScanningShort` | **CANCEL** (The counter-trend brick completed). |
| **LongActive** | **Target or Stop Hit** | `ScanningLong` | Return to Scan for next entry |
| **ShortActive** | **Target or Stop Hit** | `ScanningShort` | Return to Scan for next entry |

---

## Detailed Logic Rules

### 1. Detection (The Pierce)
Detection happens **Intra-Bar** (does not wait for bar close).
- **Short Entry (Star Pattern):** Arm when current `High > PrevOpen`. (The candle is "piercing" the previous red bar's open).
- **Long Entry (Hammer Pattern):** Arm when current `Low < PrevOpen`. (The candle is "piercing" the previous blue bar's open).

### 2. Instant Arming (No-Wait Rule)
The strategy **MUST** project the entry stop the moment the Pierce is detected.
- **DO NOT** wait for the candle color to change (e.g., stay armed while it is blue for short or red for long).
- **DO NOT** wait for the bar to close.

### 3. Entry Levels (The "Floor" & "Ceiling")
Orders are placed at the exact price where a Renko brick in the trend direction would print.
The number of bricks depends on whether the **previous bar** was with-trend or counter-trend:

| Previous Bar Color | Long Entry | Short Entry |
| :--- | :--- | :--- |
| **With-trend** (Blue for Long, Red for Short) | `PrevClose + 1 brick` | `PrevClose - 1 brick` |
| **Counter-trend** (Red for Long, Blue for Short) | `PrevClose + 2 bricks` | `PrevClose - 2 bricks` |

**Why 2 bricks for counter-trend?** One brick only gets you back to the top of the counter-trend bar (breaking even). Two bricks is where the market actually prints a **new trend brick**.

### 4. Cancellation (The "Invalidation" Rule)
If the retracement succeeds in forming a full brick against the trend, the opportunity is dead.
- **Cancel Short:** If the bar closes **BULLISH** (Blue).
- **Cancel Long:** If the bar closes **BEARISH** (Red).

### 5. Guards
- **One Bar Chill Out (Lockout):** To prevent "step-ladder" re-arming, once a signal is cancelled (Rejection Failed) or a trade is closed, the strategy captures the `CurrentBarIndex + 1`. It will not allow a new signal to arm until the market moves past that index. This ensures at least one full candle of "blank" space between trades.
- **Historical Filter:** All signals occurring before the user clicked (Ctrl-Click) are ignored. Scanning only looks forward from the click time.
- **Nuclear Flatten:** Shift-click bypasses everything. It hammers the close orders every single tick until the platform position is confirmed zero.
