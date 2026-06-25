using System;
using System.Collections.Generic;
using ALDevToolbox.Services.Al.Structure;

namespace ALDevToolbox.Services.Al;

// ── Public DTOs ───────────────────────────────────────────────────────

/// <summary>
/// Context passed into <see cref="AlReferenceExtractor.Extract"/>. The
/// owner triplet identifies the file's containing object; the global
/// variable map and the resolver are how the extractor reaches outside
/// the file for type information.
/// </summary>
public sealed record AlExtractContext(
    string OwnerKind,
    string OwnerName,
    int? OwnerObjectId,
    Guid OwnerAppId,
    IReadOnlyDictionary<string, ResolvedVariableType> GlobalVars,
    IAlTypeResolver Resolver,
    string? OwnerSourceTableName = null,
    string? OwnerExtendsName = null);

/// <summary>
/// Looks up type information used during receiver-type resolution.
/// Implementations are typically backed by per-release lookup tables
/// built once at the start of import, but the interface stays narrow
/// so tests can stub it with hand-curated dictionaries.
/// </summary>
public interface IAlTypeResolver
{
    /// <summary>
    /// Resolves an AL type name (e.g. <c>Customer</c>, <c>Sales-Post</c>)
    /// to its location in the type catalog. Returns null when the name
    /// doesn't match a known object — common for system types like
    /// <c>HttpClient</c> or <c>JsonObject</c>.
    ///
    /// <paramref name="expectedKeyword"/> is the AL type keyword the
    /// caller has from context (<c>Record</c>, <c>Codeunit</c>,
    /// <c>Page</c>, <c>Report</c>, …), or null when no kind hint is
    /// available (e.g. bare identifier with no qualifying keyword).
    /// Implementations use it to disambiguate name collisions: a page's
    /// SourceTable named <c>Sales Header</c> resolves to the Table, not
    /// to a TableExtension someone happened to give the same name.
    /// </summary>
    AlTypeRef? ResolveTypeByName(string typeName, string? expectedKeyword = null);

    /// <summary>
    /// Resolves an AL object id (e.g. <c>Record 380</c>, <c>Codeunit 1060</c>)
    /// to its catalog entry. Older AL — and a handful of still-shipping
    /// modules — declare record variables and procedure parameters by
    /// numeric id instead of the quoted name (<c>Record 380</c> →
    /// <c>"Detailed Vendor Ledg. Entry"</c>). Without resolving these,
    /// every member access on the variable strands as
    /// <c>head-var-type-unresolved</c>.
    ///
    /// <paramref name="expectedKeyword"/> is the AL type keyword the
    /// caller has from context (<c>Record</c>, <c>Codeunit</c>, …).
    /// Implementations should kind-filter so a numeric id that exists
    /// for several object kinds returns the one the keyword names.
    ///
    /// Default returns null so existing stub resolvers (unit tests)
    /// don't need to opt in.
    /// </summary>
    AlTypeRef? ResolveTypeByObjectId(int objectId, string? expectedKeyword = null) => null;

    /// <summary>
    /// Resolves a member on a known owner. The owner is identified by
    /// its triplet; the member is matched by name (case-insensitive).
    /// When multiple symbols share the name (overloads), implementations
    /// should pick a stable one (typically the first declared) — the
    /// reference row records the name + kind, not a specific overload.
    /// </summary>
    AlMember? ResolveMember(AlTypeRef owner, string memberName);

    /// <summary>
    /// Returns the SourceTable name for an AL object that has one
    /// (page, pageextension, requestpage, etc.), or <c>null</c> for
    /// kinds that don't carry a source table (tables, codeunits, ...)
    /// or when the metadata isn't available.
    ///
    /// Used by per-kind structure extractors that need to resolve
    /// cross-object field references — e.g. <c>AlPageStructure</c>'s
    /// <c>SubPageLink</c> handler keys field names off the TARGET
    /// page's source table, not the current page's Rec (step 5 of
    /// <c>.design/al-reference-extractor-refactor.md</c>).
    ///
    /// Default <c>null</c> implementation so existing stub resolvers
    /// (unit tests, snapshot catalogs) don't need to opt in — they
    /// can override only when a test exercises a cross-object lookup.
    /// </summary>
    string? ResolveSourceTableName(AlTypeRef target) => null;
}

/// <summary>Resolved reference to an AL object type used by the receiver chain.</summary>
public sealed record AlTypeRef(Guid AppId, string Kind, int? ObjectId, string Name);

