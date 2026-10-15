# gen-clouds.py - draws the splash cloud sprites assets/immersivewakes/textures/particle/splash_cloud_1..4.png after the
# hand-drawn ones of Wakes 0.7.1 (art/wakes-original, FINDINGS "Частица `splash_cloud`"): a strip of four 16x16 frames
# per file, the file sets the cloud's size, the frames of one file are the same size and only jitter. Two colours,
# white (255) and grey (215), alpha 0 or 255; a frame keeps its outer row and column empty.
#
#   python tools\gen-clouds.py                                 # writes the four strips
#   python tools\gen-clouds.py --seed 7 --preview <file.png>   # another set, plus a preview x8 beside the originals
#   python tools\gen-clouds.py --dry --preview <file.png>      # the preview only
#
# The body is a superellipse with lumps on its rim: exponent 2 is the round blob of Wakes, SQUARENESS above it fills
# the corners. Grey follows the originals: the rim that faces down and left past a diagonal through the centre, two
# pixels deep further down, and the pixels above and right of a slit through the body, where water shows. Fitted on
# the originals, this rule matches 87% of their body pixels; the rest is the hand of the artist.
import argparse, math, os, random
from PIL import Image

SQUARENESS = 2.6
LUMP = 0.09

WHITE = (255, 255, 255, 255)
GREY = (215, 215, 215, 255)
CLEAR = (0, 0, 0, 0)

FRAME = 16
FRAMES = 4

# Per file, largest first: centre, half axes, taper, opaque pixels per frame as the originals have them, slits, their
# length and how deep in the body they lie, grey creases, droplets. A positive taper narrows the bottom, as the larger
# clouds of Wakes do; a negative one narrows the top into the dome of its smallest cloud.
SIZES = [
    dict(c=(8.2, 7.9), r=(6.0, 6.3), taper=0.12, count=(117, 131), slits=(2, 3), slit_len=(2, 4), depth=2, creases=(1, 3), drops=(3, 7)),
    dict(c=(8.2, 7.7), r=(4.5, 4.8), taper=0.12, count=(69, 76), slits=(1, 2), slit_len=(2, 3), depth=2, creases=(0, 2), drops=(3, 5)),
    dict(c=(7.8, 7.7), r=(3.6, 3.7), taper=0.12, count=(46, 53), slits=(1, 1), slit_len=(1, 2), depth=1, creases=(0, 1), drops=(2, 4)),
    dict(c=(7.6, 8.5), r=(2.8, 2.9), taper=-0.1, count=(19, 28), slits=(0, 0), slit_len=(1, 1), depth=1, creases=(1, 2), drops=(1, 3)),
]

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "assets", "immersivewakes", "textures", "particle")
ORIGINALS = os.path.join(ROOT, "art", "wakes-original")

N8 = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1)]


def inside_frame(p):
    return 1 <= p[0] <= FRAME - 2 and 1 <= p[1] <= FRAME - 2


def body(cx, cy, rx, ry, taper, phases):
    pts = set()
    for y in range(FRAME):
        for x in range(FRAME):
            dx, dy = (x + 0.5 - cx) / rx, (y + 0.5 - cy) / ry
            rho = (abs(dx) ** SQUARENESS + abs(dy) ** SQUARENESS) ** (1 / SQUARENESS)
            if dy * taper > 0:
                rho *= 1 + abs(taper * dy)
            a = math.atan2(dy, dx)
            lump = sum(LUMP / k * 2 * math.sin(k * a + ph) for k, ph in phases)
            if rho <= 1 + lump:
                pts.add((x, y))
    return pts


def connected(pts):
    if not pts:
        return False
    start = next(iter(pts))
    seen, stack = {start}, [start]
    while stack:
        x, y = stack.pop()
        for dx, dy in N8:
            n = (x + dx, y + dy)
            if n in pts and n not in seen:
                seen.add(n)
                stack.append(n)
    return len(seen) == len(pts)


# A slit runs left to right, flat more often than slanted; the body stays depth pixels around it, so it never opens to
# the rim and always has grey above and to the right.
def cut_slits(rng, b, n, length, depth):
    slits = set()
    for _ in range(n):
        for _ in range(60):
            ln = rng.randint(*length)
            step = rng.choices([(1, 0), (1, 1), (1, -1)], [0.5, 0.25, 0.25])[0]
            x0, y0 = rng.choice(sorted(b))
            run = [(x0 + step[0] * i, y0 + step[1] * i) for i in range(ln)]
            ring = {(x + dx, y + dy) for x, y in run for dx in range(-depth, depth + 1) for dy in range(-depth, depth + 1)}
            if all(q in b for q in ring) and not any(q in slits or (q[0] + dx, q[1] + dy) in slits for q in run for dx, dy in N8):
                slits.update(run)
                break
    return b - slits, slits


def shade(b, slits):
    n = len(b)
    cx = sum(p[0] for p in b) / n
    cy = sum(p[1] for p in b) / n
    r = math.sqrt(n / math.pi)
    grey = set()
    for x, y in b:
        s = (-(x - cx) + (y - cy)) / r
        near = [(x - 1, y), (x, y + 1), (x - 1, y + 1)]
        far = [(x - 2, y + 1), (x - 1, y + 2), (x, y + 2), (x - 2, y)]
        if any(q in slits for q in near):
            grey.add((x, y))
        elif s > 0.6 and any(q not in b for q in near):
            grey.add((x, y))
        elif s > 1.2 and any(q not in b and q not in slits for q in far):
            grey.add((x, y))
    return grey, (cx, cy, r)


