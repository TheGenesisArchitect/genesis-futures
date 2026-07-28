# Master Prompt — NinjaTrader AI Assistant: Build & Test "RollingFib"

*Copy everything below the line into NinjaTrader's AI assistant. If the assistant truncates long
input, send PHASE 1 (sections 1–7) first, let it build and compile, then send PHASE 2 (sections 8–9).*

---

You are a senior NinjaScript (C#) developer and quantitative strategist. Build, compile, backtest,
and optimize a NinjaTrader 8 **Strategy** named `RollingFib` exactly to this specification. Work in
phases: **Phase 1** = write the strategy and confirm it compiles clean; **Phase 2** = run the
backtest and optimization protocol and report results. Do not simplify or omit any rule. Where this
spec conflicts with your defaults, this spec wins.

## 1 · Strategy concept (context)

A 1-hour candle on MES with abnormal height, volume, and directional conviction ("qualifying
candle", hour H) becomes a structural map for the following hour (H+1). A fibonacci grid is
anchored over the candle — ratios 0, 0.236, 0.382, 0.5, 0.618, 0.786, 1.0, 1.618 from the origin
extreme — plus a **midline between every adjacent pair of levels, including between 1.0 and 1.618**
(that midline = 1.309 × range, an exhaustion zone). Midlines are pivots. In hour H+1: a completed
15-minute bar that dips through a midline and closes back beyond it confirms support (bull) or
resistance (bear); completed 5-minute bars then time pullback entries in the candle's direction,
targeting the upper grid levels. If hour H+1 itself qualifies, the grid "rolls" to a new candle.

## 2 · Architecture rules (non-negotiable)

1. NinjaTrader 8 Strategy, C#, `Calculate = Calculate.OnBarClose`. Primary and ONLY data series:
   **5-minute bars**. Do **NOT** call `AddDataSeries()` — aggregate the 1-hour and 15-minute
   candles internally from 5-minute bars (avoids BarsInProgress sync bugs; clock hours align).
2. Time handling: bar timestamps (`Time[0]`) are bar CLOSE times. Convert every timestamp to US
   Eastern with `TimeZoneInfo.ConvertTime(Time[0], TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"))`.
   A 5m bar closes an hour when its ET close minute == 0; closes a 15m period when minute % 15 == 0.
   The hour-of-day bucket of a just-completed hourly candle = `etClose.AddMinutes(-1).Hour`.
3. Hourly accumulators (`hOpen, hHigh, hLow, hVol, hBarCount, hStartBarIdx`): reset on the first
   5m bar of each clock hour AND on `Bars.IsFirstBarOfSession`. 15m accumulators (`m15High, m15Low`)
   likewise per 15m period.
4. No look-ahead anywhere: every decision uses only completed bars/accumulators as of the current
   closed 5m bar. Entries therefore fill at the NEXT 5m bar's open — that is intended and realistic.
5. `TickSize` from the instrument (`Instrument.MasterInstrument.TickSize`); round every order price
   with `Instrument.MasterInstrument.RoundToTickSize()`. Never hardcode 0.25.
6. Guard `if (CurrentBar < BarsRequiredToTrade) return;`. Set `BarsRequiredToTrade = 300`.
7. `EntriesPerDirection = 8`, `EntryHandling = EntryHandling.UniqueEntries`,
   `IsExitOnSessionCloseStrategy = true`, `ExitOnSessionCloseSeconds = 30`.
8. Include a `bool DebugPrints` parameter that gates `Print()` diagnostics (qualification decisions
   with all gate values, state transitions, entry/exit reasons).

## 3 · Parameters (all `[NinjaScriptProperty]`, grouped, with these defaults)

| Name | Type | Default | Optimizer range | Meaning |
|---|---|---|---|---|
| QualStartHour | int | 7 | 6–9 | First ET hour-of-day allowed to qualify |
| QualEndHour | int | 13 | 12–14 | Last ET hour-of-day allowed to qualify |
| KRange | double | 1.25 | 1.10–1.50 step .05 | Height gate: R ≥ KRange × expected range for that hour-of-day |
| MinAbsPts | double | 8.0 | 6–12 step 1 | Absolute minimum candle height, points |
| KVol | double | 1.10 | 0, 1.0–1.3 step .1 | Volume gate multiple (0 = disabled) |
| MinBody | double | 0.45 | 0.35–0.60 step .05 | Min |close−open| / range |
| MinCLV | double | 0.60 | 0.55–0.70 step .05 | Min close-location in range (momentum-side) |
| SeasLenDays | int | 10 | 7–15 | EMA length (days) for per-hour-of-day baselines |
| MaxEntries | int | 3 | 2–3 | Max Setup-A entries per trade hour |
| CutoffMin | int | 40 | 30–45 step 5 | No new entries after this many minutes into H+1 |
| StopAtMidline | bool | true | both | true: stop below pivot midline; false: below zone level |
| StopBufTicks | int | 2 | 1–3 | Stop buffer beyond the structure, ticks |
| MinRR | double | 1.2 | 1.0–1.6 step .1 | Minimum reward:risk to T1 |
| EnableFade | bool | true | both | Extension-fade counter-trend setup |
| EnableShorts | bool | true | both | Allow bear-grid shorts |
| FlattenOnInvalidation | bool | true | fixed | Flatten if 15m closes beyond fib 0 against grid |
| CheckpointBE | bool | true | both | At minute 45 of H+1, move all stops to breakeven |
| FlattenAtWindowEnd | bool | true | fixed v1 | Flatten + cancel everything when H+1 ends |
| Contracts | int | 1 | fixed | Contracts PER LEG (each entry places 2 legs) |
| MaxConsecLosses | int | 3 | fixed | Halt trading for the day after N consecutive losing legs |
| DailyLossLimit | double | 300 | fixed | Halt trading for the day beyond this realized loss ($) |
| DebugPrints | bool | false | fixed | Diagnostics |

## 4 · Qualification engine (runs on every 5m bar that closes an hour, hBarCount ≥ 12)

Let `R = hHigh − hLow`, `body = |Close[0] − hOpen|`, `clvBull = (Close[0] − hLow)/R`,
`clvBear = (hHigh − Close[0])/R`, `hod` = hour-of-day bucket (rule 2.2).

**Baselines** (per hour-of-day, so 9am candles compete only with 9am candles):
- `rangeEma[hod]` and `volEma[hod]`: EMAs with alpha = 2/(SeasLenDays+1), updated once per
  completed hour **after** gate evaluation (never let the candle qualify against itself).
- Fallback while `rangeEma[hod]` is unseeded: average of the last 14 completed hourly ranges
  (any hour), requiring at least 5 samples. If neither exists → no qualification (warmup).
- Store baselines in `Dictionary<int,double>`; keep the rolling-14 ranges in a `Queue<double>`.

**Gates — ALL must pass:**
1. `QualStartHour ≤ hod ≤ QualEndHour`
2. `R ≥ max(KRange × expectedRange, MinAbsPts)`
3. `KVol == 0` OR volEma unseeded OR `hVol ≥ KVol × volEma[hod]`
4. `body / R ≥ MinBody`
5. Bull: `Close[0] > hOpen && clvBull ≥ MinCLV`; Bear: `Close[0] < hOpen && clvBear ≥ MinCLV`
   (bear grids only tradeable if EnableShorts)
6. `hBarCount ≥ 12` (skips holiday/early-close partial hours)

**On qualification:** direction `dir = +1` (bull) / `−1` (bear); `gridBase` = hLow (bull) or hHigh
(bear); `gridR = R`. Levels `L[i] = gridBase + dir × gridR × ratio[i]` for ratios
{0, .236, .382, .5, .618, .786, 1.0, 1.618}; midlines `M[i] = (L[i]+L[i+1])/2` for i = 0..6.
Trade window = (hour close, hour close + 60 min]. Reset: state = ACTIVE, pivotIdx = −1,
entriesUsed = 0, fadeArmed/fadeFired = false, checkpointDone = false.
**Roll handling:** if a position is open from the previous grid and the new grid is OPPOSITE
direction → flatten immediately; same direction → flatten too when FlattenAtWindowEnd (v1 default)
— clean per-hour attribution.

## 5 · Trade-window state machine (hour H+1 only)

All comparisons direction-normalized: "beyond X" means `> X` for bull grids, `< X` for bear grids.

**15m CONFIRM** (on 5m bars closing a 15m period inside the window, state ACTIVE or CONFIRMED):
- If 15m close is beyond L[0] AGAINST the grid → state = DEAD; if FlattenOnInvalidation, flatten
  and cancel all. No further entries this window.
- Else scan midlines i = 0..5: if the 15m extreme dipped to/through `M[i]` (bull: m15Low ≤ M[i])
  AND the 15m close is back beyond `M[i]` → candidate. Take the HIGHEST candidate i (grid
  direction), set `pivotIdx = i`, state = CONFIRMED. Later 15m closes may move pivotIdx.

**Setup A — rotation continuation** (on each closed 5m bar; state CONFIRMED;
entriesUsed < MaxEntries; elapsed minutes ≤ CutoffMin; consec-loss and daily-loss guards pass):
- `pivot = M[pivotIdx]`, `bandTop = L[min(pivotIdx+2, 6)]`.
- Trigger: bar pulled back into the band (bull: Low[0] ≤ bandTop), closed beyond pivot
  (bull: Close[0] > pivot), with directional body (bull: Close[0] > Open[0]).
- Stop: `SL = pivot − dir × StopBufTicks × TickSize` if StopAtMidline, else
  `SL = L[pivotIdx] − dir × StopBufTicks × TickSize`. `risk = (Close[0] − SL) × dir`; require > 0.
- T1 = nearest main level L[0..6] beyond Close[0] with distance ≥ MinRR × risk (skip entry if none).
  T2 = next main level beyond T1; if T1 == L[6] then T2 = (L[6]+L[7])/2 (the 1.309 extension midline).
- **Order recipe (two-leg pattern):** signal names `RF_A_{n}` and `RF_B_{n}` (n = entriesUsed).
  Before entering, call `SetStopLoss(signal, CalculationMode.Price, SL, false)` and
  `SetProfitTarget(signal, CalculationMode.Price, T1 or T2)` for each leg, then
  `EnterLong(Contracts, "RF_A_n")` + `EnterLong(Contracts, "RF_B_n")` (or short equivalents).
  In `OnExecutionUpdate`: when leg A's profit target fills, move leg B's stop to leg B's average
  fill price (breakeven): `SetStopLoss("RF_B_n", CalculationMode.Price, fillB, false)`.
- entriesUsed++ on submission.

**Setup B — extension fade** (once per grid, only if EnableFade; independent of CONFIRMED state;
state ≠ DEAD):
- Arm when the bar's extreme in grid direction reaches `(L[6]+L[7])/2`; track the furthest
  excursion `extExtreme`.
- Fire when armed and a 5m bar closes back INSIDE (bull grid: Close[0] < L[6]): enter COUNTER to
  the grid, single leg, `Contracts` qty, signal `RF_FADE`. SL = extExtreme + dir × StopBufTicks ×
  TickSize; profit target = L[5] (78.6); after L[5] fills nothing remains (one leg). Fade respects
  CutoffMin and the loss guards.

**Minute-45 checkpoint:** first closed bar with elapsed ≥ 45 min: if CheckpointBE, move every open
position's stop to its average fill price. (This encodes the observed :45 "changeover".)

**Window end** (first bar with ET close time > window end): if FlattenAtWindowEnd, flatten all,
cancel all orders, state = EXPIRED.

## 6 · Risk guards

Track realized PnL per day and consecutive losing legs (a breakeven leg resets the streak —
treat PnL ≥ 0 as reset). If losses ≥ MaxConsecLosses or day realized loss ≥ DailyLossLimit:
no new entries for the rest of the session (existing positions manage to completion).

## 7 · Compile checklist before you report Phase 1 done

- Compiles clean under NT8; no `AddDataSeries`; all prices tick-rounded; all `Set*` calls precede
  their `Enter*` calls; every signal name unique per window; accumulators reset on session breaks;
  no decision reads the forming bar. State the .cs is ready and list every parameter with defaults.

---

## PHASE 2 — Backtest & optimization protocol

## 8 · Backtest (Strategy Analyzer)

- Instrument: **MES ##-##** (Micro E-mini S&P 500 continuous, merge-back-adjusted). Data series:
  5 minute. Session: CME US Index Futures ETH. Period: the most recent **12 months**.
- **Order fill resolution: High, 1-tick granularity** (mandatory — stops/targets live inside 5m bars;
  standard resolution will fabricate optimistic fills).
- Commission: $0.62 per side per contract (MES). Slippage: 1 tick per market fill.
- Expect ZERO trades in roughly the first 2 weeks (baseline warmup) — that is correct behavior.
- Report with defaults BEFORE any optimization: Net profit, Profit Factor, Win rate, Avg winner /
  avg loser (avg RR), Max drawdown, Sharpe, Total trades, Avg trade net $, Max consecutive losses,
  trades/week — plus breakdowns by direction (long/short) and by qualifying hour-of-day.
  Export the trade list to CSV.

## 9 · Optimization & validation (do NOT skip the discipline here)

1. **One-at-a-time sensitivity** around defaults using the ranges in the parameter table. Objective:
   maximize NetProfit / MaxDrawdown, minimum 80 trades. For each parameter report the full curve,
   not just the best point — we accept a value only if its ±1-step neighbors are also profitable
   (plateau rule). Never jointly grid-search more than 2 parameters.
2. Freeze the plateau values, then **walk-forward**: optimization period 120 days, test period
   30 days, rolling across the 12 months. Optimize only KRange and MinRR inside the walk-forward.
3. **Acceptance criteria** (all must hold on out-of-sample):
   - OOS Profit Factor ≥ 1.3 AND ≥ 70% of in-sample PF
   - Avg trade net ≥ $5 per contract (after the commission/slippage above)
   - Win rate × avg RR sane (no <30% WR unless avg RR > 2.5)
   - Max consecutive losses ≤ 8; equity curve not dependent on a single week
4. If acceptance fails, report WHICH gate/setup degrades (direction? hour-of-day? Setup B?) rather
   than tuning further — hand the diagnosis back, do not overfit.
5. Deliverables: final .cs, default-run report, sensitivity curves, walk-forward summary table
   (per-window IS vs OOS), chosen parameter set with plateau evidence, and the OOS trade-list CSV.

## 10 · v1.1 amendments (from manual replay — implement ALL of these in Phase 1)

1. **Winsorized baselines**: when updating `rangeEma[hod]` / `volEma[hod]`, cap the incoming
   hour's value at `CapOutlier` (double, default 2.0, 0 = off) × the current EMA before blending,
   so a crash hour cannot inflate the gate and blind the system for days.
2. **QualEndHour default = 14** (last tradeable window 15:00–16:00 ET).
3. **Setup C — Acceptance Continuation** (`EnableAccel`, bool, default true, optimizer: both):
   inside the trade window, if a completed 15m bar holds beyond L[4] (61.8) for its entire range
   and closes beyond L[5] (78.6) without producing a midline-pivot confirm, arm Setup C. Then a
   closed 5m bar beyond L[6] (100) with a directional body enters WITH the grid: stop = M[5]
   (78.6/100 midline) ∓ StopBufTicks; T1 = (L[6]+L[7])/2 (the 1.309R extension midline);
   T2 = L[7] (161.8). Two-leg pattern as Setup A, one Setup-C entry per grid, counts toward
   MaxEntries, requires MinRR to T1 and respects CutoffMin. This captures no-pullback trend hours.
4. **Fade guards**: Setup B fires only if (a) the excursion extreme stayed inside L[7] —
   `FadeSkipBeyondExt` bool default true — and (b) reward to L[5] ≥ `FadeMinRR` (double, default
   1.0) × stop distance. Both as parameters.
5. **One entry per pivot**: `OnePerPivot` bool default true — after a Setup-A entry, no further
   Setup-A entries until a NEW 15m confirm sets a different (or re-set) pivot.
6. **Exit-mode experiment** — replace the single FlattenAtWindowEnd with
   `WindowExitMode` enum-style int parameter (optimize 0/1/2):
   0 = flatten at window end (baseline); 1 = hold past window end with the stop trailed to the
   last confirmed midline, exit on stop/target only; 2 = hold only while consecutive
   same-direction grids keep rolling, flatten when a window ends without a roll.
   Report the full metric table for each mode separately — this is a primary experiment,
   not a tuning afterthought.
