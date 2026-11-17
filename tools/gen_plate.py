"""Synthetic inspection samples: a stamped mounting plate, 640x480 8-bit grayscale.

Each image is either good or carries exactly one defect:
  missinghole    - one of the four corner holes was not punched
  notchdefect    - the key notch on the top edge was not punched
  distancedefect - one corner hole is punched at the wrong position
  scratch        - scratches on the surface

Usage:  python gen_plate.py --out <dir> --count 40 --seed 1
        python gen_plate.py --out <dir> --reference
Writes NNN_<label>.png plus labels.csv / labels.json with the true dimensions.
"""
import argparse
import csv
import json
import math
import os

import cv2
import numpy as np

W, H = 640, 480
S = 3                      # supersampling factor
PX_PER_MM = 4.0            # final image scale

# nominal part, mm, origin at plate centre, +y down
BODY_W, BODY_H, CORNER_R = 100.0, 64.0, 5.0
BORE_D = 22.0
HOLE_D = 8.0
HOLES = {"TL": (-38.0, -20.0), "TR": (38.0, -20.0), "BL": (-38.0, 20.0), "BR": (38.0, 20.0)}
NOTCH_X, NOTCH_W, NOTCH_DEPTH = -16.0, 14.0, 8.0     # off-centre: fixes orientation
CHAMFER = 12.0                                        # bottom-right corner cut

DEFECTS = ["missinghole", "notchdefect", "distancedefect", "scratch"]


def noise(rng, shape, scale):
    """Smooth noise in [-1, 1]; scale = feature size in pixels."""
    h, w = shape
    small = rng.standard_normal((max(2, int(h / scale) + 2), max(2, int(w / scale) + 2))).astype(np.float32)
    n = cv2.resize(small, (w, h), interpolation=cv2.INTER_CUBIC)
    return n / (np.abs(n).max() + 1e-6)


def draw_part(holes, has_notch):
    """Upright part mask at supersampled scale, centred in the canvas."""
    k = PX_PER_MM * S
    cw, ch = W * S, H * S
    cx, cy = cw / 2, ch / 2
    m = np.zeros((ch, cw), np.uint8)

    def p(x, y):
        return int(round(cx + x * k)), int(round(cy + y * k))

    hw, hh, r = BODY_W / 2, BODY_H / 2, CORNER_R
    cv2.rectangle(m, p(-hw + r, -hh), p(hw - r, hh), 255, -1)
    cv2.rectangle(m, p(-hw, -hh + r), p(hw, hh - r), 255, -1)
    for sx in (-1, 1):
        for sy in (-1, 1):
            cv2.circle(m, p(sx * (hw - r), sy * (hh - r)), int(round(r * k)), 255, -1, cv2.LINE_AA)
    # chamfer bottom-right
    tri = np.array([p(hw - CHAMFER, hh + 1), p(hw + 1, hh + 1), p(hw + 1, hh - CHAMFER)], np.int32)
    cv2.fillPoly(m, [tri], 0, cv2.LINE_AA)
    if has_notch:
        cv2.rectangle(m, p(NOTCH_X - NOTCH_W / 2, -hh - 1), p(NOTCH_X + NOTCH_W / 2, -hh + NOTCH_DEPTH), 0, -1)
    cv2.circle(m, p(0, 0), int(round(BORE_D / 2 * k)), 0, -1, cv2.LINE_AA)
    for (x, y, d) in holes.values():
        cv2.circle(m, p(x, y), int(round(d / 2 * k)), 0, -1, cv2.LINE_AA)
    return m


