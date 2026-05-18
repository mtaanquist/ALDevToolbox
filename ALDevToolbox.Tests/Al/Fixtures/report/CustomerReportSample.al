report 50000 "Customer Report Sample"
{
    Caption = 'Customer Report Sample';
    UsageCategory = ReportsAndAnalysis;
    ApplicationArea = All;

    dataset
    {
        dataitem(CustomerLoop; Customer)
        {
            column(CustomerNo; "No.")
            {
            }
            column(CustomerName; Name)
            {
            }
            column(PhoneNo; "Phone No.")
            {
            }

            trigger OnAfterGetRecord()
            begin
                if Name = '' then
                    CurrReport.Skip();
            end;
        }
    }

    requestpage
    {
        layout
        {
            area(content)
            {
                group(Options)
                {
                    field(IncludeBlocked; IncludeBlocked)
                    {
                        ApplicationArea = All;
                        Caption = 'Include blocked';
                    }
                }
            }
        }
    }

    var
        IncludeBlocked: Boolean;
}
