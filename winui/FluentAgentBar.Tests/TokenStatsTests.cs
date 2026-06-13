using System.Text.Json;
using Xunit;

namespace FluentAgentBar.Tests;

public sealed class TokenStatsTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1K")]
    [InlineData(1_250, "1.3K")]
    [InlineData(999_949, "999.9K")]
    [InlineData(1_000_000, "1M")]
    [InlineData(2_500_000, "2.5M")]
    public void FormatTokenCount_FormatsRawKAndMValues(long value, string expected)
    {
        Assert.Equal(expected, TokenStats.FormatTokenCount(value));
    }

    [Theory]
    [InlineData("openai/gpt-5-2026-01-02")]
    [InlineData("GPT-5-20260102")]
    public async Task ComputeAsync_NormalizesModelAndCalculatesCostFromTemporaryClaudeJsonl(string model)
    {
        using TemporaryDirectory temp = new();
        string jsonlPath = CreateClaudeJsonl(
            temp.Path,
            model,
            inputTokens: 1_000_000,
            outputTokens: 1_000_000,
            cacheReadTokens: 1_000_000,
            cacheCreationTokens: 0);
        File.SetLastWriteTime(jsonlPath, DateTime.Now);

        (TokenReport? codex, TokenReport? claude) = await new TokenStatsService().ComputeAsync(
            CreateClaudeOnlyConfig(temp.Path),
            includeDefaultCodexHome: false);

        Assert.Null(codex);
        Assert.NotNull(claude?.Today);
        TokenStats today = claude.Today;
        Assert.Equal(1_000_000, today.InputTokens);
        Assert.Equal(1_000_000, today.OutputTokens);
        Assert.Equal(1_000_000, today.CacheReadTokens);
        Assert.Equal(11.375, today.CostUsd, precision: 3);
    }

    [Fact]
    public async Task ComputeAsync_ReturnsZeroCostForUnknownModel()
    {
        using TemporaryDirectory temp = new();
        CreateClaudeJsonl(
            temp.Path,
            "unknown-model",
            inputTokens: 1_000_000,
            outputTokens: 1_000_000,
            cacheReadTokens: 1_000_000,
            cacheCreationTokens: 1_000_000);

        (TokenReport? codex, TokenReport? claude) = await new TokenStatsService().ComputeAsync(
            CreateClaudeOnlyConfig(temp.Path),
            includeDefaultCodexHome: false);

        Assert.Null(codex);
        Assert.NotNull(claude?.Today);
        TokenStats today = claude.Today;
        Assert.Equal(0, today.CostUsd);
    }

    private static AppConfig CreateClaudeOnlyConfig(string claudeHome)
    {
        return new AppConfig
        {
            Profiles =
            [
                new ProfileConfig
                {
                    Provider = "claude",
                    Label = "Temp",
                    Home = claudeHome,
                    Enabled = true
                }
            ]
        };
    }

    private static string CreateClaudeJsonl(
        string claudeHome,
        string model,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheCreationTokens)
    {
        string projects = Path.Combine(claudeHome, "projects", "temp-project");
        Directory.CreateDirectory(projects);
        string jsonlPath = Path.Combine(projects, "session.jsonl");
        object line = new
        {
            timestamp = DateTimeOffset.Now.ToString("O"),
            message = new
            {
                id = Guid.NewGuid().ToString("N"),
                model,
                usage = new
                {
                    input_tokens = inputTokens,
                    output_tokens = outputTokens,
                    cache_read_input_tokens = cacheReadTokens,
                    cache_creation_input_tokens = cacheCreationTokens
                }
            }
        };
        File.WriteAllText(jsonlPath, JsonSerializer.Serialize(line) + Environment.NewLine);
        return jsonlPath;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FluentAgentBar.Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
