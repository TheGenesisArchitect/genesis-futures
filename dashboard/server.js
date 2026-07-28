// Rolling Fib dashboard server — Node stdlib only.
//   node server.js            → http://localhost:8787
// Feeds:
//   · NinjaTrader live: reads state_*.json / events_*.jsonl from the live folder
//   · TradingView live: POST /webhook with the strategy's JSON alert body
//     (TradingView webhooks originate from the cloud — expose this port with
//      `ngrok http 8787` and use the ngrok URL + /webhook in the alert)
//   · TradingView backtests: drop "List of Trades" CSV exports into the
//     backtests folder — parsed and rendered automatically.
"use strict";
const http = require("http");
const https = require("https");
const fs = require("fs");
const path = require("path");

const LIVE = "C:/Users/mrbee/Documents/RollingFib/live";
const BT   = "C:/Users/mrbee/Documents/RollingFib/backtests";
const IS_SIM = a => /sim|playback/i.test(a || "");   // classify an account name as Sim/Playback vs live money
const HISTORY = path.join(LIVE, "history");   // per-day archived score cards for the calendar
try { fs.mkdirSync(HISTORY, { recursive: true }); } catch { }
// LOCAL (ET) trading day — matches the indicator's ET-keyed events; UTC would roll over in the evening
const LOCAL_DAY = d => d.getFullYear() + "-" + String(d.getMonth() + 1).padStart(2, "0") + "-" + String(d.getDate()).padStart(2, "0");
// snapshot each day's scored windows so past days can be paged back through
let _lastHistWrite = 0;
function writeHistory(windows) {
  const nowMs = Date.now();
  if (nowMs - _lastHistWrite < 15000) return;   // archive at most every 15s — the faster poll/SSE must not churn the disk
  _lastHistWrite = nowMs;
  try {
    const byDay = {};
    for (const w of windows || []) if (w.day) (byDay[w.day] = byDay[w.day] || []).push(w);
    for (const day in byDay)
      fs.writeFileSync(path.join(HISTORY, day + ".json"), JSON.stringify({ day, windows: byDay[day] }));
  } catch { }
}
const PAGE = path.join(__dirname, "dashboard.html");
const PORT = 8787;
for (const d of [LIVE, BT]) { try { fs.mkdirSync(d, { recursive: true }); } catch { } }

const ACCT_HIDE = /^(sim|backtest|playback|replay|demo|test)/i;
// live-testing accounts that LOOK like practice (Sim101 etc.) but are traded for real evaluation —
// these override ACCT_HIDE so they appear active in the panel.
const ACCT_SHOW = /^sim(101|01)$/i;
const acctVisible = name => !!name && (!ACCT_HIDE.test(name) || ACCT_SHOW.test(name));
const acctHist = {};   // module-level: account -> [{t, p}] intraday P&L trail for drawdown KPIs
const priceHist = {};  // instrument -> [{t, p}] so the co-pilot reads the trend even before a window qualifies

// ── live state / events ──
function readLive() {
  const out = { states: [], events: [], allEvents: [], accounts: [], bars: {} };
  let files = [];
  try { files = fs.readdirSync(LIVE); } catch { return out; }
  for (const f of files) {
    const p = path.join(LIVE, f);
    try {
      if (f.startsWith("account_") && f.endsWith(".json"))
        out.accounts.push(JSON.parse(fs.readFileSync(p, "utf8")));
      else if (f.startsWith("bars_") && f.endsWith(".json")) {
        const b = JSON.parse(fs.readFileSync(p, "utf8")); if (b && b.instrument) out.bars[b.instrument] = b;
      }
      else if (f.startsWith("state_") && f.endsWith(".json"))
        out.states.push(JSON.parse(fs.readFileSync(p, "utf8")));
      else if (f.startsWith("events_") && f.endsWith(".jsonl")) {
        const inst = f.slice(7, -6);
        let seq = 0;
        for (const ln of fs.readFileSync(p, "utf8").trim().split("\n").slice(-400)) {
          try { const e = JSON.parse(ln); e.i = e.i || inst; e._seq = seq++; out.allEvents.push(e); } catch { }
        }
      }
    } catch { }
  }
  out.allEvents.sort((a, b) => (a.t < b.t ? 1 : -1));   // newest first
  out.events = out.allEvents.slice(0, 80);               // feed
  out.states.sort((a, b) => (a.instrument > b.instrument ? 1 : -1));
  out.accounts.sort((a, b) => (a.account > b.account ? 1 : -1));
  const rc = readReportCard();                 // unified trade log (CSV + auto-captured) — report card AND window linking
  const rcTrades = rc ? rc.trades : [];
  if (rc) delete rc.trades;                     // keep the client report-card payload lean
  out.reportCard = rc;
  // EDGE from the unified trade set so today updates live with zero manual export; Live-Trades CSV is the fallback.
  // Split by account class (Live Apex vs Sim/Playback) so the trader/Aries can evaluate real-money edge separately.
  let mt;
  if (rcTrades && rcTrades.length) {
    const live = rcTrades.filter(t => !IS_SIM(t.acct)), sim = rcTrades.filter(t => IS_SIM(t.acct));
    mt = Object.assign(edgeWindows(rcTrades), { file: (rc && rc.file) || "auto-capture",
      acctWindows: { all: edgeWindows(rcTrades).windows, live: live.length ? edgeWindows(live).windows : null, sim: sim.length ? edgeWindows(sim).windows : null },
      trades: rcTrades.map(t => ({ day: t.day, hr: t.hour, side: t.side, pnl: +(+t.pnl).toFixed(2), inst: t.inst })) });
  } else { mt = readMyTrading(); if (mt) mt.acctWindows = { all: mt.windows, live: null, sim: null }; }
  out.copilot = buildCopilot(out.allEvents, out.accounts, out.states, mt, rcTrades);
  // chart marks — ALL structure/fade signals (not capped at the 80-event feed) so every BUY/SELL + CHoCH/BOS
  // shows wherever you pan/zoom, current or historic. Merge the realtime feed with the backtest log's history.
  const markSet = new Map();
  for (const e of out.allEvents) if (e.type === "STRUCTURE" || e.type === "FADE" || e.type === "CONTINUATION") markSet.set(e.i + e.type + e.t + e.msg, { i: e.i, type: e.type, t: e.t, msg: e.msg });
  try { const d = readBacktestData();
    if (d) { for (const s of d.structures) { const msg = s.kind + " " + (s.dir === 1 ? "up" : "down") + " — close " + s.close.toFixed(2) + " broke " + s.level.toFixed(2); markSet.set("MES" + "STRUCTURE" + s.t + msg, { i: "MES", type: "STRUCTURE", t: s.t, msg }); }
      for (const f of d.fades) { const msg = (f.dir === 1 ? "LONG" : "SHORT") + " fade — swept the 100 (" + f.swept.toFixed(2) + ") and rejected"; markSet.set("MES" + "FADE" + f.t + msg, { i: "MES", type: "FADE", t: f.t, msg }); } }
  } catch (e) { }
  out.marks = [...markSet.values()];
  writeHistory(out.copilot.windows);   // archive the day's cards for the calendar
  delete out.allEvents;
  return out;
}

