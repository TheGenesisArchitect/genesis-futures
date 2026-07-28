// ═══════════════════════════════════════════════════════════════════════════════
//  ROLLING FIB — full-fidelity NinjaTrader 8 strategy  ★ v4.1 ★
//
//  v4.1: CONFIRM BUBBLE now color-coded — GOLD = tradeable (pivot <= MaxPivotIdx, Setup A acts),
//        GRAY = too deep to trade (orphaned since Setup C retired). Explains "bubble but no
//        trade": confirms above the 50/61.8 midline have no setup. (Gap to fill = a reversion
//        entry for deep pivots; or EntryTiming=2 for trend confirms that never pull back.)
//  v4.0: TRIGGER-WINDOW SHADING (:25 & :45) drawn on the chart — the qualification-first
//        foundation made fully visible: 10pt gate → grid + midlines + ADR frame + window
//        shading + the two trigger bands where entries fire. (Grid size sets trade character:
//        small grid = scalp, big grid = swing; stops/targets scale structurally.)
//  v3.9: MIN-HOLD BARS (default 3) — the heat cut can't fire until the trade has had N bars
//        to work; the bracket TP/stop still fires anytime (the "or TP/stop"). NOTE: this
//        RELAXES the v3.7 fast-cut and is in tension with it — A/B MinHoldBars 0 vs 3.
//  v3.8: ENTRY TIMING is now a 3-way A/B (EntryTiming 0/1/2): 0 touch-limit · 1 close-confirm
//        (rejection, best for RANGE) · 2 open-on-confirm (market, rides TREND continuation).
//        Lets the data settle the operator's open-vs-close question across the whole year.
//  v3.7: ENTRY ON CLOSE-CONFIRM (default) — enter at the close of a bar that dipped to the
//        shelf and closed back beyond it (rejection held), NOT on the first touch. Data:
//        68% of losers were touch-fill knife-catches (-$1875). Plus a hard HEAT CUT: flatten
//        any trade past MaxHeatPts (default 10pt/$50) — the operator's "wrong entry" threshold.
//  v3.6: RUNNER RETIRED (data-driven). The runner-to-far-T2 leg lost -$439 at 14% WR =
//        82% of all losses — reversion moves are SHORT, a distant target rarely fills.
//        Default now banks BOTH legs at T1 (HoldRunner off). Grid-persistence toggle added.
//  v3.5: REVERSION-OSCILLATOR reframe (data-driven). Setup C (continuation chaser)
//        RETIRED — it chased the exhaustion zone the wrong way (49% went against it,
//        91% of all losses). Setup B reforged as the REVERSION FADE at the 100 (right
//        time, right direction). Setup A (midline retest, directionally correct) kept.
//        The grid is now a mean-reversion oscillator, not a breakout system.
//  v3.4: FIXED absolute gate default (MES 10 pts — no adaptive boom-bust droughts) +
//        faint prior-day ADR frame (H/Mid/L) behind the grid for range context.
//  v3.3: :25 and :45 are INDEPENDENT opportunities — entering the :45 window while flat
//        re-arms the setup even on the same pivot (fixes every missed :45 after a :25
//        fill/stop). Wrap-aware session (QualStart>QualEnd spans midnight = evening Globex).
//  v3.2: TRIGGER WINDOWS — limits only live :20–:30 & :40–:50 (the operator's :25/:45
//        cadence); budgets consume on FILL, not placement (cancelled tickets are free).
//  v3.1: VWAP direction gate (aligned hours qualify at relaxed height), wide stops
//        (full level below pivot), breakeven at 1R, cutoff :40 → :50.
//  v3.0: PASSIVE ENTRIES — resting limits AT structure (Setup A at the shelf beyond
//        the pivot, Setup C at the broken 100), replacing the market-on-momentum-close
//        trigger falsified by three backtests (67/77 losers = 1-2 bar stop-outs).
//  Plus: session-median range gate, winsorized + decay-boosted baselines, C niche
//  gate, live state feed for the dashboard, full chart drawing.
//
//  Replaces the AI-built fixed-bracket approximation. Implements the complete spec:
//   · 1H qualification with per-hour-of-day baselines (winsorized, session-scoped fallback)
//   · Fib grid + midlines from the qualifying candle; 60-minute trade window
//   · 15m midline confirm → 5m Setup A rotation entries (price-based stops/targets)
//   · Setup C acceptance-continuation (trend hours) · Setup B extension fade (guarded)
//   · Window-end / invalidation / opposite-roll / 16:00 flattens · :45 checkpoint BE
//   · Breakeven on Leg-1 target fill · one entry per pivot · daily loss/streak halts
//   · WindowExitMode experiment: 0 flatten at end · 1 hold on brackets · 2 hold on same-dir roll
//
//  IMPORT: NinjaTrader → New → NinjaScript Editor → right-click Strategies → Import,
//  or paste into a new Strategy named RollingFib and compile (F5).
//  RUN: chart/analyzer on MES (or ES) 5-MINUTE bars, CME US Index Futures ETH template.
//  Strategy Analyzer: Order fill resolution = High (1-tick), commission template set,
//  1 tick slippage. First ~2 weeks of any test = baseline warmup (no trades = correct).
//  Times are US Eastern (converted from the PC clock via TimeZoneInfo).
// ═══════════════════════════════════════════════════════════════════════════════
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class RollingFib : Strategy
    {
        // ── grid / window state ──
        private static readonly double[] Ratios = { 0.0, 0.236, 0.382, 0.5, 0.618, 0.786, 1.0, 1.618 };
        private double[] L = new double[8];          // main levels (price)
        private double[] M = new double[7];          // midlines (price)
        private int      dir;                        // +1 bull grid, -1 bear grid
        private bool     windowActive;
        private DateTime qualCloseEt, windowEndEt;
        private DateTime hourStartLoc, qualCloseLoc, windowEndLoc;   // chart-time anchors for drawing
        private double   gridR;
        private string   lastBlock = "warmup", lastQualInfo = "none yet";
        private int      pivotIdx = -1;
        private bool     pivotUsed, accelArmed, accelUsed, fadeUsed, gridDead, ckptDone;
        private int      confirmBar = -1;            // bar that set the pivot — no same-bar confirm+trigger
        private int      entriesThisWindow;
        private int      entrySeq;                   // global unique-signal counter

        // ── 1H / 15m aggregation (from completed 5m bars) ──
        private DateTime hourKey = DateTime.MinValue, m15Key = DateTime.MinValue;
        private double   hO, hH, hL, hV; private int hBars;
        private double   m15H, m15L;

        // ── baselines: per-hour-of-day EMAs + session-hour fallback queue ──
        private readonly Dictionary<int, double> rngEma = new Dictionary<int, double>();
        private readonly Dictionary<int, double> volEma = new Dictionary<int, double>();
        private readonly Queue<double> recentRanges = new Queue<double>();

        // ── legs, risk guards ──
        private class Leg { public double Fill = double.NaN; public double Stop; public double Risk0; public bool IsLong; public bool Open; }
        private double vwapPV, vwapVol;                                    // session VWAP accumulators
        private int    prevTrigWin;                                        // last bar's trigger-window id (:25/:45 independence)
        private double curSessH, curSessL = double.NaN, prevSessH = double.NaN, prevSessL = double.NaN;   // prior-day ADR frame
        private Brush  adrBrush;
        private string prevGridTag;                                        // for KeepHistory=off decluttering
        private int    entryBar = -1;                                      // bar the current position filled (min-hold clock)
        private readonly Dictionary<string, Leg> legs = new Dictionary<string, Leg>();
        private readonly List<Order> workingEntries = new List<Order>();   // resting limit entries at structure
        private bool brokeBeyond;                                          // price broke the 100 this window
        private int    lastTradeCount, consecLosses;
        private double dayPnL;
        private bool   haltDay, eodFlattened;
        private TimeZoneInfo tzEt;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                                     = "RollingFib";
                Description                              = "Rolling Fib v4.0 — trigger-window shading + full foundation visuals, 10pt qualify gate, reversion oscillator, 3-way entry timing";
                Calculate                                = Calculate.OnBarClose;
                EntriesPerDirection                      = 10;
                EntryHandling                            = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy             = true;
                ExitOnSessionCloseSeconds                = 30;
                BarsRequiredToTrade                      = 300;
                IsInstantiatedOnEachOptimizationIteration = true;

                RangeMult        = 1.25;  MinHeightPts = 8.0;  VolMult = 1.10;
                MinBodyFrac      = 0.30;  MinCLV       = 0.60; SeasLenDays = 10; CapOutlier = 2.0;   // 0.30: CLV carries direction; body just rejects true dojis
                QualStartHour    = 7;     QualEndHour  = 14;
                CutoffMin        = 50;    MaxEntries   = 3;    StopBufTicks = 2; MinRR = 1.2;
                WideStops        = true;  BreakEvenAt1R = true; VwapFilter = true; UseTrigWin = true; HoldRunner = false;
                EntryTiming      = 1;     MaxHeatPts = 10.0;   // 0=touch 1=close-confirm 2=open-on-confirm; hard cut 10pt
                MinHoldBars      = 3;     // give the trade N bars before the heat cut can fire (bracket stop still active)
                EnableShorts     = true;  EnableAccel  = false; EnableFade = true;   // C (chaser) retired — proven wrong-direction
                FadeMinRR        = 1.0;   FadeSkipBeyondExt = true; OnePerPivot = true;
                RetestFrac       = 0.08;  MaxRiskFrac = 0.22; MaxT1RR = 2.5; MaxPivotIdx = 3;
                MaxOvershoot     = 0.12;
                DecayBoost       = 2.0;   UseSessionMedian = true; FixedGate = true;   MinHeightPts = 10.0;
                CheckpointBE     = true;  WindowExitMode = 0;  ContractsPerLeg = 1;
                MaxConsecLosses  = 3;     DailyLossLimit = 300; FlattenHour = 16;
                DebugPrints      = false;
                ShowChart        = true;  EmitState = true; ShowADR = true; KeepHistory = true; ShowTrigWin = true;
                StatePath        = @"C:\Users\mrbee\Documents\RollingFib\live";
            }
            else if (State == State.DataLoaded)
            {
                tzEt = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                if (EmitState) try { Directory.CreateDirectory(StatePath); } catch { EmitState = false; }
            }
        }

        // ═══════════════ helpers ═══════════════
        private double Tick               => TickSize;
        private double RoundTick(double p) => Instrument.MasterInstrument.RoundToTickSize(p);
        private bool   GtD(double a, double b) => dir == 1 ? a > b : a < b;
        private bool   GeD(double a, double b) => dir == 1 ? a >= b : a <= b;
        private void   Dbg(string s) { if (DebugPrints) Print(Times[0][0] + "  RF: " + s); }

        private double ExpectedRange(int hod)
        {
            // median of recent session hours: regime-proof (crash can't inflate it,
            // holiday weeks pull it down within a day — the EMA lagged both)
            double med = double.NaN;
            if (recentRanges.Count >= 5)
            {
                var s = recentRanges.OrderBy(x => x).ToList();
                med = s[s.Count / 2];
            }
            if (UseSessionMedian) return med;
            if (rngEma.TryGetValue(hod, out double e)) return e;
            return med;
        }

        private void FlattenAll(string reason)
        {
            foreach (var kv in legs.Where(k => k.Value.Open).ToList())
            {
                if (kv.Value.IsLong) ExitLong(ContractsPerLeg, reason, kv.Key);
                else                 ExitShort(ContractsPerLeg, reason, kv.Key);
            }
            Dbg("FLATTEN (" + reason + ")");
        }

        private bool AnyOpen() => legs.Values.Any(l => l.Open);

        private void CancelWorking(string why)
        {
            if (workingEntries.Count == 0) return;
            foreach (var o in workingEntries.ToList())
                if (o != null && (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted))
                    CancelOrder(o);
            workingEntries.Clear();
            Dbg("cancel working limits (" + why + ")");
        }

        // passive two-leg entry: resting limits AT the structure, filled BY the pullback —
        // the market-on-momentum-close entry was falsified by three backtests (67/77 losers
        // stopped within 1-2 bars = chasing 5m impulse tops). The operator's spec was always
        // "entries at the levels"; this restores it.
        private void FirePairLimit(string tag, bool isLong, double px, double sl, double t1, double t2)
        {
            entrySeq++;
            string s1 = tag + entrySeq + "L1", s2 = tag + entrySeq + "L2";
            px = RoundTick(px); sl = RoundTick(sl); t1 = RoundTick(t1); t2 = RoundTick(t2);
            SetStopLoss(s1, CalculationMode.Price, sl, false);
            SetProfitTarget(s1, CalculationMode.Price, t1);
            SetStopLoss(s2, CalculationMode.Price, sl, false);
            SetProfitTarget(s2, CalculationMode.Price, t2);
            double r0 = Math.Abs(px - sl);
            legs[s1] = new Leg { IsLong = isLong, Stop = sl, Risk0 = r0 };
            legs[s2] = new Leg { IsLong = isLong, Stop = sl, Risk0 = r0 };
            Order o1 = isLong ? EnterLongLimit(0, true, ContractsPerLeg, px, s1) : EnterShortLimit(0, true, ContractsPerLeg, px, s1);
            Order o2 = isLong ? EnterLongLimit(0, true, ContractsPerLeg, px, s2) : EnterShortLimit(0, true, ContractsPerLeg, px, s2);
            workingEntries.Add(o1); workingEntries.Add(o2);
            Dbg(tag + " LIMIT " + (isLong ? "LONG" : "SHORT") + " @" + px + " sl=" + sl + " t1=" + t1 + " t2=" + t2);
            if (ShowChart)
                Draw.Text(this, "RFlim" + entrySeq, false, (isLong ? "▽ " : "△ ") + tag + " limit " + px.ToString("F2"),
                    Time[0], px, 0, isLong ? Brushes.Teal : Brushes.Crimson, new SimpleFont("Consolas", 10),
                    TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
            EmitEvent("ARMED", tag + " limit " + (isLong ? "LONG" : "SHORT") + " @ " + px.ToString("F2")
                + " SL " + sl.ToString("F2") + " T1 " + t1.ToString("F2") + " T2 " + t2.ToString("F2"));
        }

        // ── chart rendering (mirrors the Pine radar: grid + midlines + boxes + labels) ──
        private static readonly string[] LvlName = { "0", "23.6", "38.2", "50", "61.8", "78.6", "100", "161.8" };
        private void DrawAdr()
        {
            if (!ShowChart || !ShowADR || double.IsNaN(prevSessH)) return;
            if (adrBrush == null) { adrBrush = new SolidColorBrush(Color.FromArgb(70, 150, 165, 190)); adrBrush.Freeze(); }
            double mid = (prevSessH + prevSessL) / 2.0;
            string g = "ADR" + CurrentBar;
            Draw.Line(this, g + "H", false, 0, prevSessH, -84, prevSessH, adrBrush, DashStyleHelper.Dot, 1);
            Draw.Line(this, g + "L", false, 0, prevSessL, -84, prevSessL, adrBrush, DashStyleHelper.Dot, 1);
            Draw.Line(this, g + "M", false, 0, mid, -84, mid, adrBrush, DashStyleHelper.Dash, 1);
            Draw.Text(this, g + "Ht", false, "PDH", -84, prevSessH, 0, adrBrush, new SimpleFont("Consolas", 9),
                      TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
        }

        private void DrawGrid()
        {
            if (!ShowChart) return;
            // KeepHistory off → wipe the prior grid so only the live one shows (declutter)
            if (!KeepHistory && prevGridTag != null)
            {
                RemoveDrawObject(prevGridTag + "qb"); RemoveDrawObject(prevGridTag + "wb");
                RemoveDrawObject(prevGridTag + "t1"); RemoveDrawObject(prevGridTag + "t2");
                for (int i = 0; i < 8; i++) { RemoveDrawObject(prevGridTag + "l" + i); RemoveDrawObject(prevGridTag + "t" + i); }
                for (int i = 0; i < 7; i++) RemoveDrawObject(prevGridTag + "m" + i);
            }
            string g = "RF" + qualCloseEt.Ticks;
            prevGridTag = g;
            Brush acc = dir == 1 ? Brushes.Teal : Brushes.Crimson;
            Draw.Rectangle(this, g + "qb", false, hourStartLoc, hL, qualCloseLoc, hH, Brushes.Transparent, Brushes.MediumPurple, 12);
            Draw.Rectangle(this, g + "wb", false, qualCloseLoc, hL, windowEndLoc, hH, Brushes.Transparent, acc, 7);
            // supporting feature: the two trigger windows (:20-:30 & :40-:50) where entries fire,
            // shaded faint gold across the full grid so the operator sees the cadence at a glance
            if (ShowTrigWin)
            {
                double gTop = Math.Max(L[0], L[7]), gBot = Math.Min(L[0], L[7]);
                Draw.Rectangle(this, g + "t1", false, qualCloseLoc.AddMinutes(20), gTop, qualCloseLoc.AddMinutes(30), gBot, Brushes.Transparent, Brushes.Goldenrod, 8);
                Draw.Rectangle(this, g + "t2", false, qualCloseLoc.AddMinutes(40), gTop, qualCloseLoc.AddMinutes(50), gBot, Brushes.Transparent, Brushes.Goldenrod, 8);
            }
            for (int i = 0; i < 8; i++)
            {
                Brush b = i == 7 ? Brushes.CornflowerBlue : (i == 0 || i == 6) ? Brushes.Gainsboro : acc;
                Draw.Line(this, g + "l" + i, false, hourStartLoc, L[i], windowEndLoc, L[i], b,
                          DashStyleHelper.Solid, (i == 0 || i == 6) ? 2 : 1);
                Draw.Text(this, g + "t" + i, false, LvlName[i] + "  " + L[i].ToString("F2"),
                          windowEndLoc, L[i], 0, Brushes.White, new SimpleFont("Consolas", 11),
                          TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
            }
            for (int i = 0; i < 7; i++)
                Draw.Line(this, g + "m" + i, false, hourStartLoc, M[i], windowEndLoc, M[i],
                          Brushes.Silver, DashStyleHelper.Dash, 1);
        }

        // ── live state feed for the HTML dashboard (realtime/playback only) ──
        private void EmitEvent(string type, string msg)
        {
            if (!EmitState || State != State.Realtime) return;
            try
            {
                string line = "{\"t\":\"" + TimeZoneInfo.ConvertTime(Time[0], tzEt).ToString("yyyy-MM-dd HH:mm")
                    + "\",\"type\":\"" + type + "\",\"msg\":\"" + msg.Replace("\"", "'") + "\"}\n";
                File.AppendAllText(Path.Combine(StatePath, "events_" + Instrument.MasterInstrument.Name + ".jsonl"), line);
            } catch { }
        }
        private void WriteState(DateTime etClose, bool inWin, double elapsed)
        {
            if (!EmitState || State != State.Realtime) return;
            try
            {
                string status = !windowActive ? (AnyOpen() ? "HOLDING" : "IDLE")
                              : gridDead ? "DEAD" : pivotIdx >= 0 ? "CONFIRMED" : "ACTIVE";
                var sb = new System.Text.StringBuilder(1024);
                sb.Append("{\"instrument\":\"").Append(Instrument.MasterInstrument.Name)
                  .Append("\",\"updated\":\"").Append(etClose.ToString("yyyy-MM-dd HH:mm"))
                  .Append("\",\"price\":").Append(Close[0].ToString("F2"))
                  .Append(",\"status\":\"").Append(status)
                  .Append("\",\"dir\":").Append(windowActive ? dir : 0)
                  .Append(",\"elapsed\":").Append(inWin ? elapsed.ToString("F0") : "0")
                  .Append(",\"pivot\":").Append(pivotIdx)
                  .Append(",\"entries\":").Append(entriesThisWindow)
                  .Append(",\"maxEntries\":").Append(MaxEntries)
                  .Append(",\"accelArmed\":").Append(accelArmed ? "true" : "false")
                  .Append(",\"fadeUsed\":").Append(fadeUsed ? "true" : "false")
                  .Append(",\"consecL\":").Append(consecLosses)
                  .Append(",\"dayPnL\":").Append(dayPnL.ToString("F2"))
                  .Append(",\"halt\":").Append(haltDay ? "true" : "false")
                  .Append(",\"block\":\"").Append(lastBlock.Replace("\"", "'"))
                  .Append("\",\"lastQual\":\"").Append(lastQualInfo.Replace("\"", "'")).Append("\"");
                // current-hour progress vs gate — powers the dashboard's idle "measuring" gauge
                int hodNow = etClose.AddMinutes(-1).Hour;
                double expNow = ExpectedRange(hodNow);
                double gateNow = double.IsNaN(expNow) ? 0 : Math.Max(RangeMult * expNow, MinHeightPts);
                sb.Append(",\"hrRange\":").Append((hH - hL).ToString("F2"))
                  .Append(",\"hrGate\":").Append(gateNow.ToString("F2"));
                if (windowActive)
                {
                    sb.Append(",\"levels\":[").Append(string.Join(",", L.Select(v => v.ToString("F2"))))
                      .Append("],\"mids\":[").Append(string.Join(",", M.Select(v => v.ToString("F2")))).Append("]");
                }
                sb.Append("}");
                File.WriteAllText(Path.Combine(StatePath, "state_" + Instrument.MasterInstrument.Name + ".json"), sb.ToString());
            } catch { }
        }

        // submit one two-leg entry: L1 → t1, L2 → t2, both stopped at sl
        private void FirePair(string tag, bool isLong, double sl, double t1, double t2)
        {
            entrySeq++;
            string s1 = tag + entrySeq + "L1", s2 = tag + entrySeq + "L2";
            sl = RoundTick(sl); t1 = RoundTick(t1); t2 = RoundTick(t2);
            SetStopLoss(s1, CalculationMode.Price, sl, false);
            SetProfitTarget(s1, CalculationMode.Price, t1);
            SetStopLoss(s2, CalculationMode.Price, sl, false);
            SetProfitTarget(s2, CalculationMode.Price, t2);
            legs[s1] = new Leg { IsLong = isLong, Stop = sl };
            legs[s2] = new Leg { IsLong = isLong, Stop = sl };
            if (isLong) { EnterLong(ContractsPerLeg, s1); EnterLong(ContractsPerLeg, s2); }
            else        { EnterShort(ContractsPerLeg, s1); EnterShort(ContractsPerLeg, s2); }
            entriesThisWindow++;
            Dbg(tag + " " + (isLong ? "LONG" : "SHORT") + " sl=" + sl + " t1=" + t1 + " t2=" + t2);
            if (ShowChart)
            {
                if (isLong) Draw.TriangleUp(this, "RFe" + entrySeq, false, 0, Low[0] - 4 * Tick, Brushes.Teal);
                else        Draw.TriangleDown(this, "RFe" + entrySeq, false, 0, High[0] + 4 * Tick, Brushes.Crimson);
                Draw.Text(this, "RFet" + entrySeq, false,
                    tag + (isLong ? "▲ " : "▼ ") + Close[0].ToString("F2") + "\nSL " + sl.ToString("F2")
                    + " T1 " + t1.ToString("F2") + " T2 " + t2.ToString("F2"),
                    Time[0], isLong ? Low[0] - 10 * Tick : High[0] + 10 * Tick, 0,
                    isLong ? Brushes.Teal : Brushes.Crimson, new SimpleFont("Consolas", 10),
                    TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            EmitEvent("ENTRY", tag + " " + (isLong ? "LONG" : "SHORT") + " E " + Close[0].ToString("F2")
                + " SL " + sl.ToString("F2") + " T1 " + t1.ToString("F2") + " T2 " + t2.ToString("F2"));
        }

        // ═══════════════ main ═══════════════
        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;
            if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 5)
            {
                if (CurrentBar == BarsRequiredToTrade) Print("RollingFib requires a 5-minute chart — idle.");
                return;
            }

            DateTime etClose = TimeZoneInfo.ConvertTime(Time[0], tzEt);   // bar CLOSE time, ET
            DateTime inBar   = etClose.AddMinutes(-1);                    // a time strictly inside this bar

            // ── session reset ──
            if (Bars.IsFirstBarOfSession)
            {
                hourKey = DateTime.MinValue; m15Key = DateTime.MinValue; hBars = 0;
                consecLosses = 0; dayPnL = 0; haltDay = false; eodFlattened = false;
                vwapPV = 0; vwapVol = 0;
                // roll prior-day ADR frame, then draw it faintly across the new session
                if (!double.IsNaN(curSessH)) { prevSessH = curSessH; prevSessL = curSessL; DrawAdr(); }
                curSessH = High[0]; curSessL = Low[0];
            }
            else if (!double.IsNaN(curSessH)) { curSessH = Math.Max(curSessH, High[0]); curSessL = Math.Min(curSessL, Low[0]); }
            else { curSessH = High[0]; curSessL = Low[0]; }
            // session VWAP — the CURRENT auction's verdict, used as the direction gate
            double typ = (High[0] + Low[0] + Close[0]) / 3.0;
            vwapPV += typ * Volume[0]; vwapVol += Volume[0];
            double vwap = vwapVol > 0 ? vwapPV / vwapVol : Close[0];

            // ── realized-trade guards (consecutive losses / daily loss) ──
            while (SystemPerformance.AllTrades.Count > lastTradeCount)
            {
                double p = SystemPerformance.AllTrades[lastTradeCount].ProfitCurrency;
                dayPnL += p;
                if (p < 0) consecLosses++; else consecLosses = 0;   // BE (>= 0) resets
                lastTradeCount++;
            }
            if (!haltDay && ((MaxConsecLosses > 0 && consecLosses >= MaxConsecLosses)
                          || (DailyLossLimit > 0 && dayPnL <= -DailyLossLimit)))
            { haltDay = true; Dbg("DAILY HALT consecL=" + consecLosses + " dayPnL=" + dayPnL.ToString("F0")); }

            // ── 1H accumulation ──
            var hk = new DateTime(inBar.Year, inBar.Month, inBar.Day, inBar.Hour, 0, 0);
            if (hk != hourKey) { hourKey = hk; hO = Open[0]; hH = High[0]; hL = Low[0]; hV = Volume[0]; hBars = 1; }
            else { hH = Math.Max(hH, High[0]); hL = Math.Min(hL, Low[0]); hV += Volume[0]; hBars++; }

            // ── 15m accumulation ──
            var mk = new DateTime(inBar.Year, inBar.Month, inBar.Day, inBar.Hour, (inBar.Minute / 15) * 15, 0);
            if (mk != m15Key) { m15Key = mk; m15H = High[0]; m15L = Low[0]; }
            else { m15H = Math.Max(m15H, High[0]); m15L = Math.Min(m15L, Low[0]); }

            bool hourClosing = etClose.Minute == 0;
            bool m15Closing  = etClose.Minute % 15 == 0;

            // ── 16:00 ET hard flatten ──
            if (!eodFlattened && etClose.Hour >= FlattenHour)
            {
                CancelWorking("EOD");
                if (AnyOpen()) FlattenAll("EOD");
                eodFlattened = true; windowActive = false;
            }

            // ═══ QUALIFICATION (runs BEFORE window-expiry so a roll can hold under mode 2) ═══
            bool qualifiedNow = false;
            if (hourClosing && hBars >= 12 && etClose.Hour < FlattenHour + 1)
            {
                int    hod   = inBar.Hour;
                double R     = hH - hL;
                double body  = Math.Abs(Close[0] - hO);
                double clvB  = R > 0 ? (Close[0] - hL) / R : 0;
                double clvS  = R > 0 ? (hH - Close[0]) / R : 0;
                double expR  = ExpectedRange(hod);
                bool   haveV = volEma.TryGetValue(hod, out double expV);

                // wrap-aware session: QualStartHour > QualEndHour spans midnight (e.g. 18→15 =
                // evening Globex through next-day RTH) so evening expansion hours can qualify
                bool inSession = QualStartHour <= QualEndHour
                    ? (hod >= QualStartHour && hod <= QualEndHour)
                    : (hod >= QualStartHour || hod <= QualEndHour);
                // Fixed mode: flat absolute gate (MES 10 pts) — no adaptive baseline, so no
                // volatility boom-bust droughts. Adaptive (median/EMA) still available for A/B.
                double gateR = FixedGate ? MinHeightPts : Math.Max(RangeMult * (double.IsNaN(expR) ? 0 : expR), MinHeightPts);
                bool warm   = FixedGate || !double.IsNaN(expR);
                bool hgtOk  = warm && R >= gateR;
                bool volOk  = VolMult <= 0 || !haveV || expV <= 0 || hV >= VolMult * expV;
                bool bodyOk = R > 0 && body / R >= MinBodyFrac;
                bool isBull = Close[0] > hO && clvB >= MinCLV && (!VwapFilter || Close[0] >= vwap);
                bool isBear = Close[0] < hO && clvS >= MinCLV && EnableShorts && (!VwapFilter || Close[0] <= vwap);

                if (inSession && hgtOk && volOk && bodyOk && (isBull || isBear))
                {
                    int newDir = isBull ? 1 : -1;
                    if (AnyOpen())
                    {
                        bool oppose = (Position.MarketPosition == MarketPosition.Long && newDir == -1)
                                   || (Position.MarketPosition == MarketPosition.Short && newDir == 1);
                        if (oppose || WindowExitMode == 0) FlattenAll("Roll");
                    }
                    CancelWorking("roll");
                    dir = newDir;
                    double gBase = dir == 1 ? hL : hH;
                    for (int i = 0; i < 8; i++) L[i] = gBase + dir * R * Ratios[i];
                    for (int i = 0; i < 7; i++) M[i] = (L[i] + L[i + 1]) / 2.0;
                    qualCloseEt = etClose; windowEndEt = etClose.AddMinutes(60);
                    windowActive = true; qualifiedNow = true;
                    pivotIdx = -1; pivotUsed = false; confirmBar = -1; entriesThisWindow = 0; prevTrigWin = 0;
                    accelArmed = false; accelUsed = false; fadeUsed = false;
                    gridDead = false; ckptDone = false;
                    extremeBeyond = double.NaN; extTouched = false; brokeBeyond = false;
                    gridR = R;
                    hourStartLoc = Time[0].AddMinutes(-60); qualCloseLoc = Time[0]; windowEndLoc = Time[0].AddMinutes(60);
                    lastBlock = "—";
                    lastQualInfo = (dir == 1 ? "BULL " : "BEAR ") + R.ToString("F2") + "pts @" + hod + ":00";
                    DrawGrid();
                    EmitEvent("QUALIFIED", lastQualInfo + " grid " + L[0].ToString("F2") + " -> " + L[6].ToString("F2"));
                    Dbg("QUALIFIED " + (dir == 1 ? "BULL " : "BEAR ") + R.ToString("F2")
                        + "pts @" + hod + ":00  grid 0=" + L[0].ToString("F2") + " 100=" + L[6].ToString("F2"));
                }
                else if (inSession)
                {
                    lastBlock = !warm ? "warmup"
                        : !hgtOk ? "height " + R.ToString("F1") + " < " + Math.Max(RangeMult * (double.IsNaN(expR) ? 0 : expR), MinHeightPts).ToString("F1")
                        : !volOk ? "volume < " + VolMult.ToString("F2") + "x"
                        : !bodyOk ? "body " + (R > 0 ? (body / R * 100).ToString("F0") : "0") + "% < " + (MinBodyFrac * 100).ToString("F0") + "%"
                        : "close-location (wick candle)";
                    EmitEvent("BLOCK", hod + ":00 hour blocked — " + lastBlock);
                }

                // baseline update AFTER evaluation, winsorized
                double RU = R, VU = hV;
                // winsorized up, boosted down: spikes enter capped, and baselines decay
                // DecayBoost× faster than they rise so the gate renormalizes in days
                if (rngEma.TryGetValue(hod, out double pr))
                { if (CapOutlier > 0) RU = Math.Min(RU, pr * CapOutlier);
                  double aR = RU < pr ? Math.Min(SeasAlpha * DecayBoost, 1.0) : SeasAlpha;
                  rngEma[hod] = pr + aR * (RU - pr); }
                else rngEma[hod] = RU;
                if (volEma.TryGetValue(hod, out double pv))
                { if (CapOutlier > 0) VU = Math.Min(VU, pv * CapOutlier);
                  double aV = VU < pv ? Math.Min(SeasAlpha * DecayBoost, 1.0) : SeasAlpha;
                  volEma[hod] = pv + aV * (VU - pv); }
                else volEma[hod] = VU;
                if (inSession) { recentRanges.Enqueue(RU); while (recentRanges.Count > 14) recentRanges.Dequeue(); }
            }

            // ═══ WINDOW EXPIRY (no roll happened) ═══
            if (windowActive && !qualifiedNow && etClose >= windowEndEt)
            {
                windowActive = false;
                CancelWorking("window end");
                if ((WindowExitMode == 0 || WindowExitMode == 2) && AnyOpen()) FlattenAll("WndEnd");
                EmitEvent("WINDOW", "closed" + (AnyOpen() && WindowExitMode == 1 ? " — holding on brackets" : ""));
                Dbg("window closed");
            }

            WriteState(etClose, windowActive, windowActive ? (etClose - qualCloseEt).TotalMinutes : 0);
            if (!windowActive) return;
            double elapsed = (etClose - qualCloseEt).TotalMinutes;
            if (elapsed <= 0) return;
            if (elapsed > CutoffMin) CancelWorking("cutoff");
            // operator cadence: arm at the 15m close, trigger at :25 / :45. The :25 and :45
            // windows are INDEPENDENT — entering the :45 window while flat RE-ARMS the setup
            // even on the same pivot (fixes: one-per-pivot killed every :45 after a :25 fill).
            int trigWin = (elapsed >= 20 && elapsed <= 30) ? 1 : (elapsed >= 40 && elapsed <= 50) ? 2 : 0;
            bool inFillWin = !UseTrigWin || trigWin != 0;
            if (trigWin != 0 && trigWin != prevTrigWin && Position.MarketPosition == MarketPosition.Flat && workingEntries.Count == 0)
            { pivotUsed = false; accelUsed = false; }
            prevTrigWin = trigWin;
            if (!inFillWin && workingEntries.Count > 0) CancelWorking("outside trigger window");

            // ═══ 15m CONFIRM / INVALIDATION / ACCEPTANCE ═══
            if (m15Closing && !gridDead)
            {
                if (GtD(L[0], Close[0]))                                // 15m close beyond fib 0 against grid
                {
                    gridDead = true;
                    CancelWorking("invalidated");
                    if (AnyOpen()) FlattenAll("Inval");
                    EmitEvent("INVALIDATED", "15m close beyond fib 0 (" + L[0].ToString("F2") + ") — stand down");
                    Dbg("INVALIDATED — 15m close beyond 0");
                }
                else
                {
                    int cand = -1;
                    for (int i = 0; i <= 5; i++)
                    {
                        bool dipped = dir == 1 ? m15L <= M[i] : m15H >= M[i];
                        if (dipped && GtD(Close[0], M[i])) cand = i;
                    }
                    if (cand >= 0 && cand != pivotIdx)
                    {
                        CancelWorking("pivot update");
                        pivotIdx = cand; pivotUsed = false; confirmBar = CurrentBar;
                        // GOLD = tradeable confirm (Setup A will act); GRAY = confirm too deep to
                        // trade (pivot > MaxPivotIdx, orphaned since Setup C retired) — so a gray
                        // dot explains "bubble but no trade": the visual now tells the truth.
                        if (ShowChart) Draw.Dot(this, "RFc" + qualCloseEt.Ticks + "_" + CurrentBar, false, 0, M[cand],
                            cand <= MaxPivotIdx ? Brushes.Gold : Brushes.DimGray);
                        EmitEvent("CONFIRM", "15m " + (dir == 1 ? "support" : "resistance") + " @ "
                            + LvlName[cand] + "/" + LvlName[cand + 1] + " midline " + M[cand].ToString("F2"));
                        Dbg("15m CONFIRM pivot=" + cand + " (" + M[cand].ToString("F2") + ")");
                        // WindowExitMode 1/2: trail open-grid-direction stops to the new pivot midline
                        if (WindowExitMode >= 1)
                            foreach (var kv in legs.Where(k => k.Value.Open && k.Value.IsLong == (dir == 1)))
                            {
                                double cSt = RoundTick(M[cand] - dir * StopBufTicks * Tick);
                                bool better = dir == 1 ? cSt > kv.Value.Stop + Tick / 2 : cSt < kv.Value.Stop - Tick / 2;
                                bool safe   = dir == 1 ? cSt < Close[0] - Tick : cSt > Close[0] + Tick;
                                if (better && safe) { SetStopLoss(kv.Key, CalculationMode.Price, cSt, false); kv.Value.Stop = cSt; }
                            }
                    }
                    bool heldUpper = dir == 1 ? m15L >= L[4] : m15H <= L[4];
                    if (EnableAccel && heldUpper && GtD(Close[0], L[5])) { accelArmed = true; Dbg("Setup C armed"); }
                }
            }

            if (gridDead) return;

            // ═══ :45 CHECKPOINT — stops to breakeven ═══
            if (CheckpointBE && !ckptDone && elapsed >= 45)
            {
                ckptDone = true;
                foreach (var kv in legs.Where(k => k.Value.Open && !double.IsNaN(k.Value.Fill)))
                {
                    double f = RoundTick(kv.Value.Fill);
                    bool safe = kv.Value.IsLong ? Close[0] > f + 2 * Tick : Close[0] < f - 2 * Tick;
                    bool better = kv.Value.IsLong ? f > kv.Value.Stop : f < kv.Value.Stop;
                    if (safe && better) { SetStopLoss(kv.Key, CalculationMode.Price, f, false); kv.Value.Stop = f; }
                }
                Dbg(":45 checkpoint — BE");
            }

            // BE at 1R: a trade that has paid a full risk-unit never turns back into a loser
            // (8 of 77 losers in the 12-mo run reached 1R before dying at the original stop)
            if (BreakEvenAt1R)
                foreach (var kv in legs.Where(k => k.Value.Open && !double.IsNaN(k.Value.Fill) && k.Value.Risk0 > 0))
                {
                    var g = kv.Value;
                    double be = RoundTick(g.Fill);
                    bool earned = g.IsLong ? Close[0] >= g.Fill + g.Risk0 : Close[0] <= g.Fill - g.Risk0;
                    bool better = g.IsLong ? be > g.Stop : be < g.Stop;
                    if (earned && better) { SetStopLoss(kv.Key, CalculationMode.Price, be, false); g.Stop = be; }
                }

            // Hard heat cut (operator rule: >~$50 / 10pt drawdown on a micro = wrong entry, get out).
            // Data: a good trade takes ~2pt heat; worst-case window heat was ~9pt. Cut past that.
            // MinHoldBars: give the trade room — no DISCRETIONARY exit (heat cut) before N bars.
            // The bracket TP/stop always fires (that is the "or TP/stop"); only the heat cut waits.
            bool heldEnough = entryBar < 0 || (CurrentBar - entryBar) >= MinHoldBars;
            if (MaxHeatPts > 0 && heldEnough && Position.MarketPosition != MarketPosition.Flat)
            {
                double heat = (Position.AveragePrice - Close[0]) * (Position.MarketPosition == MarketPosition.Long ? 1 : -1);
                if (heat >= MaxHeatPts) { FlattenAll("HeatCut"); CancelWorking("HeatCut"); }
            }

            bool canEnter = !haltDay && entriesThisWindow < MaxEntries && elapsed <= CutoffMin
                            && (Position.MarketPosition == MarketPosition.Flat
                                || (Position.MarketPosition == MarketPosition.Long) == (dir == 1));

            // ═══ SETUP A — rotation continuation ═══
            if (canEnter && inFillWin && pivotIdx >= 0 && pivotIdx <= MaxPivotIdx && !(OnePerPivot && pivotUsed) && workingEntries.Count == 0)
            {
                // passive: rest a limit pair at the first main level beyond the pivot
                // (confirm at the 23-mid → limit at the 38.2). The pullback fills us.
                double shelf = L[pivotIdx + 1];
                double sl = (WideStops ? L[pivotIdx] : M[pivotIdx]) - dir * StopBufTicks * Tick;
                // EntryTiming — the operator's open-vs-close question, made testable:
                //  0 TOUCH  : rest a limit at the shelf, fill on first touch (earliest, knife-risk)
                //  1 CLOSE  : enter at close of a bar that dipped to the shelf & closed back beyond
                //             it (rejection held) — best for RANGE/reversion (avoids the knife)
                //  2 OPEN   : market-enter the moment price holds beyond the shelf on a confirmed
                //             bar (rides immediate CONTINUATION) — best for TREND/cascade
                bool dipped = dir == 1 ? Low[0] <= shelf : High[0] >= shelf;
                bool trigger = EntryTiming == 1 ? (dipped && GtD(Close[0], shelf)) : GtD(Close[0], shelf);
                double px = EntryTiming == 0 ? shelf : Close[0];
                double risk = (px - sl) * dir;
                if (trigger && risk >= 4 * Tick && risk <= MaxRiskFrac * gridR)
                {
                    double t1 = double.NaN;
                    for (int i = 0; i <= 6 && double.IsNaN(t1); i++)
                        if (GtD(L[i], px) && (L[i] - px) * dir >= risk * MinRR
                            && (L[i] - px) * dir <= risk * MaxT1RR) t1 = L[i];
                    if (!double.IsNaN(t1))
                    {
                        double extMid = (L[6] + L[7]) / 2.0;
                        double t2 = t1 == L[6] ? extMid : double.NaN;
                        if (double.IsNaN(t2))
                            for (int i = 0; i <= 6 && double.IsNaN(t2); i++)
                                if (GtD(L[i], t1)) t2 = L[i];
                        if (double.IsNaN(t2)) t2 = extMid;
                        // reversion moves are SHORT — runner-to-far-T2 lost -$439 at 14% WR.
                        // Default: bank BOTH legs at T1 (no runner). pivotUsed consumes on FILL.
                        // TOUCH (0) rests a limit; CLOSE (1) and OPEN (2) market-enter.
                        if (EntryTiming == 0) FirePairLimit("A", dir == 1, px, sl, t1, HoldRunner ? t2 : t1);
                        else                  FirePair("A", dir == 1, sl, t1, HoldRunner ? t2 : t1);
                    }
                }
            }

            // ═══ SETUP C — acceptance continuation (trend hours) ═══
            // C's niche is hours with NO tradeable pullback — if an A-eligible pivot exists,
            // trade the retest or nothing. And only FRESH breaks: chasing extended closes was
            // 28 of 43 losers (1-2 bar stop-outs) in the Jan-Jul '26 backtest.
            if (GtD(dir == 1 ? High[0] : Low[0], L[6])) brokeBeyond = true;
            if (canEnter && inFillWin && EnableAccel && accelArmed && !accelUsed && brokeBeyond
                && (pivotIdx < 0 || pivotIdx > MaxPivotIdx) && workingEntries.Count == 0)
            {
                // passive break-retest: the 100 broke — rest a limit pair AT the broken level;
                // the retest fills us with the level itself as validation
                double px = L[6];
                double sl = (WideStops ? L[5] : M[5]) - dir * StopBufTicks * Tick;
                double risk = (px - sl) * dir;
                double extMidC = (L[6] + L[7]) / 2.0;
                if (risk >= 4 * Tick && (extMidC - px) * dir >= risk * MinRR)
                    FirePairLimit("C", dir == 1, px, sl, extMidC, HoldRunner ? L[7] : extMidC);   // accelUsed consumes on FILL
            }

            // ═══ SETUP B — REVERSION FADE at the exhaustion boundary (100) ═══
            // The 100/extension is a reversion zone (Setup C chased it wrong — 49% against,
            // -$3202). This fades the REJECTION of the 100 back toward the mean.
            double extTrig = L[6];   // the 100 — where momentum exhausts and reverts
            double edge = dir == 1 ? High[0] : Low[0];
            if (GeD(edge, extTrig)) extTouched = true;
            if (extTouched)
                extremeBeyond = double.IsNaN(extremeBeyond) ? edge
                              : (dir == 1 ? Math.Max(extremeBeyond, High[0]) : Math.Min(extremeBeyond, Low[0]));
            if (EnableFade && extTouched && !fadeUsed && !haltDay && elapsed <= CutoffMin + 10
                && Position.MarketPosition == MarketPosition.Flat && workingEntries.Count == 0 && GtD(L[6], Close[0]))
            {
                bool extOk = !FadeSkipBeyondExt || GtD(L[7], extremeBeyond);
                double sl = extremeBeyond + dir * StopBufTicks * Tick;
                double risk = (sl - Close[0]) * dir;
                double rew  = (Close[0] - L[5]) * dir;
                if (extOk && risk >= 2 * Tick && rew >= risk * FadeMinRR)
                {
                    fadeUsed = true;
                    entrySeq++;
                    string sf = "F" + entrySeq + "L1";
                    SetStopLoss(sf, CalculationMode.Price, RoundTick(sl), false);
                    SetProfitTarget(sf, CalculationMode.Price, RoundTick(L[5]));
                    legs[sf] = new Leg { IsLong = dir != 1, Stop = RoundTick(sl) };
                    if (dir == 1) EnterShort(ContractsPerLeg, sf); else EnterLong(ContractsPerLeg, sf);
                    if (ShowChart) Draw.Diamond(this, "RFf" + entrySeq, false, 0,
                        dir == 1 ? High[0] + 4 * Tick : Low[0] - 4 * Tick, Brushes.Orange);
                    EmitEvent("FADE", (dir == 1 ? "SHORT" : "LONG") + " E " + Close[0].ToString("F2")
                        + " SL " + sl.ToString("F2") + " T " + L[5].ToString("F2"));
                    Dbg("FADE " + (dir == 1 ? "SHORT" : "LONG") + " sl=" + sl.ToString("F2") + " t=" + L[5].ToString("F2"));
                }
            }
        }

        private double extremeBeyond = double.NaN;
        private bool   extTouched;

        // ═══════════════ executions: fill tracking + Leg-1 breakeven ═══════════════
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
            int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            var o = execution?.Order;
            if (o == null || o.OrderState != OrderState.Filled) return;

            if (legs.TryGetValue(o.Name, out Leg entry))                 // entry fill
            {
                entry.Fill = o.AverageFillPrice; entry.Open = true;
                workingEntries.RemoveAll(w => w != null && w.Name == o.Name);
                if (o.Name.EndsWith("L1"))                       // budgets consume on FILL, once per pair
                {
                    entriesThisWindow++;
                    entryBar = CurrentBar;                       // min-hold: bars-since-entry clock starts
                    if (o.Name.StartsWith("A")) pivotUsed = true;
                    if (o.Name.StartsWith("C")) accelUsed = true;
                }
                return;
            }

            string from = o.FromEntrySignal;
            if (string.IsNullOrEmpty(from) || !legs.TryGetValue(from, out Leg leg)) return;
            leg.Open = false;                                            // any exit closes the leg

            if (o.Name == "Profit target" && from.EndsWith("L1"))        // Leg-1 target → sibling to BE
            {
                string sib = from.Substring(0, from.Length - 2) + "L2";
                if (legs.TryGetValue(sib, out Leg l2) && l2.Open && !double.IsNaN(l2.Fill))
                {
                    double be = RoundTick(l2.Fill);
                    bool better = l2.IsLong ? be > l2.Stop : be < l2.Stop;
                    if (better) { SetStopLoss(sib, CalculationMode.Price, be, false); l2.Stop = be; }
                    Dbg("L1 target filled → " + sib + " to BE " + be.ToString("F2"));
                }
            }
        }

        private double SeasAlpha => 2.0 / (SeasLenDays + 1);

        // ═══════════════ parameters ═══════════════
        [NinjaScriptProperty, Range(1.0, 3.0),  Display(Name = "Range gate multiple [x]",        GroupName = "1 Qualification", Order = 1)]
        public double RangeMult { get; set; }
        [NinjaScriptProperty, Range(0.0, 100),  Display(Name = "Absolute min height [pts]",      GroupName = "1 Qualification", Order = 2)]
        public double MinHeightPts { get; set; }
        [NinjaScriptProperty, Range(0.0, 3.0),  Display(Name = "Volume gate multiple [x] 0=off", GroupName = "1 Qualification", Order = 3)]
        public double VolMult { get; set; }
        [NinjaScriptProperty, Range(0.0, 1.0),  Display(Name = "Min body/range [frac]",          GroupName = "1 Qualification", Order = 4)]
        public double MinBodyFrac { get; set; }
        [NinjaScriptProperty, Range(0.0, 1.0),  Display(Name = "Min close-location [frac]",      GroupName = "1 Qualification", Order = 5)]
        public double MinCLV { get; set; }
        [NinjaScriptProperty, Range(3, 30),     Display(Name = "Seasonal EMA length [days]",     GroupName = "1 Qualification", Order = 6)]
        public int SeasLenDays { get; set; }
        [NinjaScriptProperty, Range(0.0, 5.0),  Display(Name = "Baseline outlier cap [x] 0=off", GroupName = "1 Qualification", Order = 7)]
        public double CapOutlier { get; set; }
        [NinjaScriptProperty, Range(1.0, 5.0),  Display(Name = "Baseline decay boost [x]",       GroupName = "1 Qualification", Order = 10)]
        public double DecayBoost { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Use session-median range model", GroupName = "1 Qualification", Order = 11)]
        public bool UseSessionMedian { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Fixed gate (flat abs pts, no adaptive baseline)", GroupName = "1 Qualification", Order = 13)]
        public bool FixedGate { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Frame prior-day ADR (H/Mid/L, faint)", GroupName = "4 Display", Order = 4)]
        public bool ShowADR { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Keep historic grids (off = only live grid)", GroupName = "4 Display", Order = 5)]
        public bool KeepHistory { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Shade trigger windows (:25 & :45)", GroupName = "4 Display", Order = 6)]
        public bool ShowTrigWin { get; set; }
        [NinjaScriptProperty, Range(0, 12),     Display(Name = "Min hold bars before heat cut (0=off)", GroupName = "2 Trade window", Order = 24)]
        public int MinHoldBars { get; set; }
        [NinjaScriptProperty, Range(0, 23),     Display(Name = "First qualifying hour [ET]",     GroupName = "1 Qualification", Order = 8)]
        public int QualStartHour { get; set; }
        [NinjaScriptProperty, Range(0, 23),     Display(Name = "Last qualifying hour [ET]",      GroupName = "1 Qualification", Order = 9)]
        public int QualEndHour { get; set; }

        [NinjaScriptProperty, Range(10, 55),    Display(Name = "Entry cutoff [min into window]", GroupName = "2 Trade window", Order = 1)]
        public int CutoffMin { get; set; }
        [NinjaScriptProperty, Range(1, 5),      Display(Name = "Max entries per window",         GroupName = "2 Trade window", Order = 2)]
        public int MaxEntries { get; set; }
        [NinjaScriptProperty, Range(0, 10),     Display(Name = "Stop buffer [ticks]",            GroupName = "2 Trade window", Order = 3)]
        public int StopBufTicks { get; set; }
        [NinjaScriptProperty, Range(0.5, 3.0),  Display(Name = "Min RR to T1 [x]",               GroupName = "2 Trade window", Order = 4)]
        public double MinRR { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Enable shorts (bear grids)",     GroupName = "2 Trade window", Order = 5)]
        public bool EnableShorts { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Setup C acceptance-continuation",GroupName = "2 Trade window", Order = 6)]
        public bool EnableAccel { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Setup B extension fade",         GroupName = "2 Trade window", Order = 7)]
        public bool EnableFade { get; set; }
        [NinjaScriptProperty, Range(0.5, 3.0),  Display(Name = "Fade min RR [x]",                GroupName = "2 Trade window", Order = 8)]
        public double FadeMinRR { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Fade skip if beyond 161.8",      GroupName = "2 Trade window", Order = 9)]
        public bool FadeSkipBeyondExt { get; set; }
        [NinjaScriptProperty,                   Display(Name = "One Setup-A entry per pivot",    GroupName = "2 Trade window", Order = 10)]
        public bool OnePerPivot { get; set; }
        [NinjaScriptProperty, Range(0.0, 0.5),  Display(Name = "Setup A retest proximity [frac grid]", GroupName = "2 Trade window", Order = 13)]
        public double RetestFrac { get; set; }
        [NinjaScriptProperty, Range(0.05, 1.0), Display(Name = "Setup A max risk [frac grid]",   GroupName = "2 Trade window", Order = 14)]
        public double MaxRiskFrac { get; set; }
        [NinjaScriptProperty, Range(1.0, 6.0),  Display(Name = "Setup A max RR to T1 [x]",       GroupName = "2 Trade window", Order = 15)]
        public double MaxT1RR { get; set; }
        [NinjaScriptProperty, Range(0, 5),      Display(Name = "Setup A deepest pivot [0-5]",    GroupName = "2 Trade window", Order = 16)]
        public int MaxPivotIdx { get; set; }
        [NinjaScriptProperty, Range(0.02, 0.5), Display(Name = "Setup C max overshoot [frac grid]", GroupName = "2 Trade window", Order = 17)]
        public double MaxOvershoot { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Wide stops (full level below pivot)", GroupName = "2 Trade window", Order = 18)]
        public bool WideStops { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Breakeven at 1R",                 GroupName = "2 Trade window", Order = 19)]
        public bool BreakEvenAt1R { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Trigger windows :20-:30 & :40-:50", GroupName = "2 Trade window", Order = 20)]
        public bool UseTrigWin { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Hold runner to T2 (off = bank both at T1, reversion)", GroupName = "2 Trade window", Order = 21)]
        public bool HoldRunner { get; set; }
        [NinjaScriptProperty, Range(0, 2),      Display(Name = "Entry timing 0=touch 1=close-confirm 2=open-on-confirm", GroupName = "2 Trade window", Order = 22)]
        public int EntryTiming { get; set; }
        [NinjaScriptProperty, Range(0.0, 30.0), Display(Name = "Hard cut at heat [pts] (0=off)", GroupName = "2 Trade window", Order = 23)]
        public double MaxHeatPts { get; set; }
        [NinjaScriptProperty,                   Display(Name = "VWAP direction gate",             GroupName = "1 Qualification", Order = 12)]
        public bool VwapFilter { get; set; }
        [NinjaScriptProperty,                   Display(Name = ":45 checkpoint breakeven",       GroupName = "2 Trade window", Order = 11)]
        public bool CheckpointBE { get; set; }
        [NinjaScriptProperty, Range(0, 2),      Display(Name = "Window exit mode 0=flat 1=hold 2=roll-hold", GroupName = "2 Trade window", Order = 12)]
        public int WindowExitMode { get; set; }

        [NinjaScriptProperty, Range(1, 20),     Display(Name = "Contracts per leg",              GroupName = "3 Risk", Order = 1)]
        public int ContractsPerLeg { get; set; }
        [NinjaScriptProperty, Range(0, 20),     Display(Name = "Max consecutive losses 0=off",   GroupName = "3 Risk", Order = 2)]
        public int MaxConsecLosses { get; set; }
        [NinjaScriptProperty, Range(0, 100000), Display(Name = "Daily loss limit [$] 0=off",     GroupName = "3 Risk", Order = 3)]
        public double DailyLossLimit { get; set; }
        [NinjaScriptProperty, Range(0, 23),     Display(Name = "Hard flatten hour [ET]",         GroupName = "3 Risk", Order = 4)]
        public int FlattenHour { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Debug prints",                   GroupName = "3 Risk", Order = 5)]
        public bool DebugPrints { get; set; }

        [NinjaScriptProperty,                   Display(Name = "Draw grid on chart",             GroupName = "4 Display", Order = 1)]
        public bool ShowChart { get; set; }
        [NinjaScriptProperty,                   Display(Name = "Emit live state for dashboard",  GroupName = "4 Display", Order = 2)]
        public bool EmitState { get; set; }
        [NinjaScriptProperty,                   Display(Name = "State folder",                   GroupName = "4 Display", Order = 3)]
        public string StatePath { get; set; }
    }
}
