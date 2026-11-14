using Atlas.Server.Platform.Live;
using Microsoft.AspNetCore.SignalR;

namespace Atlas.Server.Tests.Platform;

/// <summary>An IHubContext that records what the server sends instead of sending it, with an optional hook that runs while a
/// message is being sent (to observe what the database holds at that moment).</summary>
internal sealed class RecordingHubContext : IHubContext<LiveHub>
{
    private readonly List<Sent> m_sent = [];

    public RecordingHubContext()
    {
        Clients = new RecordingClients(this);
    }

    public IHubClients Clients { get; }

    public IGroupManager Groups => throw new NotSupportedException("Live channels pick their recipients themselves.");

    public Func<Sent, Task>? OnSend { get; set; }

    public IReadOnlyList<Sent> Sent
    {
        get
        {
            lock (m_sent)
            {
                return m_sent.ToArray();
            }
        }
    }

    private async Task RecordAsync(Sent sent)
    {
        if (OnSend is { } hook)
        {
            await hook(sent);
        }
        lock (m_sent)
        {
            m_sent.Add(sent);
        }
    }

    private sealed class RecordingClients(RecordingHubContext owner) : IHubClients
    {
        public IClientProxy All => Proxy(["*"]);
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => Proxy([connectionId]);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy(connectionIds);
        public IClientProxy Group(string groupName) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();

        private IClientProxy Proxy(IReadOnlyList<string> connections) => new RecordingProxy(owner, connections.ToArray());
    }

    private sealed class RecordingProxy(RecordingHubContext owner, string[] connections) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => owner.RecordAsync(new Sent(connections, method, args.Single()));
    }
}

internal sealed record Sent(IReadOnlyList<string> Connections, string Method, object? Payload);
