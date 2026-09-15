using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace FluentAgentBar.Tests;

public sealed class ClaudeUsageServiceTests
{
    [Fact]
    public async Task FetchAsync_WhenRefreshSucceeds_PersistsNewCredentialsAndPreservesUnknownFields()
    {
        string configDir = CreateTempDirectory();
        try
        {
            string credentialsPath = Path.Combine(configDir, ".credentials.json");
            WriteCredentials(
                credentialsPath,
                "old-access",
                "old-refresh",
                DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
                """
                "scopes": ["openid", "user:profile"],
                "subscriptionType": "max",
                "nestedUnknown": { "value": 42 }
                """,
                """
                "topLevelUnknown": { "enabled": true },
                "anotherSibling": "keep"
                """);

            long refreshedExpiresAt = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds();
            using ClaudeUsageService service = new(CreateHttpClient(
                async request =>
                {
                    if (request.RequestUri?.AbsoluteUri == "https://platform.claude.com/v1/oauth/token")
                    {
                        string body = await request.Content!.ReadAsStringAsync();
                        Assert.Contains("\"refresh_token\":\"old-refresh\"", body);
                        Assert.Contains("\"scope\":\"openid user:profile\"", body);
                        return JsonResponse($$"""
                        {
                          "access_token": "new-access",
                          "refresh_token": "new-refresh",
                          "expires_at": {{refreshedExpiresAt}}
                        }
                        """);
                    }

                    Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri?.AbsoluteUri);
                    AssertBearerToken(request.Headers.Authorization, "new-access");
                    return JsonResponse(UsageJson("team"));
                }),
                () => null);

            ProfileUsage usage = await service.FetchAsync(configDir, CancellationToken.None);

            Assert.True(usage.IsAvailable);
            Assert.Equal("team", usage.Plan);

            JsonObject root = ReadJsonObject(credentialsPath);
            JsonObject oauth = Assert.IsType<JsonObject>(root["claudeAiOauth"]);
            Assert.Equal("new-access", oauth["accessToken"]!.GetValue<string>());
            Assert.Equal("new-refresh", oauth["refreshToken"]!.GetValue<string>());
            Assert.Equal(refreshedExpiresAt, oauth["expiresAt"]!.GetValue<long>());
            Assert.Equal("keep", root["anotherSibling"]!.GetValue<string>());
            Assert.True(root["topLevelUnknown"]!["enabled"]!.GetValue<bool>());
            Assert.Equal("max", oauth["subscriptionType"]!.GetValue<string>());
            Assert.Equal("openid", oauth["scopes"]![0]!.GetValue<string>());
            Assert.Equal(42, oauth["nestedUnknown"]!["value"]!.GetValue<int>());
        }
        finally
        {
            TryDeleteDirectory(configDir);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenRefreshTokenIsRejected_ShowsLoginRequired()
    {
        string configDir = CreateTempDirectory();
        try
        {
            string credentialsPath = Path.Combine(configDir, ".credentials.json");
            WriteCredentials(
                credentialsPath,
                "expired-access",
                "dead-refresh",
                DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
                """
                "subscriptionType": "max",
                "scopes": ["user:profile", "user:inference"]
                """);

            using ClaudeUsageService service = new(CreateHttpClient(
                async request =>
                {
                    Assert.Equal("https://platform.claude.com/v1/oauth/token", request.RequestUri?.AbsoluteUri);
                    string body = await request.Content!.ReadAsStringAsync();
                    Assert.Contains("\"scope\":\"user:profile user:inference\"", body);
                    return JsonResponse(
                        """{"error":"invalid_grant"}""",
                        HttpStatusCode.Unauthorized);
                }),
                () => null);

            ProfileUsage usage = await service.FetchAsync(configDir, CancellationToken.None);

            Assert.False(usage.IsAvailable);
            Assert.Equal("Login Required", usage.Plan);
        }
        finally
        {
            TryDeleteDirectory(configDir);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenRefreshTokenChangesBeforePersist_SkipsWriteAndUsesDiskCredentials()
    {
        string configDir = CreateTempDirectory();
        try
        {
            string credentialsPath = Path.Combine(configDir, ".credentials.json");
            WriteCredentials(
                credentialsPath,
                "old-access",
                "old-refresh",
                DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
                """
                "subscriptionType": "old-plan"
                """);

            long diskExpiresAt = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeMilliseconds();
            long refreshedExpiresAt = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds();
            using ClaudeUsageService service = new(CreateHttpClient(
                request =>
                {
                    if (request.RequestUri?.AbsoluteUri == "https://platform.claude.com/v1/oauth/token")
                    {
                        WriteCredentials(
                            credentialsPath,
                            "disk-access",
                            "disk-refresh",
                            diskExpiresAt,
                            """
                            "subscriptionType": "disk-plan",
                            "fromClaudeCode": true
                            """);
                        return Task.FromResult(JsonResponse($$"""
                        {
                          "access_token": "new-access",
                          "refresh_token": "new-refresh",
                          "expires_at": {{refreshedExpiresAt}}
                        }
                        """));
                    }

                    Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri?.AbsoluteUri);
                    AssertBearerToken(request.Headers.Authorization, "disk-access");
                    return Task.FromResult(JsonResponse(UsageJson(string.Empty)));
                }),
                () => null);

            ProfileUsage usage = await service.FetchAsync(configDir, CancellationToken.None);

            Assert.True(usage.IsAvailable);
            Assert.Equal("disk-plan", usage.Plan);

            JsonObject root = ReadJsonObject(credentialsPath);
            JsonObject oauth = Assert.IsType<JsonObject>(root["claudeAiOauth"]);
            Assert.Equal("disk-access", oauth["accessToken"]!.GetValue<string>());
            Assert.Equal("disk-refresh", oauth["refreshToken"]!.GetValue<string>());
            Assert.Equal(diskExpiresAt, oauth["expiresAt"]!.GetValue<long>());
            Assert.True(oauth["fromClaudeCode"]!.GetValue<bool>());
        }
        finally
        {
            TryDeleteDirectory(configDir);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenDiskCredentialsLackProfileScope_UsesEnvironmentToken()
    {
        string configDir = CreateTempDirectory();
        try
        {
            string credentialsPath = Path.Combine(configDir, ".credentials.json");
            WriteCredentials(
                credentialsPath,
                "old-access",
                "old-refresh",
                DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
                """
                "subscriptionType": "disk-plan",
                "scopes": ["user:inference"]
                """);

            int refreshCalls = 0;
            using ClaudeUsageService service = new(CreateHttpClient(
                request =>
                {
                    if (request.RequestUri?.AbsoluteUri == "https://platform.claude.com/v1/oauth/token")
                    {
                        refreshCalls++;
                        return Task.FromResult(JsonResponse("""
                        {
                          "access_token": "should-not-use"
                        }
                        """));
                    }

                    Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri?.AbsoluteUri);
                    AssertBearerToken(request.Headers.Authorization, "env-access");
                    return Task.FromResult(JsonResponse(UsageJson(string.Empty)));
                }),
                () => "env-access");

            ProfileUsage usage = await service.FetchAsync(configDir, CancellationToken.None);

            Assert.True(usage.IsAvailable);
            Assert.Equal("disk-plan", usage.Plan);
            Assert.Equal(0, refreshCalls);

            JsonObject root = ReadJsonObject(credentialsPath);
            JsonObject oauth = Assert.IsType<JsonObject>(root["claudeAiOauth"]);
            Assert.Equal("old-access", oauth["accessToken"]!.GetValue<string>());
            Assert.Equal("old-refresh", oauth["refreshToken"]!.GetValue<string>());
        }
        finally
        {
            TryDeleteDirectory(configDir);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenEnvironmentTokenExistsWithoutCredentialsFile_UsesIt()
    {
        string configDir = CreateTempDirectory();
        try
        {
            using ClaudeUsageService service = new(CreateHttpClient(
                request =>
                {
                    Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri?.AbsoluteUri);
                    AssertBearerToken(request.Headers.Authorization, "env-access");
                    return Task.FromResult(JsonResponse(UsageJson("env-plan")));
                }),
                () => " env-access ");

            ProfileUsage usage = await service.FetchAsync(configDir, CancellationToken.None);

            Assert.True(usage.IsAvailable);
            Assert.Equal("env-plan", usage.Plan);
        }
        finally
        {
            TryDeleteDirectory(configDir);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenEnvironmentTokenChanges_BypassesCachedUsage()
    {
        string configDir = CreateTempDirectory();
        try
        {
            string environmentToken = "env-first";
            List<string> seenTokens = [];
            using ClaudeUsageService service = new(CreateHttpClient(
                request =>
                {
                    Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri?.AbsoluteUri);
                    string accessToken = request.Headers.Authorization?.Parameter ?? string.Empty;
                    seenTokens.Add(accessToken);
                    string plan = seenTokens.Count == 1 ? "first-plan" : "second-plan";
                    return Task.FromResult(JsonResponse(UsageJson(plan)));
                }),
                () => environmentToken);

            ProfileUsage first = await service.FetchAsync(configDir, CancellationToken.None);
            environmentToken = "env-second";
            ProfileUsage second = await service.FetchAsync(configDir, CancellationToken.None);

            Assert.Equal("first-plan", first.Plan);
            Assert.Equal("second-plan", second.Plan);
            Assert.Equal(["env-first", "env-second"], seenTokens);
        }
        finally
        {
            TryDeleteDirectory(configDir);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenEnvironmentTokenLacksProfileScope_ShowsFullLoginRequired()
    {
        string configDir = CreateTempDirectory();
        try
        {
            using ClaudeUsageService service = new(CreateHttpClient(
                request =>
                {
                    AssertBearerToken(request.Headers.Authorization, "env-access");
                    return Task.FromResult(JsonResponse(
                        """
                        {"type":"error","error":{"type":"permission_error","message":"OAuth token does not meet scope requirement user:profile"}}
                        """,
                        HttpStatusCode.Forbidden));
                }),
                () => "env-access");

            ProfileUsage usage = await service.FetchAsync(configDir, CancellationToken.None);

            Assert.False(usage.IsAvailable);
            Assert.Equal("Full Login Required", usage.Plan);
        }
        finally
        {
            TryDeleteDirectory(configDir);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenDiskCredentialsHaveProfileScope_IgnoresEnvironmentToken()
    {
        string configDir = CreateTempDirectory();
        try
        {
            string credentialsPath = Path.Combine(configDir, ".credentials.json");
            WriteCredentials(
                credentialsPath,
                "disk-access",
                "disk-refresh",
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
                """
                "subscriptionType": "max",
                "scopes": ["user:profile", "user:inference"]
                """);

            List<string> seenTokens = [];
            using ClaudeUsageService service = new(CreateHttpClient(
                request =>
                {
                    string accessToken = request.Headers.Authorization?.Parameter ?? string.Empty;
                    seenTokens.Add(accessToken);
                    Assert.Equal("disk-access", accessToken);
                    return Task.FromResult(JsonResponse(UsageJson("team")));
                }),
                () => "env-access");

            ProfileUsage usage = await service.FetchAsync(configDir, CancellationToken.None);

            Assert.True(usage.IsAvailable);
            Assert.Equal("team", usage.Plan);
            Assert.Equal(["disk-access"], seenTokens);
        }
        finally
        {
            TryDeleteDirectory(configDir);
        }
    }

    [Theory]
    [InlineData(0.0, 100)]
    [InlineData(0.25, 100)]
    [InlineData(1.0, 99)]
    [InlineData(1.01, 99)]
    [InlineData(100.0, 0)]
    public async Task FetchAsync_InterpretsOAuthUtilizationAsPercentagePoints(
        double utilization,
        int expectedRemainingPercent)
    {
        string configDir = CreateTempDirectory();
        try
        {
            string responseJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                five_hour = new { utilization },
                seven_day = new { utilization }
            });
            using ClaudeUsageService service = new(CreateHttpClient(
                _ => Task.FromResult(JsonResponse(responseJson))),
                () => "env-access");

            ProfileUsage usage = await service.FetchAsync(configDir, CancellationToken.None);

            Assert.True(usage.IsAvailable);
            Assert.Equal(expectedRemainingPercent, usage.RemainingPercent);
            Assert.Equal(expectedRemainingPercent, usage.WeeklyPercent);
        }
        finally
        {
            TryDeleteDirectory(configDir);
        }
    }

    [Fact]
    public async Task FetchAsync_WhenScopedWeeklyLimitIsPresent_AddsModelQuotaGroup()
    {
        ProfileUsage usage = await FetchUsageAsync("""
        {
          "five_hour": { "utilization": 22.0, "resets_at": "2026-09-15T13:10:00.942676+00:00" },
          "seven_day": { "utilization": 23.0, "resets_at": "2026-09-19T15:00:00.942701+00:00" },
          "seven_day_opus": null,
          "seven_day_sonnet": null,
          "limits": [
            {
              "kind": "session", "group": "session", "percent": 22,
              "resets_at": "2026-09-15T13:10:00.942676+00:00", "scope": null, "is_active": false
            },
            {
              "kind": "weekly_all", "group": "weekly", "percent": 23,
              "resets_at": "2026-09-19T15:00:00.942701+00:00", "scope": null, "is_active": false
            },
            {
              "kind": "weekly_scoped", "group": "weekly", "percent": 43,
              "resets_at": "2026-09-19T15:00:00.942986+00:00",
              "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null },
              "is_active": true
            }
          ]
        }
        """);

        Assert.True(usage.IsAvailable);
        Assert.Equal(78, usage.RemainingPercent);
        Assert.Equal(77, usage.WeeklyPercent);

        IReadOnlyList<QuotaGroupUsage> groups = usage.DisplayQuotaGroups;
        Assert.Equal(2, groups.Count);

        Assert.Equal(string.Empty, groups[0].Name);
        Assert.Equal(["5h", "Weekly"], groups[0].Windows.Select(window => window.Label));
        Assert.Equal(78, groups[0].Windows[0].RemainingPercent);
        Assert.Equal(77, groups[0].Windows[1].RemainingPercent);

        QuotaGroupUsage fable = groups[1];
        Assert.Equal("Fable", fable.Name);
        QuotaWindowUsage fableWindow = Assert.Single(fable.Windows);
        Assert.Equal("Weekly", fableWindow.Label);
        Assert.Equal(57, fableWindow.RemainingPercent);
        Assert.True(fableWindow.IsAvailable);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-19T15:00:00.942986+00:00", CultureInfo.InvariantCulture),
            fableWindow.ResetAt);
        Assert.Equal(MockUsageData.ClaudeAccentColor, fableWindow.AccentColor);
    }

    [Fact]
    public async Task FetchAsync_WhenScopedWeeklyLimitTargetsAllModels_AddsNoExtraGroup()
    {
        ProfileUsage usage = await FetchUsageAsync("""
        {
          "five_hour": { "utilization": 10 },
          "seven_day": { "utilization": 20 },
          "limits": [
            {
              "kind": "weekly_scoped", "group": "weekly", "percent": 20,
              "resets_at": "2026-09-19T15:00:00+00:00",
              "scope": { "model": { "id": "claude-all-models", "display_name": "All models" } },
              "is_active": false
            }
          ]
        }
        """);

        QuotaGroupUsage group = Assert.Single(usage.DisplayQuotaGroups);
        Assert.Equal(string.Empty, group.Name);
    }

    [Fact]
    public async Task FetchAsync_WhenScopedWeeklyLimitsRepeatAModel_KeepsASingleGroup()
    {
        ProfileUsage usage = await FetchUsageAsync("""
        {
          "five_hour": { "utilization": 10 },
          "seven_day": { "utilization": 20 },
          "seven_day_opus": { "utilization": 90, "resets_at": "2026-09-19T15:00:00+00:00" },
          "limits": [
            {
              "kind": "WEEKLY_SCOPED", "group": "Weekly", "percent": 12,
              "resets_at": "2026-09-19T15:00:00+00:00",
              "scope": { "model": { "id": "claude-opus-4", "display_name": "Opus" } }
            },
            {
              "kind": "weekly_scoped", "group": "weekly", "percent": 44,
              "resets_at": "2026-09-19T15:00:00+00:00",
              "scope": { "model": { "id": "claude-opus-4", "display_name": "Opus" } }
            },
            {
              "kind": "weekly_scoped", "group": "weekly",
              "resets_at": "2026-09-19T15:00:00+00:00",
              "scope": { "model": { "id": "claude-fable", "display_name": "Fable" } }
            }
          ]
        }
        """);

        IReadOnlyList<QuotaGroupUsage> groups = usage.DisplayQuotaGroups;
        Assert.Equal(["", "Opus"], groups.Select(group => group.Name));
        Assert.Equal(88, groups[1].Windows[0].RemainingPercent);
    }

    [Fact]
    public async Task FetchAsync_WhenLegacySevenDayModelWindowsArePresent_AddsModelQuotaGroups()
    {
        ProfileUsage usage = await FetchUsageAsync("""
        {
          "five_hour": { "utilization": 11 },
          "seven_day": { "utilization": 9 },
          "seven_day_opus": { "utilization": 35, "resets_at": "2026-09-19T15:00:00+00:00" },
          "seven_day_sonnet": { "utilization": 5 }
        }
        """);

        IReadOnlyList<QuotaGroupUsage> groups = usage.DisplayQuotaGroups;
        Assert.Equal(["", "Opus", "Sonnet"], groups.Select(group => group.Name));
        Assert.Equal(65, groups[1].Windows[0].RemainingPercent);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-19T15:00:00+00:00", CultureInfo.InvariantCulture),
            groups[1].Windows[0].ResetAt);
        Assert.Equal(95, groups[2].Windows[0].RemainingPercent);
        Assert.Null(groups[2].Windows[0].ResetAt);
    }

    [Fact]
    public async Task FetchAsync_WithoutScopedWeeklyLimits_KeepsASingleQuotaGroup()
    {
        ProfileUsage usage = await FetchUsageAsync("""
        {
          "five_hour": { "utilization": 11, "resets_at": "2026-09-15T13:10:00+00:00" },
          "seven_day": { "utilization": 9, "resets_at": "2026-09-19T15:00:00+00:00" }
        }
        """);

        QuotaGroupUsage group = Assert.Single(usage.DisplayQuotaGroups);
        Assert.Equal(string.Empty, group.Name);
        Assert.Equal(["5h", "Weekly"], group.Windows.Select(window => window.Label));
        Assert.Equal(89, group.Windows[0].RemainingPercent);
        Assert.Equal(91, group.Windows[1].RemainingPercent);
    }

    private static async Task<ProfileUsage> FetchUsageAsync(string responseJson)
    {
        string configDir = CreateTempDirectory();
        try
        {
            using ClaudeUsageService service = new(
                CreateHttpClient(_ => Task.FromResult(JsonResponse(responseJson))),
                () => "env-access");
            return await service.FetchAsync(configDir, CancellationToken.None);
        }
        finally
        {
            TryDeleteDirectory(configDir);
        }
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

    private static string UsageJson(string plan)
    {
        return $$"""
        {
          "five_hour": { "utilization": 0.25 },
          "seven_day": { "utilization": 0.5 },
          "plan": "{{plan}}"
        }
        """;
    }

    private static void AssertBearerToken(AuthenticationHeaderValue? authorization, string accessToken)
    {
        Assert.NotNull(authorization);
        Assert.Equal("Bearer", authorization!.Scheme);
        Assert.Equal(accessToken, authorization.Parameter);
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "FluentAgentBar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteCredentials(
        string credentialsPath,
        string accessToken,
        string refreshToken,
        long expiresAt,
        string oauthFields,
        string siblingFields = "")
    {
        string siblingPrefix = string.IsNullOrWhiteSpace(siblingFields)
            ? string.Empty
            : siblingFields + ",";
        File.WriteAllText(
            credentialsPath,
            $$"""
            {
              {{siblingPrefix}}
              "claudeAiOauth": {
                "accessToken": "{{accessToken}}",
                "refreshToken": "{{refreshToken}}",
                "expiresAt": {{expiresAt}},
                {{oauthFields}}
              }
            }
            """);
    }

    private static JsonObject ReadJsonObject(string path)
    {
        return Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path)));
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch
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
