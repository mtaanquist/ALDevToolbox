namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Join row pre-selecting a <see cref="Module"/> for a <see cref="RuntimeTemplate"/>.
/// When an end-user picks the template on the New Workspace form, the modules
/// listed here are ticked automatically — they have to opt out rather than in.
/// Ordering matches the order the admin chose; it is purely cosmetic and does
/// not affect generation.
/// </summary>
public class RuntimeTemplateDefaultModule
{
    public int Id { get; set; }

    /// <summary>Denormalised owning organisation; mirrors the template's value.</summary>
    public int OrganizationId { get; set; }

    public int RuntimeTemplateId { get; set; }

    /// <summary>The owning template. Cascade-deletes with the template.</summary>
    public RuntimeTemplate? Template { get; set; }

    public int ModuleId { get; set; }

    /// <summary>The module being pre-selected. Cascade-deletes with the module.</summary>
    public Module? Module { get; set; }

    /// <summary>Display order within the template's default-module list.</summary>
    public int Ordering { get; set; }
}
