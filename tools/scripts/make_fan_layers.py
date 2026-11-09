"""Builds the hero image layers from photo A (run from the assets folder).

Output (assets/processed):
  card-base[-mobile].webp   the card with each rotor's blades replaced by the heatsink behind them
  fanN-rotor-bK.webp        each fan's blades + hub as a frontal (un-skewed) disc, blur level K = 0..3
  fans.json                 per-fan transform: frontal disc -> photo pixels (2x3 affine), plus spin direction
Nothing here invents detail: rotors are the photo's own pixels, un-skewed with the measured ellipse of each hub badge.
"""
import cv2, numpy as np, json, os
SRC = 'originals/photo-A-card-fans.jpg'
OUT = 'processed'
os.makedirs(OUT, exist_ok=True)
a = cv2.imread(SRC)
H, W = a.shape[:2]
# Ellipse of each hub badge (cv2.fitEllipse on photo A) and rotor-to-badge size ratio, measured by eye-overlay
FANS = [((2318.2, 1011.3), (132.5, 162.3), 141.4, 2.90),
        ((2853.4, 1133.1), (141.6, 166.2), 133.8, 2.90),
        ((3424.9, 1263.2), (149.7, 171.6), 122.9, 3.05)]
R = 420                      # frontal rotor radius in the unwarped working space
S = int(R * 2.6); C = S / 2  # working square (room for the blurred ring past the blade tips)
yy, xx = np.mgrid[0:S, 0:S]; rr = np.hypot(xx - C, yy - C)

def fan_affine(c, w, h, ang, k):
    t = np.deg2rad(ang)
    Rm = np.array([[np.cos(t), -np.sin(t)], [np.sin(t), np.cos(t)]])
    M = Rm @ np.diag([w / 2 * k / R, h / 2 * k / R])
    off = np.array(c) - M @ np.array([C, C])
    return np.hstack([M, off.reshape(2, 1)])     # working (u,v) -> photo (x,y)

base = a.copy().astype(np.float32)
meta = []
for i, (c, (w, h), ang, k) in enumerate(FANS):
    A = fan_affine(c, w, h, ang, k)
    un = cv2.warpAffine(a, A, (S, S), flags=cv2.WARP_INVERSE_MAP | cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)
    g = cv2.cvtColor(un, cv2.COLOR_BGR2GRAY).astype(np.float32)
    m = cv2.blur(g, (9, 9)); sd = np.sqrt(np.maximum(cv2.blur(g * g, (9, 9)) - m * m, 0))
    blade = ((m < 75) & (sd < 14)).astype(np.uint8) * 255
    blade = cv2.morphologyEx(blade, cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))
    blade = cv2.morphologyEx(blade, cv2.MORPH_CLOSE, np.ones((9, 9), np.uint8))
    blade[rr < R * 0.36] = 255           # hub + badge always turn with the rotor
    blade[rr > R * 0.985] = 0
    alpha = cv2.GaussianBlur(blade, (5, 5), 0)
    # What sits behind the blades: the visible fins spread into the covered area, darkened (it's in shadow)
    disk = (rr < R * 0.99).astype(np.uint8) * 255
    # What sits behind the spinning blades: the whole fan disc, motion-blurred all the way round (like a fast fan),
    # so no still blade shapes stay visible underneath the rotor layer.
    acc = np.zeros(un.shape, np.float32)
    steps = 90
    for k in range(steps):
        Mr = cv2.getRotationMatrix2D((C, C), k * 360.0 / steps, 1)
        acc += cv2.warpAffine(un, Mr, (S, S), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE).astype(np.float32)
    behind = acc / steps * 0.8
    # blur reaches past the blade tips (so no still blade edges peek out) and fades out softly
    hole = (np.clip((R * 1.22 - rr) / (R * 0.09), 0, 1) * 255).astype(np.uint8)
    # rotor RGBA, plus rotational blur levels for high speeds (anti wagon-wheel)
    rgba = np.dstack([un, alpha])
    for lvl, span in enumerate([0, 10, 24, 50]):
        if span == 0:
            out = rgba
        else:
            acc = np.zeros((S, S, 4), np.float32); n = 24
            pm = rgba.astype(np.float32); pm[..., :3] *= pm[..., 3:4] / 255
            for s in range(n):
                th = (s / (n - 1) - .5) * span
                Mr = cv2.getRotationMatrix2D((C, C), th, 1)
                acc += cv2.warpAffine(pm, Mr, (S, S), flags=cv2.INTER_LINEAR)
            acc /= n
            out = acc.copy(); a_ = np.maximum(acc[..., 3:4], 1e-3)
            out[..., :3] = np.clip(acc[..., :3] * 255 / a_, 0, 255)
            out = out.astype(np.uint8)
        small = cv2.resize(out, (int(S * .62), int(S * .62)), interpolation=cv2.INTER_AREA)
        cv2.imwrite(f'{OUT}/fan{i}-rotor-b{lvl}.webp', small, [cv2.IMWRITE_WEBP_QUALITY, 88])
    # write the "behind" back into the base photo, inside the rotor disc only
    back = cv2.warpAffine(behind, A, (W, H), flags=cv2.INTER_CUBIC)
    fmask = cv2.warpAffine(cv2.GaussianBlur(hole, (5, 5), 0).astype(np.float32) / 255, A, (W, H), flags=cv2.INTER_LINEAR)
    base = base * (1 - fmask[..., None]) + back * fmask[..., None]
    # transform for the website: frontal disc of the exported (0.62-scaled) rotor -> photo pixels
    sc = .62
    Aw = A.copy(); Aw[:, :2] = A[:, :2] / sc
    meta.append({'affine': [round(float(v), 5) for v in Aw.flatten()], 'size': int(S * sc),
                 'center': [round(float(c[0]), 1), round(float(c[1]), 1)],
                 'minorAxisDeg': ang, 'tilt': round(float(np.sqrt(1 - (min(w, h) / max(w, h)) ** 2)), 3)})
