using ALDevToolbox.Components.Shared;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The wait behind the Environments and Upgrades search boxes (#981): a burst of
/// keystrokes filters once, with the last one, and nothing fires after the page closes.
/// </summary>
public sealed class DebouncerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_burst_of_calls_runs_only_the_last_one_once()
    {
        using var debouncer = new Debouncer(Delay);
        var ran = new List<int>();
        var done = new TaskCompletionSource();

        for (var i = 1; i <= 5; i++)
        {
            var n = i;
            debouncer.Trigger(() =>
            {
                lock (ran) ran.Add(n);
                done.TrySetResult();
                return Task.CompletedTask;
            });
        }

        await done.Task.WaitAsync(Settle, TestContext.Current.CancellationToken);
        await Task.Delay(Delay * 3, TestContext.Current.CancellationToken);
        ran.Should().Equal(5);
    }

    [Fact]
    public async Task Nothing_runs_once_it_is_disposed()
    {
        var debouncer = new Debouncer(Delay);
        var ran = false;
        debouncer.Trigger(() => { ran = true; return Task.CompletedTask; });

        debouncer.Dispose();
        debouncer.Trigger(() => { ran = true; return Task.CompletedTask; });

        await Task.Delay(Delay * 4, TestContext.Current.CancellationToken);
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_drops_the_waiting_call()
    {
        using var debouncer = new Debouncer(Delay);
        var ran = false;
        debouncer.Trigger(() => { ran = true; return Task.CompletedTask; });

        debouncer.Cancel();

        await Task.Delay(Delay * 4, TestContext.Current.CancellationToken);
        ran.Should().BeFalse();
    }

    [Fact]
    public void A_zero_delay_runs_the_action_straight_away()
    {
        using var debouncer = new Debouncer(TimeSpan.Zero);
        var ran = false;

        debouncer.Trigger(() => { ran = true; return Task.CompletedTask; });

        ran.Should().BeTrue();
    }
}
