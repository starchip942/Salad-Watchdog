/**
 * Animated hero: the owner's MSI RTX 5070 Ti from photo A.
 * Layers: static card (blades removed) -> each fan's own rotor, turned in that fan's measured plane
 * -> warm air pushed backwards through the card by all three fans -> temperature glow.
 * All of it runs in the browser; the PC only sends numbers.
 */
interface FanMeta { affine: number[]; size: number; center: [number, number]; minorAxisDeg: number; tilt: number }
interface Meta { photoW: number; photoH: number; fans: FanMeta[]; card?: [number, number][] }

const BASE_W = 2400;           // width the base image is exported at
const TAU = 1.2;               // fan inertia time constant (s): spool up / coast down
const SPIN_DIR = -1;           // blade curvature says clockwise as seen from the front
const BLUR_RPM = [125, 400, 750];   // fans are shown at double speed, so they blur sooner

export interface HeroInput { rpm: number[]; tempC: number | null; online: boolean }

export class Hero {
  ctx: CanvasRenderingContext2D;
  meta!: Meta;
  base = new Image();
  photo = new Image();
  rotors: HTMLImageElement[][] = [];
  ready = false;
  input: HeroInput = { rpm: [0, 0, 0], tempC: null, online: false };
  rpm = [0, 0, 0];             // smoothed (inertia)
  angle = [0, 0.7, 1.9];
  particles: P[] = [];
  puff!: HTMLCanvasElement;
  last = 0;
  running = false;
  reduced = matchMedia("(prefers-reduced-motion: reduce)");
  dpr = 1;
  view = { s: 1, x: 0, y: 0 };    // photo px -> canvas px
  glowTemp = 40;
  onLayout?: (rect: { cardLeft: number; cardRight: number }) => void;

  /** fit: "cover" fills the area with the whole photo (website on desktop); "card" always shows the whole card (phones, PC panel) */
  constructor(public canvas: HTMLCanvasElement, private assets: string, private mobile: () => boolean, private fit: () => "cover" | "card" = () => (mobile() ? "card" : "cover")) {
    this.ctx = canvas.getContext("2d")!;
  }

  async load() {
    this.meta = await (await fetch(this.assets + "fans.json")).json();
    const small = this.mobile();
    this.base.src = this.assets + (small ? "card-base-mobile.webp" : "card-base.webp");
    this.photo.src = this.assets + "card-photo.webp";
    const loads: Promise<unknown>[] = [this.base.decode(), this.photo.decode().catch(() => 0)];
    this.rotors = this.meta.fans.map((_, i) => [0, 1, 2, 3].map(b => {
      const im = new Image(); im.src = `${this.assets}fan${i}-rotor-b${b}.webp`; loads.push(im.decode()); return im;
    }));
    this.puff = makePuff();
    await Promise.all(loads);
    this.ready = true;
    this.resize();
    new ResizeObserver(() => this.resize()).observe(this.canvas);
    document.addEventListener("visibilitychange", () => this.kick());
    this.reduced.addEventListener?.("change", () => this.kick());
    this.kick();
  }

  set(input: HeroInput) { this.input = input; this.kick(); }

  resize() {
    const r = this.canvas.getBoundingClientRect();
    this.dpr = Math.min(window.devicePixelRatio || 1, 2);
    this.canvas.width = Math.round(r.width * this.dpr);
    this.canvas.height = Math.round(r.height * this.dpr);
    const W = this.meta.photoW, H = this.meta.photoH, cw = this.canvas.width, ch = this.canvas.height;
    if (this.fit() === "card") {
      // the whole card (x 49–98 %, y 20–82 % of the photo, with a little room for the air) fits inside, centred
      let bw = W * 0.50, bh = H * 0.64, cx = W * 0.735, cy = H * 0.50;
      if (this.meta.card) {
        // centre the card itself, leaving a little room above for the air
        const [x0, y0, x1, y1] = this.cardBox();
        bw = (x1 - x0) * 1.45; bh = (y1 - y0) * 1.75; cx = (x0 + x1) / 2 + (x1 - x0) * 0.04; cy = (y0 + y1) / 2 - (y1 - y0) * 0.16;
      }
      const s = Math.min((cw * 0.94) / bw, (ch * 0.94) / bh);
      this.view = { s, x: cw / 2 - cx * s, y: ch / 2 - cy * s };
    } else {
      const s = Math.max(cw / W, ch / H);
      this.view = { s, x: (cw - W * s) / 2, y: (ch - H * s) / 2 };
    }
    this.onLayout?.({ cardLeft: (this.view.x + W * 0.47 * this.view.s) / this.dpr, cardRight: (this.view.x + W * 0.98 * this.view.s) / this.dpr });
    this.draw(0);
  }