/// <summary>
/// A member resolved on an owner type. <see cref="ReturnTypeName"/> is
/// populated for procedures whose declared return type maps to another
/// AL object — used to advance the receiver type through chained calls.
///
/// <see cref="DeclaringType"/> is set when the member actually lives on
/// a *different* object than the static receiver type — the common case
/// is a tableextension adding a procedure or field to a base table. The
/// resolver returns the base-table receiver but tags the member as
/// declared by the extension; the extractor stamps the emitted
/// reference's target as the extension so a Find references on the
/// extension's declaration row finds the call.
/// </summary>
public sealed record AlMember(
    string Name,
    string Kind,
    string? ReturnTypeKeyword,
    string? ReturnTypeName,
    AlTypeRef? DeclaringType = null);

/// <summary>
/// One method-call or field-access reference the extractor recovered
/// from source. Coordinates point at the MEMBER name's start (not the
/// receiver), so the source-viewer flash highlights the right token.
/// </summary>
public sealed record ExtractedReference(
    int Line,
    int Column,
    Guid TargetAppId,
    string TargetObjectKind,
    int? TargetObjectId,
    string TargetObjectName,
    string? TargetMemberName,
    string? TargetMemberKind,
    string ReferenceKind,
    string? SourceMemberName = null,
    string? SourceMemberKind = null,
    int? SourceMemberLine = null);

/// <summary>
/// One call to a built-in / system method on a resolved receiver object —
/// the calls the normal extractor drops via <c>AlBuiltinMethods.IsBuiltin</c>.
/// Captured separately so "Find System References" can surface them without
/// inflating <see cref="ExtractedReference"/>. The target triplet is the
/// receiver object; <see cref="SystemMethodName"/> is the built-in invoked.
/// See issue #279.
/// </summary>
public sealed record ExtractedSystemReference(
    int Line,
    int Column,
    Guid TargetAppId,
    string TargetObjectKind,
    int? TargetObjectId,
    string TargetObjectName,
    string SystemMethodName,
    string ReferenceKind,
    string? SourceMemberName = null,
    string? SourceMemberKind = null,
    int? SourceMemberLine = null);

/// <summary>Per-file extraction statistics — used for diagnostic logging.</summary>
public sealed record ExtractionStats(
    int ResolvedReferences,
    int UnresolvedReceivers,
    IReadOnlyList<UnresolvedSample> UnresolvedSamples);

/// <summary>
/// A single unresolved reference captured for diagnostic logging. The
/// extractor caps these per-file so a pathological file doesn't blow
/// out memory; the import pipeline aggregates a small bucket across
/// files and logs the first N at end-of-phase so operators can spot
/// patterns (e.g. systematic gaps for a specific token shape, a
/// particular catalog name missing, …).
///
/// <para><b>Reasons:</b></para>
/// <list type="bullet">
///   <item><c>head-not-in-scope</c> — the chain's head identifier wasn't
///     a known variable, parameter, scope-frame entry, or catalog type.
///     Common cases: aliases, with-do shadowing, types from packages we
///     haven't ingested yet.</item>
///   <item><c>typed-literal-name</c> — <c>Kind::"Name"</c> where Name
///     didn't resolve as Kind. Usually a cross-release reference we
///     don't see in this release's catalog.</item>
///   <item><c>chain-step</c> — a <c>.member</c> didn't resolve on the
///     known receiver. Most often a tableextension/pageextension we
///     don't model, or an event-published procedure we haven't linked.</item>
///   <item><c>bare-call</c> — an unqualified identifier followed by
///     <c>(</c> that wasn't a system function, in-scope variable, or
///     own-member. Often a procedure on a dependency object we don't
///     surface from this owner's scope.</item>
/// </list>
/// </summary>
public sealed record UnresolvedSample(
    string Reason,
    string Token,
    int Line,
    int Column,
    string? ReceiverKind = null,
    string? ReceiverName = null,
    Guid? ReceiverAppId = null);

/// <summary>
/// Body-bearing symbol scope captured during the walk. One entry per
/// <c>procedure</c> / <c>trigger</c> / event publisher / event subscriber
/// whose matching <c>end;</c> was reached. The import service resolves
/// each entry back to the <c>oe_module_symbols</c> row it stamps
/// <c>end_line</c> / <c>end_column</c> on, via the
/// <c>(Kind, Name, StartLine)</c> tuple — the same tuple the persistence
/// layer uses for overload matching.
/// </summary>
public sealed record ExtractedSymbolScope(
    string Kind,
    string Name,
    int StartLine,
    int EndLine,
    int EndColumn);

/// <summary>Result envelope: extracted rows plus the run's stats.</summary>
public sealed record AlExtractionResult(
    IReadOnlyList<ExtractedReference> References,
    ExtractionStats Stats,
    IReadOnlyList<ExtractedSymbolScope> SymbolScopes,
    IReadOnlyList<ExtractedSystemReference> SystemReferences);
