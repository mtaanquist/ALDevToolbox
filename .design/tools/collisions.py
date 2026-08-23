#!/usr/bin/env python3
"""Every class the design layer defines that a LATER-loading legacy sheet also
defines -- and which properties the legacy sheet therefore wins.

This is issue #542's blind spot generalised. The existing collision test asks
"is this class defined twice?"; the question that actually matters is "which
declarations does the loser never get to apply?", because the legacy sheets
load after the design layer and override property by property.
"""
import pathlib
import re
from collections import defaultdict

WWW = pathlib.Path("/home/mtaanquist/projects/ALDevToolbox/ALDevToolbox/wwwroot")
# Load order from App.razor. Later wins at equal specificity.
ORDER = ["tokens.css", "components.css", "shell.css", "pages.css",
         "pages-forms.css", "base.css", "auth.css", "tools.css", "admin.css"]
DESIGN = {"tokens.css", "components.css", "shell.css", "pages.css", "pages-forms.css"}


def rules(text):
    """(selector, declarations) for every rule, descending into at-rules."""
    i, n = 0, len(text)
    while i < n:
        b = text.find("{", i)
        if b < 0:
            return
        sel = text[i:b]
        head = sel[sel.rfind("}") + 1:].strip()
        depth, j = 1, b + 1
        while j < n and depth:
            depth += text[j] == "{"
            depth -= text[j] == "}"
            j += 1
        if head.startswith("@") and "{" in text[b + 1:j]:
            yield from rules(text[b + 1:j - 1])
        else:
            yield head, text[b + 1:j - 1]
        i = j


def specificity(sel):
    """Rough (id, class, element) count -- enough to compare like with like."""
    s = re.sub(r"::?[a-z-]+(\([^)]*\))?", lambda m: "" if "::" in m.group(0) else ".x", sel)
    return (s.count("#"), s.count(".") + s.count("["), len(re.findall(r"(?:^|[\s>+~])[a-z]", s)))


def main():
    # class -> sheet -> list of (selector, {prop: value})
    seen = defaultdict(lambda: defaultdict(list))
    for name in ORDER:
        f = WWW / name
        if not f.exists():
            continue
        text = re.sub(r"/\*.*?\*/", "", f.read_text(), flags=re.S)
        for sel, decls in rules(text):
            props = {}
            for d in decls.split(";"):
                if ":" in d:
                    k, v = d.split(":", 1)
                    props[k.strip()] = v.strip()
            for c in set(re.findall(r"\.(-?[A-Za-z_][A-Za-z0-9_-]*)", sel)):
                seen[c][name].append((sel.strip(), props))

    hits = []
    for cls, sheets in seen.items():
        design = [s for s in sheets if s in DESIGN]
        legacy = [s for s in sheets if s not in DESIGN]
        if not design or not legacy:
            continue
        # Which design declarations does a later sheet beat?
        beaten = defaultdict(set)
        for ds in design:
            for dsel, dprops in sheets[ds]:
                for ls in legacy:
                    if ORDER.index(ls) <= ORDER.index(ds):
                        continue
                    for lsel, lprops in sheets[ls]:
                        # The design-layer bridge in base.css is `.page`-gated and
                        # exists precisely to restore the component value. It looks
                        # like a collision to the scan above; it is the fix.
                        if lsel.strip().startswith(".page "):
                            continue
                        if specificity(lsel) < specificity(dsel):
                            continue
                        shared = set(dprops) & set(lprops)
                        for p in shared:
                            if dprops[p] != lprops[p]:
                                beaten[f"{ls}  {lsel}"].add(p)
        if beaten:
            hits.append((cls, beaten))

    print(f"{len(hits)} design-layer classes overridden by a later legacy sheet\n")
    for cls, beaten in sorted(hits):
        print(f".{cls}")
        for where, props in sorted(beaten.items()):
            print(f"    {where}\n        wins: {', '.join(sorted(props))}")


if __name__ == "__main__":
    main()