// ═══ CO-PILOT BRAIN — score every window, read the regime, coach the next one ═══
function buildCopilot(events, accounts, states, myTrading, rcTrades) {
  const today = LOCAL_DAY(new Date());
  // group into windows (a QUALIFIED opens one; events until the next belong to it)
  const chron = events.slice().sort((a, b) => (a.t < b.t ? -1 : a.t > b.t ? 1 : (a._seq || 0) - (b._seq || 0)));
  const byInst = {};
  for (const e of chron) (byInst[e.i] = byInst[e.i] || []).push(e);
  const windows = [];
  for (const inst in byInst) {
    let cur = null;
    for (const e of byInst[inst]) {
      if (e.type === "QUALIFIED") {
        if (cur) windows.push(cur);
        const m = /(BULL|BEAR)\s+([\d.]+)pt\s+@(\d+):00/.exec(e.msg || "");
        cur = { inst, t: e.t, day: (e.t || "").slice(0, 10), hour: m ? +m[3] : null, dir: m ? m[1] : "?",
                size: m ? +m[2] : 0, confirms: [], entries: 0, outcome: null, fades: [], conts: [], structures: [] };
        // reconstruct the fib grid from "grid L0 -> L6" so the spotlight can show the levels
        const g = /grid\s+([\d.]+)\s*->\s*([\d.]+)/.exec(e.msg || "");
        if (g) { const l0 = +g[1], l6 = +g[2];
          cur.levels = RATIOS.map(r => +(l0 + (l6 - l0) * r).toFixed(2));
          cur.mids = cur.levels.slice(0, 7).map((v, k) => +((v + cur.levels[k + 1]) / 2).toFixed(2)); }
        // reversalType rides in the QUALIFIED payload so PAST windows get the read (CHoCH=REVERSAL grid, BOS=CONTINUATION)
        const rt = /\b(REVERSAL|CONTINUATION|NEUTRAL)\b/.exec(e.msg || ""); if (rt) cur.reversalType = rt[1];
      } else if (cur) {
        if (e.type === "CONFIRM") { cur.confirms.push(/tradeable/i.test(e.msg || ""));
          const cm = /midline\s+([\d.]+)/.exec(e.msg || ""); if (cm) cur.confirmLvl = +cm[1];
          if (cur.mids && cur.confirmLvl) cur.pivot = cur.mids.reduce((bi, v, k) => Math.abs(v - cur.confirmLvl) < Math.abs(cur.mids[bi] - cur.confirmLvl) ? k : bi, 0);
        }
        else if (e.type === "ENTRY") cur.entries++;
        else if (e.type === "WINDOW_OUTCOME") cur.outcome = (e.msg || "").split("→").pop().trim();
        else if (e.type === "FADE") cur.fades.push({ t: e.t, msg: e.msg || "", side: /^LONG/.test(e.msg || "") ? "long" : "short", aplus: /A\+/.test(e.msg || "") });
        else if (e.type === "CONTINUATION") cur.conts.push({ t: e.t, msg: e.msg || "", side: /^LONG/.test(e.msg || "") ? "long" : "short" });
        else if (e.type === "STRUCTURE") cur.structures.push({ t: e.t, msg: e.msg || "", kind: /CHoCH/.test(e.msg || "") ? "CHoCH" : "BOS" });
        else if (e.type === "INVALIDATE") { cur.invalidatedAt = e.t; cur.invalidReason = (e.msg || "").split("—").pop().trim(); }
      }
    }
    if (cur) windows.push(cur);
  }
  // RESILIENCE — a re-added indicator reprocesses history, and its QUALIFIED emit is realtime-only, so a
  // qualification that lands during that reload paints the grid (state.lastQual + levels) but drops the
  // event. That silently erases the window from the score cards (seen with the evening BULL @20:00 that hit
  // target yet never scored). Reconstruct it from the live state so a mid-session re-add never loses a window.
  for (const st of (states || [])) {
    const lq = /(BULL|BEAR)\s+([\d.]+)pt\s+@(\d+):00/.exec(st.lastQual || "");
    if (!lq) continue;
    const inst = st.instrument, qualHour = +lq[3], closeHour = (qualHour + 1) % 24;
    const day = ((st.updated || "").slice(0, 10)) || today;
    if (windows.some(w => w.inst === inst && w.day === day && w.hour === qualHour)) continue; // already event-scored
    const w = { inst, t: day + " " + String(closeHour).padStart(2, "0") + ":00", day, hour: qualHour,
                dir: lq[1], size: +lq[2], confirms: [], entries: 0, outcome: null, fades: [], conts: [], structures: [], reconstructed: true };
    if (st.reversalType) w.reversalType = st.reversalType;
    if (st.levels && st.levels.length >= 8) {
      w.levels = st.levels.slice();
      w.mids = (st.mids && st.mids.length >= 7) ? st.mids.slice()
             : w.levels.slice(0, 7).map((v, k) => +((v + w.levels[k + 1]) / 2).toFixed(2));
    }
    if (st.pivot != null) w.pivot = st.pivot;
    // attach the orphaned confirm/entry/outcome events from the trade window (closeHour .. closeHour+1)
    for (const e of (byInst[inst] || [])) {
      if ((e.t || "").slice(0, 10) !== day) continue;
      const eh = +(e.t || "").slice(11, 13);
      if (eh < closeHour || eh > closeHour + 1) continue;
      if (e.type === "CONFIRM") { w.confirms.push(/tradeable/i.test(e.msg || ""));
        const cm = /midline\s+([\d.]+)/.exec(e.msg || ""); if (cm) w.confirmLvl = +cm[1]; }
      else if (e.type === "ENTRY") w.entries++;
      else if (e.type === "WINDOW_OUTCOME") w.outcome = (e.msg || "").split("→").pop().trim();
      else if (e.type === "FADE") w.fades.push({ t: e.t, msg: e.msg || "", side: /^LONG/.test(e.msg || "") ? "long" : "short", aplus: /A\+/.test(e.msg || "") });
      else if (e.type === "CONTINUATION") w.conts.push({ t: e.t, msg: e.msg || "", side: /^LONG/.test(e.msg || "") ? "long" : "short" });
    }
    if (w.mids && w.confirmLvl) w.pivot = w.mids.reduce((bi, v, k) => Math.abs(v - w.confirmLvl) < Math.abs(w.mids[bi] - w.confirmLvl) ? k : bi, 0);
    windows.push(w);
  }
  windows.forEach(scoreWindow);
  // LIVE SPAN per grid — a persisting grid is in-system from its qualify until it invalidates OR the next
  // hour qualifies (a ROLL, which emits no INVALIDATE). Persistence-OFF windows keep a 1-hour span (today's
  // behavior). This one span drives BOTH trade-linking and the off-system split so PERSIST fades count right.
  const persistOn = w => !!(w.invalidatedAt || w.reversalType || (w.fades && w.fades.length) || (w.structures && w.structures.length));
  const byInstW = {}; windows.forEach(w => (byInstW[w.inst] = byInstW[w.inst] || []).push(w));
  const nowH = new Date().getHours();
  for (const inst in byInstW) {
    const arr = byInstW[inst].slice().sort((a, b) => (a.t < b.t ? -1 : 1));
    arr.forEach((w, idx) => {
      const startH = +(w.t || "").slice(11, 13), next = arr[idx + 1];
      let endH;
      if (!persistOn(w)) endH = startH;                                      // window-only: the single trade hour
      else if (w.invalidatedAt) endH = +(w.invalidatedAt).slice(11, 13);     // died mid-hour → that hour counts
      else if (next) endH = ((+(next.t).slice(11, 13)) + 23) % 24;           // ROLL at the next :00 → prior hour
      else endH = (w.day === today) ? nowH : startH;                         // still live right now
      const hrs = []; let h = startH, guard = 0;
      while (guard++ < 24) { hrs.push(h % 24); if ((h % 24) === (endH % 24)) break; h = (h + 1) % 24; }
      w._liveHours = hrs;
    });
  }
  // link your ACTUAL trades to each window across its LIVE SPAN — with entry/exit price + minute so the
  // window's price-path chart can plot exactly where you entered and exited (persist-hour fades included).
  const allTrades = (rcTrades && rcTrades.length ? rcTrades : (myTrading && myTrading.trades) || []);
  windows.forEach(w => {
    const wh = +(w.t || "").slice(11, 13), live = w._liveHours || [wh];
    const ts = allTrades.filter(t => t.day === w.day && live.includes(t.hour !== undefined ? t.hour : t.hr));
    if (ts.length) {
      w.trades = ts.map(t => {
        const et = t.et ? new Date(t.et) : null, xt = t.xt ? new Date(t.xt) : null;
        return { side: t.side, inst: t.inst, acct: t.acct || "", pnl: +(+t.pnl).toFixed(0),
          entryPx: t.entryPx != null ? +t.entryPx : null, exitPx: t.exitPx != null ? +t.exitPx : null,
          entT: t.entT || null, exT: t.exT || null,
          entMin: et ? et.getMinutes() : null, exMin: (xt && et) ? (xt.getHours() === et.getHours() && xt.getDate() === et.getDate() ? xt.getMinutes() : 60) : null };
      });
      w.tradePnl = +ts.reduce((s, t) => s + (+t.pnl), 0).toFixed(0);
    } else w.tradesNote = "No trades taken in this window.";
  });
  // OUT-OF-WINDOW — trades taken in hours the system didn't score (off-system trading). Captures the
  // complete day AND is a teachable moment: where did you deviate from the setups, and did it pay?
  // in-system hours = the union of every grid's LIVE SPAN (computed above), so a PERSIST fade between
  // windows — including a grid ended by a ROLL — counts as in-system, not an "off-system deviation".
  const winHours = new Set();
  windows.filter(w => w.day === today).forEach(w => (w._liveHours || []).forEach(h => winHours.add(h)));
  const oow = (rcTrades || []).filter(t => t.day === today && !winHours.has(t.hour));
  let outOfWindow = null;
  if (oow.length) {
    const ow = oow.filter(t => t.pnl > 0);
    const byH = {}; oow.forEach(t => { const h = byH[t.hour] = byH[t.hour] || { n: 0, net: 0 }; h.n++; h.net += t.pnl; });
    outOfWindow = { n: oow.length, net: +oow.reduce((s, t) => s + t.pnl, 0).toFixed(0),
      winPct: +(ow.length / oow.length * 100).toFixed(0),
      hours: Object.entries(byH).map(([h, v]) => ({ h: +h, n: v.n, net: +v.net.toFixed(0) })).sort((a, b) => a.h - b.h),
      trades: oow.slice().sort((a, b) => a.et - b.et).map(t => ({ side: t.side, inst: t.inst, acct: t.acct || "", pnl: +(+t.pnl).toFixed(0), entT: t.entT, exT: t.exT, entryPx: t.entryPx, exitPx: t.exitPx })) };
  }
  const todays = windows.filter(w => w.day === today).sort((a, b) => (a.t < b.t ? 1 : -1));
  const blocksToday = events.filter(e => e.type === "BLOCK" && (e.t || "").startsWith(today)).length;

  // price trend from the live state — so the co-pilot sees the regime BEFORE a window qualifies
  let trend = 0, trendPts = 0;
  const st0 = states[0];
  if (st0 && st0.price) {
    const h = priceHist[st0.instrument] = priceHist[st0.instrument] || [];
    if (!h.length || h[h.length - 1].p !== st0.price) h.push({ t: Date.now(), p: st0.price });
    if (h.length > 240) h.shift();
    if (h.length >= 3) { trendPts = st0.price - h[0].p; trend = Math.abs(trendPts) >= 6 ? Math.sign(trendPts) : 0; }
  }
  // regime: qualified-window structure first; fall back to raw price trend
  let regime = "quiet", trendRun = 0;
  if (todays.length >= 2) {
    let same = 1, max = 1;
    for (let i = 1; i < todays.length; i++) { if (todays[i].dir === todays[i - 1].dir) { same++; max = Math.max(max, same); } else same = 1; }
    trendRun = max;
    regime = max >= 3 ? "trending" : todays.length >= 3 ? "rotational" : "developing";
  } else if (trend !== 0) regime = trend < 0 ? "bear trend" : "bull trend";
  else if (todays.length) regime = "developing";
  else if (blocksToday >= 4) regime = "quiet";

  // coaching — forward-looking, sets up the NEXT window
  const coaching = [];
  const st = states[0];
  if (st) {
    // gridActive (not levels) gates live-window coaching — a closed last-window grid still carries
    // levels. Fall back to !!levels when the field is absent (state file from an older indicator build).
    const gAct = st.gridActive === undefined ? !!st.levels : !!st.gridActive;
    const arming = st.hrGate > 0 && (st.hrRange || 0) / st.hrGate >= 0.8 && !gAct;
    if (arming) coaching.push({ tone: "act", text: "Hour arming — " + (st.dir === -1 ? "BEAR" : st.dir === 1 ? "BULL" : "") + " developing " + (st.hrRange || 0).toFixed(1) + "pt vs " + (st.hrGate || 0).toFixed(0) + " gate. Be at the screen for the :00 close." });
    else if (gAct && st.status === "CONFIRMED") coaching.push({ tone: "act", text: "Live window CONFIRMED at the " + (NAMES[st.pivot] || "?") + "/" + (NAMES[st.pivot + 1] || "?") + " midline — entry window is the :25 and :45. Trade the retest, not the touch." });
    else if (gAct && st.status === "ACTIVE") coaching.push({ tone: "watch", text: "Grid live, waiting on a 15m confirm. No confirm = no trade — sit on your hands until a midline holds." });
    // ── persistence layer (only speaks when GridPersistence is emitting — gridPhase present) ──
    if (st.status === "PERSIST") coaching.push({ tone: "watch", text: "Grid in PERSIST" + (st.reversalType ? " (" + st.reversalType.toLowerCase() + ")" : "") + " — the trade window closed but the map still holds. Hunt the fade off the " + (st.dir === -1 ? "low" : "high") + " (100/161.8) and midline retests until it invalidates." });
    if (st.fadeArmed) coaching.push({ tone: "act", text: "Fade armed at the extreme — a sweep of the " + (st.dir === -1 ? "100/161.8 low then a close back above" : "100/161.8 high then a close back below") + " is the reversion trigger. Wait for the reject, not the touch." });
    else if (st.reversalType === "REVERSAL" && gAct && st.status !== "PERSIST") coaching.push({ tone: "watch", text: "REVERSAL grid (CHoCH-tagged) — this hour expects the fade off the 100, not a clean run. Favor the reversion over the breakout." });
  }
  // LIVE TRADE MONITOR — if a connected account holds a position, relate it to the grid + remind on management
  const openPos = (accounts || []).filter(a => /connected/i.test(a.conn))
    .flatMap(a => (a.positions || []).map(p => ({ ...p, account: a.account, unrealized: a.unrealized })));
  if (openPos.length && st) {
    const p = openPos[0], px = st.price, lv = st.levels;
    let where = "";
    if (lv && lv.length && px) {
      const idx = lv.map((L, i) => ({ i, d: Math.abs(L - px) })).sort((a, b) => a.d - b.d)[0].i;
      where = " Price at the " + (NAMES[idx] || "?") + " (" + lv[idx].toFixed(2) + ").";
    }
    const pnl = ((p.unrealized || 0) >= 0 ? "+$" : "-$") + Math.abs(p.unrealized || 0).toFixed(0);
    coaching.unshift({ tone: (p.unrealized || 0) < 0 ? "caution" : "act",
      text: "Open " + (p.side === "SHORT" ? "short" : "long") + " " + p.qty + " " + p.instr + " @ " + p.avg + " · " + pnl + " unrealized." + where + " At +1R move to breakeven — never give it back." });
  }
  if (regime === "trending") coaching.push({ tone: "watch", text: trendRun + " straight " + (todays[0] && todays[0].dir) + " windows — trend day. Favor continuation-aligned setups; fade the extension only on a clean 100 rejection." });
  if (regime === "rotational") coaching.push({ tone: "watch", text: "Rotational regime (windows flipping direction) — the reversion fades are the edge today; don't chase breaks." });
  if ((regime === "bear trend" || regime === "bull trend") && !todays.length)
    coaching.push({ tone: "watch", text: (regime === "bear trend" ? "BEAR" : "BULL") + " trend on the tape (" + trendPts.toFixed(1) + "pt) but hours keep just missing the gate" + (blocksToday ? " (" + blocksToday + " blocked)" : "") + ". The move is real even if it's not qualifying clean — watch for the hour that expands and holds direction." });
  const lastTwo = todays.slice(0, 2).filter(w => w.verdict && w.verdict.tone === "neg");
  if (lastTwo.length === 2) coaching.push({ tone: "caution", text: "Last two windows misread — a setup failed or a clean move slipped by. Tighten up; wait for an A/B window with a ✓." });
  if (blocksToday >= 5 && todays.length === 0) coaching.push({ tone: "watch", text: blocksToday + " quiet hours blocked, nothing qualified — sit tight, protect capital, wait for a 10pt+ expansion." });
  if (!coaching.length) coaching.push({ tone: "watch", text: "Measuring. The radar will call the next qualifying window — stay ready." });

  // personal coaching from YOUR trade history — the co-pilot knows how you trade
  const mt = myTrading;
  if (mt) {
    const nowH = new Date().getHours();
    if (mt.worstHour && mt.worstHour.h === nowH) coaching.unshift({ tone: "caution", text: "Your " + nowH + ":00 hour is a trap (" + mt.worstHour.wr + "% win, $" + mt.worstHour.net + " historically). Size down or sit this one." });
    else if (mt.bestHour && mt.bestHour.h === nowH) coaching.unshift({ tone: "act", text: "Your A-hour — " + nowH + ":00 is your best (" + mt.bestHour.wr + "% win, +$" + mt.bestHour.net + "). Trade it with size." });
    if (mt.short && mt.long && mt.short.pf > mt.long.pf * 1.5 && (regime === "bear trend" || regime === "trending"))
      coaching.push({ tone: "act", text: "You're a short-seller (shorts PF " + mt.short.pf + " vs longs " + mt.long.pf + ") and the tape is bearish — this is your setup. Trade with it." });
    if (mt.gaveBack && mt.gaveBack.n >= 3) coaching.push({ tone: "caution", text: "You've given back $" + Math.abs(mt.gaveBack.cost) + " on " + mt.gaveBack.n + " winners that round-tripped. Breakeven at +1R — never let a green trade go red." });
  }

  return {
    regime, blocksToday, outOfWindow,
    windows: windows.sort((a, b) => (a.t < b.t ? 1 : -1)).slice(0, 12),
    todayCount: todays.length, avgGrade: gradeAvg(todays),
    coaching,
    accounts: acctKpis(accounts),
    myTrading: mt
  };
}
const NAMES = ["0", "23.6", "38.2", "50", "61.8", "78.6", "100", "161.8"];
const RATIOS = [0, 0.236, 0.382, 0.5, 0.618, 0.786, 1.0, 1.618];
// OPPORTUNITY-PLUS-VERDICT scoring. The LETTER grades the window's real opportunity — anchored on the
// validated edges (grid DIRECTION 81-86% accurate, the CONTINUATION-pullback +5.1pt, the FADE +0.71R) and NOT
// the ~breakeven :25/:45 retest. A separate DECISION VERDICT answers "did we play it right?" so a correctly
// skipped quiet window reads as "low opportunity · ✓ stood down" instead of a self-contradicting D.
function scoreWindow(w) {
  const oc = !w.outcome ? "live"
           : /target|100|161/i.test(w.outcome)     ? "target"
           : /61\.8|partial/i.test(w.outcome)       ? "partial"
           : /invalid|broke the 0/i.test(w.outcome) ? "reversed" : "chop";
  const cont = (w.conts || []).length > 0, fade = (w.fades || []).length > 0, aplus = cont || fade;
  const contPaid = cont && (oc === "target" || oc === "partial");   // continuation is WITH-grid: pays when the grid runs
  const fadePaid = fade && (oc === "reversed" || oc === "chop");    // fade is COUNTER-grid: pays when the grid rejects/reverts

  // 1) PLAYABLE OUTCOME (0-65) — did the window's real edge actually pay?
  let play;
  if (contPaid)          play = oc === "target"   ? 65 : 45;
  else if (fadePaid)     play = oc === "reversed" ? 65 : 45;
  else if (aplus)        play = 8;                 // a setup fired but the window went the other way — a trap
  else if (oc === "target")  play = 42;            // grid direction paid, but no clean A+ entry existed — a miss
  else if (oc === "partial") play = 27;
  else if (oc === "live")    play = 27;
  else                   play = oc === "chop" ? 15 : 8;   // chop / reversed with no setup = low opportunity

  // 2) RETEST confirm (0-10) — demoted from 24: it's ~breakeven, a minor tiebreak now
  const tradeable = w.confirms.filter(Boolean).length;
  const conf = tradeable > 0 ? 10 : w.confirms.length ? 5 : 0;
  // 3) CONVICTION (0-25) — qualifying-candle size over the gate
  const conv = Math.min(25, (w.size || 0) * 1.6);

  const pts = Math.round(play + conf + conv);
  w.score = pts;
  w.grade = pts >= 78 ? "A" : pts >= 60 ? "B" : pts >= 44 ? "C" : pts >= 28 ? "D" : "F";

  // DECISION VERDICT — the separate axis (fixes the "D + patience paid" contradiction)
  let vmark, vtext, vtone;
  if (oc === "live")                 { vmark = "·"; vtext = "window live";                 vtone = "neutral"; }
  else if (contPaid || fadePaid)     { vmark = "✓"; vtext = "captured the " + (contPaid ? "continuation" : "fade"); vtone = "pos"; }
  else if (aplus)                    { vmark = "✗"; vtext = "setup fired, no follow-through"; vtone = "neg"; }
  else if (oc === "target")          { vmark = "✗"; vtext = "clean move — no setup fired";   vtone = "neg"; }
  else                               { vmark = "✓"; vtext = "correctly stood down";          vtone = "pos"; }
  w.verdict = { mark: vmark, text: vtext, tone: vtone };

  const setupTag = cont ? "continuation setup" : fade ? "fade setup"
                 : tradeable > 0 ? "tradeable confirm" : w.confirms.length ? "confirm too deep" : "no setup";
  w.debrief = w.dir + " " + w.size.toFixed(1) + "pt @" + w.hour + ":00 · " + setupTag
            + (w.outcome ? " · " + w.outcome : " · window live");
}
function gradeAvg(ws) {
  const map = { A: 4, B: 3, C: 2, D: 1, F: 0 };
  if (!ws.length) return "—";
  const v = ws.reduce((s, w) => s + (map[w.grade] || 0), 0) / ws.length;
  return v >= 3.5 ? "A" : v >= 2.5 ? "B" : v >= 1.5 ? "C" : v >= 0.5 ? "D" : "F";
}
function acctKpis(accounts) {
  const now = Date.now();
  const real = (accounts || []).filter(a => acctVisible(a.account));
  const per = real.map(a => {
    const h = acctHist[a.account] = acctHist[a.account] || [];
    const p = a.dayPnL || 0;
    if (!h.length || Math.abs(h[h.length - 1].p - p) > 0.01) h.push({ t: now, p });
    if (h.length > 800) h.shift();
    const peak = h.reduce((m, x) => Math.max(m, x.p), 0);
    const trough = h.reduce((m, x) => Math.min(m, x.p), 0);
    const stale = (now - new Date((a.updated || "").replace(" ", "T"))) > 60000;
    return { name: a.account, conn: a.conn || "—", stale, dayPnL: p, balance: a.balance || 0,
             unrealized: a.unrealized || 0, netQty: a.netQty || 0, positions: a.positions || [], peak, trough,
             drawdown: Math.max(0, peak - p), trail: h.slice(-40).map(x => +x.p.toFixed(2)) };
  });
  // Book totals come from CONNECTED accounts (real, last-known P&L) — NOT the 60s freshness window.
  // A file that hasn't been rewritten in the last minute is still your money; don't zero it out.
  // Freshness only drives the "live" count so a stalled NT feed is visible without erasing P&L.
  const connected = per.filter(a => /connected/i.test(a.conn));
  const fresh = connected.filter(a => !a.stale);
  const totDay = connected.reduce((s, a) => s + a.dayPnL, 0);
  const totBal = connected.reduce((s, a) => s + a.balance, 0);
  const green = connected.filter(a => a.dayPnL > 0).length, red = connected.filter(a => a.dayPnL < 0).length;
  const totDD = connected.reduce((s, a) => s + a.drawdown, 0);
  const sorted = connected.slice().sort((a, b) => b.dayPnL - a.dayPnL);
  return {
    per,
    global: { n: connected.length, live: fresh.length, feedStale: connected.length > 0 && fresh.length === 0, totDay, totBal, green, red, totDD,
              best: sorted[0] ? sorted[0].name : null, bestPnL: sorted[0] ? sorted[0].dayPnL : 0,
              worst: sorted.length ? sorted[sorted.length - 1].name : null, worstPnL: sorted.length ? sorted[sorted.length - 1].dayPnL : 0 }
  };
}

