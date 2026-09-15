using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FluentAgentBar.Tests;

public sealed class CursorUsageServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FetchAsync_MapsPlanUsageBasedSpendAndGrokBot()
    {
        var recorder = new RequestRecorder();
        using CursorUsageService service = CreateService(recorder.Handle);

        ProviderUsageSnapshot snapshot = await service.FetchAsync("C:\\cursor-home", CancellationToken.None);

        Assert.Equal("Pro", snapshot.Plan);
        Assert.Equal("dev@example.test", snapshot.Email);
        Assert.Equal(2, snapshot.Groups.Count);

        ProviderQuotaGroup plan = snapshot.Groups[0];
        Assert.Equal(string.Empty, plan.Name);
        Assert.Equal(2, plan.Windows.Count);
        Assert.Equal("Plan", plan.Windows[0].Label);
        // 40% used -> 60% left.
        Assert.Equal(60, plan.Windows[0].RemainingPercent);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), plan.Windows[0].ResetAt);
        Assert.Equal("Usage-based", plan.Windows[1].Label);
        // $12.50 of a $50.00 budget -> 75% left.
        Assert.Equal(75, plan.Windows[1].RemainingPercent);

        ProviderQuotaGroup grokBot = snapshot.Groups[1];
        Assert.Equal("Grok Bot", grokBot.Name);
        ProviderQuotaWindow grokBotWindow = Assert.Single(grokBot.Windows);
        Assert.Equal(25, grokBotWindow.RemainingPercent);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 9, 12, 32, 776, TimeSpan.Zero), grokBotWindow.ResetAt);

        // Every request must carry the desktop session cookie.
        Assert.All(recorder.Cookies, cookie => Assert.StartsWith("WorkosCursorSessionToken=user_01ABC%3A%3A", cookie));
        Assert.Contains("/api/usage-summary", recorder.Paths);
        Assert.Contains("/api/auth/me", recorder.Paths);
        Assert.Contains("/api/dashboard/get-sand-usage-status", recorder.Paths);
    }

    [Fact]
    public async Task FetchAsync_WhenSandUsageFails_StillReturnsPlanUsage()
    {
        using CursorUsageService service = CreateService(request =>
            request.RequestUri!.AbsolutePath == "/api/dashboard/get-sand-usage-status"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : DefaultResponse(request));

        ProviderUsageSnapshot snapshot = await service.FetchAsync("C:\\cursor-home", CancellationToken.None);

        ProviderQuotaGroup group = Assert.Single(snapshot.Groups);
        Assert.Equal(string.Empty, group.Name);
        Assert.Equal(60, group.Windows[0].RemainingPercent);
    }

    [Fact]
    public async Task FetchAsync_WhenAccountLookupFails_FallsBackToTokenEmail()
    {
        using CursorUsageService service = CreateService(request =>
            request.RequestUri!.AbsolutePath == "/api/auth/me"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : DefaultResponse(request));

        ProviderUsageSnapshot snapshot = await service.FetchAsync("C:\\cursor-home", CancellationToken.None);

        Assert.Equal("token@example.test", snapshot.Email);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FetchAsync_WhenApiRejectsSession_ThrowsLoginRequired(HttpStatusCode statusCode)
    {
        using CursorUsageService service = CreateService(_ => new HttpResponseMessage(statusCode));

        await Assert.ThrowsAsync<ProviderLoginRequiredException>(
            () => service.FetchAsync("C:\\cursor-home", CancellationToken.None));
    }

    [Fact]
    public async Task FetchAsync_WithoutLocalSession_ThrowsLoginRequiredWithoutCallingApi()
    {
        var recorder = new RequestRecorder();
        using CursorUsageService service = new(new DelegateHandler(recorder.Handle), _ => (CursorSession?)null, TestBaseAddress);

        await Assert.ThrowsAsync<ProviderLoginRequiredException>(
            () => service.FetchAsync("C:\\cursor-home", CancellationToken.None));
        Assert.Empty(recorder.Paths);
    }

    [Fact]
    public async Task FetchAsync_WithExpiredToken_ThrowsLoginRequired()
    {
        CursorSession expired = TestSession() with { ExpiresAt = Now.AddYears(-1) };
        using CursorUsageService service = new(new DelegateHandler(DefaultResponse), _ => expired, TestBaseAddress);

        await Assert.ThrowsAsync<ProviderLoginRequiredException>(
            () => service.FetchAsync("C:\\cursor-home", CancellationToken.None));
    }

    [Fact]
    public async Task FetchAsync_ServesTheCachedSnapshotWithinTheCacheWindow()
    {
        var recorder = new RequestRecorder();
        using CursorUsageService service = CreateService(recorder.Handle);

        ProviderUsageSnapshot first = await service.FetchAsync("C:\\cursor-home", CancellationToken.None);
        ProviderUsageSnapshot second = await service.FetchAsync("C:\\cursor-home", CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(4, recorder.Paths.Count);
    }

    [Fact]
    public async Task FetchAsync_WhenARefreshFails_KeepsServingTheLastGoodSnapshot()
    {
        bool fail = false;
        using CursorUsageService service = CreateService(
            request => fail ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : DefaultResponse(request),
            TimeSpan.Zero);

        ProviderUsageSnapshot fresh = await service.FetchAsync("C:\\cursor-home", CancellationToken.None);

        fail = true;
        ProviderUsageSnapshot stale = await service.FetchAsync("C:\\cursor-home", CancellationToken.None);

        Assert.Same(fresh, stale);
    }

    [Fact]
    public async Task FetchAsync_WhenTheApiFailsWithNothingCached_SurfacesTheFailureThenBacksOff()
    {
        var recorder = new RequestRecorder();
        using CursorUsageService service = CreateService(request =>
        {
            recorder.Handle(request);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.FetchAsync("C:\\cursor-home", CancellationToken.None));
        int afterFirstAttempt = recorder.Paths.Count;

        // Nothing is cached yet, so the caller still sees the failure, but the
        // backoff window must not turn a retry into a second burst of calls.
        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.FetchAsync("C:\\cursor-home", CancellationToken.None));
        Assert.Equal(afterFirstAttempt * 2, recorder.Paths.Count);
    }

    [Fact]
    public async Task FetchAsync_KeepsCachesSeparatePerHome()
    {
        var recorder = new RequestRecorder();
        using CursorUsageService service = CreateService(recorder.Handle);

        await service.FetchAsync("C:\\cursor-home-a", CancellationToken.None);
        await service.FetchAsync("C:\\cursor-home-b", CancellationToken.None);

        Assert.Equal(8, recorder.Paths.Count);
    }

    // MARK: - Snapshot shaping

    [Fact]
    public void BuildSnapshot_UsesEnterprisePersonalCapWhenPlanBlockIsAbsent()
    {
        // $73.84 of a $100.00 personal cap -> 26% left (CodexBar regression fixture).
        CursorUsageSummary summary = CursorUsageSummary.Parse("""
        {
            "billingCycleStart": "2026-04-01T00:00:00.000Z",
            "billingCycleEnd": "2026-05-01T00:00:00.000Z",
            "membershipType": "enterprise",
            "individualUsage": { "overall": { "enabled": true, "used": 7384, "limit": 10000 } },
            "teamUsage": {
                "onDemand": { "enabled": true, "used": 0, "limit": null },
                "pooled": { "enabled": true, "used": 12725135, "limit": 28122000 }
            }
        }
        """);

        Assert.Equal(73.84, summary.PlanPercentUsed(), 4);

        ProviderUsageSnapshot snapshot = CursorUsageService.BuildSnapshot(summary, null, null, null, null, Now);
        Assert.Equal("Enterprise", snapshot.Plan);
        ProviderQuotaGroup group = Assert.Single(snapshot.Groups);
        // No usage-based window: the team on-demand budget has no limit.
        ProviderQuotaWindow window = Assert.Single(group.Windows);
        Assert.Equal(26, window.RemainingPercent);
    }

    [Fact]
    public void PlanPercentUsed_FallsBackToTheSharedPoolWhenNoIndividualDataExists()
    {
        CursorUsageSummary summary = CursorUsageSummary.Parse("""
        {
            "membershipType": "enterprise",
            "teamUsage": { "pooled": { "enabled": true, "used": 12725135, "limit": 28122000 } }
        }
        """);

        Assert.InRange(summary.PlanPercentUsed(), 45.0, 45.5);
    }

    [Fact]
    public void PlanPercentUsed_PrefersTheExplicitTotalOverEveryRatio()
    {
        CursorUsageSummary summary = CursorUsageSummary.Parse("""
        {
            "membershipType": "pro",
            "individualUsage": {
                "plan": { "used": 1500, "limit": 5000, "totalPercentUsed": 30.0 },
                "overall": { "used": 7384, "limit": 10000 }
            },
            "teamUsage": { "pooled": { "used": 12725135, "limit": 28122000 } }
        }
        """);

        Assert.Equal(30.0, summary.PlanPercentUsed(), 4);
    }

    [Fact]
    public void PlanPercentUsed_AveragesTheAutoAndApiLanesWhenNoTotalIsReported()
    {
        CursorUsageSummary summary = CursorUsageSummary.Parse("""
        {
            "individualUsage": { "plan": { "autoPercentUsed": 20.0, "apiPercentUsed": 60.0 } }
        }
        """);

        Assert.Equal(40.0, summary.PlanPercentUsed(), 4);
    }

    [Fact]
    public void PlanPercentUsed_TreatsFractionalPercentsAsPercentagePoints()
    {
        // 0.36 means 0.36% used, not 36%: the window must still read 100% left.
        CursorUsageSummary summary = CursorUsageSummary.Parse("""
        { "individualUsage": { "plan": { "totalPercentUsed": 0.36 } } }
        """);

        Assert.Equal(0.36, summary.PlanPercentUsed(), 4);
        Assert.Equal(100, CursorUsageService.RemainingPercent(summary.PlanPercentUsed()));
    }

    [Fact]
    public void ResolveUsageBasedSpend_PrefersAPersonalBudgetOverTheTeamPool()
    {
        CursorUsageSummary summary = CursorUsageSummary.Parse("""
        {
            "individualUsage": { "onDemand": { "enabled": true, "used": 4471, "limit": 10000 } },
            "teamUsage": { "onDemand": { "enabled": true, "used": 1311125, "limit": 2000000 } }
        }
        """);

        (double used, double? limit) = summary.ResolveUsageBasedSpend();
        Assert.Equal(44.71, used, 4);
        Assert.Equal(100.0, limit);
    }

    [Fact]
    public void ResolveUsageBasedSpend_FallsBackToTheTeamPoolWithoutAPersonalLimit()
    {
        CursorUsageSummary summary = CursorUsageSummary.Parse("""
        {
            "individualUsage": { "onDemand": { "enabled": true, "used": 4471, "limit": null } },
            "teamUsage": { "onDemand": { "enabled": true, "used": 1311125, "limit": 2000000 } }
        }
        """);

        (double used, double? limit) = summary.ResolveUsageBasedSpend();
        Assert.Equal(13111.25, used, 4);
        Assert.Equal(20000.0, limit);
    }

    [Fact]
    public void BuildSnapshot_HidesTheUsageBasedWindowWithoutASpendLimit()
    {
        CursorUsageSummary summary = CursorUsageSummary.Parse("""
        {
            "membershipType": "ultra",
            "individualUsage": {
                "plan": { "used": 1499, "limit": 40000, "totalPercentUsed": 0.6 },
                "onDemand": { "enabled": true, "used": 0, "limit": null }
            }
        }
        """);

        ProviderUsageSnapshot snapshot = CursorUsageService.BuildSnapshot(summary, null, null, null, null, Now);

        Assert.Equal("Ultra", snapshot.Plan);
        Assert.Single(Assert.Single(snapshot.Groups).Windows);
    }

    [Fact]
    public void BuildSnapshot_LegacyRequestPlanDrivesThePlanWindowAndHidesGrokBot()
    {
        CursorUsageSummary summary = CursorUsageSummary.Parse("""
        { "membershipType": "pro", "individualUsage": { "plan": { "totalPercentUsed": 90.0 } } }
        """);
        CursorRequestUsage? requests = CursorRequestUsage.Parse("""
        { "gpt-4": { "numRequestsTotal": 100, "maxRequestUsage": 500 }, "startOfMonth": "2026-09-01T00:00:00.000Z" }
        """);
        CursorSandUsage sand = CursorSandUsage.Parse("""
        { "usagePercent": 75, "includedLimitZero": false, "nextResetTimestampUtc": "2026-09-21T09:12:32.776Z" }
        """);

        ProviderUsageSnapshot snapshot = CursorUsageService.BuildSnapshot(summary, null, sand, requests, null, Now);

        // 100/500 requests -> 80% left, and the Grok Bot group is suppressed.
        ProviderQuotaGroup group = Assert.Single(snapshot.Groups);
        Assert.Equal(80, group.Windows[0].RemainingPercent);
    }

    [Fact]
    public void RequestUsage_IsIgnoredOnTokenBasedPlans()
    {
        Assert.Null(CursorRequestUsage.Parse("""{ "gpt-4": { "numRequests": 12, "maxRequestUsage": null } }"""));
        Assert.Null(CursorRequestUsage.Parse("""{ "startOfMonth": "2026-09-01T00:00:00.000Z" }"""));
    }

    [Fact]
    public void BuildSnapshot_FallsBackToTheLocallyCachedMembershipType()
    {
        CursorUsageSummary summary = CursorUsageSummary.Parse("{}");

        ProviderUsageSnapshot snapshot = CursorUsageService.BuildSnapshot(summary, null, null, null, "pro_plus", Now);

        Assert.Equal("Pro+", snapshot.Plan);
        Assert.Equal(100, Assert.Single(Assert.Single(snapshot.Groups).Windows).RemainingPercent);
    }

    [Theory]
    [InlineData("pro", "Pro")]
    [InlineData("pro_student", "Pro")]
    [InlineData("pro_plus", "Pro+")]
    [InlineData("ultra", "Ultra")]
    [InlineData("business", "Business")]
    [InlineData("enterprise", "Enterprise")]
    [InlineData("team", "Team")]
    [InlineData("free", "Free")]
    [InlineData("free_trial", "Pro Trial")]
    [InlineData("hobby", "Hobby")]
    [InlineData("express", "Start")]
    [InlineData("something_new", "something_new")]
    [InlineData(null, "")]
    public void FormatMembershipType_MatchesTheCursorDashboardNames(string? raw, string expected)
    {
        Assert.Equal(expected, CursorUsageService.FormatMembershipType(raw));
    }

    // MARK: - Grok Bot window rules

    [Fact]
    public void SandUsage_ExposesAWeeklyResetForAnIncludedAllowance()
    {
        CursorSandUsage sand = CursorSandUsage.Parse("""
        {
            "currentPeriodStart": "2026-08-17T07:57:50.647Z",
            "nextResetTimestampUtc": "2026-08-24T07:57:50.647Z",
            "usagePercent": 100,
            "hasAvailableUsage": true,
            "includedLimitZero": false
        }
        """);

        ProviderQuotaWindow window = NotNull(sand.ToWindow(Now));
        Assert.Equal(0, window.RemainingPercent);
        Assert.NotNull(window.ResetAt);
    }

    [Fact]
    public void SandUsage_IsHiddenWithoutAnIncludedLimitOrTrial()
    {
        CursorSandUsage sand = CursorSandUsage.Parse("""
        { "usagePercent": 100, "hasAvailableUsage": false, "includedLimitZero": true }
        """);

        Assert.Null(sand.ToWindow(Now));
    }

    [Fact]
    public void SandUsage_UnexpiredTrialIsShownWithoutARecurringReset()
    {
        // The trial expiry is not a quota reset, so ResetAt must stay null.
        CursorSandUsage sand = CursorSandUsage.Parse("""
        {
            "currentPeriodStart": "2026-09-14T09:12:32.776Z",
            "nextResetTimestampUtc": "2026-09-21T09:12:32.776Z",
            "usagePercent": 12.3,
            "includedLimitZero": true,
            "sandTrialExpiresAt": "2026-09-21T09:12:32.776Z"
        }
        """);

        ProviderQuotaWindow window = NotNull(sand.ToWindow(Now));
        Assert.Equal(88, window.RemainingPercent);
        Assert.Null(window.ResetAt);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"not-a-date\"")]
    [InlineData("\"2026-09-14T00:00:00Z\"")]
    public void SandUsage_MissingMalformedAndExpiredTrialsAreHidden(string expiry)
    {
        CursorSandUsage sand = CursorSandUsage.Parse($$"""
        { "usagePercent": 12.3, "includedLimitZero": true, "sandTrialExpiresAt": {{expiry}} }
        """);

        Assert.Null(sand.ToWindow(Now));
    }

    [Fact]
    public void SandUsage_ReadsTheLegacyIncludedLimitFlagAndLetsTheCurrentOneWin()
    {
        Assert.NotNull(CursorSandUsage.Parse("""{"hasNonZeroIncludedLimit":true,"usagePercent":42}""").ToWindow(Now));
        Assert.Null(CursorSandUsage
            .Parse("""{"hasNonZeroIncludedLimit":true,"includedLimitZero":true,"usagePercent":42}""")
            .ToWindow(Now));
    }

    [Fact]
    public void SandUsage_WithoutAPercentageStaysHidden()
    {
        Assert.Null(CursorSandUsage.Parse("""{ "includedLimitZero": false }""").ToWindow(Now));
    }

    // MARK: - Local session store

    [Theory]
    [InlineData(BlobEncoding.Text)]
    [InlineData(BlobEncoding.Utf8)]
    [InlineData(BlobEncoding.Utf16LittleEndian)]
    public void LoadSessionFromDatabase_ReadsTheDesktopTokenInEveryStoredEncoding(BlobEncoding encoding)
    {
        string directory = CreateTempDirectory();
        try
        {
            string token = Jwt("auth0|user_01ABC", "db@example.test", Now.AddDays(30));
            string databasePath = Path.Combine(directory, "state.vscdb");
            WriteItemTable(databasePath, new Dictionary<string, object>
            {
                ["cursorAuth/accessToken"] = Encode(token, encoding),
                ["cursorAuth/cachedEmail"] = Encode("cached@example.test", encoding),
                ["cursorAuth/stripeMembershipType"] = Encode("pro_plus", encoding)
            });

            CursorSession session = NotNull(CursorUsageService.LoadSessionFromDatabase(databasePath));

            Assert.Equal(token, session.AccessToken);
            Assert.Equal("user_01ABC", session.UserId);
            Assert.Equal("db@example.test", session.Email);
            Assert.Equal("pro_plus", session.MembershipType);
            Assert.NotNull(session.ExpiresAt);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void LoadSessionFromAuthFile_ReadsTheCursorAgentCliToken()
    {
        string directory = CreateTempDirectory();
        try
        {
            string token = Jwt("google-oauth2|123456", "cli@example.test", Now.AddDays(45));
            string authFilePath = Path.Combine(directory, "auth.json");
            File.WriteAllText(authFilePath, $"{{\"accessToken\":\"{token}\",\"refreshToken\":\"{token}\"}}");

            CursorSession session = NotNull(CursorUsageService.LoadSessionFromAuthFile(authFilePath));

            Assert.Equal(token, session.AccessToken);
            Assert.Equal("123456", session.UserId);
            Assert.Equal("cli@example.test", session.Email);
            Assert.Null(session.MembershipType);
            Assert.NotNull(session.ExpiresAt);

            Assert.Null(CursorUsageService.LoadSessionFromAuthFile(Path.Combine(directory, "missing.json")));
            File.WriteAllText(authFilePath, "{\"accessToken\":\"not-a-jwt\"}");
            Assert.Null(CursorUsageService.LoadSessionFromAuthFile(authFilePath));
            File.WriteAllText(authFilePath, "not json");
            Assert.Null(CursorUsageService.LoadSessionFromAuthFile(authFilePath));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void PickFreshestSession_PrefersTheUnexpiredTokenThatLivesLongest()
    {
        CursorSession expiredDesktop = new(Jwt("auth0|user_01ABC", null, Now.AddDays(-30)), "user_01ABC", null, "free_trial", Now.AddDays(-30));
        CursorSession cli = new(Jwt("google-oauth2|1", null, Now.AddDays(45)), "1", null, null, Now.AddDays(45));
        CursorSession desktop = new(Jwt("auth0|user_02", null, Now.AddDays(10)), "user_02", null, "pro", Now.AddDays(10));

        Assert.Same(cli, CursorUsageService.PickFreshestSession(expiredDesktop, cli));
        Assert.Same(cli, CursorUsageService.PickFreshestSession(desktop, cli));
        Assert.Same(desktop, CursorUsageService.PickFreshestSession(null, desktop));
        // Only expired candidates: still return one so the caller reports "expired" rather than "missing".
        Assert.Same(expiredDesktop, CursorUsageService.PickFreshestSession(expiredDesktop, null));
        Assert.Null(CursorUsageService.PickFreshestSession(null, null));
    }

    [Fact]
    public void LoadSessionFromDisk_FallsBackToTheCliTokenWhenTheDesktopTokenIsExpired()
    {
        string home = CreateTempDirectory();
        try
        {
            string globalStorage = Path.Combine(home, "User", "globalStorage");
            Directory.CreateDirectory(globalStorage);
            WriteItemTable(Path.Combine(globalStorage, "state.vscdb"), new Dictionary<string, object>
            {
                ["cursorAuth/accessToken"] = Jwt("auth0|user_01ABC", null, Now.AddDays(-1))
            });
            string cliToken = Jwt("google-oauth2|777", "cli@example.test", Now.AddDays(45));
            File.WriteAllText(Path.Combine(home, "auth.json"), $"{{\"accessToken\":\"{cliToken}\"}}");

            CursorSession session = NotNull(CursorUsageService.LoadSessionFromDisk(home));

            Assert.Equal(cliToken, session.AccessToken);
            Assert.Equal("777", session.UserId);
        }
        finally
        {
            TryDeleteDirectory(home);
        }
    }

    [Fact]
    public void LoadSessionFromDatabase_ReturnsNullWhenTheTokenIsMissingOrNotAJwt()
    {
        string directory = CreateTempDirectory();
        try
        {
            string missing = Path.Combine(directory, "missing.vscdb");
            Assert.Null(CursorUsageService.LoadSessionFromDatabase(missing));

            string empty = Path.Combine(directory, "empty.vscdb");
            WriteItemTable(empty, new Dictionary<string, object>());
            Assert.Null(CursorUsageService.LoadSessionFromDatabase(empty));

            string garbage = Path.Combine(directory, "garbage.vscdb");
            WriteItemTable(garbage, new Dictionary<string, object> { ["cursorAuth/accessToken"] = "not-a-jwt" });
            Assert.Null(CursorUsageService.LoadSessionFromDatabase(garbage));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void LoadSessionFromDatabase_LeavesTheOriginalDatabaseUntouched()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "state.vscdb");
            WriteItemTable(databasePath, new Dictionary<string, object>
            {
                ["cursorAuth/accessToken"] = Jwt("auth0|user_01ABC", null, Now.AddDays(30))
            });
            byte[] before = File.ReadAllBytes(databasePath);

            Assert.NotNull(CursorUsageService.LoadSessionFromDatabase(databasePath));

            Assert.Equal(before, File.ReadAllBytes(databasePath));
            Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "fluent-agentbar-cursor-*"));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void BuildCookieHeader_EncodesTheDoubleColonSeparator()
    {
        Assert.Equal(
            "WorkosCursorSessionToken=user_01ABC%3A%3Atoken-value",
            CursorUsageService.BuildCookieHeader(new CursorSession("token-value", "user_01ABC", null, null, null)));
    }

    [Theory]
    [InlineData("auth0|user_01ABC", "user_01ABC")]
    [InlineData("user_01ABC", "user_01ABC")]
    [InlineData("a|b|user-x.y_z", "user-x.y_z")]
    public void TryReadUserId_TakesTheTrailingSubjectSegment(string subject, string expected)
    {
        Assert.True(CursorUsageService.TryReadUserId(Jwt(subject, null, null), out string userId));
        Assert.Equal(expected, userId);
    }

    [Theory]
    [InlineData("auth0|bad user")]
    [InlineData("|||")]
    [InlineData("")]
    public void TryReadUserId_RejectsSubjectsThatCannotGoIntoACookie(string subject)
    {
        Assert.False(CursorUsageService.TryReadUserId(Jwt(subject, null, null), out _));
    }

    // MARK: - Helpers

    public enum BlobEncoding
    {
        Text,
        Utf8,
        Utf16LittleEndian
    }

    private static readonly Uri TestBaseAddress = new("https://cursor.test");

    private static T NotNull<T>(T? value) where T : class
    {
        Assert.NotNull(value);
        return value;
    }

    private static CursorUsageService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        TimeSpan? cacheDuration = null)
    {
        return new CursorUsageService(new DelegateHandler(respond), _ => TestSession(), TestBaseAddress, cacheDuration);
    }

    private static CursorSession TestSession()
    {
        return new CursorSession(
            Jwt("auth0|user_01ABC", "token@example.test", DateTimeOffset.UtcNow.AddDays(30)),
            "user_01ABC",
            "cached@example.test",
            "pro",
            DateTimeOffset.UtcNow.AddDays(30));
    }

    private static HttpResponseMessage DefaultResponse(HttpRequestMessage request)
    {
        return request.RequestUri!.AbsolutePath switch
        {
            "/api/usage-summary" => Json("""
            {
                "billingCycleStart": "2026-09-01T00:00:00.000Z",
                "billingCycleEnd": "2026-10-01T00:00:00.000Z",
                "membershipType": "pro",
                "individualUsage": {
                    "plan": { "enabled": true, "used": 800, "limit": 2000, "totalPercentUsed": 40.0 },
                    "onDemand": { "enabled": true, "used": 1250, "limit": 5000 }
                }
            }
            """),
            "/api/auth/me" => Json("""{ "email": "dev@example.test", "sub": "auth0|user_01ABC" }"""),
            // Token-based plans report no request quota, so this endpoint stays inert.
            "/api/usage" => Json("""{ "gpt-4": { "numRequests": 0 }, "startOfMonth": "2026-09-01T00:00:00.000Z" }"""),
            "/api/dashboard/get-sand-usage-status" => Json("""
            {
                "currentPeriodStart": "2026-09-14T09:12:32.776Z",
                "nextResetTimestampUtc": "2026-09-21T09:12:32.776Z",
                "usagePercent": 75,
                "hasAvailableUsage": true,
                "includedLimitZero": false
            }
            """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static string Jwt(string subject, string? email, DateTimeOffset? expiresAt)
    {
        var payload = new Dictionary<string, object> { ["sub"] = subject };
        if (email is not null)
        {
            payload["email"] = email;
        }

        if (expiresAt is DateTimeOffset expiry)
        {
            payload["exp"] = expiry.ToUnixTimeSeconds();
        }

        string encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"header.{encoded}.signature";
    }

    private static object Encode(string value, BlobEncoding encoding)
    {
        return encoding switch
        {
            BlobEncoding.Text => value,
            BlobEncoding.Utf8 => Encoding.UTF8.GetBytes(value),
            _ => Encoding.Unicode.GetBytes(value)
        };
    }

    private static void WriteItemTable(string databasePath, IReadOnlyDictionary<string, object> items)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE ItemTable(key TEXT PRIMARY KEY, value BLOB);";
            create.ExecuteNonQuery();
        }

        foreach ((string key, object value) in items)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO ItemTable(key, value) VALUES($key, $value);";
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$value", value);
            insert.ExecuteNonQuery();
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "cursor-usage-tests-" + Guid.NewGuid().ToString("N"));
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
    }

    private sealed class RequestRecorder
    {
        public List<string> Paths { get; } = [];
        public List<string> Cookies { get; } = [];

        public HttpResponseMessage Handle(HttpRequestMessage request)
        {
            lock (Paths)
            {
                Paths.Add(request.RequestUri!.AbsolutePath);
                if (request.Headers.TryGetValues("Cookie", out IEnumerable<string>? cookies))
                {
                    Cookies.Add(string.Join("; ", cookies));
                }
            }

            return DefaultResponse(request);
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
}
