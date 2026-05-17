codeunit 50003 "Namespaced Type Sample"
{
    procedure UseFullyQualified()
    var
        SalesHeader: Record Microsoft.Sales.History."Sales Header";
        Customer: Record Customer;
    begin
        SalesHeader.Get(SalesHeader."Document Type"::Order, '');
        Customer.Get('');
    end;
}
