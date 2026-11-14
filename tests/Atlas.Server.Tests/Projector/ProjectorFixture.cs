using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Tests.Platform;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;

namespace Atlas.Server.Tests.Projector;

/// <summary>
/// A host with every module on: a WikiAdmin, a Writer and a Reader of the deployment space, an Outsider without a role. Pages are
/// created and saved through the page handlers with plain content; <see cref="WriteContentAsync"/> then puts requirement blocks
/// into a revision directly, as an editor with the plugin would have saved them (R1 makes the save accept them).
/// </summary>
internal sealed class ProjectorFixture : IAsyncDisposable
{
    private ProjectorFixture(AtlasTestHost host)
    {
        Host = host;
        Pages = new TestPages(host);
    }

    public AtlasTestHost Host { get; }
    public TestPages Pages { get; }
    public UserAccount Admin { get; private set; } = null!;
    public UserAccount Writer { get; private set; } = null!;
    public UserAccount Reader { get; private set; } = null!;
    public UserAccount Outsider { get; private set; } = null!;
    public Guid SpaceId { get; private set; }

    // These tests cover the block commands, indexer and tools on pages in the requirement block shape. Since 3.6 reading a page converts
    // such a page to headings (repair on open), except a page an administrator rolled back: every page of the host, however created
    // (handlers, deployments, presets, MCP tools, bulk seeds), gets a rolled-back backup row so it is served as stored. The heading shape
    // has its own tests (RequirementHeadingTreeTests, RequirementsRepairOnOpenTests).
    internal const string KeepBlockShapeSql = """
        CREATE FUNCTION keep_block_shape() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
          INSERT INTO "RequirementConversionBackup"("RevisionId","PageId","ContentJson","Markdown","RolledBack") VALUES (gen_random_uuid(), NEW."Id", '', '', true);
          RETURN NULL; END $$;
        CREATE TRIGGER keep_block_shape AFTER INSERT ON "Page" FOR EACH ROW EXECUTE FUNCTION keep_block_shape()
        """;

    // The trigger is part of the shared "projector" configuration: installed once per host, before its first test writes a page.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AtlasTestHost, Lazy<Task>> s_blockShape = new();

    public static async Task<ProjectorFixture> CreateAsync()
    {
        var host = await AtlasTestHost.CreateAsync("atlas_projector_", modules: TestModules.AllEnabled, sharedAs: "projector");
        await s_blockShape.GetValue(host, shared => new Lazy<Task>(() => shared.SeedAsync(session => session.CreateSQLQuery(KeepBlockShapeSql).ExecuteUpdateAsync()))).Value;
        var fixture = new ProjectorFixture(host)
        {
            Admin = await host.SeedUserAsync("admin", isWikiAdmin: true),
            Writer = await host.SeedUserAsync("writer"),
            Reader = await host.SeedUserAsync("reader"),
            Outsider = await host.SeedUserAsync("outsider")
        };
        fixture.SpaceId = await host.SeedSpaceAsync("Nasazení", (fixture.Writer, SpaceRole.Writer), (fixture.Reader, SpaceRole.Reader));
        return fixture;
    }

    public async Task<T> As<T>(UserAccount actor, object message)
    {
        Host.Actor.Current = actor;
        return await Host.InvokeAsync<T>(message);
    }

    public async Task As(UserAccount actor, object message)
    {
        Host.Actor.Current = actor;
        await Host.InvokeAsync(message);
    }

