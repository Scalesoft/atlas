using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Availability;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Usage;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Ai;

/// <summary>A3: the probes behind the circuit breaker (AI-25), driven by <see cref="AiProbeRunner.RunOnceAsync"/> on a fake
/// clock instead of the hosted service's timer.</summary>
public sealed class AiProbeRunnerTests
{
    [Fact]
    public async Task Borrowed_quick_profile_never_probes_its_unused_connection()
    {
        using var ai = new AvailabilityHost();
        var chat = FakeAi.Profile(AiProfileKind.Chat);
        var quick = FakeAi.Profile(AiProfileKind.Quick) with { UseChatProfile = true, Model = "", BaseUrl = null };
        await ai.ConfigureAsync(true, chat, quick);

        Assert.Equal(1, await ai.Runner.RunOnceAsync(ai.Clock.GetUtcNow(), default));
        Assert.Equal([chat.RevisionId], ai.Fake.ProbedRevisions);
        Assert.Equal(0, ai.Fake.Probes(AiProfileKind.Quick));

        await ai.ConfigureAsync(true, quick);
        Assert.Equal(0, await ai.Runner.RunOnceAsync(ai.Clock.GetUtcNow(), default));
    }

    [Fact]
    public async Task Gateway_tolerates_another_served_model_and_reports_it_while_openai_compatible_stays_strict()
    {
        using var ai = new AvailabilityHost();
        var chat = FakeAi.Profile(AiProfileKind.Chat) with { Provider = AiProvider.Gateway };
        var embeddings = FakeAi.Profile(AiProfileKind.Embeddings);
        await ai.ConfigureAsync(true, chat, embeddings);
        ai.Fake.SetModelListed(AiProfileKind.Chat, false);
        ai.Fake.SetModelListed(AiProfileKind.Embeddings, false);
        for (var i = 0; i < AiAvailability.FailuresToOpen; i++)
        {
            await ai.Runner.RunOnceAsync(ai.Clock.GetUtcNow(), default);
            ai.Clock.Advance(AiProbeRunner.CheckInterval);
        }

        var gateway = ai.Availability.Health(AiProfileKind.Chat);
        Assert.Equal((AiProfileState.Available, (string?)null, FakeAiClientFactory.OtherModel), (gateway.State, gateway.LastErrorCode, gateway.ServedModel));
        var strict = ai.Availability.Health(AiProfileKind.Embeddings);
        Assert.Equal((AiProfileState.Unavailable, AiOutcome.ModelNotFound, (string?)null), (strict.State, strict.LastErrorCode, strict.ServedModel));

        // The operator loads the configured model again (or the admin fixes the profile): the warning goes away.
        ai.Fake.SetModelListed(AiProfileKind.Chat, true);
        await ai.Runner.RunOnceAsync(ai.Clock.GetUtcNow(), default);
        Assert.Equal((AiProfileState.Available, (string?)null), (ai.Availability.Health(AiProfileKind.Chat).State, ai.Availability.Health(AiProfileKind.Chat).ServedModel));
    }

    [Fact]
    public async Task Unavailable_profiles_are_probed_every_10_seconds_and_configured_ones_every_60_without_traffic()
    {
        using var ai = new AvailabilityHost();
        var embeddings = FakeAi.Profile(AiProfileKind.Embeddings);
        var quick = FakeAi.Profile(AiProfileKind.Quick) with { Enabled = false };
        // Chat is not configured, quick tasks are configured but switched off: neither is ever probed.
        await ai.ConfigureAsync(true, embeddings, quick);
        var start = ai.Clock.GetUtcNow();
        ai.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Timeout);
        async Task<int> At(int seconds)
        {
            ai.Clock.Advance(start.AddSeconds(seconds) - ai.Clock.GetUtcNow());
            await ai.Runner.RunOnceAsync(ai.Clock.GetUtcNow(), default);
            return ai.Fake.Probes(AiProfileKind.Embeddings);
        }

