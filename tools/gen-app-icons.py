#!/usr/bin/env python3
# Generates the desktop icons (hicolor theme sizes) from the master icon, for the desktop entry.
#
#   python3 tools/gen-app-icons.py
#
# The master is square-padded, then each size is resampled from it with Lanczos, so small sizes
# stay sharp. Output: packaging/linux/icons/hicolor/<n>x<n>/apps/<app id>.png
import os
from PIL import Image

APP_ID = "io.github.codingncaffeine.Tuxflix"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MASTER = os.path.join(ROOT, "assets", "branding", "tuxflix-icon.png")
SIZES = [16, 22, 24, 32, 48, 64, 96, 128, 256, 512]

master = Image.open(MASTER).convert("RGBA")
side = max(master.size)
square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
square.paste(master, ((side - master.width) // 2, (side - master.height) // 2))
for size in SIZES:
    folder = os.path.join(ROOT, "packaging", "linux", "icons", "hicolor", f"{size}x{size}", "apps")
    os.makedirs(folder, exist_ok=True)
    square.resize((size, size), Image.LANCZOS).save(os.path.join(folder, APP_ID + ".png"), optimize=True)
    print(f"{size}x{size}")
