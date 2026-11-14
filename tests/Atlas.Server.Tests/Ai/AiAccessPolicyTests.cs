using Atlas.Server.Features.Ai.Access;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.Assistant.Commands;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.Assistant;
using Atlas.Server.Tests.Hub;
using Xunit;

namespace Atlas.Server.Tests.Ai;

public sealed class AiAccessPolicyTests
{
    [PostgreSqlFact]
    public async Task An_account_without_an_AI_grant_is_rejected_before_any_provider_call()
    {
        await using var f = await AnswerFlowFixture.CreateAsync();
        var unknown = await f.Index.Host.SeedUserAsync("no-ai-grant");
        await f.Sources.Search.FindAsync(f.Reader, "Jak obnovit data?"); // Fill the shared cache as an allowed account.
        var calls = f.Fake.Calls.Count;
        Assert.False((await f.Sources.Search.FindAsync(unknown, "Jak obnovit data?")).Semantic);
        await AnswerFlowFixture.FailsAsync(503, AiGate.UnavailableCode, () => f.StartAsync(unknown, "Jak obnovit data?"));
        var ungrantedAdmin = await f.Index.Host.SeedUserAsync("ungranted-admin", isWikiAdmin: true);
        await AnswerFlowFixture.FailsAsync(503, AiGate.UnavailableCode, () => f.StartAsync(ungrantedAdmin, "Jak obnovit data?"));
        Assert.Equal(calls, f.Fake.Calls.Count);
        Assert.Equal(0, await f.CountAsync("""SELECT count(*) FROM "AiConversation" """));
    }

