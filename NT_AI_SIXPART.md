# NinjaTrader AI — Six-Section Submission (Rolling Fib core)

*Answer their intake question with "all six in a single message" and paste the block below.
If it chokes, feed the numbered sections one at a time — each stands alone.
After the base compiles and backtests, submit the three follow-up prompts at the bottom, one at a time.*

---

```text
Instrument and timeframe: MES (Micro E-mini S&P 500), primary chart 5-minute. All times US/Eastern.
All hourly and 15-minute values are computed from COMPLETED 5-minute bars (no higher-timeframe series needed):
"the hour candle" = the last 12 completed 5m bars of a clock hour; "a 15m bar" = the last 3 completed 5m bars
ending at :15, :30, :45 or :00. Everything below evaluates on bar close only.

1) Direction
- Direction: both (long after a bullish qualifying hour, short after a bearish one; rules below are written
  for longs — shorts are the exact mirror).

2) Entry
- Entry description: Three stages, all exact.
  STAGE 1 — QUALIFY (checked once per hour, on the 5m bar that closes exactly on the hour):
  Let O,H,L,C,V be that clock hour's open/high/low/close/volume and R = H − L. The hour QUALIFIES BULL when
  ALL are true: (a) hour-of-day of the candle is 7 to 14 inclusive; (b) R >= RangeMult * SMA(RangeAvgPeriod)
  of the prior completed hourly ranges AND R >= MinHeightPts; (c) V >= VolMult * SMA(RangeAvgPeriod) of prior
  hourly volumes (skip this check if VolMult = 0); (d) (C − O) / R >= MinBodyFrac and C > O;
  (e) (C − L) / R >= MinCLV. Bear mirror: C < O, (O − C)/R >= MinBodyFrac, (H − C)/R >= MinCLV.
  STAGE 2 — GRID: on qualification, compute price levels from the hour candle:
  L[i] = hourLow + R * ratio[i] for bull (hourHigh − R * ratio[i] for bear), ratios = 0, 0.236, 0.382, 0.5,
  0.618, 0.786, 1.0, 1.618. Midlines M[i] = (L[i] + L[i+1]) / 2 for i = 0..6. The TRADE WINDOW is the next
  60 minutes only. If a new hour qualifies, the grid and window are replaced (flatten first if the new grid
  is opposite direction).
  STAGE 3 — CONFIRM then TRIGGER (longs, inside the trade window only):
  CONFIRM (on 5m bars closing at :15/:30/:45/:00): if the just-completed 15m bar's low <= M[i] and its close
  > M[i] for any i in 0..5, set pivot = the highest such i. A later 15m bar may reset the pivot.
  TRIGGER (any completed 5m bar after a pivot exists): enter LONG at market when ALL are true:
  (a) bar low <= L[min(pivot+2, 6)] (price pulled back into the entry band);
  (b) bar close > M[pivot]; (c) bar close > bar open; (d) 40 minutes or less have elapsed in the window;
  (e) fewer than MaxEntries entries this window; (f) no entry has been taken on this pivot yet
  (a new 15m CONFIRM re-arms).
- Indicators used: SMA of completed hourly ranges, period RangeAvgPeriod = 14; SMA of completed hourly
  volumes, period RangeAvgPeriod = 14. Tunable parameters: RangeMult = 1.25; MinHeightPts = 8.0 (points);
  VolMult = 1.10 (0 = off); MinBodyFrac = 0.45; MinCLV = 0.60; QualStartHour = 7; QualEndHour = 14;
  CutoffMin = 40; MaxEntries = 3; StopBufTicks = 2.
- Thresholds/timing: all comparisons exactly as written above; all signals on bar close (never intra-bar).

3) Exit (rule-based, separate from stop/target orders)
- Exit description: (a) flatten all positions and cancel all orders when the 60-minute trade window ends;
  (b) flatten immediately if a completed 15m bar closes below L[0] while long (above L[0] while short) —
  grid invalidation; (c) 45 minutes into the window, move every open position's stop to its entry price
  (breakeven); (d) flatten immediately if a new qualifying hour creates an opposite-direction grid.
- Indicators used: none beyond the grid levels defined in section 2.
- Thresholds/timing: bar close for (b); clock-based for (a), (c); qualification event for (d).

4) Stop loss
- Stop loss: dynamic price-based, known at entry time: stop-market at M[pivot] − StopBufTicks * TickSize
  for longs (M[pivot] + buffer for shorts) — use CalculationMode.Price. If the framework strictly requires
  a fixed tick distance instead, use 10 ticks.

5) Profit target
- Profit target: two equal legs. Leg 1 limit at the nearest main level L[i] (i = 0..6) above entry whose
  distance is >= 1.2 * (entry − stop); skip the trade if none exists. Leg 2 limit at the next main level
  above Leg 1 (if Leg 1 is L[6], use (L[6] + L[7]) / 2). When Leg 1 fills, move Leg 2's stop to Leg 2's
  entry fill price. If fixed ticks are strictly required instead: Leg 1 = 12 ticks, Leg 2 = 20 ticks.

6) Position sizing
- Quantity: 2 contracts (1 per target leg).

Optional extras:
- Time filters: qualification hours 07:00–14:59 ET only (so the last window is 15:00–16:00); no entries
  after minute 40 of any window; flatten everything by 16:00 ET.
- Trailing/breakeven: breakeven move on Leg-1 fill (section 5) and at minute 45 of the window (section 3c).
- Daily caps: stop trading for the day after 3 consecutive losing legs (a scratch/breakeven leg resets the
  count) or after a realized daily loss of $300. Max 3 entries per trade window.
```

