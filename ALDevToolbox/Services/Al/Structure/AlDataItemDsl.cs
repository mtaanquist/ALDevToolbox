namespace ALDevToolbox.Services.Al.Structure;

/// <summary>
/// Shared helper for the <c>&lt;keyword&gt;(Alias; SourceTable)</c>
/// declaration shape that <c>dataitem</c> (report / query) and
/// <c>tableelement</c> (xmlport) all use. Registers the alias in the
/// outermost scope frame so a subsequent <c>Alias."FieldName"</c>
/// chain in the same file resolves against the source table — same
/// scope-walk path that locals and globals already use.
///
/// Without this, queries with synthetic aliases (the BC convention is
/// <c>dataitem(QueryElement1; Vendor)</c> on API-style queries) fail
/// every <c>QueryElement1.X</c> chain with <c>head-not-a-variable</c>
/// and drop the field reference. Reports hit the same issue when the
/// alias differs from the source-table name.
///
/// Emits a <c>property_object</c> reference on the source-table token
/// so Find references / Go to definition / the underline all work
/// against the table.
/// </summary>
internal static class AlDataItemDsl
{
    /// <summary>
    /// Consumes the declaration when the token matches the expected
    /// keyword shape. Returns <c>(true, sourceTable)</c> on a hit —
    /// <paramref name="sourceTable"/> is the resolved source-table
    /// AlTypeRef so the per-kind structure can record it as the
    /// current dataitem context for subsequent column / filter
    /// source-expression resolution. <c>sourceTable</c> is null when
    /// the token shape matched but the source name didn't resolve
    /// in the catalog (rare). Returns <c>(false, null)</c> when the
    /// token shape didn't match — the caller falls through to its
    /// other dispatches.
    /// </summary>
    public static (bool Consumed, AlTypeRef? SourceTable) TryConsumeAliasedSourceDeclaration(
        AlExtractionState state, string keyword, AlToken tok)
    {
        if (tok.Kind != AlTokenKind.Identifier) return (false, null);
        if (!string.Equals(tok.Value, keyword, StringComparison.OrdinalIgnoreCase)) return (false, null);
        if (state.Pos + 1 >= state.Tokens.Count) return (false, null);
        var next = state.Tokens[state.Pos + 1];
        if (next.Kind != AlTokenKind.Punct || next.Value != "(") return (false, null);

        state.Pos++; // past keyword
        state.Pos++; // past (

        // First arg: alias name (identifier or quoted identifier).
        string? alias = null;
        if (state.Pos < state.Tokens.Count
            && (state.Tokens[state.Pos].Kind == AlTokenKind.Identifier
                || state.Tokens[state.Pos].Kind == AlTokenKind.QuotedIdentifier))
        {
            alias = state.Tokens[state.Pos].Value;
            state.Pos++;
        }

        // Skip to the `;` separator at depth 0.
        int depth = 0;
        while (state.Pos < state.Tokens.Count)
        {
            var current = state.Tokens[state.Pos];
            if (current.Kind == AlTokenKind.Punct)
            {
                if (current.Value == "(") { depth++; state.Pos++; continue; }
                if (current.Value == ")")
                {
                    if (depth == 0) return (true, null);
                    depth--;
                    state.Pos++;
                    continue;
                }
                if (current.Value == ";" && depth == 0)
                {
                    state.Pos++;
                    break;
                }
            }
            state.Pos++;
        }

        // Second arg: source table. Optionally preceded by the `Record`
        // keyword (rare in dataitem / tableelement but legal).
        if (state.Pos < state.Tokens.Count
            && state.Tokens[state.Pos].Kind == AlTokenKind.Identifier
            && AlExtractionState.IsAlObjectKeyword(state.Tokens[state.Pos].Value))
        {
            state.Pos++;
        }
        if (state.Pos >= state.Tokens.Count) return (true, null);

        var sourceTok = state.Tokens[state.Pos];
        if (sourceTok.Kind != AlTokenKind.Identifier
            && sourceTok.Kind != AlTokenKind.QuotedIdentifier)
        {
            return (true, null);
        }
        state.Pos++;

        // Namespace-prefixed source: `System.Utilities.Integer`,
        // `Microsoft.Foundation.NoSeries."No. Series"`. Walk the
        // dotted prefix so the LAST segment is the actual type name —
        // segments before it are the namespace path. Without this,
        // the source token is `System` (unresolved), the cursor lands
        // mid-chain, and `Utilities` / `Integer` get dispatched as
        // bare chain heads by the orchestrator's main loop.
        while (state.Pos + 1 < state.Tokens.Count
               && state.Tokens[state.Pos].Kind == AlTokenKind.Punct
               && state.Tokens[state.Pos].Value == "."
               && (state.Tokens[state.Pos + 1].Kind == AlTokenKind.Identifier
                   || state.Tokens[state.Pos + 1].Kind == AlTokenKind.QuotedIdentifier))
        {
            state.Pos++; // past .
            sourceTok = state.Tokens[state.Pos];
            state.Pos++;
        }

        var resolved = state.Ctx.Resolver.ResolveTypeByName(sourceTok.Value, "Record");
        if (resolved is not null
            && string.Equals(resolved.Kind, "table", StringComparison.OrdinalIgnoreCase))
        {
            state.EmitReference(new ExtractedReference(
                Line: sourceTok.Line,
                Column: sourceTok.Column,
                TargetAppId: resolved.AppId,
                TargetObjectKind: resolved.Kind,
                TargetObjectId: resolved.ObjectId,
                TargetObjectName: resolved.Name,
                TargetMemberName: null,
                TargetMemberKind: null,
                ReferenceKind: "property_object"));
            state.Resolved++;

            // Register the alias in the outermost (object-scope) frame
            // so `Alias.X` chains in any procedure / trigger body
            // resolve as `Record SourceTable`. Bottom-frame mutation is
            // safe here — the procedure walker only reads frame.Vars
            // when consuming a chain head; there's no concurrent
            // iteration.
            if (!string.IsNullOrEmpty(alias))
            {
                ScopeFrame? bottom = null;
                foreach (var frame in state.ScopeStack) bottom = frame;
                if (bottom is not null)
                {
                    bottom.Vars[alias.ToLowerInvariant()] =
                        new ResolvedVariableType("Record", resolved.Name);
                }
            }
            return (true, resolved);
        }
        return (true, null);
    }

