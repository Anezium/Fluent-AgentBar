using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FluentAgentBar;

// Local Cursor.app session derived from the desktop app's SQLite state store.
// AccessToken is a JWT; UserId is the trailing segment of its "sub" claim
// ("auth0|user_01ABC" -> "user_01ABC") and is what the web cookie expects.
internal sealed record CursorSession(
    string AccessToken,
    string UserId,
    string? Email,
    string? MembershipType,
    DateTimeOffset? ExpiresAt);

// Reads Cursor plan usage from cursor.com using the session token that the
// Cursor desktop app persists locally. Mirrors CodexBar's Cursor provider:
//   GET  /api/usage-summary                        -> plan, billing cycle, included + usage-based spend
//   GET  /api/auth/me                              -> account email (best effort)
//   POST /api/dashboard/get-sand-usage-status      -> "Grok Bot" included allowance (best effort)
//   GET  /api/usage?user=ID                        -> legacy request-based plans (best effort)
internal sealed class CursorUsageService : IDisposable
{
    private const string DefaultBaseAddress = "https://cursor.com";
    private const string SessionCookieName = "WorkosCursorSessionToken";
    private const string GrokBotGroupName = "Grok Bot";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _httpClient;
    private readonly Func<string, CursorSession?> _sessionLoader;
    private readonly Uri _baseAddress;
    private readonly Dictionary<string, CursorHomeState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _statesGate = new();
    private readonly TimeSpan _cacheDuration;

    public CursorUsageService()
        : this(new HttpClient { Timeout = RequestTimeout }, LoadSessionFromDisk, null, null)
    {
    }

    internal CursorUsageService(
        HttpMessageHandler handler,
        Func<string, CursorSession?> sessionLoader,
        Uri? baseAddress = null,
        TimeSpan? cacheDuration = null)
        : this(
            new HttpClient(handler, disposeHandler: false) { Timeout = RequestTimeout },
            sessionLoader,
            baseAddress,
            cacheDuration)
    {
    }

    private CursorUsageService(
        HttpClient httpClient,
        Func<string, CursorSession?> sessionLoader,
        Uri? baseAddress,
        TimeSpan? cacheDuration)
    {
        _httpClient = httpClient;
        _sessionLoader = sessionLoader;
        _baseAddress = baseAddress ?? new Uri(DefaultBaseAddress);
        _cacheDuration = cacheDuration ?? CacheDuration;
    }

    public async Task<ProviderUsageSnapshot> FetchAsync(string home, CancellationToken cancellationToken)
    {
        string key = NormalizeHome(home);
        CursorHomeState state = GetState(key);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        CursorSession session = LoadSessionOrThrow(key);
        string fingerprint = Fingerprint(session.AccessToken);

        lock (state.Gate)
        {
            bool sameAuth = string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal);
            if (sameAuth && state.Cached is not null && now - state.CachedAt < _cacheDuration)
            {
                return state.Cached;
            }

            if (sameAuth && now < state.NextAllowedFetch)
            {
                if (state.Cached is not null)
                {
                    return state.Cached;
                }

                if (state.LoginRequired)
                {
                    throw new ProviderLoginRequiredException("Cursor rejected the local session token.");
                }
            }
        }

        ProviderUsageSnapshot snapshot;
        try
        {
            snapshot = await FetchFromApiAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderLoginRequiredException)
        {
            RecordFailure(state, fingerprint, loginRequired: true);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"Cursor usage fetch failed: {ex.GetType().Name}");
            RecordFailure(state, fingerprint, loginRequired: false);

            lock (state.Gate)
            {
                if (state.Cached is not null &&
                    string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return state.Cached;
                }
            }

