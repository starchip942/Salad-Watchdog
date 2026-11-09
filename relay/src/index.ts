/**
 * GPU Watchdog relay: a Cloudflare Worker with one Durable Object ("hub").
 *
 *   /agent    WebSocket  the PC (needs AGENT_KEY)
 *   /view     WebSocket  read-only live telemetry for the website
 *   /auth     POST       password -> short-lived signed session token
 *   /command  POST       session token -> signed, expiring, nonce'd command to the PC
 *   /events   GET        older event-log lines (public lines only)
 *   /health   GET        quick check
 *
 * The password is never stored in plain text. The PC sends a PBKDF2 hash over its authenticated
 * connection; the relay verifies against that hash and invalidates sessions when it changes.
 */

export interface Env {
  HUB: DurableObjectNamespace;
  AGENT_KEY?: string;           // optional secret; if unset, the first PC to connect claims the relay (its key hash is stored)
  SESSION_SIGNING_KEY?: string; // optional secret; if unset, a random key is generated and kept in storage
  ALLOWED_ORIGIN: string;       // var: e.g. https://<user>.github.io
}

const HISTORY_MAX = 600;        // ~10 min of 1 Hz samples
const EVENTS_MAX = 1000;        // event-log lines kept for "show more history"
const EVENTS_HELLO = 50;        // sent to a new viewer
const SESSION_TTL_S = 3600;
const COMMAND_TIMEOUT_MS = 15000;
const IP_MAX_FAILS = 5;
const LOCK_STEPS_S = [60, 300, 1800, 86400];
const GLOBAL_MAX_FAILS_PER_HOUR = 30;
const GLOBAL_LOCK_S = 900;

export const COMMANDS = new Set([
  "pc_off_now", "shutdown_cancel", "salad_chop_now", "salad_pause", "salad_off_now",
  "gamemode_set", "schedule_add", "schedule_cancel",
]);

// ---------- worker entry ----------
export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);
    if (req.method === "OPTIONS") return cors(env, req, new Response(null, { status: 204 }));
    if (url.pathname === "/health") return cors(env, req, json({ ok: true }));
    const id = env.HUB.idFromName("hub");
    const res = await env.HUB.get(id).fetch(req);
    // WebSocket upgrades must be returned untouched
    if (res.status === 101) return res;
    return cors(env, req, res);
  },
};

// ---------- durable object ----------
type Sample = Record<string, unknown> & { t: number };
type LogEvent = { id: string; t: number; level: string; msg: string };
type PwRecord = { salt: string; hash: string; iter: number; ver: number };
type Pending = { resolve: (v: unknown) => void; timer: ReturnType<typeof setTimeout> };

export class Hub {
  state: DurableObjectState;
  env: Env;
  history: Sample[] = [];
  events: LogEvent[] = [];
  last: Sample | null = null;
  lastSeen = 0;
  agentInfo: Record<string, unknown> = {};
  loaded = false;
  dirty = false;
  pending = new Map<string, Pending>();

  constructor(state: DurableObjectState, env: Env) {
    this.state = state;
    this.env = env;
    // Viewers' pings are answered without waking the object
    this.state.setWebSocketAutoResponse(new WebSocketRequestResponsePair('{"type":"ping"}', '{"type":"pong"}'));
  }

  async load() {
    if (this.loaded) return;
    const got = await this.state.storage.get(["history", "events", "last", "lastSeen", "agentInfo"]);
    this.history = (got.get("history") as Sample[]) ?? [];
    this.events = (got.get("events") as LogEvent[]) ?? [];
    this.last = (got.get("last") as Sample) ?? null;
    this.lastSeen = (got.get("lastSeen") as number) ?? 0;
    this.agentInfo = (got.get("agentInfo") as Record<string, unknown>) ?? {};
    this.loaded = true;
  }

  async persist(force = false) {
    if (!this.dirty && !force) return;
    this.dirty = false;
    await this.state.storage.put({
      history: this.history, events: this.events, last: this.last, lastSeen: this.lastSeen, agentInfo: this.agentInfo,
    });
  }