base = np.clip(base, 0, 255).astype(np.uint8)
cv2.imwrite(f'{OUT}/card-base.webp', cv2.resize(base, (2400, 1350), interpolation=cv2.INTER_AREA), [cv2.IMWRITE_WEBP_QUALITY, 86])
cv2.imwrite(f'{OUT}/card-base-mobile.webp', cv2.resize(base, (1400, 788), interpolation=cv2.INTER_AREA), [cv2.IMWRITE_WEBP_QUALITY, 84])
cv2.imwrite(f'{OUT}/card-photo.webp', cv2.resize(a, (2400, 1350), interpolation=cv2.INTER_AREA), [cv2.IMWRITE_WEBP_QUALITY, 86])
prev = json.load(open(f'{OUT}/fans.json')) if os.path.exists(f'{OUT}/fans.json') else {}
out = {'photoW': W, 'photoH': H, 'fans': meta}
if 'card' in prev: out['card'] = prev['card']   # hand-traced card outline: keep it
json.dump(out, open(f'{OUT}/fans.json', 'w'), indent=1)

# Photo B: 2x for sharpness on retina screens (Lanczos + gentle sharpen; to be replaced by Real-ESRGAN on the PC)
b = cv2.imread('originals/photo-B-gpu-core.webp', cv2.IMREAD_UNCHANGED)
b2 = cv2.resize(b, (b.shape[1] * 2, b.shape[0] * 2), interpolation=cv2.INTER_LANCZOS4)
rgb = b2[..., :3].astype(np.float32); bl = cv2.GaussianBlur(rgb, (0, 0), 1.2)
b2[..., :3] = np.clip(rgb + 0.35 * (rgb - bl), 0, 255).astype(np.uint8)
cv2.imwrite(f'{OUT}/gpu-core.webp', b2, [cv2.IMWRITE_WEBP_QUALITY, 88])
cv2.imwrite(f'{OUT}/gpu-core-mobile.webp', cv2.resize(b2, (1000, 737), interpolation=cv2.INTER_AREA), [cv2.IMWRITE_WEBP_QUALITY, 86])
print('done', [ (f, os.path.getsize(f'{OUT}/{f}')//1024) for f in sorted(os.listdir(OUT))])
