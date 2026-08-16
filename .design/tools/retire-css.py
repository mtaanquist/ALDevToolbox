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


def rules(text):
    """Yield (start, end, selector) for every rule, nested ones included."""
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
            yield from rules(text[brace + 1:j - 1])
            i = j
            continue
        yield brace - len(selector), j, head
        i = j


def main():
    args = [a for a in sys.argv[1:] if a != "--check"]
    check = "--check" in sys.argv
    sheet, dead = pathlib.Path(args[0]), set(args[1:])
    text = sheet.read_text()

    drops = []
    for start, end, selector in rules(text):
        named = set(re.findall(r"\.([A-Za-z][A-Za-z0-9_-]*)", selector))
        if named and named <= dead:
            drops.append((start, end, selector.strip()))

    for _, _, selector in drops:
        print("  drop", selector.replace("\n", " ")[:78])
    if check or not drops:
        return

    # Right to left so earlier offsets stay valid.
    for start, end, _ in reversed(drops):
        # Take the blank line that followed the rule with it.
        while end < len(text) and text[end] in " \n":
            end += 1
        # A rule's `start` is the previous rule's `}`, so it carries the
        # whitespace that separated them. Dropping it verbatim glues the two
        # survivors together -- `}.next-selector {`. Valid CSS, unreadable
        # diff, and it happened 12 times across PR 8 before anyone noticed.
        keep = text[start:text.find("{", start)]
        lead = keep[:len(keep) - len(keep.lstrip())]
        text = text[:start] + ("\n\n" if "\n" in lead else "") + text[end:]

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
