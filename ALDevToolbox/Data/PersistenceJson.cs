using System.Text.Json;

namespace ALDevToolbox.Data;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for every persistence-side JSON
/// round-trip — the <c>defaults_json</c> / <c>app_source_cop_json</c> columns
/// in <see cref="AppDbContext"/>, the snapshot column written by
/// <see cref="AuditInterceptor"/>, and the validation pre-parse in
/// <see cref="ALDevToolbox.Services.TemplateService"/>. Keeping these on a
/// single static avoids the previous risk of one site silently drifting (e.g.
/// switching to <c>WriteIndented = true</c> or a different naming policy) and
/// breaking JSON column round-trips.
/// </summary>
internal static class PersistenceJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };
}
