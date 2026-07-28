# Rolling Fib — Strategy Specification v1.0
*MES/ES intraday · 1H qualification → 15m confirmation → 5m execution*
*Drafted 2026-07-03 from operator screenshot (MES, holiday session, 9am qualifying candle 7549.25→7559.25)*

---

## 1 · Concept

A 1-hour candle with abnormal height + volume ("Qualifying Candle", hour H) becomes a structural
map for the following hour (H+1). A fib retracement is anchored over the candle (0 at the origin
extreme, 100 at the momentum extreme, 161.8 extension), and a **midline is plotted between every
adjacent pair of levels — including between 100 and 161.8**. Midlines act as pivots: acceptance
beyond a midline rotates price to the next main level; rejection rotates it back.

The grid "rolls": if hour H+1 itself qualifies, a new grid replaces the old one for hour H+2.

### Verified grid math (from screenshot, R = 10.00 pts, base 7549.25)

| Ratio | Price | Midline below it | Price |
|---|---|---|---|
| 0 | 7549.25 | — | — |
| 0.236 | 7551.50 | mid(0, .236) = 0.118R | 7550.50 |
| 0.382 | 7553.00 | mid(.236, .382) = 0.309R | **7552.25** ← "the 23 midline" |
| 0.5 | 7554.25 | mid(.382, .5) | 7553.75 |
| 0.618 | 7555.50 | mid(.5, .618) | 7554.75 |
| 0.786 | 7557.25 | mid(.618, .786) | 7556.25 |
| 1.0 | 7559.25 | mid(.786, 1) | 7558.25 |
| 1.618 | 7565.50 | mid(1, 1.618) = **1.309R** | **7562.25** ← "the 1.618 midline" |

Every dashed level on the operator's chart is reproduced exactly by `mid(i) = (level_i + level_{i+1})/2`
rounded to tick. Quant note: mid(1, 1.618) = the 1.309 extension — sitting inside the classic
1.272–1.382 exhaustion cluster. The observed rejection there is consistent with well-documented
extension-fade behavior, which is why Setup B below exists.

---

## 2 · Qualification Engine (the radar)

A fixed "10 points" gate is regime-fragile (VIX 12 vs VIX 30 hours are different animals) and
hour-of-day-blind (the 9am hour is structurally larger than the 3am hour). All gates are adaptive:

| # | Gate | Rule | Default | Why |
|---|---|---|---|---|
| 1 | Session | hour-of-day ∈ [qStartH, qEndH] (ET) | 07–13 | Liquidity; trade window must land in RTH-adjacent hours |
| 2 | Height | R ≥ kRange × E[R \| hour-of-day] AND R ≥ minAbsPts | 1.25× / 8 pts | Abnormal expansion vs that hour's own baseline; abs floor keeps stops viable |
| 3 | Volume | V ≥ kVol × E[V \| hour-of-day] | 1.10× | "Enough volume", normalized for intraday volume seasonality |
| 4 | Body | \|close − open\| / R ≥ minBody | 0.45 | A 10-pt round-trip doji is rotation, not conviction — must not qualify |
| 5 | Close location | bull: (close−low)/R ≥ minCLV; bear mirrored | 0.60 | Close in the momentum third — rejects big-wick reversals posing as trend candles |
| 6 | Completeness | ≥ 12 five-minute bars in the hour | fixed | Skips half-built hours (holiday closes, data gaps) |

E[·] = EMA per hour-of-day (default 10 days), fallback rolling 14-hour ATR while warming up.

- **:45 pre-arm** — at the :45 close of a developing hour, if range-so-far ≥ 0.80 × height gate,
  fire "RADAR ARMED" (matches the operator's observation that qualification is usually knowable
  by the :45 candle). Final decision only at the :00 close.
- **Direction** = candle direction. Bull grid → longs (continuation); bear grid → shorts. The
  fade setup is the only counter-direction trade.

## 3 · Grid & State Machine (trade hour H+1)

States: `IDLE → ACTIVE (grid live, no confirm) → CONFIRMED(supportIdx) → …ENTRY(s)… → EXPIRED`,
with `DEAD` on invalidation.

**15m confirmation** (evaluated only on completed 15m bars inside H+1):
- Support confirmed at zone *i* when the 15m bar **dips to/through midline mᵢ and closes back
  beyond it** (bull: low ≤ mᵢ and close > mᵢ). Highest such midline wins; later 15m closes can
  upgrade the pivot (the intra-hour ladder → continuation entries).
- **Invalidation**: 15m close beyond level 0 against the grid → state DEAD, no more entries.

**5m entry — Setup A: Rotation Continuation** (after a 15m confirm, i.e. earliest ~:15+):
- Entry band = pivot midline mᵢ up to level i+2 (e.g. confirm at the 23-midline → entries on
  pullbacks into the 38.2/50 shelf — exactly the screenshot trade).
- Trigger: completed 5m bar pulls back into the band, closes beyond the pivot, with a
  directional body (close > open for longs).
- Stop: below pivot midline − buffer (default, aggressive) or below zone level − buffer (conservative). 2-tick buffer.
- Targets: T1 = first main level ≥ minRR (default 1.2R), T2 = next main level (typically 78.6),
  T3 = 100. Scale 50/25/25, move to BE after T1.
