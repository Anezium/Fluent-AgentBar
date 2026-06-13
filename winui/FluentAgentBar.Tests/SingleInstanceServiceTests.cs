using Xunit;

namespace FluentAgentBar.Tests;

public sealed class SingleInstanceServiceTests
{
    [Fact]
    public void ParseActivationIntent_DefaultsToSettings()
    {
        Assert.Equal(ActivationIntent.ShowSettings, SingleInstanceService.ParseActivationIntent([]));
    }

    [Theory]
    [InlineData("--show-settings", 0)]
    [InlineData("--SHOW-SETTINGS", 0)]
    [InlineData("--show-flyout", 1)]
    [InlineData("--SHOW-FLYOUT", 1)]
    public void ParseActivationIntent_RecognizesSupportedFlagsCaseInsensitively(
        string flag,
        int expected)
    {
        Assert.Equal((ActivationIntent)expected, SingleInstanceService.ParseActivationIntent([flag]));
    }

    [Fact]
    public void ParseExplicitActivationIntent_ReturnsNullWhenNoSupportedFlagIsPresent()
    {
        Assert.Null(SingleInstanceService.ParseExplicitActivationIntent(["--ignored"]));
    }

    [Fact]
    public void ParseActivationIntent_UsesFirstSupportedFlag()
    {
        ActivationIntent intent = SingleInstanceService.ParseActivationIntent([
            "--ignored",
            "--show-flyout",
            "--show-settings"
        ]);

        Assert.Equal(ActivationIntent.ShowFlyout, intent);
    }
}
