namespace ALDevToolbox.Services.ObjectExplorer;

internal sealed record MemberEntry(
    long SymbolId, string Name, string Kind, string? ReturnTypeKeyword, string? ReturnTypeName);

internal sealed record ExtensionEntry(Guid AppId, long ObjectId);

/// <summary>
/// Composite-key comparer for object-identity lookups
/// <c>(AppId, Kind, Name)</c>. Kind and Name use ordinal-ignore-case
/// (AL identifiers are case-insensitive); AppId is a Guid with its
/// own structural equality. Storing identity rather than name alone
/// disambiguates name collisions across kinds / modules — a Table
/// and a TableExtension can both be named "Sales Header".
/// </summary>
internal sealed class ObjectIdentityComparer : IEqualityComparer<(Guid AppId, string Kind, string Name)>
{
    public bool Equals((Guid AppId, string Kind, string Name) x, (Guid AppId, string Kind, string Name) y) =>
        x.AppId == y.AppId
        && string.Equals(x.Kind, y.Kind, StringComparison.OrdinalIgnoreCase)
        && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((Guid AppId, string Kind, string Name) obj) =>
        HashCode.Combine(
            obj.AppId,
            obj.Kind.ToLowerInvariant(),
            obj.Name.ToLowerInvariant());
}

/// <summary>
/// IAlTypeResolver implementation backed by the in-memory catalogs
/// built once per release. Dependency-aware: when constructed with
/// a non-null <c>visibleAppIds</c> set, type and member lookups
/// only return matches whose declaring module's AppId is in the
/// caller's visibility set (the transitive closure of the caller
/// module's app.json dependencies). When the visibility set is
/// null, the resolver is permissive — used by tests that don't
/// care about dependency direction.
///
/// Member lookup also walks tableextensions / pageextensions
/// targeting the receiver's base type: a procedure added by
/// CustomerExt is callable as <c>Cust.MyMethod()</c> on a
/// Customer-typed variable. The returned AlMember tags
/// <see cref="ALDevToolbox.Services.Al.AlMember.DeclaringType"/>
/// with the extension so the extractor stamps the reference's
/// target at the extension (the actual declaration site), not the
/// base table. Extensions are also filtered by visibility — if the
/// caller's module doesn't depend on the extension's module, the
/// extension's members are invisible.
/// </summary>
internal sealed class CatalogResolver : ALDevToolbox.Services.Al.IAlTypeResolver
{
    private readonly Dictionary<string, List<ALDevToolbox.Services.Al.AlTypeRef>> _typesByName;
    private readonly Dictionary<long, ALDevToolbox.Services.Al.AlTypeRef> _typesByObjectId;
    private readonly Dictionary<(string Kind, int ObjectId), List<ALDevToolbox.Services.Al.AlTypeRef>> _typesByAlObjectId;
    private readonly Dictionary<(Guid AppId, string Kind, string Name), long> _objectIdByIdentity;
    private readonly Dictionary<long, List<MemberEntry>> _members;
    private readonly Dictionary<string, List<ExtensionEntry>> _extensionsByBaseName;
    private readonly Dictionary<long, (Guid AppId, string Name)> _interfaceExtendsByOwnerId;
    private readonly Dictionary<long, string> _sourceTablesByObjectId;
    private readonly HashSet<Guid>? _visibleAppIds;
    // AppId of the module whose file is being walked. Used as a
    // tiebreaker in ResolveTypeByName: when the catalog has
    // multiple candidates for a name (a real BC pattern —
    // `No. Series Line` exists in both Base Application and
    // Business Foundation with the same object id, `Company` exists
    // in the System platform and as a connector-specific table),
    // prefer the candidate whose AppId matches the caller. Falls
    // through to the existing kind / extension preference when no
    // same-app match is visible.
    private readonly Guid? _ownerAppId;
    // Foundational app ids — the platform/system tables (Company,
    // User, File, Field, …) live under these. When the caller has
    // no same-app match for a name, prefer one of these over a
    // random visible candidate. Set up from the same name-based
    // probe BuildModuleVisibilityAsync uses (publisher=Microsoft
    // AND name in FoundationalAppNames).
    private readonly HashSet<Guid> _foundationalAppIds;
    // ObsoleteState by identity. `Removed` / `Moved` candidates
    // get pushed below non-obsolete ones during tiebreaker
    // selection — those have no body to dispatch against (`Removed`
    // keeps an empty shell, `Moved` relocates to a different
    // module). `Pending` declarations are intentionally NOT
    // deprioritized: they're still fully functional in the
    // current version and AL routes calls to them normally;
    // the version that flips them to `Removed` is the one where
    // resolution should follow the migration path. Missing
    // entries mean ObsoleteState=No (default).
    private readonly Dictionary<(Guid AppId, string Kind, string Name), string> _obsoleteStateByIdentity;

