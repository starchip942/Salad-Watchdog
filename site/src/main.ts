import "./style.css";
import { Hero } from "./hero";
import { layoutCore } from "./core";
import { DEMO, Live, Session, command, login, olderEvents } from "./net";
import type { HistPoint, LogEvent, Num, Telemetry } from "./types";
import { ago, esc, isNum, logTime, pcTime, range3, tempClass, tempColor, usd4 } from "./format";

const $ = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T;
const ASSETS = import.meta.env.BASE_URL + "img/";
const isWide = () => matchMedia("(min-width: 900px)").matches;

// ---------------- state ----------------
const S = {
  t: null as Telemetry | null,
  online: false,
  lastSeen: 0,
  link: "connecting" as "connecting" | "open" | "closed",
  latency: null as number | null,
  hist: [] as HistPoint[],
  events: [] as LogEvent[],         // newest first
  eventsTotal: 0,
};
const tz = () => S.t?.pc?.tz;

// ---------------- hero ----------------
const hero = new Hero($("hero-canvas") as HTMLCanvasElement, ASSETS, () => !isWide());
hero.load().catch(() => { /* image failed: the numbers still work */ });

// ---------------- core die ----------------
const coreImg = $<HTMLImageElement>("core-img");
coreImg.src = ASSETS + (devicePixelRatio > 1.5 || isWide() ? "gpu-core.webp" : "gpu-core-mobile.webp");
const corewrap = $("corewrap");
const relayoutCore = () => {
  corewrap.classList.toggle("narrow", corewrap.clientWidth < 620);
  if (coreImg.complete && coreImg.naturalWidth) layoutCore(corewrap);
};
coreImg.addEventListener("load", relayoutCore);
new ResizeObserver(relayoutCore).observe(corewrap);

// ---------------- number tweening ----------------
const tweens = new Map<HTMLElement, { from: number; to: number; t0: number; fmt: (v: number) => string }>();
function tween(el: HTMLElement, v: Num, fmt: (v: number) => string, na = "N/A") {
  if (!isNum(v)) { tweens.delete(el); el.innerHTML = `<span class="na">${na}</span>`; (el as any)._v = undefined; return; }
  const cur = (el as any)._v as number | undefined;
  if (cur === undefined || matchMedia("(prefers-reduced-motion: reduce)").matches) { (el as any)._v = v; el.innerHTML = fmt(v); return; }
  tweens.set(el, { from: cur, to: v, t0: performance.now(), fmt });
  (el as any)._v = v;
  if (tweens.size === 1) requestAnimationFrame(tick);
}
function tick(now: number) {
  for (const [el, tw] of tweens) {
    const u = Math.min(1, (now - tw.t0) / 700), e = 1 - Math.pow(1 - u, 3);
    el.innerHTML = tw.fmt(tw.from + (tw.to - tw.from) * e);
    if (u >= 1) tweens.delete(el);
  }
  if (tweens.size) requestAnimationFrame(tick);
}
const deg = (d = 0) => (v: number) => `${v.toFixed(d)}<small>°C</small>`;
const watts = (v: number) => `<span class="bolt" aria-hidden="true">⚡</span>${v.toFixed(0)}<small>W</small>`;
const unit = (u: string, d = 0) => (v: number) => `${v.toFixed(d)}<small>${u}</small>`;

// ---------------- sparklines ----------------
function spark(el: HTMLElement, data: Num[], color: string, h = 28) {
  const pts = data.filter(isNum);
  if (pts.length < 2) { el.innerHTML = ""; return; }
  const w = 200, min = Math.min(...pts) - 1, max = Math.max(...pts) + 1;
  const xy = data.map((v, i) => isNum(v) ? [i / (data.length - 1) * w, h - ((v - min) / (max - min)) * (h - 4) - 2] : null);
  let d = "", pen = false;
  for (const p of xy) { if (!p) { pen = false; continue; } d += (pen ? "L" : "M") + p[0].toFixed(1) + " " + p[1].toFixed(1); pen = true; }
  const last = xy.filter(Boolean).at(-1)!;
  const id = "g" + Math.random().toString(36).slice(2, 8);
  el.innerHTML = `<svg class="spark" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" style="height:${h}px" aria-hidden="true">
    <defs><linearGradient id="${id}" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="${color}" stop-opacity=".28"/><stop offset="1" stop-color="${color}" stop-opacity="0"/></linearGradient></defs>
    <path d="${d} L${w} ${h} L0 ${h} Z" fill="url(#${id})"/><path d="${d}" fill="none" stroke="${color}" stroke-width="1.6" vector-effect="non-scaling-stroke" stroke-linejoin="round"/>
    <circle cx="${last[0]}" cy="${last[1]}" r="2.4" fill="${color}"/></svg>`;
}
function last5min(pick: (h: HistPoint) => Num): Num[] {
  const cut = (S.t?.t ?? Date.now() / 1000) - 300;
  return S.hist.filter(h => h.t >= cut).map(pick);
}
function pushHist(t: Telemetry) {
  const p: HistPoint = { t: t.t, g: [t.gpu.tempC, t.gpu.hotspotC, t.gpu.util, t.gpu.powerW, t.gpu.fans?.[0]?.pct ?? null],
    c: [t.cpu.tempC, t.cpu.powerW], r: t.ram.map(r => r.tempC), d: t.ssd.map(s => s.tempC) };
  S.hist.push(p);
  if (S.hist.length > 900) S.hist.splice(0, S.hist.length - 900);
}

