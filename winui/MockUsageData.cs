using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentAgentBar;

public sealed record ProviderUsage(
    string Name,
    string Badge,
    IReadOnlyList<ProfileUsage> Profiles) : INotifyPropertyChanged
{
    private const double ChartHeight = 64;
    private const double MinBarHeight = 6;

    private bool _isHistoryExpanded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TokenStats? Tokens { get; init; }
    public IReadOnlyList<DailyTokenStats>? History { get; init; }

    // Mutating this in place (instead of rebuilding the Providers list) is
    // what keeps expand/collapse flicker-free: the repeater never
    // re-templates, so the quota bars do not replay their fill animation.
    public bool IsHistoryExpanded
    {
        get => _isHistoryExpanded;
        set
        {
            if (_isHistoryExpanded == value)
            {
                return;
            }

            _isHistoryExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHistoryExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HistoryVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HistoryChevronGlyph)));
        }
    }
    public string ProfileCountText => Profiles.Count == 1 ? "1 profile" : $"{Profiles.Count} profiles";
    public string TokensText => Tokens?.Summary ?? string.Empty;
    public bool HasTokens => Tokens is not null;
    public Visibility TokensVisibility => HasTokens ? Visibility.Visible : Visibility.Collapsed;
    public Geometry LogoGeometry => ProviderIcons.GeometryFor(Name);
    public Brush LogoBrush => ProviderIcons.BrushFor(Name);

    public bool HasHistory =>
        History is not null &&
        History.Any(day => day.Stats.TotalInputTokens + day.Stats.OutputTokens > 0);

    public Visibility HistoryToggleVisibility => HasHistory ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HistoryVisibility => HasHistory && IsHistoryExpanded ? Visibility.Visible : Visibility.Collapsed;
    public string HistoryChevronGlyph => IsHistoryExpanded ? "\uE70E" : "\uE70D";

    private Color AccentColor => Name.Contains("claude", StringComparison.OrdinalIgnoreCase)
        ? MockUsageData.ClaudeAccentColor
        : MockUsageData.CodexAccentColor;

    public IReadOnlyList<HistoryBar> HistoryBars
    {
        get
        {
            if (History is null || !HasHistory)
            {
                return [];
            }

            long maxActivity = Math.Max(1, History.Max(day => DayActivity(day.Stats)));
            bool hasCost = History.Any(day => day.Stats.CostUsd > 0.005);
            DateTime today = DateTime.Today;
            List<HistoryBar> bars = [];

            foreach (DailyTokenStats day in History)
            {
                long activity = DayActivity(day.Stats);
                double height = activity == 0
                    ? MinBarHeight
                    : MinBarHeight + ((ChartHeight - MinBarHeight) * activity / maxActivity);
                bool isToday = day.Date == today;

                bars.Add(new HistoryBar(
                    day.Date.ToString("ddd", CultureInfo.CurrentCulture).TrimEnd('.'),
                    ValueLabel(day.Stats, activity, hasCost),
                    $"{day.Date.ToString("ddd d MMM", CultureInfo.CurrentCulture)} · {day.Stats.ShortSummary}",
                    Math.Round(height, 1),
                    CreateBarBrush(),
                    isToday ? 1.0 : 0.45,
                    isToday ? FontWeights.SemiBold : FontWeights.Normal));
            }

            return bars;
        }
    }

    // Days are labeled with the cost when pricing is known (what people
    // actually feel), otherwise with the raw token volume.
    private static string ValueLabel(TokenStats stats, long activity, bool hasCost)
    {
        if (activity == 0)
        {
            return string.Empty;
        }

        if (hasCost)
        {
            double cost = stats.CostUsd;
            return cost < 10
                ? "$" + cost.ToString("0.0", CultureInfo.InvariantCulture)
                : "$" + Math.Round(cost).ToString(CultureInfo.InvariantCulture);
        }

        return TokenStats.FormatTokenCount(activity);
    }

    // Slim pill with a vertical fade: full accent at the top, softer at the
    // base, so the chart reads light instead of blocky.
    private Brush CreateBarBrush()
    {
        Color accent = AccentColor;
        LinearGradientBrush brush = new()
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop { Color = accent, Offset = 0 });
        brush.GradientStops.Add(new GradientStop
        {
            Color = Color.FromArgb(130, accent.R, accent.G, accent.B),
            Offset = 1
        });
        return brush;
    }

    public string HistorySummaryText
    {
        get
        {
            if (History is null || !HasHistory)
            {
                return string.Empty;
            }

            TokenStats total = new(
                History.Sum(day => day.Stats.InputTokens),
                History.Sum(day => day.Stats.OutputTokens),
                History.Sum(day => day.Stats.CacheReadTokens),
                History.Sum(day => day.Stats.CacheCreationTokens),
                History.Sum(day => day.Stats.CostUsd));
            return $"Last {History.Count} days · {total.ShortSummary}";
        }
    }

    private static long DayActivity(TokenStats stats)
    {
        return stats.TotalInputTokens + stats.OutputTokens;
    }
}

