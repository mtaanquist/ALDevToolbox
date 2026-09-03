using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ALDevToolbox.Services;

/// <summary>
/// Recognising the database errors we deliberately let happen. Several services
/// pre-check a uniqueness rule for a friendly inline message and leave the unique
/// index as the backstop for the race between the check and the save; catching
/// that backstop is what turns a lost race into the same field-keyed
/// <see cref="PlanValidationException"/> the pre-check produces, instead of a 500.
/// See issue #702.
/// </summary>
internal static class DbErrors
{
    /// <summary>
    /// True when <paramref name="ex"/> wraps a Postgres unique-constraint violation
    /// (SQLSTATE 23505). Any other <see cref="DbUpdateException"/> is a real fault
    /// and must propagate rather than be translated or swallowed.
    /// </summary>
    internal static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
