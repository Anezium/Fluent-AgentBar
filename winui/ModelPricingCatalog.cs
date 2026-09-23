using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FluentAgentBar;

// USD prices per million tokens for the models recorded in local Codex and
// Claude journals. The most specific source wins:
//   1. pricing-overrides.json next to config.json (manual escape hatch),
//   2. LiteLLM's public price list, re-downloaded once a day and cached on
//      disk so offline starts keep the last known prices,
//   3. the built-in table, matched by prefix: offline fallback, and a
//      family-level guess for releases LiteLLM has not listed yet.
internal sealed class ModelPricingCatalog : IDisposable
{
    internal const string RemoteUrl =
        "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";

    internal const string CacheFileName = "model-pricing-cache.json";
    internal const string OverridesFileName = "pricing-overrides.json";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    // Journals record first-party ids, so reseller and cloud entries
    // (bedrock/..., azure/...) are left out.
    private static readonly string[] RemoteProviders = ["anthropic", "openai"];

    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    // Sources to refresh from:
    // https://platform.claude.com/docs/en/about-claude/pricing
    // https://developers.openai.com/api/docs/pricing (standard, short-context estimates)
    private static readonly ModelPricing[] BuiltInTable =
    [
        new("claude-fable-5-1", 10.00, 50.00, 0.25, 12.50),
        new("claude-fable-5", 10.00, 50.00, 1.00, 12.50),
        new("claude-mythos-5-1", 10.00, 50.00, 0.25, 12.50),
        new("claude-mythos", 10.00, 50.00, 1.00, 12.50),
        new("claude-opus-5-5", 4.00, 20.00, 0.20, 5.00),
        new("claude-opus-5", 5.00, 25.00, 0.50, 6.25),
        new("claude-opus-4-1", 15.00, 75.00, 1.50, 18.75),
        new("claude-opus-4", 5.00, 25.00, 0.50, 6.25),
        new("claude-sonnet-5", 2.00, 10.00, 0.20, 2.50),
        new("claude-sonnet-4", 3.00, 15.00, 0.30, 3.75),
        new("claude-haiku-4", 1.00, 5.00, 0.10, 1.25),
        // Claude releases nobody lists yet are priced like the newest known
        // model of their family.
        new("claude-fable", 10.00, 50.00, 0.25, 12.50),
        new("claude-opus", 4.00, 20.00, 0.20, 5.00),
        new("claude-sonnet", 2.00, 10.00, 0.20, 2.50),
        new("claude-haiku", 1.00, 5.00, 0.10, 1.25),
        new("gpt-6-astra", 10.00, 50.00, 1.00, 12.50),
        new("gpt-6-sol", 2.00, 10.00, 0.20, 2.50),
        new("gpt-6-luna", 0.10, 0.50, 0.01, 0.125),
        new("gpt-5.6-terra", 2.00, 12.00, 0.20, 2.50),
        new("gpt-5-6-terra", 2.00, 12.00, 0.20, 2.50),
        new("gpt-5.6-luna", 0.20, 1.20, 0.02, 0.25),
        new("gpt-5-6-luna", 0.20, 1.20, 0.02, 0.25),
        // Bare "gpt-5.6" (and -sol) default to the Sol tier.
        new("gpt-5.6", 4.00, 20.00, 0.40, 5.00),
        new("gpt-5-6", 4.00, 20.00, 0.40, 5.00),
        new("gpt-5.5", 5.00, 30.00, 0.50, 0),
        new("gpt-5-5", 5.00, 30.00, 0.50, 0),
        new("gpt-5.4", 2.50, 15.00, 0.25, 0),
        new("gpt-5-4", 2.50, 15.00, 0.25, 0),
        new("gpt-5.3-codex", 1.75, 14.00, 0.175, 0),
        new("gpt-5-3-codex", 1.75, 14.00, 0.175, 0),
        new("gpt-5.2", 1.75, 14.00, 0.175, 0),
        new("gpt-5-2", 1.75, 14.00, 0.175, 0),
        new("gpt-5.1", 1.25, 10.00, 0.125, 0),
        new("gpt-5-1", 1.25, 10.00, 0.125, 0),
        new("gpt-5", 1.25, 10.00, 0.125, 0),
        new("codex-mini", 1.50, 6.00, 0.375, 0)
    ];

    private readonly string? _directory;
    private readonly HttpClient? _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<DateTimeOffset> _utcNow;
    private IReadOnlyDictionary<string, ModelPricing> _remote = new Dictionary<string, ModelPricing>();
    private IReadOnlyList<ModelPricing> _overrides = [];
    private DateTime? _overridesWriteTime;
    private DateTimeOffset _remoteFetchedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextRemoteAttempt = DateTimeOffset.MinValue;
    private string? _remoteETag;
    private bool _cacheLoaded;
    private int _refreshing;

