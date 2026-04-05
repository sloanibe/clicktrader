# Renko Tail Trading Rules

> **Status**: Draft — under active development. Rules will be refined as training images are reviewed.

---

## Overview

This strategy identifies **"Deep Pierce Rejection"** setups on Renko charts. The core premise is that in a trending market, when a bar's tail briefly pierces through the previous bar's extreme but the body closes back in the direction of the trend, it signals a failed counter-trend attempt and a high-probability continuation entry.

---

## 1. Trend Regime Detection

**Tool**: 10-period Exponential Moving Average (EMA) — the "Green Line"

| MA Slope at Click Point | Regime | Signal Direction |
|---|---|---|
| Rising (current EMA ≥ prior EMA) | **Uptrend** | Hunt for LONG setups |
| Falling (current EMA < prior EMA) | **Downtrend** | Hunt for SHORT setups |

The slope is evaluated at the **exact bar where the user clicks**, not the current bar.

### Steepness — Formal Definition

MA steepness is measured as the **angle off the vertical**, using a brick-normalized slope:

```
rise_per_bar  = |EMA[now] - EMA[1 bar ago]| / brick_size
angle_off_vertical = atan2(1 bar, rise_per_bar) × (180 / π)
```

Because each Renko bar has a fixed price height (the brick size), this normalization makes the angle **instrument-independent and scale-invariant**.

| Angle off Vertical | EMA moves per bar | Classification |
|---|---|---|
| ~27° | 2.0 bricks | Very steep ✅ |
| 45° | 1.0 bricks | Steep diagonal ✅ |
| **60°** | **0.58 bricks** | **Default threshold** |
| 75° | 0.27 bricks | Moderate — borderline |
| 84° | 0.10 bricks | Nearly flat ❌ |

> **Rule**: The MA must have an angle off vertical of **≤ 60°** (configurable via `MaxSlopeAngle` input) to qualify for signal detection. Angles greater than 60° indicate a flat or transitioning MA — no trades are taken.

---

## 2. The "Deep Pierce" Entry Rule

The entry bar must satisfy **all** of the following:

### LONG Setup (Uptrend Context)
- The entry bar **closes Blue** (bullish close: Close > Open)
- The entry bar's **Low** is equal to or below the preceding bar's Low
- The preceding bar can be **any color** (Red or Blue)
- Order is placed at the **close** of the entry bar

### SHORT Setup (Downtrend Context)
- The entry bar **closes Red** (bearish close: Close < Open)
- The entry bar's **High** is equal to or above the preceding bar's High
- The preceding bar can be **any color** (Red or Blue)
- Order is placed at the **close** of the entry bar

> **Key Insight**: The color of the *preceding* bar does not matter. What matters is that the current bar's tail pierces the prior bar's extreme and then **closes back** in the trend direction.

---

## 3. MA Proximity Rule (Quality Filter)

The entry bar's relationship to the 10 EMA determines whether the setup is high or low quality.

### LONG Setup
- The **body** of the Blue bar should be **above** the MA ✅
- The **tail** (lower wick) may touch or pierce **just slightly below** the MA ✅
- The tail **deeply below** the MA = ❌ **INVALID** — this is a breakout, not a rejection

### SHORT Setup
- The **body** of the Red bar should be **below** the MA ✅
- The **tail** (upper wick) may touch or pierce **just slightly above** the MA ✅
- The tail **deeply above** the MA = ❌ **INVALID** — this is a breakout, not a rejection

> **Why**: The MA acts as dynamic support/resistance. A valid setup shows the market **briefly testing** the MA and being **rejected**. A tail that blows deep through the MA is not a rejection — it is a potential breakout and the signal is compromised.

---

## 3b. MA Separation Distance (Trend Conviction Filter)

> **This rule is for the FUTURE AUTOMATED version.** In the current click-based system, the human's click serves as the conviction judgment.

A valid trend context requires not just that the bar is **on the correct side** of the MA, but that there is a **meaningful gap** between the bar's body and the MA line. Bars that are hugging or crossing the MA lack conviction.

