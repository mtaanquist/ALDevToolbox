codeunit 50000 "Simple Helper"
{
    procedure PostSalesOrder(SalesHeaderNo: Code[20])
    var
        SalesHeader: Record "Sales Header";
        SalesPost: Codeunit "Sales-Post";
    begin
        SalesHeader.Get(SalesHeader."Document Type"::Order, SalesHeaderNo);
        SalesHeader.Validate("Sell-to Customer No.", '');
        SalesPost.Run(SalesHeader);
    end;

    procedure CreateCustomer(CustomerNo: Code[20]; CustomerName: Text[100])
    var
        Customer: Record Customer;
    begin
        Customer.Init();
        Customer."No." := CustomerNo;
        Customer.Validate(Name, CustomerName);
        Customer.Insert(true);
    end;
}
