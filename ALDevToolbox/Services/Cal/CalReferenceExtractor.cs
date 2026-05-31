namespace ALDevToolbox.Services.Cal;

/// <summary>A resolved C/AL object type a receiver points at, by (kind, id).</summary>
public readonly record struct CalTypeRef(string Kind, int Id);

/// <summary>
/// The variable / Rec scope a body executes in. Built by
/// <see cref="ALDevToolbox.Services.ObjectExplorer.CalImportService"/> from the
/// owner object's globals plus the procedure's parameters and locals.
/// </summary>
public sealed class CalExtractScope
{
    /// <summary>Variable name → the object type it's declared as (Record/Codeunit/Page/…), case-insensitive.</summary>
    public Dictionary<string, CalTypeRef> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The object owning the body — bare <c>Proc(...)</c> calls resolve here.</summary>
    public string? OwnerKind { get; init; }
    public int? OwnerId { get; init; }

    /// <summary>What <c>Rec</c> / <c>xRec</c> / an implicit bare <c>"Field"</c> binds to (a table). Null for codeunits.</summary>
    public CalTypeRef? Rec { get; init; }
}

/// <summary>One extracted call-site reference. Target name is resolved by id later.</summary>
public sealed record CalRef(
    int Line, int Column,
    string TargetKind, int TargetId,
    string? MemberName, string MemberKind,
    string ReferenceKind);

public sealed record CalExtractionResult(IReadOnlyList<CalRef> References, int UnresolvedReceivers);

