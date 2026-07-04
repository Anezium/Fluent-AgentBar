using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FluentAgentBar;

public sealed record TokenStats(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    double CostUsd = 0)
{
    public long TotalInputTokens => InputTokens + CacheReadTokens + CacheCreationTokens;

    public string Summary => $"Today {ShortSummary}";

    public string ShortSummary
    {
        get
        {
            string summary = $"{FormatTokenCount(TotalInputTokens)} in \u00B7 {FormatTokenCount(OutputTokens)} out";
            return CostUsd > 0
                ? $"{summary} \u00B7 ${CostUsd.ToString("0.00", CultureInfo.InvariantCulture)}"
                : summary;
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

    // Sources to refresh from:
    // https://platform.claude.com/docs/en/about-claude/pricing
    // https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json
    private static readonly ModelPricing[] PricingTable =
    [
        new("claude-fable-5", 10.00, 50.00, 1.00, 12.50),
        new("claude-mythos-5", 10.00, 50.00, 1.00, 12.50),
        new("claude-opus-4", 5.00, 25.00, 0.50, 6.25),
        new("claude-sonnet-5", 2.00, 10.00, 0.20, 2.50, EffectiveThrough: new DateTime(2026, 8, 31)),
        new("claude-sonnet-5", 3.00, 15.00, 0.30, 3.75, EffectiveFrom: new DateTime(2026, 9, 1)),
        new("claude-sonnet-4", 3.00, 15.00, 0.30, 3.75),
        new("claude-haiku-4", 1.00, 5.00, 0.10, 1.25),
        new("gpt-5.5", 5.00, 30.00, 0.50, 0),
        new("gpt-5-5", 5.00, 30.00, 0.50, 0),
        new("gpt-5.4", 2.50, 15.00, 0.25, 0),
        new("gpt-5-4", 2.50, 15.00, 0.25, 0),
        new("gpt-5.1", 1.25, 10.00, 0.125, 0),
        new("gpt-5-1", 1.25, 10.00, 0.125, 0),
        new("gpt-5", 1.25, 10.00, 0.125, 0),
        new("codex-mini", 1.50, 6.00, 0.375, 0)
    ];

    public Task<(TokenReport? codex, TokenReport? claude)> ComputeAsync(AppConfig config)
    {
        return ComputeAsync(config, includeDefaultCodexHome: true);
    }

    internal Task<(TokenReport? codex, TokenReport? claude)> ComputeAsync(
        AppConfig config,
        bool includeDefaultCodexHome,
        DateTime? today = null)
    {
        return Task.Run(() =>
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

    private static TokenReport? ComputeCodex(AppConfig config, bool includeDefaultCodexHome, DateTime today)
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

    private static TokenReport? ComputeClaude(AppConfig config, DateTime today)
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

    private static void ScanCodexSessionRoot(
        string root,
        DateTime minDay,
        DateTime today,
        Dictionary<DateTime, TokenAccumulator> buckets)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);

        // Sessions live in the folder of the day they started; their lines are
        // bucketed by timestamp, so spanning midnight is handled per line.
        for (DateTime day = minDay; day <= today; day = day.AddDays(1))
        {
            foreach (string filePath in EnumerateFiles(DayFolder(root, day), "*.jsonl", recurse: false))
            {
                files.Add(filePath);
            }
        }

        foreach (string filePath in EnumerateFiles(root, "*.jsonl", recurse: false))
        {
            if (WasModifiedSince(filePath, minDay))
            {
                files.Add(filePath);
            }
        }

        foreach (string filePath in files)
        {
            ReadCodexFileUsage(filePath, minDay, today, buckets);
        }
    }

    private static string DayFolder(string root, DateTime day)
    {
        return Path.Combine(
            root,
            day.Year.ToString("0000", CultureInfo.InvariantCulture),
            day.Month.ToString("00", CultureInfo.InvariantCulture),
            day.Day.ToString("00", CultureInfo.InvariantCulture));
    }

    // Cumulative counters are turned into per-line deltas and bucketed by the
    // line's timestamp, so each day only gets the tokens actually consumed
    // that day, even when a session spans midnight.
    private static void ReadCodexFileUsage(
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
            foreach (string line in File.ReadLines(filePath))
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
                        AddToBucket(buckets, day, delta with { CostUsd = CalculateCost(delta, currentModel, day) });
                    }
                }
                else if (inWindow)
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

    private static void ReadClaudeFile(
        string filePath,
        DateTime minDay,
        DateTime maxDay,
        HashSet<string> seenMessages,
        Dictionary<DateTime, TokenAccumulator> buckets)
    {
        int lineNumber = 0;

        try
        {
            foreach (string line in File.ReadLines(filePath))
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

    private static bool TryReadClaudeLineUsage(
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
            double costUsd = CalculateCost(
                inputTokens,
                outputTokens,
                cacheReadTokens,
                cacheCreationTokens,
                model,
                day);

            usage = new TokenStats(inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens, costUsd);
            return true;
        }
        catch (JsonException)
        {
            return false;
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

    private static double CalculateCost(TokenStats usage, string? model, DateTime? usageDay = null)
    {
        return CalculateCost(
            usage.InputTokens,
            usage.OutputTokens,
            usage.CacheReadTokens,
            usage.CacheCreationTokens,
            model,
            usageDay);
    }

    private static double CalculateCost(
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheCreationTokens,
        string? model,
        DateTime? usageDay = null)
    {
        if (!TryGetPricing(model, usageDay ?? DateTime.Today, out ModelPricing? pricing))
        {
            return 0;
        }

        ModelPricing modelPricing = pricing!;
        return ((inputTokens * modelPricing.Input) +
                (outputTokens * modelPricing.Output) +
                (cacheReadTokens * modelPricing.CacheRead) +
                (cacheCreationTokens * modelPricing.CacheWrite)) / 1_000_000.0;
    }

    private static bool TryGetPricing(string? model, DateTime usageDay, out ModelPricing? pricing)
    {
        string normalizedModel = NormalizeModelName(model);
        if (normalizedModel.Length == 0)
        {
            pricing = null;
            return false;
        }

        foreach (ModelPricing candidate in PricingTable)
        {
            if (normalizedModel.StartsWith(candidate.Prefix, StringComparison.Ordinal) &&
                candidate.AppliesOn(usageDay.Date))
            {
                pricing = candidate;
                return true;
            }
        }

        pricing = null;
        return false;
    }

    private static string NormalizeModelName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return string.Empty;
        }

        string normalized = model.Trim().ToLowerInvariant();
        if (normalized.StartsWith("openai/", StringComparison.Ordinal))
        {
            normalized = normalized["openai/".Length..];
        }

        return StripDateSuffix(normalized);
    }

    private static string StripDateSuffix(string value)
    {
        if (value.Length > 11 && IsDashedDateSuffix(value.AsSpan(value.Length - 11, 11)))
        {
            return value[..^11];
        }

        if (value.Length > 9 && IsCompactDateSuffix(value.AsSpan(value.Length - 9, 9)))
        {
            return value[..^9];
        }

        return value;
    }

    private static bool IsDashedDateSuffix(ReadOnlySpan<char> value)
    {
        return value.Length == 11 &&
            value[0] == '-' &&
            char.IsDigit(value[1]) &&
            char.IsDigit(value[2]) &&
            char.IsDigit(value[3]) &&
            char.IsDigit(value[4]) &&
            value[5] == '-' &&
            char.IsDigit(value[6]) &&
            char.IsDigit(value[7]) &&
            value[8] == '-' &&
            char.IsDigit(value[9]) &&
            char.IsDigit(value[10]);
    }

    private static bool IsCompactDateSuffix(ReadOnlySpan<char> value)
    {
        return value.Length == 9 &&
            value[0] == '-' &&
            char.IsDigit(value[1]) &&
            char.IsDigit(value[2]) &&
            char.IsDigit(value[3]) &&
            char.IsDigit(value[4]) &&
            char.IsDigit(value[5]) &&
            char.IsDigit(value[6]) &&
            char.IsDigit(value[7]) &&
            char.IsDigit(value[8]);
    }

    private sealed record ModelPricing(
        string Prefix,
        double Input,
        double Output,
        double CacheRead,
        double CacheWrite,
        DateTime? EffectiveFrom = null,
        DateTime? EffectiveThrough = null)
    {
        public bool AppliesOn(DateTime day)
        {
            return (EffectiveFrom is null || day >= EffectiveFrom.Value.Date) &&
                (EffectiveThrough is null || day <= EffectiveThrough.Value.Date);
        }
    }

    private sealed class TokenAccumulator
    {
        private long _inputTokens;
        private long _outputTokens;
        private long _cacheReadTokens;
        private long _cacheCreationTokens;
        private double _costUsd;

        public bool HasValues { get; private set; }

        public void Add(TokenStats stats)
        {
            HasValues = true;
            _inputTokens += stats.InputTokens;
            _outputTokens += stats.OutputTokens;
            _cacheReadTokens += stats.CacheReadTokens;
            _cacheCreationTokens += stats.CacheCreationTokens;
            _costUsd += stats.CostUsd;
        }

        public TokenStats ToTokenStats()
        {
            return new TokenStats(_inputTokens, _outputTokens, _cacheReadTokens, _cacheCreationTokens, _costUsd);
        }
    }
}
