namespace ALDevToolbox.Domain.Tools;

/// <summary>
/// One MCP tool, described for a person rather than for an agent.
/// </summary>
/// <param name="Name">The wire name the agent calls, e.g. <c>search_objects</c>.</param>
/// <param name="Group">Which part of the toolbox it reaches, used to group the docs table.</param>
/// <param name="Blurb">One plain sentence. Not the agent-facing <c>[Description]</c>, which
/// is prompt text ("use search_recipes to find candidate ids") and reads as nonsense to a
/// consultant looking up what their assistant can do.</param>
/// <param name="Writes">True when the tool changes something. Mirrors
/// <c>McpServerToolAttribute.ReadOnly == false</c>; the catalogue test pins the two together.</param>
public sealed record McpToolDescriptor(string Name, string Group, string Blurb, bool Writes);

/// <summary>
/// The user-facing description of every tool an AI assistant can call, for
/// <c>/tools/mcp</c> and <c>/docs/mcp</c>.
///
/// It exists because both pages used to carry a hand-typed table, and both had
/// gone stale: they listed 13 and 15 tools against the 38 actually registered,
/// and two of the entries (<c>search_snippets</c>, <c>get_snippet</c>) were
/// renamed with the Cookbook and no longer exist, so the docs told people to
/// ask for a tool the server would refuse.
///
/// <c>McpToolCatalogTests</c> reflects over the same
/// <c>[McpServerTool]</c> attributes <c>WithToolsFromAssembly()</c> registers
/// and fails if this list and the server disagree on either the set of names or
/// which of them write. A new tool cannot ship undocumented, and a renamed one
/// cannot leave a dead name on the page.
/// </summary>
public static class McpToolCatalog
{
    public const string Generation = "Templates and generation";
    public const string ObjectExplorer = "Object Explorer";
    public const string Cookbook = "Cookbook";
    public const string Translator = "Translator";
    public const string Projects = "Projects and pipelines";
    public const string BcQuality = "Quality guidance";

    /// <summary>Group order for the docs table — the order the tools appear in the sidebar.</summary>
    public static readonly IReadOnlyList<string> Groups =
        new[] { Generation, ObjectExplorer, Cookbook, Translator, Projects, BcQuality };