/// <summary>
/// Walks a C/AL procedure / trigger body and emits <c>method_call</c> and
/// <c>field_access</c> references to other objects, resolving receivers by id
/// through the supplied <see cref="CalExtractScope"/>. The statement-level
/// analogue of the AL <c>AlReferenceExtractor</c>'s member-chain core — it does
/// not re-parse object structure (the C/AL parser already did that), so it only
/// needs the body text + scope.
///
/// <para>Resolves the common, high-value shapes:</para>
/// <list type="bullet">
///   <item><c>Receiver.Method(...)</c> → method_call on the receiver's type
///     (unless Method is a runtime built-in, in which case it's skipped — and
///     if it's a field-name-taking built-in like <c>SETRANGE</c>, the first
///     argument is emitted as a field_access).</item>
///   <item><c>Receiver."Field"</c> / <c>Receiver.Field</c> → field_access.</item>
///   <item>bare <c>Proc(...)</c> → method_call on the owner object.</item>
///   <item>bare <c>"Field"</c> and bare field-name-taking built-ins
///     (<c>TESTFIELD("No.")</c>) → field_access on the implicit <c>Rec</c>.</item>
/// </list>
/// Deeper chains (<c>a.b.c</c>) resolve only the first hop; option access
/// (<c>x::Value</c>) and unresolved receivers are skipped (and counted for the
/// import diagnostic).
/// </summary>
public static class CalReferenceExtractor
{
    public static CalExtractionResult Extract(string body, CalExtractScope scope)
    {
        var refs = new List<CalRef>();
        int unresolved = 0;
        var t = CalLexer.Tokenize(body);

        for (int k = 0; k < t.Count; k++)
        {
            var tok = t[k];
            if (tok.Kind != CalTokenKind.Identifier && tok.Kind != CalTokenKind.QuotedIdentifier)
                continue;
            // A token already consumed as a member (preceded by '.') isn't a head.
            if (k > 0 && t[k - 1].Kind == CalTokenKind.Dot) continue;
            // Option value qualifier (x::Value) — the head is enumerating an option, not a ref.
            if (k + 1 < t.Count && t[k + 1].Kind == CalTokenKind.ColonColon) { k++; continue; }
            if (tok.Kind == CalTokenKind.Identifier && CalBuiltinMethods.IsKeyword(tok.Text)) continue;

            var next = Peek(t, k + 1);

            // ── Receiver.Member ─────────────────────────────────────────
            if (next.Kind == CalTokenKind.Dot)
            {
                var member = Peek(t, k + 2);
                if (member.Kind is not (CalTokenKind.Identifier or CalTokenKind.QuotedIdentifier)) continue;

                var receiver = ResolveHead(tok, scope);
                if (receiver is null)
                {
                    if (!CalBuiltinMethods.IsStaticReceiver(tok.Text)) unresolved++;
                    k += 1; // step past the head; the dot/member get re-evaluated harmlessly
                    continue;
                }

                var afterMember = Peek(t, k + 3);
                if (afterMember.Kind == CalTokenKind.LParen)
                {
                    if (CalBuiltinMethods.IsReceiverMethod(member.Text))
                    {
                        if (CalBuiltinMethods.IsFieldNameTaking(member.Text))
                            EmitFieldArg(t, k + 3, receiver.Value, refs);
                    }
                    else
                    {
                        refs.Add(new CalRef(member.Line, member.Column,
                            receiver.Value.Kind, receiver.Value.Id, member.Text, "procedure", "method_call"));
                    }
                }
                else if (!CalBuiltinMethods.IsReceiverMethod(member.Text))
                {
                    // Receiver.Field (read). Quoted members are certainly fields;
                    // bare ones are usually fields too (a parameterless custom
                    // function is rarer and still resolves by name at query time).
                    refs.Add(new CalRef(member.Line, member.Column,
                        receiver.Value.Kind, receiver.Value.Id, member.Text, "field", "field_access"));
                }
                k += 2; // advance to the member; loop ++ moves past it
                continue;
            }

            // ── bare Name(...) ──────────────────────────────────────────
            if (next.Kind == CalTokenKind.LParen && tok.Kind == CalTokenKind.Identifier)
            {
                if (CalBuiltinMethods.IsBareFunction(tok.Text) || CalBuiltinMethods.IsStaticReceiver(tok.Text))
                    continue;
                if (scope.Variables.ContainsKey(tok.Text)) continue; // array index / var, not a call

                if (CalBuiltinMethods.IsFieldNameTaking(tok.Text) && scope.Rec is not null)
                {
                    EmitFieldArg(t, k + 1, scope.Rec.Value, refs); // implicit Rec.TESTFIELD("X")
                    continue;
                }
                if (CalBuiltinMethods.IsReceiverMethod(tok.Text)) continue; // implicit Rec built-in

                // A procedure on the owner object.
                if (scope.OwnerKind is not null && scope.OwnerId is not null)
                    refs.Add(new CalRef(tok.Line, tok.Column,
                        scope.OwnerKind, scope.OwnerId.Value, tok.Text, "procedure", "method_call"));
                continue;
            }

            // ── bare "Field" (implicit Rec field) ───────────────────────
            if (tok.Kind == CalTokenKind.QuotedIdentifier && scope.Rec is not null)
            {
                refs.Add(new CalRef(tok.Line, tok.Column,
                    scope.Rec.Value.Kind, scope.Rec.Value.Id, tok.Text, "field", "field_access"));
            }
        }

        return new CalExtractionResult(refs, unresolved);
    }

    private static CalTypeRef? ResolveHead(CalToken head, CalExtractScope scope)
    {
        if (string.Equals(head.Text, "Rec", StringComparison.OrdinalIgnoreCase)
            || string.Equals(head.Text, "xRec", StringComparison.OrdinalIgnoreCase))
            return scope.Rec;
        return scope.Variables.TryGetValue(head.Text, out var tr) ? tr : null;
    }

    /// <summary>Emits a field_access for the first argument after the <c>(</c> at <paramref name="lparenIndex"/>, when it names a field.</summary>
    private static void EmitFieldArg(List<CalToken> t, int lparenIndex, CalTypeRef receiver, List<CalRef> refs)
    {
        var arg = Peek(t, lparenIndex + 1);
        if (arg.Kind is CalTokenKind.QuotedIdentifier or CalTokenKind.Identifier)
        {
            // A bare-identifier first arg could be a local var rather than a field;
            // quoted names are unambiguous. Emit for both — the id-resolved target
            // name lets the query layer drop a miss harmlessly.
            refs.Add(new CalRef(arg.Line, arg.Column,
                receiver.Kind, receiver.Id, arg.Text, "field", "field_access"));
        }
    }

    private static CalToken Peek(List<CalToken> t, int i)
        => i >= 0 && i < t.Count ? t[i] : new CalToken(CalTokenKind.Other, "", 0, 0);
}
