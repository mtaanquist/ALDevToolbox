using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

public sealed class StorageDisplayTests
{
    [Theory]
    [InlineData(800L * 1024, "800 MB")]
    [InlineData(1L, "1 MB")]
    [InlineData(1024L * 1024, "1.0 GB")]
    [InlineData(13002342L, "12.4 GB")]
    [InlineData(250L * 1024 * 1024, "250 GB")]
    public void Sizes_read_in_the_unit_a_person_would_say(long kilobytes, string expected) =>
        StorageDisplay.Size(kilobytes).Should().Be(expected);

    [Theory]
    [InlineData(0.5, "")]
    [InlineData(0.79, "")]
    [InlineData(0.8, "warn")]
    [InlineData(0.999, "warn")]
    [InlineData(1.0, "danger")]
    [InlineData(1.4, "danger")]
    public void The_bar_turns_amber_from_four_fifths_and_red_at_or_over_the_allowance(double use, string tone) =>
        StorageDisplay.Tone(use).Should().Be(tone);
}