  kick() {
    if (!this.ready || this.running) return;
    if (document.visibilityState !== "visible") return;
    this.running = true;
    this.last = performance.now();
    requestAnimationFrame(t => this.frame(t));
  }

  frame(t: number) {
    const dt = Math.min(0.05, (t - this.last) / 1000);
    this.last = t;
    const still = this.step(dt);
    this.draw(dt);
    // keep animating while anything moves; stop when the tab is hidden or everything has settled
    if (document.visibilityState !== "visible" || (still && this.particles.length === 0)) { this.running = false; return; }
    requestAnimationFrame(tt => this.frame(tt));
  }

  /** advance physics; returns true when nothing is moving */
  step(dt: number): boolean {
    const reduced = this.reduced.matches;
    let moving = false;
    for (let i = 0; i < 3; i++) {
      const target = this.input.online ? (this.input.rpm[i] ?? this.input.rpm[0] ?? 0) : 0;
      this.rpm[i] += (target - this.rpm[i]) * (1 - Math.exp(-dt / TAU));
      if (target === 0 && this.rpm[i] < 3) this.rpm[i] = 0;     // zero-RPM mode: fully stopped
      // shown speed: real revolutions slowed down, capped, so the eye never sees wagon-wheel strobing
      const shownDps = Math.min(this.rpm[i] * 2.2, 2480) * (reduced ? 0.15 : 1);
      this.angle[i] += SPIN_DIR * shownDps * dt * Math.PI / 180;
      if (this.rpm[i] > 0) moving = true;
    }
    const tgt = this.input.tempC ?? 40;
    this.glowTemp += (tgt - this.glowTemp) * (1 - Math.exp(-dt / 2));
    if (!reduced) this.stepAir(dt);
    else this.particles.length = 0;
    return !moving;
  }

  // ----- air pushed backwards through the card by each fan -----
  stepAir(dt: number) {
    const cap = this.mobile() ? 70 : 150;
    for (let i = 0; i < 3; i++) {
      const f = this.meta.fans[i];
      const strength = Math.min(1, this.rpm[i] / 2200);
      if (strength <= 0.02) continue;
      const rate = 40 * strength;                       // air streaks per second per fan
      let n = rate * dt; if (Math.random() < n % 1) n = Math.ceil(n); else n = Math.floor(n);
      for (let k = 0; k < n && this.particles.length < cap; k++) {
        // start somewhere on the rotor disc, in that fan's own plane
        const a = Math.random() * Math.PI * 2, r = Math.sqrt(Math.random()) * 0.42 * f.size;
        const [x, y] = applyAff(f.affine, f.size / 2 + Math.cos(a) * r, f.size / 2 + Math.sin(a) * r);
        // "backwards" = along the fan's axis, away from the viewer: the minor axis of its ellipse, pointing up
        const th = f.minorAxisDeg * Math.PI / 180;
        let dx = Math.cos(th), dy = Math.sin(th);
        if (dy > 0) { dx = -dx; dy = -dy; }
        const sp = (700 + 900 * strength) * (0.75 + Math.random() * 0.5);   // pushed air moves fast
        // pushed through the heatsink and out of the back: it starts behind the card and only shows once it clears the card
        this.particles.push({ x: x + dx * f.size * 0.25, y: y + dy * f.size * 0.25, vx: dx * sp + (Math.random() - .5) * 120, vy: dy * sp + (Math.random() - .5) * 120,
          age: 0, life: 0.7 + Math.random() * 0.6, size: 14 + Math.random() * 20, spin: Math.random() * 6 });
      }
    }
    const t = performance.now() / 1000;
    for (const p of this.particles) {
      p.age += dt;
      // a jet of air: mostly straight, a little flutter, slowing down as it spreads out
      p.vx += Math.sin(p.y * 0.006 + t * 3 + p.spin) * 60 * dt;
      p.vy += Math.cos(p.x * 0.006 + t * 3 + p.spin) * 60 * dt;
      p.vx *= 1 - 1.2 * dt; p.vy *= 1 - 1.2 * dt;
      p.x += p.vx * dt; p.y += p.vy * dt;
    }
    this.particles = this.particles.filter(p => p.age < p.life);
  }

