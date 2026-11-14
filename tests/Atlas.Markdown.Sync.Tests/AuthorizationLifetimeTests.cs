using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Markdown.Sync;

namespace Atlas.Markdown.Sync.Tests;

public class AuthorizationLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Timed_out_lookup_keeps_its_scope_and_bounded_slot_until_actual_completion()
    {
        var lifetime = new AuthorizationLifetime { BlockAuthorization = true };
        await using var services = Services(lifetime);
        var access = services.GetRequiredService<DocumentAccessCoordinator>();
        try
        {
            var pending = access.AuthorizeAsync(new CollabUser("alice", "Alice"), "doc", CancellationToken.None);
            await lifetime.AuthorizationEntered.Task.WaitAsync(Timeout);
            Assert.False((await pending.WaitAsync(Timeout)).CanRead);
            Assert.False(lifetime.DisposalEntered.Task.IsCompleted);

            // The first authenticator ignores cancellation. Its slot must still be occupied:
            // another deadline may expire, but cannot allocate another scoped dependency.
            Assert.False((await access.AuthorizeAsync(new CollabUser("bob", "Bob"), "doc", CancellationToken.None)
                .WaitAsync(Timeout)).CanRead);
            Assert.Equal(1, lifetime.Instances);
            Assert.Equal(1, lifetime.Lookups);
            Assert.False(lifetime.DisposalEntered.Task.IsCompleted);

            lifetime.ReleaseAuthorization.TrySetResult();
            await lifetime.Disposed.Task.WaitAsync(Timeout);
            Assert.True(lifetime.AuthorizationCompleted.Task.IsCompleted);
            Assert.False(lifetime.DisposedDuringAuthorization);
        }
        finally
        {
            lifetime.ReleaseAuthorization.TrySetResult();
            lifetime.ReleaseDisposal.TrySetResult();
            await lifetime.Disposed.Task.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Slow_async_scope_disposal_does_not_hold_authorization_result_or_document_gate()
    {
        var lifetime = new AuthorizationLifetime { BlockDisposal = true };
        await using var services = Services(lifetime);
        var access = services.GetRequiredService<DocumentAccessCoordinator>();
        var doc = services.GetRequiredService<DocumentRegistry>().GetOrCreate("doc");
        doc.Peers["connection"] = new Peer
        {
            ConnectionId = "connection", User = new CollabUser("alice", "Alice"),
            CanWrite = true, JoinOrder = 1,
        };
        try
        {
            // Only a refresh looks permissions up; collaboration requests use the peer's last access.
            var admitted = access.RefreshAsync("doc");
            await lifetime.DisposalEntered.Task.WaitAsync(Timeout);
            await admitted.WaitAsync(Timeout);
            Assert.False(lifetime.Disposed.Task.IsCompleted);
            using var deadline = new CancellationTokenSource(Timeout);
            await doc.Gate.WaitAsync(deadline.Token);
            try { Assert.True(doc.Peers.TryRemove("connection", out _)); }
            finally { doc.Gate.Release(); }
            Assert.False(lifetime.Disposed.Task.IsCompleted);
        }
        finally
        {
            lifetime.ReleaseDisposal.TrySetResult();
            await lifetime.Disposed.Task.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Denied_preflight_does_not_allocate_live_document_shells()
    {
        var auth = new CountingAuthenticator { Access = DocAccess.Denied };
        await using var host = await HubTestHost.StartAsync(
            options => options.AccessRefreshInterval = TimeSpan.FromHours(1), authenticator: auth);
        await using var denied = host.Connection("denied");
        await denied.StartAsync();
        for (var index = 0; index < 8; index++)
        {
            await Assert.ThrowsAsync<HubException>(() => denied.InvokeAsync<JoinResult>("JoinDocument", $"denied-{index}")
                .WaitAsync(Timeout));
        }
        Assert.Equal(8, auth.Lookups);
        Assert.Equal(0, host.Registry.Count);
        Assert.Equal(0, host.Registry.TotalPeerCount);
    }

    [Fact]
    public async Task Awareness_from_nonmember_does_not_lookup_acl_of_existing_members()
    {
        var auth = new CountingAuthenticator();
        await using var host = await HubTestHost.StartAsync(
            options => options.AccessRefreshInterval = TimeSpan.FromHours(1), authenticator: auth);
        await using var member = host.Connection("member");
        await using var stranger = host.Connection("stranger");
        await member.StartAsync();
        await stranger.StartAsync();
        Assert.True((await member.InvokeAsync<JoinResult>("JoinDocument", "doc").WaitAsync(Timeout)).IsSnapshotter);
        var before = auth.Lookups;

        // Await the actual hub invocation: no timing delay is needed to prove that its
        // authorization path finished without inspecting or refreshing the member ACL.
        await stranger.InvokeAsync("SendAwareness", "doc", new byte[] { 1 }).WaitAsync(Timeout);

        Assert.Equal(before, auth.Lookups);
        Assert.Equal(1, host.Registry.TotalPeerCount);
        Assert.True(host.Registry.TryGet("doc")!.Peers.ContainsKey(member.ConnectionId!));
    }

    [Fact]
    public async Task A_join_authorizes_once_and_again_only_after_a_revalidation_began()
    {
        var auth = new CountingAuthenticator();
        await using var host = await HubTestHost.StartAsync(
            options => options.AccessRefreshInterval = TimeSpan.FromHours(1), authenticator: auth);
        await using var first = host.Connection("first");
        await first.StartAsync();

        await first.InvokeAsync<JoinResult>("JoinDocument", "doc").WaitAsync(Timeout);
        Assert.Equal(1, auth.Lookups);

        // A revalidation begun after a join authorized makes it authorize again; the result never serves another join.
        var access = host.Access;
        var user = new CollabUser("second", "second");
        var revalidation = access.Revalidations;
        var authorized = await access.AuthorizeAsync(user, "doc", CancellationToken.None);
        Assert.Equal((authorized, revalidation), await access.CurrentAsync(user, "doc", authorized, revalidation, CancellationToken.None));
        Assert.Equal(2, auth.Lookups);
        await access.RefreshAllAsync();
        var before = auth.Lookups;
        Assert.Equal(revalidation + 1, (await access.CurrentAsync(user, "doc", authorized, revalidation, CancellationToken.None)).Revalidation);
        Assert.Equal(before + 1, auth.Lookups);
    }

    private static ServiceProvider Services(AuthorizationLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddAtlasMarkdownSync(options =>
        {
            options.AuthorizationTimeout = TimeSpan.FromMilliseconds(100);
            options.AccessRefreshInterval = TimeSpan.FromHours(1);
            options.MaxConcurrentAuthorizations = 1;
        });
        services.AddSingleton<IDocumentStore>(new InMemoryDocumentStore());
        services.AddScoped<ICollabAuthenticator>(_ => new ScopedAuthenticator(lifetime));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class AuthorizationLifetime
    {
        public bool BlockAuthorization { get; init; }
        public bool BlockDisposal { get; init; }
        public int Instances;
        public int Lookups;
        public bool DisposedDuringAuthorization;
        public TaskCompletionSource AuthorizationEntered { get; } = Signal();
        public TaskCompletionSource ReleaseAuthorization { get; } = Signal();
        public TaskCompletionSource AuthorizationCompleted { get; } = Signal();
        public TaskCompletionSource DisposalEntered { get; } = Signal();
        public TaskCompletionSource ReleaseDisposal { get; } = Signal();
        public TaskCompletionSource Disposed { get; } = Signal();
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ScopedAuthenticator : ICollabAuthenticator, IAsyncDisposable
    {
        private readonly AuthorizationLifetime lifetime;
        public ScopedAuthenticator(AuthorizationLifetime lifetime)
        {
            this.lifetime = lifetime;
            Interlocked.Increment(ref lifetime.Instances);
        }

        public Task<CollabUser?> AuthenticateAsync(HttpContext handshakeContext, CancellationToken ct)
            => Task.FromResult<CollabUser?>(new CollabUser("alice", "Alice"));

        public async Task<DocAccess> AuthorizeAsync(CollabUser user, string documentId, CancellationToken ct)
        {
            Interlocked.Increment(ref lifetime.Lookups);
            lifetime.AuthorizationEntered.TrySetResult();
            if (lifetime.BlockAuthorization) await lifetime.ReleaseAuthorization.Task; // deliberately ignores ct
            lifetime.AuthorizationCompleted.TrySetResult();
            return DocAccess.Write;
        }

        public async ValueTask DisposeAsync()
        {
            lifetime.DisposedDuringAuthorization = !lifetime.AuthorizationCompleted.Task.IsCompleted;
            lifetime.DisposalEntered.TrySetResult();
            if (lifetime.BlockDisposal) await lifetime.ReleaseDisposal.Task;
            lifetime.Disposed.TrySetResult();
        }
    }

    private sealed class CountingAuthenticator : ICollabAuthenticator
    {
        public DocAccess Access { get; init; } = DocAccess.Write;
        private int lookups;
        public int Lookups => Volatile.Read(ref lookups);
        public Task<CollabUser?> AuthenticateAsync(HttpContext handshakeContext, CancellationToken ct)
        {
            var user = handshakeContext.Request.Query["user"].ToString();
            return Task.FromResult<CollabUser?>(new CollabUser(user, user));
        }
        public Task<DocAccess> AuthorizeAsync(CollabUser user, string documentId, CancellationToken ct)
        {
            Interlocked.Increment(ref lookups);
            return Task.FromResult(Access);
        }
    }
}
