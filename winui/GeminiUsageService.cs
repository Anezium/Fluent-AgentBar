using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluentAgentBar;

// OAuth client the Gemini CLI uses for its installed-app flow. Google ships it
// inside the npm package rather than issuing one per integration, so the only
// way to refresh a CLI login is to read the constants back out of the package
// (or let the user override them through the environment). Nothing is
// hardcoded here on purpose: CodexBar does not hardcode them either.
internal sealed record GeminiOAuthClient(string ClientId, string ClientSecret);

internal sealed record GeminiProcessResult(int ExitCode, string StandardOutput, string StandardError = "");

// Gemini quota, from the two sources that still answer.
//
// Primary: the Antigravity CLI (`agy -p /usage`). Google shut down Gemini CLI
// OAuth for consumer accounts in June 2026, so for individual AI Pro/Ultra
// users Antigravity is the only live source of Gemini quota.
//
// Fallback: the Gemini CLI OAuth path (oauth_creds.json -> loadCodeAssist ->
// retrieveUserQuota), which Workspace, education and Code Assist
// Standard/Enterprise accounts are still served by. Ported from CodexBar's
// AntigravityStatusProbe and GeminiStatusProbe.
internal sealed class GeminiUsageService : IDisposable
{
    private const string CredentialsFileName = "oauth_creds.json";
    private const string SettingsFileName = "settings.json";
    private const string TokenRefreshUrl = "https://oauth2.googleapis.com/token";
    private const string LoadCodeAssistUrl = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private const string QuotaUrl = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    private const string ProjectsUrl = "https://cloudresourcemanager.googleapis.com/v1/projects";
    private const string LoadCodeAssistBody = """{"metadata":{"ideType":"GEMINI_CLI","pluginType":"GEMINI"}}""";
    private const string OAuth2FileName = "dist/src/code_assist/oauth2.js";

    private const string AntigravityBinaryEnvironmentVariable = "AGY_PATH";
    private const string AntigravityDisableAutoUpdateEnvironmentVariable = "AGY_CLI_DISABLE_AUTO_UPDATE";
    private const string AntigravityPlan = "Antigravity";
    private const string GeminiModelsGroupName = "Gemini Models";

    // Earlier agy builds turned unsupported slash commands into model prompts,
    // which would spend quota instead of reporting it.
    private static readonly Version MinimumAntigravityVersion = new(1, 1, 11);

    // Google's June 2026 shutdown of Gemini CLI OAuth for consumer accounts.
    // Re-authenticating does not help, so this is a plain failure, not a login prompt.
    private const string ConsumerTierDeprecatedMessage =
        "Google no longer serves Gemini CLI quota for individual AI Pro/Ultra accounts. " +
        "Sign in to Antigravity (run 'agy') to keep seeing Gemini usage.";

    private const string OAuthClientUnavailableMessage =
        "Could not read the Gemini CLI OAuth client. Install or update the Gemini CLI, " +
        "or set GEMINI_OAUTH_CLIENT_ID and GEMINI_OAUTH_CLIENT_SECRET.";

    private const string NoUsableSourceMessage =
        "No Gemini usage source found. Sign in to Antigravity (run 'agy'), or sign in with the Gemini CLI.";

    private const string AntigravityUpdateRequiredMessage =
        "The Antigravity CLI is out of date. Run 'agy update', then refresh.";

    private static readonly TimeSpan MinimumFetchInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CacheFallbackWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan OAuthClientProbeInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AccessTokenSkew = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AntigravityVersionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AntigravityUsageTimeout = TimeSpan.FromSeconds(100);

    // Bounds the bundle scan so a pathological node_modules tree cannot stall a refresh.
    private const long MaximumScannedFileBytes = 64L * 1024 * 1024;
    private const int MaximumScannedBundleFiles = 40;
    private const int MaximumPackageRootAscents = 8;
    private const int MaximumProcessOutputChars = 1024 * 1024;

    private static readonly Regex OAuthClientIdPattern = new(
        """(?:const|let|var)?\s*OAUTH_CLIENT_ID\s*=\s*['"]([\w\-.]+)['"]\s*;""",
        RegexOptions.CultureInvariant);

    private static readonly Regex OAuthClientSecretPattern = new(
        """(?:const|let|var)?\s*OAUTH_CLIENT_SECRET\s*=\s*['"]([\w\-]+)['"]\s*;""",
        RegexOptions.CultureInvariant);

    private static readonly Regex AntigravityVersionPattern = new(
        @"^(\d+)\.(\d+)\.(\d+)$",
        RegexOptions.CultureInvariant);

    // Wording Antigravity uses when the CLI has no usable login. Everything else
    // (eligibility checks, 503s) is treated as a transient failure.
    private static readonly string[] LoginRequiredMarkers =
    [
        "not logged in",
        "not signed in",
        "please log in",
        "please sign in",
        "log in to",
        "sign in to",
        "login required",
        "unauthenticated",
        "unauthorized",
        "no credentials",
        "credentials expired"
    ];

    // Wording that means the installed agy is too old to be served. Auto-update is
    // disabled for the runs made here, so the user has to update it by hand.
    private static readonly string[] UpdateRequiredMarkers =
    [
        "agy update",
        "please update",
        "please upgrade",
        "update required",
        "upgrade required",
        "out of date",
        "outdated",
        "unsupported version",
        "minimum version"
    ];

    private readonly HttpClient _httpClient;
    private readonly Func<GeminiOAuthClient?> _oauthClientResolver;
    private readonly Func<string?> _antigravityBinaryResolver;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<GeminiProcessResult>> _processRunner;
    private readonly Func<string?> _antigravityCredentialReader;
    private readonly ConcurrentDictionary<string, GeminiUsageState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _oauthClientSync = new();
    private GeminiOAuthClient? _cachedOAuthClient;
    private DateTimeOffset _nextOAuthClientProbe = DateTimeOffset.MinValue;