    // Built-in table only: no disk, no network. Used by tests and as the
    // default for callers that do not opt into live prices.
    internal static ModelPricingCatalog BuiltIn { get; } = new(directory: null, httpClient: null);

    internal ModelPricingCatalog(
        string? directory,
        HttpClient? httpClient,
        bool ownsHttpClient = false,
        Func<DateTimeOffset>? utcNow = null)
    {
        _directory = directory;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal static ModelPricingCatalog CreateDefault()
    {
        HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
        return new ModelPricingCatalog(AppConfigStore.ConfigDirectory, httpClient, ownsHttpClient: true);
    }

    // Picks up the disk cache and overrides, then re-downloads the LiteLLM
    // list once the cached copy is a day old. Download and parse failures
    // keep the previous prices and retry an hour later.
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_directory is null)
        {
            return;
        }

        if (!_cacheLoaded)
        {
            _cacheLoaded = true;
            LoadCache();
        }

        ReloadOverridesIfChanged();

        DateTimeOffset now = _utcNow();
        TimeSpan age = now - _remoteFetchedAt;
        if (_httpClient is null ||
            (age >= TimeSpan.Zero && age < RefreshInterval) ||
            now < _nextRemoteAttempt ||
            Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            await DownloadAsync(now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _nextRemoteAttempt = now + RetryDelay;
            ProviderDiagnostics.Record("pricing", "LiteLLM", ex);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    internal bool TryGetPricing(string? model, out ModelPricing? pricing)
    {
        string normalizedModel = NormalizeModelName(model);
        if (normalizedModel.Length == 0)
        {
            pricing = null;
            return false;
        }

        pricing = LongestPrefixMatch(_overrides, normalizedModel) ??
            (_remote.TryGetValue(normalizedModel, out ModelPricing? remote) ? remote : null) ??
            LongestPrefixMatch(BuiltInTable, normalizedModel);
        return pricing is not null;
    }

    internal static string NormalizeModelName(string? model)
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

    // LiteLLM keys are "<model id>": { "litellm_provider", "input_cost_per_token", ... }
    // with costs in USD per token.
    internal static Dictionary<string, ModelPricing> ParseLiteLlm(JsonElement root)
    {
        Dictionary<string, ModelPricing> prices = new(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object)
        {
            return prices;
        }

        foreach (JsonProperty entry in root.EnumerateObject())
        {
            JsonElement value = entry.Value;
            if (value.ValueKind != JsonValueKind.Object ||
                entry.Name.Contains('/') ||
                !IsRemoteProvider(value) ||
                !TryReadNumber(value, "input_cost_per_token", out double input) ||
                !TryReadNumber(value, "output_cost_per_token", out double output))
            {
                continue;
            }

            TryReadNumber(value, "cache_read_input_token_cost", out double cacheRead);
            TryReadNumber(value, "cache_creation_input_token_cost", out double cacheWrite);

            // Dated snapshots collapse onto their alias once normalized; the
            // undated alias wins when both are listed.
            string key = NormalizeModelName(entry.Name);
            bool isAlias = key.Length == entry.Name.Length;
            if (key.Length == 0 || (!isAlias && prices.ContainsKey(key)))
            {
                continue;
            }

            prices[key] = new ModelPricing(
                key,
                PerMillion(input),
                PerMillion(output),
                PerMillion(cacheRead),
                PerMillion(cacheWrite));
        }

        return prices;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient?.Dispose();
        }
    }

    private async Task DownloadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, RemoteUrl);
        if (_remote.Count > 0 &&
            _remoteETag is not null &&
            EntityTagHeaderValue.TryParse(_remoteETag, out EntityTagHeaderValue? etag))
        {
            request.Headers.IfNoneMatch.Add(etag);
        }

