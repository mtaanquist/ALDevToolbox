namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// Shared defaults used both when seeding a fresh organisation settings row in
/// the application and when computing the column default in the EF migration
/// that introduced <c>code_workspace_json</c>. The migration carries its own
/// literal copy of the JSON so applied migrations can't be retroactively
/// changed by edits here — this constant is the value handed to new orgs and
/// the fallback when an organisation settings row hasn't been persisted yet.
/// </summary>
public static class OrganizationDefaults
{
    /// <summary>
    /// Initial <c>.code-workspace</c> JSON template every new organisation
    /// starts with. The <c>folders</c> array is computed at generation time
    /// from the workspace's extensions, so it is intentionally absent here —
    /// the generator overlays it onto whatever the admin has saved.
    /// </summary>
    public const string CodeWorkspaceJson = """
        {
          "settings": {
            "editor.formatOnSave": true,
            "editor.autoIndent": "full",
            "editor.detectIndentation": false,
            "editor.tabSize": 4,
            "editor.insertSpaces": true,
            "al.codeAnalyzers": [
              "${CodeCop}",
              "${AppSourceCop}",
              "${UICop}"
            ],
            "al.enableCodeAnalysis": true,
            "al.ruleSetPath": "../.assets/rulesets/Company.ruleset.json"
          }
        }
        """;
}