    /// <summary>
    /// Pre-scan helper. Walks the entire token stream once, matching
    /// every <paramref name="keyword"/>(<c>Alias; SourceTable</c>)
    /// declaration and registering the alias on the outermost scope
    /// frame. Per-kind structure extractors call this from their
    /// <c>Prescan()</c> hook so forward-referenced aliases (a
    /// dataitem / tableelement declared AFTER a procedure that uses
    /// it) resolve on the main pass.
    ///
    /// Doesn't emit any references — the main pass still handles
    /// property_object emission via
    /// <see cref="TryConsumeAliasedSourceDeclaration"/>. Restores the
    /// cursor before returning so the main walk starts from the top.
    /// </summary>
    /// <summary>
    /// Walks the whole file pre-pass and stamps every
    /// <c>keyword(Name)</c> declaration (where keyword is e.g.
    /// <c>textattribute</c> / <c>textelement</c>) as a Text-typed
    /// scope variable on the outermost frame. These nodes don't bind
    /// to a record field — they're scalar XML text values used as
    /// <c>Text</c> variables from procedure code in the same xmlport.
    /// Without this seeding every procedure-body reference to one of
    /// these names fires head-not-a-variable.
    /// </summary>
    public static void PrescanTextNodeAliases(AlExtractionState state, string keyword)
    {
        int savedPos = state.Pos;
        try
        {
            state.Pos = 0;
            while (state.Pos < state.Tokens.Count)
            {
                var tok = state.Tokens[state.Pos];
                if (tok.Kind == AlTokenKind.Identifier
                    && string.Equals(tok.Value, keyword, StringComparison.OrdinalIgnoreCase)
                    && state.Pos + 1 < state.Tokens.Count
                    && state.Tokens[state.Pos + 1].Kind == AlTokenKind.Punct
                    && state.Tokens[state.Pos + 1].Value == "(")
                {
                    state.Pos += 2; // past keyword and (
                    string? alias = null;
                    if (state.Pos < state.Tokens.Count
                        && (state.Tokens[state.Pos].Kind == AlTokenKind.Identifier
                            || state.Tokens[state.Pos].Kind == AlTokenKind.QuotedIdentifier))
                    {
                        alias = state.Tokens[state.Pos].Value;
                        state.Pos++;
                    }
                    if (!string.IsNullOrEmpty(alias))
                    {
                        ScopeFrame? bottom = null;
                        foreach (var frame in state.ScopeStack) bottom = frame;
                        if (bottom is not null)
                        {
                            // Keyword left null because Text isn't an AL
                            // object — the head-resolver short-circuits
                            // on IsKnownSystemType("Text") when both
                            // keyword is null AND the type is known,
                            // silencing the chain instead of mis-resolving.
                            bottom.Vars[alias.ToLowerInvariant()] =
                                new ResolvedVariableType(null, "Text");
                        }
                    }
                    continue;
                }
                state.Pos++;
            }
        }
        finally
        {
            state.Pos = savedPos;
        }
    }