            throw;
        }

        lock (state.Gate)
        {
            state.Cached = snapshot;
            state.CachedAt = DateTimeOffset.UtcNow;
            state.Fingerprint = fingerprint;
            state.NextAllowedFetch = DateTimeOffset.MinValue;
            state.Backoff = InitialBackoff;
            state.LoginRequired = false;
        }

        return snapshot;
    }

    private CursorSession LoadSessionOrThrow(string home)
    {
        CursorSession? session;
        try
        {
            session = _sessionLoader(home);
        }
        catch (Exception ex)
        {
            throw new ProviderLoginRequiredException("Cursor local session could not be read.", ex);
        }

        if (session is null || string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrEmpty(session.UserId))
        {
            throw new ProviderLoginRequiredException("No Cursor session token was found on this machine.");
        }

        if (session.ExpiresAt is DateTimeOffset expiry && expiry <= DateTimeOffset.UtcNow.AddSeconds(60))
        {
            throw new ProviderLoginRequiredException("The local Cursor session token has expired.");
        }

        return session;
    }

    private void RecordFailure(CursorHomeState state, string fingerprint, bool loginRequired)
    {
        lock (state.Gate)
        {
            if (!string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                state.Backoff = InitialBackoff;
                state.Cached = null;
            }

            state.Fingerprint = fingerprint;
            state.LoginRequired = loginRequired;
            state.NextAllowedFetch = DateTimeOffset.UtcNow + state.Backoff;
            state.Backoff = TimeSpan.FromMinutes(Math.Min(state.Backoff.TotalMinutes * 2, MaximumBackoff.TotalMinutes));
        }
    }

    private CursorHomeState GetState(string home)
    {
        lock (_statesGate)
        {
            if (!_states.TryGetValue(home, out CursorHomeState? state))
            {
                state = new CursorHomeState();
                _states[home] = state;
            }

            return state;
        }
    }

    // MARK: - HTTP

    private async Task<ProviderUsageSnapshot> FetchFromApiAsync(CursorSession session, CancellationToken cancellationToken)
    {
        string cookie = BuildCookieHeader(session);

        string summaryJson = await GetStringAsync("/api/usage-summary", cookie, cancellationToken).ConfigureAwait(false);
        CursorUsageSummary summary = CursorUsageSummary.Parse(summaryJson);

        string? email = await TryFetchEmailAsync(cookie, cancellationToken).ConfigureAwait(false)
            ?? ReadJwtClaim(session.AccessToken, "email")
            ?? session.Email;

        CursorSandUsage? sand = await TryFetchSandUsageAsync(cookie, cancellationToken).ConfigureAwait(false);
        CursorRequestUsage? requestUsage = await TryFetchRequestUsageAsync(session.UserId, cookie, cancellationToken)
            .ConfigureAwait(false);

        return BuildSnapshot(summary, email, sand, requestUsage, session.MembershipType, DateTimeOffset.UtcNow);
    }

    private async Task<string> GetStringAsync(string path, string cookie, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseAddress, path));
        ApplyHeaders(request, cookie);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        return await ReadBodyOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> TryFetchEmailAsync(string cookie, CancellationToken cancellationToken)
    {
        try
        {
            string body = await GetStringAsync("/api/auth/me", cookie, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            return ReadString(document.RootElement, "email");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ProviderLoginRequiredException)
        {
            Debug.WriteLine($"Cursor account lookup failed: {ex.GetType().Name}");
            return null;
        }
    }

    // Best effort: a missing or failing Grok Bot endpoint must never fail the whole fetch.
    private async Task<CursorSandUsage?> TryFetchSandUsageAsync(string cookie, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(_baseAddress, "/api/dashboard/get-sand-usage-status"))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            ApplyHeaders(request, cookie);
            request.Headers.TryAddWithoutValidation("Origin", _baseAddress.GetLeftPart(UriPartial.Authority));

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            string body = await ReadBodyOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
            return CursorSandUsage.Parse(body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"Cursor Grok Bot usage lookup failed: {ex.GetType().Name}");
            return null;
        }
    }

    // Best effort: only legacy request-based plans answer with a request quota.
    private async Task<CursorRequestUsage?> TryFetchRequestUsageAsync(
        string userId,
        string cookie,
        CancellationToken cancellationToken)
    {
        try
        {
            string path = "/api/usage?user=" + Uri.EscapeDataString(userId);
            string body = await GetStringAsync(path, cookie, cancellationToken).ConfigureAwait(false);
            return CursorRequestUsage.Parse(body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"Cursor request usage lookup failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static void ApplyHeaders(HttpRequestMessage request, string cookie)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
    }

    private static async Task<string> ReadBodyOrThrowAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ProviderLoginRequiredException("Cursor rejected the local session token.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Cursor API returned HTTP {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string BuildCookieHeader(CursorSession session)
    {
        return $"{SessionCookieName}={session.UserId}%3A%3A{session.AccessToken}";
    }

    // MARK: - Snapshot shaping

    // Windows are "percent left", so every CodexBar "used percent" is inverted here.
    // The first group is what the taskbar widget renders, so the plan window leads.
    internal static ProviderUsageSnapshot BuildSnapshot(
        CursorUsageSummary summary,
        string? email,
        CursorSandUsage? sand,
        CursorRequestUsage? requestUsage,
        string? fallbackMembershipType,
        DateTimeOffset now)
    {
        bool isLegacyRequestPlan = requestUsage is { Limit: > 0 };
        double planUsedPercent = isLegacyRequestPlan
            ? requestUsage!.UsedPercent
            : summary.PlanPercentUsed();

        var planWindows = new List<ProviderQuotaWindow>
        {
            new("Plan", RemainingPercent(planUsedPercent), summary.BillingCycleEnd)
        };

        (double used, double? limit) spend = summary.ResolveUsageBasedSpend();
        if (spend.limit is > 0)
        {
            double spendUsedPercent = spend.used / spend.limit.Value * 100.0;
            planWindows.Add(new ProviderQuotaWindow(
                "Usage-based",
                RemainingPercent(spendUsedPercent),
                summary.BillingCycleEnd));
        }

        var groups = new List<ProviderQuotaGroup>
        {
            new(string.Empty, planWindows)
        };

        // Grok Bot is a separate weekly allowance; it makes no sense next to a
        // legacy request quota, which is why CodexBar hides it on those plans.
        if (!isLegacyRequestPlan && sand?.ToWindow(now) is ProviderQuotaWindow grokBot)
        {
            groups.Add(new ProviderQuotaGroup(GrokBotGroupName, [grokBot]));
        }

        string plan = FormatMembershipType(summary.MembershipType ?? fallbackMembershipType);
        return new ProviderUsageSnapshot(plan, email ?? string.Empty, groups);
    }

    internal static int RemainingPercent(double usedPercent)
    {
        return (int)Math.Clamp(Math.Round(100.0 - Math.Clamp(usedPercent, 0.0, 100.0)), 0, 100);
    }

    internal static string FormatMembershipType(string? membershipType)
    {
        string trimmed = membershipType?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.ToLowerInvariant() switch
        {
            "business" => "Business",
            "enterprise" => "Enterprise",
            "express" => "Start",
            "free" => "Free",
            "free_trial" => "Pro Trial",
            "hobby" => "Hobby",
            "pro" or "pro_student" => "Pro",
            "pro_plus" => "Pro+",
            "team" => "Team",
            "ultra" => "Ultra",
            _ => trimmed
        };
    }

    // MARK: - Local session store

    // Two local stores share the Cursor home: the desktop app's state.vscdb
    // and the cursor-agent CLI's auth.json. Either token works as the web
    // session cookie, so take whichever unexpired one lives longest.
    internal static CursorSession? LoadSessionFromDisk(string home)
    {
        string root = Environment.ExpandEnvironmentVariables(home);
        string databasePath = Path.Combine(root, "User", "globalStorage", "state.vscdb");
        string authFilePath = Path.Combine(root, "auth.json");
        return PickFreshestSession(
            LoadSessionFromDatabase(databasePath),
            LoadSessionFromAuthFile(authFilePath));
    }

    internal static CursorSession? PickFreshestSession(params CursorSession?[] candidates)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<CursorSession> sessions = candidates
            .Where(session => session is not null && !string.IsNullOrWhiteSpace(session.AccessToken))
            .Select(session => session!)
            .ToList();

        return sessions
            .Where(session => session.ExpiresAt is null || session.ExpiresAt > now)
            .OrderByDescending(session => session.ExpiresAt ?? DateTimeOffset.MaxValue)
            .FirstOrDefault()
            ?? sessions.OrderByDescending(session => session.ExpiresAt ?? DateTimeOffset.MinValue).FirstOrDefault();
    }

    // cursor-agent (the Cursor CLI) stores {"accessToken": "<jwt>", "refreshToken": "<jwt>"}
    // at %APPDATA%\Cursor\auth.json on Windows.
    internal static CursorSession? LoadSessionFromAuthFile(string authFilePath)
    {
        if (!File.Exists(authFilePath))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(authFilePath));
            string? accessToken = ReadString(document.RootElement, "accessToken");
            if (string.IsNullOrWhiteSpace(accessToken) || !TryReadUserId(accessToken, out string userId))
            {
                return null;
            }

            return new CursorSession(
                accessToken,
                userId,
                ReadJwtClaim(accessToken, "email"),
                null,
                ReadJwtExpiry(accessToken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Cursor auth.json read failed: {ex.GetType().Name}");
            return null;
        }
    }

    // The database is locked while Cursor runs, so read a private copy of it
    // (WAL sidecars included, otherwise a fresh token can still be invisible).
    internal static CursorSession? LoadSessionFromDatabase(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        string scratch = Path.Combine(Path.GetTempPath(), "fluent-agentbar-cursor-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratch);
            string copyPath = Path.Combine(scratch, "state.vscdb");
            CopyIfPresent(databasePath, copyPath);
            CopyIfPresent(databasePath + "-wal", copyPath + "-wal");
            CopyIfPresent(databasePath + "-shm", copyPath + "-shm");

            return ReadSession(copyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            Debug.WriteLine($"Cursor session store read failed: {ex.GetType().Name}");
            return null;
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }
    }

    private static CursorSession? ReadSession(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        string? accessToken = ReadItem(connection, "cursorAuth/accessToken");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        if (!TryReadUserId(accessToken, out string userId))
        {
            return null;
        }

        return new CursorSession(
            accessToken,
            userId,
            ReadJwtClaim(accessToken, "email") ?? ReadItem(connection, "cursorAuth/cachedEmail"),
            ReadItem(connection, "cursorAuth/stripeMembershipType"),
            ReadJwtExpiry(accessToken));
    }

    private static string? ReadItem(SqliteConnection connection, string key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }

        return reader.GetFieldType(0) == typeof(byte[])
            ? DecodeBlob((byte[])reader.GetValue(0))
            : reader.GetString(0);
    }

    // Cursor writes the token as TEXT on some builds and as a BLOB on others;
    // ASCII UTF-16LE is also valid UTF-8 with NULs, so sniff it before decoding.
    internal static string? DecodeBlob(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        if (bytes.Length % 2 == 0 && IsAsciiUtf16LittleEndian(bytes))
        {
            string utf16 = Encoding.Unicode.GetString(bytes);
            if (!string.IsNullOrWhiteSpace(utf16))
            {
                return utf16;
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool IsAsciiUtf16LittleEndian(byte[] bytes)
    {
        for (int index = 0; index < bytes.Length; index += 2)
        {
            if (bytes[index] is 0 or >= 128 || bytes[index + 1] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void CopyIfPresent(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Copy(source, destination, overwrite: true);
        }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Cursor session scratch cleanup failed: {ex.GetType().Name}");
        }
    }

    // MARK: - JWT helpers

    // "auth0|user_01ABC" -> "user_01ABC"; the web cookie only accepts that segment.
    internal static bool TryReadUserId(string accessToken, out string userId)
    {
        userId = string.Empty;
        string? subject = ReadJwtClaim(accessToken, "sub");
        if (string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        string candidate = subject.Split('|', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts
            ? parts[^1]
            : subject;

        if (candidate.Length == 0 || !candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            return false;
        }

        userId = candidate;
        return true;
    }

    internal static string? ReadJwtClaim(string jwt, string claim)
    {
        if (!TryReadJwtPayload(jwt, out JsonElement payload))
        {
            return null;
        }

        return ReadString(payload, claim);
    }

    private static DateTimeOffset? ReadJwtExpiry(string jwt)
    {
        if (!TryReadJwtPayload(jwt, out JsonElement payload) ||
            !payload.TryGetProperty("exp", out JsonElement exp) ||
            exp.ValueKind != JsonValueKind.Number ||
            !exp.TryGetDouble(out double seconds))
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000.0));
    }

    private static bool TryReadJwtPayload(string jwt, out JsonElement payload)
    {
        payload = default;
        string[] parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        string encoded = parts[1].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - (encoded.Length % 4)) % 4), '=');

        try
        {
            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            payload = document.RootElement.Clone();
            return payload.ValueKind == JsonValueKind.Object;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    // MARK: - JSON helpers

    internal static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string text = value.GetString() ?? string.Empty;
        return text.Length == 0 ? null : text;
    }

    internal static double? ReadDouble(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result))
        {
            return null;
        }

        return result;
    }

    internal static bool? ReadBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    internal static JsonElement? ReadObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return value;
    }

    internal static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class CursorHomeState
    {
        public readonly object Gate = new();
        public ProviderUsageSnapshot? Cached;
        public DateTimeOffset CachedAt;
        public DateTimeOffset NextAllowedFetch = DateTimeOffset.MinValue;
        public TimeSpan Backoff = InitialBackoff;
        public string? Fingerprint;
        public bool LoginRequired;
    }

    private static string NormalizeHome(string home)
    {
        string expanded = Environment.ExpandEnvironmentVariables(home ?? string.Empty).Trim();
        return expanded.Length == 0 ? "(default)" : expanded.TrimEnd('\\', '/');
    }

    // Short, non-reversible marker used to invalidate caches when the local
    // token changes. Never log it or the token it derives from.
    private static string Fingerprint(string token)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash, 0, 8);
    }
}

