using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FluentAgentBar;

public sealed class UsageService : IDisposable
{
    private static readonly TimeSpan ProfileTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProcessExitGrace = TimeSpan.FromMilliseconds(750);

    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly object _loopLock = new();
    private readonly object _processLock = new();
    private readonly HashSet<Process> _activeProcesses = [];
    private readonly ClaudeUsageService _claudeUsageService = new();
    private readonly TokenStatsService _tokenStatsService = new();
    private readonly Func<ProfileConfig, CancellationToken, Task<ProfileUsage>> _fetchCodexProfileAsync;
    private readonly Func<ProfileConfig, CancellationToken, Task<ProfileUsage>> _fetchClaudeProfileAsync;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _disposed;

    public event EventHandler? Updated;

    public IReadOnlyList<ProviderUsage> Providers { get; private set; } = MockUsageData.CreateProviders();
    public DateTimeOffset? LastRefresh { get; private set; }

    public UsageService()
    {
        _fetchCodexProfileAsync = FetchCodexProfileAsync;
        _fetchClaudeProfileAsync = FetchClaudeProfileAsync;
        AppConfigStore.Changed += OnConfigChanged;
    }

    internal UsageService(
        Func<ProfileConfig, CancellationToken, Task<ProfileUsage>> fetchCodexProfileAsync,
        Func<ProfileConfig, CancellationToken, Task<ProfileUsage>> fetchClaudeProfileAsync)
    {
        _fetchCodexProfileAsync = fetchCodexProfileAsync;
        _fetchClaudeProfileAsync = fetchClaudeProfileAsync;
        AppConfigStore.Changed += OnConfigChanged;
    }

    public void Start()
    {
        RestartLoop(fetchImmediately: true);
    }

    public Task<IReadOnlyList<ProviderUsage>> FetchAsync()
    {
        return FetchAndPublishAsync(CancellationToken.None);
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        RestartLoop(fetchImmediately: true);
    }

