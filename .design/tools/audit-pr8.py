#!/usr/bin/env python3
"""Audit PR 8: find classes the migrated pages USE but no sheet DEFINES.

The migration's failure mode is silent. A page moves to `.field__label`, the
legacy `.form-field label` rule is retired, and if the new class was mistyped
or never existed in the design layer, nothing errors -- the element just
renders unstyled. Screenshots catch the loud ones; a 12px gap that should be
16px survives.

So: collect every class token the PR 8 files apply, collect every class any
sheet defines (global, scoped, and the design layer), and diff.
"""
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path("/home/mtaanquist/projects/ALDevToolbox")
APP = ROOT / "ALDevToolbox"

# Blazor CSS isolation and third-party mounts define classes we never see in a sheet.
IGNORE_PREFIX = ("cm-", "fa-", "bi-", "ͼ")
IGNORE = {"active", "disabled", "selected", "show", "collapse", "collapsed"}


def defined_classes():
    out = set()
    for css in list(APP.rglob("*.css")):
        if "obj" in css.parts or "bin" in css.parts:
            continue
        text = re.sub(r"/\*.*?\*/", "", css.read_text(), flags=re.S)
        # Only selector text: everything before each `{`, minus declarations.
        for chunk in re.findall(r"([^{}]*)\{", text):
            out |= set(re.findall(r"\.(-?[A-Za-z_][A-Za-z0-9_-]*)", chunk))
    return out


def used_classes(files):
    """class="a b @(...)" plus Blazor's class-building helpers."""
    used = {}
    for f in files:
        text = f.read_text()
        for m in re.finditer(r'class\s*=\s*"([^"]*)"', text):
            for tok in m.group(1).split():
                # Drop interpolations: @(...), @x, "@(a ? "b" : "c")" -- but keep
                # literal fragments the razor expression itself contains.
                if tok.startswith("@") or "@" in tok:
                    continue
                if re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]*", tok or ""):
                    used.setdefault(tok, []).append(f)
        # Blazor builds class strings in C# too: CssClass => "sub-row is-ghost".
        # Only trust strings that look like our BEM, to keep icon names out.
        for m in re.finditer(r'"((?:[a-z][a-z0-9]*__|is-|has-|u-|btn--)[a-z0-9_-]+[^"]*)"', text):
            for tok in m.group(1).split():
                if re.fullmatch(r"[a-z][A-Za-z0-9_-]*", tok):
                    used.setdefault(tok, []).append(f)
    return used


def main():
    base = sys.argv[1] if len(sys.argv) > 1 else "dd2acf1~1"
    changed = subprocess.run(
        ["git", "diff", "--name-only", f"{base}..HEAD"],
        cwd=ROOT, capture_output=True, text=True, check=True).stdout.split()
    files = [ROOT / p for p in changed
             if p.endswith((".razor", ".cs")) and (ROOT / p).exists()
             and "Tests" not in p]

    defined = defined_classes()
    used = used_classes(files)

    missing = {}
    for cls, where in used.items():
        if cls in defined or cls in IGNORE or cls.startswith(IGNORE_PREFIX):
            continue
        missing[cls] = sorted({w.name for w in where})

    print(f"scanned {len(files)} changed .razor/.cs files, "
          f"{len(used)} distinct classes, {len(defined)} defined in sheets\n")
    if not missing:
        print("no undefined classes")
        return
    print(f"!! {len(missing)} classes used but defined in no sheet:\n")
    for cls, where in sorted(missing.items()):
        print(f"  .{cls:<34} {', '.join(where[:4])}")


if __name__ == "__main__":
    main()
