enum 50000 "Sales Document Type Sample"
{
    Extensible = true;
    Caption = 'Sales Document Type Sample';

    value(0; Quote)
    {
        Caption = 'Quote';
    }
    value(1; Order)
    {
        Caption = 'Order';
    }
    value(2; Invoice)
    {
        Caption = 'Invoice';
    }
    value(3; "Credit Memo")
    {
        Caption = 'Credit Memo';
    }
}
