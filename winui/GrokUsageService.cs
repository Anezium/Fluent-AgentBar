using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FluentAgentBar;

// Grok Build (xAI `grok` CLI) credit usage.
//
// Two billing sources, tried in CodexBar's order:
//   1. the `grok` CLI itself, over JSON-RPC (`x.ai/billing` on `grok agent stdio`);
//   2. the CLI's HTTP backend, GET https://cli-chat-proxy.grok.com/v1/billing?format=credits
//      with the auth.json bearer token.
// The plan name lives on a third endpoint (/v1/settings, `subscription_tier_display`)
// and is merged in best-effort: a settings failure never fails the fetch.
//
// The grok.com gRPC-web surface CodexBar also knows about is deliberately not ported:
// it now requires a browser-held key-exchange keypair, so a bearer token cannot use it.
internal sealed class GrokUsageService : IDisposable
{
    internal const string BillingUrl = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
    internal const string SettingsUrl = "https://cli-chat-proxy.grok.com/v1/settings";
    private const string TokenAuthHeader = "x-xai-token-auth";
    private const string TokenAuthValue = "xai-grok-cli";
    private const string UserAgent = "FluentAgentBar";

    // auth.json is a map keyed by scope URL. `grok login` writes the OIDC entry for
    // SuperGrok subscribers; older flows wrote a session entry instead.
    private const string OidcScopePrefix = "https://auth.x.ai::";
    private const string LegacySessionScope = "https://accounts.x.ai/sign-in";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BillingTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SettingsTimeout = TimeSpan.FromSeconds(4);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<string, CancellationToken, Task<string?>>? _readCliBillingAsync;
    private readonly ConcurrentDictionary<string, GrokUsageState> _states = new(StringComparer.OrdinalIgnoreCase);

    public GrokUsageService()
        : this(new HttpClient(), RunCliBillingAsync, ownsHttpClient: true)
    {
    }

    // Test seam: the HTTP side is driven through an injected HttpClient (build it over a
    // stub HttpMessageHandler), the CLI side through an injected reader that returns the
    // raw JSON of the `x.ai/billing` result, or null when the CLI is unavailable.
    internal GrokUsageService(
        HttpClient httpClient,
        Func<string, CancellationToken, Task<string?>>? readCliBillingAsync = null)
        : this(httpClient, readCliBillingAsync, ownsHttpClient: false)
    {
    }

