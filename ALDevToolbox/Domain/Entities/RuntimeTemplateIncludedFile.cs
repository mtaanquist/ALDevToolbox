namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Join row linking a <see cref="RuntimeTemplate"/> to one of the
/// organisation's always-included files. New <see cref="OrganizationFile"/>
/// rows are off-by-default — a template only emits the files it has
/// explicitly opted into via this list.
/// </summary>
public class RuntimeTemplateIncludedFile
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int RuntimeTemplateId { get; set; }
    public RuntimeTemplate? RuntimeTemplate { get; set; }

    public int OrganizationFileId { get; set; }
    public OrganizationFile? OrganizationFile { get; set; }

    /// <summary>Position in the admin's reorderable list inside the template editor.</summary>
    public int Ordering { get; set; }
}