    public GeminiUsageService()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, null, null, null)
    {
    }

    internal GeminiUsageService(HttpClient httpClient)
        : this(httpClient, null, null, null)
    {
    }

    internal GeminiUsageService(
        HttpClient httpClient,
        Func<GeminiOAuthClient?>? oauthClientResolver,
        Func<string?>? antigravityBinaryResolver,
        Func<ProcessStartInfo, CancellationToken, Task<GeminiProcessResult>>? processRunner,
        Func<string?>? antigravityCredentialReader = null)
    {
        _httpClient = httpClient;
        _oauthClientResolver = oauthClientResolver ?? ResolveOAuthClient;
        _antigravityBinaryResolver = antigravityBinaryResolver ?? ResolveAntigravityBinary;
        _processRunner = processRunner ?? RunProcessAsync;
        _antigravityCredentialReader = antigravityCredentialReader
            ?? (() => WindowsCredentialStore.ReadGenericBlob(AntigravityCredentialTarget));
    }

    // The Antigravity CLI keeps {"token":{...},"auth_method":"...","id_token":"<jwt>"}
    // in the Windows Credential Manager; the id_token names the signed-in account.
    internal const string AntigravityCredentialTarget = "gemini:antigravity";

    internal static string? ReadAntigravityAccountEmail(string? credentialJson)
    {
        if (string.IsNullOrWhiteSpace(credentialJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(credentialJson);
            return ExtractClaims(ReadString(document.RootElement, "id_token")).Email;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<ProviderUsageSnapshot> FetchAsync(string home, CancellationToken cancellationToken)
    {
        string normalizedHome = NormalizeHome(home);
        GeminiUsageState state = _states.GetOrAdd(normalizedHome, _ => new GeminiUsageState());

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (state.CachedSnapshot is not null && now - state.LastSuccessfulFetch < MinimumFetchInterval)
            {
                return state.CachedSnapshot;
            }

            if (now < state.NextAllowedFetch)
            {
                ProviderUsageSnapshot? cached = CachedSnapshotWithinFallbackWindow(state, now);
                if (cached is not null)
                {
                    return cached;
                }

                throw new InvalidOperationException(
                    state.LastFailureMessage ?? "Gemini usage is temporarily unavailable.");
            }

            try
            {
                ProviderUsageSnapshot snapshot = await FetchSnapshotAsync(normalizedHome, state, cancellationToken);
                state.CachedSnapshot = snapshot;
                state.LastSuccessfulFetch = DateTimeOffset.UtcNow;
                state.NextAllowedFetch = DateTimeOffset.MinValue;
                state.Backoff = TimeSpan.Zero;
                state.LastFailureMessage = null;
                return snapshot;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ProviderLoginRequiredException)
            {
                // The stored credentials are dead; a stale snapshot would hide that.
                state.CachedSnapshot = null;
                state.AccessToken = null;
                state.IdToken = null;
                throw;
            }
            catch (ProviderUpdateRequiredException)
            {
                // Only the user can fix an outdated CLI; a stale snapshot or a backoff would hide that.
                state.CachedSnapshot = null;
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                RegisterFailure(state, ex);
                ProviderUsageSnapshot? cached = CachedSnapshotWithinFallbackWindow(state, DateTimeOffset.UtcNow);
                if (cached is null)
                {
                    throw;
                }

                return cached;
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    // Antigravity is the primary source. The Gemini CLI OAuth path is kept for the
    // accounts Google still serves, and is also tried when agy is installed but
    // cannot produce a report.
    private async Task<ProviderUsageSnapshot> FetchSnapshotAsync(
        string home,
        GeminiUsageState state,
        CancellationToken cancellationToken)
    {
        string? antigravityBinary = _antigravityBinaryResolver();
        bool hasGeminiCredentials = File.Exists(Path.Combine(home, CredentialsFileName));
        ProviderUpdateRequiredException? outdatedAntigravity = null;

        if (!string.IsNullOrWhiteSpace(antigravityBinary))
        {
            try
            {
                return await FetchAntigravitySnapshotAsync(antigravityBinary!, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ProviderUpdateRequiredException ex) when (hasGeminiCredentials)
            {
                outdatedAntigravity = ex;
            }
            catch (Exception ex) when (hasGeminiCredentials)
            {
                Debug.WriteLine(ex);
            }
        }

        if (!hasGeminiCredentials)
        {
            throw new ProviderLoginRequiredException(NoUsableSourceMessage);
        }

        try
        {
            EnsureSupportedAuthType(home);
            GeminiCredentials credentials = LoadCredentials(home);
            return await FetchGeminiOAuthSnapshotAsync(home, state, credentials, cancellationToken);
        }
        catch (Exception ex) when (outdatedAntigravity is not null && !cancellationToken.IsCancellationRequested)
        {
            // The Gemini CLI path could not stand in, so the outdated agy is what the user can act on.
            Debug.WriteLine(ex);
            throw outdatedAntigravity;
        }
    }

    // MARK: Antigravity CLI

    private async Task<ProviderUsageSnapshot> FetchAntigravitySnapshotAsync(
        string binary,
        CancellationToken cancellationToken)
    {
        // The CLI is run from a throwaway directory so it never picks up a real
        // workspace's configuration or writes into one.
        string workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "fluent-agentbar-agy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        try
        {
            string version = (await RunAntigravityAsync(
                binary,
                workingDirectory,
                ["--version"],
                AntigravityVersionTimeout,
                cancellationToken)).StandardOutput.Trim();

            EnsureSupportedAntigravityVersion(version);

            GeminiProcessResult report = await RunAntigravityAsync(
                binary,
                workingDirectory,
                ["-p", "/usage", "--output-format", "json", "--print-timeout", "90s"],
                AntigravityUsageTimeout,
                cancellationToken);

            if (report.ExitCode != 0 &&
                IsUpdateRequiredMessage(report.StandardOutput + "\n" + report.StandardError))
            {
                throw new ProviderUpdateRequiredException(AntigravityUpdateRequiredMessage);
            }

            ProviderUsageSnapshot snapshot = ParseAntigravityUsageReport(report.StandardOutput);
            string? email = ReadAntigravityAccountEmailSafely();
            return string.IsNullOrWhiteSpace(email) ? snapshot : snapshot with { Email = email };
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    private string? ReadAntigravityAccountEmailSafely()
    {
        try
        {
            return ReadAntigravityAccountEmail(_antigravityCredentialReader());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Antigravity credential read failed: {ex.GetType().Name}");
            return null;
        }
    }

    private async Task<GeminiProcessResult> RunAntigravityAsync(
        string binary,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(binary)
        {
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // agy's background updater is detached from this hidden console and starts
        // its own child without CREATE_NO_WINDOW, which flashes a terminal window on
        // refreshes. Updates are left to the user instead (see UpdateRequiredMarkers).
        // agy only honours the literal "true"; "1" is silently ignored.
        startInfo.Environment[AntigravityDisableAutoUpdateEnvironmentVariable] = "true";

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        GeminiProcessResult result;
        try
        {
            result = await _processRunner(startInfo, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The Antigravity CLI did not answer in time.");
        }

        return result;
    }

    internal static void EnsureSupportedAntigravityVersion(string version)
    {
        Match match = AntigravityVersionPattern.Match(version.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out int major) ||
            !int.TryParse(match.Groups[2].Value, out int minor) ||
            !int.TryParse(match.Groups[3].Value, out int patch))
        {
            throw new InvalidOperationException(
                $"Antigravity usage reports require agy {MinimumAntigravityVersion} or later.");
        }

        if (new Version(major, minor, patch) < MinimumAntigravityVersion)
        {
            throw new ProviderUpdateRequiredException(
                $"Antigravity usage reports require agy {MinimumAntigravityVersion} or later. Run 'agy update', then refresh.");
        }
    }

    internal static ProviderUsageSnapshot ParseAntigravityUsageReport(string json)
    {
        using JsonDocument document = ParseJson(json, "The Antigravity CLI usage report is not valid JSON.");
        JsonElement root = document.RootElement;

        string? status = ReadString(root, "status");
        if (!string.Equals(status, "SUCCESS", StringComparison.Ordinal))
        {
            string error = ReadString(root, "error") ?? $"status {status ?? "unknown"}";
            if (IsLoginRequiredMessage(error))
            {
                throw new ProviderLoginRequiredException(
                    "Antigravity is not signed in. Run 'agy' to sign in.");
            }

            if (IsUpdateRequiredMessage(error))
            {
                throw new ProviderUpdateRequiredException(AntigravityUpdateRequiredMessage);
            }

            throw new InvalidOperationException($"The Antigravity CLI usage report failed: {error}");
        }

        if (!root.TryGetProperty("command", out JsonElement command) ||
            command.ValueKind != JsonValueKind.Object ||
            !string.Equals(ReadString(command, "name"), "usage", StringComparison.Ordinal) ||
            !command.TryGetProperty("data", out JsonElement data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The Antigravity CLI did not return a usage report.");
        }

        List<ProviderQuotaGroup> groups = ParseAntigravityGroups(data);
        if (groups.Count == 0)
        {
            throw new InvalidOperationException("The Antigravity CLI usage report has no known quota.");
        }

        // The Gemini bucket is what this provider is about, so it drives the
        // taskbar widget's primary and secondary bars.
        groups = groups
            .OrderBy(group => string.Equals(group.Name, GeminiModelsGroupName, StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1)
            .ToList();

        string plan = ReadString(data, "tier") ?? ReadString(data, "plan") ?? AntigravityPlan;
        return new ProviderUsageSnapshot(plan, string.Empty, groups);
    }

    private static List<ProviderQuotaGroup> ParseAntigravityGroups(JsonElement data)
    {
        List<ProviderQuotaGroup> groups = [];
        if (!data.TryGetProperty("groups", out JsonElement groupElements) ||
            groupElements.ValueKind != JsonValueKind.Array)
        {
            return groups;
        }

        foreach (JsonElement groupElement in groupElements.EnumerateArray())
        {
            if (groupElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            List<ProviderQuotaWindow> windows = ParseAntigravityWindows(groupElement);
            if (windows.Count == 0)
            {
                continue;
            }

            string name = Trimmed(ReadString(groupElement, "display_name") ?? ReadString(groupElement, "name"))
                ?? "Quota";
            groups.Add(new ProviderQuotaGroup(name, windows));
        }

        return groups;
    }

    private static List<ProviderQuotaWindow> ParseAntigravityWindows(JsonElement groupElement)
    {
        List<ProviderQuotaWindow> windows = [];
        if (!groupElement.TryGetProperty("buckets", out JsonElement buckets) ||
            buckets.ValueKind != JsonValueKind.Array)
        {
            return windows;
        }

        List<ProviderQuotaWindow> others = [];
        ProviderQuotaWindow? weekly = null;
        ProviderQuotaWindow? fiveHour = null;

        foreach (JsonElement bucket in buckets.EnumerateArray())
        {
            if (bucket.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? bucketId = Trimmed(ReadString(bucket, "bucket_id") ?? ReadString(bucket, "id"));
            if (bucketId is null)
            {
                continue;
            }

            if (bucket.TryGetProperty("disabled", out JsonElement disabled) &&
                disabled.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            double? fraction = ReadRemainingFraction(bucket);
            if (fraction is null)
            {
                continue;
            }

            string displayName = Trimmed(ReadString(bucket, "display_name") ?? ReadString(bucket, "name"))
                ?? bucketId;
            string window = ReadString(bucket, "window") ?? string.Empty;
            DateTimeOffset? resetAt = ParseTimestamp(ReadString(bucket, "reset_time"));

            if (string.Equals(window, "5h", StringComparison.OrdinalIgnoreCase) ||
                bucketId.EndsWith("-5h", StringComparison.OrdinalIgnoreCase))
            {
                fiveHour = ToWindow("5h", fraction.Value, resetAt);
            }
            else if (string.Equals(window, "weekly", StringComparison.OrdinalIgnoreCase) ||
                bucketId.EndsWith("-weekly", StringComparison.OrdinalIgnoreCase))
            {
                weekly = ToWindow("Weekly", fraction.Value, resetAt);
            }
            else
            {
                others.Add(ToWindow(displayName, fraction.Value, resetAt));
            }
        }

        // [0] and [1] become the widget's primary and secondary bars, so the
        // short window has to come first.
        if (fiveHour is not null)
        {
            windows.Add(fiveHour);
        }

        if (weekly is not null)
        {
            windows.Add(weekly);
        }

        windows.AddRange(others);
        return windows;
    }

    private static double? ReadRemainingFraction(JsonElement bucket)
    {
        if (bucket.TryGetProperty("remaining_fraction", out JsonElement direct) &&
            direct.ValueKind == JsonValueKind.Number &&
            direct.TryGetDouble(out double fraction))
        {
            return fraction;
        }

        if (bucket.TryGetProperty("remaining", out JsonElement remaining) &&
            remaining.ValueKind == JsonValueKind.Object &&
            remaining.TryGetProperty("remaining_fraction", out JsonElement nested) &&
            nested.ValueKind == JsonValueKind.Number &&
            nested.TryGetDouble(out double nestedFraction))
        {
            return nestedFraction;
        }

        return null;
    }

    private static bool IsLoginRequiredMessage(string message)
    {
        string normalized = message.ToLowerInvariant();
        return LoginRequiredMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static bool IsUpdateRequiredMessage(string message)
    {
        string normalized = message.ToLowerInvariant();
        return UpdateRequiredMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    internal static string? ResolveAntigravityBinary()
    {
        string? fromEnvironment = Trimmed(Environment.GetEnvironmentVariable(AntigravityBinaryEnvironmentVariable));
        if (fromEnvironment is not null && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        foreach (string fileName in (string[])["agy.exe", "agy.cmd", "agy.bat", "agy"])
        {
            string? resolved = ResolveExecutableOnPath(fileName);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        string installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "agy",
            "bin",
            "agy.exe");
        return File.Exists(installed) ? installed : null;
    }

    private static async Task<GeminiProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start the Antigravity CLI.");
        }

        try
        {
            // The CLI must never wait on a console it does not have.
            process.StandardInput.Close();

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(standardOutput, standardError);

            string output = standardOutput.Result;
            if (output.Length > MaximumProcessOutputChars)
            {
                output = output[..MaximumProcessOutputChars];
            }

            string error = standardError.Result;
            if (error.Length > MaximumProcessOutputChars)
            {
                error = error[..MaximumProcessOutputChars];
            }

            return new GeminiProcessResult(process.ExitCode, output, error);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
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

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    // MARK: Gemini CLI OAuth pipeline

    private async Task<ProviderUsageSnapshot> FetchGeminiOAuthSnapshotAsync(
        string home,
        GeminiUsageState state,
        GeminiCredentials credentials,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string? accessToken = null;
        string? idToken = credentials.IdToken;

        bool storedTokenUsable = !string.IsNullOrWhiteSpace(credentials.AccessToken) &&
            (credentials.ExpiresAt is null || credentials.ExpiresAt > now);
        if (storedTokenUsable)
        {
            accessToken = credentials.AccessToken;
        }
        else if (!string.IsNullOrWhiteSpace(state.AccessToken) &&
            state.AccessTokenExpiry > now + AccessTokenSkew)
        {
            // Survives a creds file we could not write back to (read-only, locked).
            accessToken = state.AccessToken;
            idToken = state.IdToken ?? idToken;
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
            {
                throw new ProviderLoginRequiredException(
                    "Gemini credentials have no refresh token. Run 'gemini' to sign in again.");
            }

            GeminiRefreshResult refreshed = await RefreshAccessTokenAsync(
                home,
                credentials.RefreshToken!,
                cancellationToken);

            accessToken = refreshed.AccessToken;
            idToken = refreshed.IdToken ?? idToken;
            state.AccessToken = refreshed.AccessToken;
            state.IdToken = idToken;
            state.AccessTokenExpiry = refreshed.ExpiresAt;
        }

        GeminiTokenClaims claims = ExtractClaims(idToken);

        GeminiCodeAssistStatus status = await LoadCodeAssistStatusAsync(
            accessToken!,
            claims.HostedDomain,
            cancellationToken);

        string? projectId = status.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            projectId = await DiscoverProjectIdAsync(accessToken!, cancellationToken);
        }

        IReadOnlyList<GeminiModelQuota> quotas = await FetchModelQuotasAsync(
            accessToken!,
            projectId,
            status,
            cancellationToken);

        string plan = ResolveAccountPlan(status.Tier, claims.HostedDomain, status.PaidTierName);
        return new ProviderUsageSnapshot(plan, claims.Email ?? string.Empty, BuildGeminiGroups(quotas));
    }

    private async Task<GeminiRefreshResult> RefreshAccessTokenAsync(
        string home,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        GeminiOAuthClient? client = _oauthClientResolver();
        if (client is null)
        {
            throw new InvalidOperationException(OAuthClientUnavailableMessage);
        }

        using FormUrlEncodedContent content = new(new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        });

        using HttpRequestMessage request = new(HttpMethod.Post, TokenRefreshUrl) { Content = content };
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            ThrowIfConsumerTierDeprecated(body);

            // The body can carry token material; only the status code is safe to surface.
            throw new ProviderLoginRequiredException(
                $"Gemini token refresh failed with HTTP {(int)response.StatusCode}.");
        }

        using JsonDocument document = ParseJson(body, "The Gemini token refresh response is not valid JSON.");
        JsonElement root = document.RootElement;
        string? accessToken = ReadString(root, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("The Gemini token refresh response has no access token.");
        }

        string? idToken = ReadString(root, "id_token");
        DateTimeOffset? expiresAt = null;
        if (root.TryGetProperty("expires_in", out JsonElement expiresIn) &&
            expiresIn.ValueKind == JsonValueKind.Number &&
            expiresIn.TryGetDouble(out double seconds))
        {
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }

        PersistRefreshedCredentials(home, accessToken!, idToken, expiresAt);
        return new GeminiRefreshResult(accessToken!, idToken, expiresAt);
    }

    private async Task<GeminiCodeAssistStatus> LoadCodeAssistStatusAsync(
        string accessToken,
        string? hostedDomain,
        CancellationToken cancellationToken)
    {
        string body;
        HttpStatusCode statusCode;
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, LoadCodeAssistUrl)
            {
                Content = new StringContent(LoadCodeAssistBody, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            statusCode = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Tier and project are enrichment: the quota call still works without them.
            Debug.WriteLine(ex);
            return GeminiCodeAssistStatus.Empty;
        }

        if (statusCode != HttpStatusCode.OK)
        {
            ThrowIfConsumerTierDeprecated(body);
            return GeminiCodeAssistStatus.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return ParseCodeAssistStatus(document.RootElement, hostedDomain);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine(ex);
            return GeminiCodeAssistStatus.Empty;
        }
    }

    private async Task<string?> DiscoverProjectIdAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, ProjectsUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);
            return FindGeminiProjectId(document.RootElement);
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
    }

    private async Task<IReadOnlyList<GeminiModelQuota>> FetchModelQuotasAsync(
        string accessToken,
        string? projectId,
        GeminiCodeAssistStatus status,
        CancellationToken cancellationToken)
    {
        string requestBody = string.IsNullOrWhiteSpace(projectId)
            ? "{}"
            : JsonSerializer.Serialize(new Dictionary<string, string> { ["project"] = projectId! });

        using HttpRequestMessage request = new(HttpMethod.Post, QuotaUrl)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            ThrowIfConsumerTierDeprecated(body);
            throw new ProviderLoginRequiredException("Gemini rejected the access token. Run 'gemini' to sign in again.");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            ThrowIfConsumerTierDeprecated(body);

            // A quota 403 carries no migration wording, so only treat it as the consumer
            // shutdown when loadCodeAssist already flagged this client as unsupported and
            // the account is not on a licensed tier.
            if (response.StatusCode == HttpStatusCode.Forbidden &&
                status.IsConsumerClientUnsupported &&
                status.Tier != GeminiUserTier.Standard)
            {
                throw new InvalidOperationException(ConsumerTierDeprecatedMessage);
            }

            throw new InvalidOperationException($"The Gemini quota request failed with HTTP {(int)response.StatusCode}.");
        }

        using JsonDocument document = ParseJson(body, "The Gemini quota response is not valid JSON.");
        return ParseModelQuotas(document.RootElement);
    }

    internal static IReadOnlyList<GeminiModelQuota> ParseModelQuotas(JsonElement root)
    {
        if (!root.TryGetProperty("buckets", out JsonElement buckets) ||
            buckets.ValueKind != JsonValueKind.Array ||
            buckets.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("The Gemini quota response has no buckets.");
        }

        // Several buckets share a model (input/output token types); the tightest one wins.
        Dictionary<string, GeminiModelQuota> lowestPerModel = new(StringComparer.Ordinal);
        foreach (JsonElement bucket in buckets.EnumerateArray())
        {
            string? modelId = ReadString(bucket, "modelId");
            if (string.IsNullOrWhiteSpace(modelId))
            {
                continue;
            }

            if (!bucket.TryGetProperty("remainingFraction", out JsonElement fractionElement) ||
                fractionElement.ValueKind != JsonValueKind.Number ||
                !fractionElement.TryGetDouble(out double fraction))
            {
                continue;
            }

            DateTimeOffset? resetAt = ParseTimestamp(ReadString(bucket, "resetTime"));
            GeminiModelQuota candidate = new(modelId!, fraction, resetAt);
            if (!lowestPerModel.TryGetValue(modelId!, out GeminiModelQuota? existing) ||
                candidate.RemainingFraction < existing.RemainingFraction)
            {
                lowestPerModel[modelId!] = candidate;
            }
        }

        if (lowestPerModel.Count == 0)
        {
            throw new InvalidOperationException("The Gemini quota response has no usable buckets.");
        }

        return lowestPerModel.Values
            .OrderBy(quota => quota.ModelId, StringComparer.Ordinal)
            .ToList();
    }

    // Pro first, then Flash, then Flash Lite, then anything else Google reports.
    internal static IReadOnlyList<ProviderQuotaGroup> BuildGeminiGroups(IReadOnlyList<GeminiModelQuota> quotas)
    {
        List<ProviderQuotaWindow> windows = [];
        List<GeminiModelQuota> others = [];
        List<GeminiModelQuota> flashLite = [];
        List<GeminiModelQuota> flash = [];
        List<GeminiModelQuota> pro = [];

        foreach (GeminiModelQuota quota in quotas)
        {
            string modelId = quota.ModelId.ToLowerInvariant();
            if (modelId.Contains("flash-lite", StringComparison.Ordinal))
            {
                flashLite.Add(quota);
            }
            else if (modelId.Contains("flash", StringComparison.Ordinal))
            {
                flash.Add(quota);
            }
            else if (modelId.Contains("pro", StringComparison.Ordinal))
            {
                pro.Add(quota);
            }
            else
            {
                others.Add(quota);
            }
        }

        AddLowest(windows, pro, "Pro");
        AddLowest(windows, flash, "Flash");
        AddLowest(windows, flashLite, "Flash Lite");
        foreach (GeminiModelQuota quota in others)
        {
            windows.Add(ToWindow(quota.ModelId, quota.RemainingFraction, quota.ResetAt));
        }

        // Single unnamed group: the flyout only draws a header when several exist.
        return windows.Count == 0 ? [] : [new ProviderQuotaGroup(string.Empty, windows)];
    }

    private static void AddLowest(List<ProviderQuotaWindow> windows, List<GeminiModelQuota> quotas, string label)
    {
        GeminiModelQuota? lowest = quotas
            .OrderBy(quota => quota.RemainingFraction)
            .FirstOrDefault();
        if (lowest is not null)
        {
            windows.Add(ToWindow(label, lowest.RemainingFraction, lowest.ResetAt));
        }
    }

    private static ProviderQuotaWindow ToWindow(string label, double remainingFraction, DateTimeOffset? resetAt)
    {
        int remainingPercent = (int)Math.Round(
            Math.Clamp(remainingFraction, 0d, 1d) * 100d,
            MidpointRounding.AwayFromZero);
        return new ProviderQuotaWindow(label, remainingPercent, resetAt);
    }

    internal static GeminiCodeAssistStatus ParseCodeAssistStatus(JsonElement root, string? hostedDomain)
    {
        string? projectId = ReadProjectId(root);
        string? paidTierName = ReadPaidTierName(root);
        bool consumerClientUnsupported = paidTierName is null &&
            string.IsNullOrWhiteSpace(hostedDomain) &&
            HasUnsupportedClientIneligibleTier(root);

        string? tierId = null;
        if (root.TryGetProperty("currentTier", out JsonElement currentTier) &&
            currentTier.ValueKind == JsonValueKind.Object)
        {
            tierId = ReadString(currentTier, "id");
        }

        if (string.IsNullOrWhiteSpace(tierId))
        {
            // Google answers the consumer shutdown with HTTP 200, no currentTier, and the
            // consumer tier listed under ineligibleTiers with UNSUPPORTED_CLIENT.
            if (consumerClientUnsupported)
            {
                throw new InvalidOperationException(ConsumerTierDeprecatedMessage);
            }

            return new GeminiCodeAssistStatus(null, projectId, paidTierName, false);
        }

        GeminiUserTier? tier = tierId switch
        {
            "free-tier" => GeminiUserTier.Free,
            "legacy-tier" => GeminiUserTier.Legacy,
            "standard-tier" => GeminiUserTier.Standard,
            _ => null
        };

        return new GeminiCodeAssistStatus(tier, projectId, paidTierName, consumerClientUnsupported);
    }

    private static string? ReadProjectId(JsonElement root)
    {
        if (!root.TryGetProperty("cloudaicompanionProject", out JsonElement project))
        {
            return null;
        }

        string? raw = project.ValueKind switch
        {
            JsonValueKind.String => project.GetString(),
            JsonValueKind.Object => ReadString(project, "id") ?? ReadString(project, "projectId"),
            _ => null
        };

        return Trimmed(raw);
    }

    private static string? ReadPaidTierName(JsonElement root)
    {
        return root.TryGetProperty("paidTier", out JsonElement paidTier) && paidTier.ValueKind == JsonValueKind.Object
            ? Trimmed(ReadString(paidTier, "name"))
            : null;
    }

    private static bool HasUnsupportedClientIneligibleTier(JsonElement root)
    {
        if (!root.TryGetProperty("ineligibleTiers", out JsonElement tiers) ||
            tiers.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement tier in tiers.EnumerateArray())
        {
            if (tier.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (IsConsumerTierDeprecationSignal(ReadString(tier, "reasonCode")) ||
                IsConsumerTierDeprecationSignal(ReadString(tier, "reasonMessage")))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FindGeminiProjectId(JsonElement root)
    {
        if (!root.TryGetProperty("projects", out JsonElement projects) ||
            projects.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement project in projects.EnumerateArray())
        {
            string? projectId = ReadString(project, "projectId");
            if (string.IsNullOrWhiteSpace(projectId))
            {
                continue;
            }

            if (projectId!.StartsWith("gen-lang-client", StringComparison.Ordinal))
            {
                return projectId;
            }

            if (project.TryGetProperty("labels", out JsonElement labels) &&
                labels.ValueKind == JsonValueKind.Object &&
                labels.TryGetProperty("generative-language", out _))
            {
                return projectId;
            }
        }

        return null;
    }

    // paidTier.name is the most specific signal Google gives and outranks currentTier,
    // which still reports free-tier for some paid subscriptions.
    internal static string ResolveAccountPlan(GeminiUserTier? tier, string? hostedDomain, string? paidTierName)
    {
        if (!string.IsNullOrWhiteSpace(paidTierName))
        {
            return paidTierName!;
        }

        return tier switch
        {
            GeminiUserTier.Standard => "Paid",
            // Gemini has been included with Workspace since January 2025, so a free
            // tier plus an hd claim is a Workspace account, not a personal one.
            GeminiUserTier.Free when !string.IsNullOrWhiteSpace(hostedDomain) => "Workspace",
            GeminiUserTier.Free => "Free",
            GeminiUserTier.Legacy => "Legacy",
            _ => string.Empty
        };
    }

    internal static GeminiTokenClaims ExtractClaims(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return new GeminiTokenClaims(null, null);
        }

        string[] parts = idToken!.Split('.');
        if (parts.Length < 2)
        {
            return new GeminiTokenClaims(null, null);
        }

        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            int remainder = payload.Length % 4;
            if (remainder > 0)
            {
                payload += new string('=', 4 - remainder);
            }

            byte[] decoded = Convert.FromBase64String(payload);
            using JsonDocument document = JsonDocument.Parse(decoded);
            return new GeminiTokenClaims(
                ReadString(document.RootElement, "email"),
                ReadString(document.RootElement, "hd"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return new GeminiTokenClaims(null, null);
        }
    }

    // MARK: local credentials

    internal static void EnsureSupportedAuthType(string home)
    {
        string settingsPath = Path.Combine(home, SettingsFileName);
        if (!File.Exists(settingsPath))
        {
            return;
        }

        string? selectedType;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!document.RootElement.TryGetProperty("security", out JsonElement security) ||
                !security.TryGetProperty("auth", out JsonElement auth))
            {
                return;
            }

            selectedType = ReadString(auth, "selectedType");
        }
        catch (Exception ex)
        {
            // An unreadable settings.json is not a reason to block the OAuth path.
            Debug.WriteLine(ex);
            return;
        }

        switch (selectedType)
        {
            case "api-key":
            case "gemini-api-key":
                throw new InvalidOperationException(
                    "Gemini API key auth is not supported. Sign in with a Google account (OAuth) instead.");
            case "vertex-ai":
                throw new InvalidOperationException(
                    "Gemini Vertex AI auth is not supported. Sign in with a Google account (OAuth) instead.");
            default:
                return;
        }
    }

    internal static GeminiCredentials LoadCredentials(string home)
    {
        string credentialsPath = Path.Combine(home, CredentialsFileName);
        if (!File.Exists(credentialsPath))
        {
            throw new ProviderLoginRequiredException(
                "No Gemini credentials found. Run 'gemini' to sign in with a Google account.");
        }

        string contents;
        try
        {
            contents = File.ReadAllText(credentialsPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not read the Gemini credentials file.", ex);
        }

        using JsonDocument document = ParseJson(contents, "The Gemini credentials file is not valid JSON.");
        JsonElement root = document.RootElement;

        string? accessToken = ReadString(root, "access_token");
        string? refreshToken = ReadString(root, "refresh_token");
        if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new ProviderLoginRequiredException(
                "The Gemini credentials hold no tokens. Run 'gemini' to sign in again.");
        }

        DateTimeOffset? expiresAt = null;
        if (root.TryGetProperty("expiry_date", out JsonElement expiry) &&
            expiry.ValueKind == JsonValueKind.Number &&
            expiry.TryGetDouble(out double expiryMilliseconds))
        {
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)expiryMilliseconds);
        }

        return new GeminiCredentials(accessToken, refreshToken, ReadString(root, "id_token"), expiresAt);
    }

    // Mirrors the Gemini CLI: the refreshed token goes back into oauth_creds.json so
    // the CLI and AgentBar stay on the same token. Unknown fields are preserved.
    private static void PersistRefreshedCredentials(
        string home,
        string accessToken,
        string? idToken,
        DateTimeOffset? expiresAt)
    {
        string credentialsPath = Path.Combine(home, CredentialsFileName);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(credentialsPath)) is not JsonObject root)
            {
                return;
            }

            root["access_token"] = accessToken;
            if (!string.IsNullOrWhiteSpace(idToken))
            {
                root["id_token"] = idToken;
            }

            if (expiresAt is not null)
            {
                root["expiry_date"] = expiresAt.Value.ToUnixTimeMilliseconds();
            }

            File.WriteAllText(
                credentialsPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // A locked or read-only creds file must not fail the fetch: the refreshed
            // token is kept in memory for this process instead.
            Debug.WriteLine(ex);
        }
    }

    // MARK: OAuth client discovery

    private GeminiOAuthClient? ResolveOAuthClient()
    {
        GeminiOAuthClient? fromEnvironment = ResolveOAuthClientFromEnvironment();
        if (fromEnvironment is not null)
        {
            return fromEnvironment;
        }

        lock (_oauthClientSync)
        {
            if (_cachedOAuthClient is not null)
            {
                return _cachedOAuthClient;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now < _nextOAuthClientProbe)
            {
                return null;
            }

            _nextOAuthClientProbe = now + OAuthClientProbeInterval;
            _cachedOAuthClient = DiscoverOAuthClient();
            return _cachedOAuthClient;
        }
    }

    private static GeminiOAuthClient? ResolveOAuthClientFromEnvironment()
    {
        string? clientId = Trimmed(Environment.GetEnvironmentVariable("GEMINI_OAUTH_CLIENT_ID"));
        string? clientSecret = Trimmed(Environment.GetEnvironmentVariable("GEMINI_OAUTH_CLIENT_SECRET"));
        if (clientId is not null && clientSecret is not null)
        {
            return new GeminiOAuthClient(clientId, clientSecret);
        }

        string? oauth2JsPath = Trimmed(Environment.GetEnvironmentVariable("GEMINI_OAUTH2_JS_PATH"));
        return oauth2JsPath is null ? null : ReadOAuthClientFromFile(oauth2JsPath);
    }

    private static GeminiOAuthClient? DiscoverOAuthClient()
    {
        foreach (string packageRoot in EnumerateGeminiPackageRoots())
        {
            GeminiOAuthClient? client = TryReadOAuthClientFromPackageRoot(packageRoot);
            if (client is not null)
            {
                return client;
            }
        }

        return null;
    }

    // Windows global-install layouts for @google/gemini-cli, cheapest first.
    private static IEnumerable<string> EnumerateGeminiPackageRoots()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string moduleRoot in EnumerateNodeModuleRoots())
        {
            foreach (string package in (string[])["gemini-cli", "gemini-cli-core"])
            {
                string candidate = Path.Combine(moduleRoot, "@google", package);
                if (Directory.Exists(candidate) && seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        string? fromPath = FindGeminiPackageRootFromPath();
        if (fromPath is not null && seen.Add(fromPath))
        {
            yield return fromPath;
        }
    }

    private static IEnumerable<string> EnumerateNodeModuleRoots()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        List<string> roots =
        [
            Path.Combine(appData, "npm", "node_modules"),
            Path.Combine(localAppData, "npm", "node_modules"),
            Path.Combine(userProfile, ".npm-global", "node_modules"),
            Path.Combine(programFiles, "nodejs", "node_modules")
        ];

        foreach (string root in roots)
        {
            if (Directory.Exists(root))
            {
                yield return root;
            }
        }

        // Last resort: ask npm itself, which covers prefixes we cannot guess (fnm, volta).
        string? npmRoot = RunNpmRootGlobal();
        if (!string.IsNullOrWhiteSpace(npmRoot) && Directory.Exists(npmRoot))
        {
            yield return npmRoot!;
        }
    }

    private static string? FindGeminiPackageRootFromPath()
    {
        string? geminiPath = ResolveExecutableOnPath("gemini.cmd")
            ?? ResolveExecutableOnPath("gemini.exe")
            ?? ResolveExecutableOnPath("gemini.ps1");
        if (geminiPath is null)
        {
            return null;
        }

        DirectoryInfo? directory = new FileInfo(geminiPath).Directory;
        for (int ascent = 0; ascent <= MaximumPackageRootAscents && directory is not null; ascent++)
        {
            if (IsGeminiPackageRoot(directory.FullName))
            {
                return directory.FullName;
            }

            string nested = Path.Combine(directory.FullName, "node_modules", "@google", "gemini-cli");
            if (IsGeminiPackageRoot(nested))
            {
                return nested;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsGeminiPackageRoot(string directory)
    {
        string manifestPath = Path.Combine(directory, "package.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return ReadString(document.RootElement, "name") == "@google/gemini-cli";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    private static string? ResolveExecutableOnPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (string directory in path!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        return null;
    }

    private static string? RunNpmRootGlobal()
    {
        try
        {
            ProcessStartInfo startInfo = new("cmd.exe", "/c npm root -g")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                TryKillProcess(process);
                return null;
            }

            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    internal static GeminiOAuthClient? TryReadOAuthClientFromPackageRoot(string packageRoot)
    {
        string oauth2RelativePath = OAuth2FileName.Replace('/', Path.DirectorySeparatorChar);
        string[] candidates =
        [
            Path.Combine(packageRoot, oauth2RelativePath),
            Path.Combine(packageRoot, "node_modules", "@google", "gemini-cli-core", oauth2RelativePath)
        ];

        foreach (string candidate in candidates)
        {
            GeminiOAuthClient? client = ReadOAuthClientFromFile(candidate);
            if (client is not null)
            {
                return client;
            }
        }

        // Newer releases ship a single rolled-up bundle instead of dist/.
        string bundleRoot = Path.Combine(packageRoot, "bundle");
        if (!Directory.Exists(bundleRoot))
        {
            return null;
        }

        IEnumerable<string> bundleFiles = new[] { Path.Combine(bundleRoot, "gemini.js") }
            .Concat(EnumerateBundleFiles(bundleRoot))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumScannedBundleFiles);

        foreach (string bundleFile in bundleFiles)
        {
            GeminiOAuthClient? client = ReadOAuthClientFromFile(bundleFile);
            if (client is not null)
            {
                return client;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateBundleFiles(string bundleRoot)
    {
        try
        {
            return Directory.EnumerateFiles(bundleRoot, "*.js", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return [];
        }
    }

    private static GeminiOAuthClient? ReadOAuthClientFromFile(string path)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length > MaximumScannedFileBytes)
            {
                return null;
            }

            return ParseOAuthClient(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    internal static GeminiOAuthClient? ParseOAuthClient(string content)
    {
        Match clientIdMatch = OAuthClientIdPattern.Match(content);
        Match clientSecretMatch = OAuthClientSecretPattern.Match(content);
        return clientIdMatch.Success && clientSecretMatch.Success
            ? new GeminiOAuthClient(clientIdMatch.Groups[1].Value, clientSecretMatch.Groups[1].Value)
            : null;
    }

    // MARK: helpers

    internal static bool IsConsumerTierDeprecationSignal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string normalized = text!.ToLowerInvariant();
        if (normalized.Contains("unsupported_client", StringComparison.Ordinal) ||
            normalized.Contains("ineligibletiererror", StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized.Contains("no longer supported", StringComparison.Ordinal) &&
            normalized.Contains("gemini code assist", StringComparison.Ordinal))
        {
            return true;
        }

        return normalized.Contains("migrate", StringComparison.Ordinal) &&
            normalized.Contains("antigravity", StringComparison.Ordinal) &&
            normalized.Contains("gemini", StringComparison.Ordinal);
    }

    private static void ThrowIfConsumerTierDeprecated(string body)
    {
        if (IsConsumerTierDeprecationSignal(body))
        {
            throw new InvalidOperationException(ConsumerTierDeprecatedMessage);
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private static JsonDocument ParseJson(string contents, string failureMessage)
    {
        try
        {
            return JsonDocument.Parse(contents);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(failureMessage, ex);
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? Trimmed(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static ProviderUsageSnapshot? CachedSnapshotWithinFallbackWindow(
        GeminiUsageState state,
        DateTimeOffset now)
    {
        return state.CachedSnapshot is not null && now - state.LastSuccessfulFetch < CacheFallbackWindow
            ? state.CachedSnapshot
            : null;
    }

    private static void RegisterFailure(GeminiUsageState state, Exception exception)
    {
        state.Backoff = state.Backoff <= TimeSpan.Zero
            ? InitialBackoff
            : TimeSpan.FromTicks(Math.Min(state.Backoff.Ticks * 2, MaximumBackoff.Ticks));
        state.NextAllowedFetch = DateTimeOffset.UtcNow + state.Backoff;
        state.LastFailureMessage = exception.Message;
    }

    private static string NormalizeHome(string home)
    {
        string directory = string.IsNullOrWhiteSpace(home)
            ? AppConfigStore.DefaultGeminiHome
            : home;
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

    private sealed class GeminiUsageState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ProviderUsageSnapshot? CachedSnapshot { get; set; }

        public DateTimeOffset LastSuccessfulFetch { get; set; }

        public DateTimeOffset NextAllowedFetch { get; set; }

        public TimeSpan Backoff { get; set; }

        public string? LastFailureMessage { get; set; }

        public string? AccessToken { get; set; }

        public string? IdToken { get; set; }

        public DateTimeOffset? AccessTokenExpiry { get; set; }
    }
}

internal enum GeminiUserTier
{
    Free,
    Legacy,
    Standard
}

internal sealed record GeminiCredentials(
    string? AccessToken,
    string? RefreshToken,
    string? IdToken,
    DateTimeOffset? ExpiresAt);

internal sealed record GeminiRefreshResult(
    string AccessToken,
    string? IdToken,
    DateTimeOffset? ExpiresAt);

internal sealed record GeminiTokenClaims(string? Email, string? HostedDomain);

internal sealed record GeminiModelQuota(
    string ModelId,
    double RemainingFraction,
    DateTimeOffset? ResetAt);

internal sealed record GeminiCodeAssistStatus(
    GeminiUserTier? Tier,
    string? ProjectId,
    string? PaidTierName,
    bool IsConsumerClientUnsupported)
{
    public static GeminiCodeAssistStatus Empty { get; } = new(null, null, null, false);
}