- Max 3 entries/hour; **no new entries after minute :40**.

**5m entry — Setup B: Extension Fade** (counter-trend, half size):
- Arm when price tags the extension midline (1.309R). Fire when a 5m bar closes back inside
  below level 100. Stop beyond the extension extreme + buffer. Targets 78.6 → 61.8. Once per grid.

**Timing skeleton** (reproduces the observed cadence): :00 qualify+plot → :15 first possible
confirm (screenshot: 10:15) → :15–:40 entries → :45 checkpoint alert = tighten to BE / expect
changeover (screenshot: 10:45 turn) → :00 window closes; if the new hour qualifies, the grid rolls.

## 4 · Risk framework

Example on the screenshot candle (R = 10): entry 7554.25 (50), stop 7552.00 (below 23-midline
− 1 tick) → risk 2.25 pts; T1 7557.25 (+3.00, 1.33R), T2 7559.25 (+5.00, 2.2R). MES commission
≈ 1 tick RT — with 4–9 tick stops that's a real drag: size in multiples, or trade ES only when
R is large. Fixed dollar risk per entry; daily stop = 2 losing grids; consecutive-loss halt at 3.

## 5 · Gaps in the original logic → resolutions

1. "Enough height/volume" undefined → adaptive hour-of-day gates (above).
2. No direction rule → body + close-location gates; doji hours skipped (v2 idea: rotation mode fading 0/100 toward 50).
3. Stop viability unstated → 8-pt absolute floor keeps the narrowest midline stop ≥ ~4 ticks.
4. Conflicting grids on roll → newest grid wins; an opposite-direction qualification while holding a runner = exit signal.
5. No-pullback trend hours (price opens H+1 pinned above 78.6) → currently a miss; v2: "acceptance continuation" (first 15m holds above 78.6 → enter break of its high, targets = extensions).
6. News → skip trade hours containing 8:30/10:00/14:00 red-folder releases (manual toggle v1).
7. Holiday/early-close hours → completeness gate.

## 6 · Parameter ranges (robustness, not point-fits)

kRange 1.10–1.50 · minAbsPts 6–12 · kVol 1.00–1.30 (0=off) · minBody 0.40–0.60 ·
minCLV 0.55–0.70 · seasLen 7–15 d · cutoffMin 35–45 · stopBufTks 1–3 · maxEntries 2–3 · minRR 1.0–1.5

## 7 · Validation roadmap

1. **Event statistics before betting** (indicator alerts → log): P(15m confirm | qualify),
   P(T1 | confirm), P(100 touched | confirm), P(fade arm | qualify), MAE/MFE per entry, by
   hour-of-day and direction. Kill criteria: P(T1|confirm) < 55% or EV/trade < 1.5 ticks net.
2. Port to a `strategy()` (fill-lag pattern, BE management) → backtest 6–12 months of 5m MES.
3. Export the trade list CSV → run the strategy-validation-harness (walk-forward split,
   Monte Carlo 95th-pct drawdown, commission/slippage re-pricing) before sizing up.

---

## 8 · v1.1 — Replay findings & amendments (2026-07-04)

From operator replay on MES (Jun 1, 3, 4, 23, 24 '26 sessions):

| # | Finding (evidence) | Amendment |
|---|---|---|
| 1 | **Post-spike blindness** — Jun 24 crash inflated the hour baseline; gate hit 30.6 pts, later hours blocked "Height 5.75 < 30.58" | Winsorize baseline updates: an hour enters the range/volume EMA at no more than `capOut` (2.0×) the current EMA |
| 2 | **0-entry trend hours** — Jun 24 & Jun 4: grids rolled, price accepted the top of the grid and ran 50–70 pts, Setup A never triggered (no pullback to a pivot) | **Setup C — Acceptance Continuation**: 15m holds above the 61.8 and closes beyond the 78.6 (no pivot given) → arm; 5m momentum close beyond the 100 → enter with stop beyond the 78.6/100 midline, T1 = 1.309R extension midline, T2 = 161.8. Once per grid, respects MinRR + cutoff |
| 3 | **Degenerate fades** — Jun 23: FADE LONG with 18-pt stop for 0.75-pt T1 (0.04R) because the stop anchored to an extreme far beyond 161.8 | Fade fires only if the extreme stayed inside the 161.8 (beyond it = trend, not exhaustion) AND ticket RR ≥ fadeMinRR (1.0) |
| 4 | **Pivot double-fire** — Jun 1: two identical entries seconds apart off one pivot | One Setup-A entry per confirmed pivot; re-arms only on a NEW 15m confirm |
| 5 | Session cap at 13:00 leaves the 15:00–16:00 window untradeable even when the 14:00 hour qualifies | Default last qualifying hour 13 → 14 (window 15:00–16:00) |
| 6 | **Open question for backtest, not radar**: Jun 3 short paid AFTER the window closed — flatten-at-window-end may cut workers | Test three exit modes in NinjaTrader: (A) flatten at window end, (B) hold with stop trailed to last confirmed midline, (C) hold only while same-direction grids keep rolling |

Note on C + fade coexistence: after an acceptance entry runs to the extension, a fade signal
doubles as the exhaustion exit for the runner — the two setups are the same hypothesis viewed
from both sides of the 1.309 midline.
