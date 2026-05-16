using System;
using System.Collections.Generic;

namespace ALDevToolbox.Services.Al;

/// <summary>
/// Names of AL methods + system fields the runtime adds to every value
/// of a given kind. The reference extractor consults this list AFTER
/// failing to resolve a member through the catalog (oe_module_symbols)
/// so calls like <c>Cust.Insert(true)</c>, <c>Cust.SetRange(...)</c>,
/// <c>JObject.Add(...)</c>, <c>SomeText.Contains(...)</c> are quietly
/// skipped instead of inflating the unresolved counter.
///
/// Coverage is the everyday-AL subset; the canonical source is
/// <see href="https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/developer/methods-auto/library"/>
/// — extend this list when a real-world import logs an unresolved
/// receiver that's actually a built-in we missed.
///
/// Why a hand-curated allow-list instead of querying Microsoft's
/// system-symbols package: the symbol package isn't shipped in the
/// .app files our import pipeline ingests, and even when it would
/// be available the list churns slowly enough that an explicit set
/// (with the MS doc URL above the table for follow-up) reads more
/// honestly than a "we couldn't resolve" log we'd have to remember
/// to triage.
/// </summary>
public static class AlBuiltinMethods
{
    /// <summary>
    /// Methods callable on a Record value — every table / tableextension
    /// receiver gets them by default. From the BC docs' "Record Methods"
    /// page; covers the everyday CRUD + filter + field-reflection set.
    /// </summary>
    public static readonly HashSet<string> RecordMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // CRUD + cursor.
        "Insert", "Modify", "Delete", "DeleteAll", "ModifyAll", "Rename", "Reset",
        "Get", "GetBySystemId", "Find", "FindFirst", "FindLast", "FindSet", "Next",
        // Filtering / sorting.
        "SetRange", "SetFilter", "GetFilter", "GetFilters", "ClearMarks",
        "SetCurrentKey", "SetView", "GetView", "SetPosition", "GetPosition",
        "SetAutoCalcFields", "CalcFields", "CalcSums",
        "CopyFilter", "CopyFilters", "FilterGroup", "HasFilter",
        "Mark", "MarkedOnly", "Marking",
        "Ascending", "IsTemporary",
        // Field reflection / introspection.
        "FieldCaption", "FieldName", "FieldNo", "FieldExists", "FieldActive",
        "TableCaption", "TableName",
        "TestField", "Validate", "ValidateAll",
        "FieldError",
        // Count / state.
        "IsEmpty", "Count", "CountApprox",
        "GetRangeMin", "GetRangeMax",
        // Copy / transfer.
        "Copy", "TransferFields", "Init", "InitRecord",
        "SetRecFilter", "SetTable",
        "ChangeCompany", "CurrentCompany",
        // Locking / async.
        "LockTable", "ReadIsolation", "ReadPermission", "WritePermission",
        // Misc.
        "AddLoadFields", "AddLink", "GetLink", "RemoveLink",
        "Number", "RecordLevelLocking",
        // Note: AssistEdit / Lookup / Drilldown are intentionally NOT
        // listed here. Microsoft's Base App declares user procedures
        // with those names (e.g. Sales Header.AssistEdit is a real
        // public procedure that opens a number-series picker, not the
        // field-level built-in). Adding them as built-ins would silently
        // mask resolver misses instead of bumping the unresolved
        // counter — defeating the diagnostic signal we use to spot
        // catalog gaps.
    };

    /// <summary>
    /// System-added fields exposed on every Record. These don't appear
    /// in <c>oe_module_symbols</c> but a <c>Cust."SystemId"</c> access
    /// shouldn't count as unresolved.
    /// </summary>
    public static readonly HashSet<string> RecordSystemFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemId",
        "SystemCreatedAt",
        "SystemCreatedBy",
        "SystemModifiedAt",
        "SystemModifiedBy",
        "SystemRowVersion",
    };

    /// <summary>
    /// Methods callable on a Codeunit instance — primarily the Run
    /// family. Codeunit-declared methods that the user defined come
    /// from <c>oe_module_symbols</c>; this list is the runtime layer.
    /// </summary>
    public static readonly HashSet<string> CodeunitMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Run", "RunModal", "RunWithCheck",
    };

    /// <summary>
    /// Methods exposed on a Text receiver — <c>someText.Contains(...)</c>.
    /// AL accepts these as instance methods on Text variables.
    /// </summary>
    public static readonly HashSet<string> TextMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Contains", "StartsWith", "EndsWith",
        "IndexOf", "IndexOfAny", "LastIndexOf",
        "Substring", "Split", "Replace",
        "ToLower", "ToUpper",
        "Trim", "TrimEnd", "TrimStart",
        "PadLeft", "PadRight",
        "Length",
    };

    /// <summary>
    /// Methods on the AL JSON value family (JsonObject / JsonArray /
    /// JsonToken / JsonValue). Common enough to land here even though
    /// these aren't AL "object kinds" — variables typed as JsonObject
    /// etc. still pass through the extractor.
    /// </summary>
    public static readonly HashSet<string> JsonMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // JsonObject.
        "Add", "Get", "Contains", "Remove", "Replace", "Keys", "Values",
        // JsonArray.
        "AddFirst", "AddLast", "AsArray",
        // JsonToken / JsonValue.
        "AsValue", "AsObject", "AsToken",
        "AsBoolean", "AsBigInteger", "AsDecimal", "AsDuration", "AsInteger",
        "AsText", "AsCode", "AsDate", "AsTime", "AsDateTime", "AsGuid",
        "IsArray", "IsObject", "IsValue", "IsNull",
        "ReadFrom", "WriteTo",
        "SelectToken", "SelectValue",
        // Shared.
        "Count",
    };

    /// <summary>
    /// Methods on the List / Dictionary collection generics. Users
    /// declare <c>List of [Text]</c>, <c>Dictionary of [Code[20],
    /// Integer]</c> — the receiver type doesn't resolve to an AL
    /// object, but the method calls still pass through the chain.
    /// </summary>
    public static readonly HashSet<string> CollectionMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // List.
        "Add", "AddRange", "Remove", "RemoveAt", "RemoveRange",
        "Get", "GetRange", "Set",
        "IndexOf", "LastIndexOf",
        "Contains",
        "Reverse", "Count", "ToArray",
        // Dictionary.
        "ContainsKey", "Keys", "Values",
        // Shared.
        "Clear",
    };

    /// <summary>
    /// Catch-all: a name we treat as built-in regardless of receiver
    /// kind. Add sparingly — these get filtered everywhere they
    /// appear in a chain.
    /// </summary>
    public static readonly HashSet<string> CommonMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // Lots of types expose a Length() / Count() — Text, List,
        // JsonArray, etc.
        "Length",
        "Count",
    };

    /// <summary>
    /// AL system functions callable without a receiver: <c>Message('Hello')</c>,
    /// <c>Error('Boom')</c>, <c>StrSubstNo(...)</c> etc. The bare-self-call
    /// resolver consults this list first so it doesn't try to look these up
    /// as procedures on the file's owner object — a same-named user procedure
    /// would shadow the system function in AL, but in practice Microsoft's
    /// code doesn't, and treating the system function as the dominant case
    /// stays quiet across the imported corpus.
    ///
    /// Coverage is the high-frequency subset of
    /// <see href="https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/developer/methods-auto/library"/>'s
    /// "Essential functions". Extend when a real import logs a false-positive
    /// unresolved self-call for a name that's actually a system function.
    /// </summary>
    public static readonly HashSet<string> BareCallableFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Diagnostic / flow.
        "Message", "Error", "Confirm", "Exit", "Quit", "Sleep",
        "GetLastErrorText", "GetLastErrorCode", "GetLastErrorObject",
        "GetLastErrorCallStack", "ClearLastError", "AssertError",
        // Strings.
        "Format", "Evaluate", "StrLen", "StrSubstNo", "StrPos", "StrCheckSum",
        "CopyStr", "DelChr", "DelStr", "InsStr", "MaxStrLen", "IncStr",
        "LowerCase", "UpperCase", "ConvertStr", "SelectStr",
        "PadStr",
        // Numerics.
        "Abs", "Power", "Sqrt", "Round", "Random", "RandomRange",
        // Date / time.
        "Today", "Time", "CurrentDateTime", "WorkDate", "CreateDateTime",
        "Date2DMY", "Date2DWY", "DMY2Date", "DWY2Date", "CalcDate",
        // Identity / environment.
        "CreateGuid", "CompanyName", "UserId", "UserSecurityId",
        "GuiAllowed", "IsServiceTier", "ApplicationLanguage",
        "GlobalLanguage", "WindowsLanguage",
        // Misc.
        "TypeNameOf", "Database",
        // Variable lifecycle.
        "Clear", "ClearAll",
        // Numeric / type predicates that take an arg with no receiver.
        "IsNull", "IsNullGuid",
        // Cast / format helpers.
        "Increment", "Decrement",
        // Transaction control — top-level system function.
        "Commit",
    };

    /// <summary>
    /// AL <b>declarative-DSL</b> keywords that introduce nested
    /// structure inside an object body but are NOT procedure calls.
    /// Pages, pageextensions, tableextensions, reports, xmlports,
    /// enums and permissionsets all use this `keyword(args) { ... }`
    /// syntax for their layout / fields / actions / values.
    ///
    /// Without explicit handling these surface as <c>bare-call</c>
    /// unresolveds — `area(content)` inside a page body looks
    /// identical to a procedure invocation to the lexer. The
    /// extractor skips them silently (no reference emitted, no
    /// counter bump) so the diagnostic samples can focus on real
    /// gaps.
    ///
    /// Coverage note: <c>field</c>, <c>trigger</c>, <c>group</c>,
    /// <c>value</c> overlap with names that legitimately exist as
    /// AL identifiers in other contexts (a procedure named <c>group</c>
    /// is rare but legal). The skip is unconditional here because
    /// the matching call sites are always in declarative position;
    /// false positives would need a procedure named after a DSL
    /// keyword AND invoked from a place the bare-call resolver runs
    /// — vanishingly rare in practice.
    /// </summary>
    public static readonly HashSet<string> ObjectDslKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Pages / pageextensions: layout containers and content.
        "area", "group", "field", "repeater", "cuegroup", "part",
        "systempart", "usercontrol", "fixed", "grid", "label",
        "separator", "filter",
        // Pages / pageextensions: actions.
        "action", "actionref", "customaction", "actiongroup",
        // Pageextensions: layout / action manipulators.
        "modify", "addafter", "addbefore", "addlast", "addfirst",
        "movefirst", "movebefore", "moveafter", "movelast",
        "addchange",
        // Tables / tableextensions: structure.
        "key", "fieldgroup",
        // Reports: structure.
        "dataitem", "column", "requestpage", "dataset", "rendering",
        "layout",
        // XMLports: schema.
        "textelement", "tableelement", "fieldelement", "fieldattribute",
        "textattribute",
        // Enums.
        "value",
        // PermissionSets.
        "permissions", "tabledata", "includedpermissionsets",
        // Controladd-ins, queries, profiles — declarative children.
        "controladdin", "querytype", "elements", "filters", "orderby",
        "dataitemlink", "column",
    };

    /// <summary>
    /// True for an AL declarative-DSL keyword that opens a nested
    /// block inside an object body. See <see cref="ObjectDslKeywords"/>
    /// for the rationale.
    /// </summary>
    public static bool IsObjectDslKeyword(string name) =>
        !string.IsNullOrEmpty(name) && ObjectDslKeywords.Contains(name);

    /// <summary>
    /// AL built-in static APIs callable with no instance — typically
    /// invoked as <c>Kind.Method(...)</c> from anywhere in code.
    /// <c>CODEUNIT.Run(Codeunit::"Foo")</c> and <c>PAGE.RunModal(...)</c>
    /// are the canonical examples; <c>NavApp.GetCurrentModuleInfo(...)</c>
    /// is the equivalent for app metadata. The extractor doesn't model
    /// the methods on these static APIs (no symbol-package entries
    /// for them), so chains through them resolve nothing — but they
    /// shouldn't pollute the diagnostic either.
    /// </summary>
    public static readonly HashSet<string> BuiltinStaticReceivers = new(StringComparer.OrdinalIgnoreCase)
    {
        // Static kind dispatchers. `CODEUNIT.Run(...)`, `PAGE.RunModal(...)`,
        // `REPORT.RunModal(...)`, `XMLPORT.Import(...)`, `QUERY.Open(...)`.
        "CODEUNIT", "PAGE", "REPORT", "XMLPORT", "QUERY", "ENUM",
        // App metadata / lifecycle.
        "NavApp",
    };

    /// <summary>
    /// True when <paramref name="name"/> is an AL built-in static API
    /// head — chains rooted here are AL-runtime, not user code, and
    /// shouldn't be diagnosed as unresolved variables.
    /// </summary>
    public static bool IsBuiltinStaticReceiver(string name) =>
        !string.IsNullOrEmpty(name) && BuiltinStaticReceivers.Contains(name);

    /// <summary>
    /// AL built-in / system types that won't ever resolve through
    /// the catalog — they're either runtime primitives
    /// (<c>Dialog</c>, <c>RecordRef</c>, <c>RecordId</c>,
    /// <c>FieldRef</c>, <c>Variant</c>), XML / JSON primitives
    /// (<c>XmlDocument</c>, <c>XmlElement</c>, <c>JsonObject</c>),
    /// I/O primitives (<c>InStream</c>, <c>OutStream</c>, <c>File</c>),
    /// HTTP (<c>HttpClient</c>, <c>HttpRequestMessage</c>), or app
    /// metadata (<c>ModuleInfo</c>). A variable typed as one of these
    /// is legitimate but its chain steps (e.g. <c>Dialog.Open(...)</c>)
    /// can't be resolved through our catalog, so silence the
    /// diagnostic.
    /// </summary>
    public static readonly HashSet<string> KnownSystemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Runtime references and variants.
        "Dialog", "RecordRef", "RecordId", "FieldRef", "KeyRef",
        "Variant", "Guid", "DateFormula", "BigText",
        // XML / JSON primitives.
        "XmlDocument", "XmlElement", "XmlNode", "XmlNodeList",
        "XmlAttribute", "XmlAttributeCollection", "XmlComment",
        "XmlText", "XmlCData", "XmlDeclaration", "XmlDocumentType",
        "XmlNamespaceManager", "XmlReadOptions", "XmlWriteOptions",
        "XmlProcessingInstruction",
        "JsonObject", "JsonArray", "JsonValue", "JsonToken",
        // I/O.
        "InStream", "OutStream", "File", "TempBlob",
        // HTTP.
        "HttpClient", "HttpRequestMessage", "HttpResponseMessage",
        "HttpHeaders", "HttpContent",
        // App metadata.
        "ModuleInfo", "ModuleDependencyInfo",
        // .NET interop.
        "DotNet",
        // Generic collections (built-in generics).
        "List", "Dictionary",
        // Encoding / text / cryptography.
        "TextEncoding", "Encoding", "TextBuilder", "StringBuilder",
        "Base64Convert", "CryptographyManagement",
        // Misc primitives.
        "Version", "Duration",
        "ErrorInfo", "Notification", "FilterPageBuilder",
    };

    /// <summary>
    /// True when the declared type name is one of the AL runtime /
    /// system types the catalog never tracks — used to silence
    /// the <c>head-var-type-unresolved</c> diagnostic for variables
    /// typed against these primitives.
    /// </summary>
    public static bool IsKnownSystemType(string typeName) =>
        !string.IsNullOrEmpty(typeName) && KnownSystemTypes.Contains(typeName);

    /// <summary>
    /// AL statement / operator keywords that lex as <see cref="AlTokenKind.Identifier"/>
    /// and can syntactically appear right before <c>(</c> without being a
    /// procedure call: <c>if (X = 5) then</c>, <c>not (Foo)</c>,
    /// <c>while (i &lt; n) do</c>. The bare-call detector filters these so
    /// it doesn't try to resolve them as self-procedures.
    /// </summary>
    public static readonly HashSet<string> StatementKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Control flow.
        "if", "then", "else", "elseif",
        "while", "do",
        "repeat", "until",
        "for", "foreach", "to", "downto", "in",
        "case", "of",
        "with",
        "begin", "end",
        "exit", "return",
        // Declarations (defensive — the scope walker skips most of these,
        // but parametrised attributes can land here too).
        "var", "procedure", "trigger",
        // Operators that take a parenthesised expression.
        "not", "and", "or", "xor", "mod", "div",
        // Literal-shaped keywords AL exposes.
        "true", "false",
    };

    /// <summary>
    /// True for an AL system function callable with no receiver.
    /// </summary>
    public static bool IsBareCallable(string name) =>
        !string.IsNullOrEmpty(name) && BareCallableFunctions.Contains(name);

    /// <summary>
    /// True for an AL statement / operator keyword that the bare-call
    /// detector must skip so <c>if (X) then</c> doesn't get treated as
    /// a call to <c>if</c>.
    /// </summary>
    public static bool IsStatementKeyword(string name) =>
        !string.IsNullOrEmpty(name) && StatementKeywords.Contains(name);

    /// <summary>
    /// True when <paramref name="memberName"/> is a runtime built-in
    /// for the receiver's kind. The reference extractor uses this to
    /// distinguish "real unresolved" (typo / missing import / cross-
    /// release shadowing we don't yet handle) from "we caught a runtime
    /// method that was never going to be in the catalog."
    /// </summary>
    public static bool IsBuiltin(string ownerKind, string memberName)
    {
        if (string.IsNullOrEmpty(memberName)) return false;
        if (CommonMethods.Contains(memberName)) return true;

        // The catalog stores kinds lower-case (table, codeunit, …);
        // be defensive about casing on the caller side.
        return ownerKind?.ToLowerInvariant() switch
        {
            "table" or "tableextension" =>
                RecordMethods.Contains(memberName) || RecordSystemFields.Contains(memberName),
            "codeunit" =>
                CodeunitMethods.Contains(memberName),
            _ => false,
        };
    }
}
