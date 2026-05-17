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
    public static bool TryConsumeAliasedSourceDeclaration(
        AlExtractionState state, string keyword, AlToken tok)
    {
        if (tok.Kind != AlTokenKind.Identifier) return false;
        if (!string.Equals(tok.Value, keyword, StringComparison.OrdinalIgnoreCase)) return false;
        if (state.Pos + 1 >= state.Tokens.Count) return false;
        var next = state.Tokens[state.Pos + 1];
        if (next.Kind != AlTokenKind.Punct || next.Value != "(") return false;

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
                    if (depth == 0) return true;
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
        if (state.Pos >= state.Tokens.Count) return true;

        var sourceTok = state.Tokens[state.Pos];
        if (sourceTok.Kind != AlTokenKind.Identifier
            && sourceTok.Kind != AlTokenKind.QuotedIdentifier)
        {
            return true;
        }

        var resolved = state.Ctx.Resolver.ResolveTypeByName(sourceTok.Value, "Record");
        if (resolved is not null
            && string.Equals(resolved.Kind, "table", StringComparison.OrdinalIgnoreCase))
        {
            state.Refs.Add(new ExtractedReference(
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
        }
        state.Pos++;
        return true;
    }
}