    private GrokUsageService(
        HttpClient httpClient,
        Func<string, CancellationToken, Task<string?>>? readCliBillingAsync,
        bool ownsHttpClient)
    {
        _httpClient = httpClient;
        _readCliBillingAsync = readCliBillingAsync;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<ProviderUsageSnapshot> FetchAsync(string home, CancellationToken cancellationToken)
    {
        string normalizedHome = NormalizeHome(home);
        GrokUsageState state = _states.GetOrAdd(normalizedHome, _ => new GrokUsageState());

        // A missing or tokenless auth.json is "Login Required", never a transient failure,
        // so it is reported before the cache and the backoff are consulted.
        GrokCredentials credentials = ReadCredentials(Path.Combine(normalizedHome, "auth.json"));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (state.Sync)
        {
            if (state.Cached is not null && now - state.LastSuccessfulFetch < CacheDuration)
            {
                return state.Cached;
            }

            if (now < state.NextAllowedFetch)
            {
                return state.Cached ?? throw new InvalidOperationException(
                    "Grok usage is temporarily unavailable after a failed request.");
            }
        }

        ProviderUsageSnapshot snapshot;
        try
        {
            snapshot = await FetchSnapshotAsync(normalizedHome, credentials, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderLoginRequiredException)
        {
            // The backend rejected the token: the cached numbers are no longer ours to show.
            lock (state.Sync)
            {
                state.Cached = null;
                RegisterFailureNoLock(state);
            }

            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            lock (state.Sync)
            {
                RegisterFailureNoLock(state);
                if (state.Cached is not null)
                {
                    return state.Cached;
                }
            }

            throw;
        }

        lock (state.Sync)
        {
            state.Cached = snapshot;
            state.LastSuccessfulFetch = DateTimeOffset.UtcNow;
            state.NextAllowedFetch = DateTimeOffset.MinValue;
            state.CurrentBackoff = TimeSpan.Zero;
        }

        return snapshot;
    }

    private async Task<ProviderUsageSnapshot> FetchSnapshotAsync(
        string home,
        GrokCredentials credentials,
        CancellationToken cancellationToken)
    {
        GrokBillingSnapshot? billing = await TryFetchCliBillingAsync(home, cancellationToken);

        // The CLI answer is authoritative when it carries a percentage. Otherwise fall through
        // to the HTTP proxy and keep the CLI's billing period as metadata.
        if (billing?.UsedPercent is null)
        {
            GrokBillingSnapshot? cliMetadata = billing;
            try
            {
                billing = await FetchProxyBillingAsync(credentials, cancellationToken);
            }
            catch (ProviderLoginRequiredException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (cliMetadata is not null)
            {
                Debug.WriteLine(ex);
                billing = cliMetadata;
            }

            if (cliMetadata is not null && billing is not null)
            {
                billing = billing.Completing(cliMetadata);
            }
        }

        string? settingsTier = await TryFetchSettingsTierAsync(credentials, cancellationToken);
        string plan = settingsTier
            ?? billing?.SubscriptionTier
            ?? credentials.LoginMethod
            ?? string.Empty;

        List<ProviderQuotaWindow> windows = [];
        if (billing?.UsedPercent is { } usedPercent)
        {
            windows.Add(new ProviderQuotaWindow(
                WindowLabel(billing.PeriodMinutes, billing.ResetAt, DateTimeOffset.UtcNow),
                ClampPercent(100 - usedPercent),
                billing.ResetAt));
        }

        // A credits payload that describes a billing period but publishes no usage value is a
        // successful response with unknown usage: identity and plan stay, the bar does not.
        List<ProviderQuotaGroup> groups = windows.Count > 0
            ? [new ProviderQuotaGroup(string.Empty, windows)]
            : [];

        return new ProviderUsageSnapshot(plan, credentials.Email ?? string.Empty, groups);
    }

    // MARK: - CLI (JSON-RPC over `grok agent stdio`)

    private async Task<GrokBillingSnapshot?> TryFetchCliBillingAsync(
        string home,
        CancellationToken cancellationToken)
    {
        if (_readCliBillingAsync is null)
        {
            return null;
        }

        string? json;
        try
        {
            json = await _readCliBillingAsync(home, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A missing or broken CLI is not an error: the HTTP proxy is the supported path.
            Debug.WriteLine(ex);
            return null;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return ParseCliBilling(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    internal static GrokBillingSnapshot? ParseCliBilling(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Accept both the bare `x.ai/billing` result and a full JSON-RPC envelope.
        if (root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object)
        {
            root = result;
        }

        DateTimeOffset? periodStart = null;
        DateTimeOffset? periodEnd = null;
        if (root.TryGetProperty("billingCycle", out JsonElement cycle) && cycle.ValueKind == JsonValueKind.Object)
        {
            periodStart = ParseTimestamp(ReadString(cycle, "billingPeriodStart"));
            periodEnd = ParseTimestamp(ReadString(cycle, "billingPeriodEnd"));
        }

        double? monthlyLimit = ReadAmount(root, "monthlyLimit");
        double? totalUsed = null;
        if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
        {
            totalUsed = ReadAmount(usage, "totalUsed");
        }

        double? usedPercent = monthlyLimit is > 0 && totalUsed is { } used
            ? Math.Clamp(used / monthlyLimit.Value * 100, 0, 100)
            : null;

        int? periodMinutes = periodStart is { } start && periodEnd is { } end && end > start
            ? (int)(end - start).TotalMinutes
            : null;

        if (usedPercent is null && periodEnd is null)
        {
            return null;
        }

        return new GrokBillingSnapshot(usedPercent, periodEnd, periodMinutes, null);
    }

    // Default CLI reader: spawn `grok agent stdio`, speak newline-delimited JSON-RPC 2.0,
    // and return the raw `result` of `x.ai/billing`. Returns null when `grok` is not installed.
    private static async Task<string?> RunCliBillingAsync(string home, CancellationToken cancellationToken)
    {
        string? executable = ResolveGrokBinary();
        if (executable is null)
        {
            return null;
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        startInfo.ArgumentList.Add("agent");
        startInfo.ArgumentList.Add("stdio");
        startInfo.Environment["GROK_HOME"] = home;

        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await SendRpcAsync(process, 1, "initialize", InitializeParams, timeout.Token);
            _ = await ReadRpcResultAsync(process, 1, timeout.Token);

            // `grok`'s ACP server looks methods up verbatim, so the slash in `x.ai/billing`
            // must not be escaped on the wire. The payload is written by hand for that reason.
            await SendRpcAsync(process, 2, "x.ai/billing", "{}", timeout.Token);
            return await ReadRpcResultAsync(process, 2, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
        finally
        {
            TerminateQuietly(process);
        }
    }

    private const string InitializeParams =
        """{"protocolVersion":"1","clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false}}""";

    private static async Task SendRpcAsync(
        Process process,
        int id,
        string method,
        string paramsJson,
        CancellationToken cancellationToken)
    {
        string payload = "{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString(CultureInfo.InvariantCulture)
            + ",\"method\":" + JsonSerializer.Serialize(method)
            + ",\"params\":" + paramsJson + "}";
        await process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<string?> ReadRpcResultAsync(
        Process process,
        int id,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("id", out JsonElement idElement) ||
                    !idElement.TryGetInt32(out int messageId) ||
                    messageId != id)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        "Grok RPC failed: " + (ReadString(error, "message") ?? "unknown JSON-RPC error"));
                }

                return root.TryGetProperty("result", out JsonElement result)
                    ? result.GetRawText()
                    : null;
            }
        }
    }

    private static string? ResolveGrokBinary()
    {
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        string[] extensions = ["", ".exe", ".cmd", ".bat"];
        foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions)
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim(), "grok" + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry; skip it.
                }
            }
        }

        return null;
    }

    private static void TerminateQuietly(Process process)
    {
        try
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    // MARK: - HTTP (CLI proxy)

    private async Task<GrokBillingSnapshot> FetchProxyBillingAsync(
        GrokCredentials credentials,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            BillingUrl,
            credentials,
            BillingTimeout,
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ProviderLoginRequiredException(
                "Grok billing rejected the stored credentials. Run `grok login` to refresh them.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "Grok billing request failed with HTTP " + (int)response.StatusCode + ".");
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseCreditsResponse(body);
    }

    internal static GrokBillingSnapshot ParseCreditsResponse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Could not parse Grok credits usage.", ex);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("config", out JsonElement config) ||
                config.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Could not parse Grok credits usage.");
            }

            string? tier = PlanDisplayName(ReadString(config, "subscriptionTier") ?? ReadString(root, "subscriptionTier"));

            DateTimeOffset? resetAt = null;
            if (config.TryGetProperty("currentPeriod", out JsonElement period) &&
                period.ValueKind == JsonValueKind.Object)
            {
                resetAt = ParseTimestamp(ReadString(period, "end"));
            }

            resetAt ??= ParseTimestamp(ReadString(config, "billingPeriodEnd"));

            if (config.TryGetProperty("creditUsagePercent", out JsonElement percentElement) &&
                percentElement.ValueKind == JsonValueKind.Number &&
                percentElement.TryGetDouble(out double percent) &&
                double.IsFinite(percent))
            {
                return new GrokBillingSnapshot(Math.Clamp(percent, 0, 100), resetAt, null, tier);
            }

            double? cap = ReadAmount(config, "onDemandCap");
            double? used = ReadAmount(config, "onDemandUsed");
            if (cap is > 0 && used is { } usedValue)
            {
                return new GrokBillingSnapshot(
                    Math.Clamp(usedValue / cap.Value * 100, 0, 100),
                    resetAt,
                    null,
                    tier);
            }

            // A described billing period with no usage field is a valid response with
            // unknown usage; it must not be reported as zero usage.
            if (resetAt is not null)
            {
                return new GrokBillingSnapshot(null, resetAt, null, tier);
            }

            throw new InvalidOperationException("Could not parse Grok credits usage.");
        }
    }