// /api/usage-summary. Cents-based blocks; percent fields are already in
// percentage points even when fractional (0.36 means 0.36%).
internal sealed class CursorUsageSummary
{
    private CursorUsageSummary()
    {
    }

    public string? MembershipType { get; private init; }
    public DateTimeOffset? BillingCycleStart { get; private init; }
    public DateTimeOffset? BillingCycleEnd { get; private init; }
    public double? PlanUsedCents { get; private init; }
    public double? PlanLimitCents { get; private init; }
    public double? AutoPercentUsed { get; private init; }
    public double? ApiPercentUsed { get; private init; }
    public double? TotalPercentUsed { get; private init; }
    public double? OverallUsedCents { get; private init; }
    public double? OverallLimitCents { get; private init; }
    public double? PooledUsedCents { get; private init; }
    public double? PooledLimitCents { get; private init; }
    public double OnDemandUsedCents { get; private init; }
    public double? OnDemandLimitCents { get; private init; }
    public double? TeamOnDemandUsedCents { get; private init; }
    public double? TeamOnDemandLimitCents { get; private init; }

    public static CursorUsageSummary Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        JsonElement? individual = CursorUsageService.ReadObject(root, "individualUsage");
        JsonElement? plan = individual is JsonElement iv ? CursorUsageService.ReadObject(iv, "plan") : null;
        JsonElement? overall = individual is JsonElement iv2 ? CursorUsageService.ReadObject(iv2, "overall") : null;
        JsonElement? onDemand = individual is JsonElement iv3 ? CursorUsageService.ReadObject(iv3, "onDemand") : null;
        JsonElement? team = CursorUsageService.ReadObject(root, "teamUsage");
        JsonElement? teamOnDemand = team is JsonElement tv ? CursorUsageService.ReadObject(tv, "onDemand") : null;
        JsonElement? pooled = team is JsonElement tv2 ? CursorUsageService.ReadObject(tv2, "pooled") : null;

