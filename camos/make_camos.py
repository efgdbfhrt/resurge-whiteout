"""Builds the four Whiteout mastery camos (original art, procedurally generated).

For each tier this writes camos/out/<id>.wcam (static colour/gloss/normal/spec maps at 512 plus the
inputs the runtime animates the glow from at 256) and preview images (a sheet and an animated GIF).
The runtime (resurge_source/WhiteoutCamo.hpp) builds the glow frames with exactly the same recipe as
`glow_frame` below.

Run:  python make_camos.py        (needs numpy and pillow)
"""
import math, os, struct, sys
import numpy as np
from PIL import Image

SIZE = 512          # static maps
GLOW = 256          # animated glow maps
FRAMES = 60         # glow frames per loop
LOOP_MS = 4000      # loop length
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")

# ------------------------------------------------------------------ tileable noise helpers

def value_noise(n, period, rng):
    """Smooth tileable value noise in [0,1], n x n, with `period` lattice cells per side."""
    grid = rng.random((period, period))
    coords = np.arange(n) * period / n
    i0 = np.floor(coords).astype(int)
    f = coords - i0
    f = f * f * f * (f * (f * 6 - 15) + 10)
    i1 = (i0 + 1) % period
    a = grid[np.ix_(i0, i0)]; b = grid[np.ix_(i0, i1)]
    c = grid[np.ix_(i1, i0)]; d = grid[np.ix_(i1, i1)]
    fy = f[:, None]; fx = f[None, :]
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy

def fbm(n, base, octaves, rng, gain=0.5):
    total = np.zeros((n, n)); amp = 1.0; norm = 0.0
    for o in range(octaves):
        total += amp * value_noise(n, base * 2 ** o, rng); norm += amp; amp *= gain
    return total / norm

def warp(field, ux, uy, strength):
    """Domain warp a tileable field by tileable offset fields (in pixels)."""
    n = field.shape[0]
    y, x = np.mgrid[0:n, 0:n]
    xs = (x + ux * strength) % n; ys = (y + uy * strength) % n
    return bilinear(field, xs, ys)

def bilinear(field, xs, ys):
    n = field.shape[0]
    x0 = np.floor(xs).astype(int) % n; y0 = np.floor(ys).astype(int) % n
    x1 = (x0 + 1) % n; y1 = (y0 + 1) % n
    fx = xs - np.floor(xs); fy = ys - np.floor(ys)
    return (field[y0, x0] * (1 - fx) + field[y0, x1] * fx) * (1 - fy) + (field[y1, x0] * (1 - fx) + field[y1, x1] * fx) * fy

def voronoi(n, cells, rng, jitter=1.0):
    """Tileable Voronoi: returns F1, F2 distances (in cell units) and the nearest cell id."""
    pts = (np.stack(np.meshgrid(np.arange(cells), np.arange(cells)), -1).reshape(-1, 2) + 0.5
           + (rng.random((cells * cells, 2)) - 0.5) * jitter)
    y, x = np.mgrid[0:n, 0:n] * (cells / n)
    f1 = np.full((n, n), 1e9); f2 = np.full((n, n), 1e9); ids = np.zeros((n, n), int)
    for k, (px, py) in enumerate(pts):
        dx = np.abs(x - px); dx = np.minimum(dx, cells - dx)
        dy = np.abs(y - py); dy = np.minimum(dy, cells - dy)
        d = np.sqrt(dx * dx + dy * dy)
        closer = d < f1
        f2 = np.where(closer, f1, np.minimum(f2, d)); ids = np.where(closer, k, ids); f1 = np.where(closer, d, f1)
    return f1, f2, ids

def normal_from_height(h, strength):
    gx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * strength
    gy = (np.roll(h, -1, 0) - np.roll(h, 1, 0)) * strength
    nz = np.ones_like(h)
    length = np.sqrt(gx * gx + gy * gy + nz * nz)
    return np.stack([-gx / length, -gy / length], -1)  # x, y in [-1,1]

