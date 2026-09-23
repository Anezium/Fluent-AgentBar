using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FluentAgentBar.Tests;

public sealed class ModelPricingCatalogTests
{
    private const string LiteLlmJson = """
        {
          "sample_spec": { "litellm_provider": "one of https://docs.litellm.ai/docs/providers", "input_cost_per_token": 0 },
          "claude-opus-5-5": {
            "litellm_provider": "anthropic",
            "input_cost_per_token": 3e-06,
            "output_cost_per_token": 1.5e-05,
            "cache_read_input_token_cost": 3e-07,
            "cache_creation_input_token_cost": 3.75e-06
          },
          "claude-nova-7-20261201": { "litellm_provider": "anthropic", "input_cost_per_token": 7e-06, "output_cost_per_token": 3.5e-05 },
          "bedrock/claude-nova-7": { "litellm_provider": "bedrock", "input_cost_per_token": 1, "output_cost_per_token": 1 },
          "gpt-7-orion": { "litellm_provider": "openai", "input_cost_per_token": 1e-06, "output_cost_per_token": 8e-06, "cache_read_input_token_cost": 1e-07 },
          "gemini-9-pro": { "litellm_provider": "gemini", "input_cost_per_token": 1e-06, "output_cost_per_token": 1e-06 }
        }
        """;

    [Theory]
    [InlineData("claude-fable-5-1", 10.00, 0.25)]
    [InlineData("claude-fable-5", 10.00, 1.00)]
    [InlineData("claude-opus-5-5-20260915", 4.00, 0.20)]
    [InlineData("claude-opus-5", 5.00, 0.50)]
    [InlineData("gpt-6-sol", 2.00, 0.20)]
    [InlineData("gpt-5.6-sol", 4.00, 0.40)]
    [InlineData("gpt-5.6-terra", 2.00, 0.20)]
    [InlineData("openai/GPT-5.5-2026-01-02", 5.00, 0.50)]
    public void TryGetPricing_BuiltInTablePicksTheMostSpecificPrefix(string model, double input, double cacheRead)
    {
        Assert.True(ModelPricingCatalog.BuiltIn.TryGetPricing(model, out ModelPricing? pricing));
        Assert.Equal(input, pricing!.Input);
        Assert.Equal(cacheRead, pricing.CacheRead);
    }

    [Theory]
    [InlineData("claude-nova-7")]
    [InlineData("gpt-7-orion")]
    [InlineData("")]
    public void TryGetPricing_BuiltInTableDoesNotGuessUnknownFamilies(string model)
    {
        Assert.False(ModelPricingCatalog.BuiltIn.TryGetPricing(model, out _));
    }

    [Fact]
    public async Task RefreshAsync_DownloadsFirstPartyPricesAndReusesTheDiskCache()
    {
        using TemporaryDirectory temp = new();
        FakeLiteLlm server = new();
        DateTimeOffset now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

        using (ModelPricingCatalog catalog = server.CreateCatalog(temp.Path, () => now))
        {
            await catalog.RefreshAsync();

            // Remote prices win over the built-in table.
            AssertPrice(catalog, "claude-opus-5-5", 3.00, 15.00, 0.30, 3.75);
            // Dated keys are matched by their undated name, and vice versa.
            AssertPrice(catalog, "claude-nova-7", 7.00, 35.00, 0, 0);
            AssertPrice(catalog, "claude-nova-7-20261201", 7.00, 35.00, 0, 0);
            AssertPrice(catalog, "gpt-7-orion", 1.00, 8.00, 0.10, 0);
            // Only first-party Anthropic/OpenAI ids are taken.
            Assert.False(catalog.TryGetPricing("gemini-9-pro", out _));
        }

        Assert.Equal(1, server.Requests);
        Assert.True(File.Exists(Path.Combine(temp.Path, ModelPricingCatalog.CacheFileName)));

        // A fresh start within the day reads the cache without any request.
        FakeLiteLlm offline = new() { Fail = true };
        using ModelPricingCatalog restarted = offline.CreateCatalog(temp.Path, () => now.AddHours(3));
        await restarted.RefreshAsync();

        Assert.Equal(0, offline.Requests);
        AssertPrice(restarted, "claude-nova-7", 7.00, 35.00, 0, 0);
    }

    [Fact]
    public async Task RefreshAsync_RevalidatesWithETagOnceADay()
    {
        using TemporaryDirectory temp = new();
        FakeLiteLlm server = new();
        DateTimeOffset now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        using ModelPricingCatalog catalog = server.CreateCatalog(temp.Path, () => now);

        await catalog.RefreshAsync();
        now = now.AddHours(23);
        await catalog.RefreshAsync();
        Assert.Equal(1, server.Requests);

        now = now.AddHours(2);
        await catalog.RefreshAsync();

        Assert.Equal(2, server.Requests);
        Assert.Equal("\"v1\"", server.LastIfNoneMatch);
        AssertPrice(catalog, "claude-nova-7", 7.00, 35.00, 0, 0);

        // The 304 counts as a fresh download.
        now = now.AddHours(1);
        await catalog.RefreshAsync();
        Assert.Equal(2, server.Requests);
    }

