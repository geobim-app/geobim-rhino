"""Builds the Rhino Package Manager (Yak) package of geoBIM.app for Rhino.

    python tools/make_yak.py              build Release, then package
    python tools/make_yak.py --no-build   package the existing Release build

Stages GeoBimRhino.rhp, GeoBimRhino.rui, icon.png, LICENSE and manifest.yml (version from
the .csproj) in dist/yak/ and runs `Yak.exe build --platform win` there. The .yak file ends up
in dist/. Nothing is pushed: try the package on the test server first,

    "C:\\Program Files\\Rhino 8\\System\\Yak.exe" push --source https://test.yak.rhino3d.com dist\\<file>.yak

and push to the public server only on purpose (a pushed version is public and cannot be
replaced or deleted, only hidden with `yak yank`).
"""
import argparse
import glob
import os
import re
import shutil
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJ = os.path.join(REPO, "src", "GeoBimRhino", "GeoBimRhino.csproj")
BUILD = os.path.join(REPO, "src", "GeoBimRhino", "bin", "Release", "net7.0")
YAK_SRC = os.path.join(REPO, "tools", "yak")
DIST = os.path.join(REPO, "dist")
STAGE = os.path.join(DIST, "yak")
YAK = r"C:\Program Files\Rhino 8\System\Yak.exe"
PLATFORM = "win"   # Windows only until the plugin has been tested on a Mac


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--no-build", action="store_true", help="use the existing Release build")
    args = ap.parse_args()

    version = re.search(r"<Version>([^<]+)</Version>", open(PROJ, encoding="utf-8").read()).group(1).strip()
    if not args.no_build:
        r = subprocess.run(["dotnet", "build", PROJ, "-c", "Release"])
        if r.returncode != 0:
            sys.exit("build failed (a running Rhino with the plugin loaded locks the .rhp: close it, or use --no-build)")

    files = {
        "GeoBimRhino.rhp": os.path.join(BUILD, "GeoBimRhino.rhp"),
        "GeoBimRhino.rui": os.path.join(BUILD, "GeoBimRhino.rui"),
        "icon.png": os.path.join(YAK_SRC, "icon.png"),
        "LICENSE.txt": os.path.join(REPO, "LICENSE"),
    }
    missing = [src for src in files.values() if not os.path.exists(src)]
    if missing:
        sys.exit("missing: " + ", ".join(missing))

    shutil.rmtree(STAGE, ignore_errors=True)
    os.makedirs(STAGE)
    for name, src in files.items():
        shutil.copy2(src, os.path.join(STAGE, name))
    manifest = open(os.path.join(YAK_SRC, "manifest.yml"), encoding="utf-8").read()
    manifest = re.sub(r"(?m)^version: .*$", "version: " + version, manifest)
    open(os.path.join(STAGE, "manifest.yml"), "w", encoding="utf-8").write(manifest)

    for old in glob.glob(os.path.join(STAGE, "*.yak")):
        os.remove(old)
    r = subprocess.run([YAK, "build", "--platform", PLATFORM], cwd=STAGE)
    if r.returncode != 0:
        sys.exit("yak build failed")
    built = glob.glob(os.path.join(STAGE, "*.yak"))
    if len(built) != 1:
        sys.exit("expected one .yak file, found %d" % len(built))
    target = os.path.join(DIST, os.path.basename(built[0]))
    if os.path.exists(target):
        os.remove(target)
    shutil.move(built[0], target)
    print("package:", target)
    print("test server:", '"%s" push --source https://test.yak.rhino3d.com "%s"' % (YAK, target))


if __name__ == "__main__":
    main()
