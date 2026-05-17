using System;
using System.Collections.Generic;
using System.Linq;
using ALDevToolbox.Services.Al;

namespace ALDevToolbox.Tests.Al;

/// <summary>
/// Shared in-memory type catalog every snapshot fixture resolves against.
///
/// Kept intentionally narrow — only types and members a fixture actually
/// names. Adding entries here is fine; removing one risks invalidating
/// committed snapshots. Modelled on the unit-test <c>StubResolver</c> in
/// <see cref="AlReferenceExtractorTests"/> but exposed as a singleton so
/// the per-fixture <see cref="AlExtractContext"/> can hand the same
/// resolver to every test without rebuilding the catalog per call.
/// </summary>
internal static class SnapshotCatalog
{
    public static readonly Guid OwnerAppId =
        Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972");

    public static readonly IAlTypeResolver Resolver = Build();

    private static IAlTypeResolver Build()
    {
        var r = new InMemoryResolver();

        // ── Tables ────────────────────────────────────────────────
        r.AddType("Customer", new AlTypeRef(OwnerAppId, "table", 18, "Customer"));
        r.AddMember("Customer", new AlMember("No.", "table_field","Code", "20"));
        r.AddMember("Customer", new AlMember("Name", "table_field","Text", "100"));
        r.AddMember("Customer", new AlMember("Phone No.", "table_field","Text", "30"));
        r.AddMember("Customer", new AlMember("Insert", "procedure", null, null));
        r.AddMember("Customer", new AlMember("Modify", "procedure", null, null));
        r.AddMember("Customer", new AlMember("Get", "procedure", null, null));
        r.AddMember("Customer", new AlMember("Validate", "procedure", null, null));
        r.AddMember("Customer", new AlMember("SetRange", "procedure", null, null));

        r.AddType("Sales Header", new AlTypeRef(OwnerAppId, "table", 36, "Sales Header"));
        r.AddMember("Sales Header", new AlMember("No.", "table_field",null, null));
        r.AddMember("Sales Header", new AlMember("Document Type", "table_field",null, null));
        r.AddMember("Sales Header", new AlMember("Sell-to Customer No.", "table_field",null, null));
        r.AddMember("Sales Header", new AlMember("Customer", "table_field","Record", "Customer"));
        r.AddMember("Sales Header", new AlMember("Insert", "procedure", null, null));
        r.AddMember("Sales Header", new AlMember("Validate", "procedure", null, null));

        r.AddType("Sales Line", new AlTypeRef(OwnerAppId, "table", 37, "Sales Line"));
        r.AddMember("Sales Line", new AlMember("No.", "table_field",null, null));
        r.AddMember("Sales Line", new AlMember("Document Type", "table_field",null, null));
        r.AddMember("Sales Line", new AlMember("Type", "table_field",null, null));
        r.AddMember("Sales Line", new AlMember("InitRecord", "procedure", null, null));

        r.AddType("Item", new AlTypeRef(OwnerAppId, "table", 27, "Item"));
        r.AddMember("Item", new AlMember("No.", "table_field",null, null));
        r.AddMember("Item", new AlMember("Description", "table_field",null, null));

        // Tables used by query / report fixtures to exercise the
        // dataitem-alias registration path (see AlDataItemDsl).
        r.AddType("Vendor", new AlTypeRef(OwnerAppId, "table", 23, "Vendor"));
        r.AddMember("Vendor", new AlMember("No.", "table_field", null, null));
        r.AddMember("Vendor", new AlMember("Name", "table_field", null, null));
        r.AddMember("Vendor", new AlMember("SystemId", "table_field", null, null));

        r.AddType("Vendor Ledger Entry", new AlTypeRef(OwnerAppId, "table", 25, "Vendor Ledger Entry"));
        r.AddMember("Vendor Ledger Entry", new AlMember("Vendor No.", "table_field", null, null));
        r.AddMember("Vendor Ledger Entry", new AlMember("Purchase (LCY)", "table_field", null, null));
        r.AddMember("Vendor Ledger Entry", new AlMember("Posting Date", "table_field", null, null));

        // ── Pages ─────────────────────────────────────────────────
        r.AddType("Customer List", new AlTypeRef(OwnerAppId, "page", 22, "Customer List"));
        r.AddSourceTable("Customer List", "Customer");
        r.AddType("Customer Card", new AlTypeRef(OwnerAppId, "page", 21, "Customer Card"));
        r.AddSourceTable("Customer Card", "Customer");
        r.AddType("Sales Order", new AlTypeRef(OwnerAppId, "page", 42, "Sales Order"));
        r.AddSourceTable("Sales Order", "Sales Header");
        r.AddType("Customer Statistics FactBox",
            new AlTypeRef(OwnerAppId, "page", 1300, "Customer Statistics FactBox"));
        r.AddSourceTable("Customer Statistics FactBox", "Customer");

        // ── Codeunits ─────────────────────────────────────────────
        r.AddType("Sales-Post", new AlTypeRef(OwnerAppId, "codeunit", 80, "Sales-Post"));
        r.AddMember("Sales-Post", new AlMember("Run", "procedure", null, null));

        // Snapshot-fixture owners that need OwnerType() to resolve
        // (e.g. so variable_use refs find a target owner).
        r.AddType("Global Variable Usage",
            new AlTypeRef(OwnerAppId, "codeunit", 50001, "Global Variable Usage"));
        r.AddType("Attributed Var Sample",
            new AlTypeRef(OwnerAppId, "codeunit", 50002, "Attributed Var Sample"));

        // ── Reports / queries / xmlports / enums ─────────────────
        r.AddType("Customer List Report", new AlTypeRef(OwnerAppId, "report", 101, "Customer List Report"));
        r.AddType("Customer Query", new AlTypeRef(OwnerAppId, "query", 101, "Customer Query"));
        r.AddType("Item Source", new AlTypeRef(OwnerAppId, "xmlport", 101, "Item Source"));
        r.AddType("Sales Document Type",
            new AlTypeRef(OwnerAppId, "enum", 50000, "Sales Document Type"));

        return r;
    }

    private sealed class InMemoryResolver : IAlTypeResolver
    {
        private readonly Dictionary<string, AlTypeRef> _types = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<AlMember>> _members = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _sourceTables = new(StringComparer.OrdinalIgnoreCase);

        public void AddType(string name, AlTypeRef type) => _types[name] = type;

        public void AddMember(string ownerName, AlMember member)
        {
            if (!_members.TryGetValue(ownerName, out var list))
            {
                list = new List<AlMember>();
                _members[ownerName] = list;
            }
            list.Add(member);
        }

        public void AddSourceTable(string objectName, string sourceTable) =>
            _sourceTables[objectName] = sourceTable;

        public AlTypeRef? ResolveTypeByName(string typeName, string? expectedKeyword = null) =>
            _types.TryGetValue(typeName, out var t) ? t : null;

        public AlMember? ResolveMember(AlTypeRef owner, string memberName)
        {
            if (!_members.TryGetValue(owner.Name, out var list)) return null;
            return list.FirstOrDefault(m =>
                string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
        }

        public string? ResolveSourceTableName(AlTypeRef target) =>
            _sourceTables.TryGetValue(target.Name, out var src) ? src : null;
    }
}