  draw(_dt: number) {
    if (!this.ready) return;
    const { ctx, view: v } = this;
    const cw = this.canvas.width, ch = this.canvas.height;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, cw, ch);
    const settled = this.rpm.every(r => r === 0);
    const img = settled && this.photo.naturalWidth ? this.photo : this.base;   // at rest: the untouched photo
    const k = this.meta.photoW / (img.naturalWidth || BASE_W);
    ctx.setTransform(v.s * k, 0, 0, v.s * k, v.x, v.y);
    if (!this.cutout) ctx.drawImage(img, 0, 0);
    if (!this.cutout && this.fit() === "card") {
      // phones: the photo does not reach every screen edge, so melt its edges softly into the page instead of a hard grey block
      const px = v.x, py = v.y, pw = this.meta.photoW * v.s, ph = this.meta.photoH * v.s;
      const f = Math.min(pw, ph) * 0.22;
      ctx.save();
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.globalCompositeOperation = "destination-out";
      const strip = (x0: number, y0: number, x1: number, y1: number, rx: number, ry: number, rw: number, rh: number) => {
        const g = ctx.createLinearGradient(x0, y0, x1, y1);
        g.addColorStop(0, "rgba(0,0,0,1)"); g.addColorStop(1, "rgba(0,0,0,0)");
        ctx.fillStyle = g; ctx.fillRect(rx, ry, rw, rh);
      };
      strip(px, 0, px + f, 0, px, py, f, ph);
      strip(px + pw, 0, px + pw - f, 0, px + pw - f, py, f, ph);
      strip(0, py, 0, py + f, px, py, pw, f);
      strip(0, py + ph, 0, py + ph - f, px, py + ph - f, pw, f);
      ctx.restore();
    }
    ctx.setTransform(v.s, 0, 0, v.s, v.x, v.y);
    if (this.cutout && this.meta.card) {
      // just the card: a soft shadow and a faint temperature-coloured glow underneath instead of the photo's floor
      const [x0, y0, x1, y1] = this.cardBox();
      const cx = (x0 + x1) / 2, cy = y1 - (y1 - y0) * 0.06, rw = (x1 - x0) * 0.55;
      ctx.save();
      ctx.setTransform(v.s, 0, 0, v.s * 0.22, v.x, v.y + cy * v.s * 0.78);
      const sh = ctx.createRadialGradient(cx, cy, 0, cx, cy, rw);
      sh.addColorStop(0, "rgba(0,0,0,.55)"); sh.addColorStop(1, "rgba(0,0,0,0)");
      ctx.fillStyle = sh; ctx.fillRect(cx - rw, cy - rw, rw * 2, rw * 2);
      const gl = ctx.createRadialGradient(cx, cy, 0, cx, cy, rw * 1.1);
      gl.addColorStop(0, glowColor(this.glowTemp, this.input.online).replace(",0.16)", ",0.22)")); gl.addColorStop(1, "rgba(0,0,0,0)");
      ctx.globalCompositeOperation = "screen"; ctx.fillStyle = gl; ctx.fillRect(cx - rw * 1.1, cy - rw * 1.1, rw * 2.2, rw * 2.2);
      ctx.restore();
    }

    // air
    if (this.particles.length) {
      ctx.save();
      ctx.globalCompositeOperation = "screen";
      ctx.lineCap = "round";
      const warm = Math.max(0, Math.min(1, (this.glowTemp - 45) / 40));
      // cool air is a faint blue-white, hot air slightly warmer
      const rC = Math.round(190 + 60 * warm), gC = Math.round(215 + 10 * warm), bC = Math.round(255 - 70 * warm);
      for (const p of this.particles) {
        const u = p.age / p.life;
        // thin streaks along the direction of travel: fade in, then thin out and vanish
        const alpha = Math.sin(Math.min(1, u * 5) * Math.PI / 2) * (1 - u) * 0.10;
        const len = 0.16;                                   // streak length = distance covered in 160 ms
        const x0 = p.x - p.vx * len, y0 = p.y - p.vy * len;
        const g = ctx.createLinearGradient(x0, y0, p.x, p.y);
        g.addColorStop(0, `rgba(${rC},${gC},${bC},0)`);
        g.addColorStop(1, `rgba(${rC},${gC},${bC},${alpha.toFixed(3)})`);
        ctx.strokeStyle = g;
        // a soft wide stroke plus a thinner core: reads as moving air, not rain
        ctx.lineWidth = p.size * (1 + u * 2);
        ctx.beginPath(); ctx.moveTo(x0, y0); ctx.lineTo(p.x, p.y); ctx.stroke();
        ctx.lineWidth = p.size * 0.35 * (1 + u);
        ctx.stroke();
      }
      ctx.restore();
    }

