using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.SavedProfiles;
using Atlas.Server.Features.AiIndex.Commands;
using Atlas.Server.Features.AiIndex.Contracts;
using Xunit;

namespace Atlas.Server.Tests.AiIndex;

public sealed class SavedProfileIndexTests
{
    [PostgreSqlFact]
    public async Task Saved_embedding_replacement_pins_its_revision_until_generation_activation()
    {
        await using var f = await IndexFixture.CreateAsync();
        var old = await f.ActiveIndexAsync();
        var active = await f.ProfileAsync();
        var saved = await f.As<SavedProfileView>(f.Admin, new SaveSavedProfile("embeddings", null,
            new("Replacement", IndexFixture.Embeddings("replacement-model", apiKey: "candidate-fixture-secret"), null)));
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.As<AiProfileView>(f.Admin,
            new ActivateSavedProfile("embeddings", saved.Id, new(active.Version, saved.Version))));
        Assert.Equal("ai_generation_required", refused.Code);
        var request = new StartIndexGenerationRequest("replace", null, saved.Id, saved.Version, active.Version);
        var stale = await Assert.ThrowsAsync<ApiException>(() => f.As<GenerationView>(f.Admin,
            new StartIndexGeneration(request with { ExpectedSavedVersion = null })));
        Assert.Equal(409, stale.Status);
        var candidate = await f.As<GenerationView>(f.Admin, new StartIndexGeneration(request));
        await f.As<SavedProfileView>(f.Admin, new SaveSavedProfile("embeddings", saved.Id,
            new("Replacement", IndexFixture.Embeddings("later-model"), saved.Version)));
        Assert.Equal(old.Id, (await f.StatusAsync()).Active!.Id);
        Assert.Equal(active.Model, (await f.ProfileAsync()).Model);
        Assert.Equal("replacement-model", (await f.StatusAsync()).Building!.Model);
        await f.RunAsync();
        Assert.True((await f.VerifyAsync(candidate.Id)).CanActivate);
        await f.ActivateAsync((await f.StatusAsync()).Building!);
        Assert.Equal("replacement-model", (await f.ProfileAsync()).Model);
        Assert.True((await f.ProfileAsync()).HasApiKey);
    }
}
