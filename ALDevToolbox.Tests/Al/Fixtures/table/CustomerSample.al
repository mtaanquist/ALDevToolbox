table 50000 "Customer Sample"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20])
        {
            Caption = 'No.';
        }
        field(2; Name; Text[100])
        {
            Caption = 'Name';

            trigger OnValidate()
            begin
                if Name = '' then
                    Error('Name required');
            end;
        }
        field(3; "Bill-to Customer No."; Code[20])
        {
            Caption = 'Bill-to Customer No.';
            TableRelation = Customer."No.";
        }
        field(4; "Sales Document Type"; Enum "Sales Document Type")
        {
            Caption = 'Document Type';
        }
    }

    keys
    {
        key(PK; "No.")
        {
            Clustered = true;
        }
        key(SecondaryIdx; Name)
        {
        }
    }
}