    public CatalogResolver(
        Dictionary<string, List<ALDevToolbox.Services.Al.AlTypeRef>> typesByName,
        Dictionary<long, ALDevToolbox.Services.Al.AlTypeRef> typesByObjectId,
        Dictionary<(string Kind, int ObjectId), List<ALDevToolbox.Services.Al.AlTypeRef>> typesByAlObjectId,
        Dictionary<(Guid AppId, string Kind, string Name), long> objectIdByIdentity,
        Dictionary<long, List<MemberEntry>> members,
        Dictionary<string, List<ExtensionEntry>> extensionsByBaseName,
        Dictionary<long, (Guid AppId, string Name)> interfaceExtendsByOwnerId,
        Dictionary<long, string> sourceTablesByObjectId,
        HashSet<Guid>? visibleAppIds,
        Guid? ownerAppId,
        HashSet<Guid> foundationalAppIds,
        Dictionary<(Guid AppId, string Kind, string Name), string> obsoleteStateByIdentity)
    {
        _typesByName = typesByName;
        _typesByObjectId = typesByObjectId;
        _typesByAlObjectId = typesByAlObjectId;
        _objectIdByIdentity = objectIdByIdentity;
        _interfaceExtendsByOwnerId = interfaceExtendsByOwnerId;
        _members = members;
        _extensionsByBaseName = extensionsByBaseName;
        _sourceTablesByObjectId = sourceTablesByObjectId;
        _visibleAppIds = visibleAppIds;
        _ownerAppId = ownerAppId;
        _foundationalAppIds = foundationalAppIds;
        _obsoleteStateByIdentity = obsoleteStateByIdentity;
    }

