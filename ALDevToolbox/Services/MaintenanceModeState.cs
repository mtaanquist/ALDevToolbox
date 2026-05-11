namespace ALDevToolbox.Services;

/// <summary>
/// Process-local maintenance-mode flag. <see cref="BackupService.RestoreAsync"/>
/// flips it on for the duration of an in-place restore; the middleware in
/// <c>Program.cs</c> returns <c>503</c> for every non-SiteAdmin request
/// while the flag is set.
///
/// <para>
/// The flag is intentionally not persisted. If more than one app container
/// is ever pointed at the same database, restore is a SiteAdmin-coordinated
/// downtime — see <c>.design/milestones.md</c>'s M18 entry.
/// </para>
/// </summary>
public sealed class MaintenanceModeState
{
    private volatile string? _reason;
    private DateTime _startedAt;

    public bool IsActive => _reason is not null;
    public string? Reason => _reason;
    public DateTime StartedAtUtc => _startedAt;

    /// <summary>
    /// Flips the flag on. The caller owns the <c>try/finally</c> that calls
    /// <see cref="Exit"/> so a thrown restore never leaves the app stuck in
    /// maintenance.
    /// </summary>
    public void Enter(string reason)
    {
        _reason = reason;
        _startedAt = DateTime.UtcNow;
    }

    public void Exit() => _reason = null;
}
