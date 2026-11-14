using System.Diagnostics;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.AiIndex;
using Atlas.Server.Features.AiIndex.Commands;
using Atlas.Server.Features.PageTemplates;
using Atlas.Server.Features.SpaceAi.Commands;
using Atlas.Server.Features.SpaceAi.Contracts;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Ai.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.AiIndex;

/// <summary>The index worker (plan §10): what reaches the model, outages, races with publication, backoff, reuse and removal.</summary>
public sealed class IndexRunnerTests
{
    private static readonly (string, string)[] Access = [("Přístup", "pristup")];

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Publishing_enqueues_live_generations_and_the_worker_indexes_only_the_published_revision()
    {
        await using var f = await IndexFixture.CreateAsync();
        var generation = await f.ActiveIndexAsync();
        var page = await f.PublishNewAsync("VPN", "## Přístup\nPřipojení přes VPN klienta.", null, Access);
        // Password references stay in PageRevision (the page source needs them); the index never sees them.
        await f.Host.ExecuteAsync("""
            UPDATE "PageRevision" SET "Markdown"="Markdown"||' ::secret{vault="v" secret="s"} a :secret{vault="v" secret="t"}' WHERE "Id"=:id
            """, await f.PublishedRevisionAsync(page.Id));
        var task = Assert.Single(await f.TasksAsync(page.Id));
        Assert.Equal((generation.Id, 0), (task.GenerationId, task.Attempts));

        // A saved draft enqueues nothing (Page has DynamicUpdate; the trigger watches the published pointer, not saves).
        await f.SaveDraftAsync(page.Id, "VPN koncept", "KONCEPT-TEXT, který nikdy neodejde.");
        Assert.Equal(task.EnqueuedAt, Assert.Single(await f.TasksAsync(page.Id)).EnqueuedAt);

        Assert.Equal(1, await f.RunAsync());

        var indexed = await f.IndexedAsync(generation.Id, page.Id);
        Assert.NotNull(indexed);
        Assert.Equal(await f.PublishedRevisionAsync(page.Id), indexed.RevisionId);
        Assert.True(indexed.HasCentroid);
        Assert.StartsWith("VPN › Přístup\nPřipojení přes VPN klienta.", Assert.Single(indexed.Chunks));
        Assert.Equal("pristup", Assert.Single(indexed.HeadingIds));
        Assert.Empty(await f.TasksAsync());
        Assert.Contains("passage: VPN", f.Embedded);
        Assert.All(f.Embedded, text =>
        {
            Assert.DoesNotContain("KONCEPT", text);
            Assert.DoesNotContain("secret{", text);
        });
    }