    /// <summary>
    /// True when the candidate is marked <c>ObsoleteState = Removed</c>
    /// or <c>Moved</c>. Removed candidates have no body (just a shell
    /// kept for backward-compatibility) and Moved ones have relocated
    /// to a different module; either way, a live alternative should
    /// win the tiebreaker. <c>Pending</c> declarations are
    /// intentionally NOT flagged here — they're still functional in
    /// the current version, just slated for removal in a future one,
    /// so the resolver should keep treating them as first-class.
    /// </summary>
    private bool IsObsolete(ALDevToolbox.Services.Al.AlTypeRef t) =>
        _obsoleteStateByIdentity.TryGetValue((t.AppId, t.Kind, t.Name), out var state)
        && (string.Equals(state, "Removed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "Moved", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a name to a single AlTypeRef. When multiple objects
    /// share the name, preference order is:
    /// <list type="number">
    ///   <item>Visible (the caller's module can see the declaring
    ///         AppId via app.json dependencies or first-party
    ///         implicit visibility).</item>
    ///   <item>Caller's own app wins on a tie. <c>No. Series Line</c>
    ///         exists in both Base Application and Business
    ///         Foundation with the same object id; Business Foundation
    ///         files should resolve to Business Foundation's version
    ///         even though Base Application's is also visible.</item>
    ///   <item>Foundational platform apps (Base Application, System,
    ///         System Application, Application) beat random other
    ///         visible apps when there's no same-app match. The
    ///         platform virtual <c>Company</c> / <c>User</c> / <c>File</c>
    ///         tables live in <c>System</c> and Base App code
    ///         referencing them shouldn't lose to a connector's
    ///         same-named table.</item>
    ///   <item>Kind matches the caller's hint (<paramref name="expectedKeyword"/>).
    ///         A page's <c>SourceTable = "Sales Header"</c> arrives
    ///         here with keyword <c>Record</c> — a TableExtension
    ///         named "Sales Header" can't be a page's source table,
    ///         only the base Table can.</item>
    ///   <item>Otherwise, a non-extension kind beats an extension
    ///         kind. Typed-literal references in AL almost always
    ///         mean the base object.</item>
    /// </list>
    /// </summary>
    public ALDevToolbox.Services.Al.AlTypeRef? ResolveTypeByName(string typeName, string? expectedKeyword = null)
    {
        if (!_typesByName.TryGetValue(typeName, out var candidates)) return null;
        var expectedKind = MapKeywordToKind(expectedKeyword);

        // Bucket walk: classify each visible candidate by source app,
        // kind, AND ObsoleteState, then pick the highest-priority
        // bucket that has a hit. Each app-tier (same-app /
        // foundational / other) has a non-obsolete sub-bucket above
        // its obsolete one — a `Removed` / `Moved` shell loses to
        // any live alternative. `Pending` candidates are treated as
        // non-obsolete (still functional in this version). Within
        // a tier the live entry wins outright; cross-tier, all
        // non-obsolete tiers are exhausted before any obsolete
        // tier, matching the AL compiler's migration semantics.
        ALDevToolbox.Services.Al.AlTypeRef? sameAppExact = null;
        ALDevToolbox.Services.Al.AlTypeRef? sameAppExactObs = null;
        ALDevToolbox.Services.Al.AlTypeRef? sameAppAny = null;
        ALDevToolbox.Services.Al.AlTypeRef? sameAppAnyObs = null;
        ALDevToolbox.Services.Al.AlTypeRef? foundationalExact = null;
        ALDevToolbox.Services.Al.AlTypeRef? foundationalExactObs = null;
        ALDevToolbox.Services.Al.AlTypeRef? foundationalAny = null;
        ALDevToolbox.Services.Al.AlTypeRef? foundationalAnyObs = null;
        ALDevToolbox.Services.Al.AlTypeRef? otherExact = null;
        ALDevToolbox.Services.Al.AlTypeRef? otherExactObs = null;
        ALDevToolbox.Services.Al.AlTypeRef? otherNonExt = null;
        ALDevToolbox.Services.Al.AlTypeRef? otherNonExtObs = null;
        ALDevToolbox.Services.Al.AlTypeRef? otherAny = null;
        ALDevToolbox.Services.Al.AlTypeRef? otherAnyObs = null;

        foreach (var t in candidates)
        {
            if (!IsVisible(t.AppId)) continue;
            bool kindMatches = expectedKind is not null
                && string.Equals(t.Kind, expectedKind, StringComparison.OrdinalIgnoreCase);
            bool obsolete = IsObsolete(t);

            if (_ownerAppId is not null && t.AppId == _ownerAppId.Value)
            {
                if (kindMatches)
                {
                    if (obsolete) sameAppExactObs ??= t; else sameAppExact ??= t;
                }
                else
                {
                    if (obsolete) sameAppAnyObs ??= t; else sameAppAny ??= t;
                }
                continue;
            }
            if (_foundationalAppIds.Contains(t.AppId))
            {
                if (kindMatches)
                {
                    if (obsolete) foundationalExactObs ??= t; else foundationalExact ??= t;
                }
                else
                {
                    if (obsolete) foundationalAnyObs ??= t; else foundationalAny ??= t;
                }
                continue;
            }
            if (kindMatches)
            {
                if (obsolete) otherExactObs ??= t; else otherExact ??= t;
                continue;
            }
            if (!IsExtensionKind(t.Kind))
            {
                if (obsolete) otherNonExtObs ??= t; else otherNonExt ??= t;
                continue;
            }
            if (obsolete) otherAnyObs ??= t; else otherAny ??= t;
        }

        // Priority is grouped first by kind-match (when the caller
        // gave a hint), THEN by app-tier. A `Record User` reference
        // must land on the User TABLE — wherever it lives — before
        // it considers any codeunit named "User", even one in the
        // caller's own app. The earlier ordering put same-app any-
        // kind above foundational exact-kind, which made Base App
        // code referencing `Record User` resolve against Base App's
        // `User` codeunit instead of the System app's virtual User
        // table; every chain step (FindFirst, SetRange, etc.) then
        // missed because Record methods don't exist on a codeunit
        // receiver.
        //
        // When the caller has no kind hint (typed-literal references
        // like `Codeunit::"Foo"`), the exact-kind buckets are
        // empty by construction and the wrong-kind buckets carry
        // every candidate; same-app preference still wins.
        return sameAppExact
            ?? foundationalExact
            ?? otherExact
            ?? sameAppAny
            ?? foundationalAny
            ?? otherNonExt
            ?? otherAny
            // Non-obsolete tiers exhausted; fall through to obsolete
            // candidates in the same priority order. A Removed /
            // Moved declaration still resolves something — it's
            // just less preferred than any live alternative.
            ?? sameAppExactObs
            ?? foundationalExactObs
            ?? otherExactObs
            ?? sameAppAnyObs
            ?? foundationalAnyObs
            ?? otherNonExtObs
            ?? otherAnyObs;
    }

    /// <summary>
    /// Maps the caller's kind hint to a catalog kind. Accepts both
    /// AL type keywords (<c>Record</c>, <c>Codeunit</c>, <c>Page</c>, …)
    /// and catalog kind values (<c>table</c>, <c>codeunit</c>,
    /// <c>pageextension</c>, …) — the latter for cases like
    /// <c>OwnerType()</c> in the extractor where we already have the
    /// owner's catalog kind and want bare self-calls on a
    /// pageextension named the same as its base page to land on the
    /// extension, not on the base.
    /// <c>Record</c> is the only keyword that doesn't passthrough —
    /// it maps to <c>table</c>. The rest are identical except for
    /// casing.
    /// </summary>
    private static string? MapKeywordToKind(string? keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return null;
        var lower = keyword.ToLowerInvariant();
        if (lower == "record") return "table";
        return lower;
    }

    public ALDevToolbox.Services.Al.AlMember? ResolveMember(
        ALDevToolbox.Services.Al.AlTypeRef owner, string memberName)
    {
        // Owner's own members win — same-name members on extensions
        // are shadowed by the base's own declaration (AL dispatch).
        // Use the composite identity key so a same-named extension
        // doesn't get confused with the base when looking up the
        // owner's DB id.
        if (_objectIdByIdentity.TryGetValue((owner.AppId, owner.Kind, owner.Name), out var ownerId)
            && _members.TryGetValue(ownerId, out var ownerMembers))
        {
            var match = ownerMembers.FirstOrDefault(m =>
                string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return new ALDevToolbox.Services.Al.AlMember(
                    Name: match.Name,
                    Kind: match.Kind,
                    ReturnTypeKeyword: match.ReturnTypeKeyword,
                    ReturnTypeName: match.ReturnTypeName);
            }
        }

        // Enum-implements-interface fallback: BC's extensible
        // enums frequently `implements` an identically-named
        // interface (`enum "Alt. Cust. VAT Reg. Consist."
        // implements "Alt. Cust. VAT Reg. Consist."`). Calls on
        // an enum-typed variable dispatch to the interface's
        // methods, but our catalog doesn't carry the
        // implements pointer, so member lookup on the enum
        // misses every interface-declared method. Try the
        // same-named visible interface as a fallback before
        // giving up. (Other-named implements relations would
        // need a source-side `implements "Y"` capture; sticking
        // with the same-name convention covers BC's dominant
        // pattern without that work.)
        if (string.Equals(owner.Kind, "enum", StringComparison.OrdinalIgnoreCase)
            && _typesByName.TryGetValue(owner.Name, out var sameNameCandidates))
        {
            foreach (var candidate in sameNameCandidates)
            {
                if (!string.Equals(candidate.Kind, "interface", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsVisible(candidate.AppId)) continue;
                if (!_objectIdByIdentity.TryGetValue((candidate.AppId, "interface", candidate.Name), out var enumIfaceOwnerId)) continue;
                if (!_members.TryGetValue(enumIfaceOwnerId, out var enumIfaceMembers)) continue;
                var match = enumIfaceMembers.FirstOrDefault(m =>
                    string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
                if (match is null) continue;

                _typesByObjectId.TryGetValue(enumIfaceOwnerId, out var declaringInterface);
                return new ALDevToolbox.Services.Al.AlMember(
                    Name: match.Name,
                    Kind: match.Kind,
                    ReturnTypeKeyword: match.ReturnTypeKeyword,
                    ReturnTypeName: match.ReturnTypeName,
                    DeclaringType: declaringInterface);
            }
        }

        // Interface inheritance: when owner is an interface that
        // extends a base interface, walk up the chain so members
        // declared on the base resolve on the derived. Canonical
        // sample: <c>Cost Adjustment With Params extends Inventory
        // Adjustment</c>; <c>SetFilterItem</c> is declared on the
        // base only, but call sites use the derived interface as
        // the receiver. Cap the depth defensively at 16 — interface
        // hierarchies in BC don't go anywhere near that, but a
        // catalog cycle (shouldn't exist, but defensive) would
        // otherwise spin forever.
        if (string.Equals(owner.Kind, "interface", StringComparison.OrdinalIgnoreCase)
            && _objectIdByIdentity.TryGetValue((owner.AppId, "interface", owner.Name), out var ifaceOwnerId))
        {
            var visited = new HashSet<long> { ifaceOwnerId };
            var currentId = ifaceOwnerId;
            for (int depth = 0; depth < 16; depth++)
            {
                if (!_interfaceExtendsByOwnerId.TryGetValue(currentId, out var baseId)) break;
                if (!_objectIdByIdentity.TryGetValue((baseId.AppId, "interface", baseId.Name), out var baseOwnerId)) break;
                if (!visited.Add(baseOwnerId)) break;
                if (_members.TryGetValue(baseOwnerId, out var baseMembers))
                {
                    var match = baseMembers.FirstOrDefault(m =>
                        string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        _typesByObjectId.TryGetValue(baseOwnerId, out var declaringInterface);
                        return new ALDevToolbox.Services.Al.AlMember(
                            Name: match.Name,
                            Kind: match.Kind,
                            ReturnTypeKeyword: match.ReturnTypeKeyword,
                            ReturnTypeName: match.ReturnTypeName,
                            DeclaringType: declaringInterface);
                    }
                }
                currentId = baseOwnerId;
            }
        }

        // Walk visible extensions of this base.
        if (_extensionsByBaseName.TryGetValue(owner.Name, out var extensions))
        {
            foreach (var ext in extensions)
            {
                if (!IsVisible(ext.AppId)) continue;
                if (!_members.TryGetValue(ext.ObjectId, out var extMembers)) continue;
                var match = extMembers.FirstOrDefault(m =>
                    string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
                if (match is null) continue;

                _typesByObjectId.TryGetValue(ext.ObjectId, out var declaringType);
                return new ALDevToolbox.Services.Al.AlMember(
                    Name: match.Name,
                    Kind: match.Kind,
                    ReturnTypeKeyword: match.ReturnTypeKeyword,
                    ReturnTypeName: match.ReturnTypeName,
                    DeclaringType: declaringType);
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up a type by its AL object id. Used by the procedure
    /// walker for variable declarations that name the type by id
    /// instead of name (<c>DtldVendLedgEntry: Record 380;</c>,
    /// <c>var PaymentServiceSetup: Record 1060</c>). Visibility-
    /// and obsolete-state-aware in the same way
    /// <see cref="ResolveTypeByName"/> is: foundational platform
    /// apps win on ties when the caller has no same-app match.
    /// </summary>
    public ALDevToolbox.Services.Al.AlTypeRef? ResolveTypeByObjectId(
        int objectId, string? expectedKeyword = null)
    {
        var kind = MapKeywordToKind(expectedKeyword);
        if (kind is null) return null;
        if (!_typesByAlObjectId.TryGetValue((kind, objectId), out var candidates)) return null;

        ALDevToolbox.Services.Al.AlTypeRef? sameApp = null;
        ALDevToolbox.Services.Al.AlTypeRef? sameAppObs = null;
        ALDevToolbox.Services.Al.AlTypeRef? foundational = null;
        ALDevToolbox.Services.Al.AlTypeRef? foundationalObs = null;
        ALDevToolbox.Services.Al.AlTypeRef? other = null;
        ALDevToolbox.Services.Al.AlTypeRef? otherObs = null;

        foreach (var t in candidates)
        {
            if (!IsVisible(t.AppId)) continue;
            bool obsolete = IsObsolete(t);
            if (_ownerAppId is not null && t.AppId == _ownerAppId.Value)
            {
                if (obsolete) sameAppObs ??= t; else sameApp ??= t;
            }
            else if (_foundationalAppIds.Contains(t.AppId))
            {
                if (obsolete) foundationalObs ??= t; else foundational ??= t;
            }
            else
            {
                if (obsolete) otherObs ??= t; else other ??= t;
            }
        }
        return sameApp ?? foundational ?? other
            ?? sameAppObs ?? foundationalObs ?? otherObs;
    }

    public string? ResolveSourceTableName(ALDevToolbox.Services.Al.AlTypeRef target)
    {
        if (_objectIdByIdentity.TryGetValue((target.AppId, target.Kind, target.Name), out var dbId)
            && _sourceTablesByObjectId.TryGetValue(dbId, out var source))
        {
            return source;
        }
        return null;
    }

    private bool IsVisible(Guid appId) =>
        _visibleAppIds is null || _visibleAppIds.Contains(appId);

    private static bool IsExtensionKind(string kind) =>
        kind.EndsWith("extension", StringComparison.OrdinalIgnoreCase);
}
