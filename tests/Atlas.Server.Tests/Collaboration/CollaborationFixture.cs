using System.Security.Cryptography;
using System.Text.Json;
using Atlas.Markdown.Sync;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>One live page over one AtlasTestHost schema. Shared by the store tests and the checkpoint tests.</summary>
internal sealed class CollaborationFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;

    private CollaborationFixture(AtlasTestHost host)
    {
        m_host = host;
        Store = new TestCollaboration(host);
        Pages = new TestPages(host);
        History = new TestPageHistory(host);
    }

    public AtlasTestHost Host => m_host;
    public AtlasDatabase Database => m_host.Database;
    public TestCollaboration Store { get; }
    public TestPages Pages { get; }
    public TestPageHistory History { get; }
    public UserAccount Writer { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("writer"), DisplayName = "Writer" };
    public UserAccount Reader { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("reader"), DisplayName = "Reader" };
    public PageDto Page { get; private set; } = null!;

    /// <param name="isolated">A host and database of the test's own, for a test that changes the schema.</param>
    public static async Task<CollaborationFixture> CreateAsync(bool isolated = false)
    {
        var f = new CollaborationFixture(isolated ? await AtlasTestHost.CreateIsolatedAsync("atlas_collab_") : await AtlasTestHost.CreateAsync("atlas_collab_"));
        var space = new Space { Id = Guid.NewGuid(), Name = "Collaboration" };
        await f.m_host.SeedAsync(async session =>
        {
            await session.SaveAsync(f.Writer);
            await session.SaveAsync(f.Reader);
            await session.SaveAsync(space);
            await session.FlushAsync();
            foreach (var user in new[] { f.Writer, f.Reader })
            {
                await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = space.Id, UserId = user.Id, Role = user == f.Writer ? 2 : 1 });
            }
        });
        f.Page = await f.Pages.CreateAsync(new(space.Id, null, "Initial",
            "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}}", "Initial"), f.Writer, default);
        return f;
    }

    public static DocSnapshot Snapshot(long sequence)
    {
        byte[] bytes = [7, 8];
        using var document = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}");
        return new(bytes, sequence, new(sequence, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            "Snapshot", document.RootElement.Clone(), "Snapshot"));
    }

    public Task<AppendUpdateResult> Append(string id, Guid updateId, byte[] bytes)
        => Store.AppendUpdateAsync(id, updateId, Writer.Id.ToString(), DateTimeOffset.UtcNow, bytes, default);

    public async Task SeedLog(Guid generation, int count, int payloadBytes)
    {
        using var session = m_host.OpenSession();
        using var tx = session.BeginTransaction();
        await session.CreateSQLQuery("""
            INSERT INTO "CollaborationUpdate"("PageId","Generation","Sequence","ClientUpdateId","UserId","ReceivedAt","Payload")
            SELECT :page,:generation,n,gen_random_uuid(),:author,now(),decode(repeat('01',:bytes),'hex')
            FROM generate_series(1,:count) n
            """).SetParameter("page", Page.Id).SetParameter("generation", generation).SetParameter("author", Writer.Id)
            .SetParameter("bytes", payloadBytes).SetParameter("count", count).ExecuteUpdateAsync();
        await session.CreateSQLQuery("UPDATE \"PageCollaboration\" SET \"Sequence\"=:count, \"PendingCount\"=:count, \"PendingBytes\"=:total WHERE \"PageId\"=:page")
            .SetParameter("count", count).SetParameter("total", (long)count * payloadBytes).SetParameter("page", Page.Id).ExecuteUpdateAsync();
        await tx.CommitAsync();
    }

    public async Task<int> RevisionCount()
    {
        using var session = m_host.OpenSession();
        return await session.QueryOver<PageRevision>().Where(x => x.PageId == Page.Id).RowCountAsync();
    }

    public ValueTask DisposeAsync() => m_host.DisposeAsync();
}
