# AL Workspace Builder

Internal Blazor Server tool for generating Microsoft Dynamics 365 Business Central (AL) workspace skeletons.

This repo currently contains the design documentation only. Implementation has not started.

## What's here

```
docs/                  Design documents — start with docs/README.md
Templates.seed/        Initial template / module / catalogue data, imported on first run
```

## Where to start

If you're implementing this: read `docs/README.md`, then follow the order suggested there. `docs/milestones.md` proposes a build sequence.

If you're reviewing or proposing changes to a template/module: edit the relevant TOML under `Templates.seed/` and open a PR. **Once the app is deployed and running, this folder is only used for the initial seed of an empty database — day-to-day edits happen through the admin UI inside the running app.** Re-seeding from this folder is a deliberate operation, not automatic.

## Status

Design phase. No code yet.
