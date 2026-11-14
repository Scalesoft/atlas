using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Availability;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Ai;

/// <summary>A3: what <c>GET /api/ai/capabilities</c> answers (AI-24, AI-25): every condition of a capability in turn.</summary>
public sealed class AiCapabilitiesTests
{
    private static readonly AiCapabilitiesDto None = new(false, false, false, false, false, false);
    /// <summary>Everything an embeddings profile serves; the answers need a chat profile as well.</summary>
    private static readonly AiCapabilitiesDto All = new(true, true, true, true, false, false);

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Capabilities_are_false_without_configuration_and_follow_switch_tasks_profiles_generation_and_availability()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        f.Host.Actor.Current = f.Admin;
        var capabilities = f.Host.Services.GetRequiredService<AiCapabilities>();
        var gate = f.Host.Services.GetRequiredService<AiGate>();
        var runner = f.Host.Services.GetRequiredService<AiProbeRunner>();
        var fake = f.Host.Services.GetRequiredService<FakeAiClientFactory>();
        Task<AiCapabilitiesDto> Current() => capabilities.GetAsync(default).AsTask();
        Task Probe(int seconds = 0) => runner.RunOnceAsync(DateTimeOffset.UtcNow.AddSeconds(seconds), default);

        // Nothing configured (a fresh install, an upgrade from 1.x): all false, and every AI endpoint answers 503.
        Assert.Equal(None, await Current());
        Assert.Equal(AiGate.UnavailableCode, (await Assert.ThrowsAsync<ApiException>(() => gate.RequireAsync(AiTaskCatalog.SearchEmbedQuery, default).AsTask())).Code);

