namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Serialises the test classes that reach for a process-wide environment
/// variable (<c>BACKUPS_DIR</c>, the <c>SMTP_*</c> block) in their constructor
/// and restore it on dispose.
///
/// <para>Those classes set a variable the service under test reads at call
/// time, so two of them running at once means one class's directory or SMTP
/// host is live while the other is asserting against its own. The failure is
/// not deterministic: it needs the two constructors to overlap, which is why
/// the suite got away with it while every fixture spent about four seconds
/// applying migrations first. Cloning the schema from a template (#728) cut
/// that setup to a fraction of a second, so the classes now overlap tightly
/// and the race fires most runs, as leftover files under the real
/// <c>/var/lib/aldevtoolbox/backups</c>.</para>
///
/// <para>This is the same device <see cref="EndpointFactoryCollection"/> uses
/// for the connection string, and the same reasoning. The proper fix is for
/// those services to take their directory through options rather than the
/// environment; until then, keep new classes that mutate the environment in
/// this collection.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class EnvironmentVariableCollection
{
    public const string Name = "process-wide environment variables";
}
