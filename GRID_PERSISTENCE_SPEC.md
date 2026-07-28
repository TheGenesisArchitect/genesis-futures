# Rolling Fib — CHoCH/BOS + Grid Persistence Spec  v1.0 (2026-07-07)

> **STATUS: BUILT 2026-07-08 (phases 1–5), shipped DORMANT behind the `Grid persistence (CHoCH/BOS + fade)` input, default OFF.**
> Indicator: fractal swings (L=2) → `structureDir` + CHoCH/BOS (STRUCTURE events, non-repaint), `reversalType` on qualify (rides the
> QUALIFIED payload), persistence state machine (WINDOW→PERSIST, invalidation: acceptance/origin-break/opposing-CHoCH/time-decay/max-age →
> INVALIDATE events), geometry-aware fade trigger (FADE events + chart arrows). State adds reversalType/gridPhase/structureDir/lastStructure/
> gridAgeHrs/fadeArmed. Server parses all of it, computes each grid's LIVE SPAN (qualify→invalidate, or →next-qualify on a ROLL) so PERSIST
> fade-trades count in-system. Dashboard: rolling/PERSIST + CHoCH↺/BOS→ + fade-armed badges on the radar card; reversalType + fades/structure
> on the score card. Aries persona learns the AMD cycle + CHoCH/BOS + persistence.
> **SCOPE CUTS (validation build):** Wyckoff rejection-wick filter dropped; premium/discount gate simplified to "swept the 100 = beyond
> equilibrium by construction" (so the fade fires on more sweeps than §5's strictest form). **Phase 6 (backtest validation) NOT done — the
> ON-mode in-system/off-system numbers are provisional until it clears out-of-sample.** Activate: F5 compile → remove+re-add the indicator →
> set the input TRUE. Draws + signals only, no orders.

> Closes the gap between windows. Captures the full cycle: **Expansion → Consolidation → Manipulation → Expansion.**
> Grounded in: Wyckoff (spring/upthrust), Steidlmayer/Dalton (balance/imbalance, IB), ICT/SMC (CHoCH/BOS/liquidity),
> LuxAlgo (objective swing-fractal detection), Aronson/Chan (validate before trust).

---

## 1 · Swing structure (the foundation) — objective, not eyeballed

**Fractal swing points** on the execution series (5m):
- **Swing High (SH)** at bar *i*: `High[i]` is the strict max of `High[i-L .. i+L]`.
- **Swing Low (SL)** at bar *i*: `Low[i]` is the strict min of `Low[i-L .. i+L]`.
- **L = 2** (a 5-bar fractal). Confirmed *L* bars after the pivot (so a swing is only "known" 2 bars late — deterministic, no repaint).
- Maintain `lastSH`, `lastSL`, and the one prior of each (`prevSH`, `prevSL`).

**Structure direction** `structureDir ∈ {+1, −1, 0}`:
- `+1` bullish: `lastSH > prevSH` **and** `lastSL > prevSL` (higher highs + higher lows).
- `−1` bearish: `lastSH < prevSH` **and** `lastSL < prevSL` (lower highs + lower lows).
- `0` until enough swings exist.

## 2 · CHoCH vs BOS — the reversal/continuation call

Evaluated on **5m close**:
- **Bullish BOS** (continuation): `structureDir == +1` **and** a candle **closes above `lastSH`**.
- **Bearish BOS**: `structureDir == −1` **and** close **below `lastSL`**.
- **Bullish CHoCH** (reversal): `structureDir == −1` (making LH/LL) **and** close **above `lastSH`** → first break of the down-structure. `structureDir` flips to `+1`.
- **Bearish CHoCH**: `structureDir == +1` **and** close **below `lastSL`** → flip to `−1`.

Emits a `STRUCTURE` event `{kind: CHoCH|BOS, dir, level, t}`.

## 3 · Qualifier tagging (rides on every grid)

When a **1H candle qualifies**, classify the qualifying move vs. the structure that existed *before* it:
- Produced a **CHoCH** against the prior swing trend → **`REVERSAL`** grid.
- Produced a **BOS** with the trend → **`CONTINUATION`** grid.
- Neither clean → **`NEUTRAL`**.

`reversalType` is written to state and drives Aries's "read": a REVERSAL grid expects the **fade** (the manipulation off the 100); a CONTINUATION grid trades **with** the impulse.

## 4 · Grid persistence — the state machine (the core fix)

Today the grid dies at the end of the trade window. New model — a grid is **live** from qualification until an explicit invalidation:

