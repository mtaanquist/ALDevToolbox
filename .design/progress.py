#!/usr/bin/env python3
"""How far through the design-system migration are we, really?

Counts references to classes that exist ONLY in the legacy sheets (base / auth /
tools / admin) and not in the design layer (tokens / components / shell / pages /
pages-forms). A page with none of those is on the design system.

CAVEAT, and it matters: this sees only the SHARED sheets. A page with its own
.razor.css scores zero no matter what is in there -- Translator.razor reads as 3
stale refs while carrying ~400 lines of private CSS. The scoped-CSS table at the
bottom is the counterweight. Never quote one stale count as "this page is done".

Lives in .design/ rather than scratch/ because the "Where the branch stands"
section of design-migration.md tells the reader to run it, and scratch/ is
gitignored -- a fresh clone would not have it.

Usage: python3 .design/progress.py
"""
import re
import pathlib
import collections

ROOT = pathlib.Path(__file__).resolve().parents[1]
WWW = ROOT / "ALDevToolbox" / "wwwroot"
COMPONENTS = ROOT / "ALDevToolbox" / "Components"

DESIGN = ["tokens.css", "components.css", "shell.css", "pages.css", "pages-forms.css"]
LEGACY = ["base.css", "auth.css", "tools.css", "admin.css"]

# Which planned PR owns a file. First match wins, so order matters.
BUCKETS = [
    ("PR 14  Object Explorer", lambda s, n: "ObjectExplorer" in s or "SourceFileViewer" in n or n == "Compare.razor"),
    ("PR 13  Translator", lambda s, n: "Translator" in n),
    ("GAP    Pipelines / Projects", lambda s, n: "Pipelines" in s or "Projects" in s
     or any(k in n for k in ("Pipeline", "Project", "Release"))),
    ("PR 9   settings / site-admin", lambda s, n: "/SiteAdmin/" in s or "SiteAdmin" in n),
    ("PR 8c  admin edit forms", lambda s, n: "/Admin/" in s or n.startswith("Admin")),
    ("PR 11  auth", lambda s, n: n in {"Login.razor", "Signup.razor", "SignupDetails.razor",
                                       "ForgotPassword.razor", "ResetPassword.razor", "MagicLogin.razor",
                                       "LoginChallenge.razor", "AcceptInvite.razor"}),
    ("PR 9   Account", lambda s, n: n.startswith("Account") or "AccountSecurity" in s),
    ("PR 12  docs / MCP / 404", lambda s, n: "Docs" in s or n in {"Mcp.razor", "NotFound.razor", "Error.razor"}),
    # Routable pages that match no bucket above used to vanish into the catch-all,
    # which reads as miscellany. TemplateDetail.razor sat there unmigrated and
    # unmentioned in the plan until the PR 8 fidelity audit went looking.
    ("UNTRACKED end-user pages", lambda s, n: "/Pages/" in s and "/Shared/" not in s
     and n in {"TemplateDetail.razor", "Home.razor", "Piper.razor", "Compare.razor"}),
    ("shared components + odds", lambda s, n: True),
]


def selector_classes(path):
    """Every class named in a selector in one stylesheet."""
    text = re.sub(r"/\*.*?\*/", "", path.read_text(), flags=re.S)
    selectors = " ".join(m.group(1) for m in re.finditer(r"([^{}]+)\{", text))
    return set(re.findall(r"\.([A-Za-z][A-Za-z0-9_-]*)", selectors))


def used_classes(path):
    """Every literal class name in a component's markup."""
    out = set()
    for m in re.finditer(r'class="([^"]*)"', path.read_text()):
        for token in re.split(r'[\s@()?:"]+', m.group(1)):
            if token and re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]*", token):
                out.add(token)
    return out


def main():
    design, legacy = set(), set()
    for f in DESIGN:
        design |= selector_classes(WWW / f)
    for f in LEGACY:
        legacy |= selector_classes(WWW / f)
    stale_names = legacy - design

    design_lines = sum(len((WWW / f).read_text().splitlines()) for f in DESIGN)
    legacy_lines = sum(len((WWW / f).read_text().splitlines()) for f in LEGACY)

    per_file, clean = [], 0
    buckets = collections.OrderedDict((label, [0, 0]) for label, _ in BUCKETS)
    for p in sorted(COMPONENTS.rglob("*.razor")):
        used = used_classes(p)
        stale = used & stale_names
        if not stale:
            if used:
                clean += 1
            continue
        per_file.append((len(stale), p.name))
        for label, match in BUCKETS:
            if match(str(p), p.name):
                buckets[label][0] += 1
                buckets[label][1] += len(stale)
                break

    total = sum(n for n, _ in per_file)
    print("== stylesheets ==")
    print(f"  design layer  {design_lines:>6,} lines")
    print(f"  legacy layer  {legacy_lines:>6,} lines  "
          f"({legacy_lines / (design_lines + legacy_lines) * 100:.0f}% still legacy)")
    for f in sorted(LEGACY + DESIGN, key=lambda f: -len((WWW / f).read_text().splitlines())):
        print(f"    {f:<18} {len((WWW / f).read_text().splitlines()):>5,}")

    print("\n== components ==")
    print(f"  fully on the design layer   {clean:>4}")
    print(f"  still carrying legacy       {len(per_file):>4}")
    print(f"  total stale class refs      {total:>4}")

    print("\n== remaining work, by weight ==")
    for label, (files, refs) in sorted(buckets.items(), key=lambda kv: -kv[1][1]):
        if refs:
            print(f"  {label:<30} {files:>3} files  {refs:>5} refs")

    print("\n== heaviest files ==")
    for n, name in sorted(per_file, reverse=True)[:12]:
        print(f"  {n:>3}  {name}")

    # The blind spot: private CSS is invisible above.
    scoped = sorted(
        ((len(p.read_text().splitlines()), p.name) for p in COMPONENTS.rglob("*.razor.css")),
        reverse=True)
    print(f"\n== scoped .razor.css ({sum(n for n, _ in scoped):,} lines, INVISIBLE to the count above) ==")
    for n, name in scoped[:8]:
        print(f"  {n:>4}  {name}")


if __name__ == "__main__":
    main()
