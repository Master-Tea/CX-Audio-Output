#!/usr/bin/env python3
"""Generate properly padded, correctly scaled MSIX image assets.

Source: design/icon-source.png (1254x1254, transparent background).
Output: packaging/msix/Assets.

All square assets keep the source's 1:1 aspect ratio. The glyph is centered
inside a transparent safe zone, so Windows never stretches or clips it when it
scales the asset for the Start menu, taskbar, tiles, or Store listing.
"""

from __future__ import annotations

import math
import shutil
import sys
from pathlib import Path

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError:  # pragma: no cover - packaging helper only
    sys.exit("Pillow is required: python -m pip install pillow")

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "design" / "icon-source.png"
OUT = ROOT / "packaging" / "msix" / "Assets"

BACKGROUND = (10, 12, 15, 255)
TEXT = (245, 247, 242, 255)
ACCENT = (147, 190, 49, 255)

# Windows scale factor -> pixel size. Values are rounded half-up to match the
# Microsoft table (for example 310 * 1.25 = 387.5 -> 388).
SCALES = {
    "100": 1.00,
    "125": 1.25,
    "150": 1.50,
    "200": 2.00,
    "400": 4.00,
}

SQUARE_44 = [44, 55, 66, 88, 176]
SQUARE_150 = [150, 188, 225, 300, 600]
SQUARE_310 = [310, 388, 465, 620, 1240]
STORE = [50, 63, 75, 100, 200]
WIDE = [(310, 150), (388, 188), (465, 225), (620, 300), (1240, 600)]
SPLASH = [(620, 300), (775, 375), (930, 450), (1240, 600), (2480, 1200)]

# App-list target-size variants (taskbar, search, context menus).
TARGET_SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256]


def half_up(value: float) -> int:
    return int(math.floor(value + 0.5))


def load_icon() -> Image.Image:
    if not SOURCE.exists():
        sys.exit(f"Source icon not found: {SOURCE}")
    icon = Image.open(SOURCE).convert("RGBA")
    if icon.width != icon.height:
        side = min(icon.width, icon.height)
        left = (icon.width - side) // 2
        top = (icon.height - side) // 2
        icon = icon.crop((left, top, left + side, top + side))
    return icon


