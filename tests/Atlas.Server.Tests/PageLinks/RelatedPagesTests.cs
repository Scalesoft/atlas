using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NHibernate;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.PageLinks.Contracts;
using Atlas.Server.Features.PageLinks.Queries;
using Atlas.Server.Features.PageTemplates;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.SpaceAi.Commands;
using Atlas.Server.Features.SpaceAi.Contracts;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;
using static Atlas.Server.Tests.PageLinks.PageLinksFixture;

namespace Atlas.Server.Tests.PageLinks;

/// <summary>
/// R1 Související (plan phase 2, §12): neighbours precomputed per generation from the fake model's centroids (trigram vectors:
/// pages sharing more words are nearer, never a real model), filtered on the reader's rights when read.
/// </summary>
public sealed class RelatedPagesTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Related_lists_the_nearest_readable_pages_without_those_already_linked()
    {
        await using var f = await RelatedFixture.CreateAsync();
        // Five neighbours, each sharing one word less with the target; the second links to the target and the target to the fourth.
        var fourth = await f.PublishAsync("Ctvrta", RelatedFixture.Topic(8, 4));
        var target = await f.PublishAsync("Cil", RelatedFixture.Topic(12, 0), links: [fourth.Id]);
        var first = await f.PublishAsync("Prvni", RelatedFixture.Topic(11, 1));
        var second = await f.PublishAsync("Druha", RelatedFixture.Topic(10, 2), links: [target.Id]);
        var third = await f.PublishAsync("Treti", RelatedFixture.Topic(9, 3));
        var fifth = await f.PublishAsync("Pata", RelatedFixture.Topic(7, 5));
        await f.PublishAsync("Daleka", RelatedFixture.Unrelated);
        await f.Index.ActiveIndexAsync();

        var connections = await f.ConnectionsAsync(f.Reader, target.Id);

        Assert.Equal([first.Id, third.Id, fifth.Id], connections.Related!.Items.Select(x => x.Id));
        Assert.Equal(new RelatedPage(first.Id, f.Index.SpaceId, "Engineering", "Prvni"), connections.Related.Items[0]);
        // Links here is unchanged beside it.
        Assert.Equal(second.Id, Assert.Single(connections.LinksHere.Items).Id);
        // The lists are symmetric where similarity is: the target is the first page's nearest.
        Assert.Equal(target.Id, (await f.ConnectionsAsync(f.Reader, first.Id)).Related!.Items[0].Id);
    }

    [PostgreSqlFact]
    public async Task Related_people_are_the_live_shared_contacts_the_related_pages_name_the_nearest_page_first()
    {
        // P15 boost: who the pages close in meaning name ranks a person higher in the editor; a page the reader may not read, the
        // target itself and a trashed or private contact never add one.
        await using var f = await RelatedFixture.CreateAsync();
        var target = await f.PublishAsync("Cil", RelatedFixture.Topic(12, 0));
        var first = await f.PublishAsync("Prvni", RelatedFixture.Topic(11, 1));
        var second = await f.PublishAsync("Druha", RelatedFixture.Topic(10, 2));
        var hidden = await f.PublishAsync("Omezena", RelatedFixture.Topic(11, 3));
        await f.Index.ActiveIndexAsync();
        await f.Index.Host.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=:id""", hidden.Id);
        Guid jana = Guid.NewGuid(), petr = Guid.NewGuid(), trashed = Guid.NewGuid(), mine = Guid.NewGuid(), secret = Guid.NewGuid(), own = Guid.NewGuid();
        var sql = $"""
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data","TrashedAt") VALUES
              ('{jana}','atlas.contact','wiki',NULL,NULL,1,now(),'{f.Admin.Id}',now(),'Jana','{"{}"}',NULL),
              ('{petr}','atlas.contact','space','{f.Index.SpaceId}',NULL,1,now(),'{f.Admin.Id}',now(),'Petr','{"{}"}',NULL),
              ('{trashed}','atlas.contact','wiki',NULL,NULL,1,now(),'{f.Admin.Id}',now(),'Smazany','{"{}"}',now()),
              ('{mine}','atlas.contact','private',NULL,'{f.Admin.Id}',1,now(),'{f.Admin.Id}',now(),'Soukromy','{"{}"}',NULL),
              ('{secret}','atlas.contact','wiki',NULL,NULL,1,now(),'{f.Admin.Id}',now(),'Skryty','{"{}"}',NULL),
              ('{own}','atlas.contact','wiki',NULL,NULL,1,now(),'{f.Admin.Id}',now(),'Vlastni','{"{}"}',NULL);
            INSERT INTO "PageObjectMention"("SourcePageId","ObjectId","SourceRevisionId")
              SELECT p."Id",CAST(x.o AS uuid),p."PublishedRevisionId" FROM (VALUES
                ('{second.Id}','{jana}'),('{first.Id}','{petr}'),('{first.Id}','{trashed}'),
                ('{first.Id}','{mine}'),('{hidden.Id}','{secret}'),('{target.Id}','{own}')) x(page,o)
              JOIN "Page" p ON p."Id"=CAST(x.page AS uuid);
            """;
        await Events.OutboxTests.ExecuteAsync(f.Index.Host.ConnectionString, sql);

        var related = (await f.ConnectionsAsync(f.Reader, target.Id)).Related!;
        Assert.Equal([first.Id, second.Id], related.Items.Select(x => x.Id));
        Assert.Equal([petr, jana], related.People);
        Assert.Contains(secret, (await f.ConnectionsAsync(f.Admin, target.Id)).Related!.People);
    }

    [PostgreSqlFact]
    public async Task Related_never_names_unreadable_trashed_template_unpublished_or_excluded_space_pages()
    {
        await using var f = await RelatedFixture.CreateAsync();
        var target = await f.PublishAsync("Cil", RelatedFixture.Topic(12, 0));
        // Hidden pages are nearer than the visible one, so a filter applied after a limit would leak or lose it.
        var foreign = await f.PublishAsync("Cizi", RelatedFixture.Topic(11, 1), f.Index.OtherSpaceId);
        var restricted = await f.PublishAsync("Omezena", RelatedFixture.Topic(11, 2));
        var trashed = await f.PublishAsync("Smazana", RelatedFixture.Topic(11, 3));
        var template = await f.PublishAsync("Sablona", RelatedFixture.Topic(11, 4));
        var unpublished = await f.PublishAsync("Stazena", RelatedFixture.Topic(11, 5));
        var republished = await f.PublishAsync("Nova verze", RelatedFixture.Topic(11, 6));
        var excluded = await f.PublishAsync("Vyloucena", RelatedFixture.Topic(11, 7), f.ExcludedSpaceId);
        var visible = await f.PublishAsync("Viditelna", RelatedFixture.Topic(8, 8));
        await f.Index.ActiveIndexAsync();
        Assert.Equal(5, (await f.ConnectionsAsync(f.Admin, target.Id)).Related!.Items.Count);

        // Changes the worker has not seen yet: the read alone must hide them.
        await f.Index.Host.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=:id""", restricted.Id);
        await f.Index.Host.ExecuteAsync("""
            UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"=now(),"TrashedById"="OwnerId","TrashBatchId"=gen_random_uuid() WHERE "Id"=:id
            """, trashed.Id);
        await f.Index.As<PageDto>(f.Admin, new SetPageTemplate(template.Id, true));
        await f.Index.Host.ExecuteAsync("""UPDATE "Page" SET "PublishedRevisionId"=NULL WHERE "Id"=:id""", unpublished.Id);
        await f.Index.DeliverAsync(trashed.Id);
        await f.Index.DeliverAsync(unpublished.Id);
        // A new publication is not related by the content of the old one until it is indexed (plan §3: the live publication).
        var before = await f.Index.PublishedRevisionAsync(republished.Id);
        await f.Index.EditAndPublishAsync(republished.Id, "Nova verze", RelatedFixture.Topic(11, 6) + " dodatek");
        Assert.NotEqual(before, await f.Index.PublishedRevisionAsync(republished.Id));
        await f.Index.As<SpaceAiSettingsView>(f.Admin, new SaveSpaceAiSettings(f.ExcludedSpaceId, new SaveSpaceAiSettingsRequest(false, null)));

        Assert.Equal(["Viditelna"], (await f.ConnectionsAsync(f.Reader, target.Id)).Related!.Items.Select(x => x.Title));
        var admin = (await f.ConnectionsAsync(f.Admin, target.Id)).Related!.Items.Select(x => x.Id).ToArray();
        Assert.Equal(new HashSet<Guid> { foreign.Id, restricted.Id }, admin[..2].ToHashSet());
        Assert.Equal(visible.Id, admin[2]);
        Assert.Equal(3, admin.Length);

        // Once indexed, the new publication is related again; the others have left the index.
        await f.Index.RunAsync();
        Assert.Equal([republished.Id, visible.Id], (await f.ConnectionsAsync(f.Reader, target.Id)).Related!.Items.Select(x => x.Id));
        Assert.DoesNotContain(trashed.Id, await f.NeighborsAsync());
        Assert.DoesNotContain(excluded.Id, await f.NeighborsAsync());

        // A revoked session reads nothing, even past the handler's own check.
        await f.Index.Host.ExecuteAsync("""UPDATE "UserAccount" SET "SecurityStamp"='revoked' WHERE "Id"=:id""", f.Admin.Id);
        Assert.Empty(await f.StoreRelatedAsync(f.Admin, target.Id));
        await f.Index.Host.ExecuteAsync("""UPDATE "UserAccount" SET "SecurityStamp"=CAST(:id AS text) WHERE "Id"=:id""", f.Admin.Id);
        f.Admin.SecurityStamp = f.Admin.Id.ToString();
        Assert.NotEmpty(await f.StoreRelatedAsync(f.Admin, target.Id));

        // A target in a space excluded from AI has no related pages, for anyone.
        await f.Index.As<SpaceAiSettingsView>(f.Admin, new SaveSpaceAiSettings(f.Index.SpaceId, new SaveSpaceAiSettingsRequest(false, null)));
        Assert.Empty((await f.ConnectionsAsync(f.Admin, target.Id)).Related!.Items);
        Assert.Empty((await f.ConnectionsAsync(f.Reader, target.Id)).Related!.Items);
    }

    [PostgreSqlFact]
    public async Task Related_is_absent_while_the_capability_is_off_and_links_here_still_answers()
    {
        await using var f = await RelatedFixture.CreateAsync();
        var target = await f.PublishAsync("Cil", RelatedFixture.Topic(12, 0));
        var near = await f.PublishAsync("Blizka", RelatedFixture.Topic(10, 1));
        var linking = await f.PublishAsync("Odkazujici", RelatedFixture.Unrelated, links: [target.Id]);
        await f.Index.ActiveIndexAsync();
        var on = await f.ConnectionsAsync(f.Reader, target.Id);
        Assert.Equal(near.Id, Assert.Single(on.Related!.Items).Id);
        Assert.Equal(linking.Id, Assert.Single(on.LinksHere.Items).Id);
        var json = JsonSerializer.SerializeToNode(on, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var item = json["related"]!["items"]![0]!.AsObject();
        Assert.Equal(["id", "spaceId", "spaceName", "title"], item.Select(x => x.Key));

        // An embeddings outage (two failed probes open the circuit breaker): no related, links here as before.
        f.Index.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Timeout);
        f.Index.Clock.Advance(TimeSpan.FromSeconds(60));
        await f.Index.ProbeAsync();
        f.Index.Clock.Advance(TimeSpan.FromSeconds(30));
        await f.Index.ProbeAsync();
        Assert.False(f.Index.Availability.IsAvailable(AiProfileKind.Embeddings));
        var outage = await f.ConnectionsAsync(f.Reader, target.Id);
        Assert.Null(outage.Related);
        Assert.Equal(on.LinksHere.Items, outage.LinksHere.Items);
        Assert.False(JsonSerializer.SerializeToNode(outage, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject().ContainsKey("related"));

        // Back with the first good probe.
        f.Index.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Healthy);
        f.Index.Clock.Advance(TimeSpan.FromSeconds(30));
        await f.Index.ProbeAsync();
        Assert.Equal(near.Id, Assert.Single((await f.ConnectionsAsync(f.Reader, target.Id)).Related!.Items).Id);

        // The task switched off in Administration → AI → Functions.
        var features = await f.Index.As<AiFeaturesView>(f.Admin, new GetAiFeatures());
        await f.Index.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new SaveAiFeaturesRequest(
            [new SaveAiFeatureItem(AiTaskCatalog.PageRelated, false, null)], features.Version)));
        var off = await f.ConnectionsAsync(f.Reader, target.Id);
        Assert.Null(off.Related);
        Assert.Equal(on.LinksHere.Items, off.LinksHere.Items);
    }

    [PostgreSqlFact]
    public async Task Neighbors_follow_the_active_generation_and_are_rebuilt_after_activation()
    {
        await using var f = await RelatedFixture.CreateAsync();
        var target = await f.PublishAsync("Cil", RelatedFixture.Topic(12, 0));
        var first = await f.PublishAsync("Prvni", RelatedFixture.Topic(11, 1));
        var second = await f.PublishAsync("Druha", RelatedFixture.Topic(9, 2));
        var old = await f.Index.ActiveIndexAsync();
        Assert.Equal([first.Id, second.Id], (await f.ConnectionsAsync(f.Reader, target.Id)).Related!.Items.Select(x => x.Id));

        // A rebuild beside it, whose list for the target is lost: the active generation still answers.
        var building = await f.Index.StartAsync("rebuild");
        await f.Index.RunAsync();
        await f.Index.Host.ExecuteAsync("""DELETE FROM "PageNeighbor" WHERE "GenerationId"=:id""", building.Id);
        Assert.Equal([first.Id, second.Id], (await f.ConnectionsAsync(f.Reader, target.Id)).Related!.Items.Select(x => x.Id));

        // After activation only the new generation is read, though the retired one still holds its lists.
        await f.Index.ActivateAsync((await f.Index.StatusAsync()).Building!);
        Assert.True(await f.Index.CountAsync($"""SELECT count(*) FROM "PageNeighbor" WHERE "GenerationId"='{old.Id}'""") > 0);
        Assert.Empty((await f.ConnectionsAsync(f.Reader, target.Id)).Related!.Items);

        // The maintenance recomputes the activated generation's lists in full, once.
        await f.Index.Maintenance.RunOnceAsync(f.Index.Clock.GetUtcNow(), default);
        Assert.Equal([first.Id, second.Id], (await f.ConnectionsAsync(f.Reader, target.Id)).Related!.Items.Select(x => x.Id));
        Assert.Equal(0, await f.Index.Maintenance.RefreshNeighborsAsync(f.Index.Clock.GetUtcNow(), default));
        Assert.Equal(1, await f.Index.Maintenance.RefreshNeighborsAsync(f.Index.Clock.GetUtcNow().AddHours(25), default));
    }

    [PostgreSqlFact]
    public async Task Neighbors_are_refreshed_when_a_page_is_reindexed_or_becomes_a_template()
    {
        await using var f = await RelatedFixture.CreateAsync();
        var target = await f.PublishAsync("Cil", RelatedFixture.Topic(12, 0));
        var moving = await f.PublishAsync("Presouvana", RelatedFixture.Topic(11, 1));
        var other = await f.PublishAsync("Jina", RelatedFixture.Unrelated);
        await f.Index.ActiveIndexAsync();
        Assert.Equal([moving.Id], (await f.ConnectionsAsync(f.Reader, target.Id)).Related!.Items.Select(x => x.Id));
        Assert.Empty((await f.ConnectionsAsync(f.Reader, other.Id)).Related!.Items);

        // Republished about the other topic: the worker moves it in the target's list and in the other page's, both ways.
        await f.Index.EditAndPublishAsync(moving.Id, "Presouvana", RelatedFixture.Unrelated + " doplnek");
        await f.Index.RunAsync();
        Assert.Empty((await f.ConnectionsAsync(f.Reader, target.Id)).Related!.Items);
        Assert.Equal([moving.Id], (await f.ConnectionsAsync(f.Reader, other.Id)).Related!.Items.Select(x => x.Id));
        Assert.Equal([other.Id], (await f.ConnectionsAsync(f.Reader, moving.Id)).Related!.Items.Select(x => x.Id));

        // A template keeps only its title vector: it has no list and is in none.
        await f.Index.As<PageDto>(f.Admin, new SetPageTemplate(other.Id, true));
        await f.Index.RunAsync();
        Assert.Empty((await f.ConnectionsAsync(f.Reader, moving.Id)).Related!.Items);
        Assert.DoesNotContain(other.Id, await f.NeighborsAsync());
    }
}

