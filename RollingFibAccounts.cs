// ═══════════════════════════════════════════════════════════════════════════════
//  ROLLING FIB ACCOUNTS — NinjaTrader 8 INDICATOR (Apex account monitor)  v1.0
//
//  Reads EVERY connected account (your 4 Apex/Rithmic accounts) and writes each one's
//  live balance, day P&L, open position, and today's fill stats to the dashboard feed.
//  The dashboard then shows a live Performance panel — one card per account.
//
//  APPLY ONCE: add to any single chart (right-click → Indicators → RollingFibAccounts).
//  It monitors ALL accounts, not just the chart's — so one instance covers all four.
//  Live account P&L only streams in REALTIME (a live/sim connection must be up).
// ═══════════════════════════════════════════════════════════════════════════════
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class RollingFibAccounts : Indicator
    {
        private readonly List<Account> subscribed = new List<Account>();
        private readonly Dictionary<string, DateTime> lastWrite = new Dictionary<string, DateTime>();
        private readonly HashSet<string> seenExec = new HashSet<string>();   // execution ids already logged (idempotent across reloads)
        private readonly object exLock = new object();
        private TimeZoneInfo tzEt;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name             = "RollingFibAccounts";
                Description      = "Broadcasts every connected account's live P&L to the Rolling Fib dashboard";
                Calculate        = Calculate.OnBarClose;
                IsOverlay        = true;
                DisplayInDataBox = false;
                StatePath        = @"C:\Users\mrbee\Documents\RollingFib\live";
            }
            else if (State == State.Configure)
            {
                tzEt = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                try { Directory.CreateDirectory(StatePath); } catch { }
                // load already-logged execution ids so re-adding the indicator never double-writes a fill
                try {
                    string f = Path.Combine(StatePath, "executions.jsonl");
                    if (File.Exists(f))
                        foreach (string ln in File.ReadAllLines(f)) {
                            int i = ln.IndexOf("\"id\":\"", StringComparison.Ordinal);
                            if (i < 0) continue; i += 6; int j = ln.IndexOf('"', i);
                            if (j > i) seenExec.Add(ln.Substring(i, j - i));
                        }
                } catch { }
            }
            else if (State == State.Realtime)
            {
                lock (Account.All)
                    foreach (Account a in Account.All)
                        Subscribe(a);
            }
            else if (State == State.Terminated)
            {
                foreach (Account a in subscribed)
                {
                    a.AccountItemUpdate -= OnAccountItemUpdate;
                    a.ExecutionUpdate   -= OnExecution;
                }
                subscribed.Clear();
            }
        }

        private void Subscribe(Account a)
        {
            if (a == null || subscribed.Contains(a)) return;
            a.AccountItemUpdate += OnAccountItemUpdate;
            a.ExecutionUpdate   += OnExecution;
            subscribed.Add(a);
            WriteAccount(a);   // initial snapshot
            // seed today's already-filled executions (deduped) so a reload captures trades taken before it loaded
            try { foreach (Execution ex in a.Executions) LogExecution(ex); } catch { }
        }

        private void OnExecution(object sender, ExecutionEventArgs e)
        {
            if (e == null || e.Execution == null) return;
            LogExecution(e.Execution);
        }
        // Append every fill to executions.jsonl (idempotent by execution id). The dashboard pairs these into
        // round-trip trades with realized P&L — so the edge + report card stay current with NO manual export.
        private void LogExecution(Execution ex)
        {
            if (ex == null) return;
            string id; try { id = ex.ExecutionId; } catch { return; }
            if (string.IsNullOrEmpty(id)) return;
            lock (exLock)
            {
                if (seenExec.Contains(id)) return;
                try
                {
                    string action = "";
                    try { if (ex.Order != null) action = ex.Order.OrderAction.ToString(); } catch { }
                    if (action.Length == 0) return;                 // need Buy/Sell/BuyToCover/SellShort to pair FIFO
                    double pv = 1, comm = 0; string instr = "", acct = "", name = "";
                    try { instr = ex.Instrument.MasterInstrument.Name; pv = ex.Instrument.MasterInstrument.PointValue; } catch { }
                    try { comm = ex.Commission; } catch { }
                    try { acct = ex.Account != null ? ex.Account.Name : ""; } catch { }
                    try { name = ex.Name ?? ""; } catch { }
                    DateTime et; try { et = TimeZoneInfo.ConvertTime(ex.Time, tzEt); } catch { et = TimeZoneInfo.ConvertTime(DateTime.Now, tzEt); }
                    string line = "{\"id\":\"" + id.Replace("\"", "'") + "\",\"account\":\"" + acct.Replace("\"", "'")
                        + "\",\"inst\":\"" + instr + "\",\"action\":\"" + action + "\",\"qty\":" + ex.Quantity
                        + ",\"price\":" + ex.Price.ToString("F2") + ",\"pv\":" + pv.ToString("F2") + ",\"comm\":" + comm.ToString("F2")
                        + ",\"name\":\"" + name.Replace("\"", "'") + "\",\"t\":\"" + et.ToString("yyyy-MM-dd HH:mm:ss") + "\"}\n";
                    File.AppendAllText(Path.Combine(StatePath, "executions.jsonl"), line);
                    seenExec.Add(id);
                }
                catch { }
            }
        }

        private void OnAccountItemUpdate(object sender, AccountItemEventArgs e)
        {
            if (e == null || e.Account == null) return;
            // throttle: at most one write per account every 2 seconds
            DateTime last;
            if (lastWrite.TryGetValue(e.Account.Name, out last) && (DateTime.UtcNow - last).TotalSeconds < 2) return;
            lastWrite[e.Account.Name] = DateTime.UtcNow;
            WriteAccount(e.Account);
        }

        private double Item(Account a, AccountItem it)
        {
            try { return a.Get(it, Currency.UsDollar); } catch { return 0.0; }
        }

        private void WriteAccount(Account a)
        {
            if (State != State.Realtime && State != State.Historical) { }
            try
            {
                double cash   = Item(a, AccountItem.CashValue);
                double realized = Item(a, AccountItem.RealizedProfitLoss);    // day realized P&L
                double unreal = Item(a, AccountItem.UnrealizedProfitLoss);
                double buyPow = Item(a, AccountItem.BuyingPower);
                string conn   = a.Connection != null ? a.Connection.Status.ToString() : "—";

                // open positions: aggregate net + per-position detail (instrument, side, size, avg entry)
                // so Aries can relate the live trade to the fib structure and monitor it.
                int posCount = 0, netQty = 0;
                var pos = new StringBuilder();
                try {
                    foreach (Position p in a.Positions)
                    {
                        if (p.MarketPosition == MarketPosition.Flat) continue;
                        posCount++;
                        netQty += (p.MarketPosition == MarketPosition.Long ? 1 : -1) * p.Quantity;
                        if (pos.Length > 0) pos.Append(",");
                        pos.Append("{\"instr\":\"").Append(p.Instrument.MasterInstrument.Name)
                           .Append("\",\"side\":\"").Append(p.MarketPosition == MarketPosition.Long ? "LONG" : "SHORT")
                           .Append("\",\"qty\":").Append(p.Quantity)
                           .Append(",\"avg\":").Append(p.AveragePrice.ToString("F2")).Append("}");
                    }
                } catch { }

                // per-trade round-trips are now auto-captured via ExecutionUpdate → executions.jsonl (the
                // dashboard pairs them into trades). This card still shows the realized DAY P&L, the number
                // that matters intraday.
                int trN = 0, trW = 0, trL = 0; double trSum = 0;

                var sb = new StringBuilder(512);
                sb.Append("{\"account\":\"").Append(a.Name.Replace("\"", "'"))
                  .Append("\",\"updated\":\"").Append(TimeZoneInfo.ConvertTime(DateTime.Now, tzEt).ToString("yyyy-MM-dd HH:mm:ss"))
                  .Append("\",\"conn\":\"").Append(conn)
                  .Append("\",\"balance\":").Append(cash.ToString("F2"))
                  .Append(",\"dayPnL\":").Append(realized.ToString("F2"))
                  .Append(",\"unrealized\":").Append(unreal.ToString("F2"))
                  .Append(",\"buyingPower\":").Append(buyPow.ToString("F2"))
                  .Append(",\"posCount\":").Append(posCount)
                  .Append(",\"netQty\":").Append(netQty)
                  .Append(",\"positions\":[").Append(pos.ToString()).Append("]")
                  .Append(",\"tradesToday\":").Append(trN)
                  .Append(",\"winsToday\":").Append(trW)
                  .Append(",\"lossesToday\":").Append(trL)
                  .Append(",\"grossToday\":").Append(trSum.ToString("F2")).Append("}");

                string safe = string.Join("_", a.Name.Split(Path.GetInvalidFileNameChars()));
                File.WriteAllText(Path.Combine(StatePath, "account_" + safe + ".json"), sb.ToString());
            } catch { }
        }

        protected override void OnBarUpdate() { }   // account monitoring is event-driven, not bar-driven

        [Display(Name = "State folder", GroupName = "Dashboard", Order = 1)]
        public string StatePath { get; set; }
    }
}
