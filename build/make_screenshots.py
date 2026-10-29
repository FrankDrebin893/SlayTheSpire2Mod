"""Generates crisp high-resolution screenshots for Card Rarity Randomizer.

Outputs:
  - mods/CardRarityRandomizer/art/screenshots/
      1_summary_screen.png
      2_character_select_toggle.png
      3_shuffled_card_rewards.png
  - mods/CardRarityRandomizer/workshop/previews/
      (copies optimized under 1MB for Steam Workshop preview gallery)
"""
import math
import os
import random
import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

from make_art import (
    serif, _font, lerp, new_canvas, from_np, to_np,
    glow, background, vignette, grain, embers, gold_grad, drop_shadow,
    sparkles, star, make_card, place, SS
)

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "mods", "CardRarityRandomizer")
ART_DIR = os.path.join(ROOT, "art", "screenshots")
PREVIEW_DIR = os.path.join(ROOT, "workshop", "previews")

os.makedirs(ART_DIR, exist_ok=True)
os.makedirs(PREVIEW_DIR, exist_ok=True)

BASE_W, BASE_H = 1920, 1080

def sans(size, bold=False):
    names = ["segoeuib.ttf", "arialbd.ttf"] if bold else ["segoeui.ttf", "arial.ttf"]
    return _font(names, int(size * SS))

def f_serif(size):
    return serif(int(size * SS))

