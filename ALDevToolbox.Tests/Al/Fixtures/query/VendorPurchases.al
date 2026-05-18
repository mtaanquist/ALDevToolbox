query 30001 "Vendor Purchases"
{
    QueryType = API;

    elements
    {
        dataitem(QueryElement1; Vendor)
        {
            column(vendorId; SystemId)
            {
            }
            column(vendorNumber; "No.")
            {
            }
            column(name; Name)
            {
            }
            dataitem(QueryElement3; "Vendor Ledger Entry")
            {
                DataItemLink = "Vendor No." = QueryElement1."No.";
                SqlJoinType = LeftOuterJoin;
                column(totalPurchaseAmount; "Purchase (LCY)")
                {
                    Method = Sum;
                }
                filter(dateFilter; "Posting Date")
                {
                }
            }
        }
    }
}
