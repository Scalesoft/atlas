using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.AiIndex.Contracts;
using Atlas.Server.Features.AiIndex.Domain;
using Atlas.Server.Features.AiSuggestions;
using Atlas.Server.Features.PageTemplates;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.SpaceAi.Commands;
using Atlas.Server.Features.SpaceAi.Contracts;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Ai.Fakes;

namespace Atlas.Server.Tests.AiSuggestions;

/// <summary>
/// E2, E4 and P1–P3 over the real index (<see cref="IndexFixture"/>: fake model, fake clock, A3's real cache and breaker). The
/// writer writes in "Engineering" and "Archive", the reader reads "Engineering", "Excluded" is a space both use that the tests
/// switch out of AI, and "Restricted" belongs to the admin alone. Suggestions run the endpoints' path
/// (<see cref="AiSuggestionsFlow"/>): the first read, the model call outside any transaction, the second read.
/// </summary>
internal sealed class SuggestionFixture : IAsyncDisposable
{
    private SuggestionFixture(IndexFixture index, UserAccount reader, Guid excludedSpaceId, Guid restrictedSpaceId)
    {
        Index = index;
        Reader = reader;
        ExcludedSpaceId = excludedSpaceId;
        RestrictedSpaceId = restrictedSpaceId;
    }

    public IndexFixture Index { get; }
    public UserAccount Reader { get; }
    public UserAccount Writer => Index.Writer;
    public UserAccount Admin => Index.Admin;
    public Guid SpaceId => Index.SpaceId;
    public Guid OtherSpaceId => Index.OtherSpaceId;
    public Guid ExcludedSpaceId { get; }
    public Guid RestrictedSpaceId { get; }
    public FakeAiClientFactory Fake => Index.Fake;