    [Fact]
    public async Task RefreshAsync_KeepsBuiltInPricesAndRetriesHourlyWhenTheDownloadFails()
    {
        using TemporaryDirectory temp = new();
        FakeLiteLlm server = new() { Fail = true };
        DateTimeOffset now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        using ModelPricingCatalog catalog = server.CreateCatalog(temp.Path, () => now);

        await catalog.RefreshAsync();
        AssertPrice(catalog, "claude-opus-5-5", 4.00, 20.00, 0.20, 5.00);

        now = now.AddMinutes(30);
        await catalog.RefreshAsync();
        Assert.Equal(1, server.Requests);

        server.Fail = false;
        now = now.AddMinutes(45);
        await catalog.RefreshAsync();

        Assert.Equal(2, server.Requests);
        AssertPrice(catalog, "claude-opus-5-5", 3.00, 15.00, 0.30, 3.75);
    }

    [Fact]
    public async Task RefreshAsync_OverridesWinAndReloadWhenTheFileChanges()
    {
        using TemporaryDirectory temp = new();
        FakeLiteLlm server = new();
        DateTimeOffset now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        string overridesPath = Path.Combine(temp.Path, ModelPricingCatalog.OverridesFileName);
        File.WriteAllText(overridesPath, """
            {
              // Comments and trailing commas are fine.
              "Claude-Opus-5-5": { "input": 1, "output": 2 },
              "claude-nova": { "input": 9, "output": 45, "cacheRead": 0.9, "cacheWrite": 11 },
            }
            """);
        using ModelPricingCatalog catalog = server.CreateCatalog(temp.Path, () => now);

        await catalog.RefreshAsync();

        // Omitted cache prices default to 0.1x / 1.25x the input price.
        AssertPrice(catalog, "claude-opus-5-5", 1.00, 2.00, 0.10, 1.25);
        // Overrides match by prefix, even over an exact remote entry.
        AssertPrice(catalog, "claude-nova-7", 9.00, 45.00, 0.90, 11.00);

        File.WriteAllText(overridesPath, "{ not json");
        File.SetLastWriteTimeUtc(overridesPath, DateTime.UtcNow.AddMinutes(1));
        await catalog.RefreshAsync();

        AssertPrice(catalog, "claude-opus-5-5", 3.00, 15.00, 0.30, 3.75);
    }

    [Fact]
    public async Task ComputeAsync_UsesRefreshedPricesForJournalCosts()
    {
        using TemporaryDirectory temp = new();
        FakeLiteLlm server = new();
        using ModelPricingCatalog catalog = server.CreateCatalog(
            Path.Combine(temp.Path, "config"),
            () => DateTimeOffset.UtcNow);
        string projects = Path.Combine(temp.Path, "claude", "projects", "p");
        Directory.CreateDirectory(projects);
        object line = new
        {
            timestamp = DateTimeOffset.Now.ToString("O"),
            message = new
            {
                id = "m1",
                model = "claude-nova-7",
                usage = new { input_tokens = 1_000_000, output_tokens = 1_000_000 }
            }
        };
        File.WriteAllText(Path.Combine(projects, "session.jsonl"), JsonSerializer.Serialize(line) + Environment.NewLine);
        AppConfig config = new()
        {
            Profiles = [new ProfileConfig { Provider = "claude", Label = "Temp", Home = Path.Combine(temp.Path, "claude"), Enabled = true }]
        };

        (_, TokenReport? claude) = await new TokenStatsService(catalog).ComputeAsync(config, includeDefaultCodexHome: false);

        TokenStats today = Assert.IsType<TokenStats>(claude?.Today);
        Assert.Equal(42.00, today.CostUsd, precision: 6);
        Assert.Equal(0, today.UnpricedTokens);
    }

    private static void AssertPrice(
        ModelPricingCatalog catalog,
        string model,
        double input,
        double output,
        double cacheRead,
        double cacheWrite)
    {
        Assert.True(catalog.TryGetPricing(model, out ModelPricing? pricing), model);
        Assert.Equal(input, pricing!.Input, precision: 6);
        Assert.Equal(output, pricing.Output, precision: 6);
        Assert.Equal(cacheRead, pricing.CacheRead, precision: 6);
        Assert.Equal(cacheWrite, pricing.CacheWrite, precision: 6);
    }

    private sealed class FakeLiteLlm
    {
        public bool Fail { get; set; }

        public int Requests { get; private set; }

        public string? LastIfNoneMatch { get; private set; }

        public ModelPricingCatalog CreateCatalog(string directory, Func<DateTimeOffset> utcNow)
        {
            HttpClient httpClient = new(new DelegateHandler(Respond));
            return new ModelPricingCatalog(directory, httpClient, ownsHttpClient: true, utcNow);
        }

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Requests++;
            Assert.Equal(ModelPricingCatalog.RemoteUrl, request.RequestUri?.ToString());
            if (Fail)
            {
                throw new HttpRequestException("offline");
            }

            LastIfNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
            if (LastIfNoneMatch == "\"v1\"")
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(LiteLlmJson, Encoding.UTF8, "application/json")
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
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
