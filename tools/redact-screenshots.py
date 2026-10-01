r"""Redact the local Windows username from the promo screenshots.

Why: the log panel and the preview title bar print the data directory as
`C:\Users\<username>\AppData\Local\imgagent`. That username is a weak variant
of the GitHub login, so it is scrubbed before the shots enter a public repo.

Method: mosaic (downscale then nearest-neighbour upscale) over the username
only. Two things were tried and rejected first:
  * a solid colour bar -- too opaque, it also clips the neighbouring
    characters (`\` and `A`), which makes the screenshot look damaged;
  * a heavy Gaussian blur -- on a ~47x21 px region at radius 10 it averages
    down to a flat pale rectangle, i.e. the same "sticker" look.
Mosaic keeps the surrounding text intact, reads unmistakably as "redacted",
and survives the upscaling GitHub applies on the README.

Box coordinates were located by pixel analysis (row/column projections over
each text band), not by eye.

Usage:
    python tools/redact-screenshots.py
"""

import os
import sys

try:
    from PIL import Image, ImageFilter
except ImportError:
    sys.exit("Pillow is required: python -m pip install Pillow")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC_DIR = os.path.join(REPO, ".reasonix", "attachments")
OUT_DIR = os.path.join(REPO, "assets", "screenshots")

# Each box: (left, top, right, bottom), tight around the username word only.
# "Yutsuki" glyph columns were measured as x=869..909 in both log lines and
# x=499..545 in the title bar; the boxes take 1px of padding so no glyph edge
# leaks, and stop short of the neighbouring `\` (x>=910) and `A` (x>=554) so
# those stay legible.
JOBS = {
    "main-window.png": {
        "src": "clipboard-20261001-102403.287963-000002.png",
        "boxes": [
            (866, 482, 912, 500),   # log: 数据目录 C:\Users\<name>\AppData\...
            (866, 575, 912, 593),   # log: 数据目录可写 (C:\Users\<name>\...)
        ],
    },
    "mask-edit.png": {
        "src": "clipboard-20261001-102514.750879-000004.png",
        "boxes": [],                # verified: no local path in this shot
    },
    "prompt-guide.png": {
        "src": "clipboard-20261001-102603.262407-000005.png",
        "boxes": [],                # verified: no local path in this shot
    },
    "advanced-params.png": {
        "src": "clipboard-20261001-102628.602865-000006.png",
        "boxes": [
            (496, 134, 547, 151),   # preview title bar: C:\Users\<name>\AppData\...
        ],
    },
}

BLOCK = 4   # mosaic cell size in source pixels


def redact(img, boxes):
    for (l, t, r, b) in boxes:
        region = img.crop((l, t, r, b))
        w, h = region.size
        small = region.resize((max(1, w // BLOCK), max(1, h // BLOCK)),
                              Image.Resampling.BILINEAR)
        mosaic = small.resize((w, h), Image.Resampling.NEAREST)
        img.paste(mosaic, (l, t))
    return img


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    for out_name, spec in JOBS.items():
        src = os.path.join(SRC_DIR, spec["src"])
        if not os.path.exists(src):
            sys.exit("missing source screenshot: " + src)
        img = Image.open(src).convert("RGB")
        redact(img, spec["boxes"])
        dst = os.path.join(OUT_DIR, out_name)
        img.save(dst, "PNG", optimize=True)
        print("%-22s <- %-46s scrubbed=%-2d %dx%d  %d KB"
              % (out_name, spec["src"], len(spec["boxes"]),
                 img.width, img.height, os.path.getsize(dst) // 1024))


if __name__ == "__main__":
    main()
