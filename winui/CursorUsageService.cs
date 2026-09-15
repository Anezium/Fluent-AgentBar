namespace FluentAgentBar;

// Placeholder: replaced by the real Cursor usage fetcher.
internal sealed class CursorUsageService : IDisposable
{
    public Task<ProviderUsageSnapshot> FetchAsync(string home, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Cursor usage is not implemented yet.");
    }

    public void Dispose()
    {
    }
}
