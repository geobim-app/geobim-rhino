# geoBIM.app for Rhino: renders the toolbar icon strips
#
# Lucide icons (ISC license, tools/icons/*.svg, lucide-static 0.460.0) in the brand
# colour #2ECFB0, one strip per size in src/GeoBimRhino/Resources/toolbar-16/24/32.png,
# in the order of make_rui.BUTTONS. Needs PyMuPDF (fitz) and Pillow.
#
#   python tools/make_icons.py && python tools/make_rui.py

import io
import os
import sys

import fitz
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from make_rui import BUTTONS, PROJ  # noqa: E402

COLOR = "#2ECFB0"


def render(icon, size):
    svg = open(os.path.join(HERE, "icons", icon + ".svg"), encoding="utf-8").read()
    svg = svg.replace("currentColor", COLOR).replace('width="24"', 'width="%d"' % size).replace('height="24"', 'height="%d"' % size)
    pix = fitz.open("svg", svg.encode("utf-8"))[0].get_pixmap(alpha=True)
    im = Image.open(io.BytesIO(pix.tobytes("png"))).convert("RGBA")
    if im.size != (size, size):
        im = im.resize((size, size), Image.LANCZOS)
    return im


def main():
    for size in (16, 24, 32):
        strip = Image.new("RGBA", (size * len(BUTTONS), size), (0, 0, 0, 0))
        for i, b in enumerate(BUTTONS):
            strip.paste(render(b[4], size), (i * size, 0))
        path = os.path.join(PROJ, "Resources", "toolbar-%d.png" % size)
        strip.save(path)
        print("written", path, strip.size)


if __name__ == "__main__":
    main()