def resize(field, n):
    """Area-average a tileable field down to n x n."""
    k = field.shape[0] // n
    return field.reshape(n, k, n, k, *field.shape[2:]).mean(axis=(1, 3))

def ramp(stops, steps=256):
    """Palette from [(position 0..1, (r,g,b)), ...]."""
    pos = np.array([s[0] for s in stops]); cols = np.array([s[1] for s in stops], float)
    t = np.linspace(0, 1, steps)
    return np.stack([np.interp(t, pos, cols[:, c]) for c in range(3)], -1).clip(0, 255)

def u8(a):
    return np.clip(np.round(a * 255), 0, 255).astype(np.uint8)

# ------------------------------------------------------------------ the glow recipe (mirrored in C++)

def glow_frame(camo, f):
    """Emissive RGB (GLOW x GLOW, float 0..255) for frame f of FRAMES."""
    t = f / FRAMES
    n = GLOW
    y, x = np.mgrid[0:n, 0:n]
    p = camo["params"]
    s1x, s1y, s2x, s2y = p["scroll1"] + p["scroll2"]
    f1 = camo["f1"][(y + int(round(s1y * n * t))) % n, (x + int(round(s1x * n * t))) % n].astype(np.int32)
    f2 = camo["f2"][(y + int(round(s2y * n * t))) % n, (x + int(round(s2x * n * t))) % n].astype(np.int32)
    index = (f1 + f2 + int(round(p["cycles"] * 256 * t))) & 255
    pulse = 1.0 + p["pulse"] * math.sin(2 * math.pi * t * p["pulse_rate"])
    rgb = camo["palette"][index] * (camo["mask"][..., None] / 255.0) * p["glow"] * pulse
    if p["sweep"] > 0:
        u = ((x + y) / (2.0 * n) - t) % 1.0
        band = np.clip(1.0 - np.abs(u - 0.5) * p["sweep_width"], 0, 1) ** 2
        rgb = rgb + np.array(p["sweep_color"], float) * (band * p["sweep"] * camo["sweep_mask"] / 255.0)[..., None]
    if p["sparkle"] > 0:
        phase = camo["phase"] / 255.0
        tw = np.maximum(0, np.sin(2 * math.pi * (t * p["sparkle_rate"] + phase))) ** p["sparkle_power"]
        rgb = rgb + np.array(p["sparkle_color"], float) * (camo["sparkle"] / 255.0 * tw * p["sparkle"])[..., None]
    return np.clip(rgb, 0, 255)

PARAM_DEFAULTS = dict(scroll1=(1, 0), scroll2=(0, -1), cycles=1, glow=1.0, pulse=0.0, pulse_rate=1,
                      sweep=0.0, sweep_width=6.0, sweep_color=(255, 255, 255),
                      sparkle=0.0, sparkle_rate=2, sparkle_power=8, sparkle_color=(255, 255, 255), paint=0.9)

# ------------------------------------------------------------------ tier 1: Molten Forge (1,000 kills)

