using ALDevToolbox.Services;
using AwesomeAssertions;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Validation;

/// <summary>
/// The field-key contract of <see cref="TemplateValidation.ValidateExtensions"/>.
/// The rules are pure and DB-free — the class was extracted from
/// <c>TemplateService</c> exactly so they could be read and tested without
/// standing up the service — but only four of the ~20 keys had a test, through
/// the reconciliation suite.
///
/// Assertions are on the <em>key</em>, never the message: the key is what the
/// editor binds an inline error to, so a renamed key silently drops the error
/// next to the field while the message is free to be reworded. Per the tests
/// README.
/// </summary>
public sealed class TemplateValidationTests
{
    private static ExtensionAuthoring Ext(
        string path = "Core",
        string nameTemplate = "{{customer}} Core",
        int? idFrom = null,
        int? idTo = null,
        IReadOnlyList<FolderAuthoring>? folders = null,
        IReadOnlyList<DependencyAuthoring>? deps = null) =>
        new(path, nameTemplate, Required: true, Application: null, Runtime: null,
            idFrom, idTo, folders ?? [], deps ?? []);

    private static FolderAuthoring Folder(string path, IReadOnlyList<FileAuthoring>? files = null,
        IReadOnlyList<FolderAuthoring>? folders = null) =>
        new(path, folders ?? [], files ?? []);

    private static FileAuthoring File(string path) => new(path, "// content", IsExample: false);

    private static Dictionary<string, string> Validate(params ExtensionAuthoring[] extensions)
    {
        var errors = new Dictionary<string, string>();
        TemplateValidation.ValidateExtensions(extensions, errors);
        return errors;
    }

    [Fact]
    public void A_well_formed_extension_produces_no_errors()
    {
        Validate(Ext(folders: [Folder("src", [File("Setup.al")])],
                     deps: [new DependencyAuthoring(null, null, "{437dbf0e-84ff-417a-965d-ed2bb9650972}",
                                                    "Base Application", "Microsoft", "1.0.0.0")]))
            .Should().BeEmpty();
    }

    // ---- Extension path ---------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_extension_path_is_keyed_to_the_path_field(string path) =>
        Validate(Ext(path: path)).Should().ContainKey("Extensions[0].Path");

    [Theory]
    [InlineData("Sales Core")]   // whitespace
    [InlineData("Sales/Core")]   // separator
    [InlineData("1Core")]        // must start with a letter
    [InlineData("Core.App")]     // dot is not in the safe set
    public void An_unsafe_extension_path_is_keyed_to_the_path_field(string path) =>
        Validate(Ext(path: path)).Should().ContainKey("Extensions[0].Path");

    [Fact]
    public void A_duplicate_extension_path_is_keyed_to_the_second_occurrence()
    {
        var errors = Validate(Ext(path: "Core"), Ext(path: "Core"));

        errors.Should().ContainKey("Extensions[1].Path");
        errors.Should().NotContainKey("Extensions[0].Path", "the first occurrence is the valid one");
    }

    /// <summary>
    /// Two extensions differing only in case produce one folder on Windows, so
    /// the generated ZIP would silently drop an extension. The rule is the
    /// reason this test group exists.
    /// </summary>
    [Fact]
    public void A_case_insensitive_path_collision_is_keyed_to_the_second_occurrence()
    {
        var errors = Validate(Ext(path: "Core"), Ext(path: "core"));

        errors.Should().ContainKey("Extensions[1].Path");
        errors.Should().NotContainKey("Extensions[0].Path");
    }

