using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services;

/// <summary>
/// Constants for the platform-default workspace files (`.gitignore`, the
/// shared ruleset, the README stub) that ship as <see cref="Domain.Entities.OrganizationFile"/>
/// rows for every organisation. Centralising them here so the migration
/// that backfills existing orgs, the seeding path for new orgs in
/// <see cref="AccountService"/>, and the fresh-DB bootstrap in
/// <c>StartupTasks</c> all read from one place.
/// </summary>
/// <remarks>
/// Originally these were emitted from embedded resources at generation
/// time. The user-side feedback was that the live preview showed files
/// the admin hadn't opted into — moving them onto the per-template
/// included-files join makes the emission match the user's intent
/// while leaving them on by default for existing setups via the
/// migration backfill.
/// </remarks>
public static class PlatformOrganizationFiles
{
    public sealed record Definition(
        string Path,
        string Content,
        bool MustacheEnabled,
        int Ordering,
        OrganizationFileScope Scope = OrganizationFileScope.WorkspaceRoot);

    public const string GitignorePath = ".gitignore";
    public const string RulesetPath = ".assets/rulesets/Company.ruleset.json";
    public const string ReadmePath = "README.md";
    public const string AppJsonPath = "app.json";

    /// <summary>
    /// Canonical per-extension <c>app.json</c> template. Mustache-substituted
    /// per extension at generation time; the variables resolve through the
    /// renderer's per-extension context. Admins can edit / override the body
    /// at <c>/admin/templates/files</c>.
    /// </summary>
    public const string DefaultAppJsonContent = """
        {
            "id": "{{extension_id}}",
            "name": "{{extension_name}}",
            "publisher": "{{publisher}}",
            "version": "0.0.0.1",
            "brief": "{{brief}}",
            "description": "{{description}}",
            "privacyStatement": "",
            "EULA": "",
            "help": "",
            "url": "{{url}}",
            "logo": "{{logo_path}}",
            "dependencies": {{dependencies_array}},
            "screenshots": [],
            "platform": "{{platform_version}}",
            "application": "{{application_version}}",
            "target": "Cloud",
            "idRanges": {{id_ranges_array}},
            "resourceExposurePolicy": {
                "allowDebugging": true,
                "allowDownloadingSource": true,
                "includeSourceInSymbolFile": true
            },
            "runtime": "{{runtime}}",
            "features": [
                "NoImplicitWith",
                "TranslationFile"
            ]
        }
        """;

    public static IReadOnlyList<Definition> All { get; } = new Definition[]
    {
        new(
            Path: GitignorePath,
            Content: """
                # AL build output
                *.app
                .alcache/
                .alpackages/
                .altestrunner/
                .snapshots/
                .netpackages/
                rad.json
                *.tmp

                # VS Code
                .vscode/launch.json
                .vscode/.alcache/

                # OS
                Thumbs.db
                ehthumbs.db
                Desktop.ini
                .DS_Store
                """,
            MustacheEnabled: false,
            Ordering: 1000),
        new(
            Path: RulesetPath,
            Content: """
                {
                    "name": "Company default rules",
                    "description": "Baseline ruleset shipped by AL Dev Toolbox. Customise as your team's standards evolve.",
                    "generalAction": "Default",
                    "includedRuleSets": [],
                    "rules": [
                        {
                            "id": "AA0008",
                            "action": "Warning",
                            "justification": "Function calls should have parentheses."
                        },
                        {
                            "id": "AA0072",
                            "action": "Warning",
                            "justification": "Object names should not exceed 30 characters."
                        },
                        {
                            "id": "AA0074",
                            "action": "Warning",
                            "justification": "TextConst variable names should follow naming conventions."
                        },
                        {
                            "id": "AA0137",
                            "action": "Warning",
                            "justification": "Variables should be used."
                        },
                        {
                            "id": "AA0150",
                            "action": "Warning",
                            "justification": "Internal procedures should be marked as such."
                        },
                        {
                            "id": "AA0228",
                            "action": "Warning",
                            "justification": "Procedures with a Cyclomatic complexity over 8 should be refactored."
                        },
                        {
                            "id": "AS0011",
                            "action": "Warning",
                            "justification": "Each AL object should declare a namespace."
                        }
                    ]
                }
                """,
            MustacheEnabled: false,
            Ordering: 1001),
        new(
            Path: ReadmePath,
            Content: """
                # {{workspace_name}}

                Generated by AL Dev Toolbox.
                """,
            MustacheEnabled: true,
            Ordering: 1002),
        // app.json — one copy per extension. Ordering = 0 so it lands at the
        // top of each extension folder when WriteOrgFiles iterates over the
        // template's IncludedFiles join in order. Admins can replace the body
        // wholesale on /admin/templates/files; the mustache substitution
        // tolerates whatever shape they prefer.
        new(
            Path: AppJsonPath,
            Content: DefaultAppJsonContent,
            MustacheEnabled: true,
            Ordering: 0,
            Scope: OrganizationFileScope.EveryExtension),
    };
}
