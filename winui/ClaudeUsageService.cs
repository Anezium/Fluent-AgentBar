using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FluentAgentBar;

internal sealed class ClaudeUsageService : IDisposable
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string RefreshUrl = "https://platform.claude.com/v1/oauth/token";
    private const string OAuthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string ClaudeBeta = "oauth-2025-04-20";
    private const string UserAgent = "claude-code/2.0 (external, swbar)";

    private static readonly TimeSpan MinimumFetchInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(30);

    private readonly HttpClient _httpClient = new();
    private readonly ConcurrentDictionary<string, ClaudeUsageState> _states = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ProfileUsage> FetchAsync(string configDir, CancellationToken cancellationToken)
    {
        string normalizedConfigDir = NormalizeConfigDirectory(configDir);
        ClaudeUsageState state = _states.GetOrAdd(normalizedConfigDir, _ => new ClaudeUsageState());
        string credentialsPath = Path.Combine(normalizedConfigDir, ".credentials.json");
        EnsureAccountEmail(state, normalizedConfigDir);

        if (!File.Exists(credentialsPath))
        {
            return Unavailable();
        }

        ClaudeCredentials? credentials;
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
            return CachedOrUnavailable(state);
        }

        if (credentials is null)
        {
            return CachedOrUnavailable(state);
        }

        credentials = ApplyRefreshedCredentials(state, credentials);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (state.Sync)
        {
            if (state.CachedUsage is not null && now - state.LastSuccessfulFetch < MinimumFetchInterval)
            {
                return ToProfile(state.CachedUsage, state.AccountEmail ?? string.Empty);
            }

            if (now < state.NextAllowedFetch)
            {
                return CachedOrUnavailableNoLock(state);
            }
        }

        try
        {
            if (credentials.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                string sourceRefreshToken = credentials.RefreshToken;
                credentials = await RefreshCredentialsAsync(credentials, cancellationToken);
                RememberRefreshedCredentials(state, credentials, sourceRefreshToken);
            }

            ClaudeUsageSnapshot usage = await FetchUsageAsync(credentials.AccessToken, cancellationToken);
            if (string.IsNullOrWhiteSpace(usage.Plan) && !string.IsNullOrWhiteSpace(credentials.SubscriptionType))
            {
                usage = usage with { Plan = credentials.SubscriptionType };
            }

            RegisterSuccess(state, usage);
            return ToProfile(usage, state.AccountEmail ?? string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ClaudeUsageRequestException ex)
        {
            Debug.WriteLine(ex);
            RegisterFailure(state, ex.RetryAfter);
            return CachedOrUnavailable(state);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            RegisterFailure(state, null);
            return CachedOrUnavailable(state);
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
            throw new ClaudeUsageRequestException(
                $"Claude usage request failed with HTTP {(int)response.StatusCode}.",
                response.Headers.RetryAfter);
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
            ["client_id"] = OAuthClientId
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
            throw new ClaudeUsageRequestException(
                $"Claude OAuth refresh failed with HTTP {(int)response.StatusCode}.",
                response.Headers.RetryAfter);
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

        DateTimeOffset expiresAt = TryReadEpochMilliseconds(oauth, out DateTimeOffset parsedExpiresAt)
            ? parsedExpiresAt
            : DateTimeOffset.MaxValue;

        // Claude Code persists the plan here ("pro", "max", …); the usage
        // endpoint does not always echo it back.
        string subscriptionType = TryReadString(oauth, "subscriptionType", "subscription_type") ?? string.Empty;

        return new ClaudeCredentials(accessToken, refreshToken, expiresAt, subscriptionType);
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

        return new ClaudeCredentials(accessToken, refreshToken, expiresAt, previousCredentials.SubscriptionType);
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

    private static void RegisterSuccess(ClaudeUsageState state, ClaudeUsageSnapshot usage)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (state.Sync)
        {
            state.CachedUsage = usage;
            state.LastSuccessfulFetch = now;
            state.NextAllowedFetch = now + MinimumFetchInterval;
            state.FailureBackoff = InitialBackoff;
        }
    }

    private static void RegisterFailure(ClaudeUsageState state, RetryConditionHeaderValue? retryAfterHeader)
    {
        TimeSpan retryAfter = GetRetryAfterDelay(retryAfterHeader);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (state.Sync)
        {
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
        return new ProfileUsage(
            "Claude Code",
            string.Empty,
            string.Empty,
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

        return utilization <= 1.0 ? utilization * 100 : utilization;
    }

    private static int ClampPercent(double value)
    {
        return (int)Math.Clamp(Math.Round(value), 0, 100);
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

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record ClaudeCredentials(
        string AccessToken,
        string RefreshToken,
        DateTimeOffset ExpiresAt,
        string SubscriptionType);

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
        public DateTimeOffset LastSuccessfulFetch = DateTimeOffset.MinValue;
        public DateTimeOffset NextAllowedFetch = DateTimeOffset.MinValue;
        public TimeSpan FailureBackoff = InitialBackoff;
    }

    private sealed class ClaudeUsageRequestException : Exception
    {
        public ClaudeUsageRequestException(string message, RetryConditionHeaderValue? retryAfter = null)
            : base(message)
        {
            RetryAfter = retryAfter;
        }

        public RetryConditionHeaderValue? RetryAfter { get; }
    }
}