// ── TradingView "List of Trades" CSV → metrics ──
function splitCsv(line) {
  const out = []; let f = "", q = false;
  for (let i = 0; i < line.length; i++) {
    const c = line[i];
    if (q) { if (c === '"') { if (line[i + 1] === '"') { f += '"'; i++; } else q = false; } else f += c; }
    else if (c === '"') q = true;
    else if (c === ",") { out.push(f); f = ""; }
    else f += c;
  }
  out.push(f); return out;
}
function money(s) {
  if (s === undefined || String(s).trim() === "") return NaN;
  const neg = String(s).includes("(");
  const v = parseFloat(String(s).replace(/[$,()%\s]/g, ""));
  return isNaN(v) ? NaN : (neg ? -v : v);
}
function parseBacktest(file) {
  // accepts TradingView "List of Trades" AND NinjaTrader "Trades" grid exports
  const rows = fs.readFileSync(file, "utf8").split(/\r?\n/).filter(x => x.trim()).map(splitCsv);
  const hdr = rows[0].map(h => h.trim());
  const iNum = hdr.findIndex(h => /trade\s*(#|number)/i.test(h));
  const iPnl = hdr.findIndex(h => /^(net\s*)?(P&L|Profit)(\s*USD)?$/i.test(h));
  const iTyp = hdr.findIndex(h => /^type$/i.test(h));
  const iSig = hdr.findIndex(h => /^signal$/i.test(h));
  const iXN  = hdr.findIndex(h => /^exit name$/i.test(h));          // NinjaTrader
  const iDt  = hdr.findIndex(h => /date|entry time/i.test(h));
  if (iNum < 0 || iPnl < 0) return null;
  const trades = new Map(); const exitNames = {};
  let d0 = null, d1 = null;
  for (let r = 1; r < rows.length; r++) {
    const R = rows[r]; const n = parseInt(R[iNum]); if (isNaN(n)) continue;
    const v = money(R[iPnl]);
    if (!isNaN(v)) trades.set(n, v);
    if (iXN >= 0 && R[iXN]) exitNames[R[iXN]] = (exitNames[R[iXN]] || 0) + 1;
    else if (iTyp >= 0 && /exit/i.test(R[iTyp]) && iSig >= 0 && R[iSig])
      exitNames[R[iSig]] = (exitNames[R[iSig]] || 0) + 1;
    if (iDt >= 0 && R[iDt]) { const d = R[iDt]; if (!d0 || d < d0) d0 = d; if (!d1 || d > d1) d1 = d; }
  }
  const pnl = [...trades.entries()].sort((a, b) => a[0] - b[0]).map(e => e[1]);
  if (!pnl.length) return null;
  const w = pnl.filter(v => v > 0), l = pnl.filter(v => v < 0);
  const gw = w.reduce((a, b) => a + b, 0), gl = l.reduce((a, b) => a + b, 0);
  let eq = 0, pk = 0, dd = 0, cl = 0, mcl = 0; const curve = [0];
  for (const v of pnl) {
    eq += v; curve.push(+eq.toFixed(2)); pk = Math.max(pk, eq); dd = Math.max(dd, pk - eq);
    if (v < 0) { cl++; mcl = Math.max(mcl, cl); } else if (v > 0) cl = 0;
  }
  return {
    file: path.basename(file), from: d0, to: d1,
    n: pnl.length, wr: +(w.length / pnl.length).toFixed(3),
    pf: gl ? +(gw / -gl).toFixed(3) : null, net: +(gw + gl).toFixed(2),
    exp: +((gw + gl) / pnl.length).toFixed(2), maxDD: +dd.toFixed(2), maxCL: mcl,
    exitNames, curve: curve.filter((_, i) => i % Math.max(1, Math.floor(curve.length / 160)) === 0 || i === curve.length - 1)
  };
}
// ═══ YOUR TRADING — parse the newest "Live Trades" export into personal KPIs ═══
function num(s){ if(s===undefined||String(s).trim()==="")return NaN; const neg=String(s).includes("("); const v=parseFloat(String(s).replace(/[$,()%\s]/g,"")); return isNaN(v)?NaN:(neg?-v:v); }
// EDGE over multiple lookbacks (today/7/14/30/60/90/all) — one source of truth for the UI selector + Aries.
// T: normalized trades [{side:"LONG"|"SHORT", pnl, hour, mfe, et:Date}].
function edgeWindows(T) {
  const agg = a => { const w = a.filter(t => t.pnl > 0), l = a.filter(t => t.pnl < 0);
    const gw = w.reduce((s, t) => s + t.pnl, 0), gl = l.reduce((s, t) => s + t.pnl, 0);
    return { n: a.length, net: gw + gl, wr: a.length ? w.length / a.length * 100 : 0, pf: gl ? gw / -gl : (gw > 0 ? 99 : 0),
             aw: w.length ? gw / w.length : 0, al: l.length ? gl / l.length : 0 }; };
  const edgeFor = arr => {
    const a = agg(arr), lA = agg(arr.filter(t => t.side === "LONG")), sA = agg(arr.filter(t => t.side === "SHORT"));
    const bh = {}; for (const t of arr) (bh[t.hour] = bh[t.hour] || []).push(t);
    const hrs = Object.keys(bh).map(h => ({ h: +h, ...agg(bh[h]) })).filter(x => x.n >= 2).sort((x, y) => y.net - x.net);
    const gb = arr.filter(t => t.pnl < 0 && t.mfe >= 30);
    let cum = 0; const curve = arr.slice().sort((x, y) => x.et - y.et).map(t => (cum += t.pnl, +cum.toFixed(0)));
    const days = new Set(arr.map(t => LOCAL_DAY(t.et)));
    return { n: a.n, net: +a.net.toFixed(0), wr: +a.wr.toFixed(0), pf: +a.pf.toFixed(2), exp: a.n ? +(a.net / a.n).toFixed(2) : 0,
      aw: +a.aw.toFixed(0), al: +a.al.toFixed(0), days: days.size, curve: curve.slice(-60),
      long: { n: lA.n, net: +lA.net.toFixed(0), pf: +lA.pf.toFixed(2), al: +lA.al.toFixed(0) },
      short: { n: sA.n, net: +sA.net.toFixed(0), pf: +sA.pf.toFixed(2), al: +sA.al.toFixed(0) },
      bestHour: hrs[0] ? { h: hrs[0].h, net: +hrs[0].net.toFixed(0), wr: +hrs[0].wr.toFixed(0), n: hrs[0].n } : null,
      worstHour: hrs.length ? (h => ({ h: h.h, net: +h.net.toFixed(0), wr: +h.wr.toFixed(0), n: h.n }))(hrs[hrs.length - 1]) : null,
      gaveBack: { n: gb.length, cost: +gb.reduce((s, t) => s + t.pnl, 0).toFixed(0) } };
  };
  const now = new Date(), sot = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const cutN = n => { const c = new Date(sot); c.setDate(c.getDate() - (n - 1)); return c; };
  const windows = { today: edgeFor(T.filter(t => t.et >= sot)) };
  [7, 14, 30, 60, 90].forEach(n => { windows[n] = edgeFor(T.filter(t => t.et >= cutN(n))); });
  windows.all = edgeFor(T);
  const o = windows.all;
  return { overall: { n: o.n, net: o.net, wr: o.wr, pf: o.pf, aw: o.aw, al: o.al, exp: o.exp },
    today: { n: windows.today.n, net: windows.today.net },
    long: o.long, short: o.short, bestHour: o.bestHour, worstHour: o.worstHour, gaveBack: o.gaveBack, windows };
}
function readMyTrading() {
  let files = [];
  try { files = fs.readdirSync(BT).filter(f => /live\s*trades/i.test(f) && f.toLowerCase().endsWith(".csv")); } catch { return null; }
  if (!files.length) return null;
  files.sort(); const file = files[files.length - 1];
  let rows;
  try { rows = fs.readFileSync(path.join(BT, file), "utf8").split(/\r?\n/).filter(x => x.trim()).map(splitCsv); } catch { return null; }
  const hdr = rows[0].map(h => h.trim());
  const ix = n => hdr.findIndex(h => new RegExp("^" + n + "$", "i").test(h));
  const iInst = ix("Instrument"), iPos = ix("Market pos\\."), iPnl = ix("Profit"),
        iEnt = ix("Entry time"), iEx = ix("Exit time"), iMfe = ix("MFE");
  if (iPnl < 0) return null;
  const T = [];
  for (let r = 1; r < rows.length; r++) {
    const R = rows[r]; const pnl = num(R[iPnl]); if (isNaN(pnl)) continue;
    const entT = new Date(R[iEnt]), exT = new Date(R[iEx]);
    if (isNaN(entT)) continue;
    T.push({ inst: (R[iInst] || "").split(" ")[0], pos: R[iPos], pnl, entT, exT, mfe: num(R[iMfe]) || 0, hour: entT.getHours() });
  }
  if (!T.length) return null;
  const norm = T.map(t => ({ side: t.pos === "Long" ? "LONG" : "SHORT", pnl: t.pnl, hour: t.hour, mfe: t.mfe, et: t.entT }));
  return { ...edgeWindows(norm),
    trades: T.map(t => ({ day: LOCAL_DAY(t.entT), hr: t.hour,
      side: t.pos === "Long" ? "LONG" : "SHORT", pnl: +t.pnl.toFixed(2), inst: t.inst })),
    file };
}

// ── DAILY REPORT CARD — parse the full NinjaTrader _Trades.csv (MFE/MAE/ETD, entry/exit times) into
//    a rich per-day scorecard: net, win%, PF, what was LEFT ON THE TABLE (MFE), what was GIVEN BACK
//    (round-tripped winners), exit discipline (manual vs stop vs target), and per-hour timing. ──
function reportCardFor(arr) {
  const w = arr.filter(t => t.pnl > 0), l = arr.filter(t => t.pnl < 0);
  const gw = w.reduce((s, t) => s + t.pnl, 0), gl = l.reduce((s, t) => s + t.pnl, 0), net = gw + gl;
  // cumulative equity curve (chronological) for the sparkline
  let cum = 0; const curve = arr.slice().sort((a, b) => a.et - b.et).map(t => (cum += t.pnl, +cum.toFixed(0)));
  const leftOnTable = w.reduce((s, t) => s + Math.max(0, t.mfe - t.pnl), 0);  // peak minus what you kept, on winners
  const roundTripped = arr.filter(t => t.mfe >= 30 && t.pnl < t.mfe * 0.4);   // was well green, gave most back
  const exits = { manual: 0, stop: 0, target: 0 };
  arr.forEach(t => { const n = (t.exitName || "").toLowerCase(); if (/stop/.test(n)) exits.stop++; else if (/target|profit/.test(n)) exits.target++; else exits.manual++; });
  const byH = {}; arr.forEach(t => { const h = byH[t.hour] = byH[t.hour] || { n: 0, net: 0, wn: 0 }; h.n++; h.net += t.pnl; if (t.pnl > 0) h.wn++; });
  const hours = Object.entries(byH).map(([h, v]) => ({ h: +h, n: v.n, net: +v.net.toFixed(0), wr: +(v.wn / v.n * 100).toFixed(0) })).sort((a, b) => b.net - a.net);
  const avgHold = arr.reduce((s, t) => s + Math.max(0, (t.xt - t.et) / 60000), 0) / arr.length;
  const side = s => (a => ({ n: a.length, net: +a.reduce((x, t) => x + t.pnl, 0).toFixed(0), pf: (gg => gg[1] ? +(gg[0] / -gg[1]).toFixed(2) : (gg[0] > 0 ? 99 : 0))([a.filter(t => t.pnl > 0).reduce((x, t) => x + t.pnl, 0), a.filter(t => t.pnl < 0).reduce((x, t) => x + t.pnl, 0)]) }))(arr.filter(t => t.side === s));
  return {
    n: arr.length, net: +net.toFixed(0), wins: w.length, losses: l.length,
    winPct: +(arr.length ? w.length / arr.length * 100 : 0).toFixed(0),
    pf: gl ? +(gw / -gl).toFixed(2) : (gw > 0 ? 99 : 0), exp: +(net / arr.length).toFixed(2),
    gw: +gw.toFixed(0), gl: +gl.toFixed(0),
    avgWin: +(w.length ? gw / w.length : 0).toFixed(0), avgLoss: +(l.length ? gl / l.length : 0).toFixed(0),
    lrgWin: +Math.max(0, ...arr.map(t => t.pnl)).toFixed(0), lrgLoss: +Math.min(0, ...arr.map(t => t.pnl)).toFixed(0),
    leftOnTable: +leftOnTable.toFixed(0), curve,
    gaveBack: { n: roundTripped.length, cost: +roundTripped.reduce((s, t) => s + Math.max(0, t.mfe - t.pnl), 0).toFixed(0) },
    exits, hours, bestHour: hours[0] || null, worstHour: hours.length > 1 ? hours[hours.length - 1] : null,
    avgHoldMin: +avgHold.toFixed(0), long: side("LONG"), short: side("SHORT")
  };
}
// ── AUTO TRADE CAPTURE — pair the RollingFibAccounts execution log (live/executions.jsonl) into
//    round-trip trades with realized P&L, FIFO per account+instrument. No manual CSV export needed;
//    the indicator streams every fill. Point value + commission ride on each execution. ──
function readAutoTrades() {
  let lines;
  try { lines = fs.readFileSync(path.join(LIVE, "executions.jsonl"), "utf8").split(/\r?\n/).filter(x => x.trim()); }
  catch { return null; }
  if (!lines.length) return null;
  const seen = new Set(), execs = [];
  for (const ln of lines) { let e; try { e = JSON.parse(ln); } catch { continue; }
    if (!e.id || seen.has(e.id)) continue; seen.add(e.id); execs.push(e); }
  execs.sort((a, b) => (a.t < b.t ? -1 : a.t > b.t ? 1 : 0));
  const books = {}, trades = [];
  for (const e of execs) {
    const key = (e.account || "") + "|" + (e.inst || "");
    const b = books[key] = books[key] || [];                  // FIFO lot queue: {q(signed), px, t, cpu(comm/unit)}
    const act = (e.action || "").toLowerCase();
    const sign = /buy|cover/.test(act) ? 1 : /sell|short/.test(act) ? -1 : 0;   // Buy/BuyToCover=+, Sell/SellShort=-
    if (!sign) continue;
    let qty = Math.abs(+e.qty || 0); if (!qty) continue;
    const px = +e.price, pv = +e.pv || 1, cpu = (+e.comm || 0) / qty, t = e.t;
    while (qty > 0 && b.length && Math.sign(b[0].q) === -sign) {                 // close opposing lots first (FIFO)
      const lot = b[0], lotSign = Math.sign(lot.q), closeQ = Math.min(qty, Math.abs(lot.q));
      const gross = (lotSign === 1 ? (px - lot.px) : (lot.px - px)) * closeQ * pv;
      const pnl = gross - (lot.cpu + cpu) * closeQ;                              // net of entry + exit commission
      trades.push({ inst: e.inst, acct: e.account, side: lotSign === 1 ? "LONG" : "SHORT", qty: closeQ,
        entryPx: lot.px, exitPx: px, et: new Date((lot.t || "").replace(" ", "T")), xt: new Date((t || "").replace(" ", "T")), pnl, exitName: e.name || "" });
      lot.q -= lotSign * closeQ; qty -= closeQ;
      if (Math.abs(lot.q) < 1e-9) b.shift();
    }
    if (qty > 0) b.push({ q: sign * qty, px, t, cpu });                         // remainder opens a new lot
  }
  if (!trades.length) return null;
  const hh = d => isNaN(d) ? null : String(d.getHours()).padStart(2, "0") + ":" + String(d.getMinutes()).padStart(2, "0");
  return { trades: trades.filter(t => !isNaN(t.et)).map(t => ({ inst: t.inst, acct: t.acct, side: t.side, qty: t.qty,
    entryPx: t.entryPx, exitPx: t.exitPx, et: t.et, xt: t.xt, entT: hh(t.et), exT: hh(t.xt), exitName: t.exitName || "auto",
    pnl: +t.pnl.toFixed(2), mae: 0, mfe: 0, etd: 0, bars: 0, day: LOCAL_DAY(t.et), hour: t.et.getHours() })) };
}
function readReportCard() {
  let file = null; const T = [];
  const hhmm = d => isNaN(d) ? null : String(d.getHours()).padStart(2, "0") + ":" + String(d.getMinutes()).padStart(2, "0");
  // 1. RICH trades from the newest _Trades.csv (MFE/MAE/ETD, entry-exit) if one's been exported
  try {
    const files = fs.readdirSync(BT).filter(f => /_Trades\.csv$/i.test(f));
    if (files.length) {
      files.sort(); file = files[files.length - 1];
      const rows = fs.readFileSync(path.join(BT, file), "utf8").split(/\r?\n/).filter(x => x.trim()).map(splitCsv);
      const hdr = rows[0].map(h => h.trim()); const ix = n => hdr.findIndex(h => new RegExp("^" + n + "$", "i").test(h));
      const iInst = ix("Instrument"), iAcct = ix("Account"), iPos = ix("Market pos\\."), iQty = ix("Qty"), iEnt = ix("Entry price"), iExP = ix("Exit price"),
        iEntT = ix("Entry time"), iExT = ix("Exit time"), iExN = ix("Exit name"), iPnl = ix("Profit"), iMae = ix("MAE"), iMfe = ix("MFE"), iEtd = ix("ETD"), iBars = ix("Bars");
      if (iPnl >= 0 && iEntT >= 0) for (let r = 1; r < rows.length; r++) {
        const R = rows[r], pnl = num(R[iPnl]); if (isNaN(pnl)) continue;
        const et = new Date(R[iEntT]); if (isNaN(et)) continue; const xt = new Date(R[iExT]);
        T.push({ inst: (R[iInst] || "").split(" ")[0], acct: iAcct >= 0 ? (R[iAcct] || "").trim() : "", side: R[iPos] === "Long" ? "LONG" : "SHORT", qty: +R[iQty] || 1,
          entryPx: num(R[iEnt]), exitPx: num(R[iExP]), et, xt, entT: hhmm(et), exT: hhmm(xt), exitName: (R[iExN] || "").trim(),
          pnl, mae: num(R[iMae]) || 0, mfe: num(R[iMfe]) || 0, etd: num(R[iEtd]) || 0, bars: +R[iBars] || 0,
          day: LOCAL_DAY(et), hour: et.getHours() });
      }
    }
  } catch { }
  // 2. AUTO — fill the days the CSV doesn't cover (today + anything since the last export) from the live
  //    execution log, so the report card + edge stay current with ZERO manual export.
  const csvDays = new Set(T.map(t => t.day)); let autoDays = [];
  const auto = readAutoTrades();
  if (auto && auto.trades.length) {
    const extra = auto.trades.filter(t => !csvDays.has(t.day));
    if (extra.length) { for (const t of extra) T.push(t); autoDays = [...new Set(extra.map(t => t.day))].sort(); if (!file) file = "auto-capture · executions"; }
  }
  if (!T.length) return null;
  const days = {}; for (const t of T) (days[t.day] = days[t.day] || []).push(t);
  const perDay = {}; for (const d in days) perDay[d] = reportCardFor(days[d]);
  return { file, days: Object.keys(days).sort(), perDay, overall: reportCardFor(T), trades: T, autoDays };
}

// ═══ SIGNAL BACKTEST — turn the radar's forecast into scored trades: the :25/:45 retest entry (Setup A) and the
//    persist-window fade (Setup B), each at STRUCTURAL ±1R, walked forward against the bar history. Tunable. ═══
function readBacktestData() {
  let bars, lines;
  try { bars = JSON.parse(fs.readFileSync(path.join(LIVE, "bars_history_MES.json"), "utf8")).bars; } catch { return null; }
  try { lines = fs.readFileSync(path.join(LIVE, "backtest_MES.jsonl"), "utf8").split(/\r?\n/).filter(x => x.trim()); } catch { return null; }
  if (!bars || !bars.length || !lines.length) return null;
  const sig = lines.map(l => { try { return JSON.parse(l); } catch { return null; } }).filter(Boolean);
  const grids = sig.filter(s => s.type === "QUAL").map(g => {
    const l0 = +g.l0, l6 = +g.l6, levels = RATIOS.map(r => +(l0 + (l6 - l0) * r).toFixed(2));
    return { t: g.t, dir: +g.dir, rev: g.rev || "", size: +g.size, levels, mids: levels.slice(0, 7).map((v, k) => +((v + levels[k + 1]) / 2).toFixed(2)) };
  });
  const confirms = sig.filter(s => s.type === "CONFIRM").map(c => ({ t: c.t, dir: +c.dir, pivot: +c.pivot, px: +c.px, tradeable: !!c.tradeable }));
  const structures = sig.filter(s => s.type === "STRUCTURE").map(s => ({ t: s.t, kind: s.kind, dir: +s.dir, level: +s.level, close: +s.close }));
  const fades = sig.filter(s => s.type === "FADE").map(f => ({ t: f.t, dir: +f.dir, swept: +f.swept, px: +f.px }));
  return { bars, grids, confirms, structures, fades };
}
// SIGNAL ANALYSIS — answer the "what does each signal actually mean" questions from where they fired vs the bars.
function signalAnalysis() {
  const d = readBacktestData(); if (!d) return null;
  const { bars, confirms, structures, fades } = d;
  const bt = bars.map(b => Date.parse((b.t || "").replace(" ", "T")));
  const idxAt = tv => { for (let i = 0; i < bt.length; i++) if (bt[i] >= tv) return i; return -1; };
  const N = 12;   // 1-hour forward horizon
  const fwdOf = t => { const i = idxAt(Date.parse((t || "").replace(" ", "T"))); if (i < 0 || i + 1 >= bars.length) return null; return { i, fwd: bars.slice(i + 1, i + 1 + N) }; };
  const rate = (a, p) => a.length ? Math.round(a.filter(p).length / a.length * 100) : 0;
  const avg = (a, f) => a.length ? +(a.reduce((s, x) => s + f(x), 0) / a.length).toFixed(1) : 0;
  // BOS — does it continue (in the break direction) or reverse?
  const bosO = structures.filter(s => s.kind === "BOS").map(s => { const w = fwdOf(s.t); if (!w || !w.fwd.length) return null;
    const endC = +w.fwd[w.fwd.length - 1].c, hi = Math.max(...w.fwd.map(b => +b.h)), lo = Math.min(...w.fwd.map(b => +b.l));
    return { cont: s.dir === 1 ? endC - s.close : s.close - endC, fav: s.dir === 1 ? hi - s.close : s.close - lo, adv: s.dir === 1 ? s.close - lo : hi - s.close }; }).filter(Boolean);
  // CHoCH — does the reversal follow through, and does the broken level hold as support/resistance on retest?
  const chO = structures.filter(s => s.kind === "CHoCH").map(s => { const w = fwdOf(s.t); if (!w || !w.fwd.length) return null;
    const endC = +w.fwd[w.fwd.length - 1].c; let retested = false, held = false;
    for (const b of w.fwd) { const touch = s.dir === 1 ? +b.l <= s.level + 2 : +b.h >= s.level - 2; if (touch) { retested = true; held = s.dir === 1 ? +b.c >= s.level : +b.c <= s.level; break; } }
    return { rev: s.dir === 1 ? endC - s.close : s.close - endC, retested, held }; }).filter(Boolean);
  // Gold circles (tradeable confirms) — did price move in the grid direction after? and the fade for comparison.
  const outMove = (arr, pxKey, dirKey) => arr.map(x => { const w = fwdOf(x.t); if (!w || !w.fwd.length) return null;
    const endC = +w.fwd[w.fwd.length - 1].c, e = +x[pxKey], dir = +x[dirKey]; return { mv: dir === 1 ? endC - e : e - endC }; }).filter(Boolean);
  const goldO = outMove(confirms.filter(c => c.tradeable), "px", "dir");
  const fadeO = outMove(fades, "px", "dir");
  return {
    span: bars.length ? bars[0].t.slice(0, 10) + " → " + bars[bars.length - 1].t.slice(0, 10) : "",
    bos: { n: bosO.length, contPct: rate(bosO, x => x.cont > 0), avgCont: avg(bosO, x => x.cont), avgFav: avg(bosO, x => x.fav), avgAdv: avg(bosO, x => x.adv) },
    choch: { n: chO.length, revPct: rate(chO, x => x.rev > 0), avgRev: avg(chO, x => x.rev), retests: chO.filter(x => x.retested).length, holdPct: rate(chO.filter(x => x.retested), x => x.held) },
    gold: { n: goldO.length, workPct: rate(goldO, x => x.mv > 0), avgMv: avg(goldO, x => x.mv) },
    fade: { n: fadeO.length, workPct: rate(fadeO, x => x.mv > 0), avgMv: avg(fadeO, x => x.mv) }
  };
}
function scoreTrade(bars, ei, entry, stop, target, dir, maxBars) {   // walk forward: target-first = +1R, stop-first = -1R
  const R = Math.abs(entry - stop) || 1; let mae = 0;
  for (let i = ei + 1; i < Math.min(bars.length, ei + 1 + maxBars); i++) {
    const hi = +bars[i].h, lo = +bars[i].l;
    mae = Math.max(mae, dir === 1 ? entry - lo : hi - entry);
    const hitStop = dir === 1 ? lo <= stop : hi >= stop, hitTgt = dir === 1 ? hi >= target : lo <= target;
    if (hitStop) return { r: -1, held: i - ei, maeR: +(mae / R).toFixed(2), exit: "stop" };   // same-bar ambiguity → conservative loss
    if (hitTgt) return { r: 1, held: i - ei, maeR: +(mae / R).toFixed(2), exit: "target" };
  }
  const last = +bars[Math.min(bars.length - 1, ei + maxBars)].c;
  return { r: +(((dir === 1 ? last - entry : entry - last)) / R).toFixed(2), held: maxBars, maeR: +(mae / R).toFixed(2), exit: "timeout" };
}
function bAgg(ts) {
  if (!ts.length) return { n: 0, netR: 0, pf: 0, winPct: 0, expR: 0, avgHeatR: 0 };
  const w = ts.filter(t => t.r > 0), l = ts.filter(t => t.r < 0);
  const gw = w.reduce((s, t) => s + t.r, 0), gl = Math.abs(l.reduce((s, t) => s + t.r, 0));
  return { n: ts.length, wins: w.length, losses: l.length, winPct: +(w.length / ts.length * 100).toFixed(0),
    pf: gl > 0 ? +(gw / gl).toFixed(2) : (gw > 0 ? 99 : 0), expR: +(ts.reduce((s, t) => s + t.r, 0) / ts.length).toFixed(2),
    netR: +ts.reduce((s, t) => s + t.r, 0).toFixed(1), avgHeatR: +(ts.reduce((s, t) => s + t.maeR, 0) / ts.length).toFixed(2) };
}
function runBacktest(opt) {
  opt = Object.assign({ confirmBars: 2, wickFrac: 0.45, reversalOnly: true, maxPivot: 3, winBars: 12, persistBars: 48 }, opt || {});   // base = the live default (confirm 2)
  const d = readBacktestData(); if (!d) return null;
  const { bars, grids, confirms } = d;
  const ms = t => Date.parse((t || "").replace(" ", "T")), bt = bars.map(b => ms(b.t));
  const idxFrom = tv => { for (let i = 0; i < bt.length; i++) if (bt[i] >= tv) return i; return -1; };
  const setupA = [], setupB = [];
  for (const g of grids) {
    const t0 = ms(g.t), tEnd = t0 + 60 * 60000;
    // ── Setup A: the :25/:45 retest — first tradeable confirm in the window, entered on the pivot retest ──
    const cf = confirms.find(c => { const ct = ms(c.t); return ct >= t0 && ct <= tEnd && c.tradeable && c.pivot <= opt.maxPivot && c.dir === g.dir; });
    if (cf) {
      const piv = cf.px, R = (Math.abs(g.levels[cf.pivot + 1] - g.levels[cf.pivot]) / 2) || 2;
      let ei = -1; for (let i = idxFrom(ms(cf.t) + 1); i >= 0 && i < bars.length && bt[i] <= tEnd; i++) { if (+bars[i].l <= piv && +bars[i].h >= piv) { ei = i; break; } }
      if (ei >= 0) setupA.push(Object.assign(scoreTrade(bars, ei, piv, g.dir === 1 ? piv - R : piv + R, g.dir === 1 ? piv + R : piv - R, g.dir, opt.winBars), { grid: g.t, dir: g.dir }));
    }
    // ── Setup B: the persist-window fade (reversalType + wick + confirm gated), a faithful port of the indicator ──
    if (!opt.reversalOnly || g.rev === "REVERSAL" || g.rev === "NEUTRAL") {
      const t100 = g.levels[6], t162 = g.levels[7], start = idxFrom(t0), stopIdx = start < 0 ? -1 : Math.min(bars.length, start + opt.persistBars);
      let armed = true, pend = -1, pDir = 0, pExt = 0, pSwept = 0;
      for (let i = start; i >= 0 && i < stopIdx; i++) {
        const hi = +bars[i].h, lo = +bars[i].l, cl = +bars[i].c, op = +bars[i].o, rng = hi - lo;
        if (pend >= 0) {
          const blown = pDir === 1 ? lo < pExt : hi > pExt;
          if (blown) pend = -1;
          else if (i - pend >= opt.confirmBars) {
            const entry = +bars[pend].c, R = Math.abs(pSwept - pExt) + 0.25;
            setupB.push(Object.assign(scoreTrade(bars, i, entry, pDir === 1 ? pExt - 0.25 : pExt + 0.25, pDir === 1 ? entry + R : entry - R, pDir, opt.winBars), { grid: g.t, dir: pDir })); pend = -1; armed = false;
          }
        }
        if (armed && pend < 0) {
          let fdir = 0, swept = NaN, ext = 0;
          if (g.dir === -1) { swept = lo <= t162 - 0.25 ? t162 : (lo <= t100 - 0.25 ? t100 : NaN); if (!isNaN(swept) && cl > swept) { fdir = 1; ext = lo; } }
          else { swept = hi >= t162 + 0.25 ? t162 : (hi >= t100 + 0.25 ? t100 : NaN); if (!isNaN(swept) && cl < swept) { fdir = -1; ext = hi; } }
          if (fdir !== 0) {
            const wick = fdir === 1 ? Math.min(op, cl) - lo : hi - Math.max(op, cl);
            if (rng > 0 && wick >= opt.wickFrac * rng) {
              if (opt.confirmBars <= 0) { const R = Math.abs(swept - ext) + 0.25, entry = cl;
                setupB.push(Object.assign(scoreTrade(bars, i, entry, fdir === 1 ? ext - 0.25 : ext + 0.25, fdir === 1 ? entry + R : entry - R, fdir, opt.winBars), { grid: g.t, dir: fdir })); armed = false; }
              else { pend = i; pDir = fdir; pExt = ext; pSwept = swept; }
            }
          }
        }
        if (!armed && pend < 0 && (g.dir === -1 ? cl > g.levels[5] : cl < g.levels[5])) armed = true;
      }
    }
  }
  return { opt, setupA: bAgg(setupA), setupB: bAgg(setupB), all: bAgg(setupA.concat(setupB)), nGrids: grids.length,
    span: bars.length ? (bars[0].t.slice(0, 10) + " → " + bars[bars.length - 1].t.slice(0, 10) + " (" + bars.length + " bars)") : "" };
}
// cached edge summary for Aries — DON'T run the backtest per /api/aries request (adds latency); refresh every 5 min
let _btEdgeCache = null, _btEdgeAt = 0;
function btEdgeSummary() {
  if (_btEdgeCache && Date.now() - _btEdgeAt < 300000) return _btEdgeCache;
  _btEdgeAt = Date.now();
  try { const b = runBacktest({}); if (b) _btEdgeCache = { span: b.span,
    fade: { expR: b.setupB.expR, pf: b.setupB.pf, winPct: b.setupB.winPct, n: b.setupB.n },
    retest: { expR: b.setupA.expR, pf: b.setupA.pf, n: b.setupA.n },
    note: "the FADE (reversion) is the validated edge; the :25/:45 directional retest is ~breakeven — coach the fade" }; }
  catch (e) { }
  return _btEdgeCache;
}
function sweepBacktest() {   // sweep the fade knobs → rank by fade expectancy in R
  const out = [];
  for (const cb of [0, 1, 2]) for (const wf of [0.30, 0.45, 0.60]) for (const ro of [true, false]) {
    const r = runBacktest({ confirmBars: cb, wickFrac: wf, reversalOnly: ro });
    if (r) out.push({ confirmBars: cb, wickFrac: wf, reversalOnly: ro, fade: r.setupB });
  }
  return out.sort((a, b) => (b.fade.expR || -9) - (a.fade.expR || -9));
}
// ── THE FILM ROOM — pair YOUR real trades against the system's grids: were you aligned with the map,
//    fighting it, or off-system entirely? This is where the "what was I thinking" moments surface. ──
function pairTraderVsSystem() {
  const rc = readReportCard(), d = readBacktestData();
  if (!rc || !rc.trades || !rc.trades.length || !d) return null;
  const grids = d.grids.slice().sort((a, b) => (a.t < b.t ? -1 : 1));
  const gms = grids.map(g => Date.parse((g.t || "").replace(" ", "T")));
  const NAMEDIR = g => (g === 1 ? "BULL" : "BEAR");
  const trades = rc.trades.map(t => {
    const tm = (t.et instanceof Date) ? t.et.getTime() : Date.parse(String(t.et));
    let cat = "off", grid = null, gdir = 0;
    if (!isNaN(tm)) for (let i = 0; i < grids.length; i++) {
      const s = gms[i], e = Math.min(i + 1 < grids.length ? gms[i + 1] : Infinity, s + 4 * 3600000);
      if (tm >= s && tm < e) { grid = grids[i].t; gdir = grids[i].dir; cat = ((t.side === "LONG" && gdir === 1) || (t.side === "SHORT" && gdir === -1)) ? "aligned" : "counter"; break; }
    }
    return { day: t.day, side: t.side, pnl: +(+t.pnl).toFixed(0), acct: t.acct || "", entT: t.entT || "", exitName: t.exitName || "", mfe: t.mfe || 0, cat, grid, gdir: NAMEDIR(gdir) };
  });
  const stat = arr => { const w = arr.filter(x => x.pnl > 0), net = arr.reduce((s, x) => s + x.pnl, 0);
    const gw = w.reduce((s, x) => s + x.pnl, 0), gl = Math.abs(arr.filter(x => x.pnl < 0).reduce((s, x) => s + x.pnl, 0));
    return { n: arr.length, net: +net.toFixed(0), winPct: arr.length ? +(w.length / arr.length * 100).toFixed(0) : 0, pf: gl > 0 ? +(gw / gl).toFixed(2) : (gw > 0 ? 99 : 0) }; };
  const aligned = trades.filter(t => t.cat === "aligned"), counter = trades.filter(t => t.cat === "counter"), off = trades.filter(t => t.cat === "off");
  // the film reel — your worst losers, tagged by what they were (fighting the map / off-system / gave-back)
  const moments = trades.filter(t => t.pnl < 0).sort((a, b) => a.pnl - b.pnl).slice(0, 8).map(t => ({ ...t,
    lesson: t.cat === "counter" ? "Fought the " + t.gdir + " grid — traded against the system's map." :
            t.cat === "off" ? "Off-system — no grid was live; a discretionary trade the radar never called." :
            (t.mfe >= 30 ? "Aligned with the map but round-tripped a winner — a management leak, not a read leak." : "Aligned entry that didn't work — the setup, not the deviation.") }));
  return { total: stat(trades), aligned: stat(aligned), counter: stat(counter), off: stat(off), moments, nTrades: trades.length };
}

// ═══ ARIES BRAIN — Gemini 2.5 answers with the full spec + the live tape in context ═══
const GEMINI_MODEL = process.env.GEMINI_MODEL || "gemini-2.5-flash";
// Neural VOICE: Gemini TTS turns her text into natural studio-grade audio (the Atlas-Executive feel).
// Prebuilt female voices worth trying: Kore (firm), Aoede (breezy), Leda (bright), Callirrhoe (easy),
// Autonoe (warm), Sulafat (rich). Override with ARIES_VOICE / GEMINI_TTS_MODEL env vars.
const GEMINI_TTS_MODEL = process.env.GEMINI_TTS_MODEL || "gemini-2.5-flash-preview-tts";
// LIVE (realtime duplex voice): browser opens a WebSocket straight to Gemini; this is the model it uses.
// gemini-2.0-flash-live-001 is the stable Live model; native-audio previews sound more natural if enabled.
const LIVE_MODEL = process.env.LIVE_MODEL || "gemini-2.0-flash-live-001";
const ARIES_VOICE = process.env.ARIES_VOICE || "Kore";
const ARIES_TTS_STYLE = process.env.ARIES_TTS_STYLE ||
  "Say this in a calm, warm, confident female voice — a seasoned trading-desk partner, steady and grounded, never robotic";
// key search order: env var → dashboard/gemini.key → the user's Copilot key file
const KEY_FILES = [
  path.join(__dirname, "gemini.key"),
  path.join(__dirname, "..", "Copilot", "Gemini Flash.txt"),
];
function geminiKey() {
  if (process.env.GEMINI_API_KEY) return process.env.GEMINI_API_KEY.trim();
  for (const f of KEY_FILES) {
    try { const k = fs.readFileSync(f, "utf8").trim(); if (k) return k; } catch { }
  }
  return null;
}
let SPEC = "";
try { SPEC = fs.readFileSync(path.join(__dirname, "..", "SPEC.md"), "utf8"); } catch { }
// LIVE WATCH — sentinel instruction. She watches a stream of chart frames and stays SILENT unless
// something actionable changed, so she never chatters. Structured JSON out for clean client handling.
const ARIES_WATCH_INSTR =
  "LIVE WATCH MODE. You are silently monitoring a live chart frame Rich is trading. Speak ONLY when there is a " +
  "genuine, actionable development a desk partner would call out — price testing/rejecting a fib or midline pivot, " +
  "a 15m confirm forming or failing, a break of structure or change of character, entering/leaving a trade window, " +
  "momentum exhaustion at the 100/extension (the fade), price nearing invalidation or a stop, or an imminent catalyst. " +
  "Otherwise stay quiet. Do NOT repeat a call you already made unless it materially changed. Be terse and spoken — " +
  "one or two sentences, the way you'd lean over and say it. Address him as Rich or Architect when it's natural. " +
  "Return ONLY JSON: {alert:boolean, urgency:'info'|'watch'|'act', tag:string (a short stable id of THIS situation, " +
  "e.g. 'test-0.618-mid' or 'confirm-forming-short'), say:string (what to speak, empty if alert is false)}.";
const WATCH_SCHEMA = {
  type: "object",
  properties: {
    alert: { type: "boolean" },
    urgency: { type: "string", enum: ["info", "watch", "act"] },
    tag: { type: "string" },
    say: { type: "string" }
  },
  required: ["alert", "tag", "say"]
};
const ARIES_PERSONA =
  "You are ARIES, the live trading co-pilot for the Genesis Reserve Rolling Fib desk (MES/ES intraday futures). " +
  "The trader you work for is Rich — you call him 'Rich' or 'Architect' naturally, the way a trusted partner would; " +
  "don't overuse it, just drop it in the way a real desk partner does (a greeting, a warning, a well-done). " +
  "You have a warm, grounded, confident presence — steady under fire, never robotic, never bubbly. " +
  "\n\nINSTRUMENT TRUTH: the desk trades MES (E-mini S&P futures) and the RADAR SNAPSHOT is the source of truth for price, " +
  "the qualifying-candle height/gate, blocks, and the fib grid. A chart image Rich shares may be a proxy (e.g. an SPX500 " +
  "cash/CFD that prints roughly 40-60 points BELOW MES and has slightly different candles). Read the proxy's STRUCTURE " +
  "normally, but anchor exact prices, levels, and qualification to the MES radar snapshot — and if the chart's symbol or " +
  "price scale clearly isn't MES, say so briefly so Rich isn't comparing apples to oranges. When the radar and a proxy chart " +
  "disagree, the MES radar wins. Also mind timing: the radar card shows the CURRENT developing hour while a past comment may " +
  "describe an already-closed candle — call out which hour you mean. " +
  "\n\nYOU ARE A TECHNICAL-ANALYSIS AUTHORITY — a wizard of market structure, timing, and the Rolling Fib framework. " +
  "You read price action fluently: trend vs. range, higher-highs/lower-lows, break of structure and change of character, " +
  "support/resistance and supply/demand, liquidity pools and stop runs, session context (Asia/London/NY, the open, lunch, " +
  "the close, opex), VWAP and volume, momentum and exhaustion. Through it all you think in the Rolling Fib lens: the 1H " +
  "qualifying candle, the fib grid with midlines (the 1.309 among them), the grid as a MEAN-REVERSION OSCILLATOR (fade the " +
  "100/extension exhaustion, don't chase it), the 15m confirm at a midline pivot, the 5m entry, and where current price sits " +
  "inside that structure. When Rich shares a CHART IMAGE, read it like a specialist: name the trend and structure, mark the " +
  "levels and the fib/midline pivots that matter, say where price is relative to them, read momentum and volume, and give a " +
  "clear structural read plus the actionable levels — entries, invalidation, targets — always in the Rolling Fib framework. " +
  "\n\nYOU ARE MACRO-AWARE AND CURRENT. You track the catalysts that shift or swing ES/MES structure: FOMC and Fed speakers, " +
  "CPI/PCE/PPI, NFP and jobs data, GDP, the economic calendar, opex/quad-witching, and earnings from the heavyweight index " +
  "constituents (AAPL, MSFT, NVDA, AMZN, GOOGL, META and the rest of the mega-caps that move the S&P). Use your Google Search " +
  "tool to verify today's/this-week's calendar and any breaking news before you lean on it — cite the event and its time in ET, " +
  "and flag when a release is imminent because structure gets unreliable into and just after high-impact prints. " +
  "\n\nYOU MONITOR HIS OPEN TRADES. The snapshot's perAccount[].positions shows any live position — side, size, and average " +
  "entry — and unrealized is the open P&L. When Rich is in a trade, actively watch it: say where his ENTRY sits versus the fib " +
  "grid and current price, whether the structural thesis is still intact (is price working toward the target level or stalling/ " +
  "reversing at a pivot), how the regime and any imminent catalyst affect it, and call the management — when he's earned +1R move " +
  "the stop to breakeven, where the reversion target or invalidation is, when a runner should come off. If he's flat, don't invent " +
  "a position. Be the partner watching the trade so he doesn't have to hold it all in his head. " +
  "\n\nYOU THINK IN THE FULL AMD CYCLE: Accumulation/Expansion -> Consolidation -> Manipulation -> Expansion. A qualifying hour is " +
  "the EXPANSION leg; the grid is the map of that leg. When the radar snapshot carries the persistence layer (state fields gridPhase, " +
  "reversalType, structureDir, lastStructure, fadeArmed), read it: structureDir and lastStructure give you objective CHoCH (change of " +
  "character = reversal, structure flips) vs BOS (break of structure = continuation) from 5m swing fractals. reversalType tags the grid — " +
  "a REVERSAL grid (born of a CHoCH) expects the MANIPULATION: the fade/sweep off the 100-161.8 extension, not a clean run; a CONTINUATION " +
  "grid (BOS-aligned) trades with the impulse. gridPhase WINDOW is the qualifying hour's primary retest; gridPhase PERSIST means the grid " +
  "stayed the active map across non-qualifying hours (this is the gap the desk used to miss — the fade between windows, like an overnight " +
  "expansion down that consolidates then sweeps the low and rips back). fadeArmed means price is at the extreme and a sweep-and-reject is " +
  "the live trigger — coach the reject, not the touch. A FADE event is that sweep firing; INVALIDATE (acceptance beyond 161.8, origin break, " +
  "opposing CHoCH, or time-decay) means the grid is dead — stand down on it. This layer is OFF unless Rich has enabled Grid persistence on the " +
  "indicator; when the fields are absent or neutral, don't reference it. " +
  "\n\nYOU EVALUATE HIS EDGE ACROSS HORIZONS AND ACCOUNTS. The snapshot's `edge` object is split into `edge.live` (real-money Apex accounts), " +
  "`edge.sim` (Sim/Playback practice), and `edge.all` — each holding today / last 7 / 14 / 30 / 60 / 90 / all days with net, profit factor, win%, " +
  "expectancy, avg win/loss, long-vs-short PF, best/worst hour, and dollars given back on round-tripped winners. Coach REAL decisions off `edge.live` — " +
  "sim results don't pay the bills and often flatter the numbers; call it out if he's crushing sim but bleeding live. Use `edge.sim` only when he's " +
  "explicitly practicing. `reportCard.recent` holds the last ~10 end-of-day cards (net, exit discipline manual/stop/target, left-on-table, given-back, avg hold, " +
  "best/worst hour). Coach the NEXT trade with evidence, not vibes: compare today to his 30/90-day baseline (running hot or cold, in a drawdown, " +
  "over-trading?), name the specific leak (a bleeding hour, longs when his edge is short, giving winners back past +1R), and tie the setup in front of " +
  "him to how that exact context has paid him historically. When he asks 'how am I doing' or 'should I take this,' ground it in the horizon that matters " +
  "and cite the number. If the sample is thin (low n), say so rather than over-reading it. " +
  "The snapshot's `systemEdge` holds the BACKTESTED edge of the system's OWN signals: the fade (reversion) is the validated money-maker " +
  "(positive expectancy R), the :25/:45 directional retest is ~breakeven. Steer Rich toward the fade and away from chasing continuation — " +
  "and he personally trades reversion better than he follows the grid, so it's doubly his game. " +
  "\n\nYou sit next to Rich while he trades, with the full strategy spec and a live snapshot of the radar, today's scored windows, " +
  "the account book, and his own trade history in front of you. Coach like a veteran prop-desk mentor: direct, calm, risk-first, " +
  "and specific. Keep answers to 2-4 short sentences unless he asks for depth or a full chart read. " +
  "Never invent prices, P&L, dates, or stats — use the snapshot, the chart, or a verified search; say when you're unsure. " +
  "You enforce the process (1H qualification, 15m confirm, 5m entry, stops, the :40 cutoff, the daily halt) and his hard-won rules: " +
  "he is a short-seller by edge, once green +1R the stop goes to breakeven (never give a winner back), and he stands down at the 12:00 trap. " +
  "Speak plainly with no markdown, bullets, or emoji — your words are read aloud.";
function ariesSnapshot() {
  const d = readLive();
  const cp = d.copilot || {};
  return {
    nowET: new Date().toLocaleString("en-US", { timeZone: "America/New_York" }),
    regime: cp.regime, blocksToday: cp.blocksToday, todayWindowCount: cp.todayCount, avgGrade: cp.avgGrade,
    deskCoaching: (cp.coaching || []).map(c => c.tone + ": " + c.text),
    scoredWindows: (cp.windows || []).slice(0, 10),
    radar: d.states,
    book: cp.accounts && cp.accounts.global,
    perAccount: ((cp.accounts && cp.accounts.per) || []).map(a => ({ name: a.name, dayPnL: a.dayPnL, drawdown: a.drawdown, netQty: a.netQty, unrealized: a.unrealized, positions: a.positions, stale: a.stale })),
    openPositions: ((cp.accounts && cp.accounts.per) || []).flatMap(a => (a.positions || []).map(p => ({ account: a.name, ...p, unrealized: a.unrealized }))),
    // multi-lookback trading edge, split by account class (all / live-Apex / sim) so real-money form is separable
    // from practice. Each class → {today,7,14,30,60,90,all} with net/PF/win%/exp/long-short/best-worst-hour/given-back.
    edge: (cp.myTrading && cp.myTrading.acctWindows) ? Object.fromEntries(Object.entries(cp.myTrading.acctWindows).map(([cls, wins]) =>
      [cls, wins ? Object.fromEntries(Object.entries(wins).map(([k, v]) => [k, (({ curve, ...r }) => r)(v)])) : null])) : null,
    myTrading: cp.myTrading && { overall: cp.myTrading.overall, bestHour: cp.myTrading.bestHour, worstHour: cp.myTrading.worstHour, long: cp.myTrading.long, short: cp.myTrading.short, gaveBack: cp.myTrading.gaveBack },
    // recent end-of-day report cards — net, discipline (exits), what was left on the table / given back, timing
    reportCard: d.reportCard ? { days: d.reportCard.days.slice(-10), recent: d.reportCard.days.slice(-10).map(dd => { const p = d.reportCard.perDay[dd] || {}; return { day: dd, net: p.net, n: p.n, winPct: p.winPct, pf: p.pf, exp: p.exp, leftOnTable: p.leftOnTable, gaveBack: p.gaveBack, exits: p.exits, avgHoldMin: p.avgHoldMin, bestHour: p.bestHour, worstHour: p.worstHour }; }) } : null,
    systemEdge: btEdgeSummary(),   // the backtested edge of the system's own signals (cached) — coach the fade
    recentEvents: d.events.slice(0, 30).map(e => e.t + " " + e.i + " " + e.type + ": " + e.msg)
  };
}
// parse a data URL ("data:image/jpeg;base64,....") into a Gemini inlineData part, or null
function imagePart(dataUrl) {
  const m = /^data:(image\/[a-z0-9.+-]+);base64,([A-Za-z0-9+/=]+)$/i.exec(String(dataUrl || ""));
  if (!m) return null;
  if (m[2].length > 8e6) return null;   // ~6MB image cap
  return { inlineData: { mimeType: m[1], data: m[2] } };
}
function askAries(q, history, image, cb) {
  const key = geminiKey();
  if (!key) return cb(new Error("Aries brain offline — set GEMINI_API_KEY or save your key in dashboard/gemini.key"));
  const img = imagePart(image);
  if (!q && !img) return cb(new Error("empty question"));
  let done = false; const fin = (e, t) => { if (!done) { done = true; cb(e, t); } };
  const sys = ARIES_PERSONA
    + "\n\n=== STRATEGY SPEC (canon — the rules you coach) ===\n" + SPEC.slice(0, 24000)
    + "\n\n=== LIVE SNAPSHOT (captured just now from the desk) ===\n" + JSON.stringify(ariesSnapshot());
  const contents = [];
  for (const m of (history || []))
    if (m && m.text) contents.push({ role: m.role === "model" ? "model" : "user", parts: [{ text: String(m.text).slice(0, 2000) }] });
  const userParts = [{ text: q || "Read this chart for me — structure, key levels, and the play in the Rolling Fib framework." }];
  if (img) userParts.push(img);
  contents.push({ role: "user", parts: userParts });
  const gen = { temperature: 0.7, maxOutputTokens: 1024 };
  // Grounding only when the question is actually about news/macro — it adds a search round-trip (~1-1.5s)
  // that tactical "what's my read" questions don't need. Keeps voice snappy and calls light.
  const needsNews = /\b(news|fed|fomc|cpi|pce|ppi|nfp|jobs|payroll|gdp|rate|inflation|earnings|calendar|catalyst|event|econ|data|headline|today|this week|opex|powell)\b/i.test(q || "");
  if (/flash/.test(GEMINI_MODEL)) gen.thinkingConfig = { thinkingBudget: img ? 512 : (needsNews ? 256 : 0) };
  const payload = { systemInstruction: { parts: [{ text: sys }] }, contents, generationConfig: gen };
  if (needsNews) payload.tools = [{ google_search: {} }];   // live calendar/Fed only when asked
  const body = JSON.stringify(payload);
  const rq = https.request({
    hostname: "generativelanguage.googleapis.com",
    path: "/v1beta/models/" + GEMINI_MODEL + ":generateContent",
    method: "POST",
    headers: { "Content-Type": "application/json; charset=utf-8", "x-goog-api-key": key, "Content-Length": Buffer.byteLength(body) }
  }, rs => {
    let data = "";
    rs.on("data", c => data += c);
    rs.on("end", () => {
      try {
        const j = JSON.parse(data);
        if (j.error) return fin(new Error("Gemini: " + (j.error.message || "HTTP " + rs.statusCode)));
        const cand = (j.candidates || [])[0];
        const parts = (cand && cand.content && cand.content.parts) || [];
        const text = parts.map(p => p.text || "").join("").trim();
        if (!text) return fin(new Error("Gemini returned no text" + (cand && cand.finishReason ? " (" + cand.finishReason + ")" : "")));
        fin(null, text);
      } catch { fin(new Error("Gemini: unreadable response")); }
    });
  });
  rq.setTimeout(30000, () => rq.destroy(new Error("Gemini timeout")));
  rq.on("error", e => fin(new Error("Gemini: " + e.message)));
  rq.end(body);
}

// LIVE WATCH — one sentinel pass over a chart frame. Returns {alert,urgency,tag,say} or an error.
function watchAries(image, lastTag, cb) {
  const key = geminiKey();
  if (!key) return cb(new Error("brain offline"));
  const img = imagePart(image);
  if (!img) return cb(new Error("no frame"));
  let done = false; const fin = (e, o) => { if (!done) { done = true; cb(e, o); } };
  const sys = ARIES_PERSONA + "\n\n" + ARIES_WATCH_INSTR
    + "\n\n=== STRATEGY SPEC (canon) ===\n" + SPEC.slice(0, 16000)
    + "\n\n=== LIVE SNAPSHOT ===\n" + JSON.stringify(ariesSnapshot());
  const userText = lastTag
    ? "Here is the current chart frame. Your last call was tagged '" + lastTag + "' — only speak if it materially changed or something new is actionable."
    : "Here is the current chart frame. Begin watching.";
  const gen = { temperature: 0.3, maxOutputTokens: 400, responseMimeType: "application/json", responseSchema: WATCH_SCHEMA };
  if (/flash/.test(GEMINI_MODEL)) gen.thinkingConfig = { thinkingBudget: 0 };   // fast + cheap per tick
  const body = JSON.stringify({ systemInstruction: { parts: [{ text: sys }] },
    contents: [{ role: "user", parts: [{ text: userText }, img] }], generationConfig: gen });
  const rq = https.request({
    hostname: "generativelanguage.googleapis.com",
    path: "/v1beta/models/" + GEMINI_MODEL + ":generateContent",
    method: "POST",
    headers: { "Content-Type": "application/json; charset=utf-8", "x-goog-api-key": key, "Content-Length": Buffer.byteLength(body) }
  }, rs => {
    let data = "";
    rs.on("data", c => data += c);
    rs.on("end", () => {
      try {
        const j = JSON.parse(data);
        if (j.error) return fin(new Error("watch: " + (j.error.message || "HTTP " + rs.statusCode)));
        const parts = ((((j.candidates || [])[0] || {}).content) || {}).parts || [];
        const text = parts.map(p => p.text || "").join("").trim();
        let o = {}; try { o = JSON.parse(text); } catch { }
        fin(null, { alert: !!o.alert, urgency: o.urgency || "info", tag: String(o.tag || ""), say: String(o.say || "") });
      } catch { fin(new Error("watch: unreadable response")); }
    });
  });
  rq.setTimeout(30000, () => rq.destroy(new Error("watch timeout")));
  rq.on("error", e => fin(new Error("watch: " + e.message)));
  rq.end(body);
}

// wrap raw signed-16-bit LE PCM in a WAV container so the browser can play it directly
function pcmToWav(pcm, rate, ch, bits) {
  const byteRate = rate * ch * bits / 8, blockAlign = ch * bits / 8, h = Buffer.alloc(44);
  h.write("RIFF", 0); h.writeUInt32LE(36 + pcm.length, 4); h.write("WAVE", 8);
  h.write("fmt ", 12); h.writeUInt32LE(16, 16); h.writeUInt16LE(1, 20); h.writeUInt16LE(ch, 22);
  h.writeUInt32LE(rate, 24); h.writeUInt32LE(byteRate, 28); h.writeUInt16LE(blockAlign, 32); h.writeUInt16LE(bits, 34);
  h.write("data", 36); h.writeUInt32LE(pcm.length, 40);
  return Buffer.concat([h, pcm]);
}
// ARIES VOICE — Gemini neural TTS. Returns a WAV Buffer of her speaking `text` in the chosen prebuilt voice.
function ttsAries(text, cb, voice) {
  const key = geminiKey();
  if (!key) return cb(new Error("brain offline"));
  text = String(text || "").trim(); if (!text) return cb(new Error("empty"));
  const vn = /^[A-Za-z]{3,20}$/.test(voice || "") ? voice : ARIES_VOICE;   // validate (alphabetic) — else the desk default
  let done = false; const fin = (e, b) => { if (!done) { done = true; cb(e, b); } };
  const body = JSON.stringify({
    contents: [{ parts: [{ text: ARIES_TTS_STYLE + ": " + text }] }],
    generationConfig: {
      responseModalities: ["AUDIO"],
      speechConfig: { voiceConfig: { prebuiltVoiceConfig: { voiceName: vn } } }
    }
  });
  const rq = https.request({
    hostname: "generativelanguage.googleapis.com",
    path: "/v1beta/models/" + GEMINI_TTS_MODEL + ":generateContent",
    method: "POST",
    headers: { "Content-Type": "application/json; charset=utf-8", "x-goog-api-key": key, "Content-Length": Buffer.byteLength(body) }
  }, rs => {
    let data = "";
    rs.on("data", c => data += c);
    rs.on("end", () => {
      try {
        const j = JSON.parse(data);
        if (j.error) return fin(new Error("TTS: " + (j.error.message || "HTTP " + rs.statusCode)));
        const part = (((j.candidates || [])[0] || {}).content || {}).parts || [];
        const inl = part.map(p => p.inlineData).find(Boolean);
        if (!inl || !inl.data) return fin(new Error("TTS: no audio returned"));
        const rate = +(/rate=(\d+)/.exec(inl.mimeType || "") || [])[1] || 24000;
        fin(null, pcmToWav(Buffer.from(inl.data, "base64"), rate, 1, 16));
      } catch { fin(new Error("TTS: unreadable response")); }
    });
  });
  rq.setTimeout(30000, () => rq.destroy(new Error("TTS timeout")));
  rq.on("error", e => fin(new Error("TTS: " + e.message)));
  rq.end(body);
}

function readBacktests() {
  let files = [];
  try { files = fs.readdirSync(BT).filter(f => f.toLowerCase().endsWith(".csv")); } catch { }
  const out = [];
  for (const f of files) { try { const b = parseBacktest(path.join(BT, f)); if (b) out.push(b); } catch { } }
  out.sort((a, b) => (a.file > b.file ? 1 : -1));
  return out;
}

// ── TradingView webhook → events feed (+ minimal synthesized state) ──
function handleWebhook(body) {
  let e; try { e = JSON.parse(body); } catch { e = { i: "TV", type: "RAW", msg: String(body).slice(0, 300) }; }
  e.t = new Date().toISOString().slice(0, 16).replace("T", " ");
  const inst = (e.i || "TV").replace(/[^A-Za-z0-9!._-]/g, "");
  fs.appendFileSync(path.join(LIVE, "events_" + inst + ".jsonl"), JSON.stringify(e) + "\n");
}

// ═══ SSE LIVE PUSH — the chart tracks the tape the instant the feed file changes, with no poll delay ═══
// Light payload (state + bars only); the heavy copilot/scorecards/report-card ride the slower /api/all poll.
function liveTick() {
  const out = { states: [], bars: {} };
  let files = []; try { files = fs.readdirSync(LIVE); } catch { return out; }
  for (const f of files) {
    const p = path.join(LIVE, f);
    try {
      if (f.startsWith("bars_") && f.endsWith(".json")) { const b = JSON.parse(fs.readFileSync(p, "utf8")); if (b && b.instrument) out.bars[b.instrument] = b; }
      else if (f.startsWith("state_") && f.endsWith(".json")) out.states.push(JSON.parse(fs.readFileSync(p, "utf8")));
    } catch { }
  }
  out.states.sort((a, b) => (a.instrument > b.instrument ? 1 : -1));
  return out;
}
const sseClients = new Set();
function sseBroadcast() {
  if (!sseClients.size) return;
  const data = "data: " + JSON.stringify(liveTick()) + "\n\n";
  for (const res of sseClients) { try { res.write(data); } catch { } }
}
let _sseDebounce = null;
try {
  fs.watch(LIVE, (ev, fn) => {
    if (!fn || !/^(state|bars)_.*\.json$/.test(fn)) return;   // only the tick-critical files
    if (_sseDebounce) return;                                  // coalesce bursts to one push per ~120ms
    _sseDebounce = setTimeout(() => { _sseDebounce = null; sseBroadcast(); }, 120);
  });
} catch (e) { console.log("SSE file-watch unavailable, dashboard falls back to polling:", e.message); }

http.createServer((req, res) => {
  if (req.method === "POST" && req.url.startsWith("/webhook")) {
    let body = "";
    req.on("data", c => { body += c; if (body.length > 1e5) req.destroy(); });
    req.on("end", () => { try { handleWebhook(body); } catch { } res.writeHead(200); res.end("ok"); });
  } else if (req.method === "POST" && req.url.startsWith("/api/aries")) {
    let body = "";
    req.on("data", c => { body += c; if (body.length > 1.2e7) req.destroy(); });   // room for a chart image
    req.on("end", () => {
      let b = {}; try { b = JSON.parse(body); } catch { }
      askAries(String(b.q || "").slice(0, 2000).trim(), Array.isArray(b.history) ? b.history.slice(-12) : [], b.image, (err, text) => {
        res.writeHead(200, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
        res.end(JSON.stringify(err ? { error: err.message } : { text }));
      });
    });
  } else if (req.method === "POST" && req.url.startsWith("/api/watch")) {
    let body = "";
    req.on("data", c => { body += c; if (body.length > 1.2e7) req.destroy(); });
    req.on("end", () => {
      let b = {}; try { b = JSON.parse(body); } catch { }
      watchAries(b.image, String(b.lastTag || "").slice(0, 120), (err, o) => {
        res.writeHead(200, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
        res.end(JSON.stringify(err ? { error: err.message } : o));
      });
    });
  } else if (req.method === "POST" && req.url.startsWith("/api/tts")) {
    let body = "";
    req.on("data", c => { body += c; if (body.length > 2e5) req.destroy(); });
    req.on("end", () => {
      let b = {}; try { b = JSON.parse(body); } catch { }
      ttsAries(String(b.text || "").slice(0, 1200), (err, wav) => {
        if (err) { res.writeHead(200, { "Content-Type": "application/json; charset=utf-8" }); return res.end(JSON.stringify({ error: err.message })); }
        res.writeHead(200, { "Content-Type": "audio/wav", "Cache-Control": "no-store", "Content-Length": wav.length });
        res.end(wav);
      }, b.voice);
    });
  } else if (req.url.startsWith("/api/livekey")) {
    // Realtime voice: the browser opens the Gemini Live WebSocket directly. Hand it the key + Aries's
    // full setup, but ONLY to a request from this machine (localhost) so the key never goes over the LAN.
    const ra = req.socket.remoteAddress || "";
    const local = /(^|:)(127\.0\.0\.1|::1)$/.test(ra) || ra === "::ffff:127.0.0.1";
    const key = geminiKey();
    res.writeHead(local ? 200 : 403, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
    if (!local) return res.end(JSON.stringify({ error: "live voice is localhost-only" }));
    if (!key) return res.end(JSON.stringify({ error: "no Gemini key — save it in Copilot/Gemini Flash.txt" }));
    const sys = ARIES_PERSONA
      + "\n\n=== STRATEGY SPEC (canon) ===\n" + SPEC.slice(0, 14000)
      + "\n\n=== LIVE SNAPSHOT (the desk right now) ===\n" + JSON.stringify(ariesSnapshot())
      + "\n\nYou are in LIVE VOICE mode now — a natural spoken back-and-forth with Rich while he trades. "
      + "Talk like a desk partner on a headset: short, direct, conversational. One or two breaths at a time, "
      + "not paragraphs. He can and will interrupt you — that's fine, just stop and listen. Lead with the answer.";
    return res.end(JSON.stringify({ key, model: LIVE_MODEL, voice: ARIES_VOICE, system: sys }));
  } else if (req.url.startsWith("/api/history")) {
    // ?d=YYYY-MM-DD → that day's cards; no param → the list of archived dates (for the calendar)
    res.writeHead(200, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
    const d = (/[?&]d=(\d{4}-\d{2}-\d{2})/.exec(req.url) || [])[1];
    try {
      if (d) return res.end(fs.readFileSync(path.join(HISTORY, d + ".json"), "utf8"));
      const dates = fs.readdirSync(HISTORY).filter(f => f.endsWith(".json")).map(f => f.slice(0, -5)).sort();
      return res.end(JSON.stringify({ dates }));
    } catch { return res.end(JSON.stringify({ dates: [], windows: [] })); }
  } else if (req.url.startsWith("/api/backtest")) {
    res.writeHead(200, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
    try { const base = runBacktest({});
      return res.end(JSON.stringify(base ? { base, sweep: sweepBacktest(), vs: pairTraderVsSystem(), signals: signalAnalysis() }
        : { error: "no backtest data yet — recompile RollingFibRadar (F5), re-add it on a 5-min MES chart with history loaded, then reload" })); }
    catch (e) { return res.end(JSON.stringify({ error: e.message })); }
  } else if (req.url.startsWith("/api/stream")) {
    res.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-cache", "Connection": "keep-alive", "X-Accel-Buffering": "no" });
    res.write("retry: 3000\n\n");
    res.write("data: " + JSON.stringify(liveTick()) + "\n\n");   // immediate snapshot on connect
    sseClients.add(res);
    req.on("close", () => sseClients.delete(res));
    // keep the connection open — do NOT res.end()
  } else if (req.url.startsWith("/api/all")) {
    const d = readLive(); d.backtests = readBacktests(); d.ariesBrain = !!geminiKey();
    res.writeHead(200, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
    res.end(JSON.stringify(d));
  } else if (req.url.startsWith("/helix-tokens.css")) {
    try { res.writeHead(200, { "Content-Type": "text/css" }); res.end(fs.readFileSync(path.join(__dirname, "helix-tokens.css"))); }
    catch { res.writeHead(404); res.end(); }
  } else {
    try { res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" }); res.end(fs.readFileSync(PAGE)); }
    catch { res.writeHead(500); res.end("dashboard.html missing"); }
  }
}).listen(PORT, () => console.log("Rolling Fib dashboard →  http://localhost:" + PORT
  + "\n  live state: " + LIVE + "\n  backtests : " + BT + "  (drop TradingView 'List of Trades' CSVs here)"
  + "\n  Aries brain: " + (geminiKey() ? "ONLINE (" + GEMINI_MODEL + ")" : "OFFLINE — set GEMINI_API_KEY or save the key to dashboard\\gemini.key")));