    [PostgreSqlFact]
    [Trait("Category", "Smoke")]
    public async Task Group_access_is_overridden_by_personal_denial_before_completion_and_at_release()
    {
        await using var f = await AnswerFlowFixture.CreateAsync(AnswerFlowTests.Meaning);
        var user = await f.Index.Host.SeedUserAsync("pilot");
        await f.Index.Host.SeedAsync(s => s.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.Index.SpaceId, UserId = user.Id, Role = 1 }));
        var group = await f.Index.Host.SeedGroupAsync("AI pilot", user);
        var access = await f.Index.As<AiAccessView>(f.Admin, new GetAiAccess());
        await f.Index.As<AiAccessView>(f.Admin, new SaveAiAccess(new([new(group, null, null, null)], access.Version)));
        await f.PublishAsync("Návod pro správce", "## Postup obnovy\nSpusťte nástroj.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        var prepared = await f.StartAsync(user, "Jak obnovit data?");
        var personal = await f.Index.As<UserAiPolicyView>(f.Admin, new SaveUserAiPolicy(user.Id, new(false, null, null, null, null)));
        var calls = f.ChatCalls.Count;
        Assert.Equal(AiMessageState.Withheld, (await f.CompleteAsync(user, prepared.MessageId)).State);
        Assert.Equal(calls, f.ChatCalls.Count);

        personal = await f.Index.As<UserAiPolicyView>(f.Admin, new SaveUserAiPolicy(user.Id, new(null, null, null, null, personal.Version)));
        prepared = await f.StartAsync(user, "Jak obnovit data?");
        f.Fake.ChatAnswer = FakeAi.Answers.Citing(1);
        var hold = f.Fake.Hold(Atlas.Server.Features.Ai.Settings.Domain.AiProfileKind.Chat);
        var completion = f.CompleteAsync(user, prepared.MessageId);
        await hold.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await f.Index.As<UserAiPolicyView>(f.Admin, new SaveUserAiPolicy(user.Id, new(false, null, null, null, personal.Version)));
        }
        finally
        {
            f.Index.Host.Actor.Current = user;
            hold.Release();
        }
        Assert.Equal(AiMessageState.Withheld, (await completion).State);
        Assert.Null((await f.StoredAsync(prepared.MessageId)).Message.Answer);
    }

    [PostgreSqlFact]
    public async Task Quotas_resolve_inherited_unlimited_and_personal_overrides_without_resetting_reservations()
    {
        await using var f = await AnswerFlowFixture.CreateAsync(AnswerFlowTests.Meaning);
        var user = await f.Index.Host.SeedUserAsync("pilot");
        var first = await f.Index.Host.SeedGroupAsync("Pilot", user);
        var second = await f.Index.Host.SeedGroupAsync("Unlimited", user);
        var features = await f.Index.As<AiFeaturesView>(f.Admin, new GetAiFeatures());
        await f.Index.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new(AiTaskCatalog.SearchAnswer, true, null, 2)], features.Version)));
        var access = await f.Index.As<AiAccessView>(f.Admin, new GetAiAccess());
        access = await f.Index.As<AiAccessView>(f.Admin, new SaveAiAccess(new([new(first, null, null, null)], access.Version)));
        var inherited = await f.Index.As<UserAiPolicyView>(f.Admin, new GetUserAiPolicy(user.Id));
        Assert.True(inherited.Resolved.Enabled);
        var inheritedLimit = inherited.Resolved.Limits.Single(x => x.Task == AiTaskCatalog.SearchAnswer);
        Assert.Equal((2, "default", first), (inheritedLimit.Effective, inheritedLimit.Source, inheritedLimit.GroupId));
        access = await f.Index.As<AiAccessView>(f.Admin, new SaveAiAccess(new([new(first, null, null, null), new(second, 0, null, null)], access.Version)));
        var unlimited = await f.Index.As<UserAiPolicyView>(f.Admin, new GetUserAiPolicy(user.Id));
        var winner = unlimited.Resolved.Limits.Single(x => x.Task == AiTaskCatalog.SearchAnswer);
        Assert.Equal((null, "unlimited", second), (winner.Effective, winner.Source, winner.GroupId));
        await f.Index.Host.SeedAsync(s => s.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.Index.SpaceId, UserId = user.Id, Role = 1 }));
        await f.PublishAsync("Návod pro správce", "## Postup obnovy\nSpusťte nástroj.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        Assert.Equal(AiMessageState.Preparing, (await f.StartAsync(user, "Jak obnovit data?")).State);
        var custom = await f.Index.As<UserAiPolicyView>(f.Admin, new SaveUserAiPolicy(user.Id, new(null, 1, null, null, null)));
        winner = custom.Resolved.Limits.Single(x => x.Task == AiTaskCatalog.SearchAnswer);
        Assert.Equal((1, 1, 0, "user"), (winner.Effective, winner.Used, winner.Remaining, winner.Origin));
        await AnswerFlowFixture.FailsAsync(429, AiAnswerProblems.Limit, () => f.StartAsync(user, "Jak obnovit data?"));
        await AnswerFlowFixture.FailsAsync(409, null, () => f.Index.As<AiAccessView>(f.Admin, new SaveAiAccess(new([], "stale"))));
        await f.Index.As<AiAccessView>(f.Admin, new SaveAiAccess(new([], access.Version)));
        var denied = await f.Index.As<UserAiPolicyView>(f.Admin, new GetUserAiPolicy(user.Id));
        Assert.False(denied.Resolved.Enabled);
        Assert.Equal(1, denied.Resolved.Limits.Single(x => x.Task == AiTaskCatalog.SearchAnswer).Used);
        Assert.Equal(2, await f.CountAsync("""SELECT count(*) FROM "AccessAuditView" WHERE "Action"='ai.group_policy.changed' AND "GroupId"=@id""", ("id", second)));
    }

    /// <summary>D17/D20: a new grant has tools; a personal "no" beats the groups; grants that all lack tools, or the AI
    /// switched off, leave none; AI enabled personally only has tools by default. An assistant.agent turn without the right is 403 before anything is stored, while the
    /// same user's ordinary answer still starts.</summary>
    [PostgreSqlFact]
    [Trait("Category", "Smoke")]
    public async Task Tools_right_resolves_from_grants_and_personal_exceptions_and_gates_agent_turns()
    {
        await using var f = await AnswerFlowFixture.CreateAsync(AnswerFlowTests.Meaning);
        var user = await f.Index.Host.SeedUserAsync("agent-pilot");
        var group = await f.Index.Host.SeedGroupAsync("Agent pilot", user);
        var access = await f.Index.As<AiAccessView>(f.Admin, new GetAiAccess());
        access = await f.Index.As<AiAccessView>(f.Admin, new SaveAiAccess(new([new(group, null, null, null)], access.Version)));
        Assert.True(access.Groups.Single().ToolsEnabled);
        Assert.True(await ToolsAsync());

        var personal = await f.Index.As<UserAiPolicyView>(f.Admin, new SaveUserAiPolicy(user.Id, new(null, null, null, null, null, ToolsEnabled: false, AgentDailyLimit: 3)));
        Assert.Equal((false, 3), (personal.ToolsEnabled, personal.AgentDailyLimit));
        Assert.False(personal.Resolved.ToolsEnabled);
        Assert.True(personal.Resolved.Enabled);
        Assert.Equal(3, personal.Resolved.Limits.Single(x => x.Task == AiTaskCatalog.AssistantAgent).Effective);
        await AnswerFlowFixture.FailsAsync(403, AiGate.ToolsForbiddenCode, () => f.Index.As<AiTurnPrepared>(user, new PrepareAiTurn(null,
            AiConversationOrigin.Assistant, AiTaskCatalog.AssistantAgent, "Jak obnovit data?", new AiScope("wiki", null, null), null,
            Guid.NewGuid(), "compat", "model", null)));
        await f.Index.Host.SeedAsync(s => s.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.Index.SpaceId, UserId = user.Id, Role = 1 }));
        await f.PublishAsync("Návod pro správce", "## Postup obnovy\nSpusťte nástroj.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        Assert.Equal(AiMessageState.Preparing, (await f.StartAsync(user, "Jak obnovit data?")).State);

        await f.Index.As<UserAiPolicyView>(f.Admin, new SaveUserAiPolicy(user.Id, new(null, null, null, null, personal.Version)));
        await f.Index.As<AiAccessView>(f.Admin, new SaveAiAccess(new([new(group, null, null, null, ToolsEnabled: false)], access.Version)));
        Assert.False(await ToolsAsync());
        Assert.True((await f.Index.As<UserAiPolicyView>(f.Admin, new GetUserAiPolicy(user.Id))).Resolved.Enabled);

        access = await f.Index.As<AiAccessView>(f.Admin, new GetAiAccess());
        await f.Index.As<AiAccessView>(f.Admin, new SaveAiAccess(new([new(group, null, null, null)], access.Version)));
        var current = await f.Index.As<AiSwitchView>(f.Admin, new GetAiSwitch());
        await f.Index.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new(false, current.Version)));
        Assert.False(await ToolsAsync());

        // D20: AI enabled personally only (no enabled grant) defaults to tools on; an explicit personal "no" still wins.
        var solo = await f.Index.Host.SeedUserAsync("solo-pilot");
        var soloPolicy = await f.Index.As<UserAiPolicyView>(f.Admin, new SaveUserAiPolicy(solo.Id, new(true, null, null, null, null)));
        Assert.False(soloPolicy.Resolved.ToolsEnabled); // the global switch is still off here
        await f.Index.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new(true, (await f.Index.As<AiSwitchView>(f.Admin, new GetAiSwitch())).Version)));
        Assert.True((await f.Index.As<UserAiPolicyView>(f.Admin, new GetUserAiPolicy(solo.Id))).Resolved.ToolsEnabled);
        await f.Index.As<UserAiPolicyView>(f.Admin, new SaveUserAiPolicy(solo.Id, new(true, null, null, null, soloPolicy.Version, ToolsEnabled: false)));
        Assert.False((await f.Index.As<UserAiPolicyView>(f.Admin, new GetUserAiPolicy(solo.Id))).Resolved.ToolsEnabled);

        async Task<bool> ToolsAsync() => (await f.Index.As<UserAiPolicyView>(f.Admin, new GetUserAiPolicy(user.Id))).Resolved.ToolsEnabled;
    }

    [PostgreSqlFact]
    public async Task Expired_Hub_membership_cannot_keep_an_AI_grant()
    {
        await using var f = await AnswerFlowFixture.CreateAsync();
        var (user, group, identity) = await HubEffectiveAuthorityTests.SeedAsync(f.Index.Host, false);
        var access = await f.Index.As<AiAccessView>(f.Admin, new GetAiAccess());
        await f.Index.As<AiAccessView>(f.Admin, new SaveAiAccess(new([new(group, null, null, null)], access.Version)));
        Assert.True((await f.Index.As<UserAiPolicyView>(f.Admin, new GetUserAiPolicy(user.Id))).Resolved.Enabled);
        await HubEffectiveAuthorityTests.ExpireAsync(f.Index.Host, identity);
        var calls = f.Fake.Calls.Count;
        await AnswerFlowFixture.FailsAsync(503, AiGate.UnavailableCode, () => f.StartAsync(user, "Jak obnovit data?"));
        Assert.Equal(calls, f.Fake.Calls.Count);
    }
}