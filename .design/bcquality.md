# BCQuality knowledge base

Microsoft publishes its Business Central quality guidance as a git repository
of markdown: [microsoft/BCQuality](https://github.com/microsoft/BCQuality)
(MIT). We mirror it into Postgres and expose it through two MCP tools, so any
MCP client gets searchable, version-filtered guidance without a local clone.

Before this, the knowledge base was only reachable as a local agent skill
backed by a per-machine clone. That works on one laptop; remote sessions, CI
agents, and every other MCP consumer had no path to it.

## Two settled decisions

- **MCP-only for v1.** There is no browsable page in the web UI. The MCP-parity
  rule is satisfied by there being nothing to mirror: no UI surface exists.
  Adding one later means adding it in the same PR as any new capability, per
  the parity guide in `PROJECT.md`.
- **Track the default branch.** The refresh pulls the repository's default
  branch and records the commit SHA it read. No tag pinning: BCQuality has no
  release cadence to pin to, and the SHA gives the provenance a tag would.

## What the upstream content actually looks like

The repository is content, not code. Its own `skills/read.md` is the schema
contract, and the shape it describes is what the ingest depends on:

- Knowledge articles live at `<layer>/knowledge/<domain>/<slug>.md`, where the
  layer is `microsoft`, `community`, or `custom`. The layer carries authority:
  `custom` overrides `community` overrides `microsoft` when two articles give
  contradictory guidance.
- Every article opens with a six-field YAML frontmatter block, all required:
  `bc-version`, `domain`, `keywords`, `technologies`, `countries`,
  `application-area`. Each is either a scalar or a single-line flow sequence.
- `bc-version` takes four forms: the `[all]` sentinel, an explicit list
  (`[26, 27, 28]`), a closed range (`[26..28]`, which consumers must expand
  before comparing), and an open-ended range (`[26..]`, which is not
  enumerable and matches any target at or above the bound).
- Articles must carry a `## Description` section. `## Best Practice` and
  `## Anti Pattern` are the other two sections a consumer may treat as
  normative; anything else is human context.
- **Articles never contain fenced code blocks.** Sample code ships as sibling
  files named `<slug>.<kind>.<ext>` — `good` and `bad` today, extensible by a
  layer. Without those files an article that says "see sample:
  `no-nested-grids.bad.al`" is a dead end for an agent with no clone, so we
  ingest them too.
- A citation is the article's repo-relative path, optionally with the commit
  SHA. Line numbers are explicitly not stable references.

At the revision this landed against, the repository held 256 knowledge
articles across 16 domains and 407 sample files. 228 of the 256 are marked
`[all]`; the rest use open-ended ranges from `[13..]` to `[27..]`. Every
article carried `countries: [w1]` and `application-area: [all]`, and all but
seven were `technologies: [al]`.

## What we ingest, and what we skip

Only `<layer>/knowledge/**/*.md`. The `skills/` trees (the entry/read/do/write
meta-skills and the per-domain review skills) are agent process instructions
rather than guidance, and they do not carry the six-field schema the filters
depend on — they would be noise in a guidance search. Repo-level documentation
(`README.md`, `agent-consumption.md`, `SECURITY.md`) is skipped for the same
reason, as is a `README.md` sitting inside a knowledge folder.

A file that violates the schema is **skipped, never partially parsed** — that
is what BCQuality's contract requires of consumers. Each skip is recorded with
a reason and logged at warning level. The frontmatter is hand-parsed rather
than handed to a YAML library: the schema is six flat fields, so a real YAML
parser would be a new dependency bought for nothing.

## Schema

Three tables, all system-level: **no `organization_id`, and no EF query
filter**. The content is public and byte-identical for every tenant, so there
is nothing to scope — and because there is no filter, there is nothing for a
read path to escape. No `IgnoreQueryFilters()` call belongs anywhere near
them. (Same reasoning as `oe_file_contents`.)

### `bcquality_articles`

| Column | Why |
|---|---|
| `article_key` (unique) | The repo-relative path. It is BCQuality's own citation key, so it doubles as our upsert key and as the id the MCP tools take and return. |
| `layer`, `domain`, `slug` | From the path and the frontmatter. `domain` is an open enumeration upstream and is never validated against a closed list. |
| `title`, `summary`, `content` | The first `#` heading, the first paragraph of `## Description`, and the whole body with frontmatter stripped. |
| `keywords`, `technologies`, `countries`, `application_areas` | `text[]`, straight from the frontmatter. |
| `keywords_text` | The keywords joined by spaces. It exists only to feed the generated search column: Postgres requires every function in a generated column to be IMMUTABLE and `array_to_string` is only STABLE. |
| `bc_version_raw` | The frontmatter value verbatim, for display and for diagnosing a parse we got wrong. |
| `bc_version_all`, `bc_versions`, `bc_version_from` | The parsed form. `[all]` sets the flag; explicit lists and expanded closed ranges land in `bc_versions`; an open-ended `[N..]` sets `bc_version_from`. A target version matches when the flag is set, when it is in the array, or when it is at or above the bound. |
| `content_hash` | SHA-256 over the article and every sample. A refresh that finds the same hash writes nothing. |
| `search_vector` | A stored generated `tsvector`, GIN-indexed. |
| `first_seen_at`, `updated_at` | `first_seen_at` survives an update. |

### `bcquality_article_samples`

`article_id`, `kind`, `file_name`, `language`, `content`; unique on
(`article_id`, `file_name`), cascade-deleted with the article. Unknown kinds
are stored rather than rejected, as the contract requires. A sample over
256 KB is skipped — samples are short demonstrations, and a larger file means
the naming convention matched something it should not have.

### `bcquality_ingest_state`

One row, id 1. Carries the commit SHA and committer date the articles came
from, the last success and last attempt timestamps, the article count, and the
last error. It is a state record, not a history log; a failed refresh stamps
the attempt and the error without disturbing the articles or the last-success
marker, so a stale mirror is visible without trawling logs.

## Search

Postgres full-text search, which is the first FTS in this codebase. The
`search_vector` column is weighted: title (A), keywords and domain (B),
description (C), body (D). Weighting is what makes ranking mean anything on a
corpus where every article is about AL — without it a passing mention in a body
outranks nothing. The query side uses `websearch_to_tsquery`, so quoted phrases
and `-excluded` terms behave the way a caller expects from a search box rather
than raising a syntax error.

Snippets are built in C# rather than with `ts_headline`: highlighting is the
expensive half of Postgres FTS (it re-parses each document) and the `<b>`
markup it adds is of no use to an agent. The snippet is a plain window of the
body around the first matching term, falling back to the description paragraph
when the match came from the title or the keywords.

The `bc_version` filter takes a BC major version — the value an agent reads
from the target app's `app.json` `application` property. Omitting it means no
version filtering rather than a guess, which matches BCQuality's rule that a
consumer must not silently treat missing context as a match.

## Refresh policy

A single in-process `BackgroundService`, `BcQualityRefreshScheduler`, modelled
on `UsageSnapshotScheduler`: poll every five minutes, do the work only when it
is due. Due means nothing has ever been ingested, or the last **successful**
ingest is more than 24 hours old. Dueness is read from the database, not from a
field, so a restart does not re-clone and a missed slot is caught on the next
poll. A failed attempt backs off for an hour, so an upstream outage cannot turn
the poll interval into a clone interval.

Daily is enough: BCQuality is prose that gains a handful of articles a week.

There is deliberately no queue/worker pair here, unlike the four existing ones.
Those exist to move work off a *request* thread; in an MCP-only v1 nothing on a
request path enqueues a refresh, so a channel with a single producer that is
itself a background service would be machinery with no second caller. If a
"refresh now" button ever lands in the admin UI, that is when the queue earns
its place.

The ingest itself is idempotent: upsert by `article_key`, skip rows whose hash
is unchanged, and hard-delete rows whose file has disappeared upstream. Hard
delete rather than soft: these rows mirror an upstream file rather than
recording something a user authored, so a withdrawn article should stop being
citable. A checkout that yields **no** valid article is refused outright, so a
half-finished clone can never prune a good mirror down to nothing.

Git runs through `IProcessRunner`, the same seam the project-build pipeline
uses — a shallow clone into a scratch directory, then a shallow fetch plus hard
reset on later runs, and a wedged cache is thrown away and re-cloned. No git
library dependency, and the scratch clone is a cache: losing it costs one clone
of a few megabytes, which is why it does not claim a named volume.

## Configuration

| Variable | Default | Purpose |
|---|---|---|
| `DISABLE_BCQUALITY_REFRESH` | unset | `1` keeps the scheduler from starting (tests, CI, offline hosts). The tools then report an empty knowledge base rather than failing. |
| `BCQUALITY_REPO_URL` | `https://github.com/microsoft/BCQuality.git` | Point a deployment at an internal mirror of the same content. |
| `BCQUALITY_CACHE_DIR` | `<temp>/aldt-bcquality` | Where the scratch clone lives. |
| `GIT_PATH` | `git` | Shared with the project-build pipeline. |

## MCP tools

- **`search_bcquality(query, bcVersion?, domain?, limit?)`** — ranked hits,
  each with the article's path as its `Id`, plus title, domain, layer, the raw
  `bc-version`, keywords, the description paragraph, a snippet, and the sample
  count. Capped at 50, default 10. When the mirror has never been populated the
  tool says so rather than returning an empty list, so an agent can tell "no
  matches" from "no mirror yet".
- **`get_bcquality_article(id)`** — the article in full with its applicability
  metadata, every sample file (each cited by its repo-relative path), and the
  commit SHA the copy was read from.

Both are read-only. Neither takes an organisation: the content is
system-level.
