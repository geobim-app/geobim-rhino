# geoBIM.app for Rhino — builds src/GeoBimRhino/GeoBimRhino.rui
#
# One toolbar "geoBIM.app" (in its own toolbar group) with four buttons:
# Upload, Open in browser, Export IFC, Settings. Icons: Lucide (ISC license)
# rendered in the brand colour #2ECFB0 as PNG strips in Resources/
# (toolbar-16/24/32.png, one icon per 16/24/32 px, same order as BUTTONS).
# Layout as in Rhino 8's own default.rui (format 5): the bitmap_item entries
# sit inside every bitmap size, active_tool_bar_group is the id of the toolbar
# shown first. GUIDs are derived from fixed names, so a rebuilt file keeps
# its ids and Rhino treats it as the same toolbar.
#
#   python tools/make_rui.py

import base64
import os
import uuid
from xml.sax.saxutils import escape

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJ = os.path.join(ROOT, "src", "GeoBimRhino")
NS = uuid.UUID("2629d057-640c-483e-996d-942ddc6eb616")   # plugin guid as namespace
NL = "\n"

BUTTONS = [
    # (key, label, tooltip, macro)
    ("upload", "Upload", "Upload to geoBIM.app: selected objects, or all visible ones", "! _GeoBIMUpload"),
    ("open", "Open", "Open this model in geoBIM.app", "! _GeoBIMOpen"),
    ("export", "Export IFC", "Export the selection as IFC 4 for geoBIM.app", "! _GeoBIMExportIfc"),
    ("settings", "Settings", "geoBIM.app settings: server, plugin token, EPSG", "! _GeoBIMSettings"),
]


def gid(name):
    return str(uuid.uuid5(NS, "geobim-rhino/rui5/" + name))


def b64(name):
    with open(os.path.join(PROJ, "Resources", name), "rb") as f:
        return base64.b64encode(f.read()).decode("ascii")


def text(tag, value, indent):
    pad = " " * indent
    return (pad + "<" + tag + ">" + NL
            + pad + "  <locale_1033>" + escape(value) + "</locale_1033>" + NL
            + pad + "</" + tag + ">" + NL)


def build():
    file_id = gid("file")
    group_id = gid("group")
    group_item_id = gid("group-item")
    bar_id = gid("toolbar")
    o = []
    o.append('<?xml version="1.0" encoding="utf-8"?>' + NL)
    o.append('<RhinoUI major_ver="5" minor_ver="0" guid="%s" localize="False" default_language_id="1033" dpi_scale="100">' % file_id + NL)
    o.append("  <extend_rhino_menus />" + NL)
    o.append("  <menus />" + NL)
    o.append("  <tool_bar_groups>" + NL)
    o.append('    <tool_bar_group guid="%s" dock_bar_guid64="%s" active_tool_bar_group="%s" single_file="False" '
             'hide_single_tab="True" point_floating="400,200">' % (group_id, gid("dockbar"), bar_id) + NL)
    o.append(text("text", "geoBIM.app", 6))
    o.append('      <tool_bar_group_item guid="%s" major_version="1" minor_version="1">' % group_item_id + NL)
    o.append(text("text", "geoBIM.app", 8))
    o.append("        <tool_bar_id>%s</tool_bar_id>" % bar_id + NL)
    o.append("      </tool_bar_group_item>" + NL)
    o.append('      <dock_bar_info dpi_scale="100" dock_bar="False" docking="True" horz="True" visible="True" '
             'floating="True" mru_float_style="8192" bar_id="59500" mru_width="1" point_pos="400,200" '
             'float_point="400,200" dock_location_u="59419" dock_location="top" float_size="180,44" />' + NL)
    o.append("    </tool_bar_group>" + NL)
    o.append("  </tool_bar_groups>" + NL)
    o.append("  <tool_bars>" + NL)
    o.append('    <tool_bar guid="%s" bitmap_id="%s">' % (bar_id, gid("bmp-upload")) + NL)
    o.append(text("text", "geoBIM.app", 6))
    for key, label, tip, macro in BUTTONS:
        o.append('      <tool_bar_item guid="%s">' % gid("item-" + key) + NL)
        o.append(text("text", label, 8))
        o.append("        <left_macro_id>%s</left_macro_id>" % gid("macro-" + key) + NL)
        o.append("      </tool_bar_item>" + NL)
    o.append("    </tool_bar>" + NL)
    o.append("  </tool_bars>" + NL)
    o.append("  <macros>" + NL)
    for key, label, tip, macro in BUTTONS:
        o.append('    <macro_item guid="%s" bitmap_id="%s">' % (gid("macro-" + key), gid("bmp-" + key)) + NL)
        o.append(text("text", "geoBIM.app " + label, 6))
        o.append(text("tooltip", tip, 6))
        o.append(text("help_text", tip, 6))
        o.append(text("button_text", label, 6))
        o.append(text("menu_text", label, 6))
        o.append("      <script>%s</script>" % escape(macro) + NL)
        o.append("    </macro_item>" + NL)
    o.append("  </macros>" + NL)
    o.append("  <bitmaps>" + NL)
    for tag, size in (("small_bitmap", 16), ("normal_bitmap", 24), ("large_bitmap", 32)):
        o.append('    <%s item_width="%d" item_height="%d">' % (tag, size, size) + NL)
        for i, (key, label, tip, macro) in enumerate(BUTTONS):
            o.append('      <bitmap_item guid="%s" index="%d" />' % (gid("bmp-" + key), i) + NL)
        o.append("      <bitmap>%s</bitmap>" % b64("toolbar-%d.png" % size) + NL)
        o.append("    </%s>" % tag + NL)
    o.append("  </bitmaps>" + NL)
    o.append("  <scripts />" + NL)
    o.append("</RhinoUI>" + NL)
    path = os.path.join(PROJ, "GeoBimRhino.rui")
    with open(path, "w", encoding="utf-8", newline="\r\n") as f:
        f.write("".join(o))
    print("written", path)


if __name__ == "__main__":
    build()
