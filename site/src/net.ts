import type { HistPoint, LogEvent, Telemetry } from "./types";

export const RELAY: string = (import.meta.env.VITE_RELAY_URL as string | undefined)?.replace(/\/$/, "") ?? "";
export const DEMO = !RELAY || new URLSearchParams(location.search).has("demo");

export interface Handlers {
  hello(m: { online: boolean; lastSeen: number; last: Telemetry | null; history: HistPoint[]; events: LogEvent[]; eventsTotal: number }): void;
  telemetry(t: Telemetry): void;
  status(online: boolean, lastSeen: number): void;
  event(e: LogEvent): void;
  link(state: "connecting" | "open" | "closed", latencyMs?: number): void;
  needPassword(): void;
}

/** Live read-only channel to the relay. Reconnects by itself with backoff. */
export class Live {
  ws: WebSocket | null = null;
  backoff = 1000;
  pingAt = 0;
  timer = 0;
  constructor(private h: Handlers) {}

  start() {
    if (DEMO) { startDemo(this.h); return; }
    this.connect();
    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "visible" && (!this.ws || this.ws.readyState > 1)) this.connect();
    });
  }

  connect() {
    clearTimeout(this.timer);
    this.h.link("connecting");
    const tok = Session.token();
    const ws = new WebSocket(RELAY.replace(/^http/, "ws") + "/view" + (tok ? "?token=" + encodeURIComponent(tok) : ""));
    this.ws = ws;
    let opened = false;
    ws.onopen = () => { opened = true; this.backoff = 1000; this.h.link("open"); this.ping(); };
    ws.onmessage = (ev) => {
      let m: any; try { m = JSON.parse(ev.data); } catch { return; }
      if (m.type === "hello") this.h.hello(m);
      else if (m.type === "telemetry") this.h.telemetry(m.data);
      else if (m.type === "status") this.h.status(m.online, m.lastSeen);
      else if (m.type === "event") this.h.event(m.event);
      else if (m.type === "pong") this.h.link("open", Math.round(performance.now() - this.pingAt));
    };
    ws.onclose = (ev) => {
      if (this.ws !== ws) return;
      if (!opened && ev.code === 1006 && Session.viewLocked) { this.h.needPassword(); }
      this.h.link("closed");
      this.timer = window.setTimeout(() => this.connect(), this.backoff);
      this.backoff = Math.min(this.backoff * 2, 30000);
    };
  }

  ping() {
    if (this.ws?.readyState !== 1) return;
    this.pingAt = performance.now();
    this.ws.send('{"type":"ping"}');
    window.setTimeout(() => this.ping(), 10000);
  }
}

// ---------- password session (1 hour, issued by the relay) ----------
export const Session = {
  viewLocked: false,
  token(): string | null {
    try {
      const raw = sessionStorage.getItem("gw.session");
      if (!raw) return null;
      const s = JSON.parse(raw);
      return s.exp * 1000 > Date.now() + 5000 ? s.token : null;
    } catch { return null; }
  },
  exp(): number { try { return JSON.parse(sessionStorage.getItem("gw.session") ?? "{}").exp ?? 0; } catch { return 0; } },
  save(token: string, exp: number) { try { sessionStorage.setItem("gw.session", JSON.stringify({ token, exp })); } catch { /* private mode */ } memTok = { token, exp }; },
  clear() { try { sessionStorage.removeItem("gw.session"); } catch { /* ignore */ } memTok = null; },
};
let memTok: { token: string; exp: number } | null = null;
const tokenNow = () => Session.token() ?? (memTok && memTok.exp * 1000 > Date.now() ? memTok.token : null);