    public static void PrescanAliases(
        AlExtractionState state, string keyword, bool bindFirstAsRec = false)
    {
        int savedPos = state.Pos;
        bool recAssigned = false;
        try
        {
            state.Pos = 0;
            while (state.Pos < state.Tokens.Count)
            {
                var tok = state.Tokens[state.Pos];
                if (tok.Kind == AlTokenKind.Identifier
                    && string.Equals(tok.Value, keyword, StringComparison.OrdinalIgnoreCase)
                    && state.Pos + 1 < state.Tokens.Count
                    && state.Tokens[state.Pos + 1].Kind == AlTokenKind.Punct
                    && state.Tokens[state.Pos + 1].Value == "(")
                {
                    var registered = RegisterAlias(state);
                    if (bindFirstAsRec && !recAssigned && registered is not null)
                    {
                        ScopeFrame? bottom = null;
                        foreach (var f in state.ScopeStack) bottom = f;
                        if (bottom is not null)
                        {
                            // Overwrite the default Rec binding (which
                            // pointed at the owner report itself, useless
                            // for the requestpage's `Rec.<field>`
                            // expressions). xRec mirrors Rec — same
                            // contract pages have.
                            bottom.Vars["rec"] = registered;
                            bottom.Vars["xrec"] = registered;
                            recAssigned = true;
                        }
                    }
                    continue;
                }
                state.Pos++;
            }
        }
        finally
        {
            state.Pos = savedPos;
        }
    }

