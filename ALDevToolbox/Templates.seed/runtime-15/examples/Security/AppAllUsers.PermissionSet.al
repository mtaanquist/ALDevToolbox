namespace {{namespace}};

/// <summary>
/// Read-only permission set for the {{name}} extension. Default for everyday
/// users; pair it with <c>{{prefix}} App Admins</c> for elevated access.
/// </summary>
permissionset 90121 "{{prefix}} App All Users"
{
    Assignable = true;
    Caption = '{{name}} All Users';
    Permissions =
        tabledata "{{prefix}} App Setup" = R;
}