        // A profile and an index, but the master switch is off: nothing, and nothing is probed.
        var profile = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings()));
        var revision = Assert.Single(await f.RevisionsAsync(AiProfileKind.Embeddings));
        var generation = new AiActiveGeneration(Guid.NewGuid(), revision.Fingerprint!, revision.Id);
        f.Generation.Active = generation;
        f.ConfigurationChanged();
        await Probe();
        Assert.Equal(None, await Current());
        Assert.Equal(0, fake.Probes(AiProfileKind.Embeddings));

        // Switched on: still nothing until the first probe has succeeded (O13), then everything.
        await f.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new(true, null)));
        Assert.Equal(None, await Current());
        await Probe();
        Assert.Equal(All, await Current());
        await gate.RequireAsync(AiTaskCatalog.SearchEmbedQuery, default);

        // A task switched off takes only its own capability.
        var features = await f.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new(AiTaskCatalog.SearchEmbedQuery, false, null)], null)));
        Assert.Equal(All with { SemanticSearch = false }, await Current());
        await Assert.ThrowsAsync<ApiException>(() => gate.RequireAsync(AiTaskCatalog.SearchEmbedQuery, default).AsTask());
        await gate.RequireAsync(AiTaskCatalog.EditorLink, default);
        await f.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new(AiTaskCatalog.SearchEmbedQuery, true, null)], features.Version)));
        Assert.Equal(All, await Current());

        // The profile switched off, then on again: a new revision, unverified until probed.
        var off = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings() with { Enabled = false, ExpectedVersion = profile.Version }));
        Assert.Equal(None, await Current());
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings() with { ExpectedVersion = off.Version }));
        Assert.Equal(None, await Current());
        await Probe();
        Assert.Equal(All, await Current());

        // Without an active generation, or with one built by another model, the embedding capabilities have no index to use.
        f.Generation.Active = null;
        f.ConfigurationChanged();
        Assert.Equal(None, await Current());
        f.Generation.Active = generation with { Fingerprint = new string('0', 64) };
        f.ConfigurationChanged();
        Assert.Equal(None, await Current());
        f.Generation.Active = generation;
        f.ConfigurationChanged();
        Assert.Equal(All, await Current());

        // An outage: two failed calls in a row hide everything; the probe 30 s later brings it back.
        var current = (await f.Host.Services.GetRequiredService<AiConfigurationCache>().GetAsync(default)).Profile(AiProfileKind.Embeddings)!;
        fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Error(AiOutcome.Connection));
        using (var embeddings = fake.Embeddings(current, AiTaskCatalog.SearchEmbedQuery))
        {
            for (var i = 0; i < 2; i++)
            {
                await Assert.ThrowsAsync<AiCallException>(() => embeddings.GenerateAsync(["síť VPN"]));
            }
        }
        Assert.Equal(None, await Current());
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => gate.RequireAsync(AiTaskCatalog.PageRelated, default).AsTask())).Status);
        fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Healthy);
        await Probe(30);
        Assert.Equal(All, await Current());
    }

    /// <summary>Plan phase 3 R3, O1: Odpověď z wiki (<c>answer</c>) and the assistant (<c>assistant</c>) follow the chat profile,
    /// their own switches and an active generation (the stored chunks), never the embeddings or the generation's fingerprint.</summary>
    [PostgreSqlFact]
    public async Task Answer_and_assistant_follow_chat_availability_tasks_and_an_active_index()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        f.Host.Actor.Current = f.Admin;
        var capabilities = f.Host.Services.GetRequiredService<AiCapabilities>();
        var gate = f.Host.Services.GetRequiredService<AiGate>();
        var runner = f.Host.Services.GetRequiredService<AiProbeRunner>();
        var fake = f.Host.Services.GetRequiredService<FakeAiClientFactory>();
        var cache = f.Host.Services.GetRequiredService<AiConfigurationCache>();
        Task<AiCapabilitiesDto> Current() => capabilities.GetAsync(default).AsTask();
        Task Probe(int seconds = 0) => runner.RunOnceAsync(DateTimeOffset.UtcNow.AddSeconds(seconds), default);
        var everything = new AiCapabilitiesDto(true, true, true, true, true, true, PageSummary: true, Agent: true, ArrangePages: true);
        // The agent needs a chat profile only: neither embeddings nor an index (spec D3).
        var answersOnly = new AiCapabilitiesDto(false, false, false, false, true, true, PageSummary: true, Agent: true, ArrangePages: true);

        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings()));
        var revision = Assert.Single(await f.RevisionsAsync(AiProfileKind.Embeddings));
        var generation = new AiActiveGeneration(Guid.NewGuid(), revision.Fingerprint!, revision.Id);
        f.Generation.Active = generation;
        f.ConfigurationChanged();
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, AiSettingsFixture.Chat(null) with
        {
            Location = AiLocation.Company, BaseUrl = "http://lmstudio.example:1234/v1", Model = "gemma-4-26b-a4b",
        }));
        await f.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new(true, null)));
        await Probe();
        Assert.Equal(everything, await Current());

        // A chat outage: two failed calls hide the answer and the assistant; search, related pages and suggestions stay (matrix 2).
        var chat = (await cache.GetAsync(default)).Profile(AiProfileKind.Chat)!;
        fake.SetMode(AiProfileKind.Chat, FakeAiMode.Error(AiOutcome.Connection));
        using (var client = fake.Chat(chat, AiTaskCatalog.ChatAnswer))
        {
            for (var i = 0; i < 2; i++)
            {
                await Assert.ThrowsAsync<AiCallException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "otázka")]));
            }
        }
        Assert.Equal(everything with { Answer = false, Assistant = false, PageSummary = false, Agent = false, ArrangePages = false }, await Current());
        await gate.RequireAsync(AiTaskCatalog.SearchEmbedQuery, default);
        Assert.Equal(AiGate.UnavailableCode, (await Assert.ThrowsAsync<ApiException>(() => gate.RequireAsync(AiTaskCatalog.SearchAnswer, default).AsTask())).Code);
        fake.SetMode(AiProfileKind.Chat, FakeAiMode.Healthy);
        await Probe(30);
        Assert.Equal(everything, await Current());

        // An embeddings outage: the answers stay and their retrieval goes lexical over the stored chunks (O1).
        var embeddings = (await cache.GetAsync(default)).Profile(AiProfileKind.Embeddings)!;
        fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Error(AiOutcome.Connection));
        using (var generator = fake.Embeddings(embeddings, AiTaskCatalog.SearchEmbedQuery))
        {
            for (var i = 0; i < 2; i++)
            {
                await Assert.ThrowsAsync<AiCallException>(() => generator.GenerateAsync(["síť VPN"]));
            }
        }
        Assert.Equal(answersOnly, await Current());
        fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Healthy);
        await Probe(30);
        Assert.Equal(everything, await Current());

        // The answers need an active generation for the chunk text, but not one built by the current embeddings revision.
        f.Generation.Active = generation with { Fingerprint = new string('0', 64) };
        f.ConfigurationChanged();
        Assert.Equal(answersOnly, await Current());
        f.Generation.Active = null;
        f.ConfigurationChanged();
        Assert.Equal(None with { Agent = true, ArrangePages = true }, await Current());
        f.Generation.Active = generation;
        f.ConfigurationChanged();

        // Each is its own switch in Administrace → AI → Funkce.
        var saved = await f.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new(AiTaskCatalog.SearchAnswer, false, null)], null)));
        Assert.Equal(everything with { Answer = false }, await Current());
        // The left menu as one batch of moves is the agent's, off without it.
        saved = await f.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new(AiTaskCatalog.AssistantArrange, false, null)], saved.Version)));
        Assert.Equal(everything with { Answer = false, ArrangePages = false }, await Current());
        await f.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new(AiTaskCatalog.AssistantArrange, true, null), new(AiTaskCatalog.AssistantAgent, false, null, 5)], saved.Version)));
        Assert.Equal(everything with { Answer = false, Agent = false, ArrangePages = false }, await Current());
    }

    /// <summary>S1: the agent is Published, so it runs on a cloud chat profile; only reading working text (its own task row)
    /// needs the admin's permission there, and a company profile always may. The daily limit of the agent is stored and returned.</summary>
    [PostgreSqlFact]
    public async Task The_agent_runs_on_a_cloud_profile_and_its_working_text_row_needs_the_cloud_permission()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        f.Host.Actor.Current = f.Admin;
        var capabilities = f.Host.Services.GetRequiredService<AiCapabilities>();
        var runner = f.Host.Services.GetRequiredService<AiProbeRunner>();
        var cache = f.Host.Services.GetRequiredService<AiConfigurationCache>();
        var chat = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, AiSettingsFixture.Chat(null) with
        {
            Location = AiLocation.Cloud, BaseUrl = "https://chat.example/v1", ApiKey = "sk-cloud-test", Model = "gpt-test",
        }));
        await f.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new(true, null)));
        await runner.RunOnceAsync(DateTimeOffset.UtcNow, default);
        async Task<(bool Agent, bool WorkingText)> Now()
        {
            var snapshot = await cache.GetAsync(default);
            return (capabilities.Allows(snapshot, AiTaskCatalog.AssistantAgent), capabilities.Allows(snapshot, AiTaskCatalog.AssistantWorkingText));
        }

        // No index generation at all, yet the agent is available; the working text is not until the admin allows it.
        Assert.Equal((true, false), await Now());
        var features = await f.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new(AiTaskCatalog.AssistantWorkingText, true, true), new(AiTaskCatalog.AssistantAgent, true, null, 40)], null)));
        Assert.Equal((true, true), await Now());
        Assert.Equal(40, features.Items.Single(x => x.Task == AiTaskCatalog.AssistantAgent).DailyLimit);
        Assert.Null(features.Items.Single(x => x.Task == AiTaskCatalog.AssistantWorkingText).DailyLimit);
        Assert.Equal(40, (await f.As<AiFeaturesView>(f.Admin, new GetAiFeatures())).Items.Single(x => x.Task == AiTaskCatalog.AssistantAgent).DailyLimit);

        // A company profile always may read working text.
        await f.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new(AiTaskCatalog.AssistantWorkingText, true, false)], features.Version)));
        Assert.Equal((true, false), await Now());
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, AiSettingsFixture.Chat(null) with
        {
            Location = AiLocation.Company, BaseUrl = "http://lmstudio.example:1234/v1", Model = "gemma-4-26b-a4b", ExpectedVersion = chat.Version,
        }));
        await runner.RunOnceAsync(DateTimeOffset.UtcNow, default);
        Assert.Equal((true, true), await Now());
    }

    /// <summary>Z2/D3 (V5): an author's working text (E2, E4, P1–P3) reaches a cloud profile only with the admin's permission
    /// per task; published content and search queries go wherever the admin pointed the profile.</summary>
    [PostgreSqlFact]
    public async Task A_cloud_embeddings_profile_offers_working_text_tasks_only_with_the_admins_permission()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        f.Host.Actor.Current = f.Admin;
        var capabilities = f.Host.Services.GetRequiredService<AiCapabilities>();
        var gate = f.Host.Services.GetRequiredService<AiGate>();
        var runner = f.Host.Services.GetRequiredService<AiProbeRunner>();
        Task<AiCapabilitiesDto> Current() => capabilities.GetAsync(default).AsTask();
        var profile = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings() with
        {
            Location = AiLocation.Cloud, BaseUrl = "https://embeddings.example/v1", ApiKey = "sk-cloud-test",
        }));
        var revision = Assert.Single(await f.RevisionsAsync(AiProfileKind.Embeddings));
        f.Generation.Active = new AiActiveGeneration(Guid.NewGuid(), revision.Fingerprint!, revision.Id);
        f.ConfigurationChanged();
        await f.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new(true, null)));
        await runner.RunOnceAsync(DateTimeOffset.UtcNow, default);

        Assert.Equal(All with { EditorLinks = false, CreateSuggestions = false }, await Current());
        Assert.Equal(AiGate.UnavailableCode, (await Assert.ThrowsAsync<ApiException>(() => gate.RequireAsync(AiTaskCatalog.EditorLink, default).AsTask())).Code);

        var features = await f.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new(AiTaskCatalog.EditorLink, true, true)], null)));
        Assert.Equal(All with { CreateSuggestions = false }, await Current());
        await f.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new(AiTaskCatalog.PageCreateSuggest, true, true)], features.Version)));
        Assert.Equal(All, await Current());

        // The same permission means nothing for a company profile, which always may.
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings() with { ExpectedVersion = profile.Version }));
        await runner.RunOnceAsync(DateTimeOffset.UtcNow, default);
        Assert.Equal(All, await Current());
    }
}