public sealed record HistoryBar(
    string DayLabel,
    string ValueText,
    string Tooltip,
    double BarHeight,
    Brush BarBrush,
    double BarOpacity,
    Windows.UI.Text.FontWeight LabelWeight);

public sealed record QuotaWindowUsage(
    string Label,
    int RemainingPercent,
    bool IsAvailable,
    DateTimeOffset? ResetAt,
    Color AccentColor)
{
    public string RemainingText => IsAvailable ? $"{Math.Clamp(RemainingPercent, 0, 100)}%" : "--";
    public Brush AccentBrush => new SolidColorBrush(AccentColor);
}

public sealed record QuotaGroupUsage(
    string Name,
    IReadOnlyList<QuotaWindowUsage> Windows)
{
    public Visibility NameVisibility => string.IsNullOrWhiteSpace(Name)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string ResetsText
    {
        get
        {
            List<string> parts = Windows
                .Where(window => window.ResetAt.HasValue)
                .Select(window => $"{window.Label} {FormatReset(window.ResetAt!.Value)}")
                .ToList();
            return parts.Count == 0 ? string.Empty : "Resets · " + string.Join(" · ", parts);
        }
    }

    public Visibility ResetsVisibility => ResetsText.Length > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    private static string FormatReset(DateTimeOffset resetAt)
    {
        DateTime local = resetAt.LocalDateTime;
        string time = local.ToString("HH:mm", CultureInfo.CurrentCulture);
        return local.Date == DateTime.Today
            ? time
            : $"{local.ToString("ddd", CultureInfo.CurrentCulture).TrimEnd('.')} {time}";
    }
}

