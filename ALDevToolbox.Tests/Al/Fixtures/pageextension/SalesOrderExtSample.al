pageextension 50000 "Sales Order Ext Sample" extends "Sales Order"
{
    layout
    {
        addafter("Sell-to Customer No.")
        {
            field("Bill-to Customer No."; Rec."Sell-to Customer No.")
            {
                ApplicationArea = All;
                Caption = 'Bill-to copy';
            }
        }
    }

    actions
    {
        addlast(processing)
        {
            action(NotifyCustomer)
            {
                ApplicationArea = All;

                trigger OnAction()
                var
                    Customer: Record Customer;
                begin
                    if Customer.Get(Rec."Sell-to Customer No.") then
                        Customer.Validate(Name);
                end;
            }
        }
    }
}
