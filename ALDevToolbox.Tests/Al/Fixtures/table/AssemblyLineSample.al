table 50001 "Assembly Line Sample"
{
    fields
    {
        field(1; "Document Type"; Enum "Sales Document Type")
        {
        }
        field(2; "No."; Code[20])
        {
        }
    }

    trigger OnDelete()
    var
        Helper: Codeunit "Sales-Post";
    begin
        Helper.Run("Document Type".AsInteger());
    end;
}
