// Telemetry sent by the PC (version 1). Any value that can't be read is null, never a guess.
export type Num = number | null;

export interface Fan { pct: Num; rpm: Num }
export interface Temp { name: string; tempC: Num }
export interface LogEvent { id: string; t: number; level: "info" | "warn" | "error" | string; msg: string; cat?: "pc" | "salad" | "earnings" | string }
export interface Schedule { id: string; action: "pc_off" | "pc_sleep" | "salad_off"; at: number; missed?: boolean }

export type SaladState = "not_running" | "idle" | "paused" | "chopping" | "locked_out" | "unknown";

export interface Telemetry {
  v: 1;
  t: number;
  pc?: { tz: string; tzOffsetMin: number; name?: string };
  gpu: { name?: string; tempC: Num; coreC: Num; hotspotC: Num; util: Num; vramUsedMB: Num; vramTotalMB: Num; powerW: Num; fans: Fan[] };
  cpu: { name?: string; tempC: Num; powerW: Num; util?: Num };
  ram: Temp[];
  ssd: Temp[];
  salad: {
    state: SaladState;
    hold: boolean;
    gameMode: { activeGame: string | null };
    earnings: null | { balanceUSD: Num; estNextHourLowUSD: Num; estNextHourHighUSD: Num; last24hUSD: Num; updatedAt: number; nextHourFrom?: "salad" | "history" | null; nextHourAt?: number | null; lastPayoutUSD?: Num; lastPayoutAt?: number | null };
  };
  safety?: { limitC: number; sensor: "core" | "hotspot" | "either"; hotspotLimitC?: number; sensorFail?: boolean;
             lockout: null | { at: number; tempC: number; sensor: string } };
  afterburner?: { available: boolean; profile: number | null; names: string[] };
  shutdown?: { pendingAt: number | null; kind?: "off" | "sleep" | null };
  schedules: Schedule[];
}

/** Compact history point kept by the relay (see relay compact()). */
export interface HistPoint { t: number; g: Num[]; c: Num[]; r: Num[]; d: Num[] }
