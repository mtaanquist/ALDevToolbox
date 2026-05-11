namespace ALDevToolbox.Services;

/// <summary>
/// Singleton flag flipped to <c>true</c> once startup work (EF migrations + the
/// first-run seed) has finished. <c>/readyz</c> only goes green once it does,
/// which keeps reverse proxies from sending traffic mid-migration.
/// </summary>
public sealed class StartupReadinessState
{
    private volatile bool _ready;

    public bool IsReady => _ready;

    public void MarkReady() => _ready = true;
}
