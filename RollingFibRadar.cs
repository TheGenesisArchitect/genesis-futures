// ═══════════════════════════════════════════════════════════════════════════════
//  ROLLING FIB RADAR — NinjaTrader 8 INDICATOR (visualization only, no orders)  v2.0  (2026-07-10)
//  v2.0: dashboard-parity palette · persistent dimmed history (adjustable dimmer) · premium/discount
//        shading · dotted-gold 55 line · reversalType tags (continuation/reversal/neutral) · :25/:45
//        continuation-pullback callout.  VERIFY: NinjaTrader's indicator Description reads "v2.0" when this build is live.
//
//  Pure drawing companion to the RollingFib strategy. Shows exactly what the strategy
//  qualifies — but as a plain indicator, so it ALWAYS renders on any 5-minute chart
//  (historical + live), with no "enable strategy" step and no execution to interfere.
//
//  Draws, for every qualifying hour (MES 10pt+ by default):
//   · the qualifying-hour box (purple)          · all 8 fib levels + labels
//   · the 7 midlines (dashed)                    · the trade-window shading (next hour)
//   · the two trigger-window bands (:25 & :45)   · the prior-day ADR frame (H/Mid/L)
//   · a 15m-confirm dot: GOLD = tradeable pivot, GRAY = too deep for Setup A
//
//  APPLY: chart (5-min) → right-click → Indicators → RollingFibRadar → OK. That's it.
//  Times are US Eastern.
// ═══════════════════════════════════════════════════════════════════════════════
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class RollingFibRadar : Indicator
    {
        private static readonly double[] Ratios = { 0.0, 0.236, 0.382, 0.5, 0.618, 0.786, 1.0, 1.618 };
        private static readonly string[] LvlName = { "0", "23.6", "38.2", "50", "61.8", "78.6", "100", "161.8" };

        // ── Genesis / Helix chart palette — ported 1:1 from dashboard.html chartColors() so the Radar and the
        //    dashboard read as ONE surface (this was the confusion that cost trades) ──
        private static Brush GB(int r, int g, int b, double a = 1.0)
        { var br = new SolidColorBrush(Color.FromArgb((byte)(a * 255), (byte)r, (byte)g, (byte)b)); br.Freeze(); return br; }
        private static readonly Brush GxBull   = GB(0x1A, 0xBF, 0x6A);      // --color-positive  (bull / BUY)
        private static readonly Brush GxBear   = GB(0xE8, 0x40, 0x40);      // --color-negative  (bear / SELL)
        private static readonly Brush GxTeal   = GB(0x00, 0xD4, 0xAA);      // teal-400          (bull grid accent / intel)
        private static readonly Brush GxGold   = GB(0xE8, 0xCB, 0x6E);      // gold-400          (A+ / trigger windows / the 55)
        private static readonly Brush GxChoch  = GB(0xE0, 0x8A, 0x3C);      // CHoCH orange
        private static readonly Brush GxBos    = GB(0x4A, 0xA3, 0xDF);      // BOS blue / 161.8
        private static readonly Brush GxViolet = GB(0x9B, 0x6D, 0xFF);      // qualifying-hour box
        private static readonly Brush GxBound  = GB(245, 240, 232, 0.85);   // the 0 / 100 bounds
        private static readonly Brush GxLine   = GB(245, 240, 232, 0.30);   // inner level lines
        private static readonly Brush GxLbl    = GB(245, 240, 232, 0.68);   // level labels
        private static readonly Brush GxMid    = GB(245, 240, 232, 0.20);   // midlines
        // dimmed historical-grid brush is built live from the HistoryDim input (adjustable dimmer) — see DimBrush()
        private double[] L = new double[8];
        private double[] M = new double[7];
        private int    dir;

        // 1H / 15m accumulators
        private DateTime hourKey = DateTime.MinValue, m15Key = DateTime.MinValue;
        private double   hO, hH, hL, hV; private int hBars, hStartBar;
        private double   m15H, m15L;
        private int      drawDir, pivotIdx = -1;
        private DateTime qualCloseLoc, hourStartLoc, windowEndLoc, winEndEt;
        private bool     windowActive;

        // ── STRUCTURE (CHoCH/BOS) + GRID PERSISTENCE + FADE — all gated behind GridPersistence, default OFF ──
        private double   lastSH = double.NaN, lastSL = double.NaN, prevSH = double.NaN, prevSL = double.NaN;
        private int      structureDir;                 // +1 HH+HL, -1 LH+LL, 0 undetermined
        private bool     shBroken, slBroken;           // don't re-fire a break on the same swing
        private string   lastStructure = "";           // e.g. "CHoCH up" / "BOS down"
        private string   lastBreakKind = "";           // "CHoCH" | "BOS"
        private int      lastBreakDir, lastBreakBar = -1000;
        private string   reversalType = "";            // REVERSAL | CONTINUATION | NEUTRAL (tag on the live grid)
        private string   gridPhase = "IDLE";           // IDLE | WINDOW | PERSIST
        private bool     gridLive;                     // grid is the active map (through PERSIST) until invalidation
        private DateTime gridStartEt = DateTime.MinValue, lastTouchEt = DateTime.MinValue, lastFadeEt = DateTime.MinValue;
        private int      acceptCount;                  // consecutive 5m closes beyond the 161.8
        private bool     fadeArmed;                    // one-shot gate for the sweep-and-reject fade
        private int      fadePendBar = -1, fadePendDir;   // fade confirmation: bar the reject appeared, and its direction
        private double   fadePendSwept, fadePendExt;      // swept level + sweep extreme (breached ⇒ cancel the pending fade)
        private class GridSnap { public string Tag; public double[] Lv; public DateTime Start, End; }   // a drawn grid, kept so it can be redrawn dim
        private readonly List<GridSnap> gridHist = new List<GridSnap>();   // full history of grids on the chart (newest bright, rest dimmed)
        private Brush dimBrush; private double dimBrushAt = -1;            // cached historical-dim brush, rebuilt when HistoryDim changes
        private bool     btInit;                       // backtest log truncated once per load

        // adaptive baseline (only used if FixedGate = false)
        private readonly Dictionary<int, double> rngEma = new Dictionary<int, double>();
        private readonly Queue<double> recentRanges = new Queue<double>();

        // VWAP + ADR
        private double vwapPV, vwapVol;
        private double curSessH, curSessL = double.NaN, prevSessH = double.NaN, prevSessL = double.NaN;
        private Brush  adrBrush;
        private string prevGridTag;
        private TimeZoneInfo tzEt;
        private string lastQual = "none yet", lastBlock = "warmup";
        private double hrGateNow, hrRangeNow;
        private DateTime lastTickWrite = DateTime.MinValue;   // throttle the live tick feed to the dashboard
        private DateTime armedHourKey = DateTime.MinValue;   // :45 pre-arm fires once per hour
        private double winHi = double.NaN, winLo = double.NaN;   // window price excursion for outcome scoring
        private bool   contFired = false;                        // continuation :25/:45 callout — one shot per trade window
        private bool   qualEmitted = false, reemitDone = false;  // did the live grid's QUALIFIED reach the dashboard? (re-add reprocesses history, where EmitEvent is suppressed)

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name             = "RollingFibRadar";
                Description      = "Rolling Fib Radar v2.0 — dashboard-parity persistent grids + continuation/fade signals (viz only)";
                Calculate        = Calculate.OnBarClose;
                IsOverlay        = true;
                DisplayInDataBox = false;
                PaintPriceMarkers= false;
                DrawOnPricePanel = true;

                QualStartHour = 7;    QualEndHour = 14;  MinHeightPts = 10.0;  FixedGate = true;
                RangeMult     = 1.25; MinBodyFrac = 0.30; MinCLV = 0.60;  VwapFilter = true; SeasLenDays = 10;   // 0.30: allow whippy-but-directional hours; CLV carries the direction filter
                MaxPivotIdx   = 3;
                ShowADR = true;  ShowTrigWin = true;  KeepHistory = true;  HistoryGrids = 12;  HistoryDim = 40;
                EmitState = true;  StatePath = @"C:\Users\mrbee\Documents\RollingFib\live";  AlertOnQualify = true;  ArmFrac = 0.80;
                GridPersistence = false;  SwingStrength = 2;  TimeDecayHrs = 3;  PersistMaxHrs = 4;   // structure/persistence layer OFF by default — flip ON to activate (draws + signals only, no orders)
                FadeConfirmBars = 2;  FadeReversalOnly = true;  MinSwingPts = 3.0;   // confirm=2 backtested best (+0.71R vs +0.33R at 1); only fade REVERSAL grids; ignore sub-3pt micro-swings
            }
            else if (State == State.Configure)
            {
                tzEt = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                if (EmitState) try { Directory.CreateDirectory(StatePath); } catch { EmitState = false; }
            }
        }

        private double SeasAlpha => 2.0 / (SeasLenDays + 1);
        private bool   GtD(double a, double b) => dir == 1 ? a > b : a < b;

        private double ExpectedRange(int hod)
        {
            if (recentRanges.Count >= 5) { var s = recentRanges.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
            return double.NaN;
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 20) return;
            if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 5)
            {
                if (CurrentBar == 20) Draw.TextFixed(this, "warn", "RollingFibRadar needs a 5-minute chart", TextPosition.TopLeft);
                return;
            }

            if (EmitState && !btInit) { btInit = true; BtTruncate(); }   // fresh backtest log per load (logs ALL history)

            DateTime etClose = TimeZoneInfo.ConvertTime(Time[0], tzEt);
            DateTime inBar   = etClose.AddMinutes(-1);

            // belt-and-suspenders for a mid-session re-add: EmitEvent is realtime-only, so a qualification that
            // landed during the historical reprocess never reached the dashboard. On the first realtime bar,
            // re-emit the QUALIFIED for a still-live grid (uses the grid's own close time so the window keys at
            // the right hour). Ended windows are left to the server-side reconstruction, which also recovers the
            // confirm + outcome that a bare re-emit would miss.
            if (State == State.Realtime && !reemitDone) { reemitDone = true; ReemitLiveQual(); WriteBarsHistory(); }

            // session reset + VWAP + ADR roll
            if (Bars.IsFirstBarOfSession)
            {
                hourKey = DateTime.MinValue; m15Key = DateTime.MinValue; hBars = 0;
                vwapPV = 0; vwapVol = 0;
                if (!double.IsNaN(curSessH)) { prevSessH = curSessH; prevSessL = curSessL; DrawAdr(); }
                curSessH = High[0]; curSessL = Low[0];
            }
            else if (!double.IsNaN(curSessH)) { curSessH = Math.Max(curSessH, High[0]); curSessL = Math.Min(curSessL, Low[0]); }
            else { curSessH = High[0]; curSessL = Low[0]; }
            double typ = (High[0] + Low[0] + Close[0]) / 3.0;
            vwapPV += typ * Volume[0]; vwapVol += Volume[0];
            double vwap = vwapVol > 0 ? vwapPV / vwapVol : Close[0];

            // 1H accumulation
            var hk = new DateTime(inBar.Year, inBar.Month, inBar.Day, inBar.Hour, 0, 0);
            if (hk != hourKey) { hourKey = hk; hO = Open[0]; hH = High[0]; hL = Low[0]; hV = Volume[0]; hBars = 1; hStartBar = CurrentBar; }
            else { hH = Math.Max(hH, High[0]); hL = Math.Min(hL, Low[0]); hV += Volume[0]; hBars++; }
            // developing-hour gauge (for the dashboard "measuring" bar)
            hrRangeNow = hH - hL;
            double eR = ExpectedRange(inBar.Hour);
            hrGateNow = FixedGate ? MinHeightPts : Math.Max(RangeMult * (double.IsNaN(eR) ? 0 : eR), MinHeightPts);

            // 15m accumulation
            var mk = new DateTime(inBar.Year, inBar.Month, inBar.Day, inBar.Hour, (inBar.Minute / 15) * 15, 0);
            if (mk != m15Key) { m15Key = mk; m15H = High[0]; m15L = Low[0]; }
            else { m15H = Math.Max(m15H, High[0]); m15L = Math.Min(m15L, Low[0]); }

            bool hourClose = etClose.Minute == 0;
            bool m15Close  = etClose.Minute % 15 == 0;

            // structure (fractal swings → CHoCH/BOS) must run BEFORE qualification so a break in the
            // qualifying hour can tag the grid REVERSAL/CONTINUATION. Gated OFF by default → zero change.
            if (GridPersistence) UpdateStructure(etClose);

            // ═══ :45 PRE-ARM — heads-up 15 min before the hour closes ═══
            if (etClose.Minute == 45 && hBars >= 9 && armedHourKey != hourKey)
            {
                int hodA = inBar.Hour;
                bool inSessA = QualStartHour <= QualEndHour ? (hodA >= QualStartHour && hodA <= QualEndHour) : (hodA >= QualStartHour || hodA <= QualEndHour);
                if (inSessA && hrGateNow > 0 && (hH - hL) >= ArmFrac * hrGateNow)
                {
                    armedHourKey = hourKey;
                    EmitEvent("PREARM", hodA + ":00 developing " + (hH - hL).ToString("F1") + "pts vs " + hrGateNow.ToString("F1") + " gate — watch the :00 close");
                    if (AlertOnQualify && State == State.Realtime)
                        Alert("RFprearm", Priority.Medium,
                            "ROLLING FIB — PRE-ARM: " + Instrument.MasterInstrument.Name + " hour developing "
                            + (hH - hL).ToString("F1") + "pts (gate " + hrGateNow.ToString("F1") + "). Watch the :00 close.",
                            NinjaTrader.Core.Globals.InstallDir + @"sounds\Alert1.wav", 60, Brushes.Goldenrod, Brushes.Black);
                }
            }

            // window expiry
            // track the window's price excursion, then score the OUTCOME when it closes —
            // this is what lets the co-pilot grade whether each setup actually worked
            if (windowActive)
            {
                winHi = double.IsNaN(winHi) ? High[0] : Math.Max(winHi, High[0]);
                winLo = double.IsNaN(winLo) ? Low[0]  : Math.Min(winLo, Low[0]);
            }
            if (windowActive && etClose > winEndEt)
            {
                windowActive = false;
                double reach = drawDir == 1 ? winHi : winLo;      // furthest in grid direction
                double breach = drawDir == 1 ? winLo : winHi;     // furthest against
                string outcome = GtD(reach, L[6]) ? "target — reached the 100" + (GtD(reach, L[7]) ? "/161.8" : "")
                               : GtD(breach, L[0]) ? "invalidated — broke the 0"
                               : GtD(reach, L[4]) ? "partial — reached the 61.8" : "chop — held mid-grid";
                EmitEvent("WINDOW_OUTCOME", (drawDir == 1 ? "BULL" : "BEAR") + " @" + qualCloseLoc.Hour + ":00 → " + outcome);
                winHi = double.NaN; winLo = double.NaN;
                // the trade window ended, but with persistence the grid stays the active map: hunt the fade
                // + midline retests across the non-qualifying hours until an explicit invalidation.
                if (GridPersistence && gridLive) gridPhase = "PERSIST";
            }

            // ═══ QUALIFICATION ═══
            if (hourClose && hBars >= 12)
            {
                int    hod  = inBar.Hour;
                double R    = hH - hL;
                double body = Math.Abs(Close[0] - hO);
                double clvB = R > 0 ? (Close[0] - hL) / R : 0;
                double clvS = R > 0 ? (hH - Close[0]) / R : 0;
                double expR = ExpectedRange(hod);
                bool inSession = QualStartHour <= QualEndHour
                    ? (hod >= QualStartHour && hod <= QualEndHour)
                    : (hod >= QualStartHour || hod <= QualEndHour);
                double gateR = FixedGate ? MinHeightPts : Math.Max(RangeMult * (double.IsNaN(expR) ? 0 : expR), MinHeightPts);
                bool warm   = FixedGate || !double.IsNaN(expR);
                bool hgtOk  = warm && R >= gateR;
                bool bodyOk = R > 0 && body / R >= MinBodyFrac;
                bool isBull = Close[0] > hO && clvB >= MinCLV && (!VwapFilter || Close[0] >= vwap);
                bool isBear = Close[0] < hO && clvS >= MinCLV && (!VwapFilter || Close[0] <= vwap);

                if (inSession && hgtOk && bodyOk && (isBull || isBear))
                {
                    dir = isBull ? 1 : -1; drawDir = dir;
                    double gBase = dir == 1 ? hL : hH;
                    for (int i = 0; i < 8; i++) L[i] = gBase + dir * R * Ratios[i];
                    for (int i = 0; i < 7; i++) M[i] = (L[i] + L[i + 1]) / 2.0;
                    hourStartLoc = Time[0].AddMinutes(-60); qualCloseLoc = Time[0]; windowEndLoc = Time[0].AddMinutes(60);
                    winEndEt = etClose.AddMinutes(60); windowActive = true; pivotIdx = -1; contFired = false;
                    lastQual = (dir == 1 ? "BULL " : "BEAR ") + R.ToString("F1") + "pt @" + hod + ":00";
                    lastBlock = "—";
                    // tag the grid vs. the structure that produced it: an aligned CHoCH in this hour = REVERSAL
                    // (expect the fade off the 100), an aligned BOS = CONTINUATION (trade the impulse).
                    if (GridPersistence)
                    {
                        bool recentBreak = (CurrentBar - lastBreakBar) <= 12 && lastBreakDir == dir;
                        reversalType = recentBreak ? (lastBreakKind == "CHoCH" ? "REVERSAL" : "CONTINUATION") : "NEUTRAL";
                        gridLive = true; gridPhase = "WINDOW"; gridStartEt = etClose; lastTouchEt = etClose;
                        acceptCount = 0; fadeArmed = true; fadePendBar = -1;   // ROLL: a fresh qualify replaces any prior grid
                    }
                    else reversalType = "";
                    DrawGrid(R, hod);
                    EmitEvent("QUALIFIED", lastQual + " grid " + L[0].ToString("F2") + " -> " + L[6].ToString("F2")
                        + (GridPersistence && reversalType.Length > 0 ? " " + reversalType : ""));
                    qualEmitted = (State == State.Realtime);   // false if this fired during a historical reprocess → re-emitted on realtime resume
                    BtLog("{\"type\":\"QUAL\",\"t\":\"" + etClose.ToString("yyyy-MM-dd HH:mm") + "\",\"dir\":" + dir + ",\"hod\":" + hod
                        + ",\"l0\":" + L[0].ToString("F2") + ",\"l6\":" + L[6].ToString("F2") + ",\"l7\":" + L[7].ToString("F2")
                        + ",\"rev\":\"" + (GridPersistence ? reversalType : "") + "\",\"size\":" + R.ToString("F1") + "}");
                    // native alert (sound + Alerts window) — REALTIME only so a chart reload
                    // doesn't replay every historical qualification
                    if (AlertOnQualify && State == State.Realtime)
                        Alert("RFqual", Priority.High,
                            "ROLLING FIB — WINDOW QUALIFIED: " + Instrument.MasterInstrument.Name + " " + lastQual
                            + " (trade window = next hour)",
                            NinjaTrader.Core.Globals.InstallDir + @"sounds\Alert4.wav", 120,
                            Brushes.MediumPurple, Brushes.White);
                }
                else if (inSession)
                {
                    lastBlock = !warm ? "warmup"
                        : !hgtOk ? "height " + R.ToString("F1") + " < " + gateR.ToString("F1")
                        : !bodyOk ? "body " + (R > 0 ? (body / R * 100).ToString("F0") : "0") + "%"
                        : "close-location / VWAP";
                    EmitEvent("BLOCK", hod + ":00 blocked — " + lastBlock);
                }

                // baseline update (for adaptive mode)
                if (rngEma.TryGetValue(hod, out double pr)) rngEma[hod] = pr + SeasAlpha * (Math.Min(R, pr * 2.0) - pr);
                else rngEma[hod] = R;
                if (inSession) { recentRanges.Enqueue(R); while (recentRanges.Count > 14) recentRanges.Dequeue(); }
            }

            // ═══ 15m CONFIRM marker ═══
            if (m15Close && windowActive && etClose <= winEndEt && Time[0] > qualCloseLoc)
            {
                int cand = -1;
                for (int i = 0; i <= 5; i++)
                {
                    bool dipped = dir == 1 ? m15L <= M[i] : m15H >= M[i];
                    if (dipped && GtD(Close[0], M[i])) cand = i;
                }
                if (cand >= 0 && cand != pivotIdx)
                {
                    pivotIdx = cand;
                    Draw.Dot(this, "RFc" + qualCloseLoc.Ticks + "_" + CurrentBar, false, 0, M[cand],
                        cand <= MaxPivotIdx ? Brushes.Gold : Brushes.DimGray);
                    EmitEvent("CONFIRM", (dir == 1 ? "support" : "resistance") + " @ " + LvlName[cand] + "/"
                        + LvlName[cand + 1] + " midline " + M[cand].ToString("F2")
                        + (cand <= MaxPivotIdx ? " [tradeable]" : " [too deep]"));
                    BtLog("{\"type\":\"CONFIRM\",\"t\":\"" + etClose.ToString("yyyy-MM-dd HH:mm") + "\",\"dir\":" + dir
                        + ",\"pivot\":" + cand + ",\"px\":" + M[cand].ToString("F2") + ",\"tradeable\":" + (cand <= MaxPivotIdx ? "true" : "false") + "}");
                }
            }

            // ═══ CONTINUATION-PULLBACK CALLOUT — the :25 / :45 with-trend entry (backtest: +5.4pt/trade @ 70%) ═══
            // On a trend grid, price tags the 100 then pulls back; if the :25 candle CLOSES holding the 50, the trend
            // resumes ~70% for +5pt with the stop under the pullback low. Trigger = the :25 close. If that candle
            // closes DEEPER than the 50 we wait and re-check the :45. Fires once per window; never on a REVERSAL grid
            // (those are the fade). Requires a real pullback into the 55 zone (not a runaway you'd only be chasing).
            if (GridPersistence && windowActive && !contFired && etClose <= winEndEt && Time[0] > qualCloseLoc
                && (etClose.Minute == 25 || etClose.Minute == 45) && reversalType != "REVERSAL"
                && structureDir != -drawDir)   // CHoCH discriminator: if structure has flipped against the trend, it's a reversal, not a continuation
            {
                double pbExt = drawDir == 1 ? winLo : winHi;                                              // pullback extreme = structural stop anchor
                double l55   = L[0] + (L[6] - L[0]) * 0.55;                                               // the 55% retracement (works both directions)
                bool pulledBack = !double.IsNaN(pbExt) && (drawDir == 1 ? pbExt <= l55 : pbExt >= l55);   // price actually retraced into the pullback zone
                if (pulledBack && GtD(Close[0], L[3]))                                                    // …and this candle CLOSED holding the 50
                {
                    contFired = true;
                    // fill is the resting 55 limit, NOT this candle's close — backtest: 55-limit +3.7pt/trade vs close -1.2pt.
                    // the :25/:45 close is the VALIDATION (held the 50), not the entry price.
                    double entry = l55, stop = pbExt - drawDir * 1.0, tgt = entry + drawDir * 5.0;
                    double risk  = Math.Abs(entry - stop);
                    string win   = etClose.Minute == 25 ? ":25" : ":45";
                    EmitEvent("CONTINUATION", (drawDir == 1 ? "LONG" : "SHORT") + " continuation — pullback held the 50 at " + win
                        + " -> resting LIMIT at the 55 @ " + entry.ToString("F2") + " (don't chase), stop " + stop.ToString("F2")
                        + " (" + risk.ToString("F1") + "pt), target " + tgt.ToString("F2") + " (+5pt)");
                    ContAlert(drawDir, entry, stop, tgt);
                    DrawCont(drawDir);
                    BtLog("{\"type\":\"CONT\",\"t\":\"" + etClose.ToString("yyyy-MM-dd HH:mm") + "\",\"dir\":" + drawDir
                        + ",\"entry\":" + entry.ToString("F2") + ",\"stop\":" + stop.ToString("F2") + ",\"tgt\":" + tgt.ToString("F2") + ",\"win\":\"" + win + "\"}");
                }
                // a :25 close deeper than the 50 leaves contFired = false → the :45 re-check gets the next shot
            }

            // persistence: fade trigger (Setup B) + invalidation across the PERSIST hours
            if (GridPersistence && gridLive) MaintainPersistence(etClose);

            WriteState(etClose);   // push live state to the dashboard every bar
            WriteBars();           // stream the real MES bars so the dashboard renders a CONGRUENT chart
        }

        // ── BACKTEST FEED — log the stable forecast layer (qualify + confirm) over ALL history (not realtime-gated),
        //    and dump a long bar history, so the dashboard harness can score :25/:45 entries + fades at ±1R. ──
        private void BtTruncate()
        {
            if (!EmitState) return;
            try { File.WriteAllText(Path.Combine(StatePath, "backtest_" + Instrument.MasterInstrument.Name + ".jsonl"), ""); } catch { }
        }
        private void BtLog(string json)
        {
            if (!EmitState) return;
            try { File.AppendAllText(Path.Combine(StatePath, "backtest_" + Instrument.MasterInstrument.Name + ".jsonl"), json + "\n"); } catch { }
        }
        private void WriteBarsHistory()
        {
            if (!EmitState) return;
            try
            {
                int n = Math.Min(3000, CurrentBar + 1);   // ~30 trading days of 5m bars
                var sb = new StringBuilder(n * 48 + 128);
                sb.Append("{\"instrument\":\"").Append(Instrument.MasterInstrument.Name).Append("\",\"tf\":\"5m\",\"bars\":[");
                for (int i = n - 1; i >= 0; i--)
                {
                    if (i != n - 1) sb.Append(",");
                    DateTime bt = TimeZoneInfo.ConvertTime(Time[i], tzEt);
                    sb.Append("{\"t\":\"").Append(bt.ToString("yyyy-MM-dd HH:mm"))
                      .Append("\",\"o\":").Append(Open[i].ToString("F2")).Append(",\"h\":").Append(High[i].ToString("F2"))
                      .Append(",\"l\":").Append(Low[i].ToString("F2")).Append(",\"c\":").Append(Close[i].ToString("F2")).Append("}");
                }
                sb.Append("]}");
                File.WriteAllText(Path.Combine(StatePath, "bars_history_" + Instrument.MasterInstrument.Name + ".json"), sb.ToString());
            } catch { }
        }

        // ── MES bar feed: the last N 5m bars so the dashboard draws its own candlestick terminal
        //    (same instrument/prices as the radar — no more TradingView proxy mismatch) ──
        private void WriteBars()
        {
            if (!EmitState) return;
            try
            {
                int n = Math.Min(720, CurrentBar + 1);   // ~2.5 trading days of 5m bars on the dashboard chart (was 160 ≈ 13h)
                var sb = new StringBuilder(6144);
                sb.Append("{\"instrument\":\"").Append(Instrument.MasterInstrument.Name)
                  .Append("\",\"tf\":\"5m\",\"updated\":\"").Append(TimeZoneInfo.ConvertTime(DateTime.Now, tzEt).ToString("yyyy-MM-dd HH:mm:ss"))
                  .Append("\",\"bars\":[");
                for (int i = n - 1; i >= 0; i--)
                {
                    if (i != n - 1) sb.Append(",");
                    DateTime bt = TimeZoneInfo.ConvertTime(Time[i], tzEt);
                    sb.Append("{\"t\":\"").Append(bt.ToString("yyyy-MM-dd HH:mm"))
                      .Append("\",\"o\":").Append(Open[i].ToString("F2"))
                      .Append(",\"h\":").Append(High[i].ToString("F2"))
                      .Append(",\"l\":").Append(Low[i].ToString("F2"))
                      .Append(",\"c\":").Append(Close[i].ToString("F2")).Append("}");
                }
                sb.Append("]}");
                File.WriteAllText(Path.Combine(StatePath, "bars_" + Instrument.MasterInstrument.Name + ".json"), sb.ToString());
            } catch { }
        }

        // ── LIVE TICK FEED — push the forming bar + price to the dashboard between 5m closes so the
        //    terminal moves in real time (the qualification logic still runs only on bar close). ──
        protected override void OnMarketData(MarketDataEventArgs marketData)
        {
            if (marketData.MarketDataType != MarketDataType.Last) return;
            if (State != State.Realtime || CurrentBar < 20 || !EmitState) return;
            if ((DateTime.UtcNow - lastTickWrite).TotalMilliseconds < 800) return;   // ~1.25 writes/sec
            lastTickWrite = DateTime.UtcNow;
            DateTime etNow = TimeZoneInfo.ConvertTime(DateTime.Now, tzEt);
            WriteState(etNow);
            WriteBars();
        }

        // ── dashboard feed (Realtime only) ──
        private void EmitEvent(string type, string msg)
        {
            if (!EmitState || State != State.Realtime) return;
            try
            {
                string line = "{\"i\":\"" + Instrument.MasterInstrument.Name + "\",\"type\":\"" + type
                    + "\",\"msg\":\"" + msg.Replace("\"", "'") + "\",\"t\":\""
                    + TimeZoneInfo.ConvertTime(Time[0], tzEt).ToString("yyyy-MM-dd HH:mm") + "\"}\n";
                File.AppendAllText(Path.Combine(StatePath, "events_" + Instrument.MasterInstrument.Name + ".jsonl"), line);
            } catch { }
        }
        // Re-emit a qualification that fired while reprocessing history (mid-session re-add), where EmitEvent was
        // suppressed. Stamps the grid's OWN close time — not "now" — so the dashboard keys the window at the right
        // hour and links your trades correctly. Only for a STILL-LIVE window; its confirm/outcome then emit as they
        // occur in realtime. An already-ended window is left to the server-side reconstruction.
        private void ReemitLiveQual()
        {
            if (!EmitState || State != State.Realtime) return;
            if (!windowActive || qualEmitted || lastQual == "none yet" || L[6] == 0) return;
            try
            {
                string t   = TimeZoneInfo.ConvertTime(qualCloseLoc, tzEt).ToString("yyyy-MM-dd HH:mm");
                string msg = (lastQual + " grid " + L[0].ToString("F2") + " -> " + L[6].ToString("F2")).Replace("\"", "'");
                string line = "{\"i\":\"" + Instrument.MasterInstrument.Name + "\",\"type\":\"QUALIFIED\",\"msg\":\""
                    + msg + "\",\"t\":\"" + t + "\"}\n";
                File.AppendAllText(Path.Combine(StatePath, "events_" + Instrument.MasterInstrument.Name + ".jsonl"), line);
                qualEmitted = true;
            } catch { }
        }
        private void WriteState(DateTime etClose)
        {
            if (!EmitState || State != State.Realtime) return;
            try
            {
                string status = windowActive ? (pivotIdx >= 0 ? "CONFIRMED" : "ACTIVE")
                              : (GridPersistence && gridLive ? "PERSIST" : "IDLE");
                double elapsed = windowActive ? (etClose - qualCloseLoc).TotalMinutes : 0;
                var sb = new StringBuilder(768);
                sb.Append("{\"instrument\":\"").Append(Instrument.MasterInstrument.Name)
                  .Append("\",\"updated\":\"").Append(etClose.ToString("yyyy-MM-dd HH:mm"))
                  .Append("\",\"price\":").Append(Close[0].ToString("F2"))
                  .Append(",\"status\":\"").Append(status)
                  .Append("\",\"dir\":").Append(L[6] != 0 ? drawDir : 0)
                  .Append(",\"elapsed\":").Append(elapsed.ToString("F0"))
                  .Append(",\"pivot\":").Append(pivotIdx)
                  .Append(",\"entries\":0,\"maxEntries\":0,\"accelArmed\":false,\"fadeUsed\":false")
                  .Append(",\"consecL\":0,\"dayPnL\":0.00,\"halt\":false")
                  .Append(",\"block\":\"").Append(lastBlock.Replace("\"", "'"))
                  .Append("\",\"lastQual\":\"").Append(lastQual.Replace("\"", "'")).Append("\"")
                  .Append(",\"hrRange\":").Append(hrRangeNow.ToString("F2"))
                  .Append(",\"hrGate\":").Append(hrGateNow.ToString("F2"))
                  .Append(",\"gridActive\":").Append((GridPersistence ? gridLive : windowActive) ? "true" : "false")
                  // structure/persistence snapshot (neutral when the layer is OFF)
                  .Append(",\"reversalType\":\"").Append(reversalType).Append("\"")
                  .Append(",\"gridPhase\":\"").Append(gridPhase).Append("\"")
                  .Append(",\"structureDir\":").Append(structureDir)
                  .Append(",\"lastStructure\":\"").Append(lastStructure.Replace("\"", "'")).Append("\"")
                  .Append(",\"gridAgeHrs\":").Append((GridPersistence && gridLive ? (etClose - gridStartEt).TotalHours : 0).ToString("F1"))
                  .Append(",\"fadeArmed\":").Append(GridPersistence && fadeArmed ? "true" : "false");
                // emit the last grid ALWAYS (once one exists) so the dashboard mirrors the chart —
                // the live grid when in a window, dimmed as "last window" between windows
                if (L[6] != 0)
                {
                    sb.Append(",\"levels\":[").Append(string.Join(",", L.Select(v => v.ToString("F2"))))
                      .Append("],\"mids\":[").Append(string.Join(",", M.Select(v => v.ToString("F2")))).Append("]");
                }
                sb.Append("}");
                File.WriteAllText(Path.Combine(StatePath, "state_" + Instrument.MasterInstrument.Name + ".json"), sb.ToString());
            } catch { }
        }

        // ═══ STRUCTURE — fractal swings → CHoCH / BOS (objective, no repaint: confirmed SwingStrength bars late) ═══
        private void UpdateStructure(DateTime etClose)
        {
            int Lw = SwingStrength;
            if (CurrentBar < 2 * Lw + 1) return;
            bool isSH = true, isSL = true;
            double ph = High[Lw], pl = Low[Lw];              // candidate pivot = Lw bars ago
            for (int j = 0; j <= 2 * Lw; j++)
            {
                if (j == Lw) continue;
                if (High[j] >= ph) isSH = false;
                if (Low[j]  <= pl) isSL = false;
            }
            // a swing only counts if its leg from the opposite swing is >= MinSwingPts — filters micro-structure noise
            if (isSH && (double.IsNaN(lastSL) || ph - lastSL >= MinSwingPts)) { prevSH = lastSH; lastSH = ph; shBroken = false; RecalcStructureDir(); }
            if (isSL && (double.IsNaN(lastSH) || lastSH - pl >= MinSwingPts)) { prevSL = lastSL; lastSL = pl; slBroken = false; RecalcStructureDir(); }

            // CHoCH (against structure) / BOS (with structure): close must clear the swing by a real margin (not 1 tick),
            // and no OPPOSITE break within 3 bars of the last (kills the micro-swing flip-flop).
            double brk = Math.Max(TickSize, MinSwingPts * 0.33);
            if (!double.IsNaN(lastSH) && !shBroken && Close[0] >= lastSH + brk && !(lastBreakDir == -1 && CurrentBar - lastBreakBar < 3))
            {
                shBroken = true;
                RegisterBreak(structureDir < 0 ? "CHoCH" : "BOS", 1, lastSH);
                structureDir = 1;
            }
            if (!double.IsNaN(lastSL) && !slBroken && Close[0] <= lastSL - brk && !(lastBreakDir == 1 && CurrentBar - lastBreakBar < 3))
            {
                slBroken = true;
                RegisterBreak(structureDir > 0 ? "CHoCH" : "BOS", -1, lastSL);
                structureDir = -1;
            }
        }
        private void RecalcStructureDir()
        {
            if (double.IsNaN(prevSH) || double.IsNaN(prevSL) || double.IsNaN(lastSH) || double.IsNaN(lastSL)) return;
            if (lastSH > prevSH && lastSL > prevSL) structureDir = 1;
            else if (lastSH < prevSH && lastSL < prevSL) structureDir = -1;
        }
        private void RegisterBreak(string kind, int bdir, double level)
        {
            lastBreakKind = kind; lastBreakDir = bdir; lastBreakBar = CurrentBar;
            lastStructure = kind + (bdir == 1 ? " up" : " down");
            EmitEvent("STRUCTURE", kind + " " + (bdir == 1 ? "up" : "down") + " — close " + Close[0].ToString("F2") + " broke " + level.ToString("F2"));
            BtLog("{\"type\":\"STRUCTURE\",\"t\":\"" + TimeZoneInfo.ConvertTime(Time[0], tzEt).ToString("yyyy-MM-dd HH:mm") + "\",\"kind\":\"" + kind
                + "\",\"dir\":" + bdir + ",\"level\":" + level.ToString("F2") + ",\"close\":" + Close[0].ToString("F2") + "}");
            Brush b = kind == "CHoCH" ? GxChoch : GxBos;
            string g = "STR" + CurrentBar + kind;
            Draw.Text(this, g, false, kind + (bdir == 1 ? " ▲" : " ▼"), 0,
                bdir == 1 ? High[0] + 3 * TickSize : Low[0] - 3 * TickSize, 0,
                b, new SimpleFont("Consolas", 9), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
        }

        // ═══ PERSISTENCE — fade trigger (Setup B) + invalidation across the non-qualifying hours ═══
        private void MaintainPersistence(DateTime etClose)
        {
            double gTop = Math.Max(L[0], L[7]), gBot = Math.Min(L[0], L[7]);
            if (Low[0] <= gTop && High[0] >= gBot) lastTouchEt = etClose;      // touch of the grid band
            double ageHrs = (etClose - gridStartEt).TotalHours;
            double noTouchHrs = (etClose - lastTouchEt).TotalHours;
            double t100 = L[6], t162 = L[7], origin = L[0];

            // ── FADE (Setup B) — sweep the target extreme, reject back inside, filtered for quality ──
            // 1) resolve a pending fade: fire once the reject HELD FadeConfirmBars bars (sweep extreme not breached)
            if (fadePendBar >= 0)
            {
                bool blown = fadePendDir == 1 ? (Low[0] < fadePendExt - TickSize) : (High[0] > fadePendExt + TickSize);
                if (blown) fadePendBar = -1;                                          // sweep extended ⇒ not the reject, cancel
                else if (CurrentBar - fadePendBar >= FadeConfirmBars)
                {
                    bool ap = fadePendDir == 1 ? (!double.IsNaN(prevSessL) && Math.Abs(fadePendSwept - prevSessL) <= 4 * TickSize)
                                               : (!double.IsNaN(prevSessH) && Math.Abs(fadePendSwept - prevSessH) <= 4 * TickSize);
                    string ln = Math.Abs(fadePendSwept - L[7]) < TickSize ? "161.8" : "100";
                    EmitEvent("FADE", (fadePendDir == 1 ? "LONG" : "SHORT") + " fade — swept the " + ln + " (" + fadePendSwept.ToString("F2") + ") and confirmed" + (ap ? (fadePendDir == 1 ? " [A+ prior-session low]" : " [A+ prior-session high]") : ""));
                    DrawFade(fadePendDir, ap); FadeAlert(fadePendDir); fadeArmed = false; lastFadeEt = etClose; fadePendBar = -1;
                    BtLog("{\"type\":\"FADE\",\"t\":\"" + etClose.ToString("yyyy-MM-dd HH:mm") + "\",\"dir\":" + fadePendDir + ",\"swept\":" + fadePendSwept.ToString("F2") + ",\"px\":" + Close[0].ToString("F2") + "}");
                }
            }
            // 2) detect a NEW qualified sweep-reject (Wyckoff wick + trend guard + reversalType gate)
            if (fadeArmed && fadePendBar < 0)
            {
                int fdir = 0; double swept = double.NaN, ext = 0;
                if (drawDir == -1)     { swept = Low[0]  <= t162 - TickSize ? t162 : (Low[0]  <= t100 - TickSize ? t100 : double.NaN); if (!double.IsNaN(swept) && Close[0] > swept) { fdir = 1;  ext = Low[0];  } }
                else if (drawDir == 1) { swept = High[0] >= t162 + TickSize ? t162 : (High[0] >= t100 + TickSize ? t100 : double.NaN); if (!double.IsNaN(swept) && Close[0] < swept) { fdir = -1; ext = High[0]; } }
                if (fdir != 0)
                {
                    double range = High[0] - Low[0];
                    double wick  = fdir == 1 ? (Math.Min(Open[0], Close[0]) - Low[0]) : (High[0] - Math.Max(Open[0], Close[0]));
                    bool wickOk  = range > 0 && wick >= 0.45 * range;                                                    // Wyckoff rejection wick
                    bool trendOk = !(lastBreakKind == "BOS" && lastBreakDir == -fdir && CurrentBar - lastBreakBar <= 6);  // not against a fresh BOS
                    bool ap      = fdir == 1 ? (!double.IsNaN(prevSessL) && Math.Abs(swept - prevSessL) <= 4 * TickSize)
                                             : (!double.IsNaN(prevSessH) && Math.Abs(swept - prevSessH) <= 4 * TickSize);
                    bool typeOk  = !FadeReversalOnly || reversalType == "REVERSAL" || (reversalType == "NEUTRAL" && ap);  // never fade CONTINUATION
                    if (wickOk && trendOk && typeOk)
                    {
                        string ln = Math.Abs(swept - t162) < TickSize ? "161.8" : "100";
                        if (FadeConfirmBars <= 0)
                        {
                            EmitEvent("FADE", (fdir == 1 ? "LONG" : "SHORT") + " fade — swept the " + ln + " (" + swept.ToString("F2") + ") and rejected" + (ap ? (fdir == 1 ? " [A+ prior-session low]" : " [A+ prior-session high]") : ""));
                            DrawFade(fdir, ap); FadeAlert(fdir); fadeArmed = false; lastFadeEt = etClose;
                            BtLog("{\"type\":\"FADE\",\"t\":\"" + etClose.ToString("yyyy-MM-dd HH:mm") + "\",\"dir\":" + fdir + ",\"swept\":" + swept.ToString("F2") + ",\"px\":" + Close[0].ToString("F2") + "}");
                        }
                        else { fadePendBar = CurrentBar; fadePendDir = fdir; fadePendSwept = swept; fadePendExt = ext; }
                    }
                }
            }
            // 3) re-arm once price pulls back toward equilibrium (only when nothing is pending)
            if (!fadeArmed && fadePendBar < 0 && (drawDir == -1 ? Close[0] > L[5] : Close[0] < L[5])) fadeArmed = true;

            bool beyond162 = drawDir == -1 ? Close[0] < t162 : Close[0] > t162;
            acceptCount = beyond162 ? acceptCount + 1 : 0;

            string reason = null;
            if (acceptCount >= 2) reason = "acceptance beyond 161.8";
            else if (drawDir == -1 ? Close[0] > origin : Close[0] < origin) reason = "origin break";
            else if (lastBreakKind == "CHoCH" && lastBreakDir == -drawDir && (CurrentBar - lastBreakBar) <= 1) reason = "opposing CHoCH";
            else if (noTouchHrs >= TimeDecayHrs) reason = "time decay (" + TimeDecayHrs + "h no touch)";
            else if (ageHrs >= PersistMaxHrs && noTouchHrs >= 1) reason = "max persist age";
            if (reason != null)
            {
                EmitEvent("INVALIDATE", (drawDir == 1 ? "BULL" : "BEAR") + " @" + qualCloseLoc.Hour + ":00 grid invalidated — " + reason);
                gridLive = false; gridPhase = "IDLE"; fadeArmed = false; fadePendBar = -1;
                if (gridHist.Count > 0) DimGrid(gridHist[gridHist.Count - 1]);   // dim the dead grid immediately (kept whole for review)
            }
        }
        // NT-native audible BUY/SELL alert when a fade fires live (uses the AlertOnQualify toggle; realtime only)
        private void FadeAlert(int fdir)
        {
            if (!AlertOnQualify || State != State.Realtime) return;
            try { Alert("RFfade", Priority.High,
                "ROLLING FIB — " + (fdir == 1 ? "BUY" : "SELL") + " (fade) " + Instrument.MasterInstrument.Name + " @ " + Close[0].ToString("F2"),
                NinjaTrader.Core.Globals.InstallDir + @"sounds\Alert2.wav", 30, fdir == 1 ? Brushes.SeaGreen : Brushes.Firebrick, Brushes.White); } catch { }
        }
        private void DrawFade(int fdir, bool aplus)
        {
            string g = "FADE" + CurrentBar;
            Brush b = aplus ? GxGold : (fdir == 1 ? GxBull : GxBear);
            if (fdir == 1) Draw.ArrowUp(this, g, false, 0, Low[0] - 4 * TickSize, b);
            else           Draw.ArrowDown(this, g, false, 0, High[0] + 4 * TickSize, b);
            Draw.Text(this, g + "t", false, aplus ? "FADE A+" : "fade", 0,
                fdir == 1 ? Low[0] - 8 * TickSize : High[0] + 8 * TickSize, 0,
                b, new SimpleFont("Consolas", 9), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
        }
        // NT-native audible BUY/SELL for the continuation callout (uses the AlertOnQualify toggle; realtime only)
        private void ContAlert(int cdir, double entry, double stop, double tgt)
        {
            if (!AlertOnQualify || State != State.Realtime) return;
            try { Alert("RFcont", Priority.High,
                "ROLLING FIB — " + (cdir == 1 ? "BUY" : "SELL") + " (continuation) " + Instrument.MasterInstrument.Name
                + " @ " + entry.ToString("F2") + "  stop " + stop.ToString("F2") + "  tgt " + tgt.ToString("F2"),
                NinjaTrader.Core.Globals.InstallDir + @"sounds\Alert2.wav", 30, cdir == 1 ? Brushes.SeaGreen : Brushes.Firebrick, Brushes.White); } catch { }
        }
        private void DrawCont(int cdir)
        {
            string g = "CONT" + CurrentBar;
            Brush b = cdir == 1 ? GxBull : GxBear;   // BUY/SELL colors; TRIANGLE shape distinguishes it from the fade's arrow
            if (cdir == 1) Draw.TriangleUp(this, g, false, 0, Low[0] - 10 * TickSize, b);
            else           Draw.TriangleDown(this, g, false, 0, High[0] + 10 * TickSize, b);
            Draw.Text(this, g + "t", false, cdir == 1 ? "CONT ▲" : "CONT ▼", 0,
                cdir == 1 ? Low[0] - 14 * TickSize : High[0] + 14 * TickSize, 0,
                b, new SimpleFont("Consolas", 9), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
        }

        private void DrawAdr()
        {
            if (!ShowADR || double.IsNaN(prevSessH)) return;
            if (adrBrush == null) { adrBrush = new SolidColorBrush(Color.FromArgb(70, 150, 165, 190)); adrBrush.Freeze(); }
            double mid = (prevSessH + prevSessL) / 2.0;
            string g = "ADR" + CurrentBar;
            Draw.Line(this, g + "H", false, 0, prevSessH, -84, prevSessH, adrBrush, DashStyleHelper.Dot, 1);
            Draw.Line(this, g + "L", false, 0, prevSessL, -84, prevSessL, adrBrush, DashStyleHelper.Dot, 1);
            Draw.Line(this, g + "M", false, 0, mid, -84, mid, adrBrush, DashStyleHelper.Dash, 1);
            Draw.Text(this, g + "Ht", false, "PDH", -84, prevSessH, 0, adrBrush, new SimpleFont("Consolas", 9),
                      TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
        }

        // the adjustable dimmer: historical grids repaint at HistoryDim % opacity (cached, rebuilt only when it changes)
        private Brush DimBrush()
        {
            double a = Math.Max(0.03, Math.Min(0.60, HistoryDim / 100.0));
            if (dimBrush == null || Math.Abs(dimBrushAt - a) > 1e-6) { dimBrush = GB(245, 240, 232, a); dimBrushAt = a; }
            return dimBrush;
        }
        // redraw a finished grid DIM but WHOLE — the dashboard keeps history to scroll back through, so we do too.
        // strip the labels / midlines / boxes / zones (declutter), then repaint the 8 level lines at the dimmer setting.
        private void DimGrid(GridSnap s)
        {
            string g = s.Tag; Brush dim = DimBrush();
            RemoveDrawObject(g + "qb"); RemoveDrawObject(g + "wb"); RemoveDrawObject(g + "t1"); RemoveDrawObject(g + "t2");
            RemoveDrawObject(g + "tag"); RemoveDrawObject(g + "rt"); RemoveDrawObject(g + "prem"); RemoveDrawObject(g + "disc"); RemoveDrawObject(g + "l55");
            for (int i = 0; i < 8; i++) RemoveDrawObject(g + "t" + i);
            for (int i = 0; i < 7; i++) RemoveDrawObject(g + "m" + i);
            for (int i = 0; i < 8; i++)
                Draw.Line(this, g + "l" + i, false, s.Start, s.Lv[i], s.End, s.Lv[i], dim, DashStyleHelper.Solid, (i == 0 || i == 6) ? 2 : 1);
        }
        // fully remove a grid (the oldest, rolling off the history cap)
        private void PurgeGrid(string g)
        {
            RemoveDrawObject(g + "qb"); RemoveDrawObject(g + "wb"); RemoveDrawObject(g + "t1"); RemoveDrawObject(g + "t2");
            RemoveDrawObject(g + "tag"); RemoveDrawObject(g + "rt"); RemoveDrawObject(g + "prem"); RemoveDrawObject(g + "disc"); RemoveDrawObject(g + "l55");
            for (int i = 0; i < 8; i++) { RemoveDrawObject(g + "l" + i); RemoveDrawObject(g + "t" + i); }
            for (int i = 0; i < 7; i++) RemoveDrawObject(g + "m" + i);
        }

        private void DrawGrid(double R, int hod)
        {
            string g = "RF" + qualCloseLoc.Ticks;
            DateTime gridEnd = GridPersistence ? qualCloseLoc.AddHours(PersistMaxHrs) : windowEndLoc;
            // ── HISTORY: dim the just-finished grid to a quiet WHOLE skeleton (not a stripped one), then keep up to
            //    HistoryGrids of them so the operator can scroll back and review every past grid — exactly like the dashboard. ──
            if (KeepHistory)
            {
                if (gridHist.Count > 0) DimGrid(gridHist[gridHist.Count - 1]);
                gridHist.Add(new GridSnap { Tag = g, Lv = (double[])L.Clone(), Start = hourStartLoc, End = gridEnd });
                while (gridHist.Count > HistoryGrids) { PurgeGrid(gridHist[0].Tag); gridHist.RemoveAt(0); }
            }
            else if (prevGridTag != null && prevGridTag != g) PurgeGrid(prevGridTag);
            prevGridTag = g;

            Brush acc = dir == 1 ? GxTeal : GxBear;
            double mid50 = L[3], zTop = Math.Max(L[0], L[6]), zBot = Math.Min(L[0], L[6]);
            // premium / discount shading (the dashboard's signature), keyed off ABSOLUTE price so it's correct both
            // directions: upper half above the 50 = premium (red), lower half = discount (green)
            Draw.Rectangle(this, g + "prem", false, hourStartLoc, mid50, gridEnd, zTop, Brushes.Transparent, GxBear, 6);
            Draw.Rectangle(this, g + "disc", false, hourStartLoc, zBot,  gridEnd, mid50, Brushes.Transparent, GxBull, 6);
            // qualifying-hour box (violet) + trade-window box (directional)
            Draw.Rectangle(this, g + "qb", false, hourStartLoc, hL, qualCloseLoc, hH, Brushes.Transparent, GxViolet, 14);
            Draw.Rectangle(this, g + "wb", false, qualCloseLoc, hL, windowEndLoc, hH, Brushes.Transparent, acc, 8);
            if (ShowTrigWin)
            {
                double gTop = Math.Max(L[0], L[7]), gBot = Math.Min(L[0], L[7]);
                Draw.Rectangle(this, g + "t1", false, qualCloseLoc.AddMinutes(20), gTop, qualCloseLoc.AddMinutes(30), gBot, Brushes.Transparent, GxGold, 9);
                Draw.Rectangle(this, g + "t2", false, qualCloseLoc.AddMinutes(40), gTop, qualCloseLoc.AddMinutes(50), gBot, Brushes.Transparent, GxGold, 9);
            }
            for (int i = 0; i < 8; i++)
            {
                Brush b = (i == 0 || i == 6) ? GxBound : (i == 7 ? GxBos : GxLine);
                Draw.Line(this, g + "l" + i, false, hourStartLoc, L[i], gridEnd, L[i], b, DashStyleHelper.Solid, (i == 0 || i == 6) ? 2 : 1);
                Draw.Text(this, g + "t" + i, false, LvlName[i] + "  " + L[i].ToString("F2"), windowEndLoc, L[i], 0,
                          GxLbl, new SimpleFont("Consolas", 9), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
            }
            for (int i = 0; i < 7; i++)
                Draw.Line(this, g + "m" + i, false, hourStartLoc, M[i], gridEnd, M[i], GxMid, DashStyleHelper.Dash, 1);
            // the 55 — the continuation entry level — dotted gold so the trade level is unmistakable on the chart you trade from
            double l55 = L[0] + (L[6] - L[0]) * 0.55;
            Draw.Line(this, g + "l55", false, hourStartLoc, l55, gridEnd, l55, GxGold, DashStyleHelper.Dot, 1);
            Draw.Text(this, g + "tag", false, (dir == 1 ? "BULL " : "BEAR ") + R.ToString("F1") + "pt @" + hod + ":00",
                      hourStartLoc, dir == 1 ? hH : hL, dir == 1 ? 1 : -1, acc,
                      new SimpleFont("Consolas", 10), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
            // reversalType label — the dashboard's teal "continuation" / gold "reversal" / muted "neutral" tag. This is the
            // context that says trade-WITH vs FADE, now on the chart you trade from. (Only set when GridPersistence is on.)
            if (reversalType.Length > 0)
            {
                Brush rtB = reversalType == "CONTINUATION" ? GxTeal : reversalType == "REVERSAL" ? GxGold : GxLbl;
                string rtT = reversalType == "CONTINUATION" ? "→ continuation" : reversalType == "REVERSAL" ? "↺ reversal" : "neutral";
                Draw.Text(this, g + "rt", false, rtT, hourStartLoc, Math.Max(L[0], L[7]) + 2 * TickSize, 0, rtB,
                          new SimpleFont("Consolas", 10), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
            }
        }

        #region Properties
        [NinjaScriptProperty, Range(0, 23), Display(Name = "First qualifying hour [ET]", GroupName = "Qualification", Order = 1)]
        public int QualStartHour { get; set; }
        [NinjaScriptProperty, Range(0, 23), Display(Name = "Last qualifying hour [ET]", GroupName = "Qualification", Order = 2)]
        public int QualEndHour { get; set; }
        [NinjaScriptProperty, Range(1.0, 200.0), Display(Name = "Qualifying height [pts] (MES 10)", GroupName = "Qualification", Order = 3)]
        public double MinHeightPts { get; set; }
        [NinjaScriptProperty, Display(Name = "Fixed gate (flat abs pts)", GroupName = "Qualification", Order = 4)]
        public bool FixedGate { get; set; }
        [NinjaScriptProperty, Range(1.0, 3.0), Display(Name = "Adaptive gate multiple [x]", GroupName = "Qualification", Order = 5)]
        public double RangeMult { get; set; }
        [NinjaScriptProperty, Range(0.0, 1.0), Display(Name = "Min body/range [frac]", GroupName = "Qualification", Order = 6)]
        public double MinBodyFrac { get; set; }
        [NinjaScriptProperty, Range(0.0, 1.0), Display(Name = "Min close-location [frac]", GroupName = "Qualification", Order = 7)]
        public double MinCLV { get; set; }
        [NinjaScriptProperty, Display(Name = "VWAP direction gate", GroupName = "Qualification", Order = 8)]
        public bool VwapFilter { get; set; }
        [NinjaScriptProperty, Range(3, 30), Display(Name = "Seasonal EMA length [days]", GroupName = "Qualification", Order = 9)]
        public int SeasLenDays { get; set; }
        [NinjaScriptProperty, Range(0, 5), Display(Name = "Deepest tradeable pivot [0-5]", GroupName = "Qualification", Order = 10)]
        public int MaxPivotIdx { get; set; }

        [NinjaScriptProperty, Display(Name = "Frame prior-day ADR (H/Mid/L)", GroupName = "Display", Order = 1)]
        public bool ShowADR { get; set; }
        [NinjaScriptProperty, Display(Name = "Shade trigger windows (:25 & :45)", GroupName = "Display", Order = 2)]
        public bool ShowTrigWin { get; set; }
        [NinjaScriptProperty, Display(Name = "Keep historic grids", GroupName = "Display", Order = 3)]
        public bool KeepHistory { get; set; }
        [NinjaScriptProperty, Range(1, 40), Display(Name = "Historical grids to keep", GroupName = "Display", Order = 4)]
        public int HistoryGrids { get; set; }
        [NinjaScriptProperty, Range(3.0, 60.0), Display(Name = "Historical grid dimmer [%]", GroupName = "Display", Order = 5)]
        public double HistoryDim { get; set; }

        [NinjaScriptProperty, Display(Name = "Alert (sound + popup) on qualify", GroupName = "Dashboard", Order = 0)]
        public bool AlertOnQualify { get; set; }
        [NinjaScriptProperty, Range(0.5, 1.0), Display(Name = ":45 pre-arm fraction of gate", GroupName = "Dashboard", Order = 3)]
        public double ArmFrac { get; set; }
        [NinjaScriptProperty, Display(Name = "Feed dashboard (live state)", GroupName = "Dashboard", Order = 1)]
        public bool EmitState { get; set; }
        [Display(Name = "State folder", GroupName = "Dashboard", Order = 2)]
        public string StatePath { get; set; }

        [NinjaScriptProperty, Display(Name = "Grid persistence (CHoCH/BOS + fade)", GroupName = "Structure", Order = 1)]
        public bool GridPersistence { get; set; }
        [NinjaScriptProperty, Range(1, 5), Display(Name = "Swing fractal strength [bars]", GroupName = "Structure", Order = 2)]
        public int SwingStrength { get; set; }
        [NinjaScriptProperty, Range(1, 8), Display(Name = "Time-decay expiry [hrs, no touch]", GroupName = "Structure", Order = 3)]
        public int TimeDecayHrs { get; set; }
        [NinjaScriptProperty, Range(1, 12), Display(Name = "Max persist age [hrs]", GroupName = "Structure", Order = 4)]
        public int PersistMaxHrs { get; set; }
        [NinjaScriptProperty, Range(0, 3), Display(Name = "Fade confirm bars (0 = instant)", GroupName = "Structure", Order = 5)]
        public int FadeConfirmBars { get; set; }
        [NinjaScriptProperty, Display(Name = "Fade only REVERSAL grids", GroupName = "Structure", Order = 6)]
        public bool FadeReversalOnly { get; set; }
        [NinjaScriptProperty, Range(0.0, 20.0), Display(Name = "Min swing size [pts]", GroupName = "Structure", Order = 7)]
        public double MinSwingPts { get; set; }
        #endregion
    }
}