    public static readonly IReadOnlyList<McpToolDescriptor> All = new[]
    {
        // ---- Templates and generation ----
        new McpToolDescriptor("list_templates", Generation,
            "Lists the workspace templates your organisation can generate from.", false),
        new McpToolDescriptor("list_modules", Generation,
            "Lists the optional modules that can be added to a workspace.", false),
        new McpToolDescriptor("list_well_known_dependencies", Generation,
            "Lists the dependencies your organisation has on file, so the assistant names them correctly.", false),
        new McpToolDescriptor("generate_workspace", Generation,
            "Builds a new workspace and hands back the ZIP.", true),
        new McpToolDescriptor("generate_extension", Generation,
            "Builds a new standalone extension and hands back the ZIP.", true),

        // ---- Object Explorer ----
        new McpToolDescriptor("list_releases", ObjectExplorer,
            "Lists the Business Central releases that have been imported.", false),
        new McpToolDescriptor("list_release_modules", ObjectExplorer,
            "Lists the apps and modules inside one release.", false),
        new McpToolDescriptor("compare_releases", ObjectExplorer,
            "Reports what changed between two releases.", false),
        new McpToolDescriptor("compare_release_files", ObjectExplorer,
            "Reports which files differ between two releases, including the ones that hold no "
            + "objects, such as permission sets and translations.", false),
        new McpToolDescriptor("search_objects", ObjectExplorer,
            "Finds tables, pages, codeunits and the rest by name or number.", false),
        new McpToolDescriptor("search_procedures", ObjectExplorer,
            "Finds procedures by name across a release.", false),
        new McpToolDescriptor("search_content", ObjectExplorer,
            "Searches the AL source itself, for when you remember the code but not the object.", false),
        new McpToolDescriptor("find_references", ObjectExplorer,
            "Finds every place that touches a table, field or procedure.", false),
        new McpToolDescriptor("find_system_references", ObjectExplorer,
            "Finds uses of platform types that the base application does not declare.", false),
        new McpToolDescriptor("get_object_outline", ObjectExplorer,
            "Returns one object's fields, procedures and triggers.", false),
        new McpToolDescriptor("get_procedure_source", ObjectExplorer,
            "Returns the body of one procedure, up to 200 lines.", false),
        new McpToolDescriptor("list_procedure_calls", ObjectExplorer,
            "Returns what one procedure calls and which fields it touches, so you can trace what it ends up doing.", false),
        new McpToolDescriptor("download_symbol_reference", ObjectExplorer,
            "Fetches a release's symbol package, so the assistant can compile against it.", false),

        // ---- Cookbook ----
        new McpToolDescriptor("search_recipes", Cookbook,
            "Searches your organisation's recipes by title, description or keyword.", false),
        new McpToolDescriptor("get_recipe", Cookbook,
            "Returns one recipe in full, every file included.", false),
        new McpToolDescriptor("get_cookbook_guidance", Cookbook,
            "Reads your organisation's house rules for writing a recipe, so its suggestions "
            + "follow your house style.", false),
        new McpToolDescriptor("suggest_recipe", Cookbook,
            "Submits a new recipe for an editor to review. Nothing is published without a person.", true),
        new McpToolDescriptor("update_recipe_suggestion", Cookbook,
            "Revises a suggestion the assistant already submitted.", true),
        new McpToolDescriptor("update_recipe", Cookbook,
            "Edits a published recipe. Needs the Editor role.", true),

        // ---- Translator ----
        new McpToolDescriptor("list_translation_languages", Translator,
            "Lists the languages a release has been translated into.", false),
        new McpToolDescriptor("search_translations", Translator,
            "Finds how a phrase was translated in a release.", false),
        new McpToolDescriptor("search_translation_memory", Translator,
            "Searches your organisation's own past translations for a phrase.", false),
        new McpToolDescriptor("machine_translate", Translator,
            "Proposes a translation. It is a suggestion until someone accepts it.", false),
        new McpToolDescriptor("vote_translation", Translator,
            "Votes a suggested translation up or down.", true),
        new McpToolDescriptor("remove_translation", Translator,
            "Withdraws a translation the assistant suggested.", true),

        // ---- Projects and pipelines ----
        new McpToolDescriptor("list_projects", Projects,
            "Lists the customer projects you can see.", false),
        new McpToolDescriptor("list_project_builds", Projects,
            "Lists the builds recorded against one project.", false),
        new McpToolDescriptor("get_project_build", Projects,
            "Returns one build with its apps and their versions.", false),
        new McpToolDescriptor("compare_project_builds", Projects,
            "Reports what changed between two builds of the same project.", false),
        new McpToolDescriptor("list_pipelines", Projects,
            "Lists the build pipelines set up for a project.", false),
        new McpToolDescriptor("list_pipeline_builds", Projects,
            "Lists what one pipeline has built, and how each run ended.", false),
        new McpToolDescriptor("list_release_pipelines", Projects,
            "Lists the release pipelines that publish builds to a Business Central environment.", false),
        new McpToolDescriptor("list_deliveries", Projects,
            "Lists what has been published, when, and whether it landed.", false),
        new McpToolDescriptor("publish_build", Projects,
            "Publishes a build to a Business Central environment.", true),

        // ---- Quality guidance ----
        new McpToolDescriptor("search_bcquality", BcQuality,
            "Searches Microsoft's published Business Central quality guidance, filtered to the "
            + "version you are building for.", false),
        new McpToolDescriptor("get_bcquality_article", BcQuality,
            "Returns one piece of that guidance in full, with its good and bad code examples.", false),
    };

    /// <summary>The tools in one group, in catalogue order.</summary>
    public static IEnumerable<McpToolDescriptor> InGroup(string group) =>
        All.Where(t => t.Group == group);
}
