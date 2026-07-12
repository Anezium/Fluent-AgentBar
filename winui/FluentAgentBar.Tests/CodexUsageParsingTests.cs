using Xunit;

namespace FluentAgentBar.Tests;

public sealed class CodexUsageParsingTests
{
    [Fact]
    public void ParseCodexProfile_UsesMultiBucketRateLimitsWithoutDuplicatingBaseBucket()
    {
        ProfileUsage usage = UsageService.ParseCodexProfile(
            Profile(),
            [
                """
                {"id":2,"result":{"account":{"email":"person@example.com","planType":"pro"}}}
                """,
                """
                {
                  "id": 3,
                  "result": {
                    "rateLimits": {
                      "limitId": "codex",
                      "primary": { "usedPercent": 7, "windowDurationMins": 300, "resetsAt": 1783637271 },
                      "secondary": { "usedPercent": 13, "windowDurationMins": 10080, "resetsAt": 1784008910 },
                      "planType": "pro"
                    },
                    "rateLimitsByLimitId": {
                      "codex": {
                        "limitId": "codex",
                        "primary": { "usedPercent": 7, "windowDurationMins": 300 }
                      },
                      "codex_sol": {
                        "limitId": "codex_sol",
                        "limitName": "GPT-5.6-Codex-Sol",
                        "primary": { "usedPercent": 20, "windowDurationMins": 300 },
                        "secondary": { "usedPercent": 40, "windowDurationMins": 10080 }
                      },
                      "codex_terra": {
                        "limitId": "codex_terra",
                        "limitName": "GPT-5.6-Codex-Terra",
                        "primary": { "usedPercent": 30, "windowDurationMins": 1440 }
                      },
                      "codex_luna": {
                        "limitId": "codex_luna",
                        "limitName": "GPT-5.6-Codex-Luna",
                        "secondary": { "usedPercent": 50, "windowDurationMins": 60 }
                      }
                    }
                  }
                }
                """
            ]);

        Assert.True(usage.IsAvailable);
        Assert.Equal(93, usage.RemainingPercent);
        Assert.Equal(87, usage.WeeklyPercent);
        Assert.Equal("5h", usage.PrimaryQuotaLabel);
        Assert.Equal("Weekly", usage.WeeklyQuotaLabel);

        Assert.Collection(
            usage.DisplayQuotaGroups,
            group =>
            {
                Assert.Equal(string.Empty, group.Name);
                Assert.Collection(
                    group.Windows,
                    window => Assert.Equal(("5h", 93), (window.Label, window.RemainingPercent)),
                    window => Assert.Equal(("Weekly", 87), (window.Label, window.RemainingPercent)));
            },
            group =>
            {
                Assert.Equal("GPT-5.6-Codex-Sol", group.Name);
                Assert.Collection(
                    group.Windows,
                    window => Assert.Equal(("5h", 80), (window.Label, window.RemainingPercent)),
                    window => Assert.Equal(("Weekly", 60), (window.Label, window.RemainingPercent)));
            },
            group =>
            {
                Assert.Equal("GPT-5.6-Codex-Terra", group.Name);
                QuotaWindowUsage window = Assert.Single(group.Windows);
                Assert.Equal(("1d", 70), (window.Label, window.RemainingPercent));
            },
            group =>
            {
                Assert.Equal("GPT-5.6-Codex-Luna", group.Name);
                QuotaWindowUsage window = Assert.Single(group.Windows);
                Assert.Equal(("1h", 50), (window.Label, window.RemainingPercent));
            });
    }

    [Fact]
    public void ParseCodexProfile_WhenSecondaryWindowIsMissing_KeepsPrimaryAvailable()
    {
        ProfileUsage usage = UsageService.ParseCodexProfile(
            Profile(),
            [
                """
                {"id":3,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":15}}}}
                """
            ]);

        Assert.True(usage.IsAvailable);
        Assert.True(usage.HasPrimaryQuota);
        Assert.False(usage.HasWeeklyQuota);
        Assert.Equal("15m", usage.PrimaryQuotaLabel);
        Assert.Equal("75%", usage.RemainingText);
        Assert.Equal("--", usage.WeeklyText);
        Assert.Single(Assert.Single(usage.DisplayQuotaGroups).Windows);
    }

    private static ProfileConfig Profile()
    {
        return new ProfileConfig
        {
            Provider = "codex",
            Label = "Main",
            Home = Path.Combine(Path.GetTempPath(), "FluentAgentBar.Tests", "missing-profile"),
            Enabled = true
        };
    }
}
