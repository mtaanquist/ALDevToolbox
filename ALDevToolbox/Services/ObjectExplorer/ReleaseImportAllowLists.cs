namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// The allow-lists and dependency-walking helpers the Object Explorer
/// ingest resolves visibility against: the foundational Microsoft app
/// names, the BC platform virtual-table id -> name map, and the
/// app.json dependency walk that expands a module's transitive
/// visibility set.
///
/// Extracted verbatim from <see cref="ReleaseImportService"/> so the
/// tables PROJECT.md tells maintainers to extend for each new BC
/// release live in a file named for what they are.
/// </summary>
internal static class ReleaseImportAllowLists
{
    /// <summary>
    /// Well-known Microsoft module names whose AppIds are implicitly
    /// visible to every other module in the release. These are the
    /// "platform" apps the AL compiler always resolves against without
    /// requiring an <c>app.json</c> dependency declaration.
    ///
    /// <para><b>EXTENDING:</b> when Microsoft introduces a new
    /// foundational umbrella app (every extension can reference it
    /// without declaring a dep), add its display name here. Matched
    /// case-insensitively against <c>OeModule.Name</c> + a
    /// Publisher = "Microsoft" filter so a third-party can't ship an
    /// app with the same name to widen visibility.</para>
    /// </summary>
    internal static readonly HashSet<string> FoundationalAppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Application",
        "Base Application",
        "Application",
        "Business Foundation",
    };

    /// <summary>
    /// Sentinel AppId for the synthetic platform virtual tables (and
    /// any other catalog entry that doesn't belong to a specific
    /// imported module). Every module's visibility set includes this
    /// AppId so chains through <c>Record Field</c>, <c>Record Company</c>
    /// etc. resolve cleanly. <see cref="Guid.Empty"/> as a sentinel is
    /// safe because real BC AppIds are always non-empty GUIDs.
    /// </summary>
    internal static readonly Guid PlatformAppId = Guid.Empty;

    /// <summary>
    /// BC platform virtual tables — runtime-provided system tables
    /// every extension can reference but no module's symbol package
    /// declares. The id range <c>2000000001 – 2000000999</c> is
    /// reserved by Microsoft for these; names have been stable across
    /// BC versions (the compiler emits canonical names like
    /// <c>"Field"</c> / <c>"Company"</c> / <c>"User"</c> rather than
    /// the numeric id when these are referenced from AL source).
    ///
    /// Synthesised as catalog entries during Phase-2 so type lookups
    /// on <c>TempFieldSet: Record Field</c> and similar variable
    /// declarations succeed. Chain steps like <c>TempFieldSet.GET(...)</c>
    /// then resolve through <see cref="ALDevToolbox.Services.Al.AlBuiltinMethods.RecordMethods"/>;
    /// field-specific accesses (<c>TempFieldSet.TableNo</c>) still
    /// drop as <c>chain-step</c> unresolveds because we don't have the
    /// platform-table schemas — acceptable trade-off given the volume.
    ///
    /// <para><b>EXTENDING:</b> if a new BC version adds a platform
    /// virtual table — or renames an existing one — add an entry
    /// here. The numeric range safety net
    /// (<c>AlReferenceExtractor.IsPlatformVirtualTableId</c>) silences
    /// the diagnostic even for unlisted ids, but the named entry is
    /// what lets `Record &lt;Name&gt;` chains resolve cleanly through
    /// the synthetic catalog. Source for the canonical id → name map:
    /// hougaard.com (cited at the call site below).</para>
    /// </summary>
    internal static readonly (int Id, string Name)[] PlatformVirtualTables =
    {
        // Source: https://www.hougaard.com/all-the-2-billion-tables-in-business-central-v16/
        // — authoritative enumeration of the BC virtual-table id space.
        // The IsPlatformVirtualTableId range check still catches any
        // numeric id we miss; this map is for named-type chain resolution
        // (`TempFieldSet: Record Field` etc.).
        (2000000001, "Object"),
        (2000000004, "Permission Set"),
        (2000000005, "Permission"),
        (2000000006, "Company"),
        (2000000007, "Date"),
        (2000000009, "Session"),
        (2000000020, "Drive"),
        (2000000022, "File"),
        (2000000026, "Integer"),
        (2000000028, "Table Information"),
        (2000000029, "System Object"),
        (2000000038, "AllObj"),
        (2000000039, "Printer"),
        (2000000040, "License Information"),
        (2000000041, "Field"),
        (2000000043, "License Permission"),
        (2000000044, "Permission Range"),
        (2000000045, "Windows Language"),
        (2000000048, "Database"),
        (2000000049, "Code Coverage"),
        (2000000053, "Access Control"),
        (2000000055, "SID - Account ID"),
        (2000000058, "AllObjWithCaption"),
        (2000000063, "Key"),
        (2000000065, "Send-To Program"),
        (2000000066, "Style Sheet"),
        (2000000067, "User Default Style Sheet"),
        (2000000068, "Record Link"),
        (2000000069, "Add-in"),
        (2000000071, "Object Metadata"),
        (2000000072, "Profile"),
        (2000000073, "User Personalization"),
        (2000000074, "Profile Metadata"),
        (2000000075, "User Metadata"),
        (2000000076, "Web Service"),
        (2000000078, "Chart"),
        (2000000080, "Page Data Personalization"),
        (2000000081, "Upgrade Blob Storage"),
        (2000000082, "Report Layout"),
        (2000000083, "Tenant Profile Setting"),
        (2000000084, "Tenant Profile Extension"),
        (2000000086, "Profile Configuration Symbols"),
        (2000000095, "API Webhook Subscription"),
        (2000000096, "API Webhook Notification"),
        (2000000097, "API Webhook Entity"),
        (2000000098, "API Webhook Notification Aggr"),
        (2000000103, "Debugger Watch Value"),
        (2000000107, "Isolated Storage"),
        (2000000110, "Active Session"),
        (2000000111, "Session Event"),
        (2000000112, "Server Instance"),
        (2000000114, "Document Service"),
        (2000000120, "User"),
        (2000000121, "User Property"),
        (2000000130, "Device"),
        (2000000135, "Table Synch. Setup"),
        (2000000136, "Table Metadata"),
        (2000000137, "CodeUnit Metadata"),
        (2000000138, "Page Metadata"),
        (2000000139, "Report Metadata"),
        (2000000140, "Event Subscription"),
        (2000000141, "Table Relations Metadata"),
        (2000000142, "Query Metadata"),
        (2000000143, "Page Action"),
        (2000000144, "Power BI Blob"),
        (2000000145, "Power BI Default Selection"),
        (2000000146, "Intelligent Cloud"),
        (2000000152, "NAV App Data Archive"),
        (2000000153, "NAV App Installed App"),
        (2000000154, "Database Locks"),
        (2000000157, "NAV App Extra"),
        (2000000159, "Data Sensitivity"),
        (2000000162, "NAV App Capabilities"),
        (2000000163, "NAV App Object Prerequisites"),
        (2000000164, "Time Zone"),
        (2000000165, "Tenant Permission Set"),
        (2000000166, "Tenant Permission"),
        (2000000167, "Aggregate Permission Set"),
        (2000000168, "Tenant Web Service"),
        (2000000169, "NAV App Tenant Add-In"),
        (2000000170, "Configuration Package File"),
        (2000000171, "Page Table Field"),
        (2000000172, "Table Field Types"),
        (2000000173, "Intelligent Cloud Status"),
        (2000000175, "Scheduled Task"),
        (2000000177, "Tenant Profile"),
        (2000000178, "All Profile"),
        (2000000179, "OData Edm Type"),
        (2000000180, "Media Set"),
        (2000000181, "Media"),
        (2000000182, "Media Resources"),
        (2000000183, "Tenant Media Set"),
        (2000000184, "Tenant Media"),
        (2000000185, "Tenant Media Thumbnails"),
        (2000000186, "Profile Page Metadata"),
        (2000000187, "Tenant Profile Page Metadata"),
        (2000000188, "User Page Metadata"),
        (2000000189, "Tenant License State"),
        (2000000190, "Entitlement Set"),
        (2000000191, "Entitlement"),
        (2000000192, "Page Control Field"),
        (2000000193, "Api Web Service"),
        (2000000194, "Webhook Notification"),
        (2000000195, "Membership Entitlement"),
        (2000000196, "Object Options"),
        (2000000197, "Token Cache"),
        (2000000198, "Page Documentation"),
        (2000000199, "Webhook Subscription"),
        (2000000200, "NAV App Tenant Operation"),
        (2000000201, "NAV App Setting"),
        (2000000202, "All Control Fields"),
        (2000000203, "Report Data Items"),
        (2000000204, "Page Info And Fields"),
        (2000000205, "Object Access Intent Override"),
        (2000000206, "Published Application"),
        (2000000207, "Application Object Metadata"),
        (2000000208, "Application Resource"),
        (2000000209, "Application Dependency"),
        (2000000210, "Tenant Feature Key"),
        (2000000211, "Feature Key"),
        (2000000212, "Installed Application"),
        (2000000213, "Designed Query"),
        (2000000214, "Designed Query Caption"),
        (2000000215, "Designed Query Category"),
        (2000000216, "Designed Query Column"),
        (2000000217, "Designed Query Column Filter"),
        (2000000218, "Designed Query Data Item"),
        (2000000219, "Designed Query Filter"),
        (2000000220, "Designed Query Join"),
        (2000000221, "Designed Query Order By"),
    };

    internal static void WalkDeps(
        Guid current, HashSet<Guid> acc,
        Dictionary<Guid, HashSet<Guid>> directDepsByAppId)
    {
        if (!directDepsByAppId.TryGetValue(current, out var deps)) return;
        foreach (var dep in deps)
        {
            // Add returns false when already present — prevents infinite
            // recursion on (degenerate) cyclic dependency declarations.
            if (acc.Add(dep)) WalkDeps(dep, acc, directDepsByAppId);
        }
    }

    internal static HashSet<Guid> ParseDependencyAppIds(string json)
    {
        var set = new HashSet<Guid>();
        if (string.IsNullOrEmpty(json) || json == "[]") return set;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return set;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("id", out var idProp)
                    && idProp.ValueKind == System.Text.Json.JsonValueKind.String
                    && Guid.TryParse(idProp.GetString(), out var id))
                {
                    set.Add(id);
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed dep JSON shouldn't kill the import — the module
            // just won't contribute deps to its visibility set.
        }
        return set;
    }
}
