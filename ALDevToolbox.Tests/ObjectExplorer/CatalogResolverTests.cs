using System;
using System.Collections.Generic;
using ALDevToolbox.Services.Al;
using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Behavioural tests for <see cref="ReleaseImportService.CatalogResolver"/>'s
/// tiebreaker logic. The resolver is exercised end-to-end by the full
/// release-import path (DB-backed), but those integration runs only
/// catch a regression after a fresh import — too late and too slow for
/// the day-to-day feedback loop. These tests build the resolver in
/// memory and pin the priority order so a future re-ordering of the
/// bucket selection visibly breaks one of them.
///
/// Coverage focuses on the multi-candidate edge cases the real catalog
/// has surfaced:
/// <list type="bullet">
///   <item>Same-name objects across apps where one is the caller's
///         own app (<c>No. Series Line</c> in Base App vs Business
///         Foundation).</item>
///   <item>Same-name objects spanning kinds (a <c>User</c> codeunit in
///         one app vs the System app's <c>User</c> virtual table).
///         Kind-match must beat same-app preference when the caller
///         provided an expected keyword.</item>
///   <item>ObsoleteState demotion — Removed / Moved candidates lose
///         ties to live ones, but Pending candidates stay first-class
///         in the version that declares them.</item>
/// </list>
/// </summary>
public sealed class CatalogResolverTests
{
    private static readonly Guid BaseAppId = Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972");
    private static readonly Guid SystemAppId = Guid.Parse("8874ed3a-0643-4247-9ced-7a7002f7135d");
    private static readonly Guid BusinessFoundationAppId = Guid.Parse("f3552374-a1f2-4356-848e-196002525837");
    private static readonly Guid AvalaraAppId = Guid.Parse("f35c56a6-7c5f-4dbe-89c4-fef5145d00f4");

    // ── Kind-match dominance ────────────────────────────────────────

    [Fact]
    public void Record_keyword_resolves_to_table_over_same_app_codeunit_of_same_name()
    {
        // BC 26.13 regression: when Base App had both a `User` codeunit
        // and could see the System app's `User` virtual table, a
        // `Record User` reference in Base App code (kind hint = table)
        // was resolving to Base App's codeunit because same-app any-
        // kind was preferred over foundational exact-kind. Kind-match
        // is the dominant signal whenever the caller supplies a hint.
        var fixture = new ResolverFixture(ownerAppId: BaseAppId)
            .Add(BaseAppId, "codeunit", 4001, "User")
            .Add(SystemAppId, "table", 2000000120, "User")
            .Foundational(BaseAppId, SystemAppId);

        var result = fixture.Build().ResolveTypeByName("User", "Record");

        result.Should().NotBeNull();
        result!.Kind.Should().Be("table");
        result.AppId.Should().Be(SystemAppId);
    }

    [Fact]
    public void Codeunit_keyword_resolves_to_codeunit_over_table_of_same_name()
    {
        // Symmetric guard: the same setup should resolve `Codeunit "User"`
        // to the codeunit even though the table is in a foundational app.
        var fixture = new ResolverFixture(ownerAppId: BaseAppId)
            .Add(BaseAppId, "codeunit", 4001, "User")
            .Add(SystemAppId, "table", 2000000120, "User")
            .Foundational(BaseAppId, SystemAppId);

        var result = fixture.Build().ResolveTypeByName("User", "Codeunit");

        result.Should().NotBeNull();
        result!.Kind.Should().Be("codeunit");
    }

    [Fact]
    public void No_kind_hint_falls_back_to_same_app_preference()
    {
        // Typed-literal references arrive without a kind hint — e.g.
        // `DATABASE::User` in expression position. With nothing to
        // match on kind, the resolver should still prefer the same-
        // app candidate over a foreign one.
        var fixture = new ResolverFixture(ownerAppId: BusinessFoundationAppId)
            .Add(BusinessFoundationAppId, "table", 309, "No. Series Line")
            .Add(BaseAppId, "table", 309, "No. Series Line")
            .Foundational(BaseAppId, SystemAppId);

        var result = fixture.Build().ResolveTypeByName("No. Series Line", expectedKeyword: null);

        result.Should().NotBeNull();
        result!.AppId.Should().Be(BusinessFoundationAppId);
    }

    // ── Same-app preference within a kind tier ──────────────────────

