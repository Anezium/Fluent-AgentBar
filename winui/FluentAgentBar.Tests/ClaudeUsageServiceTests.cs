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