    private async Task<string?> TryFetchSettingsTierAsync(
        GrokCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (credentials.IsExpired)
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await SendAsync(
                SettingsUrl,
                credentials,
                SettingsTimeout,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseSettingsTier(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Plan naming is cosmetic: never fail the fetch over it.
            Debug.WriteLine(ex);
            return null;
        }
    }

    internal static string? ParseSettingsTier(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? PlanDisplayName(ReadString(document.RootElement, "subscription_tier_display"))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        string url,
        GrokCredentials credentials,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation(TokenAuthHeader, TokenAuthValue);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);

        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        return await _httpClient.SendAsync(request, timeoutSource.Token);
    }

    // MARK: - auth.json

    internal static GrokCredentials ReadCredentials(string authPath)
    {
        if (!File.Exists(authPath))
        {
            throw new ProviderLoginRequiredException(
                "Grok auth.json not found. Run `grok login` to authenticate.");
        }

        string json;
        try
        {
            json = File.ReadAllText(authPath);
        }
        catch (Exception ex)
        {
            throw new ProviderLoginRequiredException("Could not read Grok auth.json.", ex);
        }

        return ParseCredentials(json);
    }

    internal static GrokCredentials ParseCredentials(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ProviderLoginRequiredException("Could not decode Grok auth.json.", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ProviderLoginRequiredException("Grok auth.json is not a JSON object.");
            }

            JsonElement? oidc = null;
            string oidcScope = string.Empty;
            JsonElement? legacy = null;
            string legacyScope = string.Empty;

            foreach (JsonProperty entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // Only entries with a usable bearer token count: a stale OIDC record must
                // not shadow a healthy legacy session entry.
                if (string.IsNullOrEmpty(ReadString(entry.Value, "key")))
                {
                    continue;
                }

                if (entry.Name.StartsWith(OidcScopePrefix, StringComparison.Ordinal))
                {
                    oidc = entry.Value;
                    oidcScope = entry.Name;
                }
                else if (string.Equals(entry.Name, LegacySessionScope, StringComparison.Ordinal) ||
                    entry.Name.Contains("/sign-in", StringComparison.Ordinal))
                {
                    legacy = entry.Value;
                    legacyScope = entry.Name;
                }
            }

            JsonElement selected;
            string scope;
            if (oidc is { } oidcEntry)
            {
                selected = oidcEntry;
                scope = oidcScope;
            }
            else if (legacy is { } legacyEntry)
            {
                selected = legacyEntry;
                scope = legacyScope;
            }
            else
            {
                throw new ProviderLoginRequiredException(
                    "Grok auth.json exists but contains no access tokens.");
            }

            return new GrokCredentials(
                ReadString(selected, "key")!,
                scope,
                ReadString(selected, "email"),
                ReadString(selected, "team_id"),
                ReadString(selected, "auth_mode"),
                ParseTimestamp(ReadString(selected, "expires_at")));
        }
    }

    // MARK: - Shared helpers

    internal static string NormalizeHome(string home)
    {
        string expanded = Environment.ExpandEnvironmentVariables(home ?? string.Empty).Trim();
        if (expanded.Length == 0)
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".grok");
        }

        try
        {
            return Path.GetFullPath(expanded);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return expanded;
        }
    }

    // Grok's credit pools are either weekly or monthly. Prefer an explicit period duration;
    // fall back to the distance to the reset, which is how the credits proxy describes itself.
    internal static string WindowLabel(int? periodMinutes, DateTimeOffset? resetAt, DateTimeOffset now)
    {
        string? label = periodMinutes is { } minutes && minutes > 0
            ? LabelForDuration(TimeSpan.FromMinutes(minutes))
            : resetAt is { } reset ? LabelForDuration(reset - now) : null;

        if (label is not null)
        {
            return label;
        }

        // The untyped credits surface is the weekly pool, and that stays true right before
        // its reset, when the remaining distance no longer looks like a week.
        return periodMinutes is null && resetAt is not null ? "Weekly" : "Credits";
    }

    private static string? LabelForDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds <= 3600)
        {
            return null;
        }

        int days = (int)Math.Round(duration.TotalDays, MidpointRounding.AwayFromZero);
        if (days is >= 4 and <= 12)
        {
            return "Weekly";
        }

        return days is >= 20 and <= 45 ? "Monthly" : null;
    }

    internal static string? PlanDisplayName(string? raw)
    {
        string trimmed = raw?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }

        string compact = new string(trimmed.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
        return compact switch
        {
            "supergrokheavy" or "heavy" => "SuperGrok Heavy",
            "supergrok" => "SuperGrok",
            _ => trimmed
        };
    }

    private static int ClampPercent(double value)
    {
        return (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);
    }

    // The proxy reports amounts as `{ "val": <number> }`; fractional values are accepted so an
    // unusual shape cannot fail an otherwise valid response.
    private static double? ReadAmount(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement amount) || amount.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return amount.TryGetProperty("val", out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out double result) &&
            double.IsFinite(result)
            ? result
            : null;
    }

    private static string? ReadString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    internal static DateTimeOffset? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset value)
            ? value.ToUniversalTime()
            : null;
    }

    private static void RegisterFailureNoLock(GrokUsageState state)
    {
        state.CurrentBackoff = state.CurrentBackoff <= TimeSpan.Zero
            ? InitialBackoff
            : TimeSpan.FromTicks(Math.Min(state.CurrentBackoff.Ticks * 2, MaximumBackoff.Ticks));
        state.NextAllowedFetch = DateTimeOffset.UtcNow + state.CurrentBackoff;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed class GrokUsageState
    {
        public object Sync { get; } = new();

        public ProviderUsageSnapshot? Cached { get; set; }

        public DateTimeOffset LastSuccessfulFetch { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset NextAllowedFetch { get; set; } = DateTimeOffset.MinValue;

        public TimeSpan CurrentBackoff { get; set; } = TimeSpan.Zero;
    }
}

internal sealed record GrokCredentials(
    string AccessToken,
    string Scope,
    string? Email,
    string? TeamId,
    string? AuthMode,
    DateTimeOffset? ExpiresAt)
{
    public bool IsExpired => ExpiresAt is { } expiresAt && DateTimeOffset.UtcNow >= expiresAt;

    // OIDC only says the user signed in as SuperGrok; the billed tier comes from CLI settings.
    public string? LoginMethod => AuthMode?.Trim().ToLowerInvariant() switch
    {
        "oidc" => "SuperGrok",
        "session" => "session",
        null or "" => null,
        _ => AuthMode
    };
}

internal sealed record GrokBillingSnapshot(
    double? UsedPercent,
    DateTimeOffset? ResetAt,
    int? PeriodMinutes,
    string? SubscriptionTier)
{
    // Keep period and plan metadata the other billing surface did not publish. The usage
    // percent always stays with the surface that produced this snapshot, so an unknown
    // percent is never backfilled from another response.
    public GrokBillingSnapshot Completing(GrokBillingSnapshot other)
    {
        return new GrokBillingSnapshot(
            UsedPercent,
            ResetAt ?? other.ResetAt,
            PeriodMinutes ?? other.PeriodMinutes,
            SubscriptionTier ?? other.SubscriptionTier);
    }
}