// ---------------- render ----------------
const SALAD_LABEL: Record<string, string> = { not_running: "Not running", idle: "Idle / waiting", paused: "Paused", chopping: "Chopping", locked_out: "Locked out", unknown: "Unknown" };

function render() {
  const t = S.t;
  renderStatus();
  renderBanners();
  $("hero").classList.toggle("offline", !S.online);
  if (!t) return;
  const g = t.gpu;
  const gt = $("gpu-temp");
  tween(gt, g.tempC, v => `${Math.round(v)}<sup>°C</sup>`, "--");
  gt.className = "big num " + tempClass(g.tempC);
  tween($("gpu-util"), g.util, unit("%"));
  tween($("gpu-power"), g.powerW, unit(" W"));
  const fan = g.fans?.[0];
  tween($("gpu-fans"), fan?.pct ?? null, v => `${Math.round(v)}<small>%</small>`);

  const core = $("t-core"); tween(core, g.coreC, deg()); core.className = "v num " + tempClass(g.coreC);
  const hot = $("t-hot"); tween(hot, g.hotspotC, deg()); hot.className = "v num " + tempClass(g.hotspotC, "hot");
  tween($("t-vram"), isNum(g.vramUsedMB) ? g.vramUsedMB / 1024 : null, v => `${v.toFixed(1)}<small>/ ${isNum(g.vramTotalMB) ? Math.round(g.vramTotalMB / 1024) : "?"} GB</small>`);
  $("t-vram-bar").style.width = isNum(g.vramUsedMB) && isNum(g.vramTotalMB) ? (g.vramUsedMB / g.vramTotalMB * 100) + "%" : "0";
  tween($("t-power"), g.powerW, watts);

  const cc = $("c-core"); cc.textContent = isNum(g.coreC) ? `${Math.round(g.coreC)} °C` : "N/A"; cc.className = "t num " + tempClass(g.coreC);
  const ch = $("c-hot"); ch.textContent = isNum(g.hotspotC) ? `${Math.round(g.hotspotC)} °C` : "N/A"; ch.className = "t num " + tempClass(g.hotspotC, "hot");
  const glowC = tempColor(g.coreC);
  (corewrap.querySelector(".dieglow") as HTMLElement).style.boxShadow = S.online && isNum(g.coreC)
    ? `0 0 ${30 + Math.max(0, g.coreC - 40)}px ${4 + Math.max(0, (g.coreC - 40) / 6)}px ${glowC}40, inset 0 0 30px ${glowC}30` : "none";

  // CPU, memory, storage
  $("cpu-name").textContent = t.cpu.name ?? "";
  const ct = $("cpu-temp"); tween(ct, t.cpu.tempC, deg()); ct.className = "v " + tempClass(t.cpu.tempC, "cpu");
  tween($("cpu-power"), t.cpu.powerW, watts);
  renderPair("ram", t.ram, "ram", h => h.r);
  renderPair("ssd", t.ssd, "ssd", h => h.d);

  // Salad
  const s = t.salad;
  const st = $("salad-state");
  st.textContent = SALAD_LABEL[s.state] ?? s.state;
  if (s.hold && s.state !== "locked_out") st.textContent += " · hold";
  st.className = "state " + s.state;
  const sf = t.safety;
  const e = s.earnings;
  $("earn").classList.toggle("hidden", !e);
  if (e) {
    $("e-bal").textContent = usd4(e.balanceUSD);
    $("e-next").innerHTML = isNum(e.estNextHourHighUSD) ? `${usd4(e.estNextHourLowUSD)} ~ ${usd4(e.estNextHourHighUSD)}` : "—";
    $("e-pay").innerHTML = isNum(e.lastPayoutUSD ?? null) ? `+$${e.lastPayoutUSD!.toFixed(4)} <small>· ${e.lastPayoutAt ? ago(Date.now() / 1000 - e.lastPayoutAt) : ""}</small>` : "N/A";
    const l24 = $("e-24"); l24.textContent = usd4(e.last24hUSD, true); l24.style.color = isNum(e.last24hUSD) ? "var(--green)" : "";
  }
  (document.querySelector('[data-cmd="salad_chop_now"]') as HTMLButtonElement).disabled = s.state === "locked_out";

  renderSchedules();
  const ab = t.afterburner;
  document.querySelectorAll<HTMLButtonElement>("[data-ab]").forEach(b => {
    const n = Number(b.dataset.ab);
    b.hidden = !ab?.available;
    b.textContent = "GPU: " + (ab?.names?.[n - 1] ?? (n === 1 ? "Undervolt" : "Stock"));
    b.classList.toggle("on", ab?.profile === n);
  });

  hero.set({ rpm: (g.fans ?? []).map(f => isNum(f.rpm) ? f.rpm : isNum(f.pct) ? f.pct * 30 : 0), tempC: g.tempC, online: S.online });
}