def make_sample(rng, label, reference=False):
    k = PX_PER_MM * S
    cw, ch = W * S, H * S

    # ---- geometry (good parts get a small tolerance jitter) ----
    tol = 0.0 if reference else 1.0
    holes = {n: (x + tol * rng.normal(0, 0.05), y + tol * rng.normal(0, 0.05), HOLE_D + tol * rng.normal(0, 0.03))
             for n, (x, y) in HOLES.items()}
    has_notch = True
    detail = ""
    if label == "missinghole":
        n = rng.choice(list(holes))
        del holes[n]
        detail = f"hole {n} missing"
    elif label == "notchdefect":
        has_notch = False
        detail = "notch missing"
    elif label == "distancedefect":
        n = rng.choice(list(holes))
        x, y, d = holes[n]
        shift = rng.uniform(2.5, 4.0) * (1 if x < 0 else -1)   # always toward the centre
        holes[n] = (x + shift, y, d)
        detail = f"hole {n} shifted {shift:+.2f} mm in x"

    mask = draw_part(holes, has_notch)

    # ---- scratches, drawn in the part frame ----
    scr = np.zeros((ch, cw), np.float32)
    if label == "scratch":
        cnt = int(rng.integers(2, 4))
        for _ in range(cnt):
            while True:                # keep the whole scratch on solid metal, clear of every hole
                x0 = rng.uniform(-36, 36); y0 = rng.uniform(-22, 22)
                a = rng.uniform(0, math.pi); ln = rng.uniform(18, 34)
                ends = [(x0 + t * ln * math.cos(a), y0 + t * ln * math.sin(a)) for t in np.linspace(-0.5, 0.5, 12)]
                if all(abs(ex) < BODY_W / 2 - 4 and abs(ey) < BODY_H / 2 - 4
                       and math.hypot(ex, ey) > BORE_D / 2 + 3
                       and all(math.hypot(ex - hx, ey - hy) > HOLE_D / 2 + 3 for hx, hy in HOLES.values())
                       and not (abs(ex - NOTCH_X) < NOTCH_W / 2 + 3 and ey < -BODY_H / 2 + NOTCH_DEPTH + 3)
                       and not (ex + ey > BODY_W / 2 + BODY_H / 2 - CHAMFER - 4)
                       for ex, ey in ends):
                    break
            pts = []
            for t in np.linspace(-0.5, 0.5, 12):
                wob = rng.normal(0, 0.25)
                pts.append((cw / 2 + (x0 + t * ln * math.cos(a) - wob * math.sin(a)) * k,
                            ch / 2 + (y0 + t * ln * math.sin(a) + wob * math.cos(a)) * k))
            cv2.polylines(scr, [np.array(pts, np.int32)], False, 1.0, int(rng.integers(3, 6)) * S // 2, cv2.LINE_AA)
        scr = cv2.GaussianBlur(scr, (0, 0), 0.8 * S)
        detail = f"{cnt} scratches"

    # ---- pose ----
    ang = rng.uniform(-25, 25) if rng.random() < 0.2 else rng.uniform(-3, 3)
    tx, ty = rng.uniform(-22, 22), rng.uniform(-18, 18)
    if reference:                      # nominal part, centred, upright
        ang, tx, ty = 0.0, 0.0, 0.0
    M = cv2.getRotationMatrix2D((cw / 2, ch / 2), ang, 1.0)
    M[0, 2] += tx * S
    M[1, 2] += ty * S
    mask = cv2.warpAffine(mask, M, (cw, ch), flags=cv2.INTER_LINEAR).astype(np.float32) / 255.0
    scr = cv2.warpAffine(scr, M, (cw, ch), flags=cv2.INTER_LINEAR)

    # ---- appearance ----
    yy, xx = np.mgrid[0:ch, 0:cw].astype(np.float32)
    light = 1.0 + 0.10 * ((xx / cw - 0.5) * rng.uniform(-1, 1) + (yy / ch - 0.5) * rng.uniform(-1, 1))

    metal = (168 + 16 * noise(rng, (ch, cw), 60 * S) + 12 * noise(rng, (ch, cw), 9 * S)
             + 8 * noise(rng, (ch, cw), 2.5 * S) + 5 * rng.standard_normal((ch, cw)).astype(np.float32))
    spark = (rng.random((ch, cw)) > 0.9995).astype(np.float32)          # zinc-spangle glints
    metal += 70 * cv2.GaussianBlur(spark, (0, 0), 0.9 * S) * 12
    edge = np.clip(cv2.distanceTransform((mask > 0.5).astype(np.uint8), cv2.DIST_L2, 5) / (2.2 * S), 0, 1)
    metal *= (0.72 + 0.28 * edge) * light
    metal -= 95 * scr                                                    # scratches read dark

    bg = 36 + 5 * noise(rng, (ch, cw), 80 * S) + 3 * noise(rng, (ch, cw), 3 * S)
    r2 = ((xx - cw / 2) / cw) ** 2 + ((yy - ch / 2) / ch) ** 2
    bg *= (1.0 - 0.5 * r2)
    Ms = np.float32([[1, 0, 4 * S], [0, 1, 6 * S]])                      # drop shadow, lower right
    shadow = cv2.GaussianBlur(cv2.warpAffine(mask, Ms, (cw, ch)), (0, 0), 3.5 * S)
    bg *= (1.0 - 0.6 * shadow)

    img = mask * metal + (1 - mask) * bg
    img = cv2.GaussianBlur(img, (0, 0), 0.7 * S)
    img = cv2.resize(img, (W, H), interpolation=cv2.INTER_AREA)
    img += rng.normal(0, 1.8, img.shape)
    img = np.clip(img, 0, 255).astype(np.uint8)

    # ---- ground truth in final image pixels ----
    Mf = M.copy(); Mf[:, 2] /= S

    def to_px(x, y):
        u, v = W / 2 + x * PX_PER_MM, H / 2 + y * PX_PER_MM
        return [round(float(Mf[0, 0] * u + Mf[0, 1] * v + Mf[0, 2]), 2),
                round(float(Mf[1, 0] * u + Mf[1, 1] * v + Mf[1, 2]), 2)]

    def pitch(a, b):
        if a in holes and b in holes:
            return round(float(math.hypot(holes[a][0] - holes[b][0], holes[a][1] - holes[b][1])), 3)
        return None

    truth = {
        "label": label, "ok": label == "good", "detail": detail,
        "px_per_mm": PX_PER_MM, "angle_deg": round(float(ang), 2),
        "center_px": to_px(0, 0),
        "holes_px": {n: to_px(x, y) for n, (x, y, d) in holes.items()},
        "hole_dia_mm": {n: round(float(d), 3) for n, (x, y, d) in holes.items()},
        "pitch_top_mm": pitch("TL", "TR"), "pitch_bottom_mm": pitch("BL", "BR"),
        "has_notch": has_notch,
    }
    return img, truth


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--count", type=int, default=40)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--defect-rate", type=float, default=0.5)
    ap.add_argument("--ext", default="png", choices=["png", "bmp"])
    ap.add_argument("--reference", action="store_true",
                    help="write only reference.<ext>: a nominal good part, centred and upright")
    a = ap.parse_args()

    rng = np.random.default_rng(a.seed)
    os.makedirs(a.out, exist_ok=True)
    if a.reference:
        img, truth = make_sample(rng, "good", reference=True)
        name = f"reference.{a.ext}"
        cv2.imencode("." + a.ext, img)[1].tofile(os.path.join(a.out, name))
        truth["file"] = name
        with open(os.path.join(a.out, "reference.json"), "w", encoding="utf-8") as f:
            json.dump(truth, f, indent=1)
        print(name, "->", a.out)
        return
    rows = []
    for i in range(1, a.count + 1):
        label = str(rng.choice(DEFECTS)) if rng.random() < a.defect_rate else "good"
        img, truth = make_sample(rng, label)
        name = f"{i:03d}_{label}.{a.ext}"
        cv2.imencode("." + a.ext, img)[1].tofile(os.path.join(a.out, name))   # unicode-safe path
        truth["file"] = name
        rows.append(truth)

    with open(os.path.join(a.out, "labels.json"), "w", encoding="utf-8") as f:
        json.dump(rows, f, indent=1)
    with open(os.path.join(a.out, "labels.csv"), "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["file", "label", "ok", "detail", "angle_deg", "pitch_top_mm", "pitch_bottom_mm", "has_notch"])
        for r in rows:
            w.writerow([r["file"], r["label"], r["ok"], r["detail"], r["angle_deg"],
                        r["pitch_top_mm"], r["pitch_bottom_mm"], r["has_notch"]])
    counts = {}
    for r in rows:
        counts[r["label"]] = counts.get(r["label"], 0) + 1
    print(a.count, "images ->", a.out, counts)


if __name__ == "__main__":
    main()