```
separation = bodyBottom - emaValue   (for LONG)
separation = emaValue - bodyTop      (for SHORT)
```

| Separation (in bricks) | Classification |
|---|---|
| < 0 | Body crossing MA — ❌ invalid context |
| 0 – 0.5 bricks | Hugging MA — borderline |
| 0.5 – 1.5 bricks | Moderate separation ✅ |
| > 1.5 bricks | Strong separation — high conviction ✅✅ |

**Combined with steepness:** A valid automated scan context requires BOTH:
- MA angle off vertical ≤ `MaxSlopeAngle` (MA is steep)
- Bar body separation ≥ minimum threshold (trend has conviction)

> **Training note**: The images showing missed entries in clear uptrends (bars well above a steeply rising MA) are specifically teaching this rule. The combination of steep angle + large separation = high-probability scan zone.

---

## 4. Clustering / Proximity Filter

Even if a setup independently meets all entry rules, it is **skipped** if it occurs too soon after a prior taken entry.

- **Too clustered** = the setup appears within too few bars of the previous valid signal
- The exact minimum bar separation will be **calibrated from training images**
- Only the **first valid setup** in a cluster is taken; subsequent setups in the same cluster are skipped

> **Why**: Taking clustered signals leads to overtrading in choppy, compressed price action. A reset period is needed after each entry before looking for the next one.

---

## 5. Scan Logic (Manual Stalker Mode)

1. User holds **Control** and **left-clicks** on the chart at the start of a trend context
2. The indicator reads the **10 EMA slope** at the clicked bar to determine regime (Long or Short)
3. A **dashed vertical line** marks the scan start point (Green = uptrend, Red = downtrend)
4. The indicator scans **forward** from the click point, bar by bar
5. The **first bar** that satisfies all entry rules is marked with an arrow and label
6. The **diagnostic label** (top right) confirms the regime and result

---

## 6. Training Image Annotation Convention

| Annotation | Meaning |
|---|---|
| ⬇️ **Down arrow above bar** | Valid SHORT entry — take this trade |
| ⬆️ **Up arrow below bar** | Valid LONG entry — take this trade |
| ⭕ **Circle / ellipse around bar** | Setup is technically valid but **skipped** — too clustered with a prior entry |
| *(no annotation)* | No valid setup exists in this market segment |

---

## 7. Rules Summary Checklist

Before marking any entry, all boxes must be checked:

### LONG
- [ ] 10 EMA is sloping **UP** at the context bar
- [ ] Entry bar **closes Blue**
- [ ] Entry bar **Low ≤ prior bar Low**
- [ ] Entry bar **body is above** the 10 EMA
- [ ] Entry bar **tail does not deeply pierce** below the 10 EMA
- [ ] Entry is **not clustered** within minimum bar distance of a prior entry

### SHORT
- [ ] 10 EMA is sloping **DOWN** at the context bar
- [ ] Entry bar **closes Red**
- [ ] Entry bar **High ≥ prior bar High**
- [ ] Entry bar **body is below** the 10 EMA
- [ ] Entry bar **tail does not deeply pierce** above the 10 EMA
- [ ] Entry is **not clustered** within minimum bar distance of a prior entry

---

## 8. Open Questions (To Be Resolved from Training Images)

| Question | Status |
|---|---|
| What is the minimum bar separation to avoid "clustering"? | 🔲 TBD — appears to be ~2 bars minimum from image 1 |
| What is the maximum tail-through-MA distance (in ticks) before a setup is invalid? | 🔲 TBD |
| Does the MA slope need to exceed a minimum steepness threshold? | 🟡 Partially — flat/transitioning MA zones appear to produce NO entries |
| Can a valid entry occur on the very first bar after the context click? | 🔲 TBD |
| Is bar count the right clustering metric, or should it be price distance? | 🟡 Partially — image 1 suggests bar count is primary metric |

---

## 9. Observations from Training Images