  /** The PC's key: a Cloudflare secret if set, otherwise claimed by the first PC that connects (only its SHA-256 is stored). */
  async agentKeyOk(key: string): Promise<boolean> {
    if (key.length < 32) return false;
    if (this.env.AGENT_KEY) return safeEqual(key, this.env.AGENT_KEY);
    const h = await sha256hex(key);
    const stored = (await this.state.storage.get("agentKeyHash")) as string | undefined;
    if (!stored) { await this.state.storage.put("agentKeyHash", h); return true; }
    return safeEqual(h, stored);
  }
  async agentSecret(): Promise<string> {
    // commands are signed with the PC's key; in claim mode we use its hash, which the PC can compute too
    return this.env.AGENT_KEY ? this.env.AGENT_KEY : ((await this.state.storage.get("agentKeyHash")) as string) ?? "";
  }
  async signingKey(): Promise<string> {
    if (this.env.SESSION_SIGNING_KEY) return this.env.SESSION_SIGNING_KEY;
    let k = (await this.state.storage.get("signingKey")) as string | undefined;
    if (!k) { const b = new Uint8Array(32); crypto.getRandomValues(b); k = b64u(b); await this.state.storage.put("signingKey", k); }
    return k;
  }

  agentSocket(): WebSocket | null {
    const s = this.state.getWebSockets("agent");
    return s.length ? s[s.length - 1] : null;
  }
  viewerCount() { return this.state.getWebSockets("view").length; }

  async fetch(req: Request): Promise<Response> {
    await this.load();
    const url = new URL(req.url);
    const ip = req.headers.get("CF-Connecting-IP") ?? "unknown";
    try {
      switch (url.pathname) {
        case "/agent": return await this.acceptAgent(req);
        case "/view": return await this.acceptViewer(req, url);
        case "/auth": return await this.auth(req, ip);
        case "/command": return await this.command(req);
        case "/events": return await this.eventsPage(req, url);
        default: return json({ error: "not found" }, 404);
      }
    } catch (e) {
      return json({ error: "server error" }, 500);
    }
  }

  // ----- agent -----
  async acceptAgent(req: Request): Promise<Response> {
    if (req.headers.get("Upgrade") !== "websocket") return json({ error: "websocket only" }, 426);
    const auth = req.headers.get("Authorization") ?? "";
    const key = auth.startsWith("Bearer ") ? auth.slice(7) : "";
    if (!(await this.agentKeyOk(key))) return json({ error: "forbidden" }, 403);
    for (const old of this.state.getWebSockets("agent")) { try { old.close(4000, "replaced"); } catch { /* gone */ } }
    const pair = new WebSocketPair();
    this.state.acceptWebSocket(pair[1], ["agent"]);
    const pw = (await this.state.storage.get("pw")) as PwRecord | undefined;
    pair[1].send(JSON.stringify({ type: "hello", viewers: this.viewerCount(), passwordVer: pw?.ver ?? 0 }));
    this.lastSeen = Date.now();
    this.broadcast({ type: "status", online: true, lastSeen: this.lastSeen });
    return new Response(null, { status: 101, webSocket: pair[0] });
  }

  // ----- viewers -----
  async acceptViewer(req: Request, url: URL): Promise<Response> {
    if (req.headers.get("Upgrade") !== "websocket") return json({ error: "websocket only" }, 426);
    if (this.agentInfo.viewRequiresPassword) {
      const ok = await this.checkToken(url.searchParams.get("token") ?? "");
      if (!ok) return json({ error: "password required to view" }, 401);
    }
    const pair = new WebSocketPair();
    this.state.acceptWebSocket(pair[1], ["view"]);
    pair[1].send(JSON.stringify({
      type: "hello",
      online: !!this.agentSocket(),
      lastSeen: this.lastSeen,
      last: this.last,
      history: this.history,
      events: this.events.slice(-EVENTS_HELLO).reverse(),
      eventsTotal: this.events.length,
    }));
    this.notifyViewers();
    return new Response(null, { status: 101, webSocket: pair[0] });
  }

  notifyViewers() {
    const a = this.agentSocket();
    if (a) { try { a.send(JSON.stringify({ type: "viewers", n: this.viewerCount() })); } catch { /* reconnecting */ } }
  }

  broadcast(msg: unknown) {
    const s = JSON.stringify(msg);
    for (const ws of this.state.getWebSockets("view")) { try { ws.send(s); } catch { /* closed */ } }
  }