    [Fact]
    public void Same_app_table_wins_over_other_app_table_of_same_name()
    {
        // Business Foundation has the canonical `No. Series Line` and
        // Base App has a same-named shim. Business Foundation files
        // should reach the local version even though Base App's is
        // also visible (and foundational).
        var fixture = new ResolverFixture(ownerAppId: BusinessFoundationAppId)
            .Add(BusinessFoundationAppId, "table", 309, "No. Series Line")
            .Add(BaseAppId, "table", 309, "No. Series Line")
            .Foundational(BaseAppId, SystemAppId);

        var result = fixture.Build().ResolveTypeByName("No. Series Line", "Record");

        result.Should().NotBeNull();
        result!.AppId.Should().Be(BusinessFoundationAppId);
    }

    [Fact]
    public void Foundational_table_wins_over_random_other_app_table()
    {
        // Base App code referencing `Company` should reach the System
        // app's virtual Company table, not the E-Document Connector –
        // Avalara's same-named connector-specific table. Base App
        // has no `Company` of its own, so same-app preference is null;
        // foundational wins next.
        var fixture = new ResolverFixture(ownerAppId: BaseAppId)
            .Add(AvalaraAppId, "table", 6375, "Company")
            .Add(SystemAppId, "table", 2000000006, "Company")
            .Foundational(BaseAppId, SystemAppId);

        var result = fixture.Build().ResolveTypeByName("Company", "Record");

        result.Should().NotBeNull();
        result!.AppId.Should().Be(SystemAppId);
    }

    // ── ObsoleteState demotion ──────────────────────────────────────

    [Fact]
    public void Removed_candidate_loses_to_live_one()
    {
        // A `Removed` declaration is an empty shell kept for backward
        // compatibility. Any live alternative — even in a different
        // app — should win the tie.
        var fixture = new ResolverFixture(ownerAppId: BaseAppId)
            .Add(BaseAppId, "table", 309, "Old Table", obsoleteState: "Removed")
            .Add(BusinessFoundationAppId, "table", 309, "Old Table")
            .Foundational(BaseAppId, SystemAppId);

        var result = fixture.Build().ResolveTypeByName("Old Table", "Record");

        result.Should().NotBeNull();
        result!.AppId.Should().Be(BusinessFoundationAppId);
    }

    [Fact]
    public void Moved_candidate_loses_to_live_one()
    {
        // `Moved` candidates are forwarding stubs to a new location.
        // Resolve to the live target instead of the stub.
        var fixture = new ResolverFixture(ownerAppId: BaseAppId)
            .Add(BaseAppId, "table", 309, "Old Table", obsoleteState: "Moved")
            .Add(BusinessFoundationAppId, "table", 309, "Old Table")
            .Foundational(BaseAppId, SystemAppId);

        var result = fixture.Build().ResolveTypeByName("Old Table", "Record");

        result.Should().NotBeNull();
        result!.AppId.Should().Be(BusinessFoundationAppId);
    }

    [Fact]
    public void Pending_candidate_stays_first_class_in_its_version()
    {
        // `Pending` means deprecated-but-still-functional. AL routes
        // calls to the Pending declaration normally in the version
        // that declares it; the resolver must do the same. If we
        // demoted Pending, code in the current version would resolve
        // against a future migration target that may not have shipped
        // yet — the wrong shape for find-references and chain
        // resolution.
        var fixture = new ResolverFixture(ownerAppId: BaseAppId)
            .Add(BaseAppId, "table", 309, "Some Table", obsoleteState: "Pending")
            .Add(BusinessFoundationAppId, "table", 309, "Some Table")
            .Foundational(BaseAppId, SystemAppId);

        var result = fixture.Build().ResolveTypeByName("Some Table", "Record");

        result.Should().NotBeNull();
        result!.AppId.Should().Be(BaseAppId);
    }

    [Fact]
    public void Removed_falls_back_when_no_live_alternative_exists()
    {
        // Only a Removed declaration is visible — still resolve to it
        // rather than returning null. A Removed object is at least
        // identifiable by name; refusing to resolve would break
        // backward-compat tooling that surfaces deprecated symbols.
        var fixture = new ResolverFixture(ownerAppId: BaseAppId)
            .Add(BaseAppId, "table", 309, "Sole Table", obsoleteState: "Removed")
            .Foundational(BaseAppId, SystemAppId);

        var result = fixture.Build().ResolveTypeByName("Sole Table", "Record");

        result.Should().NotBeNull();
        result!.AppId.Should().Be(BaseAppId);
    }

    // ── Visibility gating ───────────────────────────────────────────

