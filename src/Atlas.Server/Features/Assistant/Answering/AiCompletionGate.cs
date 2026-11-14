using System.Collections.Concurrent;
using Atlas.Server.Features.Assistant.Contracts;

namespace Atlas.Server.Features.Assistant.Answering;

/// <summary>One in-flight completion per message on Atlas's single application host. Holds no database lock across the model call.</summary>
public sealed class AiCompletionGate
{
    private readonly ConcurrentDictionary<Guid, Lease> m_running = new();

    public IDisposable Enter(Guid messageId)
    {
        var lease = new Lease(m_running, messageId);
        if (!m_running.TryAdd(messageId, lease)) throw AiAnswerProblems.InProgress();
        return lease;
    }

    private sealed class Lease(ConcurrentDictionary<Guid, Lease> running, Guid messageId) : IDisposable
    {
        public void Dispose() => running.TryRemove(new KeyValuePair<Guid, Lease>(messageId, this));
    }
}
