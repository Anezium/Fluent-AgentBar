using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace FluentAgentBar.Tests;

public sealed class GeminiUsageServiceTests
{
    private const string AntigravityUsageReport = """
    {
      "status": "SUCCESS",
      "response": "Gemini Models\tWeekly Limit Remaining\t75.6%",
      "command": {
        "name": "usage",
        "data": {
          "description": "Usage for the current account",
          "groups": [
            {
              "name": "Gemini Models",
              "description": "Models within this group: Gemini Flash, Gemini Pro",
              "buckets": [
                {
                  "id": "gemini-weekly",
                  "name": "Weekly Limit Remaining",
                  "window": "weekly",
                  "remaining_fraction": 0.756,
                  "reset_time": "2026-09-18T15:10:21Z"
                },
                {
                  "id": "gemini-5h",
                  "name": "Five Hour Limit Remaining",
                  "window": "5h",
                  "remaining_fraction": 0.998,
                  "reset_time": "2026-09-15T13:27:48Z"
                }
              ]
            },
            {
              "name": "Claude and GPT models",
              "description": "Models within this group: Claude Opus, Claude Sonnet, GPT-OSS",
              "buckets": [
                {
                  "id": "3p-weekly",
                  "name": "Weekly Limit Remaining",
                  "window": "weekly",
                  "remaining_fraction": 1,
                  "reset_time": "2026-09-22T08:29:20Z"
                },
                {
                  "id": "3p-5h",
                  "name": "Five Hour Limit Remaining",
                  "window": "5h",
                  "remaining_fraction": 1,
                  "reset_time": "2026-09-15T13:29:20Z"
                }
              ]
            }
          ]
        }
      }
    }
    """;

    // MARK: Antigravity CLI (primary source)

