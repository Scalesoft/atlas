using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.KnowledgeMaintenance;
using Atlas.Server.Tests.Ai;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.KnowledgeMaintenance;

public sealed class AiEvaluationTests
{
    [PostgreSqlFact]
    public async Task Saved_profile_and_admin_are_required_and_invalid_citations_never_pass()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var fake = f.Host.Services.GetRequiredService<FakeAiClientFactory>();
        var saved = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, AiSettingsFixture.Chat("fixture")));
        await AiSettingsFixture.Denied(403, () => Run(f, f.SpaceAdmin));
        Assert.Empty(fake.Calls);
        fake.ChatAnswer = FakeAi.Answers.Citing(999);
        var result = await Run(f, f.Admin);
        Assert.Equal("answer-diagnostic@1", result.SetVersion);
        Assert.Equal(6, result.Items.Count);
        Assert.All(result.Items, item => Assert.False(item.Passed));
        Assert.All(fake.Calls, call => Assert.Equal(AiTaskCatalog.AdminEval, call.Task));
        var hold = fake.Hold(AiProfileKind.Chat);
        var running = Run(f, f.Admin);
        await hold.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, AiSettingsFixture.Chat("fixture") with { ExpectedVersion = saved.Version, TimeoutSeconds = 45 }));
        hold.Release();
        await AiSettingsFixture.Denied(409, () => running);
    }

    [PostgreSqlFact]
    public async Task Failure_exposes_only_stable_codes_and_abstention_is_measured_separately()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, AiSettingsFixture.Chat("fixture")));
        var fake = f.Host.Services.GetRequiredService<FakeAiClientFactory>();
        fake.ChatAnswer = FakeAi.Answers.Unanswerable;
        var result = await Run(f, f.Admin);
        Assert.Equal(2, result.Items.Count(x => x.Passed));
        fake.SetMode(AiProfileKind.Chat, FakeAiMode.Error(AiOutcome.Connection, "PRIVATE-UPSTREAM"));
        var failed = await Run(f, f.Admin);
        Assert.All(failed.Items, x => Assert.Equal(AiOutcome.Connection, x.Code));
        Assert.DoesNotContain("PRIVATE-UPSTREAM", System.Text.Json.JsonSerializer.Serialize(failed));
    }

    private static async Task<AiEvaluationReport> Run(AiSettingsFixture f, UserAccount actor)
    {
        f.Host.Actor.Current = actor;
        using var scope = f.Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AiEvaluationRunner>().RunAsync(default);
    }
}
