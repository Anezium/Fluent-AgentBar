using FluentAgentBar.WpfTaskbarWidget;
using Xunit;

namespace FluentAgentBar.Tests;

// Path-data and brand selection is pure string/colour logic, so it can be
// exercised without a WinUI dispatcher. Geometry parsing itself cannot.
public sealed class ProviderIconsTests
{
    [Theory]
    [InlineData("codex", nameof(ProviderIcons.OpenAiPathData))]
    [InlineData("Codex", nameof(ProviderIcons.OpenAiPathData))]
    [InlineData("claude", nameof(ProviderIcons.ClaudePathData))]
    [InlineData("Claude", nameof(ProviderIcons.ClaudePathData))]
    [InlineData("gemini", nameof(ProviderIcons.GeminiPathData))]
    [InlineData("Gemini", nameof(ProviderIcons.GeminiPathData))]
    [InlineData("cursor", nameof(ProviderIcons.CursorPathData))]
    [InlineData("Cursor", nameof(ProviderIcons.CursorPathData))]
    [InlineData("grok", nameof(ProviderIcons.GrokPathData))]
    [InlineData("Grok", nameof(ProviderIcons.GrokPathData))]
    [InlineData("something-else", nameof(ProviderIcons.OpenAiPathData))]
    public void PathDataFor_SelectsTheMarkOfTheNormalizedProvider(string providerName, string expectedField)
    {
        string expected = expectedField switch
        {
            nameof(ProviderIcons.ClaudePathData) => ProviderIcons.ClaudePathData,
            nameof(ProviderIcons.GeminiPathData) => ProviderIcons.GeminiPathData,
            nameof(ProviderIcons.CursorPathData) => ProviderIcons.CursorPathData,
            nameof(ProviderIcons.GrokPathData) => ProviderIcons.GrokPathData,
            _ => ProviderIcons.OpenAiPathData
        };

        Assert.Equal(expected, ProviderIcons.PathDataFor(providerName));
    }

    [Fact]
    public void PathDataFor_ReturnsADistinctMarkPerKnownProvider()
    {
        HashSet<string> marks = [];
        foreach (string provider in AppConfigStore.KnownProviders)
        {
            Assert.True(marks.Add(ProviderIcons.PathDataFor(provider)), $"{provider} reuses another mark.");
        }

        Assert.Equal(AppConfigStore.KnownProviders.Count, marks.Count);
    }

    // The WPF taskbar widget cannot reference the WinUI assembly, so it keeps
    // its own copy of the marks. Drift between the two is a visible bug.
    [Theory]
    [InlineData("Codex")]
    [InlineData("Claude")]
    [InlineData("Gemini")]
    [InlineData("Cursor")]
    [InlineData("Grok")]
    public void WidgetPathData_MatchesTheWinUiMark(string displayName)
    {
        Assert.Equal(ProviderIcons.PathDataFor(displayName), ProviderPathData.ForProvider(displayName));
    }

    [Theory]
    [InlineData("Codex", false)]
    [InlineData("Claude", true)]
    [InlineData("Gemini", true)]
    [InlineData("Cursor", false)]
    [InlineData("Grok", false)]
    public void WidgetBrandColor_IsSetOnlyForTheColouredMarks(string displayName, bool expectsBrandColor)
    {
        Assert.Equal(expectsBrandColor, ProviderPathData.BrandColorFor(displayName) is not null);
    }

    [Theory]
    [InlineData("codex", "codex")]
    [InlineData("Codex", "codex")]
    [InlineData("GEMINI", "gemini")]
    [InlineData("Cursor", "cursor")]
    [InlineData("grok", "grok")]
    [InlineData("", "codex")]
    [InlineData(null, "codex")]
    [InlineData("anthropic", "codex")]
    public void WidgetNormalize_MatchesAppConfigStoreNormalizeProvider(string? providerName, string expected)
    {
        Assert.Equal(expected, ProviderPathData.Normalize(providerName));
        Assert.Equal(AppConfigStore.NormalizeProvider(providerName), ProviderPathData.Normalize(providerName));
        Assert.Equal(expected, AppConfigStore.NormalizeProvider(providerName));
    }
}
