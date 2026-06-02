# ALDevToolbox.Tests

xUnit test project for AL Dev Toolbox. Established in Milestone 12 (see
`.design/completed-milestones.md`); the patterns here are the bar M13 onward
should copy rather than reinvent.

## Stack

- **xUnit** for the test runner. `[Fact]` for single cases, `[Theory]` +
  `[InlineData]` when a parameterised shape would otherwise duplicate setup.
- **FluentAssertions** for the assertion DSL. Prefer `.Should().Be(...)`,
  `.Should().Contain(...)`, etc. over raw `Assert.Equal` so failures read like
  prose.
- **Npgsql.EntityFrameworkCore.PostgreSQL** with a real Postgres instance for
  any test that touches the DB (Milestone P4.16). The test fixture
  (`TestDb`) creates a unique database per test class against a process-wide
  shared Postgres host. The host is either:
  - a runner-provided service container — when
    `ALDT_TEST_POSTGRES_CONNECTION` is set, `TestDb` reuses it. CI takes this
    path against a `postgres:18` service container.
  - a Testcontainers `postgres:18-alpine` started on first use — local-dev
    path. Requires Docker on the developer's machine.

  We deliberately do *not* use `Microsoft.EntityFrameworkCore.InMemory` — the
  SQL semantics diverge from real Postgres (constraint enforcement, jsonb
  conversions, timestamptz handling) and the milestone calls out matching
  production.

## Layout

| Folder            | What goes there                                                    |
|-------------------|--------------------------------------------------------------------|
| `Builders/`       | Small static builders that return entities pre-populated with sane |
|                   | defaults. Tests override only the fields they care about.          |
| `Infrastructure/` | Reusable test plumbing — currently just `TestDb`.                  |
| `Generation/`     | `GenerationService` tests (ID-range allocation, mustache).         |
| `Audit/`          | `AuditInterceptor` snapshot tests.                                 |
| `Toml/`           | `TemplateTomlMapper` round-trip tests.                             |
| `Validation/`     | `PlanValidationException` field-key surface tests.                 |
| `Routing/`        | End-to-end endpoint inspection — boots the app via                  |
|                   | `WebApplicationFactory<Program>` and asserts the endpoint map is    |
|                   | unambiguous (catches `MapPost` / `@page` collisions at build time). |
| `Endpoints/`      | HTTP-layer behaviour for the minimal-API handlers in `Program.cs`   |
|                   | (antiforgery enforcement, anonymous → redirect, /site-admin 404).   |
|                   | Boots via the shared `EndpointFactory` in `Infrastructure/`.        |
| `Components/`     | bUnit smoke tests for the highest-risk `.razor` pages. Use          |
|                   | `Bunit.TestContext`; stub auth via `AddTestAuthorization()` and     |
|                   | register real services against `TestDb` rather than mocking a       |
|                   | single method (CLAUDE.md: no interfaces just for tests).            |

When you add a new test file, match the folder. Resist creating new
top-level folders for one-off tests — pick the closest existing bucket.

## Patterns

### Database tests

Use `TestDb`. It creates a fresh Postgres database for the test class against
the shared host, applies migrations, and hands out `AppDbContext` instances
bound to that database. The fixture drops the database on `Dispose`, so
each test class is isolated from every other.

```csharp
public sealed class MyServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Something_works()
    {
        await using var ctx = _db.NewContext();
        // ... arrange, act, assert
    }
}
```

For audit-interceptor tests use `TestDb.NewContextWithAudit(interceptor)`
so the same write path the application uses runs in the test.

### Builders

Builders return entities ready for `_db.NewContext().Add(...)`. They set
required fields (timestamps, defaults, JSON columns) and leave optional
fields at their entity defaults so the test signal stays focused on the
fields the test sets.

```csharp
var template = TemplateBuilder.Default("runtime-15")
    .WithCoreFolder("Source", ("Sample.al", "// content"));
```

If a test needs a shape no builder covers, add a fluent extension to the
existing builder rather than spinning up a new helper class. Keep the
builders small.

### Service tests

Construct services directly with their constructor dependencies — no DI
container in tests. `GenerationService` takes
`(AppDbContext, WorkspaceConfigService, ILogger<GenerationService>)`;
construct each from `_db.NewContext()` and `NullLogger<T>.Instance`.

### Verifying generated artefacts

Open the `MemoryStream` returned by `GenerationService` as a
`ZipArchive`, then read entries by path with `archive.GetEntry("…")` and
parse the contents. Tests should assert against the file contents rather
than poking private helpers; that way a refactor of the internals can't
silently regress the contract.

### Validation tests

Validation tests live in `Validation/`. They throw a
`PlanValidationException` and assert the dictionary contains the expected
field key — never assert against the message string. The form layer keys
errors by field name to render them inline; that contract is what these
tests guard.

## Bar for new code

After Milestone 12, every service method added in M13–M15 ships with tests
for the happy path and for any validation rule it introduces. This isn't a
coverage metric — it's a posture: if the code has a rule, the rule has a
test. When you add a new service, add a sibling folder under `tests/` and
follow the patterns above.