# Creases are grey dashes of one or two pixels in the white part of the body, as the originals draw them where a slit
# would be too big for the cloud.
def add_creases(rng, b, grey, n):
    for _ in range(n):
        for _ in range(40):
            p = rng.choice(sorted(b - grey))
            run = [p] if rng.random() < 0.6 else [p, (p[0] + 1, p[1])]
            if all(q in b and q not in grey for q in run) and all((q[0] + dx, q[1] + dy) in b for q in run for dx, dy in N8):
                grey.update(run)
                break


# Droplets fly up and to the sides, rarely down, a pixel or a pair one above the other, at least one empty pixel away from the
# body and from each other. A pair is white over grey; a single one is grey on the shaded side.
def add_drops(rng, b, n, centre):
    cx, cy, r = centre
    near_body = {(x + dx, y + dy) for x, y in b for dx, dy in N8}
    cands, weights = [], []
    for y in range(FRAME):
        for x in range(FRAME):
            p = (x, y)
            if not inside_frame(p) or p in near_body:
                continue
            d = min(max(abs(x - q[0]), abs(y - q[1])) for q in b)
            if d > 3:
                continue
            down = (y + 0.5 - cy) / (math.hypot(x + 0.5 - cx, y + 0.5 - cy) + 1e-6)
            cands.append(p)
            weights.append(0.05 if down > 0.3 else 0.3 + 1.5 * max(0.0, -down))
    drops, colours = set(), {}
    for _ in range(n):
        for _ in range(40):
            if not cands:
                return colours
            p = rng.choices(cands, weights)[0]
            pair = rng.random() < 0.3
            run = [p, (p[0], p[1] + 1)] if pair else [p]
            ok = all(inside_frame(q) and q not in near_body for q in run)
            ok = ok and not any((q[0] + dx, q[1] + dy) in drops or q in drops for q in run for dx, dy in N8)
            if ok:
                drops.update(run)
                if pair:
                    colours[run[0]], colours[run[1]] = WHITE, GREY
                else:
                    s = (-(p[0] - cx) + (p[1] - cy)) / r
                    colours[p] = GREY if s > 0.4 else WHITE
                break
    return colours


def frame(rng, size, base):
    lo, hi = size["count"]
    for _ in range(5000):
        cx = size["c"][0] + base["dc"][0] + rng.uniform(-0.4, 0.4)
        cy = size["c"][1] + base["dc"][1] + rng.uniform(-0.4, 0.4)
        k = rng.uniform(0.95, 1.05)
        phases = [(kk, ph + rng.gauss(0, 0.35)) for kk, ph in base["phases"]]
        b = body(cx, cy, size["r"][0] * k, size["r"][1] * k, size["taper"], phases)
        if not b or not all(inside_frame(p) for p in b) or not connected(b):
            continue
        b, slits = cut_slits(rng, b, rng.randint(*size["slits"]), size["slit_len"], size["depth"])
        if not connected(b):
            continue
        grey, centre = shade(b, slits)
        add_creases(rng, b, grey, rng.randint(*size["creases"]))
        drops = add_drops(rng, b, rng.randint(*size["drops"]), centre)
        if not lo <= len(b) + len(drops) <= hi:
            continue
        px = {p: (GREY if p in grey else WHITE) for p in b}
        px.update(drops)
        return px
    raise SystemExit(f"no frame of {lo}-{hi} pixels for size {size['r']}; loosen SIZES")


def strip(rng, size):
    base = dict(dc=(rng.uniform(-0.3, 0.3), rng.uniform(-0.3, 0.3)),
                phases=[(k, rng.uniform(0, 2 * math.pi)) for k in (3, 4, 5)])
    im = Image.new("RGBA", (FRAME, FRAME * FRAMES), CLEAR)
    for f in range(FRAMES):
        px = frame(rng, size, base)
        for (x, y), c in px.items():
            im.putpixel((x, f * FRAME + y), c)
    return im


def preview(path, strips):
    scale, pad = 8, 16
    bg = (59, 90, 140, 255)
    cell = FRAME * scale + pad
    blocks = []
    if os.path.isdir(ORIGINALS):
        blocks.append([Image.open(os.path.join(ORIGINALS, f"splash_cloud_{i + 1}.png")).convert("RGBA") for i in range(4)])
    blocks.append(strips)
    w = len(blocks) * (4 * cell + pad) + pad
    h = FRAMES * cell + pad
    out = Image.new("RGBA", (w, h), bg)
    for bi, block in enumerate(blocks):
        for si, s in enumerate(block):
            big = s.resize((FRAME * scale, FRAME * FRAMES * scale), Image.NEAREST)
            for f in range(FRAMES):
                tile = big.crop((0, f * FRAME * scale, FRAME * scale, (f + 1) * FRAME * scale))
                out.alpha_composite(tile, (pad + bi * (4 * cell + pad) + si * cell, pad + f * cell))
    out.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, default=20261011)
    ap.add_argument("--out", default=OUT)
    ap.add_argument("--preview")
    ap.add_argument("--dry", action="store_true")
    a = ap.parse_args()
    rng = random.Random(a.seed)
    strips = [strip(rng, s) for s in SIZES]
    for i, s in enumerate(strips):
        stats = []
        for f in range(FRAMES):
            px = [s.getpixel((x, f * FRAME + y)) for y in range(FRAME) for x in range(FRAME)]
            stats.append(f"{sum(p[3] > 0 for p in px)}/{sum(p == GREY for p in px)}")
        print(f"splash_cloud_{i + 1}: opaque/grey per frame {' '.join(stats)}")
        if not a.dry:
            s.save(os.path.join(a.out, f"splash_cloud_{i + 1}.png"))
    if a.preview:
        preview(a.preview, strips)


if __name__ == "__main__":
    main()
