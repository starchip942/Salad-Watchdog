import type { Num } from "./types";

export const isNum = (v: unknown): v is number => typeof v === "number" && Number.isFinite(v);

/** Balance / last 24 h: 4 decimals, e.g. $3.4731 */
export function usd4(v: Num, sign = false): string {
  if (!isNum(v)) return "N/A";
  return (sign && v >= 0 ? "+" : v < 0 ? "−" : "") + "$" + Math.abs(v).toFixed(4);
}
/** Estimated next hour: rounded to 3 decimals only when shown, e.g. $0.1177 -> $0.118 */
export function usd3(v: Num): string {
  if (!isNum(v)) return "N/A";
  return "$" + (Math.round(v * 1000) / 1000).toFixed(3);
}
export function range3(lo: Num, hi: Num): string {
  if (!isNum(lo) || !isNum(hi)) return "N/A";
  return `${usd3(lo)} – ${usd3(hi)}`;
}

export function ago(sec: number): string {
  if (sec < 5) return "just now";
  if (sec < 60) return `${Math.floor(sec)} s ago`;
  if (sec < 3600) return `${Math.floor(sec / 60)} min ago`;
  if (sec < 86400) { const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60); return `${h} h ${m} min ago`; }
  return `${Math.floor(sec / 86400)} d ago`;
}

/** Clock time in the PC's time zone (falls back to the viewer's). */
export function pcTime(epochSec: number, tz?: string, withDay = false): string {
  const opts: Intl.DateTimeFormatOptions = { hour: "2-digit", minute: "2-digit", hour12: false };
  if (withDay) opts.weekday = "short";
  try { return new Intl.DateTimeFormat(undefined, { ...opts, timeZone: tz }).format(epochSec * 1000); }
  catch { return new Intl.DateTimeFormat(undefined, opts).format(epochSec * 1000); }
}
export function logTime(epochSec: number, tz?: string): string {
  const d = new Date(epochSec * 1000), now = Date.now();
  const sameDay = Math.abs(now - d.getTime()) < 20 * 3600 * 1000;
  try {
    return new Intl.DateTimeFormat(undefined, {
      hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false,
      ...(sameDay ? {} : { month: "short", day: "numeric" }), timeZone: tz,
    }).format(d);
  } catch { return d.toLocaleTimeString(); }
}

/** cool cyan -> green -> amber -> red; GPU throttle zone starts ~83 °C */
export function tempClass(c: Num, kind: "gpu" | "hot" | "cpu" | "ram" | "ssd" = "gpu"): string {
  if (!isNum(c)) return "na";
  const [cool, ok, warm] = kind === "gpu" ? [45, 65, 75] : kind === "hot" ? [45, 75, 85] : kind === "cpu" ? [45, 75, 88] : kind === "ram" ? [40, 55, 70] : [40, 55, 68];
  return c < cool ? "t-cool" : c < ok ? "t-ok" : c < warm ? "t-warm" : "t-hot";
}
export function tempColor(c: Num): string {
  if (!isNum(c)) return "#5b6676";
  return c < 45 ? "#3fd6ff" : c < 65 ? "#44e08a" : c < 75 ? "#ffb547" : "#ff4d5e";
}

export function esc(s: string): string {
  return s.replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]!));
}