  async webSocketMessage(ws: WebSocket, data: string | ArrayBuffer) {
    await this.load();
    const tags = this.state.getTags(ws);
    if (!tags.includes("agent")) return;   // viewers can never send anything that matters
    let m: any;
    try { m = JSON.parse(typeof data === "string" ? data : new TextDecoder().decode(data)); } catch { return; }
    this.lastSeen = Date.now();
    switch (m.type) {
      case "telemetry": {
        const sample = { ...(m.data ?? {}), t: m.data?.t ?? Math.floor(Date.now() / 1000) } as Sample;
        this.last = sample;
        this.history.push(compact(sample));
        if (this.history.length > HISTORY_MAX) this.history.splice(0, this.history.length - HISTORY_MAX);
        this.broadcast({ type: "telemetry", data: sample });
        this.dirty = true;
        await this.maybePersist();
        break;
      }
      case "info": {   // agent settings that change relay behaviour
        this.agentInfo = { viewRequiresPassword: !!m.viewRequiresPassword };
        this.dirty = true;
        await this.persist();
        break;
      }
      case "event": this.addEvents([m.event]); await this.persist(); break;
      case "events_sync": this.addEvents(m.events ?? []); await this.persist(); break;
      case "result": {
        const p = this.pending.get(m.id);
        if (p) { clearTimeout(p.timer); this.pending.delete(m.id); p.resolve({ ok: !!m.ok, msg: String(m.msg ?? "") }); }
        break;
      }
      case "password_hash": {
        const rec: PwRecord = { salt: String(m.salt), hash: String(m.hash), iter: Number(m.iter), ver: Number(m.ver) };
        if (!rec.salt || !rec.hash || !(rec.iter > 0 && rec.iter <= 100000)) {
          ws.send(JSON.stringify({ type: "password_ack", ok: false, ver: rec.ver, msg: "bad hash" }));
          break;
        }
        await this.state.storage.put("pw", rec);
        // New password: every existing website session stops working
        const epoch = ((await this.state.storage.get("epoch")) as number ?? 0) + 1;
        await this.state.storage.put("epoch", epoch);
        ws.send(JSON.stringify({ type: "password_ack", ok: true, ver: rec.ver }));
        break;
      }
    }
  }

  lastPersist = 0;
  async maybePersist() {
    if (Date.now() - this.lastPersist < 30000) return;
    this.lastPersist = Date.now();
    await this.persist();
  }

  addEvents(list: LogEvent[]) {
    const seen = new Set(this.events.map(e => e.id));
    const fresh: LogEvent[] = [];
    for (const e of list) {
      if (!e || !e.id || seen.has(e.id)) continue;
      const ev = { id: String(e.id), t: Number(e.t), level: String(e.level ?? "info"), msg: String(e.msg ?? "").slice(0, 400) };
      this.events.push(ev); fresh.push(ev); seen.add(ev.id);
    }
    if (!fresh.length) return;
    this.events.sort((a, b) => a.t - b.t);
    if (this.events.length > EVENTS_MAX) this.events.splice(0, this.events.length - EVENTS_MAX);
    for (const ev of fresh.sort((a, b) => b.t - a.t)) this.broadcast({ type: "event", event: ev });
  }

  async webSocketClose(ws: WebSocket) {
    await this.load();
    if (this.state.getTags(ws).includes("agent")) {
      if (!this.agentSocket() || this.agentSocket() === ws) {
        this.broadcast({ type: "status", online: false, lastSeen: this.lastSeen });
        await this.persist(true);
      }
    } else {
      this.notifyViewers();
    }
  }
  async webSocketError(ws: WebSocket) { await this.webSocketClose(ws); }

  // ----- event history (public) -----
  async eventsPage(req: Request, url: URL): Promise<Response> {
    if (this.agentInfo.viewRequiresPassword && !(await this.checkToken(bearer(req)))) return json({ error: "password required" }, 401);
    const before = Number(url.searchParams.get("before") ?? Infinity);
    const limit = Math.min(200, Math.max(1, Number(url.searchParams.get("limit") ?? 100)));
    const older = this.events.filter(e => e.t < before).slice(-limit).reverse();
    return json({ events: older, more: this.events.filter(e => e.t < before).length > older.length });
  }

