# geoBIM.app for Rhino — builds src/GeoBimRhino/GeoBimRhino.rui
#
# One toolbar "geoBIM.app" (in its own toolbar group) with five buttons, each
# with a left and a right click as Rhino's own toolbars have them (Rhino shows
# both in the tooltip): geoBIM.app (upload / open), Cesium ion (upload / token),
# Export (IFC / GLB), Location (set it / show it on the map), Settings
# (settings / connection test).
# Icons: drawn by tools/make_icons.py as SVG for light and dark mode
# (tools/icons/<icon>.svg and <icon>_dark.svg), embedded in <icons> as Rhino 8
# reads them: <icon guid><light><svg …/></light><dark><svg …/></dark></icon>,
# referenced by the macro's bitmap_id, so they stay sharp at every size. The SVG
# must be real child elements: Rhino 8.x showed text-only buttons for escaped
# text and for CDATA (tested 2026-11-20). The PNG strips in Resources/
# (toolbar-16/24/32.png, order of BUTTONS) under the same ids are the bitmap
# fallback.
# Layout as in Rhino 8's own default.rui (format 5): the bitmap_item entries
# sit inside every bitmap size, active_tool_bar_group is the id of the toolbar
# shown first. GUIDs are derived from fixed names, so a rebuilt file keeps
# its ids and Rhino treats it as the same toolbar.
#
#   python tools/make_icons.py && python tools/make_rui.py

import base64
import os
import uuid
from xml.sax.saxutils import escape

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJ = os.path.join(ROOT, "src", "GeoBimRhino")
NS = uuid.UUID("2629d057-640c-483e-996d-942ddc6eb616")   # plugin guid as namespace
NL = "\n"

BUTTONS = [
    # (key, label, tooltip, macro, icon in tools/icons/, right click: (label, tooltip, macro) or None)
    ("upload", "geoBIM.app", "Upload to geoBIM.app: selected objects, or all visible ones",
     "! _GeoBIMUpload", "geobim",
     ("Open in geoBIM.app", "Open this document's model in geoBIM.app", "! _GeoBIMOpen")),
    ("ion", "Cesium ion", "Upload to Cesium ion: selected objects, or all visible ones (needs the model's position, see Location)",
     "! _GeoBIMUploadIon", "ion",
     ("Cesium ion token", "Set the Cesium ion access token (scopes assets:list, assets:read, assets:write)",
      "! _GeoBIMSettings _IonToken")),
    ("export", "Export IFC", "Export the selection as IFC 4",
     "! _GeoBIMExportIfc", "export",
     ("Export GLB", "Export the selection as GLB (binary glTF with PBR materials)", "! _GeoBIMExportGlb")),
    ("origin", "Location", "Location: where the model lies — a point of the model and its map coordinates, latitude/longitude, "
     "address or a click on the map; sets the map origin and Rhino's Location together",
     "! _GeoBIMLocation", "origin",
     ("Show on map", "Show the model's position and outline on the map", "! _-GeoBIMLocation _Show")),
    ("settings", "Settings", "Settings: geoBIM.app server and plugin token, EPSG, Cesium ion token",
     "! _GeoBIMSettings", "settings",
     ("Test connections", "Test the connection to geoBIM.app and to Cesium ion", "! _-GeoBIMSettings _Test _IonTest _Enter")),
]

ICONS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "icons")


def macros():
    """(macro key, label, tooltip, script) for every left and right click"""
    for key, label, tip, macro, icon, right in BUTTONS:
        yield key, label, tip, macro
        if right:
            yield key + "-right", right[0], right[1], right[2]


def gid(name):
    return str(uuid.uuid5(NS, "geobim-rhino/rui5/" + name))


def b64(name):
    with open(os.path.join(PROJ, "Resources", name), "rb") as f:
        return base64.b64encode(f.read()).decode("ascii")


def icon_svg(icon, dark):
    with open(os.path.join(ICONS, icon + ("_dark" if dark else "") + ".svg"), encoding="utf-8") as f:
        return f.read().strip()


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
             'float_point="400,200" dock_location_u="59419" dock_location="top" float_size="270,96" />' + NL)
    o.append("    </tool_bar_group>" + NL)
    o.append("  </tool_bar_groups>" + NL)
    o.append("  <tool_bars>" + NL)
    o.append('    <tool_bar guid="%s" bitmap_id="%s">' % (bar_id, gid("bmp-upload")) + NL)
    o.append(text("text", "geoBIM.app", 6))
    for key, label, tip, macro, icon, right in BUTTONS:
        o.append('      <tool_bar_item guid="%s">' % gid("item-" + key) + NL)
        o.append(text("text", label, 8))
        o.append("        <left_macro_id>%s</left_macro_id>" % gid("macro-" + key) + NL)
        if right:
            o.append("        <right_macro_id>%s</right_macro_id>" % gid("macro-" + key + "-right") + NL)
        o.append("      </tool_bar_item>" + NL)
    o.append("    </tool_bar>" + NL)
    o.append("  </tool_bars>" + NL)
    o.append("  <macros>" + NL)
    for key, label, tip, macro in macros():
        o.append('    <macro_item guid="%s" bitmap_id="%s">' % (gid("macro-" + key), gid("bmp-" + key.replace("-right", ""))) + NL)
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
        for i, (key, label, tip, macro, icon, right) in enumerate(BUTTONS):
            o.append('      <bitmap_item guid="%s" index="%d" />' % (gid("bmp-" + key), i) + NL)
        o.append("      <bitmap>%s</bitmap>" % b64("toolbar-%d.png" % size) + NL)
        o.append("    </%s>" % tag + NL)
    o.append("  </bitmaps>" + NL)
    o.append("  <icons>" + NL)
    for key, label, tip, macro, icon, right in BUTTONS:
        o.append('    <icon guid="%s">' % gid("bmp-" + key) + NL)
        o.append("      <light>%s</light>" % icon_svg(icon, False) + NL)
        o.append("      <dark>%s</dark>" % icon_svg(icon, True) + NL)
        o.append("    </icon>" + NL)
    o.append("  </icons>" + NL)
    o.append("  <scripts />" + NL)
    o.append("</RhinoUI>" + NL)
    path = os.path.join(PROJ, "GeoBimRhino.rui")
    with open(path, "w", encoding="utf-8", newline="\r\n") as f:
        f.write("".join(o))
    print("written", path)


if __name__ == "__main__":
    build()