    /// <param name="meaning">The fake's <see cref="FakeAiClientFactory.Meaning"/>: which texts share a concept.</param>
    public static async Task<SuggestionFixture> CreateAsync(Func<string, string>? meaning = null)
    {
        var index = await IndexFixture.CreateAsync();
        var reader = await index.Host.SeedUserAsync("reader");
        await index.Host.GrantAiAsync(reader);
        await index.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant
        {
            Id = Guid.NewGuid(), SpaceId = index.SpaceId, UserId = reader.Id, Role = (int)SpaceRole.Reader,
        }));
        var excluded = await index.Host.SeedSpaceAsync("Excluded", (index.Writer, SpaceRole.Writer), (reader, SpaceRole.Reader));
        var restricted = await index.Host.SeedSpaceAsync("Restricted");
        if (meaning is not null)
        {
            index.Fake.Meaning = meaning;
        }
        return new SuggestionFixture(index, reader, excluded, restricted);
    }

    /// <summary>Configured, built from the pages published so far, verified and active.</summary>
    public async Task<GenerationView> ActivateAsync()
    {
        KeepClockAhead();
        return await Index.ActiveIndexAsync();
    }

    /// <summary>Runs the index worker until the queue is empty (pages published after activation).</summary>
    public Task<int> IndexAsync()
    {
        KeepClockAhead();
        return Index.RunAsync();
    }

    /// <summary>The worker claims tasks due at the fake clock and the trigger enqueues them at the database's now(): keep the
    /// clock ahead after a slow start (forward only), as HybridSearchFixture does.</summary>
    private void KeepClockAhead()
    {
        var ahead = DateTimeOffset.UtcNow.AddMinutes(1);
        if (Index.Clock.GetUtcNow() < ahead)
        {
            Index.Clock.SetUtcNow(ahead);
        }
    }

    public async Task<PageDto> PublishAsync(string title, string markdown = "Obsah stránky.", Guid? spaceId = null, Guid? parentId = null,
        UserAccount? author = null)
    {
        author ??= Writer;
        var page = await Index.Pages.CreateAsync(new CreatePageRequest(spaceId ?? SpaceId, parentId, title, IndexFixture.Rich(markdown), markdown), author, default);
        return await Index.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), author, default);
    }

    public Task<PageDto> DraftAsync(string title, string markdown = "Rozepsaný text.", Guid? spaceId = null)
        => Index.Pages.CreateAsync(new CreatePageRequest(spaceId ?? SpaceId, null, title, IndexFixture.Rich(markdown), markdown), Writer, default);

    public Task<PageDto> MakeTemplateAsync(Guid pageId) => Index.As<PageDto>(Admin, new SetPageTemplate(pageId, true));

    public Task TrashAsync(Guid pageId) => Index.Host.ExecuteAsync("""
        UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"=now(),"TrashedById"="OwnerId","TrashBatchId"=gen_random_uuid() WHERE "Id"=:id
        """, pageId);

    /// <summary>The page stops inheriting the space's access: only its owner (and WikiAdmins) still read it.</summary>
    public Task RestrictAsync(Guid pageId) => Index.Host.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=:id""", pageId);

    public Task<SpaceAiSettingsView> ExcludeAsync(Guid spaceId)
        => Index.As<SpaceAiSettingsView>(Admin, new SaveSpaceAiSettings(spaceId, new SaveSpaceAiSettingsRequest(false, null)));

    /// <summary>Switches one AI task on or off (and its cloud permission for working text).</summary>
    public async Task SetTaskAsync(string task, bool enabled, bool? cloudWorkingText = null)
    {
        var current = await Index.As<AiFeaturesView>(Admin, new Atlas.Server.Features.Ai.Settings.Queries.GetAiFeatures());
        await Index.As<AiFeaturesView>(Admin, new SaveAiFeatures(new SaveAiFeaturesRequest([new(task, enabled, cloudWorkingText)], current.Version)));
    }

    /// <summary>The embeddings profile moved to a cloud provider (same model, so the index stays) and probed.</summary>
    public async Task MoveProfileToCloudAsync()
    {
        var profile = await Index.ProfileAsync();
        await Index.As<AiProfileView>(Admin, new SaveAiProfile(Atlas.Server.Features.Ai.Settings.Domain.AiProfileKind.Embeddings,
            IndexFixture.Embeddings(expectedVersion: profile.Version, apiKey: "sk-cloud-test", baseUrl: "https://embeddings.example/v1")
                with { Location = Atlas.Server.Features.Ai.Settings.Domain.AiLocation.Cloud }));
        await Index.ProbeAsync();
    }

    /// <summary>Runs one step of the endpoints' path as the actor.</summary>
    public async Task<T> AsAsync<T>(UserAccount actor, Func<AiSuggestionsFlow, Task<T>> call)
    {
        Index.Host.Actor.Current = actor;
        using var scope = Index.Host.Services.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<AiSuggestionsFlow>());
    }

    /// <summary>Every text the suggestions sent to the fake model for a task.</summary>
    public IReadOnlyList<string> Sent(string task) => Fake.Calls.Where(x => x.Task == task).SelectMany(x => x.Inputs).ToArray();

    public IReadOnlyList<string> SentWorkingText => Sent(AiTaskCatalog.EditorLink).Concat(Sent(AiTaskCatalog.PageCreateSuggest)).ToArray();

    /// <summary>Gives a page that was never published (a draft) a <c>KnowledgePage</c> row for its working revision, as if a broken
    /// worker had indexed it: the reads must still never offer it.</summary>
    public async Task IndexDraftAsync(Guid generationId, Guid pageId, string meaningText)
    {
        await using var connection = new NpgsqlConnection(Index.Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO "KnowledgePage"("GenerationId","PageId","RevisionId","IsTemplate","TitleEmbedding","TitleHash","Centroid","IndexedAt")
            SELECT @generation,r."PageId",r."Id",false,CAST(@vector AS vector),sha256(convert_to(r."Id"::text,'UTF8')),CAST(@vector AS vector),now()
            FROM "PageRevision" r WHERE r."PageId"=@page AND r."ActiveTo" IS NULL
            """, connection);
        command.Parameters.AddWithValue("vector", VectorText.Format(FakeAi.Vector(Fake.Meaning(meaningText))));
        command.Parameters.AddWithValue("generation", generationId);
        command.Parameters.AddWithValue("page", pageId);
        Xunit.Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    public ValueTask DisposeAsync() => Index.DisposeAsync();
}