        // Unverified after start: probed at once (O13). A failed check is repeated after 10 s (AI4-11); the second failure in a row opens
        // the breaker.
        Assert.Equal(1, await At(0));
        Assert.Equal((AiProfileState.Unknown, AiOutcome.Timeout), (ai.Availability.Health(AiProfileKind.Embeddings).State, ai.Availability.Health(AiProfileKind.Embeddings).LastErrorCode));
        Assert.Equal(1, await At(9));
        Assert.Equal(2, await At(10));
        Assert.Equal(AiProfileState.Unavailable, ai.Availability.Health(AiProfileKind.Embeddings).State);
        Assert.Equal(start.AddSeconds(10).UtcDateTime, ai.Availability.Health(AiProfileKind.Embeddings).Since);
        Assert.Equal(2, await At(19));

        // Unavailable: probed every 10 s; the first success restores the profile.
        ai.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Healthy);
        Assert.Equal(3, await At(20));
        Assert.Equal(AiProfileState.Available, ai.Availability.Health(AiProfileKind.Embeddings).State);

        // Available without traffic: verified every 60 s.
        Assert.Equal(3, await At(79));
        Assert.Equal(4, await At(80));
        // Traffic is verification too: a successful call at 110 s moves the next probe to 170 s.
        ai.Clock.Advance(start.AddSeconds(110) - ai.Clock.GetUtcNow());
        using (var generator = ai.Fake.Embeddings(embeddings, AiTaskCatalog.SearchEmbedQuery))
        {
            await generator.GenerateAsync(["dotaz"]);
        }
        Assert.Equal(4, await At(140));
        Assert.Equal(5, await At(170));
        Assert.Equal((0, 0), (ai.Fake.Probes(AiProfileKind.Quick), ai.Fake.Probes(AiProfileKind.Chat)));

        // The master switch off: nothing is probed any more.
        await ai.ConfigureAsync(false, embeddings, quick);
        Assert.Equal(AiProfileState.Disabled, ai.Availability.Health(AiProfileKind.Embeddings).State);
        Assert.Equal(5, await At(400));
    }

    [PostgreSqlFact]
    public async Task Probe_sends_no_content_uses_the_saved_revision_and_flags_a_missing_model()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var fake = f.Host.Services.GetRequiredService<FakeAiClientFactory>();
        var runner = f.Host.Services.GetRequiredService<AiProbeRunner>();
        var availability = f.Host.Services.GetRequiredService<AiAvailability>();
        var usage = f.Host.Services.GetRequiredService<AiUsageRecorder>();
        await f.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new(true, null)));
        var saved = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings()));
        var now = DateTimeOffset.UtcNow;

        await runner.RunOnceAsync(now, default);
        var first = Assert.Single(await f.RevisionsAsync(AiProfileKind.Embeddings)).Id;
        Assert.Equal([first], fake.ProbedRevisions);
        Assert.Equal(AiProfileState.Available, availability.Health(AiProfileKind.Embeddings).State);

        // A save is a new revision: unverified again and probed at once, with the revision just saved.
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings() with { TimeoutSeconds = 45, ExpectedVersion = saved.Version }));
        await f.Host.Services.GetRequiredService<AiConfigurationCache>().GetAsync(default);
        Assert.Equal(AiProfileState.Unknown, availability.Health(AiProfileKind.Embeddings).State);
        await runner.RunOnceAsync(now, default);
        var second = (await f.RevisionsAsync(AiProfileKind.Embeddings))[1].Id;
        Assert.Equal([first, second], fake.ProbedRevisions);

        // The server answers but does not list the model: a failure with its own code, twice in a row an outage.
        fake.SetModelListed(AiProfileKind.Embeddings, false);
        await runner.RunOnceAsync(now.AddSeconds(60), default);
        await runner.RunOnceAsync(now.AddSeconds(90), default);
        Assert.Equal((AiProfileState.Unavailable, AiOutcome.ModelNotFound),
            (availability.Health(AiProfileKind.Embeddings).State, availability.Health(AiProfileKind.Embeddings).LastErrorCode));

        // Probes carry no content and spend no tokens: the fake model saw no call and nothing is usage.
        Assert.Equal(4, fake.Probes(AiProfileKind.Embeddings));
        Assert.Empty(fake.Calls);
        Assert.Empty(usage.Drain());
    }
}
