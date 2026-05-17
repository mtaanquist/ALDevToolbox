codeunit 50001 "Global Variable Usage"
{
    procedure Increment()
    begin
        Counter += 1;
    end;

    procedure Reset()
    begin
        Counter := 0;
    end;

    procedure Total(): Integer
    begin
        exit(Counter);
    end;

    var
        Counter: Integer;
}