    /// <summary>The index reads the text for AI (SEC-T-1, GAP5-5): no concealed block, no password reference, a readable line per contact.</summary>
    [PostgreSqlFact]
    public async Task The_index_leaves_out_concealed_blocks_and_reads_contacts_as_a_line_without_ids()
    {
        await using var f = await IndexFixture.CreateAsync();
        var generation = await f.ActiveIndexAsync();
        var page = await f.PublishNewAsync("Tým", "Úvod.");
        const string objectId = "0b6b7c1e-5f1a-4c3e-9a51-6d2f0f4e8a11";
        static System.Text.Json.Nodes.JsonObject Heading(string title, string id) => new()
        {
            ["type"] = "heading", ["attrs"] = new System.Text.Json.Nodes.JsonObject { ["level"] = 2, ["headingId"] = id },
            ["content"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = title })
        };
        static System.Text.Json.Nodes.JsonObject Paragraph(string text) => new()
        {
            ["type"] = "paragraph", ["content"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = text })
        };
        var document = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "doc",
            ["content"] = new System.Text.Json.Nodes.JsonArray(
                Heading("Vedení", "vedeni"),
                new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "contactCard", ["attrs"] = System.Text.Json.Nodes.JsonNode.Parse($$$"""
                        {"data":{"version":1,"contacts":[{"name":"Jana Nováková","role":"PM projektu","title":"Vedoucí provozu","objectId":"{{{objectId}}}",
                        "photo":"/api/photos/jana.png","internalUser":{"id":"8f0e3c55-2a7b-4d7e-b3c1-91a0c4d2e6f7"}}]}}
                        """)
                },
                new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "concealedContent",
                    ["content"] = new System.Text.Json.Nodes.JsonArray(Heading("Odpověď", "odpoved"), Paragraph("SKRYTO123"),
                        // An OpenAPI block is a block like any other: its specification is concealed with it.
                        new System.Text.Json.Nodes.JsonObject { ["type"] = "openapi", ["attrs"] = new System.Text.Json.Nodes.JsonObject { ["data"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["version"] = 1, ["filename"] = "skryte.yaml",
                            ["source"] = "openapi: 3.1.0\ninfo:\n  title: SKRYTEAPI\n  version: '1'\npaths:\n  /skryte:\n    get:\n      summary: SKRYTAOPERACE\n"
                        } } })
                },
                Heading("Závěr", "zaver"),
                Paragraph("Konec stránky."))
        };
        var revision = await f.PublishedRevisionAsync(page.Id);
        await f.Host.SeedAsync(session => session.CreateSQLQuery("""UPDATE "PageRevision" SET "ContentJson"=:content,"Markdown"=:markdown WHERE "Id"=:id""")
            .SetParameter("content", new System.Text.Json.Nodes.JsonObject { ["format"] = "atlas-rich", ["version"] = 1, ["document"] = document }.ToJsonString())
            .SetParameter("markdown", Atlas.Server.Platform.Content.EditorMarkdownWriter.Write(document.DeepClone().AsObject()))
            .SetParameter("id", revision).ExecuteUpdateAsync());

        Assert.Equal(1, await f.RunAsync());

        var indexed = (await f.IndexedAsync(generation.Id, page.Id))!;
        Assert.Equal(new string?[] { "vedeni", "zaver" }, indexed.HeadingIds);
        Assert.Equal(["Tým › Vedení\nJana Nováková · PM projektu · Vedoucí provozu", "Tým › Závěr\nKonec stránky."], indexed.Chunks);
        Assert.All(f.Embedded, text =>
        {
            Assert.DoesNotContain("SKRYTO123", text);
            Assert.DoesNotContain("SKRYTEAPI", text);
            Assert.DoesNotContain("SKRYTAOPERACE", text);
            Assert.DoesNotContain("Odpověď", text);
            Assert.DoesNotContain(objectId, text);
            Assert.DoesNotContain("8f0e3c55", text);
        });
    }

    [PostgreSqlFact]
    public async Task Without_a_generation_publishing_creates_no_tasks()
    {
        await using var f = await IndexFixture.CreateAsync();
        // AI fully configured and available, but no index built: publication leaves no trace and the worker reads nothing.
        await f.ConfigureAsync();
        await f.PublishNewAsync("Bez indexu", "Text.");
        Assert.Empty(await f.TasksAsync());
        Assert.Equal(0, await f.RunAsync());
        Assert.Null(await f.NextDueAsync());
        Assert.DoesNotContain(f.Fake.Calls, x => x.Task == AiTaskCatalog.IndexEmbed);
        Assert.Equal(0, await f.CountAsync("""SELECT count(*) FROM "KnowledgePage" """));
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Embedding_outage_never_blocks_publication_and_the_queue_catches_up_after_recovery()
    {
        await using var f = await IndexFixture.CreateAsync(services => services.AddJob<IndexJob>());
        var generation = await f.ActiveIndexAsync();
        f.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Timeout);

        var watch = Stopwatch.StartNew();
        var pages = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            pages.Add((await f.PublishNewAsync("Stránka " + i, "Obsah stránky " + i + ".")).Id);
        }
        watch.Stop();
        // Publication never calls the model: far below the profile's 30 s timeout (and the fake clock never moved).
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.DoesNotContain(f.Fake.Calls, x => x.Task == AiTaskCatalog.IndexEmbed && x.Inputs.Any(text => text.Contains("Stránka")));
        // The job's data schedule: new tasks are due at once (the database's now() is not compared with the clock).
        var now = f.Clock.GetUtcNow().UtcDateTime;
        Assert.InRange((await f.NextDueAsync())!.Value, now.AddMilliseconds(-1), now);

        // Two timeouts open A3's circuit breaker; each ends the round and spends no attempt.
        Assert.Equal(0, await f.TimedOutRoundAsync());
        Assert.Equal(0, await f.TimedOutRoundAsync());
        Assert.False(f.Availability.IsAvailable(AiProfileKind.Embeddings));
        // While the profile is down the job asks again in a minute, not at once.
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime + NextIndexTaskDue.OutageRecheck, await f.NextDueAsync());
        var calls = f.Fake.Calls.Count;
        Assert.Equal(0, await f.RunAsync());
        Assert.Equal(calls, f.Fake.Calls.Count);
        var waiting = await f.TasksAsync();
        Assert.Equal(3, waiting.Count);
        Assert.All(waiting, task =>
        {
            Assert.Equal(0, task.Attempts);
            Assert.Null(task.LeasedUntil);
            // No backoff: still due from its enqueueing (both times are the database's).
            Assert.True(task.NextAttemptAt <= task.EnqueuedAt);
        });

        // The probe finds the model again after 30 s; one pass indexes everything, without waiting for any backoff.
        f.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Healthy);
        f.Clock.Advance(TimeSpan.FromSeconds(30));
        await f.ProbeAsync();
        Assert.True(f.Availability.IsAvailable(AiProfileKind.Embeddings));
        long run;
        await using (var scope = f.Host.Services.CreateAsyncScope())
        {
            run = await scope.ServiceProvider.GetRequiredService<IJobRunner>().RunToEndAsync(IndexJob.JobId, "test");
        }
        Assert.Equal(3L, await f.CountAsync($"""SELECT CAST("Summary"->>'tasks' AS bigint) FROM "JobRun" WHERE "Id" = {run} AND "State" = 'succeeded'"""));
        Assert.Empty(await f.TasksAsync());
        Assert.Null(await f.NextDueAsync());
        foreach (var page in pages)
        {
            Assert.NotNull(await f.IndexedAsync(generation.Id, page));
        }
    }

    [PostgreSqlFact]
    public async Task A_newer_publication_during_embedding_wins_and_the_stale_result_is_discarded()
    {
        await using var f = await IndexFixture.CreateAsync();
        var generation = await f.ActiveIndexAsync();
        var page = await f.PublishNewAsync("Síť", "Verze jedna.");
        var hold = f.Fake.Hold(AiProfileKind.Embeddings);
        var round = f.Runner.RunOnceAsync(f.Clock.GetUtcNow(), default);
        await hold.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        // Version two is published while version one is with the model.
        await f.EditAndPublishAsync(page.Id, "Síť", "Verze dva.");
        hold.Release();
        Assert.Equal(1, await round.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Null(await f.IndexedAsync(generation.Id, page.Id));

        await f.RunAsync();
        var indexed = await f.IndexedAsync(generation.Id, page.Id);
        Assert.Equal(await f.PublishedRevisionAsync(page.Id), indexed!.RevisionId);
        Assert.Equal("Síť\nVerze dva.", Assert.Single(indexed.Chunks));
    }

    [PostgreSqlFact]
    public async Task Task_errors_back_off_exponentially_up_to_15_minutes()
    {
        await using var f = await IndexFixture.CreateAsync();
        await f.ActiveIndexAsync();
        // 768 dimensions: an unusable answer, not an outage.
        f.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.InvalidJson);
        var page = await f.PublishNewAsync("Chybná", "Text.");

        var first = f.Clock.GetUtcNow().UtcDateTime;
        Assert.Equal(1, await f.Runner.RunOnceAsync(first, default));
        var task = Assert.Single(await f.TasksAsync(page.Id));
        Assert.Equal((1, AiOutcome.InvalidOutput), (task.Attempts, task.LastErrorCode));
        Assert.InRange(task.NextAttemptAt, first.AddSeconds(9), first.AddSeconds(11));
        Assert.Null(task.LeasedUntil);
        Assert.Equal(task.NextAttemptAt, await f.Host.InvokeAsync<DateTime?>(new NextIndexTaskDue(first)));
        Assert.Equal(0, await f.Runner.RunOnceAsync(first.AddSeconds(8), default));

        var second = first.AddSeconds(12);
        Assert.Equal(1, await f.Runner.RunOnceAsync(second, default));
        task = Assert.Single(await f.TasksAsync(page.Id));
        Assert.Equal(2, task.Attempts);
        Assert.InRange(task.NextAttemptAt, second.AddSeconds(18), second.AddSeconds(22));

        await f.Host.ExecuteAsync("""UPDATE "IndexTask" SET "Attempts"=12 WHERE "PageId"=:id""", page.Id);
        var later = second.AddSeconds(30);
        Assert.Equal(1, await f.Runner.RunOnceAsync(later, default));
        task = Assert.Single(await f.TasksAsync(page.Id));
        Assert.Equal(13, task.Attempts);
        Assert.InRange(task.NextAttemptAt, later.AddMinutes(15).AddMilliseconds(-1), later.AddMinutes(15).AddMilliseconds(1));
    }

    [PostgreSqlFact]
    public async Task Unchanged_sections_reuse_their_vectors()
    {
        await using var f = await IndexFixture.CreateAsync();
        var generation = await f.ActiveIndexAsync();
        (string, string)[] headings = [("Účty", "ucty"), ("Hardware", "hardware")];
        var page = await f.PublishNewAsync("Onboarding", "## Účty\nZaložení účtů.\n\n## Hardware\nNotebook a monitor.", null, headings);
        await f.RunAsync();
        var before = f.Embedded.Count;

        await f.EditAndPublishAsync(page.Id, "Onboarding", "## Účty\nZaložení účtů.\n\n## Hardware\nNotebook, monitor a klávesnice.", headings);
        await f.RunAsync();

        Assert.Equal(["passage: Onboarding › Hardware\nNotebook, monitor a klávesnice."], f.Embedded.Skip(before));
        var indexed = await f.IndexedAsync(generation.Id, page.Id);
        Assert.Equal(await f.PublishedRevisionAsync(page.Id), indexed!.RevisionId);
        Assert.Equal(["ucty", "hardware"], indexed.HeadingIds);
    }

    /// <summary>The catalog's silent rebuild (a restore, a migration) re-indexes only what the index disagrees with, as the trigger did.</summary>
    [PostgreSqlFact]
    public async Task A_silent_rebuild_enqueues_only_pages_the_index_disagrees_with()
    {
        await using var f = await IndexFixture.CreateAsync();
        await f.ActiveIndexAsync();
        var same = await f.PublishNewAsync("Stejná", "Text se nemění.");
        var flagged = await f.PublishNewAsync("Šablona", "Text šablony.");
        await f.RunAsync();
        Assert.Empty(await f.TasksAsync());

        await f.Host.ExecuteAsync("""UPDATE "Page" SET "IsTemplate"=true WHERE "Id"=:id""", flagged.Id);
        await f.DeliverAsync(same.Id, rebuild: true);
        await f.DeliverAsync(flagged.Id, rebuild: true);

        Assert.Equal(flagged.Id, Assert.Single(await f.TasksAsync()).PageId);
    }

    [PostgreSqlFact]
    public async Task Trash_unpublish_template_and_excluded_space_remove_the_page_from_the_index()
    {
        await using var f = await IndexFixture.CreateAsync();
        var generation = await f.ActiveIndexAsync();
        var trashed = await f.PublishNewAsync("Do koše", "Smazaný text.");
        var unpublished = await f.PublishNewAsync("Stažená", "Stažený text.");
        var template = await f.PublishNewAsync("Runbook", "## Kroky\nPostup nasazení.", null, ("Kroky", "kroky"));
        var archived = await f.PublishNewAsync("Archiv", "ARCHIV-JEDNA", f.OtherSpaceId);
        var kept = await f.PublishNewAsync("Zůstává", "Text zůstává.");
        await f.RunAsync();
        foreach (var page in new[] { trashed, unpublished, template, archived, kept })
        {
            Assert.NotNull(await f.IndexedAsync(generation.Id, page.Id));
        }

        await f.Host.ExecuteAsync("""
            UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"=now(),"TrashedById"="OwnerId","TrashBatchId"=gen_random_uuid() WHERE "Id"=:id
            """, trashed.Id);
        await f.Host.ExecuteAsync("""UPDATE "Page" SET "PublishedRevisionId"=NULL WHERE "Id"=:id""", unpublished.Id);
        await f.DeliverAsync(trashed.Id);
        await f.DeliverAsync(unpublished.Id);
        await f.As<PageDto>(f.Admin, new SetPageTemplate(template.Id, true));
        await f.As<SpaceAiSettingsView>(f.Admin, new SaveSpaceAiSettings(f.OtherSpaceId, new SaveSpaceAiSettingsRequest(false, null)));
        // A page published into the excluded space afterwards is never sent to the model either.
        await f.PublishNewAsync("Archiv 2", "ARCHIV-DVA", f.OtherSpaceId);
        var before = f.Embedded.Count;
        await f.RunAsync();

        Assert.Null(await f.IndexedAsync(generation.Id, trashed.Id));
        Assert.Null(await f.IndexedAsync(generation.Id, unpublished.Id));
        Assert.Null(await f.IndexedAsync(generation.Id, archived.Id));
        // A template keeps only its title vector (O19); the title is unchanged, so nothing is embedded again.
        var asTemplate = await f.IndexedAsync(generation.Id, template.Id);
        Assert.Equal((true, false), (asTemplate!.IsTemplate, asTemplate.HasCentroid));
        Assert.Empty(asTemplate.Chunks);
        Assert.NotNull(await f.IndexedAsync(generation.Id, kept.Id));
        Assert.Empty(f.Embedded.Skip(before));
        Assert.DoesNotContain(f.Embedded, text => text.Contains("ARCHIV-DVA"));
        Assert.Empty(await f.TasksAsync());
        Assert.Equal(2, await f.CountAsync("""SELECT count(*) FROM "KnowledgePage" """));
    }
}