  // ----- password & sessions -----
  async auth(req: Request, ip: string): Promise<Response> {
    if (req.method !== "POST") return json({ error: "POST only" }, 405);
    const now = Date.now();
    const g = ((await this.state.storage.get("glock")) as { until: number; fails: number[] }) ?? { until: 0, fails: [] };
    if (g.until > now) return json({ error: "Too many attempts. Try again later.", retryAfter: Math.ceil((g.until - now) / 1000) }, 429);
    const key = "ip:" + ip;
    const r = ((await this.state.storage.get(key)) as { fails: number; level: number; until: number }) ?? { fails: 0, level: 0, until: 0 };
    if (r.until > now) return json({ error: "Locked out after wrong passwords.", retryAfter: Math.ceil((r.until - now) / 1000) }, 429);

    let body: any = {};
    try { body = await req.json(); } catch { /* empty */ }
    const password = String(body.password ?? "");
    const pw = (await this.state.storage.get("pw")) as PwRecord | undefined;
    if (!pw) return json({ error: "The PC hasn't connected yet, so no password is set." }, 503);

    const ok = password.length > 0 && password.length < 200 && (await verifyPbkdf2(password, pw));
    if (!ok) {
      r.fails++;
      if (r.fails >= IP_MAX_FAILS) {
        r.until = now + LOCK_STEPS_S[Math.min(r.level, LOCK_STEPS_S.length - 1)] * 1000;
        r.level++; r.fails = 0;
      }
      await this.state.storage.put(key, r);
      g.fails = g.fails.filter(t => t > now - 3600_000); g.fails.push(now);
      if (g.fails.length >= GLOBAL_MAX_FAILS_PER_HOUR) { g.until = now + GLOBAL_LOCK_S * 1000; g.fails = []; }
      await this.state.storage.put("glock", g);
      const left = r.until > now ? 0 : IP_MAX_FAILS - r.fails;
      return json({ error: "Wrong password.", attemptsLeft: left, retryAfter: r.until > now ? Math.ceil((r.until - now) / 1000) : 0 }, 401);
    }
    await this.state.storage.delete(key);
    const epoch = ((await this.state.storage.get("epoch")) as number) ?? 0;
    const exp = Math.floor(now / 1000) + SESSION_TTL_S;
    const token = await signToken({ exp, epoch }, await this.signingKey());
    return json({ token, exp });
  }

  async checkToken(token: string): Promise<boolean> {
    const p = await readToken(token, await this.signingKey());
    if (!p || p.exp < Date.now() / 1000) return false;
    const epoch = ((await this.state.storage.get("epoch")) as number) ?? 0;
    return p.epoch === epoch;
  }

  // ----- commands -----
  async command(req: Request): Promise<Response> {
    if (req.method !== "POST") return json({ error: "POST only" }, 405);
    if (!(await this.checkToken(bearer(req)))) return json({ error: "Session expired. Enter the password again." }, 401);
    let body: any = {};
    try { body = await req.json(); } catch { /* empty */ }
    const cmd = String(body.cmd ?? "");
    if (!COMMANDS.has(cmd)) return json({ error: "Unknown command." }, 400);
    const args = body.args && typeof body.args === "object" ? body.args : {};
    const agent = this.agentSocket();
    if (!agent) return json({ ok: false, msg: "The PC is offline." }, 503);

    const id = crypto.randomUUID();
    const ts = Math.floor(Date.now() / 1000);
    const nonce = crypto.randomUUID();
    const argsJson = JSON.stringify(args);
    const sig = await hmacHex(await this.agentSecret(), [id, cmd, argsJson, ts, nonce].join("|"));
    const result = await new Promise<unknown>((resolve) => {
      const timer = setTimeout(() => { this.pending.delete(id); resolve({ ok: false, msg: "The PC didn't answer in time." }); }, COMMAND_TIMEOUT_MS);
      this.pending.set(id, { resolve, timer });
      try { agent.send(JSON.stringify({ type: "command", id, cmd, args: argsJson, ts, nonce, sig })); }
      catch { clearTimeout(timer); this.pending.delete(id); resolve({ ok: false, msg: "The PC is offline." }); }
    });
    return json(result);
  }
}