function renderPair(id: string, list: { name: string; tempC: Num }[], kind: "ram" | "ssd", pick: (h: HistPoint) => Num[]) {
  const box = $(id);
  const items = list.length ? list : [{ name: kind === "ram" ? "DIMM 1" : "SSD 1", tempC: null }, { name: kind === "ram" ? "DIMM 2" : "SSD 2", tempC: null }];
  if (box.children.length !== items.length) {
    box.innerHTML = items.map((_, i) => `<div><div class="lbl" data-n></div><div class="v" data-v></div></div>`).join("");
  }
  items.forEach((it, i) => {
    const el = box.children[i] as HTMLElement;
    (el.querySelector("[data-n]") as HTMLElement).textContent = it.name;
    const v = el.querySelector("[data-v]") as HTMLElement;
    tween(v, it.tempC, deg()); v.className = "v " + tempClass(it.tempC, kind);
  });
}

function renderStatus() { /* status pills removed: the banners say when the PC is offline */ }

function renderBanners() {
  const t = S.t, out: string[] = [];
  const lo = t?.safety?.lockout;
  if (lo) out.push(`<div class="banner red"><b>⚠ Salad locked out at ${Math.round(lo.tempC)} °C · ${pcTime(lo.at, tz(), true)}</b>Check cooling (fans, dust, airflow). Clear it on the PC.</div>`);
  if (t?.safety?.sensorFail) out.push(`<div class="banner amber"><b>GPU temperature can't be read</b>The safety stop won't act on missing data. Check the PC.</div>`);
  const pend = t?.shutdown?.pendingAt;
  if (S.online && pend && pend * 1000 > Date.now()) {
    const left = Math.max(0, Math.round(pend - Date.now() / 1000));
    out.push(`<div class="banner red"><b>PC shutting down in <span class="num">${left} s</span></b>Salad will stop earning.<div class="row-btns"><button class="btn white" data-cmd="shutdown_cancel">Cancel shutdown</button></div></div>`);
  }
  if (!S.online && S.lastSeen) out.push(`<div class="banner grey">The PC or the Watchdog isn't connected. Showing the last values received <b>${ago((Date.now() - S.lastSeen) / 1000)}</b>. It reconnects by itself when the PC is back.</div>`);
  const html = out.join("");
  const b = $("banners");
  if (b.innerHTML !== html) b.innerHTML = html;
}

function renderSchedules() {
  const list = S.t?.schedules ?? [];
  $("sched-list").innerHTML = list.map(s => `<div class="sched-item"><span>${s.action === "pc_off" ? "Turn PC off" : "Close Salad"} · ${pcTime(s.at, tz(), true)}${s.missed ? " · missed" : ""}</span><button class="x" data-cancel="${esc(s.id)}" aria-label="Cancel this schedule">✕</button></div>`).join("");
}