export async function login(password: string): Promise<{ ok: true } | { ok: false; msg: string }> {
  if (DEMO) { if (password.length) { Session.save("demo", Math.floor(Date.now() / 1000) + 3600); return { ok: true }; } return { ok: false, msg: "Enter the password." }; }
  try {
    const r = await fetch(RELAY + "/auth", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ password }) });
    const j = await r.json();
    if (r.ok && j.token) { Session.save(j.token, j.exp); return { ok: true }; }
    let msg = j.error ?? "Couldn't sign in.";
    if (j.retryAfter > 0) msg += ` Try again in ${fmtWait(j.retryAfter)}.`;
    else if (typeof j.attemptsLeft === "number") msg += ` ${j.attemptsLeft} ${j.attemptsLeft === 1 ? "try" : "tries"} left before a lockout.`;
    return { ok: false, msg };
  } catch { return { ok: false, msg: "Can't reach the relay. Check your connection." }; }
}
function fmtWait(s: number) { return s < 90 ? `${s} s` : s < 5400 ? `${Math.round(s / 60)} min` : `${Math.round(s / 3600)} h`; }

export async function command(cmd: string, args: Record<string, unknown> = {}): Promise<{ ok: boolean; msg: string; expired?: boolean }> {
  if (DEMO) return demoCommand(cmd, args);
  const tok = tokenNow();
  if (!tok) return { ok: false, msg: "Enter the password first.", expired: true };
  try {
    const r = await fetch(RELAY + "/command", { method: "POST", headers: { "content-type": "application/json", authorization: "Bearer " + tok }, body: JSON.stringify({ cmd, args }) });
    const j = await r.json();
    if (r.status === 401) { Session.clear(); return { ok: false, msg: j.error ?? "Session expired.", expired: true }; }
    return { ok: !!j.ok, msg: j.msg ?? j.error ?? "" };
  } catch { return { ok: false, msg: "Can't reach the relay." }; }
}

export async function olderEvents(before: number, cat: string): Promise<{ events: LogEvent[]; more: boolean }> {
  if (DEMO) return demoOlder(before, cat);
  const tok = tokenNow();
  const r = await fetch(`${RELAY}/events?before=${before}&limit=100&cat=${encodeURIComponent(cat)}`, { headers: tok ? { authorization: "Bearer " + tok } : {} });
  return r.json();
}

