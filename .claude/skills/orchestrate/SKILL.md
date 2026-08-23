---
name: orchestrate
description: Run a maintainer-feeds-issues session - brief Opus sub-agents per issue, review their output, manage branches/PRs/releases. Use when the maintainer wants to hand over a stream of small issues or observations rather than one task.
---

# Orchestrated fix session

The maintainer feeds you issues, observations, or screenshots one at a time.
You are the orchestrator and reviewer; Opus sub-agents do the implementation.
This protocol was settled in the v8.1.4/v8.2.0/v9.0.0 session (2026-08-23).

## Per issue

1. **Ground the brief yourself first.** Before spawning, read the relevant
   code, the design docs, and the GitHub issue. The brief must contain real
   file paths and line regions, the applicable CLAUDE.md/PROJECT.md rules,
   and the acceptance evidence you expect (build, tests, screenshot).
   A vague brief produces a confident wrong diff.
2. **One issue per agent, Opus, isolated worktree** when anything runs in
   parallel. Agents never push and never commit to shared branches unless
   the brief says to; they report and you review.
3. **Standing instructions to bake into every brief:** fix adjacent small
   problems and list them; report failures honestly (including pre-existing
   ones); house-style grep (`Acme`, ellipsis char, curly quotes); the
   local-environment notes in PROJECT.md (asdf PATH, Testcontainers,
   lock-file churn); render user-facing changes and screenshot them.
4. **Review before relay.** Read the diff yourself, look at the screenshot,
   spot-check any claim that matters. Send the agent back with corrections
   via a follow-up message rather than starting a new agent - it keeps its
   context. Never forward an agent's report unverified.
5. **Relay to the maintainer:** what changed, what you pushed back on,
   unprompted fixes called out explicitly, open judgment calls flagged.

## Branch and merge mechanics

- Confirm the target branch at session start; never assume.
- Branch naming and PR bodies per CLAUDE.md. Each fix is its own
  `fix/`-branch and PR; squash-merge (the `protect-main` ruleset enforces
  squash + linear history on main).
- Merge only green PRs, serially: `gh pr update-branch`, wait for the head
  SHA to change, wait for checks, then merge. Auto-merge is off in this repo.
- CI waits run as background watchers; the `build` job takes ~18 minutes,
  longer than a 10-minute watcher - re-arm rather than assume.
- Tag pushes are the maintainer's: hand them the exact
  `git tag vX.Y.Z <sha> && git push origin vX.Y.Z` line and confirm
  `build.yml` is green on that SHA first. Check the tag doesn't already
  exist - published tags never move.

## Asks that stay with the maintainer

- Fence crossings (`IgnoreQueryFilters()`, secrets, migration discipline,
  new external dependencies) - present the audited, minimised list and get
  an explicit yes.
- Product decisions (removing a feature, changing scope) - propose,
  don't decide.
- Anything the permission system blocks (tag pushes) - hand over the
  command, don't work around it.

## Known frictions

- The `design-review` agent: act on its UX judgments, verify its claims
  about code before acting on them.
- SSH may offer the wrong GitHub key first ("Eressea" noise); pushes still
  land - verify via `gh api .../branches/<name>` rather than trusting the
  transcript.
- Watch scripts must not match their own command line (`pgrep` self-match
  makes a waiter hang forever).