// ---------------- logs (three separate logs, in a sheet) ----------------
const logRow = (e: LogEvent) => `<div class="log-row"><span class="tm num">${logTime(e.t, tz())}</span><span class="lv ${esc(e.level)}">${e.level === "error" ? "ERR" : esc(e.level)}</span><span>${esc(e.msg)}</span></div>`;
function renderLog() { /* the logs live in their own sheet */ }
const CATS: [string, string][] = [["pc", "PC status"], ["salad", "Salad"], ["earnings", "Earnings"]];
const catOf = (e: LogEvent) => e.cat ?? "pc";
let sheetEvents: LogEvent[] = [], sheetMore = true, cat = "pc", query = "";
async function openLogSheet() {
  query = "";
  openSheet("Logs", `
    <div class="filters">${CATS.map(([k, l]) => `<button class="chip ${k === cat ? "on" : ""}" data-c="${k}">${l}</button>`).join("")}</div>
    <input class="search" id="log-q" placeholder="Search" aria-label="Search the log">
    <div class="loglist" id="loglist"></div>
    <button class="btn ghost more" id="log-older">Load older</button>`);
  const pick = () => { sheetEvents = S.events.filter(e => catOf(e) === cat); sheetMore = true; };
  const draw = () => {
    const rows = sheetEvents.filter(e => !query || e.msg.toLowerCase().includes(query));
    $("loglist").innerHTML = rows.length ? rows.map(logRow).join("") : `<div class="empty">Nothing here yet.</div>`;
    $<HTMLButtonElement>("log-older").style.display = sheetMore ? "" : "none";
  };
  const older = async () => {
    const b = $<HTMLButtonElement>("log-older"); b.disabled = true; b.textContent = "Loading…";
    try {
      const oldest = sheetEvents.at(-1)?.t ?? Date.now() / 1000 + 1;
      const r = await olderEvents(oldest, cat);
      const seen = new Set(sheetEvents.map(e => e.id));
      sheetEvents.push(...r.events.filter(e => !seen.has(e.id)));
      sheetMore = r.more;
    } catch { /* offline */ }
    b.disabled = false; b.textContent = "Load older"; draw();
  };
  $("sheet-body").addEventListener("click", async ev => {
    const el = ev.target as HTMLElement;
    if (el.dataset.c) { cat = el.dataset.c; document.querySelectorAll("#sheet-body .chip").forEach(c => c.classList.toggle("on", c === el)); pick(); draw(); if (sheetEvents.length < 20) older(); }
    if (el.id === "log-older") older();
  });
  $("log-q").addEventListener("input", e => { query = (e.target as HTMLInputElement).value.toLowerCase(); draw(); });
  pick(); draw(); if (sheetEvents.length < 20) older();
}

// ---------------- sheet ----------------
function openSheet(title: string, html: string) {
  $("sheet-title").textContent = title;
  const body = $("sheet-body");
  const fresh = body.cloneNode(false) as HTMLElement;   // drops old listeners
  body.replaceWith(fresh);
  fresh.innerHTML = html;
  $("sheet").hidden = false;
}
function closeSheet() { $("sheet").hidden = true; }
$("sheet").addEventListener("click", e => { if ((e.target as HTMLElement).closest("[data-close]")) closeSheet(); });
document.addEventListener("keydown", e => { if (e.key === "Escape") closeSheet(); });

// ---------------- controls ----------------
const msg = (text: string, ok?: boolean) => { const m = $("ctl-msg"); m.textContent = text; m.className = "msg" + (ok === true ? " ok" : ok === false ? " bad" : ""); };
function renderLock() {
  const unlocked = !!Session.token() || (DEMO && Session.exp() * 1000 > Date.now());
  $("controls").classList.toggle("unlocked", unlocked);
  const left = Math.max(0, Math.round((Session.exp() * 1000 - Date.now()) / 60000));
  $("lock-state").innerHTML = unlocked ? `<span style="color:var(--green)">${left} min left</span>` : "";
}
$("lockbar").addEventListener("submit", async e => {
  e.preventDefault();
  const pw = $<HTMLInputElement>("pw");
  if (!pw.value) return;
  msg("Checking…");
  const r = await login(pw.value);
  pw.value = "";
  if (r.ok) { msg("Unlocked for 1 hour.", true); renderLock(); }
  else msg(r.msg, false);
});

async function run(cmd: string, args: Record<string, unknown> = {}) {
  if (!$("controls").classList.contains("unlocked")) { msg("Enter the password first.", false); $("pw").focus(); return; }
  msg("Sending…");
  const r = await command(cmd, args);
  msg(r.msg || (r.ok ? "Done." : "Failed."), r.ok);
  if (r.expired) renderLock();
}

document.addEventListener("click", e => {
  const el = (e.target as HTMLElement).closest<HTMLElement>("[data-cmd],[data-cancel],[data-ab],#log-more");
  if (!el) return;
  if (el.dataset.ab) { run("afterburner_profile", { profile: Number(el.dataset.ab) }); return; }
  if (el.id === "log-more") { openLogSheet(); return; }
  if (el.dataset.cancel) { run("schedule_cancel", { id: el.dataset.cancel }); return; }
  const cmd = el.dataset.cmd!;
  if (cmd === "pc_off_now") { confirmPcOff(); return; }
  if (cmd === "schedule") { openSchedule(); return; }
  if (cmd === "confirm_pc_off") { closeSheet(); run("pc_off_now"); return; }
  if (cmd === "close") { closeSheet(); return; }
  run(cmd);
});