    // ---- Name template and id range ---------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_name_template_is_keyed_to_the_name_template_field(string nameTemplate) =>
        Validate(Ext(nameTemplate: nameTemplate)).Should().ContainKey("Extensions[0].NameTemplate");

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-5, 100)]
    public void An_id_range_starting_at_or_below_zero_is_keyed_to_the_from_field(int from, int to) =>
        Validate(Ext(idFrom: from, idTo: to)).Should().ContainKey("Extensions[0].IdRangeFrom");

    [Theory]
    [InlineData(50000, 50000)]
    [InlineData(50000, 49999)]
    public void An_id_range_end_not_above_the_start_is_keyed_to_the_to_field(int from, int to) =>
        Validate(Ext(idFrom: from, idTo: to)).Should().ContainKey("Extensions[0].IdRangeTo");

    [Theory]
    [InlineData(50000, null)]
    [InlineData(null, 50099)]
    public void A_half_set_id_range_is_keyed_to_the_pair_not_to_either_bound(int? from, int? to)
    {
        var errors = Validate(Ext(idFrom: from, idTo: to));

        errors.Should().ContainKey("Extensions[0].IdRange");
        errors.Should().NotContainKey("Extensions[0].IdRangeFrom");
        errors.Should().NotContainKey("Extensions[0].IdRangeTo");
    }

    [Fact]
    public void A_complete_id_range_is_accepted() =>
        Validate(Ext(idFrom: 50000, idTo: 50099)).Should().BeEmpty();

    // ---- Folder tree ------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("src/nested")]
    [InlineData("..")]
    [InlineData(".")]
    public void An_unsafe_folder_path_is_keyed_to_that_folder(string path) =>
        Validate(Ext(folders: [Folder(path)])).Should().ContainKey("Extensions[0].Folders[0].Path");

    [Fact]
    public void A_duplicate_sibling_folder_is_keyed_to_the_second_sibling()
    {
        var errors = Validate(Ext(folders: [Folder("src"), Folder("SRC")]));

        errors.Should().ContainKey("Extensions[0].Folders[1].Path", "sibling uniqueness is case-insensitive");
        errors.Should().NotContainKey("Extensions[0].Folders[0].Path");
    }

    [Fact]
    public void A_nested_folder_collision_is_keyed_by_its_full_path()
    {
        var errors = Validate(Ext(folders: [Folder("src", folders: [Folder("Tables"), Folder("tables")])]));

        errors.Should().ContainKey("Extensions[0].Folders[0].Folders[1].Path");
    }

    [Theory]
    [InlineData("")]
    [InlineData("sub/Setup.al")]
    [InlineData("..")]
    public void An_unsafe_file_path_is_keyed_to_that_file(string path) =>
        Validate(Ext(folders: [Folder("src", [File(path)])]))
            .Should().ContainKey("Extensions[0].Folders[0].Files[0].Path");

    [Fact]
    public void A_duplicate_file_in_one_folder_is_keyed_to_the_second_file()
    {
        var errors = Validate(Ext(folders: [Folder("src", [File("Setup.al"), File("setup.al")])]));

        errors.Should().ContainKey("Extensions[0].Folders[0].Files[1].Path");
        errors.Should().NotContainKey("Extensions[0].Folders[0].Files[0].Path");
    }

    [Fact]
    public void The_same_file_name_in_two_different_folders_is_fine() =>
        Validate(Ext(folders: [Folder("src", [File("Setup.al")]), Folder("test", [File("Setup.al")])]))
            .Should().BeEmpty();

    // ---- Dependencies -----------------------------------------------------

    [Fact]
    public void A_dependency_referencing_no_shape_is_keyed_to_the_dependency_itself() =>
        Validate(Ext(deps: [new DependencyAuthoring(null, null, null, null, null, null)]))
            .Should().ContainKey("Extensions[0].Dependencies[0]");

    [Fact]
    public void A_dependency_referencing_several_shapes_is_keyed_to_the_dependency_itself() =>
        Validate(Ext(deps: [new DependencyAuthoring("Core", "base-app", null, null, null, null)]))
            .Should().ContainKey("Extensions[0].Dependencies[0]");

    [Fact]
    public void An_intra_template_reference_to_an_undeclared_extension_is_keyed_to_the_extension_field() =>
        Validate(Ext(deps: [new DependencyAuthoring("Nowhere", null, null, null, null, null)]))
            .Should().ContainKey("Extensions[0].Dependencies[0].Extension");

    [Fact]
    public void An_intra_template_reference_to_an_earlier_extension_resolves() =>
        Validate(Ext(path: "Core"),
                 Ext(path: "Sales", deps: [new DependencyAuthoring("Core", null, null, null, null, null)]))
            .Should().BeEmpty();

    [Theory]
    [InlineData("abc", "Base Application", "Microsoft", "1.0.0.0", "Extensions[0].Dependencies[0].Id")]
    [InlineData("{437dbf0e-84ff-417a-965d-ed2bb9650972}", "", "Microsoft", "1.0.0.0", "Extensions[0].Dependencies[0].Name")]
    [InlineData("{437dbf0e-84ff-417a-965d-ed2bb9650972}", "Base Application", "  ", "1.0.0.0", "Extensions[0].Dependencies[0].Publisher")]
    [InlineData("{437dbf0e-84ff-417a-965d-ed2bb9650972}", "Base Application", "Microsoft", null, "Extensions[0].Dependencies[0].Version")]
    public void A_literal_dependency_reports_each_missing_part_under_its_own_key(
        string id, string? name, string? publisher, string? version, string expectedKey) =>
        Validate(Ext(deps: [new DependencyAuthoring(null, null, id, name, publisher, version)]))
            .Should().ContainKey(expectedKey);

    // ---- Indexing ---------------------------------------------------------

    [Fact]
    public void Errors_carry_the_index_of_the_extension_they_came_from()
    {
        var errors = Validate(Ext(path: "Core"), Ext(path: "Sales", nameTemplate: ""));

        errors.Should().ContainKey("Extensions[1].NameTemplate");
        errors.Keys.Should().NotContain(k => k.StartsWith("Extensions[0]"));
    }
}
