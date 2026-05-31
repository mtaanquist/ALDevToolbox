using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.Cal;

/// <summary>
/// Section-driven structural parser for a single C/AL object block. It is not a
/// full grammar: it locates the brace-delimited sections by their known
/// keywords, then extracts the pieces the Object Explorer needs — the object
/// header, table FIELDS, CODE VAR globals, PROCEDUREs, <c>OnXxx</c> triggers,
/// and page CONTROLS. Procedure / trigger bodies and their scope (params,
/// locals, owner globals) are captured so the Part-2 reference walker can run
/// without re-parsing. All line/column numbers are slice-relative (line 1 = the
/// <c>OBJECT</c> header), matching how each object is stored as its own source
/// file. Pure — no IO, no DB.
/// </summary>
public static partial class CalObjectParser
{
    private static readonly HashSet<string> SectionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "OBJECT-PROPERTIES", "PROPERTIES", "FIELDS", "KEYS", "FIELDGROUPS",
        "CODE", "CONTROLS", "DATASET", "ELEMENTS", "REQUESTPAGE", "RDLDATA",
        "RDLLAYOUT", "WORDLAYOUT", "LABELS", "MENUNODES", "EVENTS",
    };

    [GeneratedRegex(@"Version List=(?<v>[^;]*);")]
    private static partial Regex VersionListRegex();

    [GeneratedRegex(@"SourceTable=Table(?<id>\d+)")]
    private static partial Regex SourceTableRegex();

    // Procedure header: optional LOCAL/INTERNAL, PROCEDURE, name@id, then '('.
    [GeneratedRegex(@"(?im)^\s*(?<local>LOCAL\s+)?(?:INTERNAL\s+)?PROCEDURE\s+(?<name>""[^""]+""|[A-Za-z_]\w*)@(?<eid>\d+)\s*\(")]
    private static partial Regex ProcedureHeaderRegex();

    // A FIELDS record opens at depth-1 with '{ <no> ; ...'.
    [GeneratedRegex(@"(?<name>""[^""]+""|[A-Za-z_][\w .,/&-]*?)@(?<eid>\d+)\s*:\s*(?<type>.*)", RegexOptions.Singleline)]
    private static partial Regex VarDeclRegex();

    public static CalParsedObject Parse(CalObjectBlock block)
    {
        var text = block.RawText;
        var kind = CalObjectKinds.TypeToKind.TryGetValue(block.TypeKeyword, out var k)
            ? k
            : block.TypeKeyword.ToLowerInvariant();

        int bodyOpen = text.IndexOf('{');
        int bodyClose = bodyOpen >= 0 ? CalScan.FindMatchingBrace(text, bodyOpen) : -1;
        if (bodyOpen < 0 || bodyClose < 0)
            return new CalParsedObject(kind, block.Id, block.Name, null, null,
                [], [], [], [], []);

        var sections = ExtractSections(text, bodyOpen + 1, bodyClose);

        string? versionList = null;
        if (sections.TryGetValue("OBJECT-PROPERTIES", out var op))
        {
            var vm = VersionListRegex().Match(text, op.Start, op.End - op.Start);
            if (vm.Success) versionList = vm.Groups["v"].Value.Trim();
        }

        string? sourceTableId = null;
        var triggers = new List<CalTrigger>();
        if (sections.TryGetValue("PROPERTIES", out var props))
        {
            var sm = SourceTableRegex().Match(text, props.Start, props.End - props.Start);
            if (sm.Success) sourceTableId = sm.Groups["id"].Value;
            triggers.AddRange(ScanTriggers(text, props.Start, props.End));
        }

        var fields = new List<CalField>();
        if (sections.TryGetValue("FIELDS", out var fs))
        {
            fields.AddRange(ParseFields(text, fs.Start, fs.End));
            // Field-level OnValidate / OnLookup triggers live inside the records.
            triggers.AddRange(ScanTriggers(text, fs.Start, fs.End));
        }

        var globals = new List<CalVariable>();
        var procedures = new List<CalProcedure>();
        if (sections.TryGetValue("CODE", out var code))
        {
            ParseCode(text, code.Start, code.End, globals, procedures);
        }

        var pageFields = new List<CalPageField>();
        if (sections.TryGetValue("CONTROLS", out var controls))
        {
            pageFields.AddRange(ParsePageFields(text, controls.Start, controls.End));
            triggers.AddRange(ScanTriggers(text, controls.Start, controls.End));
        }

        return new CalParsedObject(
            kind, block.Id, block.Name, versionList, sourceTableId,
            fields, globals, procedures, triggers, pageFields);
    }

    /// <summary>
    /// Maps each top-level <c>NAME { … }</c> section inside the object body to
    /// the char span of its content. First occurrence wins.
    /// </summary>
    private static Dictionary<string, (int Start, int End)> ExtractSections(string text, int bodyStart, int bodyEnd)
    {
        var sections = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        int i = bodyStart;
        while (i < bodyEnd)
        {
            while (i < bodyEnd && char.IsWhiteSpace(text[i])) i++;
            if (i >= bodyEnd) break;
            if (!CalScan.IsWordStart(text[i])) { i++; continue; }

            int ws = i;
            while (i < bodyEnd && (CalScan.IsWordChar(text[i]) || text[i] == '-')) i++;
            string name = text.Substring(ws, i - ws);

            int j = i;
            while (j < bodyEnd && char.IsWhiteSpace(text[j])) j++;
            if (j < bodyEnd && text[j] == '{')
            {
                int close = CalScan.FindMatchingBrace(text, j);
                if (close < 0) break;
                if (SectionKeywords.Contains(name) && !sections.ContainsKey(name))
                    sections[name] = (j + 1, close);
                i = close + 1;
            }
            else
            {
                while (i < bodyEnd && text[i] != '\n') i++;
            }
        }
        return sections;
    }

    /// <summary>Parses the brace-delimited records of a FIELDS section: <c>{ &lt;no&gt; ; ; &lt;name&gt; ; &lt;type&gt; ; … }</c>.</summary>
    private static IEnumerable<CalField> ParseFields(string text, int start, int end)
    {
        int i = start;
        while (i < end)
        {
            if (text[i] == '{')
            {
                int close = CalScan.FindMatchingBrace(text, i);
                if (close < 0) yield break;
                int line = CalScan.LineAt(text, i);
                var cols = CalScan.SplitTopLevelSemicolons(text, i + 1, close);
                if (cols.Count >= 4)
                {
                    var idStr = text[cols[0].Start..cols[0].End].Trim();
                    var name = text[cols[2].Start..cols[2].End].Trim();
                    var type = text[cols[3].Start..cols[3].End].Trim();
                    if (int.TryParse(idStr, out var fid) && name.Length > 0)
                        yield return new CalField(fid, Unquote(name), type, line);
                }
                i = close + 1;
            }
            else i++;
        }
    }

    /// <summary>Parses page CONTROLS records, yielding fields bound via <c>SourceExpr</c>.</summary>
    private static IEnumerable<CalPageField> ParsePageFields(string text, int start, int end)
    {
        int i = start;
        while (i < end)
        {
            if (text[i] == '{')
            {
                int close = CalScan.FindMatchingBrace(text, i);
                if (close < 0) yield break;
                var record = text[(i + 1)..close];
                // Only Field controls carry a SourceExpr; capture the bound expression as the name.
                var m = Regex.Match(record, @"SourceExpr=(?<e>""[^""]+""|[^;}\r\n]+)");
                if (m.Success)
                {
                    var expr = m.Groups["e"].Value.Trim();
                    var name = Unquote(expr);
                    if (name.Length > 0)
                        yield return new CalPageField(name, CalScan.LineAt(text, i));
                }
                i = close + 1;
            }
            else i++;
        }
    }

    /// <summary>Parses the CODE section: leading VAR globals, then each PROCEDURE with its scope and body.</summary>
    private static void ParseCode(
        string text, int start, int end,
        List<CalVariable> globals, List<CalProcedure> procedures)
    {
        // First procedure header bounds the object-level VAR block.
        var first = ProcedureHeaderRegex().Match(text, start, end - start);
        int globalsEnd = first.Success ? first.Index : end;

        int varKw = IndexOfWord(text, "VAR", start, globalsEnd);
        if (varKw >= 0)
            globals.AddRange(ParseVarBlock(text, varKw + 3, globalsEnd));

        var m = first;
        while (m.Success && m.Index < end)
        {
            var name = Unquote(m.Groups["name"].Value);
            bool isLocal = m.Groups["local"].Success;
            int parenOpen = m.Index + m.Length - 1;          // the '(' the regex ended on
            int parenClose = CalScan.FindMatchingParen(text, parenOpen);
            if (parenClose < 0) break;

            var parameters = ParseParams(text, parenOpen + 1, parenClose);

            // Signature: from '(' to the terminating ';' of the header.
            int semi = text.IndexOf(';', parenClose);
            if (semi < 0 || semi > end) semi = parenClose;
            var header = text[(parenClose + 1)..semi].Trim();
            string? returnType = null;
            if (header.StartsWith(':'))
                returnType = header[1..].Trim();
            else if (header.Length > 0 && header.Contains(':'))
                returnType = header[(header.IndexOf(':') + 1)..].Trim();   // named return value
            var signature = RenderSignature(parameters);

            // Body: first BEGIN after the header (a local VAR block may sit between).
            int searchFrom = semi + 1;
            int nextProc = ProcedureHeaderRegex().Match(text, searchFrom, end - searchFrom) is { Success: true } nm ? nm.Index : end;

            int localVar = IndexOfWord(text, "VAR", searchFrom, nextProc);
            int beginIdx = IndexOfWord(text, "BEGIN", searchFrom, nextProc);
            var locals = (localVar >= 0 && (beginIdx < 0 || localVar < beginIdx))
                ? ParseVarBlock(text, localVar + 3, beginIdx >= 0 ? beginIdx : nextProc)
                : new List<CalVariable>();

            string body = string.Empty;
            int? endLine = null;
            if (beginIdx >= 0)
            {
                int bodyEnd = CalScan.FindMatchingEnd(text, beginIdx);
                if (bodyEnd > 0)
                {
                    body = text[beginIdx..bodyEnd];
                    endLine = CalScan.LineAt(text, bodyEnd);
                }
            }

            procedures.Add(new CalProcedure(
                name, isLocal, signature, returnType,
                CalScan.LineAt(text, m.Index),
                endLine, body,
                beginIdx >= 0 ? CalScan.LineAt(text, beginIdx) : CalScan.LineAt(text, m.Index),
                parameters, locals));

            m = ProcedureHeaderRegex().Match(text, nextProc, end - nextProc);
        }
    }

    /// <summary>Parses <c>Name@id : Type;</c> declarations between two offsets (object globals or procedure locals).</summary>
    private static List<CalVariable> ParseVarBlock(string text, int start, int end)
    {
        var vars = new List<CalVariable>();
        foreach (var (s, e) in CalScan.SplitTopLevelSemicolons(text, start, end))
        {
            var span = text[s..e];
            if (string.IsNullOrWhiteSpace(span)) continue;
            var m = VarDeclRegex().Match(span);
            if (!m.Success) continue;
            int nameOffset = s + m.Groups["name"].Index;
            var (typeKeyword, targetId, typeName) = ParseType(m.Groups["type"].Value.Trim());
            vars.Add(new CalVariable(
                Unquote(m.Groups["name"].Value.Trim()),
                typeKeyword, targetId, typeName,
                CalScan.LineAt(text, nameOffset),
                CalScan.ColumnAt(text, nameOffset),
                CalScan.ColumnAt(text, nameOffset) + m.Groups["name"].Value.Trim().Length));
        }
        return vars;
    }

    private static List<CalVariable> ParseParams(string text, int start, int end)
    {
        var ps = new List<CalVariable>();
        foreach (var (s, e) in CalScan.SplitTopLevelSemicolons(text, start, end))
        {
            var raw = text[s..e].Trim();
            if (raw.Length == 0) continue;
            bool byRef = false;
            if (raw.StartsWith("VAR ", StringComparison.OrdinalIgnoreCase))
            {
                byRef = true;
                raw = raw[4..].TrimStart();
            }
            var m = VarDeclRegex().Match(raw);
            if (!m.Success) continue;
            var (typeKeyword, targetId, typeName) = ParseType(m.Groups["type"].Value.Trim());
            ps.Add(new CalVariable(
                Unquote(m.Groups["name"].Value.Trim()), typeKeyword, targetId, typeName,
                0, 0, 0, byRef));
        }
        return ps;
    }

    /// <summary>
    /// Splits a C/AL type into (keyword, target-object-id, display name).
    /// <c>Record 36</c> → (Record, 36, "Record 36"); <c>TEMPORARY Codeunit 80</c>
    /// → (Codeunit, 80, "Codeunit 80"); scalars / option strings → (null, null, verbatim).
    /// </summary>
    private static (string? Keyword, int? TargetId, string TypeName) ParseType(string type)
    {
        var t = type;
        if (t.StartsWith("TEMPORARY ", StringComparison.OrdinalIgnoreCase))
            t = t["TEMPORARY ".Length..].TrimStart();

        var m = Regex.Match(t, @"^(?<kw>[A-Za-z]+)\s+(?<id>\d+)");
        if (m.Success && CalObjectKinds.ObjectTypeKeywordToKind.ContainsKey(m.Groups["kw"].Value))
        {
            var kw = m.Groups["kw"].Value;
            var id = int.Parse(m.Groups["id"].Value);
            return (kw, id, $"{kw} {id}");
        }
        // Scalar / system / option type — keep the first line only.
        var firstLine = t.Split('\n')[0].Trim();
        return (null, null, firstLine);
    }

    /// <summary>
    /// Finds every <c>OnXxx=BEGIN…END;</c> / <c>OnXxx=VAR…BEGIN…END;</c> trigger
    /// property in a span. Anchors on a property assignment (<c>name=</c>) whose
    /// value opens a code block, so <c>:=</c> assignments aren't mistaken for triggers.
    /// </summary>
    private static IEnumerable<CalTrigger> ScanTriggers(string text, int start, int end)
    {
        int i = start;
        while (i < end)
        {
            char c = text[i];
            if (c == '\'' || c == '"') { int e = CalScan.QuoteSpanEnd(text, i); i = e == i ? i + 1 : e + 1; continue; }
            if (c == '/' && i + 1 < end && text[i + 1] == '/') { i = CalScan.SkipLineComment(text, i) + 1; continue; }

            if (c == '=' && i > start && CalScan.IsWordChar(text[i - 1]) && text[i - 1] != ':')
            {
                // Read the property name ending at this '='.
                int ne = i;
                int ns = ne;
                while (ns > start && CalScan.IsWordChar(text[ns - 1])) ns--;
                // Reject ':=' (assignment): char before the name must not be ':'.
                if (ns > start && text[ns - 1] == ':') { i++; continue; }
                string name = text[ns..ne];

                int v = i + 1;
                while (v < end && (text[v] == ' ' || text[v] == '\t')) v++;
                int? localStart = null;
                if (MatchesWord(text, v, "VAR")) { localStart = v + 3; }
                if (localStart.HasValue || MatchesWord(text, v, "BEGIN"))
                {
                    int beginIdx = MatchesWord(text, v, "BEGIN") ? v : IndexOfWord(text, "BEGIN", v, end);
                    if (beginIdx >= 0)
                    {
                        int bodyEnd = CalScan.FindMatchingEnd(text, beginIdx);
                        if (bodyEnd > 0)
                        {
                            var locals = localStart.HasValue
                                ? ParseVarBlock(text, localStart.Value, beginIdx)
                                : new List<CalVariable>();
                            yield return new CalTrigger(
                                name, CalScan.LineAt(text, ns),
                                text[beginIdx..bodyEnd], CalScan.LineAt(text, beginIdx), locals);
                            i = bodyEnd;
                            continue;
                        }
                    }
                }
            }
            i++;
        }
    }

    private static string RenderSignature(IReadOnlyList<CalVariable> parameters)
    {
        if (parameters.Count == 0) return "()";
        var parts = parameters.Select(p =>
            (p.ByRef ? "var " : string.Empty) + p.Name + ": " + p.TypeName);
        return "(" + string.Join("; ", parts) + ")";
    }

    private static string Unquote(string s)
        => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    /// <summary>Index of a whole-word keyword between two offsets, or -1.</summary>
    private static int IndexOfWord(string text, string word, int start, int end)
    {
        int i = start;
        while (i < end)
        {
            char c = text[i];
            if (c == '\'' || c == '"') { int e = CalScan.QuoteSpanEnd(text, i); i = e == i ? i + 1 : e + 1; continue; }
            if (c == '/' && i + 1 < end && text[i + 1] == '/') { i = CalScan.SkipLineComment(text, i) + 1; continue; }
            if (MatchesWord(text, i, word)) return i;
            i++;
        }
        return -1;
    }

    private static bool MatchesWord(string text, int i, string word)
    {
        if (i < 0 || i + word.Length > text.Length) return false;
        if (i > 0 && CalScan.IsWordChar(text[i - 1])) return false;
        if (!text.AsSpan(i, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase)) return false;
        int after = i + word.Length;
        return after >= text.Length || !CalScan.IsWordChar(text[after]);
    }
}
