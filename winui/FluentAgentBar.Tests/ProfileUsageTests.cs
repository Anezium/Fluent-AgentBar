using Xunit;

namespace FluentAgentBar.Tests;

public sealed class ProfileUsageTests
{
    [Fact]
    public void DetailText_RedactsEmailAndDoesNotReturnRawEmail()
    {
        ProfileUsage usage = new(
            "Main",
            "person@example.com",
            "max_20x",
            80,
            60,
            true,
            MockUsageData.CodexAccentColor);

        string detail = usage.DetailText;

        Assert.Contains("Max 20x", detail);
        Assert.Contains("p", detail);
        Assert.Contains("@example.com", detail);
        Assert.DoesNotContain("person@example.com", detail);
    }

    [Fact]
    public void UsageStatusText_WhenUnavailableWithAHint_ShowsTheHint()
    {
        ProfileUsage usage = new ProfileUsage(
            "Main",
            string.Empty,
            "Update Required",
            0,
            0,
            false,
            MockUsageData.CodexAccentColor) with { StatusHint = "Run 'agy update', then refresh." };

        Assert.Equal("Run 'agy update', then refresh.", usage.UsageStatusText);
    }

    [Fact]
    public void UsageStatusText_WhenAvailable_IgnoresTheHint()
    {
        ProfileUsage usage = new ProfileUsage(
            "Main",
            string.Empty,
            "Antigravity",
            80,
            60,
            true,
            MockUsageData.CodexAccentColor) with { StatusHint = "Run 'agy update', then refresh." };

        Assert.Equal(string.Empty, usage.UsageStatusText);
    }
}
