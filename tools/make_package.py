#!/usr/bin/env python3
"""make_package.py - builds the download zip.

    python3 tools/make_package.py <version> <TLDCoop.dll> <folder with LiteNetLib.dll, NAudio.Core.dll, NAudio.WinMM.dll> [out folder] [MelonLoader folder]

Writes <out>/TLD-Shared-World-<version>.zip with Mods/, UserLibs/, licenses/, install.bat, INSTALL.txt and KURULUM.txt.
With a MelonLoader folder (a working MelonLoader 0.7.3 install: winhttp.dll plus its MelonLoader folder, without
Il2CppAssemblies, Logs, Latest.log or the generator's Config.cfg and cpp2il_out) it also writes
<out>/TLD-Shared-World-<version>-HAZIR-MelonLoader-dahil.zip: everything at the top, to be copied into the game folder.
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
    if len(sys.argv) > 5:
        ml = sys.argv[5]
        ready = os.path.join(out, f"TLD-Shared-World-{version}-HAZIR-MelonLoader-dahil.zip")
        skip = ("Il2CppAssemblies", "Logs", "Latest.log", "cpp2il_out", "Config.cfg")
        with zipfile.ZipFile(ready, "w", zipfile.ZIP_DEFLATED) as z:
            text = open(os.path.join(pkg, "HAZIR-KURULUM-OKU.txt.in"), encoding="utf-8").read()
            text = text.replace("{V}", version).replace("{PAGE}", PAGE).replace("\r\n", "\n").replace("\n", "\r\n")
            z.writestr("HAZIR-KURULUM-OKU.txt", ("\ufeff" + text).encode("utf-8"))
            z.write(os.path.join(ml, "winhttp.dll"), "winhttp.dll")
            for dp, dns, fns in os.walk(os.path.join(ml, "MelonLoader")):
                dns[:] = [d for d in dns if d not in skip]
                for f in fns:
                    if f in skip: continue
                    full = os.path.join(dp, f)
                    z.write(full, os.path.relpath(full, ml).replace(os.sep, "/"))
            z.write(dll, "Mods/TLDCoop.dll")
            for name in LIBS:
                z.write(os.path.join(libs, name), f"UserLibs/{name}")
            z.write(os.path.join(root, "LICENSE"), "licenses/LICENSE-TLDCoop.txt")
            for name in sorted(os.listdir(os.path.join(pkg, "licenses"))):
                z.write(os.path.join(pkg, "licenses", name), f"licenses/{name}")
            z.write(os.path.join(ml, "MelonLoader", "Documentation", "LICENSE.md"), "licenses/MelonLoader-LICENSE.md")
        print(ready)

if __name__ == "__main__":
    main()
