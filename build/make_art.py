"""Generates icon.png, banner.png and the Workshop image.png for each mod.

    python build/make_art.py

Needs Pillow and numpy. Writes into mods/<Name>/art/ and mods/<Name>/workshop/.
Everything is drawn procedurally at 2x and downsampled, in a dark fantasy
palette (ember glow, gold filigree, vignette) to match the game's look.
"""
import glob
import math
import os
import random

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "mods")
SS = 2  # supersampling factor


def _font(names, size):
    for pat in names:
        hits = glob.glob(f"/usr/share/fonts/**/{pat}", recursive=True) + glob.glob(f"C:/Windows/Fonts/{pat}")
        if hits:
            return ImageFont.truetype(hits[0], size)
    return ImageFont.load_default()


def serif(size):
    return _font(["Cinzel*Bold*.ttf", "trajanbd.ttf", "LiberationSerif-Bold.ttf", "DejaVuSerif-Bold.ttf", "georgiab.ttf"], size)


# ---------------------------------------------------------------- primitives
def lerp(a, b, t):
    return tuple(int(a[i] * (1 - t) + b[i] * t) for i in range(len(a)))


def new_canvas(w, h):
    return Image.new("RGBA", (w * SS, h * SS), (0, 0, 0, 255))


def to_np(im):
    return np.asarray(im.convert("RGB"), dtype=np.float32)


def from_np(a):
    return Image.fromarray(np.clip(a, 0, 255).astype(np.uint8), "RGB").convert("RGBA")


def glow(im, x, y, r, col, k=1.0, sy=1.0):
    """Additive soft light, gaussian falloff; sy squashes it vertically."""
    w, h = im.size
    a = to_np(im)
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float32)
    d2 = ((xs - x) / r) ** 2 + ((ys - y) / (r * sy)) ** 2
    f = np.exp(-d2 * 1.6) * k
    a += f[..., None] * np.array(col, dtype=np.float32)
    return from_np(a)


