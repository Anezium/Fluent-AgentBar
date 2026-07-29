using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluentAgentBar;

internal sealed class ClaudeUsageService : IDisposable
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string RefreshUrl = "https://platform.claude.com/v1/oauth/token";
    private const string OAuthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string ClaudeCodeOAuthTokenEnvironmentVariable = "CLAUDE_CODE_OAUTH_TOKEN";
    private const string ClaudeBeta = "oauth-2025-04-20";
    private const string UserAgent = "claude-code/2.0 (external, swbar)";
    private const string DefaultFullOAuthScopes =
        "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";

    private static readonly TimeSpan MinimumFetchInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(30);

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _readEnvironmentOAuthToken;
    private readonly ConcurrentDictionary<string, ClaudeUsageState> _states = new(StringComparer.OrdinalIgnoreCase);

    public ClaudeUsageService()
        : this(new HttpClient(), ReadEnvironmentOAuthToken)
    {
    }

    internal ClaudeUsageService(HttpClient httpClient)
        : this(httpClient, ReadEnvironmentOAuthToken)
    {
    }

    internal ClaudeUsageService(HttpClient httpClient, Func<string?> readEnvironmentOAuthToken)
    {
        _httpClient = httpClient;
        _readEnvironmentOAuthToken = readEnvironmentOAuthToken;
    }

    public async Task<ProfileUsage> FetchAsync(string configDir, CancellationToken cancellationToken)
    {
        string normalizedConfigDir = NormalizeConfigDirectory(configDir);
        ClaudeUsageState state = _states.GetOrAdd(normalizedConfigDir, _ => new ClaudeUsageState());
        string credentialsPath = Path.Combine(normalizedConfigDir, ".credentials.json");
        EnsureAccountEmail(state, normalizedConfigDir);
        string? environmentAccessToken = NormalizeToken(_readEnvironmentOAuthToken());

        bool hasCredentialsFile = File.Exists(credentialsPath);
        if (!hasCredentialsFile && string.IsNullOrWhiteSpace(environmentAccessToken))
        {
            return Unavailable();
        }

        ClaudeCredentials? credentials = null;
        if (hasCredentialsFile)
        {
            try
            {
                credentials = await ReadCredentialsAsync(credentialsPath, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                if (string.IsNullOrWhiteSpace(environmentAccessToken))
                {
                    return CachedOrUnavailable(state);
                }
            }
        }

        if (credentials is null && string.IsNullOrWhiteSpace(environmentAccessToken))
        {
            return CachedOrUnavailable(state);
        }

        ClaudeCredentials? fileCredentials = credentials;
        bool hasQuotaCapableFileCredentials = credentials?.AllowsUserProfileScope == true;
        if (!hasQuotaCapableFileCredentials && string.IsNullOrWhiteSpace(environmentAccessToken))
        {
            return Unavailable("Full Login Required");
        }

        bool usesEnvironmentToken = !hasQuotaCapableFileCredentials &&
            !string.IsNullOrWhiteSpace(environmentAccessToken);
        credentials = usesEnvironmentToken
            ? WithEnvironmentAccessToken(environmentAccessToken!, credentials)
            : ApplyRefreshedCredentials(state, credentials!);
        string authFingerprint = CreateAuthFingerprint(credentials.AccessToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (state.Sync)
        {
            bool sameCachedAuth = string.Equals(
                state.CachedUsageAuthFingerprint,
                authFingerprint,
                StringComparison.Ordinal);
            if (sameCachedAuth &&
                state.CachedUsage is not null &&
                now - state.LastSuccessfulFetch < MinimumFetchInterval)
            {
                return ToProfile(state.CachedUsage, state.AccountEmail ?? string.Empty);
            }

            bool sameBackoffAuth = string.Equals(
                state.BackoffAuthFingerprint,
                authFingerprint,
                StringComparison.Ordinal);
            if (sameBackoffAuth && now < state.NextAllowedFetch)
            {
                return sameCachedAuth ? CachedOrUnavailableNoLock(state) : Unavailable();
            }
        }

        try
        {
            if (!usesEnvironmentToken && credentials.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                string sourceRefreshToken = credentials.RefreshToken;
                ClaudeCredentials refreshedCredentials = await RefreshCredentialsAsync(credentials, cancellationToken);
                ClaudeRefreshPersistenceResult persistenceResult = await TryPersistRefreshedCredentialsAsync(
                    credentialsPath,
                    refreshedCredentials,
                    sourceRefreshToken,
                    cancellationToken);
                credentials = persistenceResult.Credentials;
                if (persistenceResult.RememberInMemory)
                {
                    RememberRefreshedCredentials(state, credentials, sourceRefreshToken);
                }

                authFingerprint = CreateAuthFingerprint(credentials.AccessToken);
            }

            ClaudeUsageSnapshot usage = await FetchUsageAsync(credentials.AccessToken, cancellationToken);
            if (string.IsNullOrWhiteSpace(usage.Plan) && !string.IsNullOrWhiteSpace(credentials.SubscriptionType))
            {
                usage = usage with { Plan = credentials.SubscriptionType };
            }

            RegisterSuccess(state, usage, authFingerprint);
            return ToProfile(usage, state.AccountEmail ?? string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ClaudeUsageRequestException ex)
        {
            Debug.WriteLine(ex);
            if (usesEnvironmentToken && ex.RequiresUserProfileScope)
            {
                ProfileUsage? fallbackUsage = await TryFetchWithFileCredentialsAfterEnvironmentScopeFailureAsync(
                    state,
                    credentialsPath,
                    fileCredentials,
                    cancellationToken);
                return fallbackUsage ?? Unavailable("Full Login Required");
            }

            RegisterFailure(state, authFingerprint, ex.RetryAfter);
            ProfileUsage fallback = CachedOrUnavailable(state, authFingerprint);
            return ex.RequiresLogin && !fallback.IsAvailable
                ? Unavailable("Login Required")
                : fallback;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            RegisterFailure(state, authFingerprint, null);
            return CachedOrUnavailable(state, authFingerprint);
        }
    }

    private async Task<ClaudeUsageSnapshot> FetchUsageAsync(string accessToken, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", ClaudeBeta);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ClaudeUsageRequestException(
                $"Claude usage request failed with HTTP {(int)response.StatusCode}.",
                response.Headers.RetryAfter,
                RequiresUserProfileScope: ErrorBodyRequiresUserProfileScope(errorBody),
                RequiresLogin: response.StatusCode == System.Net.HttpStatusCode.Unauthorized);
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseUsage(json);
    }

    private async Task<ClaudeCredentials> RefreshCredentialsAsync(
        ClaudeCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            throw new ClaudeUsageRequestException("Claude OAuth credentials are expired and missing a refresh token.");
        }

        Dictionary<string, string> payload = new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.RefreshToken,
            ["client_id"] = OAuthClientId,
            ["scope"] = string.IsNullOrWhiteSpace(credentials.Scopes)
                ? DefaultFullOAuthScopes
                : credentials.Scopes
        };

        using StringContent content = new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using HttpRequestMessage request = new(HttpMethod.Post, RefreshUrl)
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ClaudeUsageRequestException(
                $"Claude OAuth refresh failed with HTTP {(int)response.StatusCode}.",
                response.Headers.RetryAfter,
                RequiresLogin: response.StatusCode is System.Net.HttpStatusCode.BadRequest or
                    System.Net.HttpStatusCode.Unauthorized ||
                    errorBody.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase));
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseRefreshResponse(json, credentials);
    }

    private static async Task<ClaudeCredentials?> ReadCredentialsAsync(
        string credentialsPath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(credentialsPath);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;
        if (!TryGetProperty(root, "claudeAiOauth", out JsonElement oauth))
        {
            return null;
        }

        string accessToken = TryReadString(oauth, "accessToken", "access_token") ?? string.Empty;
        string refreshToken = TryReadString(oauth, "refreshToken", "refresh_token") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        DateTimeOffset expiresAt = TryReadEpochMilliseconds(oauth!, out DateTimeOffset parsedExpiresAt)
            ? parsedExpiresAt
            : DateTimeOffset.MaxValue;

        // Claude Code persists the plan here ("pro", "max", …); the usage
        // endpoint does not always echo it back.
        string subscriptionType = TryReadString(oauth, "subscriptionType", "subscription_type") ?? string.Empty;
        bool allowsUserProfileScope = AllowsUserProfileScope(oauth);
        string scopes = ReadScopes(oauth);

        return new ClaudeCredentials(
            accessToken,
            refreshToken,
            expiresAt,
            subscriptionType,
            allowsUserProfileScope,
            scopes);
    }

    private static async Task<ClaudeRefreshPersistenceResult> TryPersistRefreshedCredentialsAsync(
        string credentialsPath,
        ClaudeCredentials credentials,
        string sourceRefreshToken,
        CancellationToken cancellationToken)
    {
        string? tempPath = null;
        try
        {
            string json = await File.ReadAllTextAsync(credentialsPath, cancellationToken);
            JsonNode? parsed = JsonNode.Parse(json);
            if (parsed is not JsonObject root ||
                !TryGetObject(root, "claudeAiOauth", out JsonObject? oauth))
            {
                return ClaudeRefreshPersistenceResult.UseInMemory(credentials);
            }

            ClaudeCredentials? currentCredentials = TryReadCredentials(root);
            if (currentCredentials is not null &&
                !string.Equals(currentCredentials.RefreshToken, sourceRefreshToken, StringComparison.Ordinal))
            {
                return ClaudeRefreshPersistenceResult.UseDisk(currentCredentials);
            }

            if (currentCredentials is null)
            {
                return ClaudeRefreshPersistenceResult.UseInMemory(credentials);
            }

            SetProperty(oauth!, "accessToken", credentials.AccessToken, "access_token");
            SetProperty(oauth!, "refreshToken", credentials.RefreshToken, "refresh_token");
            SetProperty(oauth!, "expiresAt", credentials.ExpiresAt.ToUnixTimeMilliseconds(), "expires_at");

            string directory = Path.GetDirectoryName(credentialsPath) ?? string.Empty;
            string fileName = Path.GetFileName(credentialsPath);
            tempPath = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
            JsonSerializerOptions options = new() { WriteIndented = true };
            await File.WriteAllTextAsync(tempPath, root.ToJsonString(options), cancellationToken);

            ClaudeCredentials? guardedCredentials = await ReadCredentialsAsync(credentialsPath, cancellationToken);
            if (guardedCredentials is not null &&
                !string.Equals(guardedCredentials.RefreshToken, sourceRefreshToken, StringComparison.Ordinal))
            {
                return ClaudeRefreshPersistenceResult.UseDisk(guardedCredentials);
            }

            if (guardedCredentials is null)
            {
                return ClaudeRefreshPersistenceResult.UseInMemory(credentials);
            }

            File.Replace(tempPath, credentialsPath, null, true);
            tempPath = null;

            return ClaudeRefreshPersistenceResult.UseInMemory(credentials);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return ClaudeRefreshPersistenceResult.UseInMemory(credentials);
        }
        finally
        {
            if (tempPath is not null)
            {
                TryDelete(tempPath);
            }
        }
    }

    private static ClaudeUsageSnapshot ParseUsage(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!TryGetProperty(root, "five_hour", out JsonElement fiveHour) &&
            !TryGetProperty(root, "fiveHour", out fiveHour))
        {
            throw new ClaudeUsageRequestException("Claude usage response is missing five_hour.");
        }

        if (!TryGetProperty(root, "seven_day", out JsonElement sevenDay) &&
            !TryGetProperty(root, "sevenDay", out sevenDay))
        {
            throw new ClaudeUsageRequestException("Claude usage response is missing seven_day.");
        }

        int remainingPercent = ClampPercent(100 - ReadUtilizationPercent(fiveHour));
        int weeklyPercent = ClampPercent(100 - ReadUtilizationPercent(sevenDay));
        DateTimeOffset? primaryResetAt = TryReadResetAt(fiveHour);
        DateTimeOffset? weeklyResetAt = TryReadResetAt(sevenDay);
        string plan = FindString(
            root,
            "plan",
            "planName",
            "plan_name",
            "planType",
            "plan_type",
            "subscriptionType",
            "subscription_type") ?? string.Empty;
        return new ClaudeUsageSnapshot(remainingPercent, weeklyPercent, plan, primaryResetAt, weeklyResetAt);
    }

    private static DateTimeOffset? TryReadResetAt(JsonElement element)
    {
        if (!TryGetProperty(element, "resets_at", out JsonElement value) &&
            !TryGetProperty(element, "resetsAt", out value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long epoch))
        {
            return epoch > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        return null;
    }

    private static ClaudeCredentials ParseRefreshResponse(string json, ClaudeCredentials previousCredentials)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string accessToken = TryReadString(root, "access_token", "accessToken") ?? previousCredentials.AccessToken;
        string refreshToken = TryReadString(root, "refresh_token", "refreshToken") ?? previousCredentials.RefreshToken;
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        if (TryReadEpochMilliseconds(root, out DateTimeOffset epochExpiresAt))
        {
            expiresAt = epochExpiresAt;
        }
        else if (TryReadLong(root, out long expiresInSeconds, "expires_in", "expiresIn"))
        {
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, expiresInSeconds));
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ClaudeUsageRequestException("Claude OAuth refresh did not return an access token.");
        }

        return new ClaudeCredentials(
            accessToken,
            refreshToken,
            expiresAt,
            previousCredentials.SubscriptionType,
            previousCredentials.AllowsUserProfileScope,
            TryReadString(root, "scope", "scopes") ?? previousCredentials.Scopes);
    }

    private ClaudeCredentials ApplyRefreshedCredentials(ClaudeUsageState state, ClaudeCredentials fileCredentials)
    {
        lock (state.Sync)
        {
            if (state.RefreshedCredentials is not null &&
                (string.Equals(
                    state.RefreshedCredentialsSourceRefreshToken,
                    fileCredentials.RefreshToken,
                    StringComparison.Ordinal) ||
                 string.Equals(
                     state.RefreshedCredentials.RefreshToken,
                     fileCredentials.RefreshToken,
                     StringComparison.Ordinal)))
            {
                return state.RefreshedCredentials;
            }
        }

        return fileCredentials;
    }

    private static ClaudeCredentials WithEnvironmentAccessToken(
        string accessToken,
        ClaudeCredentials? fileCredentials)
    {
        return new ClaudeCredentials(
            accessToken,
            fileCredentials?.RefreshToken ?? string.Empty,
            DateTimeOffset.MaxValue,
            fileCredentials?.SubscriptionType ?? string.Empty,
            AllowsUserProfileScope: false,
            Scopes: string.Empty);
    }

    private async Task<ProfileUsage?> TryFetchWithFileCredentialsAfterEnvironmentScopeFailureAsync(
        ClaudeUsageState state,
        string credentialsPath,
        ClaudeCredentials? fileCredentials,
        CancellationToken cancellationToken)
    {
        if (fileCredentials is null)
        {
            return null;
        }

        ClaudeCredentials credentials = ApplyRefreshedCredentials(state, fileCredentials);
        string authFingerprint = CreateAuthFingerprint(credentials.AccessToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (state.Sync)
        {
            bool sameCachedAuth = string.Equals(
                state.CachedUsageAuthFingerprint,
                authFingerprint,
                StringComparison.Ordinal);
            if (sameCachedAuth &&
                state.CachedUsage is not null &&
                now - state.LastSuccessfulFetch < MinimumFetchInterval)
            {
                return ToProfile(state.CachedUsage, state.AccountEmail ?? string.Empty);
            }

            bool sameBackoffAuth = string.Equals(
                state.BackoffAuthFingerprint,
                authFingerprint,
                StringComparison.Ordinal);
            if (sameBackoffAuth && now < state.NextAllowedFetch)
            {
                return sameCachedAuth ? CachedOrUnavailableNoLock(state) : null;
            }
        }

        try
        {
            if (credentials.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                string sourceRefreshToken = credentials.RefreshToken;
                ClaudeCredentials refreshedCredentials = await RefreshCredentialsAsync(credentials, cancellationToken);
                ClaudeRefreshPersistenceResult persistenceResult = await TryPersistRefreshedCredentialsAsync(
                    credentialsPath,
                    refreshedCredentials,
                    sourceRefreshToken,
                    cancellationToken);
                credentials = persistenceResult.Credentials;
                if (persistenceResult.RememberInMemory)
                {
                    RememberRefreshedCredentials(state, credentials, sourceRefreshToken);
                }

                authFingerprint = CreateAuthFingerprint(credentials.AccessToken);
            }

            ClaudeUsageSnapshot usage = await FetchUsageAsync(credentials.AccessToken, cancellationToken);
            if (string.IsNullOrWhiteSpace(usage.Plan) && !string.IsNullOrWhiteSpace(credentials.SubscriptionType))
            {
                usage = usage with { Plan = credentials.SubscriptionType };
            }

            RegisterSuccess(state, usage, authFingerprint);
            return ToProfile(usage, state.AccountEmail ?? string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ClaudeUsageRequestException ex)
        {
            Debug.WriteLine(ex);
            RegisterFailure(state, authFingerprint, ex.RetryAfter);
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            RegisterFailure(state, authFingerprint, null);
            return null;
        }
    }

    private static void RememberRefreshedCredentials(
        ClaudeUsageState state,
        ClaudeCredentials credentials,
        string sourceRefreshToken)
    {
        lock (state.Sync)
        {
            state.RefreshedCredentials = credentials;
            state.RefreshedCredentialsSourceRefreshToken = sourceRefreshToken;
        }
    }

    private static void RegisterSuccess(ClaudeUsageState state, ClaudeUsageSnapshot usage, string authFingerprint)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (state.Sync)
        {
            state.CachedUsage = usage;
            state.CachedUsageAuthFingerprint = authFingerprint;
            state.BackoffAuthFingerprint = authFingerprint;
            state.LastSuccessfulFetch = now;
            state.NextAllowedFetch = now + MinimumFetchInterval;
            state.FailureBackoff = InitialBackoff;
        }
    }

    private static void RegisterFailure(
        ClaudeUsageState state,
        string authFingerprint,
        RetryConditionHeaderValue? retryAfterHeader)
    {
        TimeSpan retryAfter = GetRetryAfterDelay(retryAfterHeader);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (state.Sync)
        {
            state.BackoffAuthFingerprint = authFingerprint;
            TimeSpan delay = retryAfter > state.FailureBackoff ? retryAfter : state.FailureBackoff;
            state.NextAllowedFetch = now + delay;
            state.FailureBackoff = TimeSpan.FromMinutes(Math.Min(
                state.FailureBackoff.TotalMinutes * 2,
                MaximumBackoff.TotalMinutes));
        }
    }

    private static TimeSpan GetRetryAfterDelay(RetryConditionHeaderValue? retryAfterHeader)
    {
        if (retryAfterHeader is null)
        {
            return TimeSpan.Zero;
        }

        if (retryAfterHeader.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfterHeader.Date is { } date)
        {
            TimeSpan delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return TimeSpan.Zero;
    }

    private static ProfileUsage CachedOrUnavailable(ClaudeUsageState state)
    {
        lock (state.Sync)
        {
            return CachedOrUnavailableNoLock(state);
        }
    }

    private static ProfileUsage CachedOrUnavailable(ClaudeUsageState state, string authFingerprint)
    {
        lock (state.Sync)
        {
            return string.Equals(state.CachedUsageAuthFingerprint, authFingerprint, StringComparison.Ordinal)
                ? CachedOrUnavailableNoLock(state)
                : Unavailable();
        }
    }

    private static ProfileUsage CachedOrUnavailableNoLock(ClaudeUsageState state)
    {
        return state.CachedUsage is null
            ? Unavailable()
            : ToProfile(state.CachedUsage, state.AccountEmail ?? string.Empty);
    }

    private static ProfileUsage ToProfile(ClaudeUsageSnapshot usage, string email)
    {
        return new ProfileUsage(
            "Claude Code",
            email,
            usage.Plan,
            usage.RemainingPercent,
            usage.WeeklyPercent,
            true,
            MockUsageData.ClaudeAccentColor)
        {
            PrimaryResetAt = usage.PrimaryResetAt,
            WeeklyResetAt = usage.WeeklyResetAt
        };
    }

    // The account email lives in .claude.json (oauthAccount.emailAddress),
    // either inside the config dir or as a sibling of it (~/.claude.json for
    // the default home). The file can grow to several MB of project history,
    // so the result is cached for the process lifetime.
    private static void EnsureAccountEmail(ClaudeUsageState state, string configDir)
    {
        lock (state.Sync)
        {
            if (state.AccountEmail is not null)
            {
                return;
            }
        }

        string email = ReadAccountEmail(configDir);
        lock (state.Sync)
        {
            state.AccountEmail ??= email;
        }
    }

    private static string ReadAccountEmail(string configDir)
    {
        string?[] candidates =
        [
            Path.Combine(configDir, ".claude.json"),
            Path.GetDirectoryName(configDir) is { Length: > 0 } parent
                ? Path.Combine(parent, ".claude.json")
                : null
        ];

        foreach (string? path in candidates)
        {
            if (path is null || !File.Exists(path))
            {
                continue;
            }

            try
            {
                using FileStream stream = File.OpenRead(path);
                using JsonDocument document = JsonDocument.Parse(stream);
                if (TryGetProperty(document.RootElement, "oauthAccount", out JsonElement account) &&
                    TryReadString(account, "emailAddress", "email_address", "email") is { Length: > 0 } email)
                {
                    return email;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        return string.Empty;
    }

    private static ProfileUsage Unavailable()
    {
        return Unavailable(string.Empty);
    }

    private static ProfileUsage Unavailable(string plan)
    {
        return new ProfileUsage(
            "Claude Code",
            string.Empty,
            plan,
            0,
            0,
            false,
            MockUsageData.ClaudeAccentColor);
    }

    private static double ReadUtilizationPercent(JsonElement element)
    {
        if (!TryReadDouble(element, out double utilization, "utilization"))
        {
            throw new ClaudeUsageRequestException("Claude usage response is missing utilization.");
        }

        // The OAuth usage endpoint reports percentage points in the 0..100
        // range. Values at or below 1 are therefore valid low utilization,
        // not fractional ratios (1.0 means 1% used, not 100% used).
        return utilization;
    }

    private static int ClampPercent(double value)
    {
        return (int)Math.Clamp(Math.Round(value), 0, 100);
    }

    private static bool ErrorBodyRequiresUserProfileScope(string errorBody)
    {
        return errorBody.Contains("user:profile", StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetClaudeConfigDirectory()
    {
        string? configuredDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Environment.ExpandEnvironmentVariables(configuredDirectory);
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
        }

        return Path.Combine(userProfile, ".claude");
    }

    private static string? ReadEnvironmentOAuthToken()
    {
        return NormalizeToken(Environment.GetEnvironmentVariable(ClaudeCodeOAuthTokenEnvironmentVariable)) ??
            NormalizeToken(Environment.GetEnvironmentVariable(
                ClaudeCodeOAuthTokenEnvironmentVariable,
                EnvironmentVariableTarget.User)) ??
            NormalizeToken(Environment.GetEnvironmentVariable(
                ClaudeCodeOAuthTokenEnvironmentVariable,
                EnvironmentVariableTarget.Machine));
    }

    private static string? NormalizeToken(string? token)
    {
        string? trimmed = token?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string CreateAuthFingerprint(string accessToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(accessToken);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string NormalizeConfigDirectory(string configDir)
    {
        string directory = string.IsNullOrWhiteSpace(configDir)
            ? GetClaudeConfigDirectory()
            : configDir;
        string expanded = Environment.ExpandEnvironmentVariables(directory);

        try
        {
            expanded = Path.GetFullPath(expanded);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        return expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? FindString(JsonElement element, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (TryGetProperty(element, propertyName, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string? value = FindString(property.Value, propertyNames);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? value = FindString(item, propertyNames);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static bool TryReadEpochMilliseconds(JsonElement element, out DateTimeOffset value)
    {
        if (TryReadLong(element, out long epoch, "expiresAt", "expires_at"))
        {
            try
            {
                value = DateTimeOffset.FromUnixTimeMilliseconds(epoch);
                return true;
            }
            catch (ArgumentOutOfRangeException ex)
            {
                Debug.WriteLine(ex);
            }
        }

        value = default;
        return false;
    }

    private static string? TryReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (TryGetProperty(element, propertyName, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static bool TryReadLong(JsonElement element, out long number, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number))
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return true;
            }
        }

        number = 0;
        return false;
    }

    private static bool TryReadDouble(JsonElement element, out double number, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number))
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return true;
            }
        }

        number = 0;
        return false;
    }

    private static bool AllowsUserProfileScope(JsonElement oauth)
    {
        if (!TryGetProperty(oauth, "scopes", out JsonElement scopes))
        {
            return true;
        }

        if (scopes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement scope in scopes.EnumerateArray())
            {
                if (scope.ValueKind == JsonValueKind.String &&
                    IsUserProfileScope(scope.GetString()))
                {
                    return true;
                }
            }
        }
        else if (scopes.ValueKind == JsonValueKind.String)
        {
            return ContainsUserProfileScope(scopes.GetString());
        }

        return false;
    }

    private static string ReadScopes(JsonElement oauth)
    {
        if (!TryGetProperty(oauth, "scopes", out JsonElement scopes))
        {
            return string.Empty;
        }

        if (scopes.ValueKind == JsonValueKind.String)
        {
            return scopes.GetString()?.Trim() ?? string.Empty;
        }

        return scopes.ValueKind == JsonValueKind.Array
            ? string.Join(' ', scopes.EnumerateArray()
                .Where(scope => scope.ValueKind == JsonValueKind.String)
                .Select(scope => scope.GetString())
                .Where(scope => !string.IsNullOrWhiteSpace(scope)))
            : string.Empty;
    }

    private static ClaudeCredentials? TryReadCredentials(JsonObject root)
    {
        if (!TryGetObject(root, "claudeAiOauth", out JsonObject? oauth))
        {
            return null;
        }

        string accessToken = TryReadString(oauth!, "accessToken", "access_token") ?? string.Empty;
        string refreshToken = TryReadString(oauth!, "refreshToken", "refresh_token") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        DateTimeOffset expiresAt = TryReadEpochMilliseconds(oauth!, out DateTimeOffset parsedExpiresAt)
            ? parsedExpiresAt
            : DateTimeOffset.MaxValue;
        string subscriptionType = TryReadString(oauth!, "subscriptionType", "subscription_type") ?? string.Empty;
        bool allowsUserProfileScope = AllowsUserProfileScope(oauth!);
        string scopes = ReadScopes(oauth!);

        return new ClaudeCredentials(
            accessToken,
            refreshToken,
            expiresAt,
            subscriptionType,
            allowsUserProfileScope,
            scopes);
    }

    private static bool AllowsUserProfileScope(JsonObject oauth)
    {
        if (!TryGetProperty(oauth, "scopes", out JsonNode? scopes))
        {
            return true;
        }

        if (scopes is JsonArray array)
        {
            foreach (JsonNode? scope in array)
            {
                if (scope is JsonValue value &&
                    value.TryGetValue(out string? text) &&
                    IsUserProfileScope(text))
                {
                    return true;
                }
            }
        }
        else if (scopes is JsonValue value &&
            value.TryGetValue(out string? text))
        {
            return ContainsUserProfileScope(text);
        }

        return false;
    }

    private static string ReadScopes(JsonObject oauth)
    {
        if (!TryGetProperty(oauth, "scopes", out JsonNode? scopes))
        {
            return string.Empty;
        }

        if (scopes is JsonValue value && value.TryGetValue(out string? text))
        {
            return text?.Trim() ?? string.Empty;
        }

        return scopes is JsonArray array
            ? string.Join(' ', array
                .OfType<JsonValue>()
                .Select(scope => scope.TryGetValue(out string? text) ? text : null)
                .Where(scope => !string.IsNullOrWhiteSpace(scope)))
            : string.Empty;
    }

    private static bool ContainsUserProfileScope(string? scopes)
    {
        if (string.IsNullOrWhiteSpace(scopes))
        {
            return false;
        }

        string[] parts = scopes.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(IsUserProfileScope);
    }

    private static bool IsUserProfileScope(string? scope)
    {
        return string.Equals(scope?.Trim(), "user:profile", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadEpochMilliseconds(JsonObject obj, out DateTimeOffset value)
    {
        if (TryReadLong(obj, out long epoch, "expiresAt", "expires_at"))
        {
            try
            {
                value = DateTimeOffset.FromUnixTimeMilliseconds(epoch);
                return true;
            }
            catch (ArgumentOutOfRangeException ex)
            {
                Debug.WriteLine(ex);
            }
        }

        value = default;
        return false;
    }

    private static string? TryReadString(JsonObject obj, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (TryGetProperty(obj, propertyName, out JsonNode? value) &&
                value is JsonValue jsonValue &&
                jsonValue.TryGetValue(out string? text))
            {
                return text;
            }
        }

        return null;
    }

    private static bool TryReadLong(JsonObject obj, out long number, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!TryGetProperty(obj, propertyName, out JsonNode? value) ||
                value is not JsonValue jsonValue)
            {
                continue;
            }

            if (jsonValue.TryGetValue(out long longValue))
            {
                number = longValue;
                return true;
            }

            if (jsonValue.TryGetValue(out string? text) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return true;
            }
        }

        number = 0;
        return false;
    }

    private static bool TryGetObject(JsonObject obj, string propertyName, out JsonObject? value)
    {
        if (TryGetProperty(obj, propertyName, out JsonNode? node) &&
            node is JsonObject jsonObject)
        {
            value = jsonObject;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryGetProperty(JsonObject obj, string propertyName, out JsonNode? value)
    {
        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static void SetProperty(JsonObject obj, string propertyName, string value, params string[] aliases)
    {
        string targetName = FindExistingPropertyName(obj, propertyName, aliases) ?? propertyName;
        obj[targetName] = JsonValue.Create(value);
    }

    private static void SetProperty(JsonObject obj, string propertyName, long value, params string[] aliases)
    {
        string targetName = FindExistingPropertyName(obj, propertyName, aliases) ?? propertyName;
        obj[targetName] = JsonValue.Create(value);
    }

    private static string? FindExistingPropertyName(JsonObject obj, string propertyName, params string[] aliases)
    {
        if (TryFindPropertyName(obj, propertyName, out string? existingName))
        {
            return existingName;
        }

        foreach (string alias in aliases)
        {
            if (TryFindPropertyName(obj, alias, out existingName))
            {
                return existingName;
            }
        }

        return null;
    }

    private static bool TryFindPropertyName(JsonObject obj, string propertyName, out string? existingName)
    {
        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                existingName = property.Key;
                return true;
            }
        }

        existingName = null;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record ClaudeCredentials(
        string AccessToken,
        string RefreshToken,
        DateTimeOffset ExpiresAt,
        string SubscriptionType,
        bool AllowsUserProfileScope,
        string Scopes);

    private sealed record ClaudeRefreshPersistenceResult(
        ClaudeCredentials Credentials,
        bool RememberInMemory)
    {
        public static ClaudeRefreshPersistenceResult UseInMemory(ClaudeCredentials credentials)
        {
            return new ClaudeRefreshPersistenceResult(credentials, true);
        }

        public static ClaudeRefreshPersistenceResult UseDisk(ClaudeCredentials credentials)
        {
            return new ClaudeRefreshPersistenceResult(credentials, false);
        }
    }

    private sealed record ClaudeUsageSnapshot(
        int RemainingPercent,
        int WeeklyPercent,
        string Plan,
        DateTimeOffset? PrimaryResetAt = null,
        DateTimeOffset? WeeklyResetAt = null);

    private sealed class ClaudeUsageState
    {
        public object Sync { get; } = new();
        public string? AccountEmail;
        public ClaudeUsageSnapshot? CachedUsage;
        public ClaudeCredentials? RefreshedCredentials;
        public string? RefreshedCredentialsSourceRefreshToken;
        public string? CachedUsageAuthFingerprint;
        public string? BackoffAuthFingerprint;
        public DateTimeOffset LastSuccessfulFetch = DateTimeOffset.MinValue;
        public DateTimeOffset NextAllowedFetch = DateTimeOffset.MinValue;
        public TimeSpan FailureBackoff = InitialBackoff;
    }

    private sealed class ClaudeUsageRequestException : Exception
    {
        public ClaudeUsageRequestException(
            string message,
            RetryConditionHeaderValue? retryAfter = null,
            bool RequiresUserProfileScope = false,
            bool RequiresLogin = false)
            : base(message)
        {
            RetryAfter = retryAfter;
            this.RequiresUserProfileScope = RequiresUserProfileScope;
            this.RequiresLogin = RequiresLogin;
        }

        public RetryConditionHeaderValue? RetryAfter { get; }
        public bool RequiresUserProfileScope { get; }
        public bool RequiresLogin { get; }
    }
}
