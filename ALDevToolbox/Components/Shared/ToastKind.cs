namespace ALDevToolbox.Components.Shared;

/// <summary>
/// Which of the design system's <c>.toast--*</c> variants a transient message
/// renders as. The variant sets the toast's leading keyline and icon, so it is
/// what tells the user at a glance whether the thing they just did worked.
/// </summary>
public enum ToastKind
{
    /// <summary>It worked. Green keyline, tick.</summary>
    Success,

    /// <summary>It failed and the user may need to do something else. Red keyline, cross.</summary>
    Danger,

    /// <summary>Nothing broke, but nothing happened either. Amber keyline, warning triangle.</summary>
    Warn,
}
