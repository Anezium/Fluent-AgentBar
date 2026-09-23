using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FluentAgentBar;

public sealed record TokenStats(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    double CostUsd = 0,
    long UnpricedTokens = 0)
{
    public long TotalInputTokens => InputTokens + CacheReadTokens + CacheCreationTokens;

    public string Summary => $"Today {ShortSummary}";

    public string ShortSummary
    {
        get
        {
            string summary = $"{FormatTokenCount(TotalInputTokens)} in \u00B7 {FormatTokenCount(OutputTokens)} out";
            if (CostUsd <= 0)
            {
                return summary;
            }

            // Tokens from a model with no known price make the cost a floor.
            string cost = "$" + CostUsd.ToString("0.00", CultureInfo.InvariantCulture);
            return UnpricedTokens > 0
                ? $"{summary} \u00B7 \u2265 {cost}"
                : $"{summary} \u00B7 {cost}";
        }
    }

    internal static string FormatTokenCount(long value)
    {
        if (value < 1000)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        if (value < 1_000_000)
        {
            return FormatScaled(value / 1000.0, "K");
        }

        return FormatScaled(value / 1_000_000.0, "M");
    }

    private static string FormatScaled(double value, string suffix)
    {
        return value.ToString("0.#", CultureInfo.InvariantCulture) + suffix;
    }
}

public sealed record DailyTokenStats(DateTime Date, TokenStats Stats);

public sealed record TokenReport(TokenStats? Today, IReadOnlyList<DailyTokenStats> Daily);

internal sealed class TokenStatsService
{
    private const int HistoryDays = 7;

    private readonly ModelPricingCatalog _pricing;

    public TokenStatsService()
        : this(ModelPricingCatalog.BuiltIn)
    {
    }

    internal TokenStatsService(ModelPricingCatalog pricing)
    {
        _pricing = pricing;
    }

    public Task<(TokenReport? codex, TokenReport? claude)> ComputeAsync(AppConfig config)
    {
        return ComputeAsync(config, includeDefaultCodexHome: true);
    }

    internal async Task<(TokenReport? codex, TokenReport? claude)> ComputeAsync(
        AppConfig config,
        bool includeDefaultCodexHome,
        DateTime? today = null)
    {
        await _pricing.RefreshAsync();
        return await Task.Run(() =>
        {
            TokenReport? codex = null;
            TokenReport? claude = null;
            DateTime reportToday = (today ?? DateTime.Today).Date;

            try
            {
                codex = ComputeCodex(config, includeDefaultCodexHome, reportToday);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            try
            {
                claude = ComputeClaude(config, reportToday);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            return (codex, claude);
        });
    }

    private TokenReport? ComputeCodex(AppConfig config, bool includeDefaultCodexHome, DateTime today)
    {
        DateTime minDay = today.AddDays(-(HistoryDays - 1));
        Dictionary<DateTime, TokenAccumulator> buckets = [];
        HashSet<string> codexHomes = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProfileConfig profile in config.Profiles.Where(profile =>
                     profile.Enabled && AppConfigStore.IsProvider(profile, "codex")))
        {
            AddNormalizedDirectory(codexHomes, profile.Home);
        }

        if (includeDefaultCodexHome)
        {
            string userProfile = GetUserProfileDirectory();
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                AddNormalizedDirectory(codexHomes, Path.Combine(userProfile, ".codex"));
            }
        }

        foreach (string codexHome in codexHomes)
        {
            ScanCodexSessionRoot(Path.Combine(codexHome, "sessions"), minDay, today, buckets);
            ScanCodexSessionRoot(Path.Combine(codexHome, "archived_sessions"), minDay, today, buckets);
        }

        return ToReport(buckets, minDay, today);
    }

    private TokenReport? ComputeClaude(AppConfig config, DateTime today)
    {
        DateTime minDay = today.AddDays(-(HistoryDays - 1));
        Dictionary<DateTime, TokenAccumulator> buckets = [];
        HashSet<string> seenMessages = new(StringComparer.Ordinal);
        HashSet<string> projectRoots = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProfileConfig profile in config.Profiles.Where(profile =>
                     profile.Enabled && AppConfigStore.IsProvider(profile, "claude")))
        {
            string claudeHome = NormalizeDirectoryPath(profile.Home);
            if (claudeHome.Length > 0)
            {
                AddNormalizedDirectory(projectRoots, Path.Combine(claudeHome, "projects"));
            }
        }

