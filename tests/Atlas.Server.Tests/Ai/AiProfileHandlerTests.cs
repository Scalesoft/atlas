using System.Text.Json;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Xunit;

namespace Atlas.Server.Tests.Ai;

/// <summary>A1: profiles are immutable revisions behind a temporal assignment; keys are write-only and never follow a
/// profile to another origin; the embedding fingerprint decides index compatibility.</summary>
public sealed class AiProfileHandlerTests
{
    private const string SyntheticKey = "ai-handler-fixture-credential";

    [PostgreSqlFact]
    public async Task Draft_catalogue_reuses_keys_only_at_the_saved_origin_and_never_persists()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var request = AiSettingsFixture.Embeddings() with { ApiKey = SyntheticKey };
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, request));
        var draft = new DiscoverAiModelsRequest(request.Provider, request.Location, request.BaseUrl, null, false);
        var original = await f.As<Atlas.Server.Features.Ai.Providers.AiProfileSnapshot>(f.Admin, new PrepareAiModelDiscovery(AiProfileKind.Embeddings, draft));
        Assert.Equal(SyntheticKey, f.Unprotect(original.ProtectedApiKey));
        Assert.Equal("", original.Model);
        var moved = await f.As<Atlas.Server.Features.Ai.Providers.AiProfileSnapshot>(f.Admin, new PrepareAiModelDiscovery(AiProfileKind.Embeddings,
            draft with { BaseUrl = "http://another-company.example/v1" }));
        Assert.Equal("", moved.ProtectedApiKey);
        var replaced = await f.As<Atlas.Server.Features.Ai.Providers.AiProfileSnapshot>(f.Admin, new PrepareAiModelDiscovery(AiProfileKind.Embeddings,
            draft with { ApiKey = "replacement-fixture-key" }));
        Assert.Equal("replacement-fixture-key", f.Unprotect(replaced.ProtectedApiKey));
        Assert.Equal(SyntheticKey, f.Unprotect(Assert.Single(await f.RevisionsAsync(AiProfileKind.Embeddings)).ProtectedApiKey));
        await AiSettingsFixture.Denied(403, () => f.As<Atlas.Server.Features.Ai.Providers.AiProfileSnapshot>(f.Reader,
            new PrepareAiModelDiscovery(AiProfileKind.Embeddings, draft)));
    }

    [PostgreSqlFact]
    public async Task Hosted_default_url_and_chat_model_changes_preserve_the_embedding_profile()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings()));
        var embedding = Assert.Single(await f.RevisionsAsync(AiProfileKind.Embeddings));
        f.Index.ActiveFingerprint = embedding.Fingerprint;
        var request = AiSettingsFixture.Chat(SyntheticKey) with { BaseUrl = null };
        var chat = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, request));
        Assert.Equal("https://api.openai.com/v1", chat.BaseUrl);
        var changed = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat,
            request with { Model = "another-chat", ApiKey = null, ExpectedVersion = chat.Version }));
        Assert.Equal("another-chat", changed.Model);
        Assert.True(changed.HasApiKey);
        Assert.False(changed.ModelLockedByIndex);
        var still = Assert.Single(await f.RevisionsAsync(AiProfileKind.Embeddings));
        Assert.Equal((embedding.Id, embedding.Fingerprint), (still.Id, still.Fingerprint));
    }

    [PostgreSqlFact]
    public async Task Gateway_profiles_round_trip_and_a_provider_change_drops_the_stored_key()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var request = AiSettingsFixture.Chat(SyntheticKey) with
        {
            Provider = "gateway", Location = AiLocation.Company, BaseUrl = "http://gateway.example:8080/v1", Model = "gateway-test",
        };
        var saved = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, request));
        Assert.Equal(("gateway", AiLocation.Company, "gateway-test"), (saved.Provider, saved.Location, saved.Model));
        Assert.Equal(saved, await f.As<AiProfileView>(f.Admin, new GetAiProfile(AiProfileKind.Chat)));
        Assert.True(saved.HasApiKey);
        Assert.DoesNotContain(SyntheticKey, JsonSerializer.Serialize(saved), StringComparison.Ordinal);
        var stored = Assert.Single(await f.RevisionsAsync(AiProfileKind.Chat)).ProtectedApiKey;
        Assert.DoesNotContain(SyntheticKey, stored, StringComparison.Ordinal);
        Assert.Equal(SyntheticKey, f.Unprotect(stored));

        var kept = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, request with
        {
            ApiKey = null, BaseUrl = "http://gateway.example:8080/api/v1", ExpectedVersion = saved.Version,
        }));
        Assert.True(kept.HasApiKey);
        var changed = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, request with
        {
            Provider = AiProvider.OpenAiCompatible, ApiKey = null, BaseUrl = kept.BaseUrl, ExpectedVersion = kept.Version,
        }));
        Assert.False(changed.HasApiKey);
        Assert.Equal("", (await f.RevisionsAsync(AiProfileKind.Chat))[^1].ProtectedApiKey);

        var quick = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Quick, request with { ApiKey = null }));
        Assert.Equal("gateway", quick.Provider);
        Assert.False(quick.HasApiKey);
    }

    [PostgreSqlFact]
    public async Task Gateway_rejects_cloud_embeddings_and_an_enabled_or_tested_profile_without_a_url()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var request = AiSettingsFixture.Chat(null) with { Provider = "gateway", Location = AiLocation.Company };
        var cloud = await AiSettingsFixture.Denied(400, () => f.As<AiProfileView>(f.Admin,
            new SaveAiProfile(AiProfileKind.Chat, request with { Location = AiLocation.Cloud })));
        Assert.Equal("Gateway je dostupná jen na firemním serveru pro Chat a Rychlé úlohy.", cloud.Message);
        var embeddings = await AiSettingsFixture.Denied(400, () => f.As<AiProfileView>(f.Admin,
            new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings() with { Provider = "gateway" })));
        Assert.Equal(cloud.Message, embeddings.Message);
        var missingUrl = await AiSettingsFixture.Denied(400, () => f.As<AiProfileView>(f.Admin,
            new SaveAiProfile(AiProfileKind.Chat, request with { BaseUrl = null })));
        Assert.Equal("Pro zapnutí profilu vyplňte adresu API.", missingUrl.Message);
        Assert.Empty(await f.RevisionsAsync(AiProfileKind.Chat));
        Assert.Empty(await f.RevisionsAsync(AiProfileKind.Embeddings));

        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, request with { Enabled = false, BaseUrl = null }));
        var cannotTest = await AiSettingsFixture.Denied(400, () => f.As<Atlas.Server.Features.Ai.Providers.AiProfileSnapshot>(f.Admin,
            new BeginAiProfileTest(AiProfileKind.Chat)));
        Assert.Equal("Pro test spojení vyplňte a uložte model a adresu API.", cannotTest.Message);
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Saving_a_profile_adds_an_immutable_revision_and_moves_the_assignment_with_cas()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var initial = await f.As<AiProfileView>(f.Admin, new GetAiProfile(AiProfileKind.Embeddings));
        Assert.False(initial.Configured);
        Assert.False(initial.Enabled);
        Assert.Null(initial.Version);

        var request = AiSettingsFixture.Embeddings();
        var first = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, request));
        Assert.True(first.Configured);
        Assert.True(first.Enabled);
        Assert.Equal(("text-embedding-bge-m3", 1024), (first.Model, first.Dimensions));
        var second = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, request with { MaxConcurrency = 2, ExpectedVersion = first.Version }));
        Assert.NotEqual(first.Version, second.Version);
        Assert.Equal(2, second.MaxConcurrency);
        Assert.Equal(second, await f.As<AiProfileView>(f.Admin, new GetAiProfile(AiProfileKind.Embeddings)));

        // A stale or missing version changes nothing.
        await AiSettingsFixture.Denied(409, () => f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, request with { ExpectedVersion = first.Version })));
        await AiSettingsFixture.Denied(409, () => f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, request)));
        var revisions = await f.RevisionsAsync(AiProfileKind.Embeddings);
        Assert.Equal(2, revisions.Count);
        using (var session = f.Host.OpenSession())
        {
            var assignments = await session.QueryOver<AiProfileAssignment>().OrderBy(x => x.ActiveFrom).Asc.ListAsync();
            Assert.Equal(2, assignments.Count);
            Assert.Equal(assignments[0].ActiveTo, assignments[1].ActiveFrom);
            Assert.Null(assignments[1].ActiveTo);
            Assert.Equal(revisions.Select(x => x.Id), assignments.Select(x => x.RevisionId));
            Assert.Equal(second.Version, AdministrationVersion.Format(assignments[1].ActiveFrom));
        }
        // Only the two committed saves told the configuration cache.
        Assert.Equal(2, f.Changes.Count);

        // A revision is history (since 3.6 in C#, migration 2026110945): its mapping is immutable, so a change is never written, and
        // IntegrityRules refuses a delete.
        await f.Host.SeedAsync(async s => (await s.GetAsync<AiProfileRevision>(revisions[0].Id))!.Model = "other");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.SeedAsync(async s =>
        {
            await s.DeleteAsync(await s.GetAsync<AiProfileRevision>(revisions[0].Id));
            await s.FlushAsync();
        }));
        Assert.StartsWith("AI profile revisions are immutable", error.Message, StringComparison.Ordinal);
        Assert.Equal(revisions.Select(x => x.Model), (await f.RevisionsAsync(AiProfileKind.Embeddings)).Select(x => x.Model));
    }

    [PostgreSqlFact]
    public async Task Api_key_is_write_only_protected_and_dropped_when_the_origin_changes()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var request = AiSettingsFixture.Embeddings() with { ApiKey = SyntheticKey };
        var saved = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, request));
        Assert.True(saved.HasApiKey);
        Assert.DoesNotContain(SyntheticKey, JsonSerializer.Serialize(saved), StringComparison.Ordinal);
        var stored = Assert.Single(await f.RevisionsAsync(AiProfileKind.Embeddings)).ProtectedApiKey;
        Assert.DoesNotContain(SyntheticKey, stored, StringComparison.Ordinal);
        Assert.Equal(SyntheticKey, f.Unprotect(stored));

        // Another timeout (and another path on the same origin) keeps the key.
        var kept = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, request with
        {
            ApiKey = null, TimeoutSeconds = 45, BaseUrl = "http://lmstudio.example:1234/api/v1", ExpectedVersion = saved.Version
        }));
        Assert.True(kept.HasApiKey);
        Assert.Equal(SyntheticKey, f.Unprotect((await f.RevisionsAsync(AiProfileKind.Embeddings))[^1].ProtectedApiKey));

        // Another host without a new key must never receive the old one.
        var moved = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, request with
        {
            ApiKey = null, BaseUrl = "http://gpu.example:1234/v1", ExpectedVersion = kept.Version
        }));
        Assert.False(moved.HasApiKey);
        Assert.Equal("", (await f.RevisionsAsync(AiProfileKind.Embeddings))[^1].ProtectedApiKey);

        var cleared = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, request with { ExpectedVersion = moved.Version, BaseUrl = moved.BaseUrl }));
        Assert.True(cleared.HasApiKey);
        cleared = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, request with { ApiKey = null, ClearApiKey = true, ExpectedVersion = cleared.Version, BaseUrl = moved.BaseUrl }));
        Assert.False(cleared.HasApiKey);
    }

    [PostgreSqlFact]
    public async Task Validation_rejects_anthropic_embeddings_other_dimensions_cloud_over_http_and_credentials_in_the_url()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var embeddings = AiSettingsFixture.Embeddings();
        var anthropic = await AiSettingsFixture.Denied(400, () => f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, embeddings with { Provider = AiProvider.Anthropic, BaseUrl = null })));
        Assert.Equal("Anthropic neposkytuje embeddingy; použijte LM Studio nebo OpenAI.", anthropic.Message);
        var dimensions = await AiSettingsFixture.Denied(400, () => f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, embeddings with { Dimensions = 768 })));
        Assert.Equal("Atlas 2.0 podporuje embeddingy s 1 024 rozměry.", dimensions.Message);
        var chat = AiSettingsFixture.Chat(SyntheticKey);
        var reasoning = await AiSettingsFixture.Denied(400, () => f.As<AiProfileView>(f.Admin,
            new SaveAiProfile(AiProfileKind.Chat, chat with { ReasoningEffort = "invalid" })));
        Assert.Equal("Úroveň uvažování musí být none, low, medium nebo high.", reasoning.Message);
        var temperature = await AiSettingsFixture.Denied(400, () => f.As<AiProfileView>(f.Admin,
            new SaveAiProfile(AiProfileKind.Chat, chat with { Temperature = 3f })));
        Assert.Equal("Teplota musí být mezi 0 a 2.", temperature.Message);
        await AiSettingsFixture.Denied(400, () => f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, chat with { BaseUrl = "http://api.openai.example/v1" })));
        await AiSettingsFixture.Denied(400, () => f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, chat with { BaseUrl = "https://user:secret@api.openai.example/v1" })));

        Assert.Empty(await f.RevisionsAsync(AiProfileKind.Embeddings));
        Assert.Empty(await f.RevisionsAsync(AiProfileKind.Chat));
        Assert.Equal(0, f.Changes.Count);
    }

    [PostgreSqlFact]
    public async Task Key_rotation_keeps_the_embedding_fingerprint()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var request = AiSettingsFixture.Embeddings() with { ApiKey = SyntheticKey };
        var saved = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, request));
        var fingerprint = Assert.Single(await f.RevisionsAsync(AiProfileKind.Embeddings)).Fingerprint;
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);

        // An index generation is now active with that fingerprint: key, URL and timeout still change freely.
        f.Index.ActiveFingerprint = fingerprint;
        Assert.True((await f.As<AiProfileView>(f.Admin, new GetAiProfile(AiProfileKind.Embeddings))).ModelLockedByIndex);
        var rotated = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, request with
        {
            ApiKey = "rotated-fixture-credential", BaseUrl = "https://lmstudio-proxy.example/v1", TimeoutSeconds = 90, ExpectedVersion = saved.Version
        }));
        Assert.Equal(fingerprint, (await f.RevisionsAsync(AiProfileKind.Embeddings))[^1].Fingerprint);

        // Model version and prefixes are part of it: refused while the index is active, a new fingerprint without it.
        var versioned = request with { ApiKey = null, BaseUrl = rotated.BaseUrl, ModelVersion = "2", ExpectedVersion = rotated.Version };
        var refused = await AiSettingsFixture.Denied(409, () => f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, versioned)));
        Assert.Equal("ai_generation_required", refused.Code);
        Assert.Equal("Změna modelu vyžaduje novou generaci indexu. Použijte Index → Vyměnit model.", refused.Message);
        f.Index.ActiveFingerprint = null;
        var other = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, versioned));
        var prefixed = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, versioned with { QueryPrefix = "search_query: ", ExpectedVersion = other.Version }));
        Assert.Equal("search_query: ", prefixed.QueryPrefix);
        var fingerprints = (await f.RevisionsAsync(AiProfileKind.Embeddings)).Select(x => x.Fingerprint).ToArray();
        Assert.Equal(4, fingerprints.Length);
        Assert.Equal(3, fingerprints.Distinct().Count());
    }
}