```
IDLE ──qualify──▶ WINDOW ──hour ends, not invalidated──▶ PERSIST ──invalidation──▶ IDLE
  ▲                  │                                       │
  └────── ROLL (a new hour qualifies replaces the grid) ─────┘
```

- **WINDOW** — the hour immediately after the qualifier (today's behavior; primary A/retest window).
- **PERSIST** — grid stays the active map across non-qualifying hours; the system keeps hunting the **fade** + **midline retests**.

**Invalidation (grid dies) — any one of:**
1. **ROLL** — a new 1H candle qualifies → new grid replaces the old.
2. **ACCEPTANCE** — **2 consecutive 5m closes beyond the 161.8** extension in the grid's direction → move complete.
3. **ORIGIN BREAK** — a decisive **close beyond the 0** against the grid (bear grid: close above the 0; bull: below) → structure failed.
4. **OPPOSING CHoCH** — a CHoCH against the grid's direction → the reversal reversed.
5. **TIME DECAY** — **K = 3 hours** with **no touch of any grid level** → stale, expire.

## 5 · The fade trigger (the manipulation) — precise

Active in **WINDOW and PERSIST**. This is the sweep-and-reject:
- **Bearish fade (short):** a 5m bar whose **High exceeds the 100 or 161.8 (or a tracked SH = external liquidity)** by ≥ 1 tick, then **closes back below** that level. Fire only from **premium** (prior close was above the 50 / equilibrium).
- **Bullish fade (long):** symmetric at the 0-side / discount.
- **Confluence bonus (A+):** the swept level coincides with the **prior-session or prior-day high/low** → flag `FADE_A+`.
- Emits a `FADE` event `{dir, level, sweptLiquidity, t}`.

Wyckoff check (effort/result): the sweep bar ideally shows a **long rejection wick** relative to body (upthrust/spring signature).

## 6 · Setup mapping (no new philosophy, just extended reach)
- **Setup A (retest)** — the WINDOW confirm at a midline. Unchanged.
- **Setup B (reversion fade)** — the sweep-and-reject at the 100/extension. **Now allowed to fire in PERSIST**, not just the window. This is the captured gap.
- CONTINUATION grids favor **BOS-aligned** entries; REVERSAL grids favor **the fade**.

## 7 · What changes in code
**`RollingFibRadar.cs`:** fractal swing tracking; `structureDir` + CHoCH/BOS each bar (→ STRUCTURE events); `reversalType` on qualify; persistence state (`gridPhase: WINDOW|PERSIST`, `gridAgeHrs`, keep grid live until invalidation); the fade trigger (→ FADE events); invalidation logic (→ INVALIDATE event + reason).
**State JSON (new fields):** `reversalType` (REVERSAL|CONTINUATION|NEUTRAL), `gridPhase`, `gridAgeHrs`, `structureDir`, `fadeArmed`, `lastStructure` (CHoCH/BOS).
**Dashboard:** grid card/terminal shows phase (`live · rolling`), a **CHoCH↺ / BOS→ badge**, and a fade-armed marker; the spotlight "read" uses `reversalType`; cycle labels (Expansion/Consolidation/Manipulation).
**Aries:** persona learns the AMD cycle + CHoCH/BOS + persistence so her coaching and Vault citations speak it.

## 8 · Validation before trust (Aronson/Chan — non-negotiable)
- Everything ships behind a **`GridPersistence` input, default OFF**.
- **Backtest** the persistence-fade vs. the window-only baseline on **unseen data** (Market Replay weeks, then the Pine/NT strategy). Metrics: expectancy, PF, win%, avg R, and specifically **does Setup B in PERSIST add positive expectancy** or just noise?
- Only flip the default ON after it clears out-of-sample. No live trust on narrative alone.

## 9 · Parameters (defaults — flag any to change)
| Param | Default | Note |
|---|---|---|
| Swing fractal `L` | 2 | 5-bar fractal on 5m |
| Time-decay `K` | 3 hrs | no-touch expiry |
| Acceptance | 2 closes beyond 161.8 | move-complete |
| Sweep threshold | ≥1 tick beyond + close back inside | the reject |
| Premium/discount gate | the 50 (equilibrium) | fade side filter |
| Max PERSIST age | 4 hrs (then require re-touch) | safety cap |

---
*Build order once approved: (1) swing + CHoCH/BOS detection & events; (2) reversalType tag + Aries read; (3) persistence state machine + invalidation; (4) fade trigger + Setup B in PERSIST; (5) dashboard phase/badges; (6) validate behind the toggle.*