        foreach (string projectsRoot in projectRoots)
        {
            if (!Directory.Exists(projectsRoot))
            {
                continue;
            }

            foreach (string filePath in EnumerateFiles(projectsRoot, "*.jsonl", recurse: true))
            {
                if (!WasModifiedSince(filePath, minDay))
                {
                    continue;
                }

                ReadClaudeFile(filePath, minDay, today, seenMessages, buckets);
            }
        }

        return ToReport(buckets, minDay, today);
    }

    private static TokenReport? ToReport(
        Dictionary<DateTime, TokenAccumulator> buckets,
        DateTime minDay,
        DateTime today)
    {
        if (buckets.Values.All(bucket => !bucket.HasValues))
        {
            return null;
        }

        List<DailyTokenStats> daily = [];
        for (DateTime day = minDay; day <= today; day = day.AddDays(1))
        {
            daily.Add(new DailyTokenStats(
                day,
                buckets.TryGetValue(day, out TokenAccumulator? bucket) && bucket.HasValues
                    ? bucket.ToTokenStats()
                    : new TokenStats(0, 0, 0, 0)));
        }

        TokenStats? todayStats = buckets.TryGetValue(today, out TokenAccumulator? todayBucket) && todayBucket.HasValues
            ? todayBucket.ToTokenStats()
            : null;
        return new TokenReport(todayStats, daily);
    }

    private static void AddToBucket(
        Dictionary<DateTime, TokenAccumulator> buckets,
        DateTime day,
        TokenStats stats)
    {
        if (!buckets.TryGetValue(day, out TokenAccumulator? bucket))
        {
            bucket = new TokenAccumulator();
            buckets[day] = bucket;
        }

        bucket.Add(stats);
    }

    private void ScanCodexSessionRoot(
        string root,
        DateTime minDay,
        DateTime today,
        Dictionary<DateTime, TokenAccumulator> buckets)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        // Resumed sessions stay in their original date folder, even months
        // later. Select by modification time, then bucket each event by date.
        foreach (string filePath in EnumerateFiles(root, "*.jsonl", recurse: true))
        {
            if (WasModifiedSince(filePath, minDay))
            {
                ReadCodexFileUsage(filePath, minDay, today, buckets);
            }
        }
    }

    // Cumulative counters are turned into per-line deltas and bucketed by the
    // line's timestamp, so each day only gets the tokens actually consumed
    // that day, even when a session spans midnight.
    private void ReadCodexFileUsage(
        string filePath,
        DateTime minDay,
        DateTime today,
        Dictionary<DateTime, TokenAccumulator> buckets)
    {
        TokenStats? previousCumulativeUsage = null;
        string currentModel = string.Empty;
        DateTime fallbackDay = FileDay(filePath, today);

        try
        {
            foreach (string line in ReadSharedLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using JsonDocument? document = TryParseJson(line);
                if (document is null)
                {
                    continue;
                }

                JsonElement root = document.RootElement;
                if (TryFindString(root, "model", out string? model) && !string.IsNullOrWhiteSpace(model))
                {
                    currentModel = model;
                }

                if (!TryReadCodexLineUsage(root, out TokenStats usage, out bool isCumulative))
                {
                    continue;
                }

                DateTime day = LineDay(root) ?? fallbackDay;
                bool inWindow = day >= minDay && day <= today;
                if (isCumulative)
                {
                    TokenStats delta = previousCumulativeUsage is null
                        ? usage
                        : ClampDelta(usage, previousCumulativeUsage);
                    previousCumulativeUsage = usage;

                    if (inWindow)
                    {
                        AddToBucket(buckets, day, Price(delta, currentModel));
                    }
                }
                else if (inWindow)
                {
                    AddToBucket(buckets, day, Price(usage, currentModel));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static DateTime? LineDay(JsonElement root)
    {
        string? timestampText = TryReadString(root, "timestamp");
        if (timestampText is null ||
            !DateTimeOffset.TryParse(
                timestampText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp))
        {
            return null;
        }

        return timestamp.LocalDateTime.Date;
    }

    private static DateTime FileDay(string filePath, DateTime fallback)
    {
        try
        {
            return File.GetLastWriteTime(filePath).Date;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return fallback;
        }
    }

    private void ReadClaudeFile(
        string filePath,
        DateTime minDay,
        DateTime maxDay,
        HashSet<string> seenMessages,
        Dictionary<DateTime, TokenAccumulator> buckets)
    {
        int lineNumber = 0;

        try
        {
            foreach (string line in ReadSharedLines(filePath))
            {
                lineNumber++;
                if (!TryReadClaudeLineUsage(
                        line,
                        filePath,
                        lineNumber,
                        minDay,
                        maxDay,
                        out DateTime day,
                        out string dedupeKey,
                        out TokenStats usage))
                {
                    continue;
                }

                if (seenMessages.Add(dedupeKey))
                {
                    AddToBucket(buckets, day, usage);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static bool TryReadCodexLineUsage(JsonElement root, out TokenStats usage, out bool isCumulative)
    {
        usage = new TokenStats(0, 0, 0, 0);
        isCumulative = false;

        // token_count events nest the counters under payload.info; older
        // shapes keep them at the root or under a top-level info object.
        JsonElement scope = TryGetProperty(root, "payload", out JsonElement payload) ? payload : root;
        if (TryGetProperty(scope, "info", out JsonElement info))
        {
            scope = info;
        }

        if (TryGetProperty(scope, "total_token_usage", out JsonElement usageElement))
        {
            isCumulative = true;
        }
        else if (!TryGetProperty(scope, "last_token_usage", out usageElement))
        {
            return false;
        }

        bool hasInput = TryReadLong(usageElement, out long inputTokens, "input_tokens");
        bool hasOutput = TryReadLong(usageElement, out long outputTokens, "output_tokens");
        bool hasCacheRead = TryReadLong(
            usageElement,
            out long cacheReadTokens,
            "cached_input_tokens",
            "cache_read_input_tokens");
        bool hasCacheCreation = TryReadLong(
            usageElement,
            out long cacheCreationTokens,
            "cache_creation_input_tokens",
            "cache_write_input_tokens");

        if (!hasInput && !hasOutput && !hasCacheRead && !hasCacheCreation)
        {
            return false;
        }

        // Codex reports cached tokens as a subset of input_tokens (Claude
        // keeps them separate); normalize to the separate representation so
        // cache reads are not also billed at the full input rate.
        if (cacheReadTokens > 0)
        {
            inputTokens = Math.Max(0, inputTokens - cacheReadTokens);
        }

        usage = new TokenStats(inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens);
        return true;
    }

    private bool TryReadClaudeLineUsage(
        string line,
        string filePath,
        int lineNumber,
        DateTime minDay,
        DateTime maxDay,
        out DateTime day,
        out string dedupeKey,
        out TokenStats usage)
    {
        day = default;
        dedupeKey = string.Empty;
        usage = new TokenStats(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string? timestampText = TryReadString(root, "timestamp");
            if (timestampText is null ||
                !DateTimeOffset.TryParse(
                    timestampText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset timestamp))
            {
                return false;
            }

            day = timestamp.LocalDateTime.Date;
            if (day < minDay || day > maxDay)
            {
                return false;
            }

            if (!TryGetProperty(root, "message", out JsonElement message) ||
                !TryGetProperty(message, "usage", out JsonElement usageElement))
            {
                return false;
            }

            bool hasInput = TryReadLong(usageElement, out long inputTokens, "input_tokens");
            bool hasOutput = TryReadLong(usageElement, out long outputTokens, "output_tokens");
            bool hasCacheCreation = TryReadLong(
                usageElement,
                out long cacheCreationTokens,
                "cache_creation_input_tokens");
            bool hasCacheRead = TryReadLong(
                usageElement,
                out long cacheReadTokens,
                "cache_read_input_tokens");

            if (!hasInput && !hasOutput && !hasCacheCreation && !hasCacheRead)
            {
                return false;
            }

            string messageId = TryReadString(message, "id") ?? string.Empty;
            string requestId = TryReadString(root, "requestId", "request_id") ?? string.Empty;
            dedupeKey = messageId.Length > 0 || requestId.Length > 0
                ? messageId + "\u001F" + requestId
                : filePath + "\u001F" + lineNumber.ToString(CultureInfo.InvariantCulture);

            string model = TryReadString(message, "model") ?? TryReadString(root, "model") ?? string.Empty;
            usage = Price(new TokenStats(inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens), model);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<string> ReadSharedLines(string filePath)
    {
        // Codex and Claude keep their journals open for writing. File.ReadLines
        // uses FileShare.Read, which prevents opening those active logs on Windows.
        using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static IReadOnlyList<string> EnumerateFiles(string root, string pattern, bool recurse)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return [];
            }

            EnumerationOptions options = new()
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = recurse
            };
            return [.. Directory.EnumerateFiles(root, pattern, options)];
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return [];
        }
    }

    private static bool WasModifiedSince(string filePath, DateTime minDay)
    {
        try
        {
            return File.GetLastWriteTime(filePath).Date >= minDay;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    private static void AddNormalizedDirectory(HashSet<string> directories, string path)
    {
        string normalizedPath = NormalizeDirectoryPath(path);
        if (normalizedPath.Length > 0)
        {
            directories.Add(normalizedPath);
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string expanded = Environment.ExpandEnvironmentVariables(path);
        try
        {
            expanded = Path.GetFullPath(expanded);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return string.Empty;
        }

        return expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string GetUserProfileDirectory()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
        }

        return userProfile;
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

    private static bool TryFindString(JsonElement element, string propertyName, out string? value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    value = property.Value.GetString();
                    return true;
                }

                if (TryFindString(property.Value, propertyName, out value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (TryFindString(item, propertyName, out value))
                {
                    return true;
                }
            }
        }

        value = null;
        return false;
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

    private static TokenStats ClampDelta(TokenStats current, TokenStats previous)
    {
        return new TokenStats(
            Math.Max(0, current.InputTokens - previous.InputTokens),
            Math.Max(0, current.OutputTokens - previous.OutputTokens),
            Math.Max(0, current.CacheReadTokens - previous.CacheReadTokens),
            Math.Max(0, current.CacheCreationTokens - previous.CacheCreationTokens));
    }

    private TokenStats Price(TokenStats usage, string? model)
    {
        if (!_pricing.TryGetPricing(model, out ModelPricing? pricing))
        {
            return usage with { CostUsd = 0, UnpricedTokens = usage.TotalInputTokens + usage.OutputTokens };
        }

        ModelPricing modelPricing = pricing!;
        double costUsd = ((usage.InputTokens * modelPricing.Input) +
                (usage.OutputTokens * modelPricing.Output) +
                (usage.CacheReadTokens * modelPricing.CacheRead) +
                (usage.CacheCreationTokens * modelPricing.CacheWrite)) / 1_000_000.0;
        return usage with { CostUsd = costUsd, UnpricedTokens = 0 };
    }

    private sealed class TokenAccumulator
    {
        private long _inputTokens;
        private long _outputTokens;
        private long _cacheReadTokens;
        private long _cacheCreationTokens;
        private double _costUsd;
        private long _unpricedTokens;

        public bool HasValues { get; private set; }

        public void Add(TokenStats stats)
        {
            HasValues = true;
            _inputTokens += stats.InputTokens;
            _outputTokens += stats.OutputTokens;
            _cacheReadTokens += stats.CacheReadTokens;
            _cacheCreationTokens += stats.CacheCreationTokens;
            _costUsd += stats.CostUsd;
            _unpricedTokens += stats.UnpricedTokens;
        }

        public TokenStats ToTokenStats()
        {
            return new TokenStats(
                _inputTokens,
                _outputTokens,
                _cacheReadTokens,
                _cacheCreationTokens,
                _costUsd,
                _unpricedTokens);
        }
    }
}
