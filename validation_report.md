# RollingFib — Backtest Validation Report
*Data: NinjaTrader Trades grid, ES 09-26 fills, 2026-01-05 → 2026-07-02 (126 trading days)*
*704 legs = 355 entry decisions ("pairs"). Costing: MES view = $1.24 RT commission + 1 tick slippage per leg; ES view = $4.10 + 1 tick.*

## Verdict: REJECT (this build) — but the build is not the strategy

| Gate | Target | Result | Pass? |
|---|---|---|---|
| Sample size | ≥ 100 | 355 pairs | ✅ |
| Gross profit factor | — | **1.033** (≈ coin flip before costs) | ⚠️ |
| Net PF (MES costs) | ≥ 1.10 | **0.719** | ❌ |
| Expectancy/trade net | > 0 | **−$4.50** | ❌ |
| Walk-forward OOS PF | > 1.0 | 0.702 (IS 0.726 — consistent, not overfit; consistently unprofitable) | ❌ |
| Monte Carlo p95 max DD | < 15–20% | $1,940 (19.4% of $10k) while LOSING | ❌ |
| Recovery factor | > 1.5 | −0.93 | ❌ |

Net −$1,599 (MES) / −$10,149 (ES) over 6 months. Max 6 consecutive losing pairs; MC p99 streak 16.

## Why it failed — three separable causes

**1. The build is missing half the management logic it confirmed.**
Every one of the 704 legs exited at "Profit target" or "Stop loss" — zero window-end flattens,
zero 16:00 flattens, zero invalidation exits. Trades held up to ~60 min from entry regardless of
window state. Breakeven moves appear only partially (58 near-$0 stops). 10 entries violated the
minute-40 cutoff. **This backtest tested a fixed-bracket approximation, not the Rolling Fib.**

**2. Fixed tick brackets destroyed the geometry.**
10-tick stop / 12- and 20-tick targets on every trade, regardless of grid size, gives
avgWin/avgLoss = 0.92 at a 43.9% win rate — negative geometry even before costs. The strategy's
edge lives in structural placement (stop just past the pivot, targets AT grid levels); the
approximation threw that away.

**3. Qualification became unselective.**
2.8 entries/day, evenly spread across all hours 07–14, all months, both directions — no pocket of
edge anywhere. The SMA(14-hour) baseline includes quiet overnight hours, so nearly every RTH hour
"qualifies" on ES. The filter that IS the thesis (abnormal hour vs that hour's own normal) was
diluted to a participation trophy. Supporting evidence: losers' average MFE was only 3.1 ticks —
entries died without ever progressing, the signature of entering noise, not confirmed pivots.

## What the test DID establish (useful)

- **Cost bar**: MES costs ≈ $4.93/pair (2 legs × commission + slip). The system needs ≥ ~4 ticks/pair
  of true edge to clear it. ES costs are proportionally lighter per dollar ($8.20 + $25 slip on 10× P&L).
- **Stop width is not the problem**: winners' avg MAE = 3.1 ticks; only 26/261 winners saw MAE ≥ 8 ticks.
  A structural 6–9 tick stop keeps nearly all winners. Don't widen stops — improve entry selection.
- **No regime dependence**: IS ≈ OOS, months uniform. The failure is structural, not a bad quarter —
  which means fixing the build should show up immediately in any period.

## Fix plan (in order)

1. **Full-fidelity build** — stop approximating. Hand-written RollingFib.cs (NinjaScript C#) with:
   price-based stops/targets from the grid (CalculationMode.Price), window-end + 16:00 + invalidation
   flattens, complete breakeven logic, cutoff enforcement, daily caps.
2. **Restore selectivity** — baseline from session hours only (07–15 ET) or per-hour-of-day averages
   (as radar v1.1); target ≤ ~1 qualifying window/day. Raise RangeMult toward 1.4 if still loose.
3. **Re-test same period** with identical costing. Expect: far fewer trades (~80–150 pairs), higher
   avgW/avgL from structural targets. Then judge against the gates; then Setup C follow-up.

*Verdict on the EDGE: unproven, not disproven. The screenshot replays showed structure this test
never implemented. Next test must be full fidelity or it will reject the approximation again.*