    [Fact]
    public void Invisible_candidates_are_skipped()
    {
        // The visibility set restricts which AppIds the caller can
        // see. A candidate from an app outside the visible set is
        // skipped entirely; the resolver falls through to the next
        // visible candidate or returns null.
        var fixture = new ResolverFixture(
            ownerAppId: BaseAppId,
            visibleAppIds: new HashSet<Guid> { BaseAppId, SystemAppId })
            .Add(AvalaraAppId, "table", 6375, "Company")
            .Add(SystemAppId, "table", 2000000006, "Company")
            .Foundational(BaseAppId, SystemAppId);

        var result = fixture.Build().ResolveTypeByName("Company", "Record");

        result.Should().NotBeNull();
        result!.AppId.Should().Be(SystemAppId);
    }

    [Fact]
    public void Returns_null_when_no_candidate_is_visible()
    {
        var fixture = new ResolverFixture(
            ownerAppId: BaseAppId,
            visibleAppIds: new HashSet<Guid> { BaseAppId })
            .Add(AvalaraAppId, "table", 6375, "Company")
            .Foundational(BaseAppId, SystemAppId);

        var result = fixture.Build().ResolveTypeByName("Company", "Record");

        result.Should().BeNull();
    }

    // ── Fixture ─────────────────────────────────────────────────────

    /// <summary>
    /// Builder for in-memory <see cref="ReleaseImportService.CatalogResolver"/>
    /// instances. Same shape ReleaseImportService.LoadCatalogAsync
    /// produces, but small enough to construct in a single test with
    /// the candidates you care about. Defaults to "everyone visible"
    /// so most tests don't have to thread the visibility set
    /// explicitly.
    /// </summary>
    private sealed class ResolverFixture
    {
        private readonly Guid _ownerAppId;
        private readonly HashSet<Guid>? _visibleAppIds;
        private readonly Dictionary<string, List<AlTypeRef>> _typesByName =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, AlTypeRef> _typesByObjectId = new();
        private readonly Dictionary<(string Kind, int ObjectId), List<AlTypeRef>> _typesByAlObjectId = new();
        private readonly Dictionary<(Guid AppId, string Kind, string Name), long> _objectIdByIdentity =
            new(new ReleaseImportService.ObjectIdentityComparer());
        private readonly Dictionary<long, List<ReleaseImportService.MemberEntry>> _members = new();
        private readonly Dictionary<string, List<ReleaseImportService.ExtensionEntry>> _extensionsByBaseName =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, string> _sourceTablesByObjectId = new();
        private readonly HashSet<Guid> _foundationalAppIds = new();
        private readonly Dictionary<(Guid AppId, string Kind, string Name), string> _obsoleteStateByIdentity =
            new(new ReleaseImportService.ObjectIdentityComparer());
        private long _nextRowId = 1;

        public ResolverFixture(Guid ownerAppId, HashSet<Guid>? visibleAppIds = null)
        {
            _ownerAppId = ownerAppId;
            _visibleAppIds = visibleAppIds;
        }

        public ResolverFixture Add(
            Guid appId, string kind, int objectId, string name, string? obsoleteState = null)
        {
            var typeRef = new AlTypeRef(appId, kind, objectId, name);
            var rowId = _nextRowId++;
            _typesByObjectId[rowId] = typeRef;
            if (!_typesByName.TryGetValue(name, out var list))
            {
                list = new List<AlTypeRef>();
                _typesByName[name] = list;
            }
            list.Add(typeRef);
            if (objectId > 0)
            {
                var idKey = (kind, objectId);
                if (!_typesByAlObjectId.TryGetValue(idKey, out var idList))
                {
                    idList = new List<AlTypeRef>();
                    _typesByAlObjectId[idKey] = idList;
                }
                idList.Add(typeRef);
            }
            _objectIdByIdentity[(appId, kind, name)] = rowId;
            if (!string.IsNullOrEmpty(obsoleteState))
            {
                _obsoleteStateByIdentity[(appId, kind, name)] = obsoleteState;
            }
            return this;
        }

        public ResolverFixture Foundational(params Guid[] appIds)
        {
            foreach (var id in appIds) _foundationalAppIds.Add(id);
            return this;
        }

        public ReleaseImportService.CatalogResolver Build() => new(
            _typesByName,
            _typesByObjectId,
            _typesByAlObjectId,
            _objectIdByIdentity,
            _members,
            _extensionsByBaseName,
            _sourceTablesByObjectId,
            _visibleAppIds,
            _ownerAppId,
            _foundationalAppIds,
            _obsoleteStateByIdentity);
    }
}
