# Vendored CodeMirror 6

`wwwroot/code-editor.js` imports these files by relative path. Nothing here is
built by this repo: each file is a pre-bundled ES module downloaded once from
esm.sh, so the app has no runtime CDN dependency and no bundler or npm step
(the "no JS bundler" fence in `CLAUDE.md` holds). See `.design/architecture.md`
→ "Client-side code" for the rationale.

## Pinned versions

| File | Package |
| --- | --- |
| `state.js` | `@codemirror/state@6.4.1` |
| `view.js` | `@codemirror/view@6.34.1` |
| `commands.js` | `@codemirror/commands@6.7.1` |
| `language.js` | `@codemirror/language@6.10.6` |
| `search.js` | `@codemirror/search@6.5.7` |
| `autocomplete.js` | `@codemirror/autocomplete@6.18.3` |
| `lint.js` | `@codemirror/lint@6.8.4` |
| `legacy-modes-toml.js` | `@codemirror/legacy-modes@6.4.1` (entry `mode/toml`) |
| `lang-json.js` | `@codemirror/lang-json@6.0.1` |
| `lezer-highlight.js` | `@lezer/highlight@1.2.1` |
| `lezer-common.js` | `@lezer/common@1.2.3` |

`@lezer/common` has no import in `code-editor.js`; it is vendored because the
other bundles share it.

## The one rule: shared packages must not be duplicated

CodeMirror's extension system uses `instanceof` and facet identity, so a second
copy of `@codemirror/state` (or `view`, `language`, `@lezer/common`,
`@lezer/highlight`) fails at runtime with "Unrecognized extension value in
extension set".

So every download declares that whole set as `external=`: each bundle inlines
only its *private* dependencies (`style-mod`, `crelt`, `w3c-keyname`,
`@lezer/lr`, `@lezer/json`) and leaves the shared ones as bare imports, which we
then rewrite to the sibling files.

## How to bump

Edit the versions below and re-run the script from this directory. It refetches
every file, so bump the whole set together — mixing majors across CodeMirror
packages is not supported upstream.

```sh
EXT='@codemirror/state,@codemirror/view,@codemirror/language,@codemirror/autocomplete,@codemirror/commands,@codemirror/lint,@codemirror/search,@lezer/highlight,@lezer/common'

fetch() { # $1 = output file, $2 = package@version[/entry]
  # The ?bundle&external= URL returns a small stub that re-exports the real
  # bundle; follow it and save that.
  inner=$(curl -sSf "https://esm.sh/$2?bundle&external=$EXT" \
          | grep -o 'export \* from "[^"]*"' | grep -o '/[^"]*')
  curl -sSf "https://esm.sh$inner" -o "$1"
}

fetch state.js             '@codemirror/state@6.4.1'
fetch view.js              '@codemirror/view@6.34.1'
fetch commands.js          '@codemirror/commands@6.7.1'
fetch language.js          '@codemirror/language@6.10.6'
fetch search.js            '@codemirror/search@6.5.7'
fetch autocomplete.js      '@codemirror/autocomplete@6.18.3'
fetch lint.js              '@codemirror/lint@6.8.4'
fetch legacy-modes-toml.js '@codemirror/legacy-modes@6.4.1/mode/toml'
fetch lang-json.js         '@codemirror/lang-json@6.0.1'
fetch lezer-highlight.js   '@lezer/highlight@1.2.1'
fetch lezer-common.js      '@lezer/common@1.2.3'

# Rewrite the bare shared-package imports to the sibling files.
sed -i \
  -e 's#"@codemirror/state"#"./state.js"#g' \
  -e 's#"@codemirror/view"#"./view.js"#g' \
  -e 's#"@codemirror/language"#"./language.js"#g' \
  -e 's#"@lezer/common"#"./lezer-common.js"#g' \
  -e 's#"@lezer/highlight"#"./lezer-highlight.js"#g' \
  *.js

# Drop the trailing sourceMappingURL comments; we do not vendor the .map files,
# and they only produce 404s with devtools open.
sed -i '/^\/\/# sourceMappingURL=/d' *.js

# lang-json bundles @lezer/lr, which reads process.env.LOG behind a `typeof`
# guard to enable a debug log. esm.sh satisfies that by importing its own Node
# process shim; replace it with an empty local one so nothing is fetched.
sed -i 's#^import __Process\$ from "/node/process.mjs";$#var __Process$ = { env: {} };#' lang-json.js
```

Then check, before committing:

```sh
# 1. No file reaches back out to a URL, and no bare or absolute specifiers are left.
grep -n 'from"[^.]' *.js ; grep -n '"/node' *.js ; grep -n 'https://esm.sh' *.js
# 2. Exactly one copy of @codemirror/state (its error string appears once).
grep -c 'Unrecognized extension value' *.js
```

Finally load `/diff` (or an admin TOML editor) in a browser with the network
tab open: the editor must mount with no external requests and no console errors.
