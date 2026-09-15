using System.Diagnostics;
using Xunit;

namespace FluentAgentBar.Tests;

[Collection("ProviderDiagnostics")]
public sealed class UsageServiceConcurrencyTests : IDisposable
{
    private readonly string _logDirectory;
    private readonly IDisposable _logScope;

    public UsageServiceConcurrencyTests()
    {
        _logDirectory = Path.Combine(Path.GetTempPath(), "FluentAgentBar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_logDirectory);
        _logScope = ProviderDiagnostics.UseLogDirectory(_logDirectory);
    }

    public void Dispose()
    {
        _logScope.Dispose();
        if (Directory.Exists(_logDirectory))
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FetchProvidersAsync_FetchesProfilesConcurrentlyAndPreservesOrder()
    {
        using UsageService service = new(
            async (profile, cancellationToken) =>
            {
                await Task.Delay(200, cancellationToken);
                return AvailableProfile(profile);
            },
            (_, _) => throw new InvalidOperationException("Claude fetch should not run."));
        AppConfig config = new()
        {
            Profiles =
            [
                Profile("codex", "First"),
                Profile("codex", "Second")
            ]
        };

        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<ProviderUsage> providers = await service.FetchProvidersAsync(config, CancellationToken.None);
        stopwatch.Stop();

        ProviderUsage provider = Assert.Single(providers);
        Assert.Equal("Codex", provider.Name);
        Assert.Collection(
            provider.Profiles,
            profile => Assert.Equal("First", profile.Label),
            profile => Assert.Equal("Second", profile.Label));
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(350),
            $"Expected concurrent fetch under 350 ms, got {stopwatch.Elapsed.TotalMilliseconds:0} ms.");
    }

    [Fact]
    public async Task FetchProvidersAsync_IsolatesProfileFailureAndKeepsSuccessfulResult()
    {
        using UsageService service = new(
            (_, _) => throw new InvalidOperationException("Codex fetch should not run."),
            (profile, _) => profile.Label == "Broken"
                ? Task.FromException<ProfileUsage>(new InvalidOperationException("fake failure"))
                : Task.FromResult(AvailableProfile(profile, MockUsageData.ClaudeAccentColor)));
        AppConfig config = new()
        {
            Profiles =
            [
                Profile("claude", "Broken"),
                Profile("claude", "Working")
            ]
        };

        IReadOnlyList<ProviderUsage> providers = await service.FetchProvidersAsync(config, CancellationToken.None);

        ProviderUsage provider = Assert.Single(providers);
        Assert.Equal("Claude", provider.Name);
        Assert.Collection(
            provider.Profiles,
            profile =>
            {
                Assert.Equal("Broken", profile.Label);
                Assert.False(profile.IsAvailable);
                Assert.Equal(0, profile.RemainingPercent);
                Assert.Equal(MockUsageData.ClaudeAccentColor, profile.AccentColor);
            },
            profile =>
            {
                Assert.Equal("Working", profile.Label);
                Assert.True(profile.IsAvailable);
                Assert.Equal(73, profile.RemainingPercent);
                Assert.Equal(41, profile.WeeklyPercent);
                Assert.Equal("fake@example.com", profile.Email);
                Assert.Equal("Team", profile.Plan);
            });
    }

    private static ProfileConfig Profile(string provider, string label)
    {
        return new ProfileConfig
        {
            Provider = provider,
            Label = label,
            Home = label,
            Enabled = true
        };
    }

    private static ProfileUsage AvailableProfile(ProfileConfig profile)
    {
        return AvailableProfile(profile, MockUsageData.CodexAccentColor);
    }

    private static ProfileUsage AvailableProfile(ProfileConfig profile, Windows.UI.Color accentColor)
    {
        return new ProfileUsage(
            profile.Label,
            "fake@example.com",
            "Team",
            73,
            41,
            true,
            accentColor);
    }
}
