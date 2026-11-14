using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.AiIndex;
using Atlas.Server.Features.AiIndex.Commands;
using Atlas.Server.Features.AiIndex.Contracts;
using Atlas.Server.Features.AiIndex.Queries;
using Xunit;

namespace Atlas.Server.Tests.AiIndex;

/// <summary>Generations (plan §10, AI-07): a model exchange beside the active index, atomic activation, cancel and purge.</summary>
public sealed class IndexGenerationTests
{
    [PostgreSqlFact]
    public async Task Verification_does_not_send_titles_when_the_master_switch_is_off()
    {
        await using var f = await IndexFixture.CreateAsync();
        await f.PublishNewAsync("Private title", "Body");
        var generation = await f.ActiveIndexAsync();
        var settings = await f.As<AiSwitchView>(f.Admin, new Atlas.Server.Features.Ai.Settings.Queries.GetAiSwitch());
        await f.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new SaveAiSwitchRequest(false, settings.Version)));
        var before = f.Embedded.Count;

        var error = await Assert.ThrowsAsync<ApiException>(() => f.VerifyAsync(generation.Id));

        Assert.Equal((503, "ai_unavailable"), (error.Status, error.Code));
        Assert.Equal(before, f.Embedded.Count);
    }

    [PostgreSqlFact]
    public async Task Verification_samples_only_current_published_titles_in_included_spaces()
    {
        await using var f = await IndexFixture.CreateAsync();
        await f.PublishNewAsync("Allowed", "Included content");
        var trashed = await f.PublishNewAsync("Trashed", "Removed content");
        await f.PublishNewAsync("Excluded", "Excluded content", f.OtherSpaceId);
        var outdated = await f.PublishNewAsync("Old title", "Old content");
        var generation = await f.ActiveIndexAsync();
        await f.Host.SeedAsync(session => session.CreateSQLQuery("""
            UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"=now(),"TrashedById"="OwnerId","TrashBatchId"=gen_random_uuid() WHERE "Id"=:id
            """).SetParameter("id", trashed.Id).ExecuteUpdateAsync());
        await f.As<Atlas.Server.Features.SpaceAi.Contracts.SpaceAiSettingsView>(f.Admin,
            new Atlas.Server.Features.SpaceAi.Commands.SaveSpaceAiSettings(f.OtherSpaceId,
                new Atlas.Server.Features.SpaceAi.Contracts.SaveSpaceAiSettingsRequest(false, null)));
        await f.EditAndPublishAsync(outdated.Id, "New title", "New content");
        var before = f.Embedded.Count;

        await f.VerifyAsync(generation.Id);

        Assert.Equal(["query: Allowed"], f.Embedded.Skip(before));
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task A_new_model_builds_beside_the_active_index_and_activation_switches_generation_and_profile_atomically()
    {
        await using var f = await IndexFixture.CreateAsync();
        var first = await f.PublishNewAsync("VPN", "Připojení přes VPN klienta.");
        var old = await f.ActiveIndexAsync();
        var profile = await f.ProfileAsync();
        Assert.True(profile.ModelLockedByIndex);

        var building = await f.StartAsync("replace", IndexFixture.Embeddings("qwen3-embedding-0.6b", profile.Version));
        Assert.Equal(("Building", "qwen3-embedding-0.6b", 1), (building.State, building.Model, building.PendingTasks));
        // The candidate has no assignment yet: the section and the queries keep the old model.
        Assert.Equal("text-embedding-bge-m3", (await f.ProfileAsync()).Model);
        var status = await f.StatusAsync();
        Assert.Equal((old.Id, building.Id), (status.Active!.Id, status.Building!.Id));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.StartAsync("rebuild"))).Status);

        // A publication during the build reaches both generations.
        var second = await f.PublishNewAsync("Wi-Fi", "Firemní bezdrátová síť.");
        Assert.Equal(2, (await f.TasksAsync(second.Id)).Count);
        await f.RunAsync();
        var verification = await f.VerifyAsync(building.Id);
        Assert.True(verification.Complete);
        Assert.Equal((2, 2), (verification.Sanity.Checked, verification.Sanity.HitsAt5));
        Assert.True(verification.CanActivate);

        var activated = await f.ActivateAsync((await f.StatusAsync()).Building!);
        Assert.Equal(building.Id, activated.Active!.Id);
        Assert.Equal(2, activated.Active.IndexedPages);
        Assert.Equal(old.Id, Assert.Single(activated.Retired).Id);
        Assert.Null(activated.Building);
        // Generation and profile switched together, and A3's configuration cache already holds the new revision.
        Assert.Equal("qwen3-embedding-0.6b", (await f.ProfileAsync()).Model);
        Assert.Equal("qwen3-embedding-0.6b", (await f.Cache.GetAsync(default)).Profile(AiProfileKind.Embeddings)!.Model);
        foreach (var page in new[] { first, second })
        {
            Assert.NotNull(await f.IndexedAsync(building.Id, page.Id));
        }
        // From now on only the active generation is maintained.
        var third = await f.PublishNewAsync("Tisk", "Tiskárny v kanceláři.");
        Assert.Equal(building.Id, Assert.Single(await f.TasksAsync(third.Id)).GenerationId);
    }

    [PostgreSqlFact]
    public async Task A_replacement_build_proceeds_while_the_assigned_model_is_unavailable()
    {
        await using var f = await IndexFixture.CreateAsync();
        var active = await f.ActiveIndexAsync();
        var current = (await f.Cache.GetAsync(default)).Profile(AiProfileKind.Embeddings)!;
        f.Availability.Failure(current, AiOutcome.ModelNotFound);
        f.Availability.Failure(current, AiOutcome.ModelNotFound);
        Assert.False(f.Availability.IsAvailable(AiProfileKind.Embeddings));
        // More old-generation tasks than one claim batch must not starve the replacement.
        for (var i = 0; i <= IndexRunner.ClaimBatch; i++)
            await f.PublishNewAsync("Page " + i, "Body " + i);
        var profile = await f.ProfileAsync();
        var building = await f.StartAsync("replace", IndexFixture.Embeddings("replacement-model", profile.Version));

        Assert.Equal(IndexRunner.ClaimBatch + 1, await f.RunAsync());
        Assert.True((await f.VerifyAsync(building.Id)).CanActivate);
        Assert.All(await f.TasksAsync(), task =>
        {
            Assert.Equal(active.Id, task.GenerationId);
            Assert.Equal(0, task.Attempts);
            Assert.Null(task.LeasedUntil);
        });
        Assert.False(f.Availability.IsAvailable(AiProfileKind.Embeddings));
        Assert.Equal(building.Id, (await f.ActivateAsync((await f.StatusAsync()).Building!)).Active!.Id);
    }

    [PostgreSqlFact]
    public async Task Activation_is_refused_while_incomplete_and_a_restart_never_activates_a_build()
    {
        await using var f = await IndexFixture.CreateAsync();
        await f.ConfigureAsync();
        await f.PublishNewAsync("VPN", "Připojení přes VPN klienta.");
        var building = await f.StartAsync("build");

        var verification = await f.VerifyAsync(building.Id);
        Assert.Equal((false, 1, false), (verification.Complete, verification.MissingPages, verification.CanActivate));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.ActivateAsync(building))).Status);

        // A new worker after a restart builds on, but only an administrator activates.
        var restarted = ActivatorUtilities.CreateInstance<IndexRunner>(f.Host.Services);
        Assert.Equal(1, await restarted.RunUntilIdleAsync(f.Clock.GetUtcNow(), default));
        var status = await f.StatusAsync();
        Assert.Null(status.Active);
        Assert.Equal(("Building", 1), (status.Building!.State, status.Building.IndexedPages));

        // Only the generation's current version activates it, and only for a wiki admin.
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.As<IndexStatus>(f.Admin,
            new ActivateIndexGeneration(building.Id, new ActivateIndexGenerationRequest("2020-01-01T00:00:00.0000000Z"))))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<IndexStatus>(f.Writer, new GetIndexStatus()))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<IndexStatus>(f.Writer,
            new ActivateIndexGeneration(building.Id, new ActivateIndexGenerationRequest(status.Building.Version))))).Status);
        Assert.Equal(building.Id, (await f.ActivateAsync(status.Building)).Active!.Id);
    }

    [PostgreSqlFact]
    public async Task Incompatible_embedding_change_with_an_active_index_answers_ai_generation_required()
    {
        await using var f = await IndexFixture.CreateAsync();
        var generation = await f.ActiveIndexAsync();
        var profile = await f.ProfileAsync();

        var refused = await Assert.ThrowsAsync<ApiException>(() => f.As<AiProfileView>(f.Admin,
            new SaveAiProfile(AiProfileKind.Embeddings, IndexFixture.Embeddings("other-model", profile.Version))));
        Assert.Equal((409, "ai_generation_required"), (refused.Status, refused.Code));

        // Another URL (a TLS proxy in front of LM Studio) keeps the fingerprint: saved, and the index keeps working with the new revision.
        var moved = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings,
            IndexFixture.Embeddings(expectedVersion: profile.Version, baseUrl: "https://lmstudio.example/v1")));
        Assert.Equal("https://lmstudio.example/v1", moved.BaseUrl);
        await f.ProbeAsync();
        var page = await f.PublishNewAsync("Po přesunu", "Text po přesunu serveru.");
        await f.RunAsync();
        Assert.NotNull(await f.IndexedAsync(generation.Id, page.Id));
        Assert.Equal(generation.Id, (await f.StatusAsync()).Active!.Id);
    }

    [PostgreSqlFact]
    public async Task A_failed_build_keeps_the_active_index_and_retired_generations_are_removed_after_24_hours()
    {
        await using var f = await IndexFixture.CreateAsync();
        var page = await f.PublishNewAsync("VPN", "Připojení přes VPN klienta.");
        var first = await f.ActiveIndexAsync();

        var cancelled = await f.StartAsync("rebuild");
        var status = await f.As<IndexStatus>(f.Admin, new CancelIndexGeneration(cancelled.Id));
        Assert.Equal(first.Id, status.Active!.Id);
        Assert.Null(status.Building);
        Assert.DoesNotContain(await f.TasksAsync(), task => task.GenerationId == cancelled.Id);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.As<IndexStatus>(f.Admin, new CancelIndexGeneration(first.Id)))).Status);
        await f.Maintenance.RunOnceAsync(f.Clock.GetUtcNow(), default);
        Assert.Equal(0, await f.CountAsync($"""SELECT count(*) FROM "IndexGeneration" WHERE "Id"='{cancelled.Id}'"""));

        var second = await f.StartAsync("rebuild");
        await f.RunAsync();
        Assert.True((await f.VerifyAsync(second.Id)).CanActivate);
        await f.ActivateAsync((await f.StatusAsync()).Building!);
        Assert.NotNull(await f.IndexedAsync(first.Id, page.Id));

        await f.Maintenance.RunOnceAsync(f.Clock.GetUtcNow().AddHours(23), default);
        Assert.Equal(first.Id, Assert.Single((await f.StatusAsync()).Retired).Id);
        await f.Maintenance.RunOnceAsync(f.Clock.GetUtcNow().AddHours(25), default);
        Assert.Empty((await f.StatusAsync()).Retired);
        Assert.Null(await f.IndexedAsync(first.Id, page.Id));
        Assert.NotNull(await f.IndexedAsync(second.Id, page.Id));
    }
}