    /// <summary>A page of the Writer whose content is <paramref name="json"/>; published with it unless <paramref name="publish"/> is false.</summary>
    public async Task<Guid> PageAsync(string title, string json, bool publish = true)
    {
        var page = await Pages.CreateAsync(new CreatePageRequest(SpaceId, null, title, Json(Paragraph(title)), null), Writer, default);
        await WriteContentAsync(page.Id, json);
        if (publish)
        {
            await Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), Writer, default);
        }
        return page.Id;
    }

    /// <summary>A published space template (a deployment preset when it holds blocks) of the WikiAdmin with <paramref name="json"/> as its content.</summary>
    public async Task<Guid> TemplateAsync(string title, string json)
    {
        var template = await Pages.CreateAsync(new CreatePageRequest(SpaceId, null, title, json, null, IsTemplate: true), Admin, default);
        await Pages.PublishAsync(template.Id, new PublishPageRequest(template.Version), Admin, default);
        return template.Id;
    }

    /// <summary>A new working revision saved by the Writer, then given <paramref name="json"/>: the publication stays as it was.</summary>
    public async Task SaveWorkingAsync(Guid pageId, string json)
    {
        var page = await Pages.GetAsync(pageId, Writer, default);
        await Pages.SaveAsync(pageId, new SavePageRequest(page.Title, Json(Paragraph("rozpracováno " + Guid.NewGuid())), null, page.Version), Writer, default);
        await WriteContentAsync(pageId, json);
    }

    public Task WriteContentAsync(Guid pageId, string json) => Host.SeedAsync(session => session
        // Written past the save, as an import: no change summary tells which blocks it changed.
        .CreateSQLQuery("""UPDATE "PageRevision" SET "ContentJson"=:json,"ChangeSummary"=NULL WHERE "PageId"=:page AND "ActiveTo" IS NULL""")
        .SetParameter("json", json).SetParameter("page", pageId).ExecuteUpdateAsync());

    /// <summary>Only the page's own grants (and the space Admin) apply: the Writer and the Reader lose it.</summary>
    public Task OwnAccessAsync(Guid pageId) => Host.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=:id""", pageId);

    /// <summary>A direct page grant (1 reader, 2 writer), as the page access dialog writes it.</summary>
    public Task GrantAsync(Guid pageId, UserAccount user, int role) => Host.SeedAsync(session => session
        .CreateSQLQuery("""INSERT INTO "ResourceGrant"("Id","PageId","UserId","Role") VALUES (gen_random_uuid(),:page,:user,:role)""")
        .SetParameter("page", pageId).SetParameter("user", user.Id).SetParameter("role", role).ExecuteUpdateAsync());

    /// <summary>An image attachment of the page (a GIF in the attachment store), for content that shows it by its canonical URL. Its bytes
    /// are its own (a random tail after the trailer), so a test tells its copies apart in the store the host's tests share.</summary>
    public async Task<Guid> ImageAsync(Guid pageId)
    {
        var id = Guid.NewGuid();
        var files = Host.Services.GetRequiredService<Atlas.Server.Features.Attachments.AttachmentFiles>();
        var (size, hash, mime) = await files.WriteAsync(id, new MemoryStream([.. "GIF89a\u0001\0\u0001\0\0\0\0;"u8, .. Guid.NewGuid().ToByteArray()]), true, default);
        await Host.SeedAsync(session => session.SaveAsync(new Atlas.Server.Features.Attachments.Domain.Attachment
        {
            Id = id, PageId = pageId, UploadedBy = Writer.Id, Name = "logo.gif", MimeType = mime, Size = size, Sha256 = hash, IsImage = true,
            ActiveFrom = DateTime.UtcNow
        }));
        return id;
    }

    public async Task<long> ScalarAsync(string sql, Guid id)
    {
        using var session = Host.OpenSession();
        return Convert.ToInt64(await session.CreateSQLQuery(sql).SetParameter("id", id).UniqueResultAsync());
    }

    /// <summary>The index of every page of the test's space made current, as the outbox's deliveries keep it in a running server (this host has no outbox).</summary>
    public async Task FreshIndexAsync()
    {
        using var session = Host.OpenSession();
        using var transaction = session.BeginTransaction();
        await Atlas.Server.Features.Projector.Persistence.ProjectorIndex.EnsureFreshAsync(session, null, default, SpaceId);
        await transaction.CommitAsync();
    }

    public async Task<string> IndexAsync(Guid pageId)
    {
        using var session = Host.OpenSession();
        // The index follows the page in code (ProjectorIndex): a read makes it current first, as the module's own reads do.
        await Atlas.Server.Features.Projector.Persistence.ProjectorIndex.EnsureFreshAsync(session, [pageId], default);
        return await session.CreateSQLQuery("""
            SELECT coalesce(string_agg("BlockId" || ':' || "State" || ':' || CASE WHEN "IsWorking" THEN 't' ELSE 'f' END || CASE WHEN "IsPublished" THEN 't' ELSE 'f' END, ' '
              ORDER BY "IsWorking","Position"),'')
            FROM "ProjectorRequirementIndex" WHERE "PageId"=:page
            """).SetParameter("page", pageId).UniqueResultAsync<string>();
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
