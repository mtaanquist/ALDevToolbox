tableextension 50000 "Customer Ext Sample" extends Customer
{
    fields
    {
        field(50000; "Loyalty Points"; Integer)
        {
            Caption = 'Loyalty Points';
        }
        field(50001; "Preferred Contact"; Code[20])
        {
            Caption = 'Preferred Contact';
            TableRelation = Customer."No.";
        }
    }

    procedure AddLoyalty(Points: Integer)
    begin
        "Loyalty Points" += Points;
        Modify(true);
    end;
}