def background(w, h, top, bottom, light, seed):
    random.seed(seed)
    np.random.seed(seed)
    W, H = w * SS, h * SS
    ys = np.linspace(0, 1, H, dtype=np.float32)[:, None, None]
    a = np.array(top, np.float32) * (1 - ys) + np.array(bottom, np.float32) * ys
    a = np.repeat(a, W, axis=1)
    im = from_np(a)
    # rolling fog bands
    fog = Image.new("L", (W // 8, H // 8))
    fa = np.random.rand(H // 8, W // 8).astype(np.float32)
    fog = Image.fromarray((fa * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(6)).resize((W, H), Image.BICUBIC)
    fa = np.asarray(fog, np.float32) / 255.0
    fa = (fa - fa.mean()) * 3.2
    arr = to_np(im) + fa[..., None] * np.array(light, np.float32) * 0.55
    im = from_np(arr)
    return im


def vignette(im, strength=0.75):
    w, h = im.size
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float32)
    d = np.sqrt(((xs - w / 2) / (w / 2)) ** 2 + ((ys - h / 2) / (h / 2)) ** 2)
    f = 1 - strength * np.clip(d - 0.35, 0, 1) ** 1.6
    return from_np(to_np(im) * f[..., None])


def grain(im, amt=4.0):
    a = to_np(im)
    n = np.random.randn(a.shape[0], a.shape[1], 1).astype(np.float32) * amt
    return from_np(a + n)


def embers(im, n, colors, area, seed, rmax=3.0):
    random.seed(seed)
    w, h = im.size
    layer = Image.new("RGBA", im.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    x0, y0, x1, y1 = area
    for _ in range(n):
        x = random.uniform(x0, x1) * w
        y = random.uniform(y0, y1) * h
        r = random.uniform(0.6, rmax) * SS
        c = random.choice(colors)
        a = random.randint(110, 255)
        d.ellipse([x - r, y - r, x + r, y + r], fill=c + (a,))
    halo = layer.filter(ImageFilter.GaussianBlur(3 * SS))
    out = Image.alpha_composite(im, halo)
    out = Image.alpha_composite(out, halo)
    return Image.alpha_composite(out, layer)


def gold_grad(h, hi=(255, 236, 160), mid=(240, 180, 60), lo=(150, 80, 20)):
    ys = np.linspace(0, 1, h, dtype=np.float32)
    cols = np.zeros((h, 3), np.float32)
    for i, t in enumerate(ys):
        cols[i] = lerp(hi, mid, t / 0.45) if t < 0.45 else lerp(mid, lo, (t - 0.45) / 0.55)
    return cols


def shape_gradient(mask, hi, mid, lo):
    """Fill the L mask with a vertical gold gradient -> RGBA."""
    w, h = mask.size
    cols = gold_grad(h, hi, mid, lo)
    arr = np.repeat(cols[:, None, :], w, axis=1)
    img = from_np(arr)
    img.putalpha(mask)
    return img


def composite(im, layer, xy=(0, 0)):
    out = im.copy()
    out.alpha_composite(layer, xy)
    return out


def drop_shadow(im, layer, blur=8, off=(0, 6), a=0.8):
    sh = Image.new("RGBA", layer.size, (0, 0, 0, 0))
    alpha = layer.getchannel("A").point(lambda v: int(v * a))
    sh.putalpha(alpha)
    sh = sh.filter(ImageFilter.GaussianBlur(blur * SS))
    out = im.copy()
    out.alpha_composite(sh, (off[0] * SS, off[1] * SS))
    return out


# ---------------------------------------------------------------- text / frame
def spaced(draw, xy, text, font, fill, spacing, anchor_center=True):
    widths = [draw.textlength(c, font=font) for c in text]
    total = sum(widths) + spacing * (len(text) - 1)
    x, y = xy
    if anchor_center:
        x -= total / 2
    for c, wd in zip(text, widths):
        draw.text((x, y), c, font=font, fill=fill)
        x += wd + spacing
    return total


def title_text(im, text, cx, y, size, spacing, tint=((255, 240, 180), (245, 185, 65), (140, 70, 20)), left=False):
    W, H = im.size
    f = serif(size * SS)
    probe = ImageDraw.Draw(im)
    widths = [probe.textlength(c, font=f) for c in text]
    total = sum(widths) + spacing * SS * (len(text) - 1)
    x0 = cx * SS if left else cx * SS - total / 2
    y = y * SS
    mask = Image.new("L", im.size, 0)
    md = ImageDraw.Draw(mask)
    x = x0
    for c, wd in zip(text, widths):
        md.text((x, y), c, font=f, fill=255)
        x += wd + spacing * SS
    outline = mask.filter(ImageFilter.MaxFilter(2 * (SS * 3) + 1))
    glowm = mask.filter(ImageFilter.GaussianBlur(14 * SS))
    out = im
    # soft halo in the title colour
    halo = Image.new("RGBA", im.size, tint[1] + (0,))
    halo.putalpha(glowm.point(lambda v: int(v * 0.55)))
    out = Image.alpha_composite(out, halo)
    # dark outline + drop shadow
    shadow = Image.new("RGBA", im.size, (0, 0, 0, 0))
    shadow.putalpha(outline.filter(ImageFilter.GaussianBlur(4 * SS)).point(lambda v: int(v * 0.95)))
    out = Image.alpha_composite(out, shadow)
    ol = Image.new("RGBA", im.size, (28, 12, 8, 255))
    ol.putalpha(outline)
    out = Image.alpha_composite(out, ol)
    # gold fill with vertical gradient confined to the glyph band
    bbox = mask.getbbox() or (0, 0, W, H)
    band_h = bbox[3] - bbox[1]
    cols = gold_grad(band_h, *tint)
    arr = np.zeros((H, W, 3), np.float32)
    arr[bbox[1]:bbox[3]] = cols[:, None, :]
    fill = from_np(arr)
    fill.putalpha(mask)
    out = Image.alpha_composite(out, fill)
    # thin highlight on the top edge of the glyphs
    top = ImageChops.subtract(mask, ImageChops.offset(mask, 0, 2 * SS))
    hl = Image.new("RGBA", im.size, (255, 250, 220, 0))
    hl.putalpha(top.point(lambda v: int(v * 0.8)))
    out = Image.alpha_composite(out, hl)
    return out, bbox


def subtitle(im, text, cx, y, size, col=(224, 214, 196), spacing=6, rule=True, left=False):
    f = _font(["LiberationSerif-Regular.ttf", "DejaVuSerif.ttf", "georgia.ttf"], size * SS)
    layer = Image.new("RGBA", im.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    widths = [d.textlength(c, font=f) for c in text]
    total = sum(widths) + spacing * SS * (len(text) - 1)
    x = cx * SS if left else cx * SS - total / 2
    yy = y * SS
    x_start = x
    for c, wd in zip(text, widths):
        d.text((x + 2, yy + 3), c, font=f, fill=(0, 0, 0, 255))
        d.text((x, yy), c, font=f, fill=col + (255,))
        x += wd + spacing * SS
    if rule:
        my = yy + size * SS * 0.62
        gap = 18 * SS
        for sgn, ex in ((-1, x_start - gap), (1, x - spacing * SS + gap)):
            for i in range(60 * SS):
                t = i / (60 * SS)
                a = int(220 * (1 - t))
                xx = ex + sgn * i
                d.line([(xx, my), (xx, my + SS)], fill=(240, 190, 90, a))
        for ex in (x_start - gap, x - spacing * SS + gap):
            r = 3 * SS
            d.polygon([(ex, my - r), (ex + r, my + SS / 2), (ex, my + r + SS), (ex - r, my + SS / 2)], fill=(250, 205, 100, 255))
    layer = layer.filter(ImageFilter.GaussianBlur(0.4))
    return Image.alpha_composite(im, layer)


def frame(im, inset=14, col=(214, 160, 70), corner=22):
    W, H = im.size
    layer = Image.new("RGBA", im.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    i = inset * SS
    for k, (o, a, wd) in enumerate(((0, 230, 3), (7, 120, 1))):
        o *= SS
        d.rectangle([i + o, i + o, W - i - o, H - i - o], outline=col + (a,), width=wd * SS)
    c = corner * SS
    for (x, y, sx, sy) in ((i, i, 1, 1), (W - i, i, -1, 1), (i, H - i, 1, -1), (W - i, H - i, -1, -1)):
        d.polygon([(x, y + sy * c), (x + sx * c * 0.35, y + sy * c * 0.35), (x + sx * c, y), (x + sx * c * 0.35, y - sy * c * 0.0)],
                  fill=col + (255,))
        d.ellipse([x - 5 * SS, y - 5 * SS, x + 5 * SS, y + 5 * SS], fill=(255, 224, 140, 255), outline=(90, 50, 10, 255), width=SS)
    glow_l = layer.filter(ImageFilter.GaussianBlur(3 * SS))
    out = Image.alpha_composite(im, glow_l)
    return Image.alpha_composite(out, layer)


def ring(layer_draw, cx, cy, r, width, fill):
    layer_draw.ellipse([cx - r, cy - r, cx + r, cy + r], outline=fill, width=max(1, int(width)))


def star(d, cx, cy, r, col, points=4, inner=0.28, rot=0):
    pts = []
    for i in range(points * 2):
        ang = rot + i * math.pi / points - math.pi / 2
        rr = r if i % 2 == 0 else r * inner
        pts.append((cx + math.cos(ang) * rr, cy + math.sin(ang) * rr))
    d.polygon(pts, fill=col)


def sparkles(im, pts, col=(255, 240, 200)):
    layer = Image.new("RGBA", im.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    for (x, y, r) in pts:
        star(d, x, y, r, col + (255,), 4, 0.12)
    h = layer.filter(ImageFilter.GaussianBlur(4 * SS))
    return Image.alpha_composite(Image.alpha_composite(im, h), layer)


def metal_ring(im, cx, cy, r, width, hi=(255, 226, 140), mid=(212, 150, 50), lo=(96, 52, 16)):
    """A bevelled gold ring: dark base, bright band, thin specular line."""
    layer = Image.new("RGBA", im.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    ring(d, cx, cy, r, width, lo + (255,))
    ring(d, cx, cy, r - width * 0.12, int(width * 0.7), mid + (255,))
    ring(d, cx - width * 0.1, cy - width * 0.1, r - width * 0.2, max(1, int(width * 0.3)), hi + (255,))
    im = drop_shadow(im, layer, 10, (0, 8))
    return Image.alpha_composite(im, layer)


def rounded_grad(size, radius, top, bottom):
    w, h = size
    cols = np.array([lerp(top, bottom, y / h) for y in range(h)], np.float32)
    arr = np.repeat(cols[:, None, :], w, axis=1)
    img = from_np(arr)
    m = Image.new("L", size, 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, w - 1, h - 1], radius, fill=255)
    img.putalpha(m)
    return img


# ---------------------------------------------------------------- cards
def make_card(cw, ch, frame_col, glow_col, art_col, cost=None, seal=False, dim=False, glyph="diamond"):
    """A single card face on a transparent RGBA layer, drawn at SS scale."""
    pad = int(cw * 0.25)
    W, H = cw + pad * 2, ch + pad * 2
    layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    # outer glow
    gl = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(gl).rounded_rectangle([pad, pad, pad + cw, pad + ch], int(cw * 0.08), fill=glow_col + (255,))
    gl = gl.filter(ImageFilter.GaussianBlur(cw * 0.12))
    gl.putalpha(gl.getchannel("A").point(lambda v: int(v * (0.35 if dim else 0.8))))
    layer.alpha_composite(gl)
    body = rounded_grad((cw, ch), int(cw * 0.08), (58, 46, 52), (22, 16, 24))
    layer.alpha_composite(body, (pad, pad))
    d = ImageDraw.Draw(layer)
    bw = max(2, int(cw * 0.035))
    d.rounded_rectangle([pad, pad, pad + cw, pad + ch], int(cw * 0.08), outline=frame_col + (255,), width=bw)
    d.rounded_rectangle([pad + bw * 2, pad + bw * 2, pad + cw - bw * 2, pad + ch - bw * 2], int(cw * 0.06),
                        outline=lerp(frame_col, (0, 0, 0), 0.55) + (255,), width=max(1, bw // 2))
    # art window
    ax0, ay0, ax1, ay1 = pad + cw * 0.12, pad + ch * 0.13, pad + cw * 0.88, pad + ch * 0.55
    win = Image.new("RGBA", (int(ax1 - ax0), int(ay1 - ay0)), (0, 0, 0, 255))
    ww, wh = win.size
    ys, xs = np.mgrid[0:wh, 0:ww].astype(np.float32)
    dd = np.sqrt(((xs - ww / 2) / (ww * 0.6)) ** 2 + ((ys - wh / 2) / (wh * 0.7)) ** 2)
    f = np.clip(1 - dd, 0, 1) ** 1.3
    arr = f[..., None] * np.array(art_col, np.float32) + (1 - f[..., None]) * np.array((12, 10, 20), np.float32)
    win = from_np(arr)
    wd_ = ImageDraw.Draw(win)
    cx, cy = ww / 2, wh / 2
    s = min(ww, wh) * 0.34
    if glyph == "diamond":
        wd_.polygon([(cx, cy - s), (cx + s * 0.75, cy), (cx, cy + s), (cx - s * 0.75, cy)], fill=(255, 255, 255, 235))
        wd_.polygon([(cx, cy - s), (cx + s * 0.75, cy), (cx, cy)], fill=(255, 255, 255, 120))
    elif glyph == "star":
        star(wd_, cx, cy, s * 1.1, (255, 250, 230, 240), 8, 0.35)
    else:
        wd_.ellipse([cx - s * 0.8, cy - s * 0.8, cx + s * 0.8, cy + s * 0.8], fill=(255, 255, 255, 235))
    mk = Image.new("L", win.size, 0)
    ImageDraw.Draw(mk).rounded_rectangle([0, 0, ww - 1, wh - 1], int(cw * 0.04), fill=255)
    win.putalpha(mk)
    layer.alpha_composite(win, (int(ax0), int(ay0)))
    d.rounded_rectangle([ax0, ay0, ax1, ay1], int(cw * 0.04), outline=frame_col + (255,), width=max(1, bw // 2 + 1))
    # name plate + text rows
    ny = pad + ch * 0.575
    d.rounded_rectangle([pad + cw * 0.1, ny, pad + cw * 0.9, ny + ch * 0.08], int(cw * 0.03), fill=lerp(frame_col, (0, 0, 0), 0.35) + (255,))
    for i, wf in enumerate((0.72, 0.6, 0.66)):
        y = pad + ch * (0.7 + i * 0.065)
        d.rounded_rectangle([pad + cw * (0.5 - wf / 2), y, pad + cw * (0.5 + wf / 2), y + ch * 0.022], int(cw * 0.01), fill=(150, 140, 150, 150 if not dim else 90))
    if cost is not None:
        r = cw * 0.15
        ox, oy = pad + cw * 0.08, pad + cw * 0.06
        d.ellipse([ox - r * 0.2, oy - r * 0.2, ox + r * 1.8, oy + r * 1.8], fill=(14, 26, 60, 255), outline=(120, 200, 255, 255), width=bw)
        fnt = serif(int(r * 1.55))
        d.text((ox + r * 0.8, oy + r * 0.78), str(cost), font=fnt, fill=(255, 255, 255, 255 if not dim else 150), anchor="mm")
    if seal:
        r = cw * 0.1
        cxs, cys = pad + cw / 2, pad + ch * 0.93
        d.polygon([(cxs, cys - r), (cxs + r, cys), (cxs, cys + r), (cxs - r, cys)], fill=glow_col + (255,), outline=(255, 255, 255, 255))
    return layer, pad


def place(im, layer, cx, cy, angle=0.0, shadow=True):
    lay = layer.rotate(angle, expand=True, resample=Image.BICUBIC)
    xy = (int(cx - lay.width / 2), int(cy - lay.height / 2))
    base = im
    if shadow:
        sh = Image.new("RGBA", lay.size, (0, 0, 0, 0))
        sh.putalpha(lay.getchannel("A").point(lambda v: int(v * 0.0)))
    base = base.copy()
    base.alpha_composite(lay, xy)
    return base


# ---------------------------------------------------------------- emblems
def emblem_seed(im, cx, cy, R):
    """A lens over a branching map; the lens is the search."""
    cx, cy, R = cx * SS, cy * SS, R * SS
    im = glow(im, cx, cy, R * 1.5, (20, 120, 150), 0.9)
    im = glow(im, cx, cy, R * 0.9, (120, 220, 255), 0.35)
    lens_r = R * 0.72
    lx, ly = cx - R * 0.12, cy - R * 0.1
    # handle
    layer = Image.new("RGBA", im.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    ang = math.radians(42)
    hx0, hy0 = lx + math.cos(ang) * lens_r * 1.0, ly + math.sin(ang) * lens_r * 1.0
    hx1, hy1 = lx + math.cos(ang) * (lens_r + R * 0.78), ly + math.sin(ang) * (lens_r + R * 0.78)
    d.line([(hx0, hy0), (hx1, hy1)], fill=(88, 46, 18, 255), width=int(R * 0.2))
    d.line([(hx0, hy0), (hx1, hy1)], fill=(196, 130, 52, 255), width=int(R * 0.14))
    d.line([(hx0 - 3 * SS, hy0 - 3 * SS), (hx1 - 3 * SS, hy1 - 3 * SS)], fill=(255, 214, 130, 255), width=int(R * 0.035))
    for t in (0.35, 0.55, 0.75):
        px, py = hx0 + (hx1 - hx0) * t, hy0 + (hy1 - hy0) * t
        d.ellipse([px - R * 0.1, py - R * 0.1, px + R * 0.1, py + R * 0.1], outline=(70, 36, 14, 255), width=int(R * 0.03))
    d.ellipse([hx1 - R * 0.13, hy1 - R * 0.13, hx1 + R * 0.13, hy1 + R * 0.13], fill=(88, 220, 240, 255), outline=(255, 226, 140, 255), width=int(R * 0.035))
    im = drop_shadow(im, layer, 10, (4, 10))
    im = Image.alpha_composite(im, layer)
    # glass: dark teal with inner glow
    glass = Image.new("RGBA", im.size, (0, 0, 0, 0))
    gd = ImageDraw.Draw(glass)
    gd.ellipse([lx - lens_r, ly - lens_r, lx + lens_r, ly + lens_r], fill=(10, 24, 44, 255))
    im = Image.alpha_composite(im, glass)
    inner = Image.new("RGBA", im.size, (0, 0, 0, 0))
    inner = glow(inner.convert("RGB").convert("RGBA"), lx, ly, lens_r * 0.9, (30, 120, 150), 1.0)
    mask = Image.new("L", im.size, 0)
    ImageDraw.Draw(mask).ellipse([lx - lens_r, ly - lens_r, lx + lens_r, ly + lens_r], fill=255)
    inner.putalpha(mask)
    im = Image.alpha_composite(im, inner)
    # map graph inside the lens: layers of nodes joined by paths (Spire map)
    random.seed(11)
    rows = 6
    nodes = []
    top, bot = ly - lens_r * 0.7, ly + lens_r * 0.72
    for r_i in range(rows):
        n = [2, 3, 3, 4, 3, 2][r_i]
        y = bot - (bot - top) * r_i / (rows - 1)
        xs = [lx + (i - (n - 1) / 2) * lens_r * 0.52 + random.uniform(-0.07, 0.07) * lens_r for i in range(n)]
        nodes.append([(x, y + random.uniform(-0.03, 0.03) * lens_r) for x in xs])
    kinds = ["m", "m", "e", "r", "s", "m", "e", "u", "m", "r", "u", "m", "s", "e", "m", "m", "u", "r", "m"]
    colmap = {"m": (200, 210, 225), "e": (240, 70, 60), "r": (90, 220, 110), "s": (255, 205, 80), "u": (150, 130, 255)}
    path = Image.new("RGBA", im.size, (0, 0, 0, 0))
    pd = ImageDraw.Draw(path)
    for r_i in range(rows - 1):
        for a in nodes[r_i]:
            tgt = sorted(nodes[r_i + 1], key=lambda b: abs(b[0] - a[0]))[:random.choice([1, 2])]
            for b in tgt:
                pd.line([a, b], fill=(120, 200, 230, 200), width=int(R * 0.025))
    # the highlighted route
    route = [nodes[0][0], nodes[1][0], nodes[2][1], nodes[3][1], nodes[4][2 if len(nodes[4]) > 2 else 1], nodes[5][-1]]
    pd.line(route, fill=(255, 224, 120, 255), width=int(R * 0.05), joint="curve")
    pg = path.filter(ImageFilter.GaussianBlur(5 * SS))
    cl = Image.new("L", im.size, 0)
    ImageDraw.Draw(cl).ellipse([lx - lens_r * 0.97, ly - lens_r * 0.97, lx + lens_r * 0.97, ly + lens_r * 0.97], fill=255)
    nodelayer = Image.new("RGBA", im.size, (0, 0, 0, 0))
    nd = ImageDraw.Draw(nodelayer)
    k = 0
    for row in nodes:
        for (x, y) in row:
            c = colmap[kinds[k % len(kinds)]]
            k += 1
            nr = R * 0.075
            nd.ellipse([x - nr, y - nr, x + nr, y + nr], fill=c + (255,), outline=(255, 255, 255, 235), width=max(1, int(R * 0.016)))
    stack = Image.alpha_composite(Image.alpha_composite(pg, path), nodelayer)
    stack.putalpha(ImageChops.multiply(stack.getchannel("A"), cl))
    im = Image.alpha_composite(im, stack)
    # glass reflections
    refl = Image.new("RGBA", im.size, (0, 0, 0, 0))
    rd = ImageDraw.Draw(refl)
    rd.arc([lx - lens_r * 0.86, ly - lens_r * 0.86, lx + lens_r * 0.86, ly + lens_r * 0.86], 200, 262, fill=(255, 255, 255, 190), width=int(R * 0.04))
    im = Image.alpha_composite(im, refl.filter(ImageFilter.GaussianBlur(1 * SS)))
    im = metal_ring(im, lx, ly, lens_r, R * 0.1)
    # gold seed sprouting above-left (the "seed")
    sp = Image.new("RGBA", im.size, (0, 0, 0, 0))
    sd = ImageDraw.Draw(sp)
    for i, (dx, dy) in enumerate(((-0.95, -0.75), (-0.05, -1.08), (0.85, -0.85))):
        x, y = cx + dx * R, cy + dy * R
        r = R * (0.07 + 0.02 * (i % 2))
        star(sd, x, y, r * 2.6, (255, 232, 160, 255), 4, 0.13)
    im = Image.alpha_composite(Image.alpha_composite(im, sp.filter(ImageFilter.GaussianBlur(5 * SS))), sp)
    # floating seed digits orbiting
    f = serif(int(R * 0.22))
    dig = Image.new("RGBA", im.size, (0, 0, 0, 0))
    dg = ImageDraw.Draw(dig)
    random.seed(4)
    for (dx, dy, ch) in ((-1.15, 0.35, "7"), (-1.0, -0.2, "A"), (1.12, -0.38, "3"), (1.05, 0.05, "F"), (-0.7, 1.0, "9"), (0.25, 1.15, "K")):
        dg.text((cx + dx * R, cy + dy * R), ch, font=f, fill=(130, 230, 250, 110), anchor="mm")
    im = Image.alpha_composite(im, dig.filter(ImageFilter.GaussianBlur(0.8 * SS)))
    return im


def emblem_balance(im, cx, cy, R):
    """Scales of judgement: a 2-cost card on one pan, a 1-cost on the other."""
    cx, cy, R = cx * SS, cy * SS, R * SS
    im = glow(im, cx, cy, R * 1.5, (160, 40, 24), 1.0)
    im = glow(im, cx, cy - R * 0.2, R * 0.8, (255, 170, 70), 0.35)
    tilt = math.radians(-9)  # left side heavy, right (cheaper) side rises
    px, py = cx, cy - R * 0.85  # pivot
    arm = R * 1.12
    lp = (px - math.cos(tilt) * arm, py - math.sin(tilt) * arm * -1)
    rp = (px + math.cos(tilt) * arm, py + math.sin(tilt) * arm * -1)
    lp = (px - arm * math.cos(tilt), py + arm * math.sin(-tilt))
    rp = (px + arm * math.cos(tilt), py - arm * math.sin(-tilt))
    layer = Image.new("RGBA", im.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    # base + post
    d.polygon([(cx - R * 0.55, cy + R * 1.05), (cx + R * 0.55, cy + R * 1.05), (cx + R * 0.38, cy + R * 0.86), (cx - R * 0.38, cy + R * 0.86)], fill=(120, 70, 24, 255), outline=(255, 210, 120, 255))
    d.rectangle([cx - R * 0.07, py, cx + R * 0.07, cy + R * 0.88], fill=(170, 110, 40, 255), outline=(255, 215, 130, 255), width=SS)
    d.line([(cx - R * 0.07, py), (cx - R * 0.07, cy + R * 0.88)], fill=(255, 225, 150, 255), width=int(R * 0.025))
    # beam
    d.line([lp, rp], fill=(100, 56, 18, 255), width=int(R * 0.12))
    d.line([lp, rp], fill=(226, 160, 56, 255), width=int(R * 0.085))
    d.line([(lp[0], lp[1] - R * 0.03), (rp[0], rp[1] - R * 0.03)], fill=(255, 232, 160, 255), width=int(R * 0.02))
    # chains + pans
    pans = []
    for (ex, ey) in (lp, rp):
        py2 = ey + R * 1.0
        d.line([(ex, ey), (ex - R * 0.42, py2)], fill=(210, 170, 90, 255), width=int(R * 0.022))
        d.line([(ex, ey), (ex + R * 0.42, py2)], fill=(210, 170, 90, 255), width=int(R * 0.022))
        d.pieslice([ex - R * 0.5, py2 - R * 0.28, ex + R * 0.5, py2 + R * 0.28], 0, 180, fill=(150, 92, 30, 255), outline=(255, 214, 130, 255), width=int(R * 0.02))
        d.line([(ex - R * 0.5, py2), (ex + R * 0.5, py2)], fill=(255, 226, 150, 255), width=int(R * 0.025))
        pans.append((ex, py2))
    d.ellipse([px - R * 0.15, py - R * 0.15, px + R * 0.15, py + R * 0.15], fill=(255, 226, 140, 255), outline=(110, 60, 14, 255), width=int(R * 0.03))
    d.ellipse([px - R * 0.06, py - R * 0.06, px + R * 0.06, py + R * 0.06], fill=(255, 70, 50, 255))
    im = drop_shadow(im, layer, 10, (0, 10))
    # cards are drawn first so the pan sits in front of their lower edge
    cw = int(R * 0.5)
    ch = int(cw * 1.4)
    (lx, ly), (rx, ry) = pans
    c1, pad = make_card(cw, ch, (150, 150, 165), (120, 120, 140), (120, 70, 60), cost=2, dim=True)
    c2, _ = make_card(cw, ch, (255, 205, 90), (255, 160, 40), (240, 90, 50), cost=1, seal=True, glyph="star")
    im = Image.alpha_composite(im, Image.new("RGBA", im.size, (0, 0, 0, 0)))
    im = place(im, c1, lx, ly - ch * 0.5 + R * 0.04, 4)
    im = place(im, c2, rx, ry - ch * 0.5 + R * 0.04, -4)
    im = Image.alpha_composite(im, layer)
    # re-draw pans' front lip over the cards
    front = Image.new("RGBA", im.size, (0, 0, 0, 0))
    fd = ImageDraw.Draw(front)
    for (ex, ey) in pans:
        fd.pieslice([ex - R * 0.5, ey - R * 0.28, ex + R * 0.5, ey + R * 0.28], 0, 180, fill=(150, 92, 30, 255), outline=(255, 214, 130, 255), width=int(R * 0.02))
        fd.line([(ex - R * 0.5, ey), (ex + R * 0.5, ey)], fill=(255, 226, 150, 255), width=int(R * 0.03))
    im = Image.alpha_composite(im, front)
    im = sparkles(im, [(rx + R * 0.45, ry - ch * 0.95, R * 0.12), (px + R * 0.28, py - R * 0.38, R * 0.08), (rx - R * 0.55, ry - ch * 0.55, R * 0.06)])
    return im


def emblem_rarity(im, cx, cy, R):
    """Three rarities fanned out inside a swirl of shuffle arrows."""
    cx, cy, R = cx * SS, cy * SS, R * SS
    im = glow(im, cx, cy, R * 1.7, (110, 40, 170), 1.0)
    im = glow(im, cx, cy - R * 0.1, R * 0.9, (255, 190, 70), 0.45)
    # rays behind the rare card
    rays = Image.new("RGBA", im.size, (0, 0, 0, 0))
    rd = ImageDraw.Draw(rays)
    for i in range(18):
        a0 = math.radians(i * 20 - 4)
        a1 = math.radians(i * 20 + 4)
        rd.polygon([(cx, cy - R * 0.1), (cx + math.cos(a0) * R * 1.8, cy - R * 0.1 + math.sin(a0) * R * 1.8), (cx + math.cos(a1) * R * 1.8, cy - R * 0.1 + math.sin(a1) * R * 1.8)], fill=(255, 210, 110, 34))
    m = Image.new("L", im.size, 0)
    ImageDraw.Draw(m).ellipse([cx - R * 1.35, cy - R * 1.35, cx + R * 1.35, cy + R * 1.35], fill=255)
    m = m.filter(ImageFilter.GaussianBlur(R * 0.25))
    rays.putalpha(ImageChops.multiply(rays.getchannel("A"), m))
    im = Image.alpha_composite(im, rays)
    # swirl of two shuffle arrows
    arr = Image.new("RGBA", im.size, (0, 0, 0, 0))
    ad = ImageDraw.Draw(arr)
    rr = R * 1.12
    for base in (205, 25):
        ad.arc([cx - rr, cy - rr * 0.9, cx + rr, cy + rr * 0.9], base, base + 120, fill=(190, 140, 255, 255), width=int(R * 0.06))
        a = math.radians(base + 120)
        ex, ey = cx + math.cos(a) * rr, cy + math.sin(a) * rr * 0.9
        tx, ty = -math.sin(a), math.cos(a) * 0.9
        n = math.hypot(tx, ty)
        tx, ty = tx / n, ty / n
        nx, ny = -ty, tx
        hl = R * 0.2
        ad.polygon([(ex + tx * hl, ey + ty * hl), (ex + nx * hl * 0.7, ey + ny * hl * 0.7), (ex - nx * hl * 0.7, ey - ny * hl * 0.7)], fill=(190, 140, 255, 255))
    im = Image.alpha_composite(im, arr.filter(ImageFilter.GaussianBlur(6 * SS)))
    im = Image.alpha_composite(im, arr)
    cw = int(R * 0.74)
    ch = int(cw * 1.4)
    spec = [
        (-0.62, 0.12, -17, (150, 150, 166), (130, 130, 150), (110, 110, 140), "circle", 1, False),
        (0.62, 0.12, 17, (80, 160, 255), (50, 130, 255), (60, 130, 240), "diamond", 1, False),
        (0.0, -0.06, 0, (255, 205, 80), (255, 160, 30), (240, 120, 30), "star", 2, True),
    ]
    for (dx, dy, ang, fc, gc, ac, gl, cost, seal) in spec:
        card, _ = make_card(cw, ch, fc, gc, ac, cost=cost if gl != "circle" else 1, seal=seal, glyph=gl)
        im = place(im, card, cx + dx * R, cy + dy * R, -ang)
    im = sparkles(im, [(cx + R * 0.05, cy - R * 0.98, R * 0.14), (cx + R * 0.95, cy - R * 0.45, R * 0.09), (cx - R * 0.98, cy - R * 0.5, R * 0.08), (cx + R * 0.75, cy + R * 0.82, R * 0.07)])
    return im


# ---------------------------------------------------------------- layouts
MODS = {
    "SeedSearch": dict(
        name="SEED SEARCH", icon_label="SEED SEARCH", words=("SEED", "SEARCH"), tag="FIND THE PERFECT RUN",
        top=(8, 22, 38), bottom=(4, 8, 18), light=(30, 120, 140), emb=[(120, 230, 255), (255, 220, 140)], emblem=emblem_seed, seed=1,
    ),
    "CardBalance": dict(
        name="CARD BALANCE", icon_label="CARD BALANCE", icon_r=0.27, icon_y=0.5, words=("CARD", "BALANCE"), tag="SMALL, CAREFUL TWEAKS",
        top=(34, 8, 10), bottom=(12, 4, 8), light=(150, 40, 20), emb=[(255, 150, 60), (255, 220, 120)], emblem=emblem_balance, seed=2,
    ),
    "CardRarityRandomizer": dict(
        name="CARD RARITY RANDOMIZER", icon_label="RARITY", icon_r=0.33, words=("CARD RARITY", "RANDOMIZER"), tag="EVERY RUN, A NEW ORDER",
        top=(24, 8, 40), bottom=(8, 4, 18), light=(120, 50, 170), emb=[(220, 150, 255), (255, 210, 110)], emblem=emblem_rarity, seed=3,
    ),
}


def finish(im, w, h, inset, amt=1.0):
    im = frame(im, inset)
    im = vignette(im, 0.45)
    out = im.resize((w, h), Image.LANCZOS).convert("RGB")
    return grain(out.convert("RGBA"), amt).convert("RGB")


def render_icon(m, size=512):
    c = new_canvas(size, size)
    bgim = background(size, size, m["top"], m["bottom"], m["light"], m["seed"])
    bgim = vignette(bgim, 0.8)
    bgim = embers(bgim, 70, m["emb"], (0.05, 0.1, 0.95, 0.95), m["seed"])
    im = m["emblem"](bgim, size / 2, size * m.get("icon_y", 0.47), size * m.get("icon_r", 0.37))
    im, _ = title_text(im, m["icon_label"], size / 2, size * 0.8, size * 0.075, 3)
    im = vignette(im, 0.3)
    return finish(im, size, size, 10)


def render_banner(m, w=1280, h=420):
    bgim = background(w, h, m["top"], m["bottom"], m["light"], m["seed"] + 10)
    bgim = glow(bgim, w * 0.74, h * 0.5, h * 1.4, m["light"], 0.55, 0.9)
    bgim = vignette(bgim, 0.85)
    bgim = embers(bgim, 160, m["emb"], (0.02, 0.05, 0.98, 0.98), m["seed"] + 10)
    # emblem on the left, title block on the right
    im = m["emblem"](bgim, h * 0.7, h * 0.52, h * 0.34)
    l1, l2 = m["words"]
    tx = w * 0.43
    size1 = h * 0.2
    im, b1 = title_text(im, l1, tx, h * 0.17, size1, 6, left=True)
    im, b2 = title_text(im, l2, tx, h * 0.17 + size1 * 1.28, size1, 6, left=True)
    im = subtitle(im, m["tag"], tx, h * 0.17 + size1 * 2.62, h * 0.052, left=True, rule=False)
    im = vignette(im, 0.25)
    return finish(im, w, h, 12)


def render_workshop(m, w=1280, h=720):
    bgim = background(w, h, m["top"], m["bottom"], m["light"], m["seed"] + 20)
    bgim = glow(bgim, w * 0.5, h * 0.4, h * 0.9, m["light"], 0.6, 0.8)
    bgim = vignette(bgim, 0.85)
    bgim = embers(bgim, 260, m["emb"], (0.02, 0.05, 0.98, 0.98), m["seed"] + 20, 3.4)
    im = m["emblem"](bgim, w / 2, h * 0.39, h * 0.3)
    l1, l2 = m["words"]
    name = m["name"]
    fs = h * 0.115 if len(name) < 16 else h * 0.088
    im, _ = title_text(im, name, w / 2, h * 0.745, fs, 8 if len(name) < 16 else 5)
    im = subtitle(im, m["tag"], w / 2, h * 0.745 + fs * 1.45, h * 0.034, spacing=9)
    im = vignette(im, 0.25)
    return finish(im, w, h, 16, 0.4)


def main():
    for mod, m in MODS.items():
        art = os.path.join(ROOT, mod, "art")
        ws = os.path.join(ROOT, mod, "workshop")
        os.makedirs(art, exist_ok=True)
        os.makedirs(ws, exist_ok=True)
        render_icon(m).save(os.path.join(art, "icon.png"), optimize=True)
        render_banner(m).save(os.path.join(art, "banner.png"), optimize=True)
        render_workshop(m).save(os.path.join(ws, "image.png"), optimize=True)
        print("done", mod)


if __name__ == "__main__":
    main()
