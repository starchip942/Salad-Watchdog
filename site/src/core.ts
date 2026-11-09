/**
 * Core die view: leader lines from the CORE and HOTSPOT callouts to the die in photo B.
 * Anchors are in percent of the image, so the lines stay right at any size.
 * The hotspot marker is illustrative: the sensor gives a number, not a location.
 */
export const DIE_CENTER = { x: 0.505, y: 0.47 };     // measured on photo B (die spans x 33–68 %, y 19–75 %)
export const HOT_MARK = { x: 0.585, y: 0.37 };

export function layoutCore(wrap: HTMLElement) {
  const img = wrap.querySelector("img")!, svg = wrap.querySelector("svg")!;
  const core = wrap.querySelector<HTMLElement>(".co-core")!, hot = wrap.querySelector<HTMLElement>(".co-hot")!;
  const W = wrap.clientWidth, ir = img.getBoundingClientRect(), wr = wrap.getBoundingClientRect();
  const ix = ir.left - wr.left, iy = ir.top - wr.top;
  const pt = (p: { x: number; y: number }) => [ix + p.x * ir.width, iy + p.y * ir.height];
  const [cx, cy] = pt(DIE_CENTER), [hx, hy] = pt(HOT_MARK);
  const cr = core.getBoundingClientRect(), hr = hot.getBoundingClientRect();
  const c = { l: cr.left - wr.left, t: cr.top - wr.top, w: cr.width, h: cr.height };
  const h = { l: hr.left - wr.left, t: hr.top - wr.top, w: hr.width, h: hr.height };
  svg.setAttribute("viewBox", `0 0 ${W} ${wrap.clientHeight}`);
  const narrow = wrap.classList.contains("narrow");
  // narrow phones: callouts sit above/below the photo with clean elbows; wide: beside it
  const corePath = narrow
    ? `M${c.l + 22} ${c.t + c.h} V${c.t + c.h + 18} H${cx - 30} L${cx} ${cy}`
    : `M${c.l + c.w} ${c.t + c.h / 2} H${c.l + c.w + 40} L${cx} ${cy}`;
  const hotPath = narrow
    ? `M${h.l + h.w - 22} ${h.t} V${h.t - 18} H${hx + 24} L${hx} ${hy}`
    : `M${h.l} ${h.t + h.h / 2} H${h.l - 40} L${hx} ${hy}`;
  svg.innerHTML = `
    <path class="lead lead-core" d="${corePath}" pathLength="1"/>
    <circle class="dot-core" cx="${cx}" cy="${cy}" r="5"/><circle class="ring-core" cx="${cx}" cy="${cy}" r="13"/>
    <path class="lead lead-hot" d="${hotPath}" pathLength="1"/>
    <rect class="dot-hot" x="${hx - 6}" y="${hy - 6}" width="12" height="12" transform="rotate(45 ${hx} ${hy})"/>`;
  const glow = wrap.querySelector<HTMLElement>(".dieglow")!;
  glow.style.left = ix + 0.33 * ir.width + "px"; glow.style.top = iy + 0.19 * ir.height + "px";
  glow.style.width = 0.35 * ir.width + "px"; glow.style.height = 0.56 * ir.height + "px";
}