    /// <summary>
    /// Reads <c>keyword(alias; SourceTable)</c> at the current
    /// position and stamps the alias onto the outermost scope frame
    /// when the source table resolves through the catalog. Mirrors
    /// the alias-registration half of
    /// <see cref="TryConsumeAliasedSourceDeclaration"/> without
    /// emitting the property_object reference (that's still the main
    /// pass's job — pre-scan only seeds scope, doesn't double-emit).
    /// Assumes the cursor sits on the keyword token; advances past
    /// the source-table token on return.
    ///
    /// Returns the registered Record-typed binding (the source table
    /// the alias resolved to) so callers can also stamp it as Rec /
    /// xRec when the report-style "first dataitem is Rec" semantics
    /// apply. Returns null when no alias was registered (no name,
    /// no source, source didn't resolve, etc.).
    /// </summary>
    private static ResolvedVariableType? RegisterAlias(AlExtractionState state)
    {
        // We arrive at the keyword; advance past it and the `(`.
        state.Pos += 2;

        string? alias = null;
        if (state.Pos < state.Tokens.Count
            && (state.Tokens[state.Pos].Kind == AlTokenKind.Identifier
                || state.Tokens[state.Pos].Kind == AlTokenKind.QuotedIdentifier))
        {
            alias = state.Tokens[state.Pos].Value;
            state.Pos++;
        }

        // Skip to the `;` separator at depth 0.
        int depth = 0;
        while (state.Pos < state.Tokens.Count)
        {
            var current = state.Tokens[state.Pos];
            if (current.Kind == AlTokenKind.Punct)
            {
                if (current.Value == "(") { depth++; state.Pos++; continue; }
                if (current.Value == ")")
                {
                    if (depth == 0) return null;
                    depth--;
                    state.Pos++;
                    continue;
                }
                if (current.Value == ";" && depth == 0)
                {
                    state.Pos++;
                    break;
                }
            }
            state.Pos++;
        }

        // Second arg: source table. May be preceded by `Record`.
        if (state.Pos < state.Tokens.Count
            && state.Tokens[state.Pos].Kind == AlTokenKind.Identifier
            && AlExtractionState.IsAlObjectKeyword(state.Tokens[state.Pos].Value))
        {
            state.Pos++;
        }
        if (state.Pos >= state.Tokens.Count) return null;

        var sourceTok = state.Tokens[state.Pos];
        if (sourceTok.Kind != AlTokenKind.Identifier
            && sourceTok.Kind != AlTokenKind.QuotedIdentifier)
        {
            return null;
        }
        state.Pos++;

        // Namespace prefix: walk `.<segment>` chains so the LAST
        // segment is the actual type name. Mirrors the same path in
        // TryConsumeAliasedSourceDeclaration above.
        while (state.Pos + 1 < state.Tokens.Count
               && state.Tokens[state.Pos].Kind == AlTokenKind.Punct
               && state.Tokens[state.Pos].Value == "."
               && (state.Tokens[state.Pos + 1].Kind == AlTokenKind.Identifier
                   || state.Tokens[state.Pos + 1].Kind == AlTokenKind.QuotedIdentifier))
        {
            state.Pos++; // past .
            sourceTok = state.Tokens[state.Pos];
            state.Pos++;
        }

        ResolvedVariableType? registered = null;
        if (!string.IsNullOrEmpty(alias))
        {
            var resolved = state.Ctx.Resolver.ResolveTypeByName(sourceTok.Value, "Record");
            if (resolved is not null
                && string.Equals(resolved.Kind, "table", StringComparison.OrdinalIgnoreCase))
            {
                ScopeFrame? bottom = null;
                foreach (var frame in state.ScopeStack) bottom = frame;
                if (bottom is not null)
                {
                    registered = new ResolvedVariableType("Record", resolved.Name);
                    bottom.Vars[alias.ToLowerInvariant()] = registered;
                }
            }
        }
        return registered;
    }

    /// <summary>
    /// Emits a <c>field_access</c> reference when
    /// <paramref name="tok"/> resolves to a field on the most-recent
    /// dataitem / tableelement source table
    /// (<paramref name="currentSource"/>). Advances the cursor past
    /// the token on a hit so the orchestrator returns without its
    /// fallback advance. Silent no-op when
    /// <paramref name="currentSource"/> is null (no dataitem context
    /// yet) or the token isn't a known field on it — common case at
    /// object scope where bare tokens have many meanings.
    /// </summary>
    public static bool TryEmitBareFieldOnSource(
        AlExtractionState state, AlTypeRef? currentSource, AlToken tok)
    {
        if (currentSource is null) return false;
        if (tok.Kind != AlTokenKind.Identifier && tok.Kind != AlTokenKind.QuotedIdentifier)
        {
            return false;
        }

        var member = state.Ctx.Resolver.ResolveMember(currentSource, tok.Value);
        if (member is null) return false;
        if (!AlExtractionState.IsFieldKind(member.Kind)) return false;

        var targetOwner = member.DeclaringType ?? currentSource;
        state.EmitReference(new ExtractedReference(
            Line: tok.Line,
            Column: tok.Column,
            TargetAppId: targetOwner.AppId,
            TargetObjectKind: targetOwner.Kind,
            TargetObjectId: targetOwner.ObjectId,
            TargetObjectName: targetOwner.Name,
            TargetMemberName: member.Name,
            TargetMemberKind: member.Kind,
            ReferenceKind: "field_access"));
        state.Resolved++;
        state.Pos++;
        return true;
    }
}
