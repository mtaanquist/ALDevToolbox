# Example AL files

This folder holds the `.al` files that get seeded into a generated extension when the user enables "Include example AL files."

The structure under here mirrors the `example` field in `template.toml`. For example, this template has:

```toml
[[folders]]
path = "Source/Foundation"
example = "Foundation"
```

…which means every `.al` file under `examples/Foundation/` will be copied into the generated extension's `Source/Foundation/` folder, with mustache variables substituted.

## Available mustache variables

See `docs/templates-and-seeding.md` for the full list. Common ones:

- `{{name}}` — full extension name e.g. "Acme Customer Core"
- `{{shortName}}` — workspace name without spaces e.g. "AcmeCustomer"
- `{{prefix}}` — the AppSourceCop mandatoryPrefix e.g. "EXMPL"
- `{{namespace}}` — the folder path dot-separated e.g. "Source.Foundation"

## What goes here

The team should populate this with sanitised versions of the boilerplate files from existing Core extensions. Suggested first set, based on the team's existing structure:

- `Foundation/AppInstall.Codeunit.al`
- `Foundation/AppUpgrade.Codeunit.al`
- `Foundation/AppUpgradeTagDefinitions.Codeunit.al`
- `Finance/AppSetup.Page.al`
- `Finance/AppSetup.Table.al`
- `Security/AppAdmins.PermissionSet.al`
- `Security/AppAllUsers.PermissionSet.al`

These don't ship with the design docs because the actual file contents come from your existing Core extension. Drop them in here, replace the project-specific bits with mustache variables, and the generator will pick them up.