def render_summary_screen():
    print("Generating 1_summary_screen.png...")
    im = background(BASE_W, BASE_H, (18, 12, 28), (8, 6, 14), (90, 40, 140), seed=42)
    W, H = im.size
    im = glow(im, W * 0.5, H * 0.45, 600 * SS, (110, 40, 170), 0.6)
    im = embers(im, 80, [(220, 150, 255), (255, 210, 110)], (0.05, 0.05, 0.95, 0.95), seed=42)

    # Dim overlay
    dim = Image.new("RGBA", (W, H), (0, 0, 0, 195))
    im = Image.alpha_composite(im, dim)

    # Central modal panel (960 x 720 base)
    pw, ph = int(960 * SS), int(720 * SS)
    px0 = (W - pw) // 2
    py0 = (H - ph) // 2
    px1, py1 = px0 + pw, py0 + ph

    panel = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    pd = ImageDraw.Draw(panel)

    pd.rounded_rectangle([px0, py0, px1, py1], 12 * SS, fill=(20, 18, 26, 250), outline=(80, 70, 95, 255), width=2 * SS)
    pd.rounded_rectangle([px0 + 3 * SS, py0 + 3 * SS, px1 - 3 * SS, py1 - 3 * SS], 10 * SS, outline=(42, 38, 52, 255), width=1 * SS)
    im = drop_shadow(im, panel, blur=18, off=(0, 12), a=0.7)
    im = Image.alpha_composite(im, panel)

    d = ImageDraw.Draw(im)

    # Title
    f_title = f_serif(34)
    title_text = "Card Rarity Randomizer"
    tw = d.textlength(title_text, font=f_title)
    d.text(((W - tw) / 2, py0 + 26 * SS), title_text, font=f_title, fill=(255, 230, 150, 255))

    # Toggle Button on the modal
    bw, bh = int(460 * SS), int(46 * SS)
    bx0 = (W - bw) // 2
    by0 = py0 + int(82 * SS)
    d.rounded_rectangle([bx0, by0, bx0 + bw, by0 + bh], 8 * SS, fill=(62, 30, 92, 255), outline=(242, 198, 90, 255), width=2 * SS)
    f_btn = sans(19, bold=True)
    btn_text = "🎲 Randomizer: ON  (Click to Disable)"
    btw = d.textlength(btn_text, font=f_btn)
    d.text(((W - btw) / 2, by0 + 10 * SS), btn_text, font=f_btn, fill=(255, 236, 170, 255))

    # Subtitle info
    f_sub = sans(15)
    sub_text = "48 cards changed rarity this run.   Common -> Rare: 6   Rare -> Common: 6   [Press F8 anytime]"
    stw = d.textlength(sub_text, font=f_sub)
    d.text(((W - stw) / 2, by0 + 58 * SS), sub_text, font=f_sub, fill=(190, 185, 205, 255))

    # Inner scroll container frame
    sx0, sy0, sx1, sy1 = px0 + int(30 * SS), by0 + int(90 * SS), px1 - int(30 * SS), py1 - int(72 * SS)
    d.rounded_rectangle([sx0, sy0, sx1, sy1], 6 * SS, fill=(13, 12, 17, 255), outline=(55, 50, 65, 255), width=1 * SS)

    rows = [
        ("Ironclad", [
            ("Strike", "Common", "Rare"),
            ("Whirlwind", "Uncommon", "Common"),
            ("Demon Form", "Rare", "Uncommon"),
            ("Defend", "Common", "Rare"),
            ("Limit Break", "Rare", "Common"),
            ("Offering", "Rare", "Uncommon"),
        ]),
        ("Silent", [
            ("Neutralize", "Common", "Rare"),
            ("Adrenaline", "Rare", "Common"),
            ("Corpse Explosion", "Rare", "Uncommon"),
            ("Blade Dance", "Common", "Rare"),
        ]),
        ("Defect", [
            ("Echo Form", "Rare", "Common"),
            ("Zap", "Common", "Rare"),
            ("Biased Cognition", "Rare", "Uncommon"),
        ])
    ]

    col_map = {
        "Common": (185, 190, 200),
        "Uncommon": (90, 195, 255),
        "Rare": (255, 205, 65)
    }

    f_pool = f_serif(21)
    f_card = sans(17)
    f_rar = sans(16, bold=True)

    curr_y = sy0 + int(14 * SS)
    for pool_name, cards in rows:
        d.text((sx0 + 20 * SS, curr_y), pool_name, font=f_pool, fill=(245, 215, 130, 255))
        curr_y += int(30 * SS)
        for card_name, from_r, to_r in cards:
            if curr_y + int(26 * SS) > sy1 - int(10 * SS):
                break
            d.rounded_rectangle([sx0 + 10 * SS, curr_y - 2 * SS, sx1 - 18 * SS, curr_y + 24 * SS], 4 * SS, fill=(22, 20, 28, 140))
            d.text((sx0 + 24 * SS, curr_y + 2 * SS), card_name, font=f_card, fill=(230, 226, 235, 255))

            d.text((sx0 + 380 * SS, curr_y + 2 * SS), from_r, font=f_rar, fill=col_map[from_r] + (255,))
            d.text((sx0 + 510 * SS, curr_y + 2 * SS), "  ->  ", font=f_card, fill=(150, 145, 160, 255))
            d.text((sx0 + 590 * SS, curr_y + 2 * SS), to_r, font=f_rar, fill=col_map[to_r] + (255,))

            curr_y += int(28 * SS)
        curr_y += int(10 * SS)

    sb_x = sx1 - int(12 * SS)
    d.rounded_rectangle([sb_x, sy0 + int(20 * SS), sb_x + int(6 * SS), sy0 + int(180 * SS)], int(3 * SS), fill=(110, 95, 140, 255))

    # Bottom button: "Start the run"
    cw, ch = int(320 * SS), int(46 * SS)
    cx0 = (W - cw) // 2
    cy0 = py1 - int(60 * SS)
    d.rounded_rectangle([cx0, cy0, cx0 + cw, cy0 + ch], 8 * SS, fill=(35, 30, 48, 255), outline=(150, 130, 185, 255), width=2 * SS)
    f_close = sans(18, bold=True)
    close_text = "Start the run"
    clw = d.textlength(close_text, font=f_close)
    d.text(((W - clw) / 2, cy0 + int(11 * SS)), close_text, font=f_close, fill=(240, 235, 255, 255))

    out = vignette(im, 0.35)
    return out.resize((BASE_W, BASE_H), Image.LANCZOS)