    // the card itself goes back on top, so the air only shows where it comes out behind the card
    if ((this.particles.length || this.cutout) && this.meta.card) {
      ctx.save();
      if (this.cutout && !this.input.online) ctx.globalAlpha = 0.5;
      ctx.beginPath();
      this.meta.card.forEach(([px, py], i) => i ? ctx.lineTo(px, py) : ctx.moveTo(px, py));
      ctx.closePath();
      ctx.clip();
      ctx.setTransform(v.s * k, 0, 0, v.s * k, v.x, v.y);
      ctx.drawImage(img, 0, 0);
      ctx.restore();
      ctx.setTransform(v.s, 0, 0, v.s, v.x, v.y);
    }

    if (!settled) {
      for (let i = 0; i < 3; i++) {
        const f = this.meta.fans[i], a = f.affine;
        ctx.save();
        ctx.transform(a[0], a[3], a[1], a[4], a[2], a[5]);
        ctx.translate(f.size / 2, f.size / 2);
        ctx.rotate(this.angle[i]);
        ctx.translate(-f.size / 2, -f.size / 2);
        // crossfade between blur levels as the fan speeds up
        const r = this.rpm[i];
        const lv = r < BLUR_RPM[0] ? r / BLUR_RPM[0] : r < BLUR_RPM[1] ? 1 + (r - BLUR_RPM[0]) / (BLUR_RPM[1] - BLUR_RPM[0]) : r < BLUR_RPM[2] ? 2 + (r - BLUR_RPM[1]) / (BLUR_RPM[2] - BLUR_RPM[1]) : 3;
        const lo = Math.max(0, Math.min(3, Math.floor(lv || 0))), fr = Math.max(0, lv - lo);
        ctx.globalAlpha = 1;
        ctx.drawImage(this.rotors[i][lo], 0, 0);
        if (lo < 3 && fr > 0.02) { ctx.globalAlpha = fr; ctx.drawImage(this.rotors[i][lo + 1], 0, 0); }
        ctx.restore();
      }
    }


    if (this.cutout) return;
    // temperature glow on the floor under the card
    ctx.save();
    ctx.globalCompositeOperation = "screen";
    const col = glowColor(this.glowTemp, this.input.online);
    const [gx, gy] = [this.meta.photoW * 0.72, this.meta.photoH * 0.72];
    const g = ctx.createRadialGradient(gx, gy, 0, gx, gy, this.meta.photoW * 0.28);
    g.addColorStop(0, col); g.addColorStop(1, "rgba(0,0,0,0)");
    ctx.fillStyle = g;
    ctx.setTransform(v.s, 0, 0, v.s * 0.38, v.x, v.y + gy * v.s * 0.62);
    ctx.fillRect(gx - this.meta.photoW * 0.3, gy - this.meta.photoW * 0.3, this.meta.photoW * 0.6, this.meta.photoW * 0.6);
    ctx.restore();

    if (!this.input.online) {
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.fillStyle = "rgba(7,9,12,.45)";
      ctx.fillRect(0, 0, cw, ch);
    }
  }

  /** true: draw only the card, its fans and the air (no photo background) */
  cutout = false;
  cardBox(): [number, number, number, number] {
    const c = this.meta.card!;
    const xs = c.map(p => p[0]), ys = c.map(p => p[1]);
    return [Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys)];
  }

  _warm?: HTMLCanvasElement;
  puffWarm() { return this._warm ??= makePuff("255,170,90"); }
}

interface P { x: number; y: number; vx: number; vy: number; age: number; life: number; size: number; spin: number }

function applyAff(a: number[], u: number, v: number): [number, number] {
  return [a[0] * u + a[1] * v + a[2], a[3] * u + a[4] * v + a[5]];
}
function makePuff(rgb = "235,240,245") {
  const c = document.createElement("canvas"); c.width = c.height = 128;
  const x = c.getContext("2d")!;
  const g = x.createRadialGradient(64, 64, 0, 64, 64, 64);
  g.addColorStop(0, `rgba(${rgb},1)`); g.addColorStop(0.35, `rgba(${rgb},.55)`); g.addColorStop(1, `rgba(${rgb},0)`);
  x.fillStyle = g; x.fillRect(0, 0, 128, 128);
  return c;
}
function glowColor(t: number, online: boolean) {
  if (!online) return "rgba(0,0,0,0)";
  const stops: [number, number[]][] = [[35, [63, 214, 255]], [60, [68, 224, 138]], [75, [255, 181, 71]], [88, [255, 77, 94]]];
  let c = stops[0][1];
  for (let i = 1; i < stops.length; i++) {
    if (t <= stops[i][0]) { const [t0, c0] = stops[i - 1], [t1, c1] = stops[i]; const u = Math.max(0, (t - t0) / (t1 - t0)); c = c0.map((v, k) => v + (c1[k] - v) * u); break; }
    c = stops[i][1];
  }
  return `rgba(${c.map(Math.round).join(",")},0.16)`;
}
