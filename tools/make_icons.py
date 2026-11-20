# geoBIM.app for Rhino: draws the toolbar icons
#
# Rhino 8 style: filled shapes with a dark outline, a coloured main symbol and an
# action badge (orange arrow up = upload, green arrow down = export), on a 48 x 48
# grid. Every icon exists twice, for Rhino's light and dark mode:
#   tools/icons/<icon>.svg, tools/icons/<icon>_dark.svg   (embedded in the .rui by make_rui.py)
#   src/GeoBimRhino/Resources/toolbar-16/24/32.png        (bitmap fallback, light, order of BUTTONS)
# The geoBIM.app icon is the geoBIM logo (globe with four bars). No <text> and no
# clip paths in the SVGs, so Rhino's own SVG renderer draws them as PyMuPDF does.
# Needs PyMuPDF (fitz) and Pillow.
#
#   python tools/make_icons.py [preview.png] && python tools/make_rui.py

import io
import math
import os
import sys

import fitz
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from make_rui import BUTTONS, PROJ  # noqa: E402

ICON_DIR = os.path.join(HERE, "icons")

# colours that change with Rhino's light / dark mode
PAL = {
    "light": dict(o="#1E1E1E", paper="#F7F7F7", fold="#D2D2D2", floor="#ECECEC", gridl="#A0A0A0",
                  gear="#B4B4B4", line="#9A9A9A", ifc="#3C8DDE"),
    "dark": dict(o="#D8D8D8", paper="#3A3A3A", fold="#5A5A5A", floor="#3A3A3A", gridl="#7A7A7A",
                 gear="#8C8C8C", line="#8A8A8A", ifc="#5AA7F0"),
}
TEAL, TEAL_L, TEAL_D, TEAL_DD, NAVY = "#2ECFB0", "#7BF2DA", "#22A88F", "#1B7E6D", "#121A2B"
ORANGE, GREEN, BLUE, RED, GREEN_AX, LAND = "#FF8A00", "#2EA44F", "#3C8DDE", "#E5322D", "#35B04A", "#7CCB5B"


def svg(body):
    return '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48">' + body + "</svg>"


def lerp(a, b, t):
    return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)


def badge(kind, p):
    """action badge at the bottom right: up = upload (orange), down = export (green)"""
    fill = ORANGE if kind == "up" else GREEN
    arrow = "M37 30.8 L43 37 H39.3 V43 H34.7 V37 H31 Z" if kind == "up" else "M37 43.2 L43 37 H39.3 V31 H34.7 V37 H31 Z"
    return ('<circle cx="37" cy="37" r="10" fill="%s" stroke="%s" stroke-width="2.2"/>' % (fill, p["o"])
            + '<path d="%s" fill="#FFFFFF"/>' % arrow)


def icon_geobim(p):
    """geoBIM logo: dark globe with a teal ring and four bars, plus the upload badge"""
    s = '<circle cx="21" cy="21" r="18.5" fill="%s" stroke="%s" stroke-width="3"/>' % (NAVY, TEAL)
    s += '<ellipse cx="21" cy="21" rx="8" ry="17" fill="none" stroke="#2C5E5C" stroke-width="1.4"/>'
    s += '<path d="M4.5 21 H37.5" stroke="#2C5E5C" stroke-width="1.4"/>'
    for x, top, c in ((9.5, 14, TEAL), (16, 8, TEAL_L), (22.5, 12, TEAL_D), (29, 16.5, TEAL_DD)):
        s += '<rect x="%g" y="%g" width="5" height="%g" rx="0.8" fill="%s"/>' % (x, top, 29.5 - top, c)
    return svg(s + badge("up", p))