def render_character_select_toggle():
    print("Generating 2_character_select_toggle.png...")
    im = background(BASE_W, BASE_H, (22, 10, 16), (10, 6, 10), (140, 40, 30), seed=10)
    W, H = im.size
    im = glow(im, W * 0.3, H * 0.5, 700 * SS, (180, 50, 40), 0.5)
    im = embers(im, 100, [(255, 120, 60), (255, 200, 100)], (0.05, 0.05, 0.95, 0.95), seed=10)

    d = ImageDraw.Draw(im)

    # Top title: CHOOSE YOUR CHARACTER
    f_top = f_serif(36)
    d.text((80 * SS, 50 * SS), "CHOOSE YOUR CHARACTER", font=f_top, fill=(240, 220, 160, 255))

    # Character icon cards on left
    char_names = ["THE IRONCLAD", "THE SILENT", "THE DEFECT", "THE NECROBINDER", "THE REGENT"]
    f_ch = f_serif(19)
    for i, name in enumerate(char_names):
        bx = int((80 + i * 220) * SS)
        by = int(130 * SS)
        is_sel = (i == 0)
        col = (210, 160, 60) if is_sel else (60, 50, 70)
        bg = (55, 24, 26) if is_sel else (25, 20, 28)
        d.rounded_rectangle([bx, by, bx + int(200 * SS), by + int(120 * SS)], 8 * SS, fill=bg + (240,), outline=col + (255,), width=3 * SS if is_sel else 1 * SS)
        d.text((bx + 18 * SS, by + 48 * SS), name, font=f_ch, fill=(255, 235, 180, 255) if is_sel else (160, 150, 170, 255))

    # Character info showcase panel (left/middle)
    ix, iy, iw, ih = int(80 * SS), int(290 * SS), int(540 * SS), int(480 * SS)
    d.rounded_rectangle([ix, iy, ix + iw, iy + ih], 10 * SS, fill=(18, 14, 20, 220), outline=(70, 60, 75, 255), width=2 * SS)
    d.text((ix + 30 * SS, iy + 30 * SS), "THE IRONCLAD", font=f_serif(30), fill=(255, 225, 140, 255))
    d.text((ix + 30 * SS, iy + 76 * SS), "HP: 80/80      GOLD: 99", font=sans(18, bold=True), fill=(240, 100, 80, 255))
    f_desc = sans(16)
    desc_lines = [
        "The remaining soldier of the Ironclads.",
        "Sold his soul to harness demonic power.",
        "",
        "Starting Relic: Burning Blood",
        "At the end of combat, heal 6 HP."
    ]
    for k, line in enumerate(desc_lines):
        d.text((ix + 30 * SS, iy + (130 + k * 26) * SS), line, font=f_desc, fill=(200, 195, 210, 255))

    # Ascension panel (bottom center)
    ax, ay, aw, ah = int(700 * SS), int(860 * SS), int(480 * SS), int(90 * SS)
    d.rounded_rectangle([ax, ay, ax + aw, ay + ah], 8 * SS, fill=(24, 18, 26, 230), outline=(90, 75, 105, 255), width=2 * SS)
    d.text((ax + 130 * SS, ay + 18 * SS), "ASCENSION LEVEL 10", font=f_serif(20), fill=(245, 210, 120, 255))
    d.text((ax + 100 * SS, ay + 50 * SS), "< Normal modifiers applied >", font=sans(15), fill=(160, 155, 175, 255))

    # Back button bottom left
    d.rounded_rectangle([80 * SS, 960 * SS, 260 * SS, 1020 * SS], 8 * SS, fill=(35, 30, 40, 255), outline=(90, 80, 100, 255), width=2 * SS)
    d.text((120 * SS, 978 * SS), "BACK", font=f_serif(22), fill=(220, 215, 230, 255))

    # EMBARK BUTTON bottom right
    ex0, ey0, ex1, ey1 = int(1540 * SS), int(940 * SS), int(1840 * SS), int(1020 * SS)
    d.rounded_rectangle([ex0, ey0, ex1, ey1], 8 * SS, fill=(140, 40, 30, 255), outline=(255, 200, 80, 255), width=3 * SS)
    emb_f = f_serif(28)
    d.text((ex0 + 82 * SS, ey0 + 22 * SS), "EMBARK", font=emb_f, fill=(255, 240, 180, 255))

    # --- THE MOD FEATURE: Randomizer Toggle Button Right Above Embark! ---
    tx0, ty0, tx1, ty1 = ex0, ey0 - int(64 * SS), ex1, ey0 - int(12 * SS)
    d.rounded_rectangle([tx0, ty0, tx1, ty1], 8 * SS, fill=(65, 32, 98, 255), outline=(250, 205, 90, 255), width=3 * SS)
    tog_f = sans(18, bold=True)
    tog_text = "🎲 Randomizer: ON"
    ttw = d.textlength(tog_text, font=tog_f)
    d.text((tx0 + (int(300 * SS) - ttw) / 2, ty0 + 13 * SS), tog_text, font=tog_f, fill=(255, 240, 180, 255))

    # Callout Arrow pointing to the toggle button
    cx0, cy0 = tx0 - int(380 * SS), ty0 - int(80 * SS)
    d.rounded_rectangle([cx0, cy0, cx0 + int(350 * SS), cy0 + int(104 * SS)], 8 * SS, fill=(15, 12, 22, 245), outline=(180, 140, 240, 255), width=2 * SS)
    f_call = sans(15, bold=True)
    d.text((cx0 + 16 * SS, cy0 + 14 * SS), "✨ One-Click In-Game Toggle!", font=f_call, fill=(255, 225, 110, 255))
    d.text((cx0 + 16 * SS, cy0 + 40 * SS), "Easily enable/disable for any run right here.", font=sans(14), fill=(220, 215, 235, 255))
    d.text((cx0 + 16 * SS, cy0 + 64 * SS), "Click to switch between ON & OFF instantly!", font=sans(14), fill=(160, 220, 255, 255))

    # Pointer arrow from callout to button
    d.line([(cx0 + int(350 * SS), cy0 + int(52 * SS)), (tx0 - int(10 * SS), ty0 + int(24 * SS))], fill=(200, 160, 255, 255), width=3 * SS)
    d.polygon([(tx0 - int(8 * SS), ty0 + int(24 * SS)), (tx0 - int(24 * SS), ty0 + int(16 * SS)), (tx0 - int(24 * SS), ty0 + int(32 * SS))], fill=(250, 205, 90, 255))

    # Secondary preview of OFF state
    ox0, oy0 = cx0, cy0 + int(124 * SS)
    d.rounded_rectangle([ox0, oy0, ox0 + int(350 * SS), oy0 + int(64 * SS)], 6 * SS, fill=(18, 18, 22, 240), outline=(80, 85, 95, 255), width=1 * SS)
    d.rounded_rectangle([ox0 + int(12 * SS), oy0 + int(12 * SS), ox0 + int(210 * SS), oy0 + int(52 * SS)], 6 * SS, fill=(30, 32, 38, 255), outline=(95, 100, 110, 255), width=2 * SS)
    d.text((ox0 + 26 * SS, oy0 + 19 * SS), "🎲 Randomizer: OFF", font=sans(15, bold=True), fill=(170, 175, 185, 255))
    d.text((ox0 + 226 * SS, oy0 + 22 * SS), "(Vanilla Mode)", font=sans(13), fill=(140, 140, 150, 255))

    out = vignette(im, 0.35)
    return out.resize((BASE_W, BASE_H), Image.LANCZOS)

