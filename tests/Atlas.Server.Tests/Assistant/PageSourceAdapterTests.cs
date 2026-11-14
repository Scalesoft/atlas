using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Assistant.Sources;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>The page adapter after retrieval (plan phase 3 R2, R4): the text of stored references while they are current, and the
/// gate's and history's decisions over access, trash, publication, templates and inclusion in AI.</summary>
public sealed class PageSourceAdapterTests
{
    private const string Question = "Jak obnovit data?";

    /// <summary>Every "Postup obnovy" section means the question exactly.</summary>
    private static string Meaning(string text)
        => text.Contains("obnovit data", StringComparison.Ordinal) || text.Contains("Postup obnovy", StringComparison.Ordinal)
            ? "backup restore database"
            : text;

    [PostgreSqlFact]
    public async Task Load_text_returns_null_after_reindexing_or_a_generation_swap()
    {
        await using var f = await AnswerSourcesFixture.CreateAsync(Meaning);
        var a = await f.PublishAsync("Návod A", "## Postup obnovy\nText A.", null, ("Postup obnovy", "postup"));
        var b = await f.PublishAsync("Návod B", "## Postup obnovy\nText B.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        var sources = (await f.RetrieveAsync(f.Reader, Question)).Sources;
        Assert.Equal(new HashSet<Guid> { a, b }, sources.Select(source => source.Chunk!.PageId).ToHashSet());
        // The step after the model sees only the stored columns: rebuild the references from them.
        using var scope = f.Index.Host.Services.CreateScope();
        var registry = f.Registry(scope);
        var refs = sources.Select(source => registry.Reference(registry.Columns(source))!).ToArray();
        Assert.Equal(sources.Select(source => source.Reference), refs);
        var texts = await f.LoadTextAsync(f.Reader, refs);
        Assert.Equal(sources.Select(source => (string?)source.Text), refs.Select(reference => texts[reference]));

        // A new publication of A: its old revision's text is never read again, before and after the worker reindexes it.
        await f.Index.EditAndPublishAsync(a, "Návod A", "## Postup obnovy\nNový text A.", ("Postup obnovy", "postup"));
        var ofA = refs.Single(reference => reference.Id == a.ToString());
        var ofB = refs.Single(reference => reference.Id == b.ToString());
        Assert.Null((await f.LoadTextAsync(f.Reader, [ofA]))[ofA]);
        await f.IndexAsync();
        texts = await f.LoadTextAsync(f.Reader, refs);
        Assert.Null(texts[ofA]);
        Assert.Equal("Text B.", texts[ofB]);

        // A new generation replaces the active one: the chunks of the retired one are not read, though B is unchanged.
        var building = await f.Index.StartAsync("rebuild");
        await f.Index.RunAsync();
        Assert.True((await f.Index.VerifyAsync(building.Id)).CanActivate);
        await f.Index.ActivateAsync((await f.Index.StatusAsync()).Building!);
        Assert.Null((await f.LoadTextAsync(f.Reader, [ofB]))[ofB]);
        // A garbled anchor names no chunk.
        var garbled = ofB with { Anchor = "x:1" };
        Assert.Null((await f.LoadTextAsync(f.Reader, [garbled]))[garbled]);
    }

    [PostgreSqlFact]
    public async Task Authorization_and_status_follow_access_trash_publication_and_inclusion_in_ai()
    {
        await using var f = await AnswerSourcesFixture.CreateAsync(Meaning);
        var included = await f.Index.Host.SeedSpaceAsync("Zahrnutý", (f.Writer, SpaceRole.Writer), (f.Reader, SpaceRole.Reader));
        var kept = await f.PublishAsync("Zůstává", "## Postup obnovy\nK.", null, ("Postup obnovy", "postup"));
        var restricted = await f.PublishAsync("Omezená", "## Postup obnovy\nO.", null, ("Postup obnovy", "postup"));
        var trashed = await f.PublishAsync("V koši", "## Postup obnovy\nT.", null, ("Postup obnovy", "postup"));
        var republished = await f.PublishAsync("Znovu publikovaná", "## Postup obnovy\nR.", null, ("Postup obnovy", "postup"));
        var excluded = await f.PublishAsync("Vyloučená", "## Postup obnovy\nV.", included, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        var sources = (await f.RetrieveAsync(f.Reader, Question, take: 6, perSource: 1)).Sources;
        var byPage = sources.ToDictionary(source => source.Chunk!.PageId, source => source.Reference);
        Assert.Equal(5, byPage.Count);
        var refs = byPage.Values.Append(new SourceReference("page", 1, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), null)).ToArray();
        var initial = await f.AuthorizeAsync(f.Reader, refs, true);
        Assert.Equal(5, initial.Count(x => x.Value == SourceStatus.Available));

        await f.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=@id""", ("id", restricted));
        await f.ExecuteAsync("""
            UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"=now(),"TrashedById"=@admin,"TrashBatchId"=gen_random_uuid() WHERE "Id"=@id
            """, ("id", trashed), ("admin", f.Admin.Id));
        await f.Index.EditAndPublishAsync(republished, "Znovu publikovaná", "## Postup obnovy\nR2.", ("Postup obnovy", "postup"));
        await f.ExecuteAsync("""
            INSERT INTO "SpaceAiSetting"("Id","SpaceId","Included","ChangedById","ActiveFrom") VALUES (gen_random_uuid(),@space,false,@admin,now())
            """, ("space", included), ("admin", f.Admin.Id));

        var context = await f.AuthorizeAsync(f.Reader, refs, true);
        var history = await f.AuthorizeAsync(f.Reader, refs, false);
        var status = await f.StatusAsync(f.Reader, refs);

        Assert.Equal(SourceStatus.Available, context[byPage[kept]]);
        Assert.Equal(SourceStatus.Changed, context[byPage[republished]]);
        // A history dependency needs access and publication only: a newer revision passes the gate.
        Assert.Equal(SourceStatus.Available, history[byPage[republished]]);
        Assert.Equal(SourceStatus.Changed, status[byPage[republished]]);
        foreach (var gone in new[] { restricted, trashed, excluded })
        {
            Assert.Equal((SourceStatus.Unavailable, SourceStatus.Unavailable, SourceStatus.Unavailable),
                (context[byPage[gone]], history[byPage[gone]], status[byPage[gone]]));
        }
        Assert.Equal(SourceStatus.Unavailable, context[refs[^1]]);
        Assert.Equal(SourceStatus.Unavailable, status[refs[^1]]);
        Assert.Equal(context.Keys.ToHashSet(), refs.ToHashSet());
        // A WikiAdmin reads the restricted page; the trash and the excluded space stay closed to everyone.
        var admin = await f.StatusAsync(f.Admin, refs);
        Assert.Equal((SourceStatus.Available, SourceStatus.Unavailable, SourceStatus.Unavailable),
            (admin[byPage[restricted]], admin[byPage[trashed]], admin[byPage[excluded]]));
        Assert.Equal(SourceStatus.Available, (await f.AuthorizeAsync(f.Admin, [byPage[restricted]], true))[byPage[restricted]]);
        // A revoked credential is refused as on every content path, not decided per source.
        await f.ExecuteAsync("""UPDATE "UserAccount" SET "SecurityStamp"='revoked' WHERE "Id"=@id""", ("id", f.Reader.Id));
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => f.AuthorizeAsync(f.Reader, refs, true))).Status);
    }
}