        using HttpResponseMessage response = await _httpClient!.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            _remoteFetchedAt = now;
            SaveCache();
            return;
        }

        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        Dictionary<string, ModelPricing> prices = ParseLiteLlm(document.RootElement);
        if (prices.Count == 0)
        {
            throw new InvalidDataException("LiteLLM price list has no Anthropic or OpenAI entries.");
        }

        _remote = prices;
        _remoteETag = response.Headers.ETag?.ToString();
        _remoteFetchedAt = now;
        SaveCache();
    }

    private string CachePath => Path.Combine(_directory!, CacheFileName);

    private string OverridesPath => Path.Combine(_directory!, OverridesFileName);

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(CachePath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("models", out JsonElement models))
            {
                return;
            }

            Dictionary<string, ModelPricing> prices = ParsePriceMap(models);
            if (prices.Count == 0)
            {
                return;
            }

            _remote = prices;
            _remoteETag = root.TryGetProperty("etag", out JsonElement etag) && etag.ValueKind == JsonValueKind.String
                ? etag.GetString()
                : null;
            if (root.TryGetProperty("fetchedAt", out JsonElement fetchedAt) &&
                fetchedAt.ValueKind == JsonValueKind.String &&
                fetchedAt.TryGetDateTimeOffset(out DateTimeOffset fetchedAtValue))
            {
                _remoteFetchedAt = fetchedAtValue;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine(ex);
        }
    }

    private void SaveCache()
    {
        try
        {
            using MemoryStream buffer = new();
            using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("source", RemoteUrl);
                writer.WriteString("fetchedAt", _remoteFetchedAt);
                if (_remoteETag is not null)
                {
                    writer.WriteString("etag", _remoteETag);
                }

                writer.WriteStartObject("models");
                foreach (ModelPricing price in _remote.Values.OrderBy(price => price.Prefix, StringComparer.Ordinal))
                {
                    writer.WriteStartObject(price.Prefix);
                    writer.WriteNumber("input", price.Input);
                    writer.WriteNumber("output", price.Output);
                    writer.WriteNumber("cacheRead", price.CacheRead);
                    writer.WriteNumber("cacheWrite", price.CacheWrite);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            Directory.CreateDirectory(_directory!);
            string temporaryPath = CachePath + ".tmp";
            File.WriteAllBytes(temporaryPath, buffer.ToArray());
            File.Move(temporaryPath, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex);
        }
    }

    private void ReloadOverridesIfChanged()
    {
        DateTime? writeTime = null;
        try
        {
            if (File.Exists(OverridesPath))
            {
                writeTime = File.GetLastWriteTimeUtc(OverridesPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex);
        }

        if (writeTime == _overridesWriteTime)
        {
            return;
        }

        _overridesWriteTime = writeTime;
        _overrides = writeTime is null ? [] : ReadOverrides(OverridesPath);
    }

    private static IReadOnlyList<ModelPricing> ReadOverrides(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), LenientJson);
            return [.. ParsePriceMap(document.RootElement).Values];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ProviderDiagnostics.Record("pricing", OverridesFileName, ex);
            return [];
        }
    }

    // { "<model id or prefix>": { "input": 4, "output": 20, "cacheRead": 0.2, "cacheWrite": 5 } }
    // in USD per million tokens. Omitted cache prices use the usual 0.1x read
    // and 1.25x write multipliers of the input price.
    private static Dictionary<string, ModelPricing> ParsePriceMap(JsonElement map)
    {
        Dictionary<string, ModelPricing> prices = new(StringComparer.Ordinal);
        if (map.ValueKind != JsonValueKind.Object)
        {
            return prices;
        }

        foreach (JsonProperty entry in map.EnumerateObject())
        {
            string key = NormalizeModelName(entry.Name);
            if (key.Length == 0 ||
                entry.Value.ValueKind != JsonValueKind.Object ||
                !TryReadNumber(entry.Value, "input", out double input) ||
                !TryReadNumber(entry.Value, "output", out double output))
            {
                continue;
            }

            double cacheRead = TryReadNumber(entry.Value, "cacheRead", out double read) ? read : input * 0.1;
            double cacheWrite = TryReadNumber(entry.Value, "cacheWrite", out double write) ? write : input * 1.25;
            prices[key] = new ModelPricing(key, input, output, cacheRead, cacheWrite);
        }

        return prices;
    }

    private static ModelPricing? LongestPrefixMatch(IReadOnlyList<ModelPricing> table, string model)
    {
        ModelPricing? best = null;
        foreach (ModelPricing candidate in table)
        {
            if (Matches(candidate.Prefix, model) &&
                (best is null || candidate.Prefix.Length > best.Prefix.Length))
            {
                best = candidate;
            }
        }

        return best;
    }

    // "claude-opus-5" covers "claude-opus-5-5" but not "claude-opus-50".
    private static bool Matches(string prefix, string model)
    {
        return model.StartsWith(prefix, StringComparison.Ordinal) &&
            (model.Length == prefix.Length || !char.IsLetterOrDigit(model[prefix.Length]));
    }

    private static bool IsRemoteProvider(JsonElement entry)
    {
        return entry.TryGetProperty("litellm_provider", out JsonElement provider) &&
            provider.ValueKind == JsonValueKind.String &&
            provider.GetString() is string name &&
            RemoteProviders.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryReadNumber(JsonElement element, string propertyName, out double value)
    {
        if (element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value) &&
            value >= 0)
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static double PerMillion(double perToken)
    {
        return Math.Round(perToken * 1_000_000, 6);
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
}

// USD per million tokens.
internal sealed record ModelPricing(
    string Prefix,
    double Input,
    double Output,
    double CacheRead,
    double CacheWrite);