public sealed record ProfileUsage(
    string Label,
    string Email,
    string Plan,
    int RemainingPercent,
    int WeeklyPercent,
    bool IsAvailable,
    Color AccentColor)
{
    public string Provider { get; init; } = "codex";
    public string Home { get; init; } = string.Empty;
    public bool HasCodexAuth { get; init; }
    public bool IsActiveCodexAccount { get; init; }
    public DateTimeOffset? PrimaryResetAt { get; init; }
    public DateTimeOffset? WeeklyResetAt { get; init; }
    public bool HasPrimaryQuota { get; init; } = true;
    public bool HasWeeklyQuota { get; init; } = true;
    public string PrimaryQuotaLabel { get; init; } = "5h";
    public string WeeklyQuotaLabel { get; init; } = "Weekly";
    public IReadOnlyList<QuotaGroupUsage>? QuotaGroups { get; init; }

    public string RemainingText => IsAvailable && HasPrimaryQuota
        ? $"{Math.Clamp(RemainingPercent, 0, 100)}%"
        : "--";
    public string WeeklyText => IsAvailable && HasWeeklyQuota
        ? $"{Math.Clamp(WeeklyPercent, 0, 100)}%"
        : "--";
    public string UsageStatusText => IsAvailable
        ? string.Empty
        : Plan.Contains("Login Required", StringComparison.OrdinalIgnoreCase)
            ? "Sign in again from Settings to restore usage."
            : "Usage unavailable. Refresh, then sign in again if it persists.";
    public Visibility UsageStatusVisibility => IsAvailable
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Brush AccentBrush => new SolidColorBrush(AccentColor);
    public bool IsCodexProfile => AppConfigStore.NormalizeProvider(Provider) == "codex";
    public bool CanSwitchCodexAccount => IsCodexProfile && HasCodexAuth && !IsActiveCodexAccount;
    public Visibility CodexSwitchVisibility => IsCodexProfile && HasCodexAuth
        ? Visibility.Visible
        : Visibility.Collapsed;
    public string CodexSwitchText => IsActiveCodexAccount ? "Active" : "Switch";
    public string CodexSwitchGlyph => IsActiveCodexAccount ? "\uE73E" : "\uE8AB";
    public string CodexSwitchTooltip => IsActiveCodexAccount
        ? "This Codex account is active"
        : $"Switch Codex to {Label}";

    public IReadOnlyList<QuotaGroupUsage> DisplayQuotaGroups
    {
        get
        {
            if (QuotaGroups is { Count: > 0 })
            {
                return QuotaGroups;
            }

            List<QuotaWindowUsage> windows = [];
            if (HasPrimaryQuota)
            {
                windows.Add(new QuotaWindowUsage(
                    PrimaryQuotaLabel,
                    RemainingPercent,
                    IsAvailable,
                    PrimaryResetAt,
                    AccentColor));
            }

            if (HasWeeklyQuota)
            {
                windows.Add(new QuotaWindowUsage(
                    WeeklyQuotaLabel,
                    WeeklyPercent,
                    IsAvailable,
                    WeeklyResetAt,
                    AccentColor));
            }

            return [new QuotaGroupUsage(string.Empty, windows)];
        }
    }

    public string ResetsText
    {
        get
        {
            List<string> parts = [];
            if (PrimaryResetAt is { } primary)
            {
                parts.Add($"5h {FormatReset(primary)}");
            }

            if (WeeklyResetAt is { } weekly)
            {
                parts.Add($"Wk {FormatReset(weekly)}");
            }

            return parts.Count == 0 ? string.Empty : "Resets · " + string.Join(" · ", parts);
        }
    }

    public Visibility ResetsVisibility => ResetsText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private static string FormatReset(DateTimeOffset resetAt)
    {
        DateTime local = resetAt.LocalDateTime;
        string time = local.ToString("HH:mm", CultureInfo.CurrentCulture);
        return local.Date == DateTime.Today
            ? time
            : $"{local.ToString("ddd", CultureInfo.CurrentCulture).TrimEnd('.')} {time}";
    }

    // Raw plan identifiers come back as "max", "plus", "max_20x", …
    public string PlanDisplay
    {
        get
        {
            string[] tokens = Plan
                .Replace('_', ' ')
                .Replace('-', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(' ', tokens.Select(token => char.ToUpperInvariant(token[0]) + token[1..]));
        }
    }

    // The account email stays private: only plan + a redacted hint are shown.
    public string DetailText
    {
        get
        {
            string redacted = RedactEmail(Email);
            string plan = PlanDisplay;
            if (plan.Length > 0 && redacted.Length > 0)
            {
                return $"{plan} · {redacted}";
            }

            return plan.Length > 0 ? plan : redacted;
        }
    }

    private static string RedactEmail(string email)
    {
        int at = email.IndexOf('@');
        if (at <= 0)
        {
            return string.Empty;
        }

        return $"{email[0]}•••{email[at..]}";
    }
}

internal static class MockUsageData
{
    internal static readonly Color CodexAccentColor = ParseHexColor("#60CDFF");
    internal static readonly Color ClaudeAccentColor = ParseHexColor("#E08A5E");

    public static ProfileUsage PrimaryProfile => CreatePrimaryProfile(AppConfigStore.Load());

    public static ProfileUsage CreatePrimaryProfile(AppConfig config)
    {
        ProfileConfig profile = config.Profiles.FirstOrDefault(profile =>
                AppConfigStore.IsProvider(profile, "codex") && profile.Enabled)
            ?? config.Profiles.FirstOrDefault(profile => AppConfigStore.IsProvider(profile, "codex"))
            ?? config.Profiles.First();
        return new ProfileUsage(profile.Label, string.Empty, "Pro", 72, 54, true, CodexAccentColor)
        {
            Provider = profile.Provider,
            Home = profile.Home,
            HasCodexAuth = CodexAccountSwitchService.HasProfileAuth(profile),
            IsActiveCodexAccount = CodexAccountSwitchService.IsActiveProfile(profile)
        };
    }

    public static ProfileUsage CreateUnavailableProfile(ProfileConfig profile)
    {
        return new ProfileUsage(profile.Label, string.Empty, string.Empty, 0, 0, false, CodexAccentColor)
        {
            Provider = profile.Provider,
            Home = profile.Home,
            HasPrimaryQuota = false,
            HasWeeklyQuota = false,
            HasCodexAuth = CodexAccountSwitchService.HasProfileAuth(profile),
            IsActiveCodexAccount = CodexAccountSwitchService.IsActiveProfile(profile)
        };
    }

    public static IReadOnlyList<ProviderUsage> CreateProviders(AppConfig config)
    {
        List<ProfileUsage> codexProfiles = config.Profiles
            .Where(profile => AppConfigStore.IsProvider(profile, "codex") && profile.Enabled)
            .Select((profile, index) => new ProfileUsage(
                profile.Label,
                string.Empty,
                index == 0 ? "Pro" : "Team",
                index == 0 ? 72 : 43,
                index == 0 ? 54 : 61,
                true,
                CodexAccentColor)
            {
                Provider = profile.Provider,
                Home = profile.Home,
                HasCodexAuth = CodexAccountSwitchService.HasProfileAuth(profile),
                IsActiveCodexAccount = CodexAccountSwitchService.IsActiveProfile(profile)
            })
            .ToList();
        if (codexProfiles.Count == 0)
        {
            codexProfiles.Add(CreatePrimaryProfile(config));
        }

        List<ProviderUsage> providers =
        [
            new ProviderUsage("Codex", "C", codexProfiles)
        ];

        List<ProfileUsage> claudeProfiles = config.Profiles
            .Where(profile => AppConfigStore.IsProvider(profile, "claude") && profile.Enabled)
            .Select(profile => new ProfileUsage("Personal", string.Empty, "Pro", 88, 76, true, ClaudeAccentColor) with
            {
                Label = profile.Label,
                Provider = profile.Provider,
                Home = profile.Home
            })
            .ToList();

        if (claudeProfiles.Count > 0)
        {
            providers.Add(new ProviderUsage("Claude", "A", claudeProfiles));
        }

        return providers;
    }

    public static IReadOnlyList<ProviderUsage> CreateProviders()
    {
        AppConfig config = AppConfigStore.Load();
        return CreateProviders(config);
    }

    private static Color ParseHexColor(string value)
    {
        string hex = value.TrimStart('#');
        byte r = Convert.ToByte(hex[..2], 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
        return Color.FromArgb(255, r, g, b);
    }
}
