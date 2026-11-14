using Microsoft.Extensions.DependencyInjection;
using NHibernate;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Collaboration.Contracts;
using Atlas.Markdown.Sync;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Tests.Platform;

/// <summary>Relay persistence over one AtlasTestHost schema, through the production adapter: scope per call, one message per operation.</summary>
internal sealed class TestCollaboration
{
    private readonly AtlasTestHost m_host;
    private readonly PostgresCollaborationStore m_store;

    public TestCollaboration(AtlasTestHost host)
    {
        m_host = host;
        m_store = new PostgresCollaborationStore(host.Services.GetRequiredService<IServiceScopeFactory>());
    }

    public Task<WikiCollaborationSession> OpenAsync(Guid pageId, UserAccount actor, CancellationToken ct = default)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync<WikiCollaborationSession>(new OpenCollaboration(pageId), ct);
    }

    public Task<StoredDocument?> LoadAsync(string documentId, CancellationToken ct = default)
    {
        return m_store.LoadAsync(documentId, ct);
    }

    public Task<MarkdownDocumentSchema?> BindSchemaAsync(string documentId, MarkdownDocumentSchema? schema, CancellationToken ct = default)
    {
        return m_store.BindSchemaAsync(documentId, schema, ct);
    }

    public Task<AppendUpdateResult> AppendUpdateAsync(string documentId, Guid clientUpdateId, string userId, DateTimeOffset receivedAtUtc, ReadOnlyMemory<byte> update, CancellationToken ct = default)
    {
        return m_store.AppendUpdateAsync(documentId, clientUpdateId, userId, receivedAtUtc, update, ct);
    }

    public Task<FlushResult> FlushAsync(string documentId, DocSnapshot snapshot, CancellationToken ct = default)
    {
        return m_store.FlushAsync(documentId, snapshot, ct);
    }

    public Task<bool> HasExternalChangeAsync(string documentId, CancellationToken ct = default)
    {
        return m_store.HasExternalChangeAsync(documentId, ct);
    }

    public async Task<CollaborationCheckpoint> ValidateCheckpointAsync(ISession session, Page page, Guid generation, long? sequence, CancellationToken ct)
    {
        var current = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).Select(x => x.Id).SingleOrDefaultAsync<Guid>(ct);
        return await new CollaborationCheckpoints(session).ValidateCheckpointAsync(page, generation, current, sequence, ct);
    }

    public Task AdvanceCheckpointAsync(ISession session, Page page, PageRevision revision, CancellationToken ct, bool markSaved = false)
    {
        return new CollaborationCheckpoints(session).AdvanceCheckpointAsync(page, revision, ct, markSaved);
    }

    public Task ResetAsync(ISession session, Page page, PageRevision revision, CancellationToken ct)
    {
        return new CollaborationCheckpoints(session).ResetAsync(page, revision, ct);
    }
}
