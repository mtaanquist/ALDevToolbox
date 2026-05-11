namespace {{namespace}};

/// <summary>
/// Singleton setup table for the {{moduleName}} module. Keep configuration
/// flags here so they can be edited from <c>{{prefix}} App Setup</c>.
/// </summary>
table 90110 "{{prefix}} App Setup"
{
    Caption = '{{name}} Setup';
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Primary Key"; Code[10])
        {
            Caption = 'Primary Key';
            DataClassification = SystemMetadata;
        }
        field(10; "Enabled"; Boolean)
        {
            Caption = 'Enabled';
            DataClassification = CustomerContent;
        }
    }

    keys
    {
        key(PK; "Primary Key") { Clustered = true; }
    }
}
