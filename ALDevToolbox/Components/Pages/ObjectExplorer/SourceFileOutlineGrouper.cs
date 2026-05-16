using ALDevToolbox.Services.ObjectExplorer;

namespace ALDevToolbox.Components.Pages.ObjectExplorer;

/// <summary>
/// Splits the flat outline list returned by
/// <see cref="ObjectExplorerService.GetFileOutlineAsync"/> into the
/// categorised sections the source-viewer panel renders. Lives outside
/// the razor file so the grouping logic is unit-testable as a pure
/// function — the razor side just passes its in-memory list plus the
/// current filter string and renders what comes back.
///
/// Field-bound triggers (<c>OnValidate</c>, <c>OnLookup</c>, …) are
/// detected by name and rendered nested under the preceding field;
/// action-bound triggers (<c>OnAction</c> in particular) nest under
/// the preceding action; table/page/report triggers (<c>OnInsert</c>,
/// <c>OnOpenPage</c>, …) fall into the object-level <c>TRIGGERS</c>
/// section.
/// </summary>
public static class SourceFileOutlineGrouper
{
    private static readonly HashSet<string> ObjectLevelTriggerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "OnInsert", "OnModify", "OnDelete", "OnRename",
        "OnOpenPage", "OnClosePage", "OnFindRecord", "OnNextRecord",
        "OnAfterGetRecord", "OnAfterGetCurrRecord",
        "OnInitReport", "OnPreReport", "OnPostReport",
        "OnRun",
    };

    private static readonly HashSet<string> ObjectHeaderKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "codeunit", "table", "page", "report", "xmlport", "query",
        "controladdin", "enum", "interface", "permissionset",
        "pageextension", "tableextension", "reportextension",
        "enumextension", "permissionsetextension", "dotnetpackage",
        "profile",
    };

    public static List<OutlineGroup> Build(IReadOnlyList<SourceFileOutlineItem> items, string? filter)
    {
        var f = (filter ?? string.Empty).Trim();
        bool Matches(SourceFileOutlineItem i) =>
            f.Length == 0 || i.Name.Contains(f, StringComparison.OrdinalIgnoreCase);

        var objectItems = new List<OutlineEntry>();
        var fieldItems = new List<OutlineEntry>();
        var actionItems = new List<OutlineEntry>();
        var procedures = new List<OutlineEntry>();
        var localProcedures = new List<OutlineEntry>();
        var eventPublishers = new List<OutlineEntry>();
        var eventSubscribers = new List<OutlineEntry>();
        var objectTriggers = new List<OutlineEntry>();

        // Track which field / action is "active" so a field-bound or
        // action-bound trigger nests under it. When the parent is filtered
        // out, the trigger drops to the object-level triggers section so
        // the user can still find it by name. Fields and actions don't
        // strictly nest in AL grammar (you can't put a field inside an
        // action), but tracking the most-recent of each lets us bind
        // triggers correctly regardless of source ordering.
        SourceFileOutlineItem? currentField = null;
        bool currentFieldVisible = false;
        SourceFileOutlineItem? currentAction = null;
        bool currentActionVisible = false;
        // Which container the next trigger should bind to. Updated as
        // fields and actions stream past; flips to "action" the moment we
        // see one (since the section is below the fields block in
        // practice), and back to "field" if a later field reopens.
        string activeParent = "none"; // "field" | "action" | "none"

        foreach (var item in items)
        {
            if (ObjectHeaderKinds.Contains(item.Kind))
            {
                if (Matches(item))
                {
                    objectItems.Add(new OutlineEntry(item, IsChild: false));
                }
                currentField = null;
                currentFieldVisible = false;
                currentAction = null;
                currentActionVisible = false;
                activeParent = "none";
                continue;
            }

            switch (item.Kind)
            {
                case "field":
                    currentField = item;
                    currentFieldVisible = Matches(item);
                    activeParent = "field";
                    if (currentFieldVisible)
                    {
                        fieldItems.Add(new OutlineEntry(item, IsChild: false));
                    }
                    break;

                case "action":
                    currentAction = item;
                    currentActionVisible = Matches(item);
                    activeParent = "action";
                    if (currentActionVisible)
                    {
                        actionItems.Add(new OutlineEntry(item, IsChild: false));
                    }
                    break;

                case "trigger":
                    var fieldBound = activeParent == "field"
                        && currentField is not null
                        && !ObjectLevelTriggerNames.Contains(item.Name);
                    var actionBound = activeParent == "action"
                        && currentAction is not null
                        && !ObjectLevelTriggerNames.Contains(item.Name);
                    if (fieldBound && currentFieldVisible)
                    {
                        if (Matches(item))
                        {
                            fieldItems.Add(new OutlineEntry(item, IsChild: true));
                        }
                    }
                    else if (actionBound && currentActionVisible)
                    {
                        if (Matches(item))
                        {
                            actionItems.Add(new OutlineEntry(item, IsChild: true));
                        }
                    }
                    else if (Matches(item))
                    {
                        objectTriggers.Add(new OutlineEntry(item, IsChild: false));
                    }
                    break;

                case "procedure":
                case "internal_procedure":
                case "protected_procedure":
                    if (Matches(item)) procedures.Add(new OutlineEntry(item, IsChild: false));
                    currentField = null;
                    currentFieldVisible = false;
                    currentAction = null;
                    currentActionVisible = false;
                    activeParent = "none";
                    break;

                case "local_procedure":
                    if (Matches(item)) localProcedures.Add(new OutlineEntry(item, IsChild: false));
                    currentField = null;
                    currentFieldVisible = false;
                    currentAction = null;
                    currentActionVisible = false;
                    activeParent = "none";
                    break;

                case "event_publisher":
                    if (Matches(item)) eventPublishers.Add(new OutlineEntry(item, IsChild: false));
                    currentField = null;
                    currentFieldVisible = false;
                    currentAction = null;
                    currentActionVisible = false;
                    activeParent = "none";
                    break;

                case "event_subscriber":
                    if (Matches(item)) eventSubscribers.Add(new OutlineEntry(item, IsChild: false));
                    currentField = null;
                    currentFieldVisible = false;
                    currentAction = null;
                    currentActionVisible = false;
                    activeParent = "none";
                    break;
            }
        }

        var groups = new List<OutlineGroup>(8);
        AddIfAny(groups, "object", "OBJECT", objectItems);
        AddIfAny(groups, "fields", "FIELDS", fieldItems);
        AddIfAny(groups, "actions", "ACTIONS", actionItems);
        AddIfAny(groups, "procedures", "PROCEDURES", procedures);
        AddIfAny(groups, "local-procedures", "LOCAL PROCEDURES", localProcedures);
        AddIfAny(groups, "event-publishers", "EVENT PUBLISHERS", eventPublishers);
        AddIfAny(groups, "event-subscribers", "EVENT SUBSCRIBERS", eventSubscribers);
        AddIfAny(groups, "triggers", "TRIGGERS", objectTriggers);
        return groups;
    }

    private static void AddIfAny(List<OutlineGroup> groups, string key, string title, List<OutlineEntry> items)
    {
        if (items.Count > 0)
        {
            groups.Add(new OutlineGroup(key, title, items));
        }
    }
}

public sealed record OutlineGroup(string Key, string Title, IReadOnlyList<OutlineEntry> Items);

public sealed record OutlineEntry(SourceFileOutlineItem Item, bool IsChild);
