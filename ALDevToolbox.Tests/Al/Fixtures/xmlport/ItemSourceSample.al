xmlport 50000 "Item Source Sample"
{
    Caption = 'Item Source Sample';
    Direction = Both;
    Format = Xml;

    schema
    {
        textelement(Root)
        {
            tableelement(ItemRow; Item)
            {
                fieldattribute(No; ItemRow."No.")
                {
                }
                fieldelement(Description; ItemRow.Description)
                {
                }
            }
        }
    }
}