// ---------- demo mode (no relay configured, or ?demo in the address) ----------
let demoT: Telemetry;
let demoH: Handlers;
function startDemo(h: Handlers) {
  demoH = h;
  const now = Math.floor(Date.now() / 1000);
  demoT = {
    v: 1, t: now, pc: { tz: "Europe/Istanbul", tzOffsetMin: 180 },
    gpu: { name: "NVIDIA GeForce RTX 5070 Ti", tempC: 66, coreC: 66, hotspotC: null, util: 98, vramUsedMB: 12700, vramTotalMB: 16303, powerW: 248, fans: [{ pct: 58, rpm: 1700 }, { pct: 58, rpm: 1700 }] },
    cpu: { name: "CPU", tempC: 52, powerW: 46 },
    ram: [{ name: "DIMM 1", tempC: 44 }, { name: "DIMM 2", tempC: 45 }],
    ssd: [{ name: "SSD 1", tempC: 41 }, { name: "SSD 2", tempC: 38 }],
    salad: { state: "chopping", hold: false, gameMode: { activeGame: null },
      earnings: { balanceUSD: 3.4731, estNextHourLowUSD: 0.1177, estNextHourHighUSD: 0.2486, last24hUSD: 3.0407, updatedAt: now - 60, lastPayoutUSD: 0.0213, lastPayoutAt: now - 600, nextHourAt: now - 240 } },
    safety: { limitC: 85, sensor: "core", lockout: null },
    shutdown: { pendingAt: null },
    schedules: [],
  };
  const hist: HistPoint[] = [];
  for (let i = 300; i > 0; i--) hist.push({ t: now - i, g: [64 + Math.sin(i / 17) * 2 + Math.random(), null, 97, 240 + Math.random() * 15, 58], c: [50 + Math.sin(i / 9) * 3, 44 + Math.random() * 5], r: [44, 45], d: [41, 38] });
  const msgs: [string, string][] = [["pc", "64°C average (peak 68°C)"], ["salad", "Kingdom Come: Deliverance II started (KingdomCome.exe); Salad closed."], ["salad", "Games closed → Salad chopping again."], ["earnings", "Payout +$0.0213 → balance $3.4731"], ["pc", "Watchdog started."], ["salad", "Salad: idle → chopping."]];
  const events: LogEvent[] = [];
  for (let i = 0; i < 140; i++) { const [cat, msg] = msgs[i % msgs.length]; events.push({ id: "d" + i, t: now - (140 - i) * 600, level: i % 23 === 5 ? "warn" : "info", msg, cat }); }
  demoEvents = events;
  h.hello({ online: true, lastSeen: Date.now(), last: demoT, history: hist, events: events.slice(-50).reverse(), eventsTotal: events.length });
  h.link("open", 42);
  let k = 0;
  setInterval(() => {
    k++;
    const tt = demoT;
    tt.t = Math.floor(Date.now() / 1000);
    const load = tt.salad.state === "chopping" ? 1 : 0.05;
    const target = 34 + 34 * load;
    tt.gpu.tempC = tt.gpu.coreC = Math.round(((tt.gpu.tempC ?? 40) + (target - (tt.gpu.tempC ?? 40)) * 0.08 + (Math.random() - .5)) * 10) / 10;
    tt.gpu.util = Math.round(load * 97 + Math.random() * 2);
    tt.gpu.powerW = Math.round(30 + load * 215 + Math.random() * 10);
    const pct = (tt.gpu.tempC ?? 0) < 50 ? 0 : Math.round(30 + ((tt.gpu.tempC ?? 0) - 50) * 2.1);
    tt.gpu.fans = [{ pct, rpm: pct ? Math.round(pct * 29) : 0 }, { pct, rpm: pct ? Math.round(pct * 29) : 0 }];
    tt.cpu.tempC = Math.round(48 + Math.sin(k / 7) * 4); tt.cpu.powerW = Math.round(40 + Math.random() * 10);
    h.telemetry(structuredClone(tt));
  }, 1000);
}
let demoEvents: LogEvent[] = [];
function demoOlder(before: number, cat: string) {
  const older = demoEvents.filter(e => e.t < before && e.cat === cat);
  return Promise.resolve({ events: older.slice(-100).reverse(), more: older.length > 100 });
}
function demoCommand(cmd: string, args: Record<string, unknown>) {
  const s = demoT.salad;
  const now = Math.floor(Date.now() / 1000);
  const ev = (msg: string) => demoH.event({ id: "x" + Math.random(), t: now, level: "info", msg, cat: "salad" });
  let msg = "Done.";
  switch (cmd) {
    case "salad_chop_now": s.state = "chopping"; s.hold = false; msg = "Salad is chopping."; break;
    case "salad_pause": s.state = "paused"; s.hold = true; msg = "Chopping paused. Salad stays open."; break;
    case "salad_off_now": s.state = "not_running"; s.hold = true; msg = "Salad closed."; break;
    case "pc_off_now": demoT.shutdown = { pendingAt: now + 30 }; msg = "Shutting down in 30 s."; break;
    case "shutdown_cancel": demoT.shutdown = { pendingAt: null }; msg = "Shutdown cancelled."; break;
    case "schedule_add": {
      let at = now + 60 * Number(args.inMinutes ?? 0);
      if (args.atLocal) { const [hh, mm] = String(args.atLocal).split(":").map(Number); const d = new Date(); d.setHours(hh, mm, 0, 0); if (d.getTime() < Date.now()) d.setDate(d.getDate() + 1); at = Math.floor(d.getTime() / 1000); }
      demoT.schedules.push({ id: "s" + Math.random().toString(36).slice(2, 7), action: args.action as "pc_off", at }); msg = "Scheduled."; break;
    }
    case "schedule_cancel": demoT.schedules = demoT.schedules.filter(x => x.id !== args.id); msg = "Schedule cancelled."; break;
  }
  ev(`Website: ${msg}`);
  demoH.telemetry(structuredClone(demoT));
  return Promise.resolve({ ok: true, msg });
}
