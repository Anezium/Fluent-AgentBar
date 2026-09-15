using System.Net;
using System.Text;
using Xunit;

namespace FluentAgentBar.Tests;

public sealed class GrokUsageServiceTests
{
    private const string AccessToken = "secret-access-token-123";

    [Fact]
    public async Task FetchAsync_WhenAuthFileIsMissing_ThrowsLoginRequired()
    {
        string home = CreateTempDirectory();
        try
        {
            using GrokUsageService service = new(CreateHttpClient(_ => throw new InvalidOperationException("no HTTP expected")));

            await Assert.ThrowsAsync<ProviderLoginRequiredException>(
                () => service.FetchAsync(home, CancellationToken.None));
        }
        finally
        {
            DeleteTempDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenAuthFileHasNoUsableToken_ThrowsLoginRequired()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteAuth(home, """{"https://auth.x.ai::client":{"auth_mode":"oidc"}}""");
            using GrokUsageService service = new(CreateHttpClient(_ => throw new InvalidOperationException("no HTTP expected")));

            await Assert.ThrowsAsync<ProviderLoginRequiredException>(
                () => service.FetchAsync(home, CancellationToken.None));
        }
        finally
        {
            DeleteTempDirectory(home);
        }
    }

    [Fact]
    public void ParseCredentials_PrefersOidcEntryOverLegacySession()
    {
        GrokCredentials credentials = GrokUsageService.ParseCredentials("""
        {
          "https://accounts.x.ai/sign-in": { "key": "legacy-should-not-win" },
          "https://auth.x.ai::client-id": {
            "key": "oidc-wins",
            "auth_mode": "oidc",
            "email": "pilot@x.ai",
            "team_id": "team-7",
            "expires_at": "2099-01-01T00:00:00Z"
          }
        }
        """);

        Assert.Equal("oidc-wins", credentials.AccessToken);
        Assert.Equal("pilot@x.ai", credentials.Email);
        Assert.Equal("team-7", credentials.TeamId);
        Assert.Equal("SuperGrok", credentials.LoginMethod);
        Assert.False(credentials.IsExpired);
    }

    [Fact]
    public void ParseCredentials_WhenOidcEntryHasNoKey_FallsBackToLegacySession()
    {
        GrokCredentials credentials = GrokUsageService.ParseCredentials("""
        {
          "https://auth.x.ai::stale-client": { "key": "", "auth_mode": "oidc" },
          "https://accounts.x.ai/sign-in": { "key": "healthy-legacy-token", "auth_mode": "session" }
        }
        """);

        Assert.Equal("healthy-legacy-token", credentials.AccessToken);
        Assert.Equal("session", credentials.LoginMethod);
    }

    [Fact]
    public async Task FetchAsync_MapsCreditUsagePercentToRemainingPercent()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteAuth(home, DefaultAuthJson("pilot@x.ai"));
            string resetAt = DateTimeOffset.UtcNow.AddDays(6).ToString("yyyy-MM-ddTHH:mm:ssZ");
            using GrokUsageService service = new(
                CreateHttpClient(request =>
                {
                    if (request.RequestUri?.AbsoluteUri == GrokUsageService.SettingsUrl)
                    {
                        return Task.FromResult(JsonResponse("""{"subscription_tier_display":"supergrok_heavy"}"""));
                    }

                    Assert.Equal(GrokUsageService.BillingUrl, request.RequestUri?.AbsoluteUri);
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                    Assert.Equal(AccessToken, request.Headers.Authorization?.Parameter);
                    Assert.Equal("xai-grok-cli", request.Headers.GetValues("x-xai-token-auth").Single());
                    return Task.FromResult(JsonResponse($$"""
                    {
                      "config": {
                        "creditUsagePercent": 12.5,
                        "currentPeriod": { "end": "{{resetAt}}" },
                        "billingPeriodEnd": "{{resetAt}}"
                      }
                    }
                    """));
                }),
                (_, _) => Task.FromResult<string?>(null));

            ProviderUsageSnapshot snapshot = await service.FetchAsync(home, CancellationToken.None);

