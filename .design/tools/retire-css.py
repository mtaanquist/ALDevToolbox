#!/usr/bin/env python3
"""Delete CSS rules whose classes no longer appear anywhere in Components/.

Written after a span-based cut ("delete from rule A to rule B") removed 896
unrelated lines from tools.css, because the two markers were nowhere near each
other in the file. Ranges are the wrong unit; a rule is the right one.

Walks the sheet brace by brace so a rule inside an @media is a rule, matches
each rule's selector against the dead-class list, and drops only the rules
where EVERY class named is dead. A rule that also names a live class stays.

Usage:  python3 .design/tools/retire-css.py <sheet.css> <class> [<class> ...]
        --check   report what would go and stop
"""
import pathlib
import re
import sys


def blank_comments(text):
    """Replace every comment's body with spaces, keeping length and newlines.

    The brace walker below counts `{` and `}` literally, and CSS comments
    contain both -- `/* ...read-only /templates/{key}... */` is a real one from
    tools.css, and it made the walker treat the comment as the start of a rule.
    Offsets have to stay valid because they index back into the ORIGINAL text,
    so blank in place rather than deleting.
    """
    out, i, n = list(text), 0, len(text)
    while i < n:
        start = text.find("/*", i)
        if start < 0:
            break
        end = text.find("*/", start + 2)
        end = n if end < 0 else end + 2
        for j in range(start, end):
            if out[j] != "\n":
                out[j] = " "
        i = end
    return "".join(out)


def rules(text, base=0):
    """Yield (start, end, selector) for every rule, nested ones included.

    Offsets are absolute in the ORIGINAL sheet. `base` carries that through the
    recursion: descending into an @media used to yield offsets relative to the
    inner slice, so a dead rule inside one would splice at the wrong place and
    corrupt the file. It never fired before because nothing dead had lived in an
    at-rule yet — the self-test at the bottom of this file now covers it.
    """
    i, n = 0, len(text)
    while i < n:
        brace = text.find("{", i)
        if brace < 0:
            return
        selector = text[i:brace]
        # An at-rule with a block (@media, @supports) is a container: descend.
        head = selector[selector.rfind("}") + 1:].strip()
        depth, j = 1, brace + 1
        while j < n and depth:
            depth += text[j] == "{"
            depth -= text[j] == "}"
            j += 1
        if head.startswith("@") and "{" in text[brace + 1:j]:
            yield from rules(text[brace + 1:j - 1], base + brace + 1)
            i = j
            continue
        # lstrip so the span starts at the selector itself, not at the previous
        # rule's `}`. Everything between them -- blank lines, and the section
        # comment that explains the rules AROUND this one -- stays put.
        yield base + brace - len(selector.lstrip()), base + j, head
        i = j


SELF_TEST_INPUT = """\
/* ---------- Section (read-only /templates/{key}) ---------- */

.doomed { color: red; }

.keeper { color: green; }

@media (max-width: 700px) {
    .doomed { color: blue; }
    .keeper { color: teal; }
}
"""

SELF_TEST_EXPECTED = """\
/* ---------- Section (read-only /templates/{key}) ---------- */

.keeper { color: green; }

@media (max-width: 700px) {
    .keeper { color: teal; }
}
"""


def self_test():
    """Both bugs this tool has actually had, in one fixture.

    The comment carries a `{` (a real one from tools.css said
    "read-only /templates/{key}"), which the brace walker read as the start of a
    rule and spliced through. And the dead rule inside the @media exercises the
    recursion's offsets, which used to be relative to the inner slice.
    Run before trusting it on a sheet you care about.
    """
    import tempfile
    with tempfile.NamedTemporaryFile("w", suffix=".css", delete=False) as f:
        f.write(SELF_TEST_INPUT)
        path = f.name
    sys.argv = ["retire-css", path, "doomed"]
    main()
    got = pathlib.Path(path).read_text()
    pathlib.Path(path).unlink()
    if got != SELF_TEST_EXPECTED:
        sys.exit(f"!! self-test FAILED\n--- got ---\n{got}\n--- want ---\n{SELF_TEST_EXPECTED}")
    print("self-test ok: comment with a brace survived, nested rule dropped")


def main():
    if "--self-test" in sys.argv:
        sys.argv.remove("--self-test")
        self_test()
        return
    args = [a for a in sys.argv[1:] if a != "--check"]
    check = "--check" in sys.argv
    sheet, dead = pathlib.Path(args[0]), set(args[1:])
    text = sheet.read_text()

    drops = []
    # Walk a comment-blanked copy; the offsets it yields index into `text`.
    for start, end, selector in rules(blank_comments(text)):
        named = set(re.findall(r"\.([A-Za-z][A-Za-z0-9_-]*)", selector))
        if named and named <= dead:
            drops.append((start, end, selector.strip()))

    for _, _, selector in drops:
        print("  drop", selector.replace("\n", " ")[:78])
    if check or not drops:
        return

    # Right to left so earlier offsets stay valid.
    for start, end, _ in reversed(drops):
        # Take the blank line that followed the rule with it. The span already
        # starts at the selector, so what precedes it is untouched and the
        # survivors cannot end up glued (`}.next-selector {`), which is what
        # happened 12 times across PR 8 before anyone noticed.
        while end < len(text) and text[end] in " \n":
            end += 1
        text = text[:start] + text[end:]

    depth = 0
    for c in text:
        depth += c == "{"
        depth -= c == "}"
    if depth:
        sys.exit(f"!! braces unbalanced ({depth}) - not writing")
    sheet.write_text(re.sub(r"\n{3,}", "\n\n", text))
    print(f"  -> {sheet.name}: dropped {len(drops)} rules")


if __name__ == "__main__":
    main()