    [Fact]
    public async Task FetchAsync_WithAntigravityReport_MapsGeminiGroupFirstAndShortWindowFirst()
    {
        string home = CreateTempDirectory();
        try
        {
            using GeminiUsageService service = CreateService(
                antigravityBinary: @"C:\agy\agy.exe",
                processRunner: AntigravityRunner("1.2.3", 0, AntigravityUsageReport));

            ProviderUsageSnapshot snapshot = await service.FetchAsync(home, CancellationToken.None);

            Assert.Equal("Antigravity", snapshot.Plan);
            Assert.Equal(string.Empty, snapshot.Email);
            Assert.Equal(2, snapshot.Groups.Count);

            ProviderQuotaGroup gemini = snapshot.Groups[0];
            Assert.Equal("Gemini Models", gemini.Name);
            Assert.Equal(["5h", "Weekly"], gemini.Windows.Select(window => window.Label));
            Assert.Equal([100, 76], gemini.Windows.Select(window => window.RemainingPercent));
            Assert.Equal(
                DateTimeOffset.Parse("2026-09-15T13:27:48Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal),
                gemini.Windows[0].ResetAt);
            Assert.Equal(
                DateTimeOffset.Parse("2026-09-18T15:10:21Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal),
                gemini.Windows[1].ResetAt);

            ProviderQuotaGroup thirdParty = snapshot.Groups[1];
            Assert.Equal("Claude and GPT models", thirdParty.Name);
            Assert.Equal(["5h", "Weekly"], thirdParty.Windows.Select(window => window.Label));
            Assert.Equal([100, 100], thirdParty.Windows.Select(window => window.RemainingPercent));
        }
        finally
        {
            TryDeleteDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_WithAntigravityReport_RunsUsageFromATemporaryWorkingDirectory()
    {
        string home = CreateTempDirectory();
        try
        {
            List<ProcessStartInfo> invocations = [];
            using GeminiUsageService service = CreateService(
                antigravityBinary: @"C:\agy\agy.exe",
                processRunner: (startInfo, _) =>
                {
                    invocations.Add(startInfo);
                    return Task.FromResult(new GeminiProcessResult(
                        0,
                        startInfo.ArgumentList.Contains("--version") ? "1.2.3\n" : AntigravityUsageReport));
                });

            await service.FetchAsync(home, CancellationToken.None);

            Assert.Equal(2, invocations.Count);
            Assert.Equal(["--version"], invocations[0].ArgumentList);
            Assert.Equal(
                ["-p", "/usage", "--output-format", "json", "--print-timeout", "90s"],
                invocations[1].ArgumentList);

            foreach (ProcessStartInfo invocation in invocations)
            {
                Assert.Equal(@"C:\agy\agy.exe", invocation.FileName);
                Assert.True(invocation.CreateNoWindow);
                Assert.False(invocation.UseShellExecute);
                Assert.True(invocation.RedirectStandardInput);
                Assert.StartsWith(Path.GetTempPath(), invocation.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual(home, invocation.WorkingDirectory);
            }
        }
        finally
        {
            TryDeleteDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenAntigravityVersionIsTooOld_Fails()
    {
        string home = CreateTempDirectory();
        try
        {
            using GeminiUsageService service = CreateService(
                antigravityBinary: @"C:\agy\agy.exe",
                processRunner: AntigravityRunner("1.1.10", 0, AntigravityUsageReport));

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.FetchAsync(home, CancellationToken.None));

            Assert.Contains("1.1.11", exception.Message);
        }
        finally
        {
            TryDeleteDirectory(home);
        }
    }

    [Theory]
    [InlineData("1.1.11")]
    [InlineData("1.2.3")]
    [InlineData("2.0.0")]
    public void EnsureSupportedAntigravityVersion_AcceptsSupportedVersions(string version)
    {
        GeminiUsageService.EnsureSupportedAntigravityVersion(version);
    }

    [Theory]
    [InlineData("1.1.10")]
    [InlineData("1.0.99")]
    [InlineData("0.9.0")]
    [InlineData("1.2")]
    [InlineData("1.2.3-beta")]
    [InlineData("")]
    public void EnsureSupportedAntigravityVersion_RejectsUnsupportedVersions(string version)
    {
        Assert.Throws<InvalidOperationException>(() => GeminiUsageService.EnsureSupportedAntigravityVersion(version));
    }

    [Fact]
    public void ParseAntigravityUsageReport_WhenStatusIsError_ReportsATransientFailure()
    {
        const string report = """
        {
          "status": "ERROR",
          "error": "Eligibility check failed: rpc error: code = UNAVAILABLE (code 503) desc = upstream"
        }
        """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => GeminiUsageService.ParseAntigravityUsageReport(report));

        Assert.IsNotType<ProviderLoginRequiredException>(exception);
        Assert.Contains("503", exception.Message);
    }

    [Fact]
    public void ParseAntigravityUsageReport_WhenNotSignedIn_RequiresLogin()
    {
        const string report = """
        { "status": "ERROR", "error": "You are not logged in. Run agy to authenticate." }
        """;

        Assert.Throws<ProviderLoginRequiredException>(
            () => GeminiUsageService.ParseAntigravityUsageReport(report));
    }

    [Fact]
    public void ParseAntigravityUsageReport_WhenCommandIsNotUsage_Fails()
    {
        const string report = """
        { "status": "SUCCESS", "command": { "name": "chat", "data": { "groups": [] } } }
        """;

        Assert.Throws<InvalidOperationException>(() => GeminiUsageService.ParseAntigravityUsageReport(report));
    }

    [Fact]
    public void ParseAntigravityUsageReport_WhenEveryBucketIsDisabledOrEmpty_Fails()
    {
        const string report = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "usage",
            "data": {
              "groups": [
                {
                  "name": "Gemini Models",
                  "buckets": [
                    { "id": "gemini-5h", "window": "5h", "remaining_fraction": 0.5, "disabled": true },
                    { "id": "gemini-weekly", "window": "weekly" }
                  ]
                }
              ]
            }
          }
        }
        """;

        Assert.Throws<InvalidOperationException>(() => GeminiUsageService.ParseAntigravityUsageReport(report));
    }

    [Fact]
    public void ParseAntigravityUsageReport_AcceptsAlternateBucketKeysAndNestedRemaining()
    {
        const string report = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "usage",
            "data": {
              "groups": [
                {
                  "display_name": "Gemini Models",
                  "buckets": [
                    {
                      "bucket_id": "gemini-5h",
                      "display_name": "Five Hour Limit Remaining",
                      "remaining": { "remaining_fraction": 0.42 }
                    },
                    {
                      "bucket_id": "gemini-credits",
                      "display_name": "Bonus Credits",
                      "remaining_fraction": 0.1
                    }
                  ]
                }
              ]
            }
          }
        }
        """;

        ProviderUsageSnapshot snapshot = GeminiUsageService.ParseAntigravityUsageReport(report);

        ProviderQuotaGroup group = Assert.Single(snapshot.Groups);
        Assert.Equal("Gemini Models", group.Name);
        Assert.Equal(["5h", "Bonus Credits"], group.Windows.Select(window => window.Label));
        Assert.Equal([42, 10], group.Windows.Select(window => window.RemainingPercent));
    }

    [Fact]
    public async Task FetchAsync_WhenAntigravityIsAbsentAndNoGeminiCredentials_RequiresLogin()
    {
        string home = CreateTempDirectory();
        try
        {
            using GeminiUsageService service = CreateService(antigravityBinary: null);

            ProviderLoginRequiredException exception =
                await Assert.ThrowsAsync<ProviderLoginRequiredException>(
                    () => service.FetchAsync(home, CancellationToken.None));

            Assert.Contains("agy", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_CachesTheAntigravityReportForFiveMinutes()
    {
        string home = CreateTempDirectory();
        try
        {
            int usageRuns = 0;
            using GeminiUsageService service = CreateService(
                antigravityBinary: @"C:\agy\agy.exe",
                processRunner: (startInfo, _) =>
                {
                    if (startInfo.ArgumentList.Contains("--version"))
                    {
                        return Task.FromResult(new GeminiProcessResult(0, "1.2.3"));
                    }

                    usageRuns++;
                    return Task.FromResult(new GeminiProcessResult(0, AntigravityUsageReport));
                });

            ProviderUsageSnapshot first = await service.FetchAsync(home, CancellationToken.None);
            ProviderUsageSnapshot second = await service.FetchAsync(home, CancellationToken.None);

            Assert.Same(first, second);
            Assert.Equal(1, usageRuns);
        }
        finally
        {
            TryDeleteDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_AfterAFailure_BacksOffInsteadOfRerunningTheCli()
    {
        string home = CreateTempDirectory();
        try
        {
            int runs = 0;
            using GeminiUsageService service = CreateService(
                antigravityBinary: @"C:\agy\agy.exe",
                processRunner: (_, _) =>
                {
                    runs++;
                    throw new InvalidOperationException("agy exploded");
                });

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.FetchAsync(home, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.FetchAsync(home, CancellationToken.None));

            Assert.Equal(1, runs);
        }
        finally
        {
            TryDeleteDirectory(home);
        }
    }

    // MARK: Gemini CLI OAuth fallback

    [Fact]
    public async Task FetchAsync_WhenAntigravityIsAbsent_FallsBackToTheGeminiOAuthPipeline()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteCredentials(home, accessToken: "live-access", expiresAt: DateTimeOffset.UtcNow.AddHours(1));

            using GeminiUsageService service = CreateService(
                antigravityBinary: null,
                httpClient: CreateHttpClient(request =>
                {
                    AssertBearerToken(request, "live-access");
                    return request.RequestUri?.AbsoluteUri switch
                    {
                        "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist" => JsonResponse("""
                        {
                          "currentTier": { "id": "free-tier" },
                          "cloudaicompanionProject": "gen-lang-client-0001"
                        }
                        """),
                        "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota" => JsonResponse("""
                        {
                          "buckets": [
                            { "modelId": "gemini-2.5-pro", "remainingFraction": 0.6, "resetTime": "2026-09-16T00:00:00Z" },
                            { "modelId": "gemini-2.5-flash", "remainingFraction": 0.9, "resetTime": "2026-09-16T00:00:00Z" },
                            { "modelId": "gemini-2.5-flash-lite", "remainingFraction": 0.8, "resetTime": "2026-09-16T00:00:00Z" }
                          ]
                        }
                        """),
                        _ => throw new InvalidOperationException($"Unexpected request {request.RequestUri}")
                    };
                }));

            ProviderUsageSnapshot snapshot = await service.FetchAsync(home, CancellationToken.None);

            Assert.Equal("Free", snapshot.Plan);
            ProviderQuotaGroup group = Assert.Single(snapshot.Groups);
            Assert.Equal(string.Empty, group.Name);
            Assert.Equal(["Pro", "Flash", "Flash Lite"], group.Windows.Select(window => window.Label));
            Assert.Equal([60, 90, 80], group.Windows.Select(window => window.RemainingPercent));
        }
        finally
        {
            TryDeleteDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenTheStoredTokenExpired_RefreshesAndPersistsTheNewToken()
    {
        string home = CreateTempDirectory();
        try
        {
            string idToken = MakeIdToken("""{"email":"dev@example.com","hd":"example.com"}""");
            WriteCredentials(
                home,
                accessToken: "stale-access",
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5),
                refreshToken: "live-refresh",
                extraFields: """, "token_type": "Bearer", "scope": "openid" """);

            using GeminiUsageService service = CreateService(
                antigravityBinary: null,
                oauthClient: new GeminiOAuthClient("test-client-id", "test-client-secret"),
                httpClient: CreateAsyncHttpClient(async request =>
                {
                    if (request.RequestUri?.AbsoluteUri == "https://oauth2.googleapis.com/token")
                    {
                        string body = await request.Content!.ReadAsStringAsync();
                        Assert.Contains("client_id=test-client-id", body);
                        Assert.Contains("client_secret=test-client-secret", body);
                        Assert.Contains("refresh_token=live-refresh", body);
                        Assert.Contains("grant_type=refresh_token", body);
                        return JsonResponse($$"""
                        { "access_token": "fresh-access", "expires_in": 3600, "id_token": "{{idToken}}" }
                        """);
                    }

                    AssertBearerToken(request, "fresh-access");
                    return request.RequestUri?.AbsoluteUri switch
                    {
                        "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist" => JsonResponse("""
                        { "currentTier": { "id": "free-tier" }, "cloudaicompanionProject": { "id": "proj-1" } }
                        """),
                        "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota" => JsonResponse("""
                        { "buckets": [ { "modelId": "gemini-2.5-pro", "remainingFraction": 0.25 } ] }
                        """),
                        _ => throw new InvalidOperationException($"Unexpected request {request.RequestUri}")
                    };
                }));

            ProviderUsageSnapshot snapshot = await service.FetchAsync(home, CancellationToken.None);

            // free-tier plus an hd claim means a Workspace account.
            Assert.Equal("Workspace", snapshot.Plan);
            Assert.Equal("dev@example.com", snapshot.Email);
            Assert.Equal(25, Assert.Single(Assert.Single(snapshot.Groups).Windows).RemainingPercent);

            JsonObject stored = ReadCredentials(home);
            Assert.Equal("fresh-access", stored["access_token"]!.GetValue<string>());
            Assert.Equal(idToken, stored["id_token"]!.GetValue<string>());
            Assert.Equal("live-refresh", stored["refresh_token"]!.GetValue<string>());
            Assert.Equal("Bearer", stored["token_type"]!.GetValue<string>());
            Assert.True(stored["expiry_date"]!.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        finally
        {
            TryDeleteDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenTheRefreshTokenIsRejected_RequiresLogin()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteCredentials(
                home,
                accessToken: "stale-access",
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5),
                refreshToken: "dead-refresh");

            using GeminiUsageService service = CreateService(
                antigravityBinary: null,
                oauthClient: new GeminiOAuthClient("id", "secret"),
                httpClient: CreateHttpClient(_ => JsonResponse(
                    """{ "error": "invalid_grant" }""",
                    HttpStatusCode.Unauthorized)));

            await Assert.ThrowsAsync<ProviderLoginRequiredException>(
                () => service.FetchAsync(home, CancellationToken.None));
        }
        finally
        {
            TryDeleteDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenThereIsNoRefreshToken_RequiresLogin()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteCredentials(home, accessToken: "stale-access", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));

            using GeminiUsageService service = CreateService(antigravityBinary: null);

            await Assert.ThrowsAsync<ProviderLoginRequiredException>(
                () => service.FetchAsync(home, CancellationToken.None));
        }
        finally
        {
            TryDeleteDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenQuotaReturns401_RequiresLogin()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteCredentials(home, accessToken: "live-access", expiresAt: DateTimeOffset.UtcNow.AddHours(1));

            using GeminiUsageService service = CreateService(
                antigravityBinary: null,
                httpClient: CreateHttpClient(request =>
                    request.RequestUri?.AbsoluteUri == "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist"
                        ? JsonResponse("""{ "currentTier": { "id": "free-tier" } }""")
                        : JsonResponse("""{ "error": "unauthenticated" }""", HttpStatusCode.Unauthorized)));

            await Assert.ThrowsAsync<ProviderLoginRequiredException>(
                () => service.FetchAsync(home, CancellationToken.None));
        }
        finally
        {
            TryDeleteDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenGoogleReportsTheConsumerShutdown_PointsAtAntigravity()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteCredentials(home, accessToken: "live-access", expiresAt: DateTimeOffset.UtcNow.AddHours(1));

            using GeminiUsageService service = CreateService(
                antigravityBinary: null,
                httpClient: CreateHttpClient(_ => JsonResponse("""
                {
                  "ineligibleTiers": [
                    { "reasonCode": "UNSUPPORTED_CLIENT", "reasonMessage": "Client is no longer supported", "tierId": "free-tier" }
                  ]
                }
                """)));

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.FetchAsync(home, CancellationToken.None));

            Assert.IsNotType<ProviderLoginRequiredException>(exception);
            Assert.Contains("agy", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenSettingsSelectApiKeyAuth_Fails()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteCredentials(home, accessToken: "live-access", expiresAt: DateTimeOffset.UtcNow.AddHours(1));
            File.WriteAllText(
                Path.Combine(home, "settings.json"),
                """{ "security": { "auth": { "selectedType": "api-key" } } }""");

            using GeminiUsageService service = CreateService(antigravityBinary: null);

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.FetchAsync(home, CancellationToken.None));

            Assert.Contains("API key", exception.Message);
        }
        finally
        {
            TryDeleteDirectory(home);
        }
    }

    [Fact]
    public async Task FetchAsync_ExpandsEnvironmentVariablesInTheHomePath()
    {
        string home = CreateTempDirectory();
        try
        {
            WriteCredentials(home, accessToken: "live-access", expiresAt: DateTimeOffset.UtcNow.AddHours(1));
            Environment.SetEnvironmentVariable("FLUENT_AGENTBAR_TEST_GEMINI_HOME", home);

            using GeminiUsageService service = CreateService(
                antigravityBinary: null,
                httpClient: CreateHttpClient(request =>
                    request.RequestUri?.AbsoluteUri == "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist"
                        ? JsonResponse("""{ "currentTier": { "id": "standard-tier" } }""")
                        : JsonResponse("""{ "buckets": [ { "modelId": "gemini-2.5-pro", "remainingFraction": 0.5 } ] }""")));

            ProviderUsageSnapshot snapshot = await service.FetchAsync(
                "%FLUENT_AGENTBAR_TEST_GEMINI_HOME%",
                CancellationToken.None);

            Assert.Equal("Paid", snapshot.Plan);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLUENT_AGENTBAR_TEST_GEMINI_HOME", null);
            TryDeleteDirectory(home);
        }
    }

    // MARK: pure helpers

    [Fact]
    public void ParseModelQuotas_KeepsTheTightestBucketPerModel()
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse("""
        {
          "buckets": [
            { "modelId": "gemini-2.5-flash", "remainingFraction": 0.9, "tokenType": "input" },
            { "modelId": "gemini-2.5-flash", "remainingFraction": 0.4, "tokenType": "output" }
          ]
        }
        """);

        GeminiModelQuota quota = Assert.Single(GeminiUsageService.ParseModelQuotas(document.RootElement));

        Assert.Equal("gemini-2.5-flash", quota.ModelId);
        Assert.Equal(0.4, quota.RemainingFraction, 5);
    }

    [Fact]
    public void BuildGeminiGroups_PutsProFirstAndKeepsUnknownModels()
    {
        IReadOnlyList<ProviderQuotaGroup> groups = GeminiUsageService.BuildGeminiGroups(
        [
            new GeminiModelQuota("gemini-2.5-flash-lite", 0.8, null),
            new GeminiModelQuota("gemini-embedding", 0.7, null),
            new GeminiModelQuota("gemini-2.5-flash", 0.9, null),
            new GeminiModelQuota("gemini-2.5-pro", 0.6, null)
        ]);

        ProviderQuotaGroup group = Assert.Single(groups);
        Assert.Equal(["Pro", "Flash", "Flash Lite", "gemini-embedding"], group.Windows.Select(window => window.Label));
        Assert.Equal([60, 90, 80, 70], group.Windows.Select(window => window.RemainingPercent));
    }

    [Fact]
    public void ResolveAccountPlan_PrefersTheNamedPaidTier()
    {
        Assert.Equal(
            "Google One AI Pro",
            GeminiUsageService.ResolveAccountPlan(GeminiUserTier.Free, null, "Google One AI Pro"));
        Assert.Equal("Workspace", GeminiUsageService.ResolveAccountPlan(GeminiUserTier.Free, "example.com", null));
        Assert.Equal("Free", GeminiUsageService.ResolveAccountPlan(GeminiUserTier.Free, null, null));
        Assert.Equal("Legacy", GeminiUsageService.ResolveAccountPlan(GeminiUserTier.Legacy, null, null));
        Assert.Equal(string.Empty, GeminiUsageService.ResolveAccountPlan(null, null, null));
    }

    [Fact]
    public void ParseOAuthClient_ReadsTheGeminiCliConstants()
    {
        GeminiOAuthClient client = Assert.IsType<GeminiOAuthClient>(GeminiUsageService.ParseOAuthClient("""
        const OAUTH_CLIENT_ID = '1234-abc.apps.googleusercontent.com';
        const OAUTH_CLIENT_SECRET = 'GOCSPX-fake-secret';
        """));

        Assert.Equal("1234-abc.apps.googleusercontent.com", client.ClientId);
        Assert.Equal("GOCSPX-fake-secret", client.ClientSecret);
    }

    [Fact]
    public void TryReadOAuthClientFromPackageRoot_FindsTheDistributedAndBundledForms()
    {
        string root = CreateTempDirectory();
        try
        {
            string distributed = Path.Combine(root, "dist", "src", "code_assist");
            Directory.CreateDirectory(distributed);
            File.WriteAllText(
                Path.Combine(distributed, "oauth2.js"),
                """
                var OAUTH_CLIENT_ID = "dist-id.apps.googleusercontent.com";
                var OAUTH_CLIENT_SECRET = "dist-secret";
                """);

            GeminiOAuthClient fromDist = Assert.IsType<GeminiOAuthClient>(
                GeminiUsageService.TryReadOAuthClientFromPackageRoot(root));
            Assert.Equal("dist-id.apps.googleusercontent.com", fromDist.ClientId);

            string bundleRoot = CreateTempDirectory();
            try
            {
                Directory.CreateDirectory(Path.Combine(bundleRoot, "bundle"));
                File.WriteAllText(
                    Path.Combine(bundleRoot, "bundle", "gemini.js"),
                    """OAUTH_CLIENT_ID="bundle-id";OAUTH_CLIENT_SECRET="bundle-secret";""");

                GeminiOAuthClient fromBundle = Assert.IsType<GeminiOAuthClient>(
                    GeminiUsageService.TryReadOAuthClientFromPackageRoot(bundleRoot));
                Assert.Equal("bundle-id", fromBundle.ClientId);
                Assert.Equal("bundle-secret", fromBundle.ClientSecret);
            }
            finally
            {
                TryDeleteDirectory(bundleRoot);
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void ExtractClaims_ReadsEmailAndHostedDomainFromAnIdToken()
    {
        GeminiTokenClaims claims = GeminiUsageService.ExtractClaims(
            MakeIdToken("""{"email":"dev@example.com","hd":"example.com"}"""));

        Assert.Equal("dev@example.com", claims.Email);
        Assert.Equal("example.com", claims.HostedDomain);

        GeminiTokenClaims empty = GeminiUsageService.ExtractClaims("not-a-jwt");
        Assert.Null(empty.Email);
        Assert.Null(empty.HostedDomain);
    }

    [Theory]
    [InlineData("UNSUPPORTED_CLIENT", true)]
    [InlineData("IneligibleTierError: blocked", true)]
    [InlineData("Gemini Code Assist is no longer supported for this account", true)]
    [InlineData("SUBSCRIPTION_REQUIRED", false)]
    [InlineData(null, false)]
    public void IsConsumerTierDeprecationSignal_MatchesGooglesShutdownWording(string? text, bool expected)
    {
        Assert.Equal(expected, GeminiUsageService.IsConsumerTierDeprecationSignal(text));
    }

    // MARK: fixtures

    private static GeminiUsageService CreateService(
        string? antigravityBinary,
        HttpClient? httpClient = null,
        GeminiOAuthClient? oauthClient = null,
        Func<ProcessStartInfo, CancellationToken, Task<GeminiProcessResult>>? processRunner = null)
    {
        return new GeminiUsageService(
            httpClient ?? CreateHttpClient(request =>
                throw new InvalidOperationException($"Unexpected request {request.RequestUri}")),
            () => oauthClient,
            () => antigravityBinary,
            processRunner ?? ((_, _) => throw new InvalidOperationException("Unexpected process launch")));
    }

    private static Func<ProcessStartInfo, CancellationToken, Task<GeminiProcessResult>> AntigravityRunner(
        string version,
        int usageExitCode,
        string usageOutput)
    {
        return (startInfo, _) => Task.FromResult(startInfo.ArgumentList.Contains("--version")
            ? new GeminiProcessResult(0, version + Environment.NewLine)
            : new GeminiProcessResult(usageExitCode, usageOutput));
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> send)
    {
        return new HttpClient(new DelegateHandler(request => Task.FromResult(send(request))));
    }

    private static HttpClient CreateAsyncHttpClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync)
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

    private static void AssertBearerToken(HttpRequestMessage request, string expected)
    {
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal(expected, request.Headers.Authorization?.Parameter);
    }

    private static string MakeIdToken(string payloadJson)
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return $"header.{encoded}.signature";
    }

    private static void WriteCredentials(
        string home,
        string accessToken,
        DateTimeOffset expiresAt,
        string? refreshToken = null,
        string extraFields = "")
    {
        string refresh = refreshToken is null ? string.Empty : $""" "refresh_token": "{refreshToken}", """;
        File.WriteAllText(
            Path.Combine(home, "oauth_creds.json"),
            $$"""
            {
              "access_token": "{{accessToken}}",
              {{refresh}}
              "expiry_date": {{expiresAt.ToUnixTimeMilliseconds()}}{{extraFields}}
            }
            """);
    }

    private static JsonObject ReadCredentials(string home)
    {
        return Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(Path.Combine(home, "oauth_creds.json"))));
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "fluent-agentbar-gemini-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return sendAsync(request);
        }
    }
}