def molten(rng):
    n = SIZE
    f1, f2, ids = voronoi(n, 7, rng, 0.9)
    edge = f2 - f1
    rough = fbm(n, 8, 5, rng)
    wob = fbm(n, 4, 3, rng)
    crack = np.clip(1 - (edge + (wob - 0.5) * 0.08) / 0.07, 0, 1) ** 1.5           # thin glowing seams
    plate_shade = (ids * 0.6180339) % 1.0
    hammered = fbm(n, 32, 3, rng)
    base = 0.16 + 0.08 * plate_shade + 0.10 * (hammered - 0.5) + 0.06 * (rough - 0.5)
    heat = np.clip(1 - edge / 0.22, 0, 1) ** 2                                           # metal near seams is heat-tinted
    color = np.stack([base * 1.0 + heat * 0.30, base * 0.92 + heat * 0.10, base * 0.88], -1)
    color = np.where(crack[..., None] > 0.25, np.array([0.35, 0.10, 0.02]) * crack[..., None] + color * (1 - crack[..., None]), color)
    gloss = 0.55 + 0.25 * plate_shade - 0.35 * crack + 0.1 * (hammered - 0.5)
    spec = 0.30 + 0.10 * plate_shade - 0.25 * crack
    height = 1 - crack * 0.9 + (hammered - 0.5) * 0.15
    g = GLOW
    mask = resize(np.clip(crack * 1.2 + heat * 0.25, 0, 1), g)
    flow1 = resize(fbm(n, 4, 4, rng), g); flow2 = resize(fbm(n, 8, 3, rng), g)
    palette = ramp([(0, (40, 4, 0)), (0.35, (200, 40, 0)), (0.6, (255, 120, 10)), (0.8, (255, 200, 60)), (1, (60, 8, 0))])
    params = dict(PARAM_DEFAULTS, scroll1=(1, 1), scroll2=(-1, 0), cycles=1, glow=1.35, pulse=0.18, pulse_rate=2)
    return dict(name="Molten Forge", color=color, gloss=gloss, spec=spec, height=height, normal_strength=6,
                mask=mask, f1=flow1, f2=flow2, palette=palette, params=params)

# ------------------------------------------------------------------ tier 2: Glacier (10,000 kills)

def glacier(rng):
    n = SIZE
    f1, f2, ids = voronoi(n, 9, rng, 1.0)
    facet = (ids * 0.7548776) % 1.0
    edge = f2 - f1
    frost = fbm(n, 16, 5, rng)
    veins = np.abs(fbm(n, 5, 6, rng) - 0.5) * 2
    vein_line = np.clip(1 - veins / 0.025, 0, 1) ** 2
    depth = fbm(n, 3, 4, rng)
    shard = np.clip(1 - f1 / 0.55, 0, 1)                                     # brighter facet centres
    deep = np.array([0.02, 0.12, 0.32]); mid = np.array([0.10, 0.42, 0.78]); light = np.array([0.62, 0.88, 1.0])
    k = np.clip(0.25 + 0.45 * facet + 0.35 * (depth - 0.5) + 0.25 * shard, 0, 1)[..., None]
    base_ice = np.where(k < 0.5, deep + (mid - deep) * (k / 0.5), mid + (light - mid) * ((k - 0.5) / 0.5))
    rim = np.clip(1 - edge / 0.035, 0, 1)
    # Big glowing fractures through the ice (a second, coarser crack network), plus the fine veins.
    c1, c2, _ = voronoi(n, 5, rng, 1.0)
    wob = fbm(n, 6, 3, rng)
    fracture = np.clip(1 - ((c2 - c1) + (wob - 0.5) * 0.10) / 0.08, 0, 1) ** 1.4
    frost_spots = np.clip((frost - 0.74) * 4, 0, 1)
    base_ice = base_ice * 0.72                                                # deeper ice so the glow pops
    color = base_ice * (1 - 0.45 * rim[..., None]) + np.array([0.9, 0.97, 1.0]) * (vein_line * 0.45 + frost_spots * 0.30)[..., None]
    color = color * (1 - 0.5 * fracture[..., None]) + np.array([0.55, 0.9, 1.0]) * (0.35 * fracture)[..., None]
    gloss = 0.9 - 0.5 * frost_spots - 0.25 * rim - 0.2 * fracture
    spec = 0.22 + 0.08 * facet
    height = facet * 0.5 - rim * 0.6 + vein_line * 0.2 - fracture * 0.8
    g = GLOW
    halo = np.clip(1 - (c2 - c1) / 0.25, 0, 1) ** 2                           # soft light around fractures
    mask = resize(np.clip(fracture * 1.3 + halo * 0.35 + vein_line * 0.6 + rim * 0.35, 0, 1), g)
    flow1 = resize(fbm(n, 2, 4, rng), g); flow2 = resize(fbm(n, 4, 4, rng), g)
    palette = ramp([(0, (0, 40, 120)), (0.25, (0, 160, 255)), (0.45, (170, 250, 255)), (0.55, (255, 255, 255)),
                    (0.65, (90, 220, 255)), (0.85, (60, 90, 255)), (1, (0, 40, 120))])
    sparkle = resize(((rng.random((n, n)) > 0.996) * 1.0), g) * 4
    phase = resize(rng.random((n, n)), g)
    params = dict(PARAM_DEFAULTS, scroll1=(0, 1), scroll2=(1, 0), cycles=1, glow=1.9, pulse=0.18, pulse_rate=1,
                  sweep=0.6, sweep_width=5.0, sweep_color=(170, 235, 255),
                  sparkle=1.4, sparkle_rate=2, sparkle_power=10, sparkle_color=(230, 250, 255), paint=1.0)
    return dict(name="Glacier", color=color, gloss=gloss, spec=spec, height=height, normal_strength=5,
                mask=mask, f1=flow1, f2=flow2, palette=palette, params=params,
                sparkle=np.clip(sparkle, 0, 1), phase=phase, sweep_mask=resize(np.clip(fracture + halo * 0.5, 0, 1), g))

