namespace FluentAgentBar;

// Provider-neutral usage payload returned by the Gemini, Cursor and Grok
// services. UsageService maps it onto ProfileUsage/QuotaGroupUsage and adds
// the provider accent colour, so the services stay free of WinUI types and
// remain unit-testable.
internal sealed record ProviderQuotaWindow(
    string Label,
    int RemainingPercent,
    DateTimeOffset? ResetAt);

internal sealed record ProviderQuotaGroup(
    string Name,
    IReadOnlyList<ProviderQuotaWindow> Windows);

internal sealed record ProviderUsageSnapshot(
    string Plan,
    string Email,
    IReadOnlyList<ProviderQuotaGroup> Groups);

// Thrown when the provider has no usable local credentials; the profile is
// shown as "Login Required" instead of a generic unavailable state.
internal sealed class ProviderLoginRequiredException : Exception
{
    public ProviderLoginRequiredException(string message) : base(message)
    {
    }

    public ProviderLoginRequiredException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

// Thrown when the provider's CLI is too old to report usage and only the user
// can update it; the profile is shown as "Update Required" with the message as
// the hint, instead of a generic unavailable state.
internal sealed class ProviderUpdateRequiredException : Exception
{
    public ProviderUpdateRequiredException(string message) : base(message)
    {
    }
}
