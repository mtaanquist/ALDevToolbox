#!/usr/bin/env python3
"""The other half of the audit: classes a legacy sheet still defines that
nothing applies any more.

`retire-css.py` deletes what you tell it to. This finds what to tell it. A
class counts as live if it appears in any .razor, .razor.css, .cs or .js --
a lot of tools.css is applied by CodeMirror, not by markup.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path("/home/mtaanquist/projects/ALDevToolbox/ALDevToolbox")
SHEETS = ["tools.css", "base.css", "admin.css", "auth.css", "pages.css",
          "pages-forms.css", "components.css", "shell.css", "tokens.css"]


def live_classes():
    live = set()
    for pat in ("**/*.razor", "**/*.cs", "**/*.js", "Components/**/*.css"):
        for f in ROOT.glob(pat):
            if "obj" in f.parts or "bin" in f.parts:
                continue
            live |= set(re.findall(r"[A-Za-z0-9_-]+", f.read_text()))
    return live


def main():
    only = sys.argv[1:] or SHEETS
    live = live_classes()
    for name in only:
        sheet = ROOT / "wwwroot" / name
        if not sheet.exists():
            continue
        text = re.sub(r"/\*.*?\*/", "", sheet.read_text(), flags=re.S)
        defined = {}
        for chunk in re.findall(r"([^{}]*)\{", text):
            for c in re.findall(r"\.(-?[A-Za-z_][A-Za-z0-9_-]*)", chunk):
                defined.setdefault(c, 0)
                defined[c] += 1
        dead = sorted(c for c in defined if c not in live)
        print(f"\n== {name}: {len(defined)} classes, {len(dead)} dead ==")
        for c in dead:
            print(f"  .{c}  ({defined[c]} rules)")


if __name__ == "__main__":
    main()
