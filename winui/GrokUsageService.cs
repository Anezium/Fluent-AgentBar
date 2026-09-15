namespace FluentAgentBar;

// Placeholder: replaced by the real Grok usage fetcher.
internal sealed class GrokUsageService : IDisposable
{
    public Task<ProviderUsageSnapshot> FetchAsync(string home, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Grok usage is not implemented yet.");
    }

    public void Dispose()
    {
    }
}