def padded(icon: Image.Image, size: int, ratio: float) -> Image.Image:
    """Center the full icon square on a transparent square, with padding."""
    safe = max(1, half_up(size * ratio))
    scaled = icon.resize((safe, safe), Image.LANCZOS)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.alpha_composite(scaled, ((size - safe) // 2, (size - safe) // 2))
    return canvas


def save(image: Image.Image, name: str) -> None:
    image.save(OUT / name, "PNG", optimize=True)


def font(name: str, size: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    candidates = [
        Path(r"C:\Windows\Fonts") / name,
        Path(r"C:\Windows\Fonts") / "segoeuib.ttf",
        Path(r"C:\Windows\Fonts") / "segoeui.ttf",
        Path(r"C:\Windows\Fonts") / "arialbd.ttf",
    ]
    for candidate in candidates:
        try:
            if candidate.exists():
                return ImageFont.truetype(str(candidate), size)
        except OSError:
            continue
    return ImageFont.load_default()


def make_wide_master(icon: Image.Image) -> Image.Image:
    """4x composition; all wide scale variants are downscaled from this."""
    master = Image.new("RGBA", (1240, 600), BACKGROUND)
    glyph = padded(icon, 368, 0.84)
    master.alpha_composite(glyph, (56, 116))

    draw = ImageDraw.Draw(master)
    draw.text((480, 216), "CX Audio Output", font=font("segoeuib.ttf", 72), fill=TEXT)
    draw.text((484, 330), "WIRELESS + USB SPEAKER", font=font("segoeui.ttf", 36), fill=ACCENT)
    return master


def make_splash_master(icon: Image.Image) -> Image.Image:
    """4x composition; all splash scale variants are downscaled from this."""
    master = Image.new("RGBA", (2480, 1200), BACKGROUND)
    glyph = padded(icon, 672, 0.84)
    master.alpha_composite(glyph, (160, 264))

    draw = ImageDraw.Draw(master)
    draw.text((944, 448), "CX Audio Output", font=font("segoeuib.ttf", 152), fill=TEXT)
    draw.text((952, 656), "NATIVE SPEAKER CONTROL", font=font("segoeui.ttf", 56), fill=ACCENT)
    return master


def main() -> None:
    if not SOURCE.exists():
        sys.exit(f"Source icon not found: {SOURCE}")

    OUT.mkdir(parents=True, exist_ok=True)
    icon = load_icon()

    # Remove old generated variants so stale sizes cannot survive a re-run.
    for pattern in (
        "Square44x44Logo*.png",
        "Square150x150Logo*.png",
        "Square310x310Logo*.png",
        "StoreLogo*.png",
        "Wide310x150Logo*.png",
        "SplashScreen*.png",
    ):
        for existing in OUT.glob(pattern):
            existing.unlink()

    # Square 44 (app list / taskbar). 0.88 keeps a real margin even at 16 px.
    for index, size in enumerate(SQUARE_44):
        scale = list(SCALES)[index]
        image = padded(icon, size, 0.88)
        save(image, f"Square44x44Logo.scale-{scale}.png")
        if scale == "100":
            save(image, "Square44x44Logo.png")

    for size in TARGET_SIZES:
        image = padded(icon, size, 0.88)
        save(image, f"Square44x44Logo.targetsize-{size}.png")
        save(image, f"Square44x44Logo.targetsize-{size}_altform-unplated.png")
        save(image, f"Square44x44Logo.targetsize-{size}_altform-lightunplated.png")

    # Medium / large tiles. 0.78 leaves breathing room inside the tile.
    for index, size in enumerate(SQUARE_150):
        scale = list(SCALES)[index]
        image = padded(icon, size, 0.78)
        save(image, f"Square150x150Logo.scale-{scale}.png")
        if scale == "100":
            save(image, "Square150x150Logo.png")

    for index, size in enumerate(SQUARE_310):
        scale = list(SCALES)[index]
        image = padded(icon, size, 0.78)
        save(image, f"Square310x310Logo.scale-{scale}.png")
        if scale == "100":
            save(image, "Square310x310Logo.png")

    for index, size in enumerate(STORE):
        scale = list(SCALES)[index]
        image = padded(icon, size, 0.80)
        save(image, f"StoreLogo.scale-{scale}.png")
        if scale == "100":
            save(image, "StoreLogo.png")

    wide_master = make_wide_master(icon)
    for index, size in enumerate(WIDE):
        scale = list(SCALES)[index]
        image = wide_master.resize(size, Image.LANCZOS)
        save(image, f"Wide310x150Logo.scale-{scale}.png")
        if scale == "100":
            save(image, "Wide310x150Logo.png")

    splash_master = make_splash_master(icon)
    for index, size in enumerate(SPLASH):
        scale = list(SCALES)[index]
        image = splash_master.resize(size, Image.LANCZOS)
        save(image, f"SplashScreen.scale-{scale}.png")
        if scale == "100":
            save(image, "SplashScreen.png")

    # A small preview strip is convenient when reviewing a release; it is not
    # referenced by the package manifest.
    preview = Image.new("RGBA", (780, 220), BACKGROUND)
    preview.alpha_composite(padded(icon, 150, 0.78), (20, 35))
    preview.alpha_composite(wide_master.resize((310, 150), Image.LANCZOS), (190, 35))
    preview.alpha_composite(splash_master.resize((240, 116), Image.LANCZOS), (520, 52))
    preview.save(OUT.parent / "icon-preview-msix.png", "PNG", optimize=True)

    print(f"Generated {len(list(OUT.glob('*.png')))} MSIX assets in {OUT}")


if __name__ == "__main__":
    main()
