#!/usr/bin/env python3
"""make_package.py - builds the download zip.

    python3 tools/make_package.py <version> <TLDCoop.dll> <folder with LiteNetLib.dll, NAudio.Core.dll, NAudio.WinMM.dll> [out folder]

Writes <out>/TLD-Shared-World-<version>.zip with Mods/, UserLibs/, licenses/, install.bat, INSTALL.txt and KURULUM.txt.
"""
import os, sys, zipfile

PAGE = "https://github.com/TopraklikGameDevStudio/TLD-Shared-World"
LIBS = ["LiteNetLib.dll", "NAudio.Core.dll", "NAudio.WinMM.dll"]

def main():
    if len(sys.argv) < 4:
        print(__doc__); sys.exit(1)
    version, dll, libs = sys.argv[1], sys.argv[2], sys.argv[3]
    out = sys.argv[4] if len(sys.argv) > 4 else "dist"
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)
    pkg = os.path.join(root, "package")
    os.makedirs(out, exist_ok=True)
    top = f"TLD-Shared-World-{version}"
    path = os.path.join(out, top + ".zip")
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(dll, f"{top}/Mods/TLDCoop.dll")
        for name in LIBS:
            z.write(os.path.join(libs, name), f"{top}/UserLibs/{name}")
        z.write(os.path.join(pkg, "install.bat"), f"{top}/install.bat")
        for name in ("INSTALL.txt", "KURULUM.txt"):
            text = open(os.path.join(pkg, name + ".in"), encoding="utf-8").read()
            text = text.replace("{V}", version).replace("{PAGE}", PAGE).replace("\r\n", "\n").replace("\n", "\r\n")
            z.writestr(f"{top}/{name}", ("﻿" + text).encode("utf-8"))
        z.write(os.path.join(root, "LICENSE"), f"{top}/licenses/LICENSE-TLDCoop.txt")
        for name in sorted(os.listdir(os.path.join(pkg, "licenses"))):
            z.write(os.path.join(pkg, "licenses", name), f"{top}/licenses/{name}")
    print(path)

if __name__ == "__main__":
    main()
