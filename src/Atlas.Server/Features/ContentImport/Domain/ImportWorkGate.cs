namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>A single host runs bounded imports sequentially; regular wiki requests are unaffected. Replaces the former static semaphore.
/// The endpoint (ContentImportEndpoints), not any handler, holds the gate for the whole preview/commit call;
/// PreviewImportHandler/CommitImportHandler assume single-flight and never wait on or release it themselves.</summary>
public sealed class ImportWorkGate : IDisposable
{
    private readonly SemaphoreSlim m_gate = new(1, 1);

    public Task WaitAsync(CancellationToken ct)
    {
        return m_gate.WaitAsync(ct);
    }

    public void Release()
    {
        m_gate.Release();
    }

    public void Dispose()
    {
        m_gate.Dispose();
    }
}
