using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Tests.Builders;

/// <summary>Helpers for constructing <see cref="ProjectPlan"/> / <see cref="StandaloneExtensionPlan"/> in tests.</summary>
public static class PlanBuilder
{
    public static ProjectPlan WorkspacePlan(
        string templateKey = "runtime-test",
        string workspaceName = "Acme Customer",
        IReadOnlyList<string>? selectedModules = null,
        bool includeExamples = true,
        bool includeForNav = false,
        int coreFrom = 90000,
        int coreTo = 90999) => new(
            TemplateKey: templateKey,
            WorkspaceName: workspaceName,
            Brief: "Test brief.",
            Description: "Test description.",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15",
            CoreIdRangeFrom: coreFrom,
            CoreIdRangeTo: coreTo,
            IncludeExamples: includeExamples,
            IncludeForNav: includeForNav,
            SelectedModuleKeys: selectedModules ?? Array.Empty<string>());

    public static StandaloneExtensionPlan ExtensionPlan(
        string templateKey = "runtime-test",
        string extensionName = "AcmeAddon",
        string publisher = "Acme",
        int idFrom = 70000,
        int idTo = 70999,
        IReadOnlyList<DependencyEntry>? dependencies = null) => new(
            TemplateKey: templateKey,
            ExtensionName: extensionName,
            Brief: "Standalone brief.",
            Description: "Standalone description.",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15",
            IdRangeFrom: idFrom,
            IdRangeTo: idTo,
            IncludeExamples: false,
            Publisher: publisher,
            Dependencies: dependencies ?? Array.Empty<DependencyEntry>());
}
