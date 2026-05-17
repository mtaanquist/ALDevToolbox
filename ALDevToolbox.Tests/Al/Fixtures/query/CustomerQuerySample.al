query 50000 "Customer Query Sample"
{
    Caption = 'Customer Query Sample';
    QueryType = Normal;

    elements
    {
        dataitem(CustomerData; Customer)
        {
            column(CustomerNo; "No.")
            {
            }
            column(CustomerName; Name)
            {
            }
            filter(PhoneFilter; "Phone No.")
            {
            }
        }
    }
}
