namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// Thrown by <c>GenerationService</c> when a plan fails validation. Errors are
/// keyed by field name so the form can render them inline next to the offending
/// input.
/// </summary>
public class PlanValidationException : Exception
{
    public IReadOnlyDictionary<string, string> Errors { get; }

    public PlanValidationException(IReadOnlyDictionary<string, string> errors)
        : base("The submitted plan has " + errors.Count + " validation error(s).")
    {
        Errors = errors;
    }
}