        return new CursorUsageSummary
        {
            MembershipType = CursorUsageService.ReadString(root, "membershipType"),
            BillingCycleStart = CursorUsageService.ParseTimestamp(CursorUsageService.ReadString(root, "billingCycleStart")),
            BillingCycleEnd = CursorUsageService.ParseTimestamp(CursorUsageService.ReadString(root, "billingCycleEnd")),
            PlanUsedCents = plan is JsonElement p1 ? CursorUsageService.ReadDouble(p1, "used") : null,
            PlanLimitCents = plan is JsonElement p2 ? CursorUsageService.ReadDouble(p2, "limit") : null,
            AutoPercentUsed = plan is JsonElement p3 ? CursorUsageService.ReadDouble(p3, "autoPercentUsed") : null,
            ApiPercentUsed = plan is JsonElement p4 ? CursorUsageService.ReadDouble(p4, "apiPercentUsed") : null,
            TotalPercentUsed = plan is JsonElement p5 ? CursorUsageService.ReadDouble(p5, "totalPercentUsed") : null,
            OverallUsedCents = overall is JsonElement o1 ? CursorUsageService.ReadDouble(o1, "used") : null,
            OverallLimitCents = overall is JsonElement o2 ? CursorUsageService.ReadDouble(o2, "limit") : null,
            PooledUsedCents = pooled is JsonElement q1 ? CursorUsageService.ReadDouble(q1, "used") : null,
            PooledLimitCents = pooled is JsonElement q2 ? CursorUsageService.ReadDouble(q2, "limit") : null,
            OnDemandUsedCents = (onDemand is JsonElement d1 ? CursorUsageService.ReadDouble(d1, "used") : null) ?? 0,
            OnDemandLimitCents = onDemand is JsonElement d2 ? CursorUsageService.ReadDouble(d2, "limit") : null,
            TeamOnDemandUsedCents = teamOnDemand is JsonElement t1 ? CursorUsageService.ReadDouble(t1, "used") : null,
            TeamOnDemandLimitCents = teamOnDemand is JsonElement t2 ? CursorUsageService.ReadDouble(t2, "limit") : null
        };
    }

    // Headline "Total" precedence, ported from CodexBar:
    //   totalPercentUsed -> auto+api average -> either lane -> plan ratio
    //   -> individualUsage.overall ratio (enterprise personal cap)
    //   -> teamUsage.pooled ratio (shared pool, last resort).
    public double PlanPercentUsed()
    {
        double? auto = Clamp(AutoPercentUsed);
        double? api = Clamp(ApiPercentUsed);

        if (Clamp(TotalPercentUsed) is double total)
        {
            return total;
        }

        if (auto is double autoUsed && api is double apiUsed)
        {
            return Math.Clamp((autoUsed + apiUsed) / 2.0, 0.0, 100.0);
        }

        if (api is double apiOnly)
        {
            return apiOnly;
        }

        if (auto is double autoOnly)
        {
            return autoOnly;
        }

        if (Ratio(PlanUsedCents, PlanLimitCents) is double planRatio)
        {
            return planRatio;
        }

        if (Ratio(OverallUsedCents, OverallLimitCents) is double overallRatio)
        {
            return overallRatio;
        }

        return Ratio(PooledUsedCents, PooledLimitCents) ?? 0.0;
    }

    // A personal usage-based budget wins; a team account with no personal cap
    // falls back to the shared on-demand pool.
    public (double Used, double? Limit) ResolveUsageBasedSpend()
    {
        if (OnDemandLimitCents is > 0)
        {
            return (OnDemandUsedCents / 100.0, OnDemandLimitCents.Value / 100.0);
        }

        if (TeamOnDemandLimitCents is > 0)
        {
            return ((TeamOnDemandUsedCents ?? 0) / 100.0, TeamOnDemandLimitCents.Value / 100.0);
        }

        return (OnDemandUsedCents / 100.0, OnDemandLimitCents / 100.0);
    }

    private static double? Clamp(double? value)
    {
        return value is double raw ? Math.Clamp(raw, 0.0, 100.0) : null;
    }

    private static double? Ratio(double? used, double? limit)
    {
        return limit is > 0 ? Math.Clamp(used.GetValueOrDefault() / limit.Value * 100.0, 0.0, 100.0) : null;
    }
}