### Image 1 — Full Market Cycle (Multiple Swings)

**Annotations observed:**
- Multiple small entry markers (red dots) in both uptrend and downtrend sections
- One blue circle/ellipse at the first V-bottom trough — two consecutive blue bars, first taken, second skipped

**Rules extracted or confirmed:**

#### Rule: MA Must Be Actively Sloping (Not Flat)
Entries are **only taken when the MA has clear directional slope**. In transitional zones where the MA is curving or flat between swings, few or no entries are marked. A flat MA = **no trade zone**.

#### Rule: Entries Are Taken EARLY in the Trend Move
Marked entries appear near the **beginning of each trending leg**, not late in the move when it is extended. This suggests entries should only be valid for the first N bars after the MA establishes its slope.

#### Rule: Clustering Applies to Same-Direction Bars Immediately Adjacent  
The cluster circle in Image 1 appears at a V-bottom where **two consecutive Blue bars appeared back-to-back**. The first was taken; the second was circled (skipped). This suggests the minimum separation between entries of the same direction is at least **2 bars**.

#### Rule: Both Long AND Short Signals Are Active in the Same Session
The full market cycle image shows short entries during downswings and long entries during upswings taken in the same time period, confirming the strategy is **regime-switching and bidirectional**.

---

### Image 2 — Steep Downtrend with Cluster Rejection

**Annotations observed:**
- **Three** red down-arrows marking valid SHORT entries (Entry 1, 2, 3)
- Entry 2 and Entry 3 both show tails poking slightly **above** the steep MA
- One large blue circle rejecting the **4th and 5th** technically-valid setups after Entry 3

**Rules extracted or confirmed:**

#### Rule: MA Must Be STEEP, Not Just Sloping
All three taken entries occurred during a **steep, sustained downtrend**. The MA is visibly angled sharply. Steepness is a required quality filter — gradual drift does not qualify.

#### Rule: Tail Pierce Through MA Must Be SMALL ("Just a Little Bit")
The user confirmed: *"entry two's tail was poking through slightly above the steep MA and entry three's tail is also slightly poking through."*  
All three valid entries show tails that **barely pierce** the MA. `MaxTailPierceTicks` is a small value — a brief test, not a break.

#### Rule: Cooldown Rejects ALL Subsequent Signals Until Gap Is Restored
After Entry 3, the **4th and 5th** valid setups are circled/skipped. Both entries 1→2→3 had sufficient separation to qualify. Entry 4 did not.

#### Rule: Minimum Bar Gap Bounded by Image 2
- Entry 2 → Entry 3 gap = **sufficient** (Entry 3 was taken) — sets the **lower bound**
- Entry 3 → Circle gap = **insufficient** (Entry 4 was skipped) — sets the **upper bound**  
- `MinBarsBetweenEntries` is somewhere between those two gaps
- Estimated range: **3–6 bars** (to be confirmed with user)

---

### Image 3 — Clean Uptrend, No Entries (No Pierce)

**Annotations observed:**
- No arrows — zero entries taken
- Strong uptrend: blue bars stacking cleanly above a steeply rising MA
- Bars are well separated from the MA (bodies far above the green line)
- Some bars have downward tails, but none qualify

**Rules confirmed:**

#### Rule: "Deep Pierce" Requires Tail to TOUCH OR GO THROUGH Previous Bar's Low
The bars in this image had tails that dipped downward but **did not reach the previous bar's Low**. The pierce threshold is: `currentBar.Low ≤ previousBar.Low`. A tail that reaches into the previous bar's range but stops before the Low = **no signal**.

> "It can just touch; it can pierce just by touching. It can go either: touching or going through." — User

#### Rule: Clean Trend With Offset Bars = No Signal
When bars are well-spaced and stacking cleanly (each bar opens near where the last closed), there are no "rejection" tails reaching back. This is a **no-trade zone** despite a steep and valid MA slope. The absence of entries here confirms the pierce rule is the primary gating condition.