/// <summary>The index fixture (real AI modules, the fake model and clock) with a reader of the first space and a third space
/// the reader may read, to be excluded from AI.</summary>
internal sealed class RelatedFixture : IAsyncDisposable
{
    // Words with few shared trigrams: a page's similarity to the target follows the number of words it shares.
    private static readonly string[] TopicWords =
        ["kryptografie", "certifikat", "podpis", "klic", "verejny", "soukromy", "retezec", "autorita", "revokace", "obnova", "platnost", "algoritmus"];
    private static readonly string[] Fillers =
        ["zebra", "tulipan", "sopka", "kompas", "hvezda", "lampa", "mrkev", "rybnik", "vlak", "kopec", "louka", "jablko", "datel", "orchidej",
         "ponorka", "zvon", "fazole", "kanoe", "medved", "pirat", "sneh", "bubny", "citron", "jezevec", "kytara", "ledovec", "moucha", "nuzky",
         "opice", "papousek", "rakev", "salam", "tygr", "uhli", "vejce", "zirafa", "babicka", "cibule", "dyne", "fiala", "granat", "hrusky",
         "jizda", "kukurice", "lilie", "mandle", "nosorozec", "okurka", "pstruh", "repa", "sardinka", "tuleni", "veverka", "zubr"];

    private RelatedFixture(IndexFixture index, UserAccount reader, Guid excludedSpaceId)
    {
        Index = index;
        Reader = reader;
        ExcludedSpaceId = excludedSpaceId;
    }