// ---------- helpers ----------
function json(v: unknown, status = 200) {
  return new Response(JSON.stringify(v), { status, headers: { "content-type": "application/json", "cache-control": "no-store" } });
}
function bearer(req: Request) {
  const a = req.headers.get("Authorization") ?? "";
  return a.startsWith("Bearer ") ? a.slice(7) : "";
}
function cors(env: Env, req: Request, res: Response) {
  const origin = req.headers.get("Origin") ?? "";
  const allowed = (env.ALLOWED_ORIGIN ?? "").split(",").map(s => s.trim()).filter(Boolean);
  const h = new Headers(res.headers);
  if (allowed.includes(origin)) {
    h.set("Access-Control-Allow-Origin", origin);
    h.set("Access-Control-Allow-Headers", "content-type, authorization");
    h.set("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
    h.set("Vary", "Origin");
  }
  return new Response(res.body, { status: res.status, headers: h });
}

/** Keep the chart history small: only the numbers the sparklines need. */
export function compact(s: any): Sample {
  const gpu = s.gpu ?? {}, cpu = s.cpu ?? {};
  return {
    t: s.t,
    g: [gpu.tempC ?? null, gpu.hotspotC ?? null, gpu.util ?? null, gpu.powerW ?? null, gpu.fans?.[0]?.pct ?? null],
    c: [cpu.tempC ?? null, cpu.powerW ?? null],
    r: (s.ram ?? []).map((d: any) => d?.tempC ?? null),
    d: (s.ssd ?? []).map((d: any) => d?.tempC ?? null),
  } as Sample;
}

const enc = new TextEncoder();
export function b64u(bytes: Uint8Array) {
  let s = ""; for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}
export function unb64u(s: string) {
  const b = atob(s.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((s.length + 3) % 4));
  return Uint8Array.from(b, c => c.charCodeAt(0));
}
async function hmacKey(secret: string) {
  return crypto.subtle.importKey("raw", enc.encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign", "verify"]);
}
export async function hmacHex(secret: string, msg: string) {
  const sig = new Uint8Array(await crypto.subtle.sign("HMAC", await hmacKey(secret), enc.encode(msg)));
  return [...sig].map(b => b.toString(16).padStart(2, "0")).join("");
}
export async function signToken(payload: object, secret: string) {
  const body = b64u(enc.encode(JSON.stringify(payload)));
  const sig = new Uint8Array(await crypto.subtle.sign("HMAC", await hmacKey(secret), enc.encode(body)));
  return body + "." + b64u(sig);
}
export async function readToken(token: string, secret: string): Promise<{ exp: number; epoch: number } | null> {
  const [body, sig] = token.split(".");
  if (!body || !sig) return null;
  try {
    const ok = await crypto.subtle.verify("HMAC", await hmacKey(secret), unb64u(sig), enc.encode(body));
    return ok ? JSON.parse(new TextDecoder().decode(unb64u(body))) : null;
  } catch { return null; }
}
/** PBKDF2-SHA256, same parameters the PC used; compared in constant time. */
export async function verifyPbkdf2(password: string, pw: PwRecord) {
  const key = await crypto.subtle.importKey("raw", enc.encode(password), "PBKDF2", false, ["deriveBits"]);
  const bits = new Uint8Array(await crypto.subtle.deriveBits(
    { name: "PBKDF2", hash: "SHA-256", salt: unb64(pw.salt), iterations: pw.iter }, key, 256));
  return constantTimeEq(bits, unb64(pw.hash));
}
function unb64(s: string) { return Uint8Array.from(atob(s), c => c.charCodeAt(0)); }
function constantTimeEq(a: Uint8Array, b: Uint8Array) {
  if (a.length !== b.length) return false;
  let d = 0; for (let i = 0; i < a.length; i++) d |= a[i] ^ b[i];
  return d === 0;
}
async function sha256hex(s: string) {
  return [...new Uint8Array(await crypto.subtle.digest("SHA-256", enc.encode(s)))].map(b => b.toString(16).padStart(2, "0")).join("");
}
async function safeEqual(a: string, b: string) {
  // Hash both first so the comparison time doesn't depend on where they differ, or on length
  const ha = new Uint8Array(await crypto.subtle.digest("SHA-256", enc.encode(a)));
  const hb = new Uint8Array(await crypto.subtle.digest("SHA-256", enc.encode(b)));
  return constantTimeEq(ha, hb);
}
