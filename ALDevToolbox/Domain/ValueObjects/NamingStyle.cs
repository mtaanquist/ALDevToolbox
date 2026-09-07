namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// How a transliterated customer name is joined back into a machine name by
/// <see cref="Services.Generation.CustomerNaming"/>. Word boundaries come from
/// the separators in the typed name only, so a run that is already one word
/// (CRONUS) keeps its own casing under <see cref="PascalCase"/>.
/// Specified in <c>.design/customer-naming.md</c>.
/// </summary>
public enum NamingStyle
{
    /// <summary>Words joined with no separator, each capitalised: "JorgensenMoblerAS".</summary>
    PascalCase = 0,

    /// <summary>Like <see cref="PascalCase"/> with a lowercase first letter: "jorgensenMoblerAS".</summary>
    CamelCase = 1,

    /// <summary>Lowercase words joined with hyphens: "jorgensen-mobler-a-s".</summary>
    KebabCase = 2,

    /// <summary>Lowercase words joined with underscores: "jorgensen_mobler_a_s".</summary>
    SnakeCase = 3,

    /// <summary>Lowercase words joined with no separator: "jorgensenmobleras".</summary>
    Lowercase = 4,

    /// <summary>Transliterated only, separators collapsed to one space: "Jorgensen Mobler A S".</summary>
    None = 5,
}
