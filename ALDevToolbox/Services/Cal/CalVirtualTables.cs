namespace ALDevToolbox.Services.Cal;

/// <summary>
/// The <c>2000000xxx</c> platform virtual / hidden tables as they exist in
/// classic NAV (union of NAV 2016 and NAV 2018). A C/AL export references these
/// by id (in <c>TableRelation</c>, <c>CalcFormula</c>, RecordRef/FieldRef code,
/// …) but never ships them as objects, so the id→name resolution pass needs
/// this map to put a name on them.
///
/// <para>
/// This is deliberately <b>separate</b> from the AL path's <c>PlatformVirtualTables</c>:
/// modern BC renumbered / renamed parts of this space (e.g. NAV <c>2000000038</c>
/// is <c>AllObj</c> and <c>2000000058</c> is <c>AllObjWithCaption</c>, which the
/// BC list assigns differently), so reusing the BC map would mis-name objects in
/// a NAV import. Source: the canonical "All Hidden / Virtual Tables in Microsoft
/// Dynamics NAV 2016 / 2018" enumerations.
/// </para>
///
/// EXTENDING: if a real NAV export references a <c>2000000xxx</c> id that still
/// shows unresolved, add it here with the name from that NAV version's virtual
/// table list — don't guess.
/// </summary>
internal static class CalVirtualTables
{
    public static readonly (int Id, string Name)[] All =
    {
        (2000000001, "Object"),
        (2000000007, "Date"),
        (2000000009, "Session"),
        (2000000020, "Drive"),
        (2000000022, "File"),
        (2000000024, "Monitor"),
        (2000000026, "Integer"),
        (2000000028, "Table Information"),
        (2000000029, "System Object"),
        (2000000038, "AllObj"),
        (2000000039, "Printer"),
        (2000000040, "License Information"),
        (2000000041, "Field"),
        (2000000042, "OLE Control"),
        (2000000043, "License Permission"),
        (2000000044, "Permission Range"),
        (2000000045, "Windows Language"),
        (2000000046, "Automation Server"),
        (2000000047, "Server"),
        (2000000048, "Database"),
        (2000000049, "Code Coverage"),
        (2000000055, "SID - Account ID"),
        (2000000058, "AllObjWithCaption"),
        (2000000063, "Key"),
        (2000000070, "Error List"),
        (2000000081, "Upgrade Blob Storage"),
        (2000000082, "Report Layout"),
        (2000000101, "Debugger Call Stack"),
        (2000000102, "Debugger Variable"),
        (2000000103, "Debugger Watch Value"),
        (2000000135, "Table Synch. Setup"),
        (2000000136, "Table Metadata"),
        (2000000137, "CodeUnit Metadata"),
        (2000000138, "Page Metadata"),
        (2000000139, "Report Metadata"),
        (2000000140, "Event Subscription"),
        (2000000141, "Table Relations Metadata"),
        (2000000142, "Query Metadata"),
        (2000000154, "Database Locks"),
        (2000000164, "Time Zone"),
        (2000000167, "Aggregate Permission Set"),
        (2000000171, "Page Table Field"),
        (2000000172, "Table Field Types"),
        (2000000173, "Finish Design Save Mode"),
        (2000000176, "NAV App Resource"),
        (2000000177, "Tenant Profile"),
        (2000000178, "All Profile"),
        (2000000179, "OData Edm Type"),
        (2000000182, "Media Resources"),
        (2000000186, "Profile Page Metadata"),
        (2000000187, "Tenant Profile Page Metadata"),
        (2000000188, "User Page Metadata"),
        (2000000192, "Page Control Field"),
        (2000000193, "Api Web Service"),
    };

    public static int[] Ids { get; } = All.Select(v => v.Id).ToArray();
    public static string[] Names { get; } = All.Select(v => v.Name).ToArray();
}