# ------------------------------------------------------------------ tier 3: Void Nebula (100,000 kills)

def nebula(rng):
    n = SIZE
    wx = fbm(n, 2, 4, rng) - 0.5; wy = fbm(n, 2, 4, rng) - 0.5
    clouds = warp(fbm(n, 3, 6, rng), wx, wy, n * 0.35)
    clouds2 = warp(fbm(n, 5, 6, rng), wy, wx, n * 0.25)
    dust = np.clip((fbm(n, 6, 5, rng) - 0.45) * 3, 0, 1)
    space = np.array([0.02, 0.01, 0.05])
    violet = np.array([0.45, 0.10, 0.75]); teal = np.array([0.0, 0.55, 0.65]); pink = np.array([0.95, 0.25, 0.65])
    c1 = np.clip((clouds - 0.42) * 2.4, 0, 1)[..., None]; c2 = np.clip((clouds2 - 0.5) * 2.6, 0, 1)[..., None]
    color = space + c1 * violet * 1.15 + c2 * teal * 1.0 + (c1 * c2) * pink * 1.1
    color = color * (1 - 0.6 * dust[..., None])
    stars = (rng.random((n, n)) > 0.996) * rng.random((n, n))
    color = np.clip(color + stars[..., None] * 1.5, 0, 1)
    gloss = 0.38 - 0.15 * dust + 0.25 * c1[..., 0]
    spec = 0.10 + color * 0.25
    height = clouds * 0.3 + clouds2 * 0.2
    g = GLOW
    mask = resize(np.clip(c1[..., 0] * 1.0 + c2[..., 0] * 0.8 + 0.08, 0, 1), g)
    flow1 = resize(clouds, g); flow2 = resize(clouds2, g)
    palette = ramp([(0, (20, 0, 60)), (0.2, (140, 30, 255)), (0.4, (255, 60, 180)), (0.55, (40, 220, 255)),
                    (0.75, (120, 40, 255)), (1, (20, 0, 60))])
    star_glow = resize((rng.random((n, n)) > 0.993) * 1.0, g) * 4
    phase = resize(rng.random((n, n)), g)
    params = dict(PARAM_DEFAULTS, scroll1=(1, 0), scroll2=(-1, 1), cycles=2, glow=2.2, pulse=0.1, pulse_rate=1,
                  sparkle=1.6, sparkle_rate=3, sparkle_power=6, sparkle_color=(255, 240, 255), paint=0.7)
    return dict(name="Void Nebula", color=color, gloss=gloss, spec=spec, height=height, normal_strength=2,
                mask=mask, f1=flow1, f2=flow2, palette=palette, params=params,
                sparkle=np.clip(star_glow, 0, 1), phase=phase)

