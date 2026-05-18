codeunit 50004 "Namespaced Typed Literal"
{
    procedure UseTypedLiterals()
    var
        AssemblyHeader: Record "Assembly Header";
        TableNo: Integer;
    begin
        TableNo := Database::Microsoft.Assembly.Document."Assembly Header";
        Codeunit::"Sales-Post".Run(AssemblyHeader);
    end;
}
