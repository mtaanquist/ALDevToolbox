interface "ICustomer Handler Sample"
{
    procedure HandleCustomer(var Customer: Record Customer): Boolean;
    procedure GetDocumentType(): Enum "Sales Document Type";
    procedure PostOrder(var SalesHeader: Record "Sales Header"): Boolean;
}