---

## Follow-up prompts (submit AFTER the base compiles and backtests, one at a time, in order)

**Follow-up 1 — Setup C (acceptance continuation, the trend-hour fix):**
> Add an additional entry called Setup C: inside the trade window, if a completed 15m bar's low stays >= L[4]
> and it closes > L[5] without triggering a CONFIRM, arm Setup C. Then any completed 5m bar closing above L[6]
> with close > open enters LONG (2 contracts, two legs): stop at (L[5]+L[6])/2 − StopBufTicks ticks,
> Leg 1 target (L[6]+L[7])/2, Leg 2 target L[7], breakeven on Leg-1 fill. Require Leg-1 distance >= 1.2 × stop
> distance. Once per window, counts toward MaxEntries, respects the 40-minute cutoff. Shorts are the mirror.

**Follow-up 2 — Setup B (extension fade, with v1.1 guards):**
> Add a counter-trend entry called Setup B: track the highest high after any bar touches (L[6]+L[7])/2 in the
> trade window. If that extreme stays BELOW L[7] and a completed 5m bar closes back below L[6], enter SHORT
> 1 contract: stop at extreme + StopBufTicks ticks, target L[5]. Skip unless (entry − L[5]) >= 1.0 × (stop −
> entry). Once per window. Mirror for bear grids (fade long).

**Follow-up 3 — Exit-mode experiment:**
> Replace the flatten-at-window-end rule with an int parameter WindowExitMode: 0 = flatten at window end
> (current behavior); 1 = do not flatten — trail the stop to the most recent confirmed midline and exit on
> stop or target only; 2 = flatten at window end UNLESS a new same-direction grid qualified, in which case
> keep holding under mode-1 management. Backtest all three and report the full metric table per mode.

## Notes on the translation (for us, not for the NT agent)
- The hour-of-day seasonal baseline was simplified to SMA(14) of hourly ranges (their intake can't express
  per-hour dictionaries). On ETH data this makes RTH hours qualify slightly more easily — acceptable for the
  first backtest; revisit if trade count runs hot.
- The winsorized-baseline (outlier cap) and one-per-pivot rules from radar v1.1 ARE included (2f, and the
  SMA is naturally less contaminated than the EMA; add capping later if crash weeks distort results).
- If the NT AI still can't build this, fallback: Claude writes the complete RollingFib.cs NinjaScript
  directly for import via NinjaScript Editor — no AI middleman.
