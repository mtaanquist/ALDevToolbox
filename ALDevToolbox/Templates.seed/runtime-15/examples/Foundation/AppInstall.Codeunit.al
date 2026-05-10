namespace {{namespace}};

/// <summary>
/// Runs once when the {{name}} extension is installed in a tenant. Hook
/// post-install setup here (defaults, telemetry signups, etc.).
/// </summary>
codeunit 90100 "{{prefix}} App Install"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    begin
        // First-install logic for {{moduleName}} goes here.
    end;
}