    private void RestartLoop(bool fetchImmediately)
    {
        CancellationTokenSource nextCts = new();
        CancellationTokenSource? previousCts;

        lock (_loopLock)
        {
            if (_disposed)
            {
                nextCts.Dispose();
                return;
            }

            previousCts = _loopCts;
            _loopCts = nextCts;
            _loopTask = Task.Run(() => RunLoopAsync(nextCts.Token, fetchImmediately));
        }

        previousCts?.Cancel();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken, bool fetchImmediately)
    {
        try
        {
            if (fetchImmediately)
            {
                await FetchAndPublishAsync(cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                AppConfig config = AppConfigStore.Load();
                TimeSpan interval = TimeSpan.FromSeconds(Math.Clamp(config.RefreshIntervalSeconds, 30, 3600));
                await Task.Delay(interval, cancellationToken);
                await FetchAndPublishAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task<IReadOnlyList<ProviderUsage>> FetchAndPublishAsync(CancellationToken cancellationToken)
    {
        await _fetchLock.WaitAsync(cancellationToken);
        try
        {
            AppConfig config = AppConfigStore.Load();
            Task<(TokenReport? codex, TokenReport? claude)> tokenStatsTask = ComputeTokenStatsSafelyAsync(config);
            IReadOnlyList<ProviderUsage> providers = await FetchProvidersAsync(config, cancellationToken);
            (TokenReport? codexTokens, TokenReport? claudeTokens) = await tokenStatsTask;
            providers = AttachTokenStats(providers, codexTokens, claudeTokens);

            Providers = providers;
            LastRefresh = DateTimeOffset.Now;
            Updated?.Invoke(this, EventArgs.Empty);
            return providers;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    internal async Task<IReadOnlyList<ProviderUsage>> FetchProvidersAsync(AppConfig config, CancellationToken cancellationToken)
    {
        List<ProviderUsage> providers = [];
        List<ProfileConfig> enabledProfiles = config.Profiles
            .Where(profile => profile.Enabled)
            .ToList();

        List<ProfileUsage> codexProfiles = await FetchProfileGroupAsync(
            enabledProfiles.Where(profile => AppConfigStore.IsProvider(profile, "codex")),
            _fetchCodexProfileAsync,
            MockUsageData.CodexAccentColor,
            cancellationToken);

        if (codexProfiles.Count > 0)
        {
            providers.Add(new ProviderUsage("Codex", "C", codexProfiles));
        }

        List<ProfileUsage> claudeProfiles = await FetchProfileGroupAsync(
            enabledProfiles.Where(profile => AppConfigStore.IsProvider(profile, "claude")),
            _fetchClaudeProfileAsync,
            MockUsageData.ClaudeAccentColor,
            cancellationToken);

        if (claudeProfiles.Count > 0)
        {
            providers.Add(new ProviderUsage("Claude", "A", claudeProfiles));
        }

        return providers;
    }

    private static async Task<List<ProfileUsage>> FetchProfileGroupAsync(
        IEnumerable<ProfileConfig> profiles,
        Func<ProfileConfig, CancellationToken, Task<ProfileUsage>> fetchAsync,
        Windows.UI.Color unavailableAccentColor,
        CancellationToken cancellationToken)
    {
        List<ProfileConfig> orderedProfiles = profiles.ToList();
        cancellationToken.ThrowIfCancellationRequested();

        Task<ProfileUsage>[] tasks = orderedProfiles
            .Select(profile => FetchProfileSafelyAsync(profile, fetchAsync, unavailableAccentColor, cancellationToken))
            .ToArray();

        ProfileUsage[] usages = await Task.WhenAll(tasks);
        return [.. usages];
    }

    private static async Task<ProfileUsage> FetchProfileSafelyAsync(
        ProfileConfig profile,
        Func<ProfileConfig, CancellationToken, Task<ProfileUsage>> fetchAsync,
        Windows.UI.Color unavailableAccentColor,
        CancellationToken cancellationToken)
    {
        try
        {
            return await fetchAsync(profile, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return Unavailable(profile, unavailableAccentColor);
        }
    }

    private async Task<(TokenReport? codex, TokenReport? claude)> ComputeTokenStatsSafelyAsync(AppConfig config)
    {
        try
        {
            return await _tokenStatsService.ComputeAsync(config);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return (null, null);
        }
    }

    private static IReadOnlyList<ProviderUsage> AttachTokenStats(
        IReadOnlyList<ProviderUsage> providers,
        TokenReport? codexTokens,
        TokenReport? claudeTokens)
    {
        List<ProviderUsage> updatedProviders = [];
        foreach (ProviderUsage provider in providers)
        {
            TokenReport? report = null;
            if (string.Equals(provider.Name, "Codex", StringComparison.OrdinalIgnoreCase))
            {
                report = codexTokens;
            }
            else if (string.Equals(provider.Name, "Claude", StringComparison.OrdinalIgnoreCase))
            {
                report = claudeTokens;
            }

            updatedProviders.Add(report is null
                ? provider
                : provider with { Tokens = report.Today, History = report.Daily });
        }

        return updatedProviders;
    }

    private async Task<ProfileUsage> FetchCodexProfileAsync(ProfileConfig profile, CancellationToken cancellationToken)
    {
        string codexHome = Environment.ExpandEnvironmentVariables(profile.Home);

        try
        {
            EnsureCodexProfileHome(codexHome);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return Unavailable(profile);
        }

        List<string> stdoutLines = [];
        StringBuilder stderr = new();
        TaskCompletionSource<bool> rateLimitsResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using Process process = new()
        {
            StartInfo = CreateCodexAppServerStartInfo(codexHome),
            EnableRaisingEvents = true
        };

        try
        {
            process.Start();
            RegisterProcess(process);

            Task stdoutTask = ReadStdoutLinesAsync(process, stdoutLines, rateLimitsResponse);
            Task stderrTask = ReadStderrAsync(process, stderr);

            await SendCodexRequestsAsync(process, cancellationToken);

            Task timeoutTask = Task.Delay(ProfileTimeout, cancellationToken);
            Task exitTask = process.WaitForExitAsync(cancellationToken);
            Task completed = await Task.WhenAny(rateLimitsResponse.Task, exitTask, timeoutTask);

            if (cancellationToken.IsCancellationRequested)
            {
                KillProcess(process);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (completed == timeoutTask)
            {
                KillProcess(process);
                return Unavailable(profile);
            }

            CloseStandardInput(process);

            if (!process.HasExited)
            {
                Task gracefulExit = process.WaitForExitAsync(CancellationToken.None);
                if (await Task.WhenAny(gracefulExit, Task.Delay(ProcessExitGrace)) != gracefulExit)
                {
                    KillProcess(process);
                }
            }

            await WaitForReaderAsync(stdoutTask);
            await WaitForReaderAsync(stderrTask);

            if (stdoutLines.Count == 0)
            {
                Debug.WriteLine(stderr.ToString());
                return Unavailable(profile);
            }

            return ParseCodexProfile(profile, stdoutLines);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillProcess(process);
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            KillProcess(process);
            return Unavailable(profile);
        }
        finally
        {
            UnregisterProcess(process);
        }
    }

    private async Task<ProfileUsage> FetchClaudeProfileAsync(ProfileConfig profile, CancellationToken cancellationToken)
    {
        ProfileUsage usage = await _claudeUsageService.FetchAsync(profile.Home, cancellationToken);
        return usage with
        {
            Label = profile.Label,
            Provider = profile.Provider,
            Home = profile.Home
        };
    }

    private static ProcessStartInfo CreateCodexAppServerStartInfo(string codexHome)
    {
        string cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        ProcessStartInfo startInfo = new()
        {
            FileName = File.Exists(cmdPath) ? cmdPath : "cmd.exe",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // BOM-less UTF-8 is required: Encoding.UTF8 emits a BOM on the
            // first write, which corrupts the initialize line and leaves the
            // app-server permanently uninitialized ("Not initialized" errors).
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        startInfo.ArgumentList.Add("/D");
        startInfo.ArgumentList.Add("/S");
        startInfo.ArgumentList.Add("/C");
        startInfo.ArgumentList.Add("codex -s read-only -a untrusted app-server");
        startInfo.Environment["CODEX_HOME"] = codexHome;
        return startInfo;
    }

    private static async Task SendCodexRequestsAsync(Process process, CancellationToken cancellationToken)
    {
        process.StandardInput.NewLine = "\n";
        await process.StandardInput.WriteLineAsync(
            "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"Fluent AgentBar\",\"version\":\"0.1.0\"}}}"
                .AsMemory(),
            cancellationToken);
        await process.StandardInput.WriteLineAsync(
            "{\"method\":\"initialized\",\"params\":{}}".AsMemory(),
            cancellationToken);
        await process.StandardInput.WriteLineAsync(
            "{\"id\":2,\"method\":\"account/read\",\"params\":{\"refreshToken\":false}}".AsMemory(),
            cancellationToken);
        await process.StandardInput.WriteLineAsync(
            "{\"id\":3,\"method\":\"account/rateLimits/read\"}".AsMemory(),
            cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task ReadStdoutLinesAsync(
        Process process,
        List<string> stdoutLines,
        TaskCompletionSource<bool> rateLimitsResponse)
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            lock (stdoutLines)
            {
                stdoutLines.Add(trimmed);
            }

            if (IsJsonRpcResponseLine(trimmed, 3))
            {
                rateLimitsResponse.TrySetResult(true);
            }
        }
    }

    private static async Task ReadStderrAsync(Process process, StringBuilder stderr)
    {
        string text = await process.StandardError.ReadToEndAsync();
        if (text.Length > 0)
        {
            stderr.Append(text);
        }
    }

    private static async Task WaitForReaderAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }
    }

    private static void CloseStandardInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch
        {
        }
    }

    private static void EnsureCodexProfileHome(string codexHome)
    {
        Directory.CreateDirectory(codexHome);
        string configPath = Path.Combine(codexHome, "config.toml");
        if (File.Exists(configPath))
        {
            return;
        }

        File.WriteAllText(
            configPath,
            "cli_auth_credentials_store = \"file\"" + Environment.NewLine +
            "service_tier = \"fast\"" + Environment.NewLine);
    }

    internal static ProfileUsage ParseCodexProfile(ProfileConfig profile, IReadOnlyList<string> stdoutLines)
    {
        string email = string.Empty;
        string plan = string.Empty;
        int? remainingPercent = null;
        int? weeklyPercent = null;
        DateTimeOffset? primaryResetAt = null;
        DateTimeOffset? weeklyResetAt = null;
        string primaryQuotaLabel = "5h";
        string weeklyQuotaLabel = "Weekly";
        List<QuotaGroupUsage> quotaGroups = [];
        bool rateLimitError = false;

        foreach (string line in stdoutLines)
        {
            using JsonDocument? document = TryParseJson(line);
            if (document is null)
            {
                continue;
            }

            JsonElement root = document.RootElement;
            int? id = GetTopLevelId(root);
            if (id == 2)
            {
                string? accountEmail = FindString(root, "email");
                string? planType = FindString(root, "planType", "plan_type");
                if (!string.IsNullOrWhiteSpace(accountEmail))
                {
                    email = accountEmail;
                }

                if (!string.IsNullOrWhiteSpace(planType))
                {
                    plan = planType;
                }
            }
            else if (id == 3)
            {
                if (HasTopLevelProperty(root, "error") ||
                    !TryGetProperty(root, "result", out JsonElement result))
                {
                    rateLimitError = true;
                    continue;
                }

                string baseLimitId = string.Empty;
                if (TryGetProperty(result, "rateLimits", out JsonElement rateLimits) &&
                    rateLimits.ValueKind == JsonValueKind.Object)
                {
                    baseLimitId = FindString(rateLimits, "limitId", "limit_id") ?? string.Empty;
                    string? rateLimitPlan = FindString(rateLimits, "planType", "plan_type");
                    if (!string.IsNullOrWhiteSpace(rateLimitPlan))
                    {
                        plan = rateLimitPlan;
                    }

                    QuotaGroupUsage? baseGroup = ParseCodexQuotaGroup(rateLimits, string.Empty);
                    if (baseGroup is not null)
                    {
                        quotaGroups.Add(baseGroup);
                        if (TryGetQuotaWindow(rateLimits, "primary", "5h", out QuotaWindowUsage primary))
                        {
                            remainingPercent = primary.RemainingPercent;
                            primaryResetAt = primary.ResetAt;
                            primaryQuotaLabel = primary.Label;
                        }

                        if (TryGetQuotaWindow(rateLimits, "secondary", "Weekly", out QuotaWindowUsage secondary))
                        {
                            weeklyPercent = secondary.RemainingPercent;
                            weeklyResetAt = secondary.ResetAt;
                            weeklyQuotaLabel = secondary.Label;
                        }
                    }
                }

                if (TryGetProperty(result, "rateLimitsByLimitId", out JsonElement rateLimitsById) &&
                    rateLimitsById.ValueKind == JsonValueKind.Object)
                {
                    HashSet<string> seenLimitIds = new(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(baseLimitId))
                    {
                        seenLimitIds.Add(baseLimitId);
                    }

                    foreach (JsonProperty property in rateLimitsById.EnumerateObject())
                    {
                        JsonElement bucket = property.Value;
                        if (bucket.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        string limitId = FindString(bucket, "limitId", "limit_id") ?? property.Name;
                        if (!seenLimitIds.Add(limitId))
                        {
                            continue;
                        }

                        string limitName = FindString(bucket, "limitName", "limit_name") ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(limitName))
                        {
                            limitName = FormatLimitName(limitId);
                        }

                        QuotaGroupUsage? group = ParseCodexQuotaGroup(bucket, limitName);
                        if (group is not null)
                        {
                            quotaGroups.Add(group);
                        }
                    }
                }
            }
        }

        bool available = !rateLimitError && (remainingPercent.HasValue || weeklyPercent.HasValue);
        return new ProfileUsage(
            profile.Label,
            email,
            plan,
            remainingPercent.GetValueOrDefault(),
            weeklyPercent.GetValueOrDefault(),
            available,
            MockUsageData.CodexAccentColor)
        {
            Provider = profile.Provider,
            Home = profile.Home,
            HasCodexAuth = CodexAccountSwitchService.HasProfileAuth(profile),
            IsActiveCodexAccount = CodexAccountSwitchService.IsActiveProfile(profile),
            PrimaryResetAt = primaryResetAt,
            WeeklyResetAt = weeklyResetAt,
            HasPrimaryQuota = remainingPercent.HasValue,
            HasWeeklyQuota = weeklyPercent.HasValue,
            PrimaryQuotaLabel = primaryQuotaLabel,
            WeeklyQuotaLabel = weeklyQuotaLabel,
            QuotaGroups = quotaGroups
        };
    }

    private static QuotaGroupUsage? ParseCodexQuotaGroup(JsonElement bucket, string name)
    {
        List<QuotaWindowUsage> windows = [];
        if (TryGetQuotaWindow(bucket, "primary", "5h", out QuotaWindowUsage primary))
        {
            windows.Add(primary);
        }

        if (TryGetQuotaWindow(bucket, "secondary", "Weekly", out QuotaWindowUsage secondary))
        {
            windows.Add(secondary);
        }

        return windows.Count == 0 ? null : new QuotaGroupUsage(name, windows);
    }

    private static bool TryGetQuotaWindow(
        JsonElement bucket,
        string propertyName,
        string fallbackLabel,
        out QuotaWindowUsage window)
    {
        if (TryGetProperty(bucket, propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Object &&
            TryReadRemainingPercent(value) is { } remainingPercent)
        {
            window = new QuotaWindowUsage(
                FormatQuotaWindowLabel(value, fallbackLabel),
                remainingPercent,
                true,
                TryReadResetTime(value),
                MockUsageData.CodexAccentColor);
            return true;
        }

        window = null!;
        return false;
    }

    private static string FormatQuotaWindowLabel(JsonElement window, string fallbackLabel)
    {
        if (!TryFindNumber(window, out double rawMinutes, "windowDurationMins", "window_duration_mins") ||
            rawMinutes <= 0)
        {
            return fallbackLabel;
        }

        int minutes = Math.Max(1, (int)Math.Round(rawMinutes));
        if (minutes == 7 * 24 * 60)
        {
            return "Weekly";
        }

        if (minutes % (24 * 60) == 0)
        {
            return $"{minutes / (24 * 60)}d";
        }

        if (minutes % 60 == 0)
        {
            return $"{minutes / 60}h";
        }

        return $"{minutes}m";
    }

    private static string FormatLimitName(string limitId)
    {
        string[] tokens = limitId
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0
            ? "Additional usage"
            : string.Join(' ', tokens.Select(token => char.ToUpperInvariant(token[0]) + token[1..]));
    }

    private static DateTimeOffset? TryReadResetTime(JsonElement element)
    {
        if (TryFindNumber(element, out double epoch, "resets_at", "resetsAt") && epoch > 0)
        {
            long value = (long)epoch;
            return value > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }

        if (TryFindNumber(element, out double resetsIn, "resets_in_seconds", "resetsInSeconds") && resetsIn > 0)
        {
            return DateTimeOffset.Now.AddSeconds(resetsIn);
        }

        return null;
    }

    private static JsonDocument? TryParseJson(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsJsonRpcResponseLine(string line, int responseId)
    {
        using JsonDocument? document = TryParseJson(line);
        if (document is null)
        {
            return false;
        }

        JsonElement root = document.RootElement;
        return GetTopLevelId(root) == responseId &&
               (HasTopLevelProperty(root, "result") || HasTopLevelProperty(root, "error"));
    }

    private static int? GetTopLevelId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, "id", out JsonElement idElement))
        {
            return null;
        }

        if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt32(out int id))
        {
            return id;
        }

        return null;
    }

    private static bool HasTopLevelProperty(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object && TryGetProperty(root, propertyName, out _);
    }

    private static string? FindString(JsonElement element, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (TryFindProperty(element, propertyName, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static int? TryReadRemainingPercent(JsonElement element)
    {
        if (TryFindNumber(element, out double usedPercent, "usedPercent", "used_percent", "percentUsed", "percent_used"))
        {
            return ClampPercent(100 - usedPercent);
        }

        if (TryFindNumber(element, out double remainingPercent, "remainingPercent", "remaining_percent"))
        {
            return ClampPercent(remainingPercent);
        }

        return null;
    }

    private static int ClampPercent(double value)
    {
        return (int)Math.Clamp(Math.Round(value), 0, 100);
    }

    private static bool TryFindNumber(JsonElement element, out double number, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (TryFindProperty(element, propertyName, out JsonElement value))
            {
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
        }

        number = 0;
        return false;
    }

    private static bool TryFindProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(element, propertyName, out value))
            {
                return true;
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (TryFindProperty(property.Value, propertyName, out value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (TryFindProperty(item, propertyName, out value))
                {
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static ProfileUsage Unavailable(ProfileConfig profile)
    {
        return Unavailable(profile, MockUsageData.CodexAccentColor);
    }

    private static ProfileUsage Unavailable(ProfileConfig profile, Windows.UI.Color accentColor)
    {
        return new ProfileUsage(profile.Label, string.Empty, string.Empty, 0, 0, false, accentColor)
        {
            Provider = profile.Provider,
            Home = profile.Home,
            HasCodexAuth = AppConfigStore.IsProvider(profile, "codex") &&
                CodexAccountSwitchService.HasProfileAuth(profile),
            IsActiveCodexAccount = AppConfigStore.IsProvider(profile, "codex") &&
                CodexAccountSwitchService.IsActiveProfile(profile)
        };
    }

    private void RegisterProcess(Process process)
    {
        lock (_processLock)
        {
            _activeProcesses.Add(process);
        }
    }

    private void UnregisterProcess(Process process)
    {
        lock (_processLock)
        {
            _activeProcesses.Remove(process);
        }
    }

    private void KillActiveProcesses()
    {
        Process[] processes;
        lock (_processLock)
        {
            processes = [.. _activeProcesses];
        }

        foreach (Process process in processes)
        {
            KillProcess(process);
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_loopLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cts = _loopCts;
            _loopCts = null;
        }

        AppConfigStore.Changed -= OnConfigChanged;
        cts?.Cancel();
        KillActiveProcesses();
        _claudeUsageService.Dispose();
        _fetchLock.Dispose();
        cts?.Dispose();
    }
}
