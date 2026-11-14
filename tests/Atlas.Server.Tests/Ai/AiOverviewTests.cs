using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Availability;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.Ai.Usage;
using Atlas.Server.Features.Ai.Usage.Commands;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Ai;

/// <summary>A3: Administrace → AI → Přehled (AI-25 bod 7): the admin sees state, since when and the last error code; the
/// upstream's own words never.</summary>
public sealed class AiOverviewTests
{
    [PostgreSqlFact]
    public async Task Borrowed_quick_status_follows_chat_and_preserves_its_own_switch()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        await f.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new(true, null)));
        var quickRequest = AiSettingsFixture.Chat(null) with { UseChatProfile = true, Model = "", BaseUrl = null };
        var quick = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Quick, quickRequest));
        async Task<AiProfileStatus> Quick() => Assert.Single((await f.As<AiOverview>(f.Admin, new GetAiOverview())).Profiles,
            profile => profile.Kind == AiProfileKind.Quick);
        Assert.Equal("NotConfigured", (await Quick()).State);

        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, AiSettingsFixture.Chat("chat-fixture-credential")));
        var configuration = await f.Host.Services.GetRequiredService<AiConfigurationCache>().GetAsync(default);
        var chat = configuration.Profile(AiProfileKind.Chat)!;
        var availability = f.Host.Services.GetRequiredService<AiAvailability>();
        availability.Success(chat);
        var available = await Quick();
        Assert.Equal(("Available", chat.Model, true), (available.State, available.Model, available.UseChatProfile));

        availability.Failure(chat, AiOutcome.Connection);
        availability.Failure(chat, AiOutcome.Connection);
        var down = await Quick();
        Assert.Equal(("Unavailable", AiOutcome.Connection, availability.Health(AiProfileKind.Chat).Since),
            (down.State, down.LastErrorCode, down.Since));
        availability.Success(chat);
        Assert.Equal("Available", (await Quick()).State);

        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Quick, quickRequest with { Enabled = false, ExpectedVersion = quick.Version }));
        Assert.Equal("Disabled", (await Quick()).State);
    }

    private const string UpstreamText = "502 from gateway: TAJNE-PRESMEROVANI internal.example";

    [PostgreSqlFact]
    public async Task Overview_shows_state_since_and_last_error_code_but_never_upstream_text()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var fake = f.Host.Services.GetRequiredService<FakeAiClientFactory>();
        var usage = f.Host.Services.GetRequiredService<AiUsageRecorder>();
        var empty = await f.As<AiOverview>(f.Admin, new GetAiOverview());
        Assert.Equal((false, (string?)null), (empty.Enabled, empty.SwitchVersion));
        Assert.Equal([("chat", "NotConfigured"), ("embeddings", "NotConfigured"), ("quick", "NotConfigured"), ("rerank", "NotConfigured")], empty.Profiles.Select(x => (x.Kind, x.State)));
        Assert.Equal(new AiUsageSummary(0, 0, null, null, null), empty.Usage7Days);
        Assert.Null(empty.Index);

        var on = await f.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new(true, null)));
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings()));
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Quick, AiSettingsFixture.Chat("quick-fixture-credential") with { Enabled = false }));
        var probed = DateTimeOffset.UtcNow;
        await f.Host.Services.GetRequiredService<AiProbeRunner>().RunOnceAsync(probed, default);

        // Two failed calls; the gateway's answer carries text that must stay inside the provider client.
        var profile = (await f.Host.Services.GetRequiredService<AiConfigurationCache>().GetAsync(default)).Profile(AiProfileKind.Embeddings)!;
        fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Error(AiOutcome.Http5xx, UpstreamText));
        var before = DateTime.UtcNow;
        using (var embeddings = fake.Embeddings(profile, AiTaskCatalog.SearchEmbedQuery))
        {
            for (var i = 0; i < 2; i++)
            {
                await Assert.ThrowsAsync<AiCallException>(() => embeddings.GenerateAsync(["síť VPN"]));
            }
        }
        var after = DateTime.UtcNow;
        await f.Host.InvokeAsync(new RecordAiUsage(usage.Drain()));

        var overview = await f.As<AiOverview>(f.Admin, new GetAiOverview());
        Assert.Equal((true, on.Version), (overview.Enabled, overview.SwitchVersion));
        var state = Assert.Single(overview.Profiles, x => x.Kind == AiProfileKind.Embeddings);
        Assert.Equal(("Unavailable", AiOutcome.Http5xx, "text-embedding-bge-m3", "company"), (state.State, state.LastErrorCode, state.Model, state.Location));
        Assert.InRange(state.Since!.Value, before, after);
        Assert.Equal(probed.UtcDateTime, state.LastSuccessAt);
        Assert.Equal("Disabled", Assert.Single(overview.Profiles, x => x.Kind == AiProfileKind.Quick).State);
        Assert.Equal("NotConfigured", Assert.Single(overview.Profiles, x => x.Kind == AiProfileKind.Chat).State);
        // Seven days of use from AiUsage: both failed calls, tokens unknown (LM Studio reports none), so no cost estimate.
        Assert.Equal(new AiUsageSummary(2, 2, null, null, null), overview.Usage7Days);
        Assert.Null(overview.Index);

        var json = JsonSerializer.Serialize(overview, JsonSerializerOptions.Web);
        Assert.DoesNotContain("TAJNE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("internal.example", json, StringComparison.Ordinal);
        Assert.DoesNotContain(f.Logs.Messages, line => line.Contains("TAJNE", StringComparison.Ordinal));

        // Nobody but the wiki admin sees any of it.
        foreach (var actor in new[] { f.SpaceAdmin, f.Reader })
        {
            await AiSettingsFixture.Denied(403, () => f.As<AiOverview>(actor, new GetAiOverview()));
        }
    }
}
