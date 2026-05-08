namespace {{namespace}};

/// <summary>
/// Runs on upgrade of the {{name}} extension. Use upgrade tags to make each
/// migration step idempotent — see <c>{{prefix}} App Upgrade Tag Definitions</c>.
/// </summary>
codeunit 90101 "{{prefix}} App Upgrade"
{
    Subtype = Upgrade;

    trigger OnUpgradePerCompany()
    begin
        // Per-company upgrade logic for {{moduleName}} goes here.
    end;
}