            Assert.Equal("SuperGrok Heavy", snapshot.Plan);
            Assert.Equal("pilot@x.ai", snapshot.Email);
            ProviderQuotaGroup group = Assert.Single(snapshot.Groups);
            Assert.Equal(string.Empty, group.Name);
            ProviderQuotaWindow window = Assert.Single(group.Windows);
            Assert.Equal("Weekly", window.Label);
            Assert.Equal(88, window.RemainingPercent);
            Assert.Equal(DateTimeOffset.Parse(resetAt).ToUniversalTime(), window.ResetAt);
        }
        finally
        {
            DeleteTempDirectory(home);
        }
    }

    [Fact]
    public void ParseCreditsResponse_DerivesPercentFromOnDemandCapAndUsage()
    {
        GrokBillingSnapshot snapshot = GrokUsageService.ParseCreditsResponse("""
        {
          "config": {
            "onDemandCap": { "val": 1000.0 },
            "onDemandUsed": { "val": 250.5 }
          }
        }
        """);

        Assert.Equal(25.05, snapshot.UsedPercent!.Value, 6);
        Assert.Null(snapshot.ResetAt);
    }

    [Theory]
    [InlineData(104.2, 100.0)]
    [InlineData(-3.5, 0.0)]
    public void ParseCreditsResponse_ClampsOutOfRangePercent(double raw, double expected)
    {
        GrokBillingSnapshot snapshot = GrokUsageService.ParseCreditsResponse($$"""
        { "config": { "creditUsagePercent": {{raw.ToString(System.Globalization.CultureInfo.InvariantCulture)}} } }
        """);

        Assert.Equal(expected, snapshot.UsedPercent!.Value, 6);
    }

    [Fact]
    public void ParseCreditsResponse_TreatsPeriodWithoutUsageAsUnknown()
    {
        GrokBillingSnapshot snapshot = GrokUsageService.ParseCreditsResponse("""
        {
          "config": {
            "currentPeriod": { "end": "2026-08-13T00:00:00.123Z" },
            "billingPeriodEnd": "2026-08-14T00:00:00Z"
          }
        }
        """);

        Assert.Null(snapshot.UsedPercent);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-13T00:00:00.123Z").ToUniversalTime(),
            snapshot.ResetAt);
    }

    [Fact]
    public async Task FetchAsync_WhenUsageIsUnknown_KeepsIdentityAndExposesNoWindow()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteAuth(home, DefaultAuthJson("pilot@x.ai"));
            using GrokUsageService service = new(
                CreateHttpClient(request => Task.FromResult(
                    request.RequestUri?.AbsoluteUri == GrokUsageService.SettingsUrl
                        ? JsonResponse("""{"subscription_tier_display":"SuperGrok"}""")
                        : JsonResponse("""
                          { "config": { "billingPeriodEnd": "2099-08-14T00:00:00Z" } }
                          """))),
                (_, _) => Task.FromResult<string?>(null));

            ProviderUsageSnapshot snapshot = await service.FetchAsync(home, CancellationToken.None);

            Assert.Equal("SuperGrok", snapshot.Plan);
            Assert.Equal("pilot@x.ai", snapshot.Email);
            Assert.Empty(snapshot.Groups);
        }
        finally
        {
            DeleteTempDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenBillingReturnsUnauthorized_ThrowsLoginRequired()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteAuth(home, DefaultAuthJson("pilot@x.ai"));
            using GrokUsageService service = new(
                CreateHttpClient(request => Task.FromResult(
                    request.RequestUri?.AbsoluteUri == GrokUsageService.SettingsUrl
                        ? JsonResponse("{}")
                        : JsonResponse("""{"error":"unauthenticated"}""", HttpStatusCode.Unauthorized))),
                (_, _) => Task.FromResult<string?>(null));

            await Assert.ThrowsAsync<ProviderLoginRequiredException>(
                () => service.FetchAsync(home, CancellationToken.None));
        }
        finally
        {
            DeleteTempDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_PrefersCliBillingOverTheHttpProxy()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteAuth(home, DefaultAuthJson("pilot@x.ai"));
            DateTimeOffset periodStart = DateTimeOffset.UtcNow.AddDays(-5);
            DateTimeOffset periodEnd = DateTimeOffset.UtcNow.AddDays(25);
            int billingRequests = 0;

            using GrokUsageService service = new(
                CreateHttpClient(request =>
                {
                    if (request.RequestUri?.AbsoluteUri == GrokUsageService.SettingsUrl)
                    {
                        return Task.FromResult(JsonResponse("""{"subscription_tier_display":"SuperGrok"}"""));
                    }

                    billingRequests++;
                    return Task.FromResult(JsonResponse("{}", HttpStatusCode.InternalServerError));
                }),
                (cliHome, _) =>
                {
                    Assert.Equal(GrokUsageService.NormalizeHome(home), cliHome);
                    return Task.FromResult<string?>($$"""
                    {
                      "billingCycle": {
                        "billingPeriodStart": "{{Iso(periodStart)}}",
                        "billingPeriodEnd": "{{Iso(periodEnd)}}"
                      },
                      "monthlyLimit": { "val": 99900 },
                      "usage": { "totalUsed": { "val": 49950 } }
                    }
                    """);
                });

            ProviderUsageSnapshot snapshot = await service.FetchAsync(home, CancellationToken.None);

            Assert.Equal(0, billingRequests);
            ProviderQuotaWindow window = Assert.Single(Assert.Single(snapshot.Groups).Windows);
            Assert.Equal("Monthly", window.Label);
            Assert.Equal(50, window.RemainingPercent);
            Assert.Equal(Iso(periodEnd), Iso(window.ResetAt!.Value));
        }
        finally
        {
            DeleteTempDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenCliIsUnavailable_FallsBackToTheHttpProxy()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteAuth(home, DefaultAuthJson("pilot@x.ai"));
            using GrokUsageService service = new(
                CreateHttpClient(request => Task.FromResult(
                    request.RequestUri?.AbsoluteUri == GrokUsageService.SettingsUrl
                        ? JsonResponse("{}")
                        : JsonResponse("""{"config":{"creditUsagePercent":40}}"""))),
                (_, _) => throw new FileNotFoundException("grok CLI not installed"));

            ProviderUsageSnapshot snapshot = await service.FetchAsync(home, CancellationToken.None);

            ProviderQuotaWindow window = Assert.Single(Assert.Single(snapshot.Groups).Windows);
            Assert.Equal(60, window.RemainingPercent);
            Assert.Equal("Credits", window.Label);
            // No settings tier and no billing tier: the auth-mode login method is the last resort.
            Assert.Equal("SuperGrok", snapshot.Plan);
        }
        finally
        {
            DeleteTempDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenSettingsFails_StillReportsUsage()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteAuth(home, DefaultAuthJson("pilot@x.ai"));
            using GrokUsageService service = new(
                CreateHttpClient(request => Task.FromResult(
                    request.RequestUri?.AbsoluteUri == GrokUsageService.SettingsUrl
                        ? JsonResponse("nope", HttpStatusCode.ServiceUnavailable)
                        : JsonResponse("""{"config":{"creditUsagePercent":0,"subscriptionTier":"supergrok"}}"""))),
                (_, _) => Task.FromResult<string?>(null));

            ProviderUsageSnapshot snapshot = await service.FetchAsync(home, CancellationToken.None);

            Assert.Equal("SuperGrok", snapshot.Plan);
            Assert.Equal(100, Assert.Single(Assert.Single(snapshot.Groups).Windows).RemainingPercent);
        }
        finally
        {
            DeleteTempDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_CachesTheSnapshotPerHome()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteAuth(home, DefaultAuthJson("pilot@x.ai"));
            int billingRequests = 0;
            using GrokUsageService service = new(
                CreateHttpClient(request =>
                {
                    if (request.RequestUri?.AbsoluteUri == GrokUsageService.SettingsUrl)
                    {
                        return Task.FromResult(JsonResponse("{}"));
                    }

                    billingRequests++;
                    return Task.FromResult(JsonResponse("""{"config":{"creditUsagePercent":10}}"""));
                }),
                (_, _) => Task.FromResult<string?>(null));

            ProviderUsageSnapshot first = await service.FetchAsync(home, CancellationToken.None);
            ProviderUsageSnapshot second = await service.FetchAsync(home, CancellationToken.None);

            Assert.Equal(1, billingRequests);
            Assert.Same(first, second);
        }
        finally
        {
            DeleteTempDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_AfterAFailure_BacksOffAndKeepsTheLastSnapshot()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteAuth(home, DefaultAuthJson("pilot@x.ai"));
            int billingRequests = 0;
            bool fail = false;
            using GrokUsageService service = new(
                CreateHttpClient(request =>
                {
                    if (request.RequestUri?.AbsoluteUri == GrokUsageService.SettingsUrl)
                    {
                        return Task.FromResult(JsonResponse("{}"));
                    }

                    billingRequests++;
                    return Task.FromResult(fail
                        ? JsonResponse("{}", HttpStatusCode.InternalServerError)
                        : JsonResponse("""{"config":{"creditUsagePercent":10}}"""));
                }),
                (_, _) => Task.FromResult<string?>(null));

            ProviderUsageSnapshot first = await service.FetchAsync(home, CancellationToken.None);
            ExpireCache(service, home);

            fail = true;
            ProviderUsageSnapshot stale = await service.FetchAsync(home, CancellationToken.None);
            ProviderUsageSnapshot backedOff = await service.FetchAsync(home, CancellationToken.None);

            Assert.Same(first, stale);
            Assert.Same(first, backedOff);
            // Two billing calls total: the initial success and the single failure. The third
            // fetch is served from the cache because the backoff window is still open.
            Assert.Equal(2, billingRequests);
        }
        finally
        {
            DeleteTempDirectory(home);
        }
    }

    [Theory]
    [InlineData("supergrok", "SuperGrok")]
    [InlineData("SUPER_GROK_HEAVY", "SuperGrok Heavy")]
    [InlineData("heavy", "SuperGrok Heavy")]
    [InlineData("  Enterprise  ", "Enterprise")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void PlanDisplayName_NormalisesKnownTiers(string? raw, string? expected)
    {
        Assert.Equal(expected, GrokUsageService.PlanDisplayName(raw));
    }

    [Fact]
    public void WindowLabel_UsesThePeriodDurationThenTheResetDistance()
    {
        DateTimeOffset now = new(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("Weekly", GrokUsageService.WindowLabel(7 * 24 * 60, now.AddDays(1), now));
        Assert.Equal("Monthly", GrokUsageService.WindowLabel(30 * 24 * 60, now.AddDays(1), now));
        Assert.Equal("Weekly", GrokUsageService.WindowLabel(null, now.AddDays(6), now));
        Assert.Equal("Monthly", GrokUsageService.WindowLabel(null, now.AddDays(28), now));
        // An untyped credits window close to its reset is still the weekly pool.
        Assert.Equal("Weekly", GrokUsageService.WindowLabel(null, now.AddHours(2), now));
        // An explicit period that matches neither cadence falls back to the generic label.
        Assert.Equal("Credits", GrokUsageService.WindowLabel(3 * 24 * 60, null, now));
        Assert.Equal("Credits", GrokUsageService.WindowLabel(null, null, now));
    }

    [Fact]
    public void ParseCliBilling_AcceptsTheJsonRpcEnvelopeAndTheBareResult()
    {
        const string result = """
        {
          "billingCycle": {
            "billingPeriodStart": "2026-08-01T00:00:00Z",
            "billingPeriodEnd": "2026-09-01T00:00:00Z"
          },
          "monthlyLimit": { "val": 1000 },
          "usage": { "totalUsed": { "val": 250 } }
        }
        """;

        GrokBillingSnapshot? bare = GrokUsageService.ParseCliBilling(result);
        GrokBillingSnapshot? enveloped = GrokUsageService.ParseCliBilling(
            $$"""{"jsonrpc":"2.0","id":2,"result":{{result}}}""");

        Assert.NotNull(bare);
        Assert.NotNull(enveloped);
        Assert.Equal(25.0, bare!.UsedPercent!.Value, 6);
        Assert.Equal(bare.UsedPercent, enveloped!.UsedPercent);
        Assert.Equal(31 * 24 * 60, bare.PeriodMinutes);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T00:00:00Z").ToUniversalTime(), bare.ResetAt);
    }

    [Fact]
    public void ParseCliBilling_WithoutLimitOrPeriod_ReturnsNull()
    {
        Assert.Null(GrokUsageService.ParseCliBilling("""{"usage":{"totalUsed":{"val":100}}}"""));
    }

    [Fact]
    public void ParseCliBilling_ClampsUsageAboveTheMonthlyLimit()
    {
        GrokBillingSnapshot? snapshot = GrokUsageService.ParseCliBilling("""
        { "monthlyLimit": { "val": 1000 }, "usage": { "totalUsed": { "val": 5000 } },
          "billingCycle": { "billingPeriodEnd": "2026-09-01T00:00:00Z" } }
        """);

        Assert.Equal(100.0, snapshot!.UsedPercent!.Value, 6);
    }

    [Fact]
    public void NormalizeHome_ExpandsEnvironmentVariables()
    {
        string expected = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".grok"));

        Assert.Equal(expected, GrokUsageService.NormalizeHome("%USERPROFILE%\\.grok"));
        Assert.Equal(expected, GrokUsageService.NormalizeHome("   "));
    }

    private static string DefaultAuthJson(string email)
    {
        return $$"""
        {
          "https://auth.x.ai::client-id": {
            "key": "{{AccessToken}}",
            "auth_mode": "oidc",
            "email": "{{email}}",
            "expires_at": "2099-01-01T00:00:00Z"
          }
        }
        """;
    }

    private static string Iso(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    private static void WriteAuth(string home, string json)
    {
        File.WriteAllText(Path.Combine(home, "auth.json"), json);
    }

    // The per-home cache is private state; rewind its clock instead of sleeping for minutes.
    private static void ExpireCache(GrokUsageService service, string home)
    {
        object states = typeof(GrokUsageService)
            .GetField("_states", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;
        object state = states.GetType()
            .GetProperty("Item")!
            .GetValue(states, [GrokUsageService.NormalizeHome(home)])!;
        state.GetType()
            .GetProperty("LastSuccessfulFetch")!
            .SetValue(state, DateTimeOffset.MinValue);
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync)
    {
        return new HttpClient(new DelegateHandler(sendAsync));
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "FluentAgentBar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTempDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return sendAsync(request);
        }
    }
}