def render_shuffled_card_rewards():
    print("Generating 3_shuffled_card_rewards.png...")
    im = background(BASE_W, BASE_H, (14, 16, 32), (6, 8, 18), (60, 100, 180), seed=77)
    W, H = im.size
    im = glow(im, W * 0.5, H * 0.45, 750 * SS, (40, 110, 180), 0.5)
    im = embers(im, 120, [(120, 210, 255), (255, 220, 130)], (0.05, 0.05, 0.95, 0.95), seed=77)

    d = ImageDraw.Draw(im)

    f_top = f_serif(38)
    title = "CHOOSE A CARD"
    ttw = d.textlength(title, font=f_top)
    d.text(((W - ttw) / 2, 80 * SS), title, font=f_top, fill=(255, 235, 170, 255))

    f_sub = sans(18)
    sub = "Card Rarity Randomizer shuffles cards into new tiers each run!"
    stw = d.textlength(sub, font=f_sub)
    d.text(((W - stw) / 2, 138 * SS), sub, font=f_sub, fill=(190, 220, 255, 255))

    # 3 Cards displayed side by side
    cw, ch = int(320 * SS), int(450 * SS)
    spacing = int(100 * SS)
    total_w = 3 * cw + 2 * spacing
    start_x = (W - total_w) // 2
    cards_y = int(230 * SS)

    # Card 1: Strike (Promoted to RARE! Glowing gold border, 1 cost)
    c1, _ = make_card(cw, ch, (255, 205, 80), (255, 170, 30), (220, 90, 40), cost=1, seal=True, glyph="star")
    im = place(im, c1, start_x + cw // 2, cards_y + ch // 2, angle=-2)

    # Card 2: Whirlwind (Demoted to COMMON! Grey border, 0 cost)
    c2, _ = make_card(cw, ch, (155, 160, 175), (120, 125, 140), (90, 95, 115), cost=0, dim=False, glyph="circle")
    im = place(im, c2, start_x + cw + spacing + cw // 2, cards_y + ch // 2, angle=0)

    # Card 3: Demon Form (Swapped to UNCOMMON! Cyan/Teal border, 3 cost)
    c3, _ = make_card(cw, ch, (75, 185, 255), (45, 140, 245), (70, 60, 160), cost=3, seal=False, glyph="diamond")
    im = place(im, c3, start_x + 2 * (cw + spacing) + cw // 2, cards_y + ch // 2, angle=2)

    d = ImageDraw.Draw(im)

    f_name = f_serif(24)
    f_badge = sans(17, bold=True)

    items = [
        ("STRIKE", "Common -> RARE!", (255, 215, 90), (120, 60, 10), start_x + cw // 2),
        ("WHIRLWIND", "Uncommon -> COMMON", (180, 185, 200), (35, 40, 50), start_x + cw + spacing + cw // 2),
        ("DEMON FORM", "Rare -> UNCOMMON", (90, 200, 255), (10, 50, 90), start_x + 2 * (cw + spacing) + cw // 2),
    ]

    for name, badge_text, bcol, bgcol, cx in items:
        ntw = d.textlength(name, font=f_name)
        d.text((cx - ntw / 2, cards_y + ch + 50 * SS), name, font=f_name, fill=(255, 245, 220, 255))

        btw = d.textlength(badge_text, font=f_badge)
        bx0 = cx - btw / 2 - 16 * SS
        by0 = cards_y + ch + 92 * SS
        d.rounded_rectangle([bx0, by0, bx0 + btw + 32 * SS, by0 + 36 * SS], 6 * SS, fill=bgcol + (240,), outline=bcol + (255,), width=2 * SS)
        d.text((cx - btw / 2, by0 + 7 * SS), badge_text, font=f_badge, fill=bcol + (255,))

    sw, sh = int(240 * SS), int(48 * SS)
    d.rounded_rectangle([(W - sw) / 2, 950 * SS, (W + sw) / 2, (950 + 48) * SS], 8 * SS, fill=(30, 26, 38, 255), outline=(90, 85, 110, 255), width=2 * SS)
    f_skip = sans(18, bold=True)
    d.text(((W - d.textlength("SKIP", font=f_skip)) / 2, 962 * SS), "SKIP", font=f_skip, fill=(210, 205, 225, 255))

    im = sparkles(im, [
        (start_x + 60 * SS, cards_y + 40 * SS, 16 * SS),
        (start_x + cw - 40 * SS, cards_y + 80 * SS, 14 * SS),
        (start_x + 80 * SS, cards_y + ch - 40 * SS, 12 * SS),
    ])

    out = vignette(im, 0.35)
    return out.resize((BASE_W, BASE_H), Image.LANCZOS)

def main():
    screenshots = [
        ("1_summary_screen.png", render_summary_screen()),
        ("2_character_select_toggle.png", render_character_select_toggle()),
        ("3_shuffled_card_rewards.png", render_shuffled_card_rewards()),
    ]

    for filename, img in screenshots:
        art_path = os.path.join(ART_DIR, filename)
        img.save(art_path, optimize=True)
        sz_art = os.path.getsize(art_path) / 1024
        print(f"Saved Full-HD: {art_path} ({sz_art:.1f} KB)")

        preview_img = img.resize((1280, 720), Image.LANCZOS)
        prev_path = os.path.join(PREVIEW_DIR, filename)
        preview_img.save(prev_path, optimize=True, quality=90)
        sz_prev = os.path.getsize(prev_path) / 1024
        print(f"Saved Workshop Preview: {prev_path} ({sz_prev:.1f} KB)")

if __name__ == "__main__":
    main()