# ------------------------------------------------------------------ tier 4: Prism (1,000,000 kills)

def prism(rng):
    n = SIZE
    # Hexagonal crystal facets with iridescent thin-film colour.
    y, x = np.mgrid[0:n, 0:n] / n
    cells = 8
    hx = x * cells; hy = y * cells * (2 / math.sqrt(3))
    f1, f2, ids = voronoi(n, 8, rng, 0.25)
    edge = f2 - f1
    facet = (ids * 0.38196601) % 1.0
    film = fbm(n, 2, 4, rng)
    hue = (film * 0.7 + (x + y) * 1.0 + facet * 0.06) % 1.0                 # oil-slick rainbow bands (whole cycles: tiles seamlessly)
    def hsv(h, s, v):
        i = np.floor(h * 6).astype(int) % 6; f = h * 6 - np.floor(h * 6)
        p_, q, t_ = v * (1 - s), v * (1 - f * s), v * (1 - (1 - f) * s)
        r = np.choose(i, [v, q, p_, p_, t_, v]); g_ = np.choose(i, [t_, v, v, q, p_, p_]); b = np.choose(i, [p_, p_, t_, v, v, q])
        return np.stack([r, g_, b], -1)
    chrome = fbm(n, 2, 3, rng)
    gem = np.clip(1 - f1 / 0.75, 0, 1)                                       # a pyramid on every facet
    gn = normal_from_height(gem, 40)
    light = np.array([-0.45, -0.55]); lz = math.sqrt(1 - (light ** 2).sum())
    nz = np.sqrt(np.clip(1 - (gn ** 2).sum(-1), 0, 1))
    sheen = np.clip(gn[..., 0] * light[0] + gn[..., 1] * light[1] + nz * lz, 0, 1) ** 3
    color = hsv(hue, 0.9, 0.10 + 0.22 * chrome + 0.6 * sheen)
    rim = np.clip(1 - edge / 0.04, 0, 1)
    color = color * (1 - 0.85 * rim[..., None])                              # dark seams; the glow lights them
    gloss = 0.96 - 0.3 * rim
    spec = np.clip(color * 0.75 + 0.06, 0, 1)
    height = facet * 0.6 - rim * 0.8
    g = GLOW
    mask = resize(np.clip(rim * 1.1 + 0.06 + 0.12 * film * sheen, 0, 1), g)
    flow1 = resize((hue * 255 % 256) / 255.0, g); flow2 = resize(fbm(n, 2, 3, rng), g)
    palette = ramp([(0, (255, 40, 40)), (1 / 6, (255, 200, 0)), (2 / 6, (60, 255, 80)), (3 / 6, (0, 220, 255)),
                    (4 / 6, (60, 80, 255)), (5 / 6, (230, 40, 255)), (1, (255, 40, 40))])
    sweep_mask = resize(np.clip(0.4 + rim, 0, 1), g)
    sparkle = resize(((rng.random((n, n)) > 0.995) * 1.0), g) * 4
    phase = resize(rng.random((n, n)), g)
    params = dict(PARAM_DEFAULTS, scroll1=(1, 0), scroll2=(0, 1), cycles=2, glow=1.25, pulse=0.12, pulse_rate=2,
                  sweep=1.0, sweep_width=7.0, sweep_color=(255, 255, 255),
                  sparkle=1.8, sparkle_rate=3, sparkle_power=8, sparkle_color=(255, 255, 255))
    return dict(name="Prism", color=color, gloss=gloss, spec=spec, height=height, normal_strength=7,
                mask=mask, f1=flow1, f2=flow2, palette=palette, params=params,
                sparkle=np.clip(sparkle, 0, 1), phase=phase, sweep_mask=sweep_mask)

# ------------------------------------------------------------------ output