// POST /api/dashboard/get-sand-usage-status — the "Grok Bot" (internally
// "Sand") included allowance, a weekly window on the same Cursor account.
internal sealed class CursorSandUsage
{
    private CursorSandUsage()
    {
    }

    public DateTimeOffset? CurrentPeriodStart { get; private init; }
    public DateTimeOffset? NextResetTimestampUtc { get; private init; }
    public double? UsagePercent { get; private init; }
    public bool? HasNonZeroIncludedLimit { get; private init; }
    public bool? IncludedLimitZero { get; private init; }
    public DateTimeOffset? SandTrialExpiresAt { get; private init; }

    public static CursorSandUsage Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        return new CursorSandUsage
        {
            CurrentPeriodStart = CursorUsageService.ParseTimestamp(CursorUsageService.ReadString(root, "currentPeriodStart")),
            NextResetTimestampUtc = CursorUsageService.ParseTimestamp(CursorUsageService.ReadString(root, "nextResetTimestampUtc")),
            UsagePercent = CursorUsageService.ReadDouble(root, "usagePercent"),
            HasNonZeroIncludedLimit = CursorUsageService.ReadBool(root, "hasNonZeroIncludedLimit"),
            IncludedLimitZero = CursorUsageService.ReadBool(root, "includedLimitZero"),
            SandTrialExpiresAt = CursorUsageService.ParseTimestamp(CursorUsageService.ReadString(root, "sandTrialExpiresAt"))
        };
    }

    // Shown for an included allowance, or for an unexpired trial. A trial expiry
    // is not a recurring quota reset, so it is deliberately not surfaced as one.
    public ProviderQuotaWindow? ToWindow(DateTimeOffset now)
    {
        bool? hasLimit = IncludedLimitZero is bool zero ? !zero : HasNonZeroIncludedLimit;
        bool hasTrial = hasLimit != true && SandTrialExpiresAt is DateTimeOffset expiry && expiry > now;

        if ((hasLimit != true && !hasTrial) || UsagePercent is not double usagePercent)
        {
            return null;
        }

        return new ProviderQuotaWindow(
            "Included",
            CursorUsageService.RemainingPercent(usagePercent),
            hasTrial ? null : NextResetTimestampUtc);
    }
}

// GET /api/usage?user=ID — legacy request-based plans only.
internal sealed class CursorRequestUsage
{
    private CursorRequestUsage()
    {
    }

    public int Used { get; private init; }
    public int Limit { get; private init; }

    public double UsedPercent => Limit > 0 ? (double)Used / Limit * 100.0 : 0.0;

    public static CursorRequestUsage? Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (CursorUsageService.ReadObject(document.RootElement, "gpt-4") is not JsonElement gpt4)
        {
            return null;
        }

        double? limit = CursorUsageService.ReadDouble(gpt4, "maxRequestUsage");
        if (limit is not > 0)
        {
            return null;
        }

        double used = CursorUsageService.ReadDouble(gpt4, "numRequestsTotal")
            ?? CursorUsageService.ReadDouble(gpt4, "numRequests")
            ?? 0;

        return new CursorRequestUsage
        {
            Used = (int)Math.Round(used),
            Limit = (int)Math.Round(limit.Value)
        };
    }
}
