using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Ai.Availability;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.Ai.Usage;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Ai;

/// <summary>A2: "Test spojení" in Administrace → AI runs against the saved revision, outside any transaction, and answers
/// with codes only.</summary>
public sealed class TestAiProfileTests
{
    private const string UpstreamText = "secret upstream detail";

    [PostgreSqlFact]
    public async Task Test_uses_the_saved_revision_and_returns_codes_without_upstream_text()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var fake = f.Host.Services.GetRequiredService<FakeAiClientFactory>();
        var usage = f.Host.Services.GetRequiredService<AiUsageRecorder>();
        // Nothing saved yet: nothing to test, and no call.
        await AiSettingsFixture.Denied(400, () => Test(f, f.Admin, AiProfileKind.Embeddings));
        var saved = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings()));
        await Assert.ThrowsAsync<ApiException>(() => Test(f, f.SpaceAdmin, AiProfileKind.Embeddings));
        Assert.Empty(fake.Calls);

        var healthy = await Test(f, f.Admin, AiProfileKind.Embeddings);
        Assert.Equal((true, (string?)null, (int?)1024, FakeAiClientFactory.EmbeddingModel), (healthy.Ok, healthy.Code, healthy.Dimensions, healthy.Model));
        Assert.Equal([TestAiProfileRunner.ProbeSentence], Assert.Single(fake.Calls).Inputs);

        fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Error(AiOutcome.Auth, UpstreamText));
        var denied = await Test(f, f.Admin, AiProfileKind.Embeddings);
        Assert.Equal((false, AiOutcome.Auth, (int?)null), (denied.Ok, denied.Code, denied.Dimensions));
        Assert.DoesNotContain(UpstreamText, JsonSerializer.Serialize(denied), StringComparison.Ordinal);
        Assert.DoesNotContain(f.Logs.Messages, line => line.Contains(UpstreamText, StringComparison.Ordinal));
        var revision = Assert.Single(await f.RevisionsAsync(AiProfileKind.Embeddings)).Id;
        Assert.Equal([(AiTaskCatalog.AdminTestEmbedding, revision, AiOutcome.Ok), (AiTaskCatalog.AdminTestEmbedding, revision, AiOutcome.Auth)],
            usage.Drain().Select(x => (x.Task, x.ProfileRevisionId, x.Outcome)));

        // A chat profile is tested through structured output, so the JSON fallback of weaker models is part of the test.
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, AiSettingsFixture.Chat("chat-fixture-credential") with { NativeJsonSchema = false }));
        var chat = await Test(f, f.Admin, AiProfileKind.Chat);
        Assert.Equal((true, FakeAiClientFactory.ChatModel), (chat.Ok, chat.Model));
        fake.SetMode(AiProfileKind.Chat, FakeAiMode.InvalidJson);
        Assert.Equal(AiOutcome.InvalidOutput, (await Test(f, f.Admin, AiProfileKind.Chat)).Code);

        // A save while the test runs: the answer would describe a revision that is no longer current.
        fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Healthy);
        var hold = fake.Hold(AiProfileKind.Embeddings);
        var running = Test(f, f.Admin, AiProfileKind.Embeddings);
        await hold.Entered;
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings() with { TimeoutSeconds = 45, ExpectedVersion = saved.Version }));
        hold.Release();
        await AiSettingsFixture.Denied(409, () => running);
    }

    [PostgreSqlFact]
    public async Task Gateway_serving_another_model_warns_in_the_overview_and_the_test_instead_of_failing()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var fake = f.Host.Services.GetRequiredService<FakeAiClientFactory>();
        var runner = f.Host.Services.GetRequiredService<AiProbeRunner>();
        await f.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new(true, null)));
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, AiSettingsFixture.Chat(null) with
        {
            Provider = AiProvider.Gateway, Location = AiLocation.Company, BaseUrl = "http://gateway.example:8080/v1", Model = "gateway-old",
        }));
        async Task<AiProfileStatus> Chat() => Assert.Single((await f.As<AiOverview>(f.Admin, new GetAiOverview())).Profiles,
            profile => profile.Kind == AiProfileKind.Chat);
        var expected = AiProfileStatus.ModelMismatch("gateway-old", FakeAiClientFactory.OtherModel);
        Assert.Contains("„gateway-old“", expected, StringComparison.Ordinal);
        Assert.Contains($"„{FakeAiClientFactory.OtherModel}“", expected, StringComparison.Ordinal);

        fake.SetModelListed(AiProfileKind.Chat, false);
        var now = DateTimeOffset.UtcNow;
        await runner.RunOnceAsync(now, default);
        await runner.RunOnceAsync(now.AddSeconds(60), default);
        var mismatch = await Chat();
        Assert.Equal(("Available", (string?)null, expected), (mismatch.State, mismatch.LastErrorCode, mismatch.Warning));
        var tested = await Test(f, f.Admin, AiProfileKind.Chat);
        Assert.Equal((true, (string?)null, expected), (tested.Ok, tested.Code, tested.Warning));

        fake.SetModelListed(AiProfileKind.Chat, true);
        await runner.RunOnceAsync(now.AddSeconds(120), default);
        Assert.Equal(("Available", (string?)null), ((await Chat()).State, (await Chat()).Warning));
        Assert.Null((await Test(f, f.Admin, AiProfileKind.Chat)).Warning);
    }

    private static async Task<AiTestResult> Test(AiSettingsFixture f, UserAccount actor, string kind)
    {
        f.Host.Actor.Current = actor;
        using var scope = f.Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestAiProfileRunner>().TestAsync(kind, default);
    }
}