function confirmPcOff() {
  if (!$("controls").classList.contains("unlocked")) { msg("Enter the password first.", false); return; }
  openSheet("Turn the PC off?", `<p class="help">Shuts down in 30 s. It can't be turned back on from here.</p>
    <div class="ctrls" style="grid-template-columns:1fr 1fr"><button class="btn" data-cmd="close">Keep it on</button><button class="btn danger" data-cmd="confirm_pc_off">⏻ Turn off</button></div>`);
}

function openSchedule() {
  if (!$("controls").classList.contains("unlocked")) { msg("Enter the password first.", false); return; }
  let action: "pc_off" | "salad_off" = "pc_off", mode: "at" | "in" = "at";
  openSheet("Schedule", `
    <div class="seg" id="seg-a"><button class="on" data-a="pc_off">Turn PC off</button><button data-a="salad_off">Close Salad</button></div>
    <div class="seg" id="seg-m"><button class="on" data-m="at">At a time</button><button data-m="in">In … minutes</button></div>
    <div id="pick-at"><div class="timepick"><input id="at" type="time" value="07:00" step="60" aria-label="Time"></div>
      <div class="quick">${["07:00", "08:00", "23:00", "00:00"].map(t => `<button class="chip" data-t="${t}">${t}</button>`).join("")}</div></div>
    <div id="pick-in" hidden><div class="timepick"><input id="in" type="number" min="1" max="1440" value="30" inputmode="numeric" aria-label="Minutes"></div>
      <div class="quick">${[15, 30, 60, 120].map(m => `<button class="chip" data-mm="${m}">${m} min</button>`).join("")}</div></div>
    <p class="help" id="sched-help"></p>
    <button class="btn go" id="sched-go" style="width:100%">Schedule</button>`);
  const help = () => {
    const tzName = tz() ?? "the PC's time zone";
    $("sched-help").innerHTML = mode === "at" ? `PC time (${esc(tzName)})` : "";
  };
  const body = $("sheet-body");
  body.addEventListener("click", e => {
    const el = e.target as HTMLElement;
    if (el.dataset.a) { action = el.dataset.a as typeof action; body.querySelectorAll("[data-a]").forEach(b => b.classList.toggle("on", b === el)); }
    if (el.dataset.m) { mode = el.dataset.m as typeof mode; body.querySelectorAll("[data-m]").forEach(b => b.classList.toggle("on", b === el)); $("pick-at").hidden = mode !== "at"; $("pick-in").hidden = mode !== "in"; }
    if (el.dataset.t) $<HTMLInputElement>("at").value = el.dataset.t;
    if (el.dataset.mm) $<HTMLInputElement>("in").value = el.dataset.mm;
    if (el.id === "sched-go") {
      const args: Record<string, unknown> = { action };
      if (mode === "at") args.atLocal = $<HTMLInputElement>("at").value || "07:00";
      else args.inMinutes = Math.max(1, Math.min(1440, Number($<HTMLInputElement>("in").value) || 30));
      closeSheet(); run("schedule_add", args);
    }
    help();
  });
  body.addEventListener("input", help);
  help();
}

// ---------------- live data ----------------
new Live({
  hello(m) {
    S.online = m.online; S.lastSeen = m.lastSeen;
    if (m.last) S.t = m.last;
    S.hist = m.history ?? [];
    S.events = m.events ?? []; S.eventsTotal = m.eventsTotal ?? S.events.length;
    render(); renderLog();
  },
  telemetry(t) { S.t = t; S.online = true; S.lastSeen = Date.now(); pushHist(t); render(); },
  status(online, lastSeen) { S.online = online; S.lastSeen = lastSeen; render(); },
  event(e) {
    if (S.events.some(x => x.id === e.id)) return;
    S.events.unshift(e); S.eventsTotal++;
    if (S.events.length > 200) S.events.length = 200;
    renderLog();
  },
  link(state, latency) { S.link = state; if (latency != null) S.latency = latency; renderStatus(); },
  needPassword() { Session.viewLocked = true; msg("Viewing needs the password on this site.", false); },
}).start();

// keep "last seen", countdowns and session time fresh
setInterval(() => { renderStatus(); renderBanners(); renderLock(); }, 1000);
renderLock();
renderLog();
$("foot").innerHTML = "";
window.addEventListener("resize", () => renderLog());
