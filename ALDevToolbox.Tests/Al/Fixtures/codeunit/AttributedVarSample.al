codeunit 50002 "Attributed Var Sample"
{
    procedure UseAttributedVar()
    var
        [SecurityFiltering(SecurityFilter::Filtered)]
        Customer: Record Customer;
    begin
        Customer.Get('C001');
        Customer.Validate(Name);
    end;
}
