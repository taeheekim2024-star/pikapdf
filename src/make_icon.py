import sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter

S = 1024
out_ico, out_png = sys.argv[1], sys.argv[2]

def draw_base():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    m = 110
    fold = 230
    left, right, top, bottom = m + 40, S - m - 20, m, S - m
    page = [(left, top), (right - fold, top), (right, top + fold), (right, bottom), (left, bottom)]
    # soft shadow
    sh = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(sh).polygon([(x + 16, y + 24) for x, y in page], fill=(0, 0, 0, 80))
    sh = sh.filter(ImageFilter.GaussianBlur(26))
    img.alpha_composite(sh)
    d = ImageDraw.Draw(img)
    d.polygon(page, fill=(255, 255, 255, 255))
    # folded corner
    d.polygon([(right - fold, top), (right - fold, top + fold), (right, top + fold)], fill=(222, 227, 234, 255))
    d.line([(right - fold, top), (right - fold, top + fold), (right, top + fold)], fill=(178, 186, 197, 255), width=12, joint="curve")
    d.line(page + [page[0]], fill=(178, 186, 197, 255), width=12, joint="curve")
    # text lines with a yellow highlight on the second one
    x0, x1 = left + 110, right - 110
    y0 = top + fold + 70
    gap = 118
    ys = [y0, y0 + gap, y0 + 2 * gap]
    d.rounded_rectangle([x0 - 34, ys[1] - 66, x1 - 150 + 34, ys[1] + 66], radius=34, fill=(255, 212, 0, 255))
    for i, y in enumerate(ys):
        w = x1 if i != 1 else x1 - 150
        d.rounded_rectangle([x0, y - 24, w, y + 24], radius=24, fill=(141, 150, 163, 255))
    # blue band with PDF
    band_top = bottom - 330
    d.rounded_rectangle([m - 40, band_top, right + 40, bottom - 60], radius=40, fill=(27, 78, 145, 255))
    font = ImageFont.truetype("/mnt/c/Windows/Fonts/arialbd.ttf", 215)
    text = "PDF"
    bb = d.textbbox((0, 0), text, font=font)
    tw, th = bb[2] - bb[0], bb[3] - bb[1]
    cx = (m - 40 + right + 40) / 2
    cy = (band_top + bottom - 60) / 2
    d.text((cx - tw / 2 - bb[0], cy - th / 2 - bb[1]), text, font=font, fill=(255, 255, 255, 255))
    return img

base = draw_base()
sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
frames = [base.resize((s, s), Image.LANCZOS) for s in sizes]
frames[-1].save(out_png)
frames[-1].save(out_ico, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1], bitmap_format="bmp")
print("ok", out_ico)