TIERS = [("molten", molten, 11), ("glacier", glacier, 22), ("nebula", nebula, 33), ("prism", prism, 44)]

def finalize(c):
    g = GLOW
    for key in ("sparkle", "phase", "sweep_mask"):
        c.setdefault(key, np.zeros((g, g)))
    c["params"] = dict(PARAM_DEFAULTS, **c["params"])
    for key in ("mask", "f1", "f2", "sparkle", "phase", "sweep_mask"):
        c[key] = u8(np.clip(c[key], 0, 1))
    c["normal"] = normal_from_height(c["height"], c["normal_strength"])
    return c

def write_wcam(path, c):
    p = c["params"]
    header = struct.pack("<4sIIIII", b"WCAM", 3, SIZE, GLOW, FRAMES, LOOP_MS)
    params = struct.pack("<4i f f f i f f 3f f i f 3f f",
                         *p["scroll1"], *p["scroll2"], p["cycles"], p["glow"], p["pulse"], p["pulse_rate"],
                         p["sweep"], p["sweep_width"], *p["sweep_color"], p["sparkle"], p["sparkle_rate"],
                         p["sparkle_power"], *p["sparkle_color"], p["paint"])
    color = u8(np.clip(c["color"], 0, 1))                                    # SIZE x SIZE x 3 (sRGB)
    gloss = u8(np.clip(c["gloss"], 0, 1))
    spec = c["spec"] if c["spec"].ndim == 3 else np.repeat(c["spec"][..., None], 3, -1)
    spec = u8(np.clip(spec, 0, 1))                                          # SIZE x SIZE x 3 (specular colour)
    normal = u8(c["normal"] * 0.5 + 0.5)                                     # SIZE x SIZE x 2
    palette = np.clip(np.round(c["palette"]), 0, 255).astype(np.uint8)       # 256 x 3
    with open(path, "wb") as out:
        out.write(header); out.write(params)
        for block in (color, gloss, spec, normal, c["mask"], c["f1"], c["f2"], c["phase"], c["sparkle"], c["sweep_mask"], palette):
            out.write(np.ascontiguousarray(block).tobytes())

def preview(c, key):
    color = np.clip(c["color"], 0, 1) * 255
    frames = []
    for f in range(0, FRAMES, 2):
        glow = glow_frame(c, f)
        glow_big = np.kron(glow, np.ones((SIZE // GLOW, SIZE // GLOW, 1)))
        lit = np.clip(color + glow_big * c["params"]["paint"], 0, 255).astype(np.uint8)   # the runtime's colour frame
        frames.append(Image.fromarray(lit).resize((256, 256), Image.LANCZOS))
    frames[0].save(os.path.join(OUT, f"{key}.gif"), save_all=True, append_images=frames[1:],
                   duration=LOOP_MS * 2 // FRAMES, loop=0)
    # Launcher card: 12 small frames (one loop) shown as a flipbook on the Unlocks page.
    cards = os.path.join(OUT, "cards")
    os.makedirs(cards, exist_ok=True)
    for i in range(12):
        frames[i * len(frames) // 12].resize((150, 150), Image.LANCZOS).save(os.path.join(cards, f"{key}_{i:02d}.png"), optimize=True)
    return frames

def main():
    os.makedirs(OUT, exist_ok=True)
    sheet = Image.new("RGB", (256 * 4, 256 * 3), (10, 12, 16))
    for column, (key, build, seed) in enumerate(TIERS):
        camo = finalize(build(np.random.default_rng(seed)))
        write_wcam(os.path.join(OUT, f"{key}.wcam"), camo)
        frames = preview(camo, key)
        for row, idx in enumerate((0, len(frames) // 3, 2 * len(frames) // 3)):
            sheet.paste(frames[idx], (column * 256, row * 256))
        print(f"{camo['name']}: written")
    sheet.save(os.path.join(OUT, "sheet.png"))

if __name__ == "__main__":
    main()