def icon_ion(p):
    """the Earth (Cesium ion streams globes) with the upload badge"""
    s = '<circle cx="21" cy="21" r="18.5" fill="%s"/>' % BLUE
    s += '<path d="M9 12 C12 8 18 7 20 10 C21.5 12.5 18 14 19 17 C20 20 16 22 13.5 20 C11 18 7.5 16 9 12 Z" fill="%s"/>' % LAND
    s += '<path d="M24 23 C27 21 32 22 33 25.5 C34 29 30 33 27 34.5 C24.5 35.5 23 31 24 28 C24.5 26 22.5 24.5 24 23 Z" fill="%s"/>' % LAND
    s += '<path d="M27 8.5 C30 8 34 10.5 34.5 13.5 C32 14 29 12.5 27 8.5 Z" fill="%s"/>' % LAND
    s += '<ellipse cx="21" cy="21" rx="8" ry="18.5" fill="none" stroke="#FFFFFF" stroke-opacity="0.85" stroke-width="1.5"/>'
    s += '<path d="M21 2.5 V39.5 M4.6 13 H37.4 M2.5 21 H39.5 M4.6 29 H37.4" stroke="#FFFFFF" stroke-opacity="0.85" stroke-width="1.5"/>'
    s += '<circle cx="21" cy="21" r="18.5" fill="none" stroke="%s" stroke-width="2.4"/>' % p["o"]
    return svg(s + badge("up", p))


def icon_export(p):
    """a page with "IFC" (drawn as shapes) and the export badge"""
    s = '<path d="M8 3 H28 L37 12 V45 H8 Z" fill="%s" stroke="%s" stroke-width="2.4" stroke-linejoin="round"/>' % (p["paper"], p["o"])
    s += '<path d="M28 3 V12 H37" fill="%s" stroke="%s" stroke-width="2.4" stroke-linejoin="round"/>' % (p["fold"], p["o"])
    b = p["ifc"]
    s += '<rect x="11" y="15" width="3.2" height="12" fill="%s"/>' % b
    s += '<path d="M16.3 15 H24 V18 H19.5 V20 H23 V22.8 H19.5 V27 H16.3 Z" fill="%s"/>' % b
    s += ('<path d="M33 16.6 C31.8 15.4 30.6 15 29.4 15 C26.2 15 24.6 17.6 24.6 21 C24.6 24.4 26.2 27 29.4 27 '
          'C30.6 27 31.8 26.6 33 25.4 L31.2 23.2 C30.6 23.8 30 24 29.4 24 C28.2 24 27.8 22.6 27.8 21 '
          'C27.8 19.4 28.2 18 29.4 18 C30 18 30.6 18.2 31.2 18.8 Z" fill="%s"/>' % b)
    s += '<path d="M11 32 H24 M11 37 H22" stroke="%s" stroke-width="2"/>' % p["line"]
    return svg(s + badge("down", p))


def icon_origin(p):
    """a map pin on a Rhino construction grid with the red X and green Y axis"""
    back, right, front, left = (25, 22), (47, 32.5), (23, 44), (1, 33.5)
    s = '<path d="M%g %g L%g %g L%g %g L%g %g Z" fill="%s" stroke="%s" stroke-width="2" stroke-linejoin="round"/>' % (
        back + right + front + left + (p["floor"], p["o"]))
    for t in (0.25, 0.5, 0.75):
        s += '<path d="M%g %g L%g %g" stroke="%s" stroke-width="1.1"/>' % (lerp(back, right, t) + lerp(left, front, t) + (p["gridl"],))
        s += '<path d="M%g %g L%g %g" stroke="%s" stroke-width="1.1"/>' % (lerp(back, left, t) + lerp(right, front, t) + (p["gridl"],))
    o = lerp(lerp(left, back, 0.25), lerp(front, right, 0.25), 0.25)
    ex = ((right[0] - back[0]) * 0.6, (right[1] - back[1]) * 0.6)
    ey = ((back[0] - left[0]) * 0.6, (back[1] - left[1]) * 0.6)
    s += '<path d="M%g %g L%g %g" stroke="%s" stroke-width="3.2" stroke-linecap="round"/>' % (o + (o[0] + ex[0], o[1] + ex[1], RED))
    s += '<path d="M%g %g L%g %g" stroke="%s" stroke-width="3.2" stroke-linecap="round"/>' % (o + (o[0] + ey[0], o[1] + ey[1], GREEN_AX))
    x, y = o
    s += ('<path d="M{x:g} {y:g} C{a:g} {b:g} {c:g} {d:g} {c:g} {e:g} C{c:g} {f:g} {g:g} {h:g} {x:g} {h:g} '
          'C{i:g} {h:g} {j:g} {f:g} {j:g} {e:g} C{j:g} {d:g} {k:g} {b:g} {x:g} {y:g} Z" fill="{red}" stroke="{o}" '
          'stroke-width="2.2" stroke-linejoin="round"/>').format(
        x=x, y=y, a=x - 4.2, b=y - 7.3, c=x - 8.8, d=y - 11.6, e=y - 18.2, f=y - 23.2, g=x - 4.9, h=y - 27,
        i=x + 4.9, j=x + 8.8, k=x + 4.2, red=RED, o=p["o"])
    s += '<circle cx="%g" cy="%g" r="3.4" fill="#FFFFFF"/>' % (x, y - 18.2)
    return svg(s)


