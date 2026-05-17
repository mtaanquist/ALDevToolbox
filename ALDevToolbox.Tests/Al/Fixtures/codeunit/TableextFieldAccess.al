codeunit 50005 "Tableext Field Access"
{
    procedure AccessExtensionField(var Item: Record Item)
    var
        TableNo: Integer;
    begin
        TableNo := Item.FieldNo("Qty. on Asm. Component");
        if Item."Qty. on Assembly Order" > 0 then
            Message('has assembly order');
        Item.Validate("Qty. on Asm. Component", 0);
    end;
}
