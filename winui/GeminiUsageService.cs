namespace FluentAgentBar;

// Placeholder: replaced by the real Gemini usage fetcher.
internal sealed class GeminiUsageService : IDisposable
{
    public Task<ProviderUsageSnapshot> FetchAsync(string home, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Gemini usage is not implemented yet.");
    }

    public void Dispose()
    {
    }
}
