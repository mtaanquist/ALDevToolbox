namespace {{namespace}};

/// <summary>
/// Admin-level permissions for the {{name}} extension. Grants full access to
/// every object the extension introduces; assign to power users only.
/// </summary>
permissionset 90120 "{{prefix}} App Admins"
{
    Assignable = true;
    Caption = '{{name}} Admins';
    Permissions =
        tabledata "{{prefix}} App Setup" = RIMD;
}
