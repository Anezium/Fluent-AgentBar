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
}
