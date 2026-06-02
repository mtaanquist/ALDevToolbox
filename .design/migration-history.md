# Migration history

The authoritative list of schema changes is the migrations directory itself:

```
ALDevToolbox/Data/Migrations/
```

Each migration is named `<timestamp>_<Name>.cs`. Read its class XML doc for the per-migration rationale — that's where the "why" lives, kept next to the code so it can't drift from it. List them in order with:

```
ls ALDevToolbox/Data/Migrations/*.cs | grep -v Designer
```

> This page used to hand-maintain a one-line summary per migration. That table fell out of sync every time a migration was added (it listed ~12 of the 50+ on disk), so it was retired in favour of pointing at the source. Don't reintroduce a hand-maintained list here — annotate the migration's own XML doc instead.

## Conventions

- **One milestone per migration where possible.** A migration with two unrelated concerns is a code-review red flag.
- **Migration files are immutable once merged.** New schema changes go in a new migration, never as edits to a shipped one.
- **`MigrateAsync` runs at startup**; the app refuses to serve traffic if it can't apply pending migrations. There is no separate migrate-then-deploy step.
- **The model snapshot (`AppDbContextModelSnapshot.cs`) is partly hand-maintained** for the M14+ migrations because EF's reverse-engineering doesn't capture every detail of multi-tenant query filters. The pending-changes warning is suppressed in `Program.cs`; real schema drift still surfaces at `MigrateAsync` time. Run `dotnet ef migrations add <Name>` and review what EF generated before committing.
