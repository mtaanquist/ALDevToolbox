using System.Security.Claims;
using ALDevToolbox.Services.Palette;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// A source that answers from a list handed to it, for the tests that are about
/// <see cref="PaletteSearchService"/> and the endpoint rather than about any
/// real data. It records whether it was asked, which is how the gate and the
/// short-query short-circuit are proved: the thing to assert is that the source
/// was never called, not that the answer happened to be empty.
/// </summary>
public sealed class FakePaletteSource : IPaletteSource
{
    private readonly IReadOnlyList<PaletteCandidate> _candidates;

    public FakePaletteSource(
        string id, string label, int order, params PaletteCandidate[] candidates)
    {
        Id = id;
        Label = label;
        Order = order;
        _candidates = candidates;
    }

    public string Id { get; }
    public string Label { get; }
    public int Order { get; }

    /// <summary>Flip to false to stand in for a caller who fails this source's role check.</summary>
    public bool Available { get; set; } = true;

    /// <summary>Set to have <see cref="SearchAsync"/> wait on the token, for the cancellation test.</summary>
    public bool WaitForCancellation { get; set; }

    public int SearchCallCount { get; private set; }

    public int? LastLimit { get; private set; }

    public ClaimsPrincipal? LastGatedPrincipal { get; private set; }

    public Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        LastGatedPrincipal = user;
        return Task.FromResult(Available);
    }

    public async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(
        PaletteQuery query, int limit, CancellationToken ct)
    {
        SearchCallCount++;
        LastLimit = limit;
        if (WaitForCancellation) await Task.Delay(Timeout.Infinite, ct);
        return _candidates.Take(limit).ToList();
    }
}