    public IndexFixture Index { get; }
    public UserAccount Admin => Index.Admin;
    public UserAccount Reader { get; }
    public Guid ExcludedSpaceId { get; }

    /// <summary>Text about a topic unrelated to <see cref="Topic"/>.</summary>
    public const string Unrelated = "pocasi bourka vitr dest mraky slunce teplota vlhkost tlak predpoved radar fronta";

    public static async Task<RelatedFixture> CreateAsync()
    {
        var index = await IndexFixture.CreateAsync();
        var reader = await index.Host.SeedUserAsync("reader");
        await index.Host.GrantAiAsync(reader);
        await index.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant
        {
            Id = Guid.NewGuid(), SpaceId = index.SpaceId, UserId = reader.Id, Role = (int)SpaceRole.Reader,
        }));
        var excluded = await index.Host.SeedSpaceAsync("Excluded", (index.Writer, SpaceRole.Writer), (reader, SpaceRole.Reader));
        return new RelatedFixture(index, reader, excluded);
    }

    /// <summary>Twelve words: the first <paramref name="shared"/> of the topic, the rest fillers no other call with another
    /// <paramref name="variant"/> uses.</summary>
    public static string Topic(int shared, int variant)
        => string.Join(" ", TopicWords.Take(shared).Concat(Fillers.Skip(variant * 6).Take(12 - shared)));

    public async Task<PageDto> PublishAsync(string title, string text, Guid? spaceId = null, Guid[]? links = null)
    {
        var rich = Doc([Text(text), .. (links ?? []).Select(Ref)]);
        var page = await Index.Pages.CreateAsync(new CreatePageRequest(spaceId ?? Index.SpaceId, null, title, rich, text), Index.Writer, default);
        var published = await Index.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), Index.Writer, default);
        await Index.DeliverAsync(page.Id);
        // The links are derived by the outbox's delivery, which this host has no queue for.
        if (links is { Length: > 0 })
        {
            await Index.Host.RefreshPageDerivedAsync();
        }
        return published;
    }

    /// <summary>What the endpoint sends: the related capability as A3 has it now.</summary>
    public async Task<PageConnections> ConnectionsAsync(UserAccount actor, Guid pageId)
    {
        var query = await GetPageConnections.ForReaderAsync(pageId, 10, Index.Host.Services.GetRequiredService<AiCapabilities>(), default);
        return await Index.As<PageConnections>(actor, query);
    }

    /// <summary>The store's read on its own, with the actor's in-memory security stamp.</summary>
    public async Task<IReadOnlyList<RelatedPage>> StoreRelatedAsync(UserAccount actor, Guid pageId)
    {
        using var session = Index.Host.OpenSession();
        using var transaction = session.BeginTransaction();
        var page = await session.GetAsync<Page>(pageId);
        return await new PageLinkStore(session).RelatedAsync(actor, page, 5, default);
    }

    /// <summary>Every page in any neighbour row (either side).</summary>
    public async Task<IReadOnlyList<Guid>> NeighborsAsync()
    {
        using var session = Index.Host.OpenSession();
        return (await session.CreateSQLQuery("""
            SELECT "PageId" FROM "PageNeighbor" UNION SELECT "NeighborPageId" FROM "PageNeighbor"
            """).AddScalar("PageId", NHibernateUtil.Guid).ListAsync<Guid>()).ToArray();
    }

    public ValueTask DisposeAsync() => Index.DisposeAsync();
}
