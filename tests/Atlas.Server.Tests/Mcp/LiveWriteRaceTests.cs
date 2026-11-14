using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Tests.Collaboration;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using NHibernate;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Mcp;

/// <summary>
/// 3.6 review (P1): a live write's revision is the live document's base only once its update is in the log, so no checkpoint of the live
/// document stores the page without it, and a crash between the two is undone on the next open; its target is the same blocks at the same
/// place, never identical text elsewhere.
/// </summary>
public sealed class LiveWriteRaceTests
{
    [PostgreSqlFact]
    public async Task A_browser_checkpoint_between_the_agents_commit_and_its_append_is_refused_and_keeps_the_agents_change()
    {
        var authority = new PausedAuthority();
        await using var f = await McpSite.CreateAsync(services: services => services.AddSingleton<ILiveWriteAuthority>(authority));
        f.Site.OnDisposing = () => authority.Release.TrySetResult();
        var (live, page, generation) = await McpLiveWriteTests.OpenLiveAsync(f);
        await using var hub = live.Hub;
        var pageId = Guid.Parse(page.Id);
        var stored = (string)(await f.Site.ScalarAsync("SELECT \"ContentJson\" FROM \"PageRevision\" WHERE \"PageId\"=@page AND \"ActiveTo\" IS NULL", ("page", pageId)))!;
        var projection = (string)(await f.Site.ScalarAsync("SELECT \"ProjectionJson\" FROM \"PageCollaboration\" WHERE \"PageId\"=@page", ("page", pageId)))!;
        // The browser's save of the live document as it stands before the agent's update: the old text.
        var oldContent = JsonNode.Parse(stored)!;
        oldContent["document"] = JsonNode.Parse(projection);
        oldContent["plugins"] = JsonSerializer.SerializeToNode(CollaborationSchema.KnownHostSchemas[^1].Plugins.Select(x => new { id = x.Id, version = x.Version }));
        var (_, client) = await McpAttachmentToolTests.DraftClientAsync(f);
        var agentWrite = CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = page.Version, headingId = "b", markdown = "## B\n\nText B přepsal agent.\n"
        });
        try
        {
            await authority.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Contains("přepsal agent", (string)(await f.Site.ScalarAsync("SELECT \"Markdown\" FROM \"PageRevision\" WHERE \"PageId\"=@page AND \"ActiveTo\" IS NULL", ("page", pageId)))!);
            // On the version the browser had, and on the agent's version: neither stores the old text over the agent's revision.
            foreach (var expectedVersion in new[] { page.Version, page.Version + 1 })
            {
                var browserSave = await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/pages/{page.Id}", new
                {
                    title = "Živá", contentJson = oldContent.ToJsonString(), expectedVersion, collaborationGeneration = generation
                });
                Assert.True(browserSave.StatusCode == HttpStatusCode.Conflict, await browserSave.Content.ReadAsStringAsync());
            }
            // A rename on the agent's version waits too: moving the base past the agent's revision would let the old text through again.
            var renamed = await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/pages/{page.Id}/title", new { title = "Přejmenovaná", expectedVersion = page.Version + 1 });
            Assert.Equal(HttpStatusCode.Conflict, renamed.StatusCode);
            Assert.Contains("collaboration_pending", await renamed.Content.ReadAsStringAsync());
        }
        finally
        {
            authority.Release.TrySetResult();
        }
        var result = await agentWrite;
        Assert.True(result.IsError != true, Text(result));
        var current = (string)(await f.Site.ScalarAsync("SELECT \"Markdown\" FROM \"PageRevision\" WHERE \"PageId\"=@page AND \"ActiveTo\" IS NULL", ("page", pageId)))!;
        Assert.Contains("Text B přepsal agent.", current);
        // With its update appended the agent's revision is the live document's base, its sequence saved, on the same generation.
        Assert.Equal($"{generation}:1:1:true",await f.Site.ScalarAsync("""
            SELECT c."Generation" || ':' || c."Sequence" || ':' || c."LastSavedSequence" || ':' || (c."BaseRevisionId" = r."Id")
            FROM "PageCollaboration" c JOIN "PageRevision" r ON r."PageId" = c."PageId" AND r."ActiveTo" IS NULL WHERE c."PageId"=@page
            """, ("page", pageId)));
        // Now the rename goes through and keeps the live document's base on the current revision.
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/pages/{page.Id}/title",
            new { title = "Přejmenovaná", expectedVersion = page.Version + 1 })).StatusCode);
        Assert.Equal("true", await f.Site.ScalarAsync("""
            SELECT (c."BaseRevisionId" = r."Id")::text FROM "PageCollaboration" c JOIN "PageRevision" r ON r."PageId" = c."PageId" AND r."ActiveTo" IS NULL
            WHERE c."PageId"=@page
            """, ("page", pageId)));
    }

    /// <summary>A write that resets the generation (a save without one) between the agent's commit and its append: the append fails on the
    /// old generation, and its fallback leaves the new one alone, where people may already type.</summary>
    [PostgreSqlFact]
    public async Task A_reset_between_the_agents_commit_and_its_append_is_kept_by_the_appends_fallback()
    {
        var authority = new PausedAuthority();
        await using var f = await McpSite.CreateAsync(services: services => services.AddSingleton<ILiveWriteAuthority>(authority));
        f.Site.OnDisposing = () => authority.Release.TrySetResult();
        var (live, page, _) = await McpLiveWriteTests.OpenLiveAsync(f);
        await using var hub = live.Hub;
        var pageId = Guid.Parse(page.Id);
        var (_, client) = await McpAttachmentToolTests.DraftClientAsync(f);
        var agentWrite = CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = page.Version, headingId = "b", markdown = "## B\n\nText B přepsal agent.\n"
        });
        object? reset;
        try
        {
            await authority.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var stored = (string)(await f.Site.ScalarAsync("SELECT \"ContentJson\" FROM \"PageRevision\" WHERE \"PageId\"=@page AND \"ActiveTo\" IS NULL", ("page", pageId)))!;
            var saved = await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/pages/{page.Id}", new
            {
                title = "Živá", contentJson = stored.Replace("Text A.", "Text A uložený."), expectedVersion = page.Version + 1
            });
            Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
            reset = await f.Site.ScalarAsync("SELECT \"Generation\" FROM \"PageCollaboration\" WHERE \"PageId\"=@page", ("page", pageId));
        }
        finally
        {
            authority.Release.TrySetResult();
        }
        Assert.True((await agentWrite).IsError != true);
        Assert.Equal($"{reset}:true", await f.Site.ScalarAsync("""
            SELECT c."Generation" || ':' || (c."BaseRevisionId" = r."Id") FROM "PageCollaboration" c
            JOIN "PageRevision" r ON r."PageId" = c."PageId" AND r."ActiveTo" IS NULL WHERE c."PageId"=@page
            """, ("page", pageId)));
    }

    /// <summary>
    /// What a crash between a live write's commit and its append leaves behind: a generation whose base is not the current revision. People
    /// cannot type into it any more, nothing saves it over the revision, and the next open starts the page again from the revision.
    /// </summary>
    [PostgreSqlFact]
    public async Task A_generation_left_behind_its_revision_takes_no_typing_or_checkpoint_and_the_next_open_starts_from_the_revision()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var first = (await f.Store.OpenAsync(f.Page.Id, f.Writer)).Generation;
        var saved = await f.Pages.SaveAsync(f.Page.Id, new SavePageRequest("Initial",
            "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Agent\"}]}]}}",
            "Agent", f.Page.Version), f.Writer, default);
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        Assert.NotEqual(first, opened.Generation);
        await f.Append(opened.DocumentId, Guid.NewGuid(), [1]);
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(1), default);
        // The crash: what people typed was saved before the agent's write, whose revision is current; the live document's base is still
        // the revision before it.
        using (var session = f.Host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            Assert.Equal(1, await session.CreateSQLQuery("""
                UPDATE "PageCollaboration" c SET "BaseRevisionId" = r."Id", "LastSavedSequence" = c."Sequence" FROM "PageRevision" r
                WHERE r."PageId" = c."PageId" AND r."Version" = 1 AND c."PageId" = :page
                """).SetParameter("page", f.Page.Id).ExecuteUpdateAsync());
            await transaction.CommitAsync();
        }

        await Assert.ThrowsAsync<DocumentSupersededException>(() => f.Append(opened.DocumentId, Guid.NewGuid(), [1]));
        using (var session = f.Host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var page = await session.GetAsync<Page>(f.Page.Id, LockMode.Upgrade);
            Assert.Equal("collaboration_pending", (await Assert.ThrowsAsync<ApiException>(() => f.Store.ValidateCheckpointAsync(session, page, opened.Generation, null, default))).Code);
        }
        var reopened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        Assert.NotEqual(opened.Generation, reopened.Generation);
        using (var session = f.Host.OpenSession())
        {
            var state = await session.GetAsync<PageCollaboration>(f.Page.Id);
            Assert.Equal(saved.Version, (await session.GetAsync<PageRevision>(state.BaseRevisionId)).Version);
        }
        Assert.Equal(1, (await f.Append(reopened.DocumentId, Guid.NewGuid(), [2])).Sequence);
    }

    [Fact]
    public void A_target_a_person_changed_never_matches_an_identical_block_elsewhere_in_the_read()
    {
        // Read A B C; a person changed C into A; the agent rewrites that last A.
        Assert.False(LiveWriteTarget.Unchanged(Rich(Paragraph("A"), Paragraph("B"), Paragraph("C")), Rich(Paragraph("A"), Paragraph("B"), Paragraph("A")),
            Rich(Paragraph("A"), Paragraph("B"), Paragraph("AI")), wholeDocument: false));
        // Ambiguous: read A C, now A A. Either A may be the one a person typed, so neither is the agent's to rewrite.
        Assert.False(LiveWriteTarget.Unchanged(Rich(Paragraph("A"), Paragraph("C")), Rich(Paragraph("A"), Paragraph("A")),
            Rich(Paragraph("A"), Paragraph("AI")), wholeDocument: false));
        // A neighbour typed in, or an identical empty paragraph elsewhere, still leaves the target as it was read.
        Assert.True(LiveWriteTarget.Unchanged(Rich(Paragraph("A"), Paragraph("B"), Paragraph("C")), Rich(Paragraph("A"), Paragraph("B typed"), Paragraph("C")),
            Rich(Paragraph("A"), Paragraph("B typed"), Paragraph("C by AI")), wholeDocument: false));
        Assert.True(LiveWriteTarget.Unchanged(Rich(Paragraph("H"), Paragraph(""), Paragraph("P"), Paragraph("")), Rich(Paragraph("H"), Paragraph(""), Paragraph("P typed"), Paragraph("")),
            Rich(Paragraph("H"), Paragraph(""), Paragraph("P typed"), Paragraph("By AI")), wholeDocument: false));
        // Typing at both ends leaves the middle the agent's.
        Assert.True(LiveWriteTarget.Unchanged(Rich(Paragraph("A"), Paragraph("B"), Paragraph("C")), Rich(Paragraph("A typed"), Paragraph("B"), Paragraph("C typed")),
            Rich(Paragraph("A typed"), Paragraph("B by AI"), Paragraph("C typed")), wholeDocument: false));
    }

    /// <summary>Holds the agent's update between its revision's commit and its append until <see cref="Release"/>.</summary>
    private sealed class PausedAuthority : ILiveWriteAuthority
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DocSnapshot> CheckpointAsync(MarkdownDocumentSchema schema, byte[] snapshot, IReadOnlyList<byte[]> updates, long sequence, CancellationToken ct)
            => throw new InvalidOperationException("Nothing is unsaved in this test.");

        public Task<DocSnapshot> SeedAsync(MarkdownDocumentSchema schema, JsonElement document, CancellationToken ct)
            => throw new InvalidOperationException("The browser seeds this test (OpenLiveAsync).");

        public async Task<byte[]> WriteAsync(MarkdownDocumentSchema schema, byte[] snapshot, IReadOnlyList<byte[]> updates, JsonElement target, long sequence, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return McpLiveWriteTests.Agent;
        }
    }
}
