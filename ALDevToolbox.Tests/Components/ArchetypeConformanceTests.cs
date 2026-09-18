using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Keeps the page frame in one place.
///
/// <para>Every page used to hand-write the same <c>page-head</c> from the design
/// handoff, and the structure lived in sixty copies that nothing compared - which
/// is how the Upgrades page shipped a filter bar with its Search button wrapped
/// under the box (#805). The archetype components in
/// <c>Components/Shared/Archetypes/</c> own that markup now; see "Page archetypes"
/// in PROJECT.md.</para>
///
/// <para>Same shape as <c>IgnoreQueryFiltersBaselineTests</c>: a baseline of the
/// files that have not been migrated yet. A file off the list that writes its own
/// <c>page-head</c> fails, so new pages compose <c>PageHead</c>; a file on the list
/// that no longer writes one fails too, so each migration shrinks the list and it
/// stays an honest count of what is left.</para>
/// </summary>
public sealed class ArchetypeConformanceTests
{
    /// <summary>
    /// A <c>class</c> attribute naming the <c>page-head</c> block itself (alone, with
    /// other classes, or with a modifier) - not its <c>page-head__*</c> parts, which
    /// the detail archetype legitimately reuses outside a page head.
    /// </summary>
    private static readonly Regex HandWrittenPageHead = new(
        """class="(?:[^"]*\s)?page-head(?:\s|"|--)""", RegexOptions.Compiled);

    /// <summary>
    /// Components that still hand-write their page head, relative to the repository
    /// root. Generated from the tree. Only ever remove entries: migrate the page to
    /// <c>PageHead</c> and delete its line in the same change.
    /// </summary>
    private static readonly IReadOnlySet<string> NotYetMigrated = new HashSet<string>(StringComparer.Ordinal)
    {
        "ALDevToolbox/Components/Pages/AccountSecurity/AccessTokenCreated.razor",
        "ALDevToolbox/Components/Pages/AccountSecurity/OAuthConsent.razor",
        "ALDevToolbox/Components/Pages/AccountSecurity/RecoveryCodes.razor",
        "ALDevToolbox/Components/Pages/AccountSecurity/TotpSetup.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminApplicationVersions.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminCatalog.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminCookbook.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminCookbookSuggestionReview.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminCookbookSuggestions.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminDashboard.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminModuleEdit.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminModuleList.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminObjectExplorerHeader.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminRecipeEdit.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminReleaseManage.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminReleaseModules.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminReleaseTranslations.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminTemplateDefaults.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminTemplateEdit.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminTemplateFiles.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminTemplateList.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminTemplateWorkspace.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminTranslationMemory.razor",
        "ALDevToolbox/Components/Pages/Admin/AdminTranslationMemoryImport.razor",
        "ALDevToolbox/Components/Pages/Admin/Administration/AdminAdministrationRepositoryStandards.razor",
        "ALDevToolbox/Components/Pages/Admin/Administration/AdminAdministrationUsersInvite.razor",
        "ALDevToolbox/Components/Pages/Admin/AuditLogPage.razor",
        "ALDevToolbox/Components/Pages/CookbookBrowser.razor",
        "ALDevToolbox/Components/Pages/NewExtension.razor",
        "ALDevToolbox/Components/Pages/NewWorkspace.razor",
        "ALDevToolbox/Components/Pages/ObjectExplorer/OeModuleDetail.razor",
        "ALDevToolbox/Components/Pages/ObjectExplorer/OeObjectDetail.razor",
        "ALDevToolbox/Components/Pages/ObjectExplorer/OeReleaseDetail.razor",
        "ALDevToolbox/Components/Pages/ObjectExplorer/ReleasesBrowserView.razor",
        "ALDevToolbox/Components/Pages/ObjectExplorer/SourceFileViewer.razor",
        "ALDevToolbox/Components/Pages/Pipelines/PipelinesBrowser.razor",
        "ALDevToolbox/Components/Pages/Piper.razor",
        "ALDevToolbox/Components/Pages/Projects/ProjectsBrowser.razor",
        "ALDevToolbox/Components/Pages/SiteAdmin/SiteAdminAudit.razor",
        "ALDevToolbox/Components/Pages/SiteAdmin/SiteAdminEmail.razor",
        "ALDevToolbox/Components/Pages/SiteAdmin/SiteAdminUsers.razor",
        "ALDevToolbox/Components/Pages/SiteAdmin/SiteAdminWorkers.razor",
        "ALDevToolbox/Components/Pages/SuggestRecipe.razor",
        "ALDevToolbox/Components/Pages/Teams/TeamDetail.razor",
        "ALDevToolbox/Components/Pages/Teams/TeamsIndex.razor",
        "ALDevToolbox/Components/Pages/TemplateDetail.razor",
        "ALDevToolbox/Components/Pages/TemplatesBrowser.razor",
        "ALDevToolbox/Components/Pages/Translator.razor",
        "ALDevToolbox/Components/Pages/Upgrades/UpgradesPage.razor",
        "ALDevToolbox/Components/Shared/SettingsPage.razor",
        "ALDevToolbox/Components/Shared/TabbedPage.razor",
    };

    [Fact]
    public void No_new_component_hand_writes_a_page_head()
    {
        var offenders = HandWriting().Where(p => !NotYetMigrated.Contains(p)).ToList();

        offenders.Should().BeEmpty(
            "a page composes <PageHead> from Components/Shared/Archetypes/ rather than copying the " +
            "page-head markup - see \"Page archetypes\" in PROJECT.md. Do not add to the baseline");
    }

    [Fact]
    public void Migrated_components_are_removed_from_the_baseline()
    {
        var handWriting = HandWriting().ToHashSet(StringComparer.Ordinal);
        var stale = NotYetMigrated.Where(p => !handWriting.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();

        stale.Should().BeEmpty(
            "these files no longer hand-write a page-head (migrated, moved or deleted) - good news, " +
            "but the baseline has to stay honest, so drop their entries from this test");
    }

    private static List<string> HandWriting()
    {
        var root = RepoRoot();
        return Directory.EnumerateFiles(Path.Combine(root, "ALDevToolbox", "Components"), "*.razor", SearchOption.AllDirectories)
            .Select(p => (Full: p, Relative: Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/')))
            // The archetype components are the one place the markup is meant to live.
            .Where(f => !f.Relative.StartsWith("ALDevToolbox/Components/Shared/Archetypes/", StringComparison.Ordinal))
            .Where(f => HandWrittenPageHead.IsMatch(File.ReadAllText(f.Full)))
            .Select(f => f.Relative)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