def gear_path(cx, cy, ro, ri, teeth=8, tw=0.42):
    pts = []
    da = 2 * math.pi / teeth
    for i in range(teeth):
        a0 = da * i
        for a, r in ((a0 - da * tw / 2 - 0.06, ri), (a0 - da * tw / 2 + 0.04, ro), (a0 + da * tw / 2 - 0.04, ro), (a0 + da * tw / 2 + 0.06, ri)):
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return "M" + " L".join("%.2f %.2f" % q for q in pts) + " Z"


def icon_settings(p):
    s = '<path d="%s" fill="%s" stroke="%s" stroke-width="2.2" stroke-linejoin="round"/>' % (gear_path(24, 24, 21, 16), p["gear"], p["o"])
    s += '<circle cx="24" cy="24" r="7.5" fill="%s" stroke="%s" stroke-width="2.2"/>' % (TEAL, p["o"])
    return svg(s)


ICONS = {"geobim": icon_geobim, "ion": icon_ion, "export": icon_export, "origin": icon_origin, "settings": icon_settings}


def png(svg_text, size):
    doc = fitz.open(stream=svg_text.encode("utf-8"), filetype="svg")
    z = size / doc[0].rect.width
    pix = doc[0].get_pixmap(matrix=fitz.Matrix(z, z), alpha=True)
    return Image.open(io.BytesIO(pix.tobytes("png"))).convert("RGBA")


def preview(path):
    """light and dark side by side at 16, 24, 32 and 48 px"""
    sizes, pad = (16, 24, 32, 48), 10
    n = len(BUTTONS)
    colw = n * (48 + pad) + pad
    sheet = Image.new("RGBA", (2 * colw, pad + len(sizes) * (48 + pad)), (240, 240, 240, 255))
    sheet.paste((41, 41, 43, 255), (colw, 0, 2 * colw, sheet.height))
    for half, mode in enumerate(("light", "dark")):
        for r, size in enumerate(sizes):
            for i, b in enumerate(BUTTONS):
                im = png(ICONS[b[4]](PAL[mode]), size)
                x = half * colw + pad + i * (48 + pad) + (48 - size) // 2
                y = pad + r * (48 + pad) + (48 - size) // 2
                sheet.alpha_composite(im, (x, y))
    sheet.save(path)
    print("written", path)


def main():
    os.makedirs(ICON_DIR, exist_ok=True)
    for name, fn in ICONS.items():
        for mode, suffix in (("light", ""), ("dark", "_dark")):
            path = os.path.join(ICON_DIR, name + suffix + ".svg")
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(fn(PAL[mode]) + "\n")
    print("written", len(ICONS) * 2, "SVGs to", ICON_DIR)
    for size in (16, 24, 32):
        strip = Image.new("RGBA", (size * len(BUTTONS), size), (0, 0, 0, 0))
        for i, b in enumerate(BUTTONS):
            strip.paste(png(ICONS[b[4]](PAL["light"]), size), (i * size, 0))
        path = os.path.join(PROJ, "Resources", "toolbar-%d.png" % size)
        strip.save(path)
        print("written", path, strip.size)
    if len(sys.argv) > 1:
        preview(sys.argv[1])


if __name__ == "__main__":
    main()
