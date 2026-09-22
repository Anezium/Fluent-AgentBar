using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FluentAgentBar.Tests;

public sealed class CodexTokenStatsTests
{
    [Theory]
    [InlineData("gpt-6-astra")]
    [InlineData("openai/GPT-6-ASTRA-2026-09-09")]
    public async Task ComputeAsync_PricesAstraCumulativeDeltasWithoutCountingSnapshotsTwice(string model)
    {
        using SessionFixture session = new(model);
        session.AppendUsage(session.Today, 1_000, 200, 100);
        session.AppendUsage(session.Today, 2_000, 400, 200);
        session.AppendUsage(session.Today, 2_000, 400, 200);

        TokenStats stats = Assert.IsType<TokenStats>((await session.ComputeAsync())?.Today);

        Assert.Equal(1_600, stats.InputTokens);
        Assert.Equal(400, stats.CacheReadTokens);
        Assert.Equal(200, stats.OutputTokens);
        Assert.Equal(0.0264, stats.CostUsd, precision: 6);
    }

    [Fact]
    public async Task ComputeAsync_ReadsOpenSessionAndPicksUpCompletedTrailingLineOnRefresh()
    {
        using SessionFixture session = new();
        session.AppendUsage(session.Today, 1_000, 200, 100);
        using FileStream writer = new(session.FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        byte[] nextLine = Encoding.UTF8.GetBytes(session.UsageLine(session.Today, 2_000, 400, 200));
        writer.Write(nextLine.AsSpan(0, nextLine.Length / 2));
        writer.Flush();

        TokenStats before = Assert.IsType<TokenStats>((await session.ComputeAsync())?.Today);
        Assert.Equal(1_000, before.TotalInputTokens);
        Assert.Equal(100, before.OutputTokens);

        writer.Write(nextLine.AsSpan(nextLine.Length / 2));
        writer.Flush();

        TokenStats after = Assert.IsType<TokenStats>((await session.ComputeAsync())?.Today);
        Assert.Equal(2_000, after.TotalInputTokens);
        Assert.Equal(200, after.OutputTokens);
        Assert.Equal(0.0264, after.CostUsd, precision: 6);
    }

    [Theory]
    [InlineData("sessions")]
    [InlineData("archived_sessions")]
    public async Task ComputeAsync_IncludesResumedOldSessionsButOnlyRecentTokenDeltas(string root)
    {
        using SessionFixture session = new(root: root, startedDaysAgo: 30);
        session.AppendUsage(session.Today.AddDays(-30), 1_000, 200, 100);
        session.AppendUsage(session.Today, 2_000, 400, 200);
        File.SetLastWriteTime(session.FilePath, session.Today.AddHours(12));

        TokenReport report = Assert.IsType<TokenReport>(await session.ComputeAsync());
        TokenStats stats = Assert.IsType<TokenStats>(report.Today);
        Assert.Equal(1_000, stats.TotalInputTokens);
        Assert.Equal(100, stats.OutputTokens);
        Assert.Equal(0.0132, stats.CostUsd, precision: 6);
        Assert.Equal(7, report.Daily.Count);
        Assert.Equal(1_000, report.Daily.Sum(day => day.Stats.TotalInputTokens));
    }

    [Fact]
    public async Task ComputeAsync_PricesUsageWhenOnlyLastTokenUsageIsAvailable()
    {
        using SessionFixture session = new();
        session.AppendUsage(session.Today, 1_000, 200, 100, cumulative: false);

        TokenStats stats = Assert.IsType<TokenStats>((await session.ComputeAsync())?.Today);
        Assert.Equal(1_000, stats.TotalInputTokens);
        Assert.Equal(0.0132, stats.CostUsd, precision: 6);
    }

    private sealed class SessionFixture : IDisposable
    {
        private readonly string _home = Path.Combine(Path.GetTempPath(), "FluentAgentBar.Tests-" + Guid.NewGuid().ToString("N"));

        public SessionFixture(string model = "gpt-6-astra", string root = "sessions", int startedDaysAgo = 0)
        {
            string folder = Path.Combine(_home, root, Today.AddDays(-startedDaysAgo).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            FilePath = Path.Combine(folder, "session.jsonl");
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new
            {
                type = "turn_context",
                payload = new { model }
            }) + Environment.NewLine);
        }

        public DateTime Today { get; } = DateTime.Today;
        public string FilePath { get; }

        public string UsageLine(DateTime day, long input, long cached, long output, bool cumulative = true)
        {
            return JsonSerializer.Serialize(new
            {
                timestamp = new DateTimeOffset(day.AddHours(12)).ToString("O"),
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new Dictionary<string, object>
                    {
                        [cumulative ? "total_token_usage" : "last_token_usage"] = new
                        {
                            input_tokens = input,
                            cached_input_tokens = cached,
                            output_tokens = output
                        }
                    }
                }
            }) + Environment.NewLine;
        }

        public void AppendUsage(DateTime day, long input, long cached, long output, bool cumulative = true)
        {
            File.AppendAllText(FilePath, UsageLine(day, input, cached, output, cumulative));
        }

        public async Task<TokenReport?> ComputeAsync()
        {
            AppConfig config = new()
            {
                Profiles = [new ProfileConfig { Provider = "codex", Label = "Test", Home = _home, Enabled = true }]
            };
            (TokenReport? codex, TokenReport? claude) = await new TokenStatsService().ComputeAsync(
                config, includeDefaultCodexHome: false, today: Today);
            Assert.Null(claude);
            return codex;
        }

        public void Dispose() => Directory.Delete(_home, recursive: true);
    }
}
