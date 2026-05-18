page 50000 "Customer Card Sample"
{
    PageType = Card;
    SourceTable = Customer;
    Caption = 'Customer Card Sample';

    layout
    {
        area(content)
        {
            group(General)
            {
                field("No."; Rec."No.")
                {
                    ApplicationArea = All;
                }
                field(Name; Rec.Name)
                {
                    ApplicationArea = All;

                    trigger OnValidate()
                    begin
                        Rec.Validate(Name);
                    end;
                }
            }
            part(Statistics; "Customer Statistics FactBox")
            {
                ApplicationArea = All;
                SubPageLink = "No." = field("No.");
            }
        }
    }

    actions
    {
        area(processing)
        {
            action(Refresh)
            {
                ApplicationArea = All;

                trigger OnAction()
                begin
                    Rec.Modify(true);
                end;
            }
        }
    }
}
