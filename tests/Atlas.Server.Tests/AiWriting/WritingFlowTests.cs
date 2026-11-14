using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Administration.Commands;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Queries;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.AiWriting;
using Atlas.Server.Tests.Assistant;
using Xunit;

namespace Atlas.Server.Tests.AiWriting;

public sealed class WritingFlowTests
{
    /// <summary>
    /// The read locks the account before it knows whether the page exists, so a deactivated account is answered 401 for an
    /// unknown page id too, where an active one gets the 404. PageGate does not lock the account for an unknown id: the
    /// handler keeps this answer itself.
    /// </summary>
    [PostgreSqlFact]
    public async Task A_deactivated_account_is_answered_401_also_for_an_unknown_page()
    {
        await using var host = await Atlas.Server.Tests.Platform.AtlasTestHost.CreateAsync("atlas_writing_unknown_");
        var writer = await host.SeedUserAsync("writer");
        host.Actor.Current = writer;
        var unknown = new Atlas.Server.Features.AiWriting.Commands.WritingRead(Guid.NewGuid(), "editor.outline", "Topic");

        ApiException missing;
        IReadOnlyList<string> sent;
        using (var recording = Atlas.Server.Tests.Platform.SqlRecording.Start())
        {
            missing = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<Atlas.Server.Features.AiWriting.Commands.WritingContext>(unknown));
            sent = recording.Statements;
        }
        await host.ExecuteAsync("UPDATE \"UserAccount\" SET \"IsActive\"=false WHERE \"Id\"=:id", writer.Id);
        var deactivated = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<Atlas.Server.Features.AiWriting.Commands.WritingContext>(unknown));

        Assert.Equal((404, "Stránka nebyla nalezena."), (missing.Status, missing.Message));
        Assert.Equal((401, "Přihlášení vypršelo."), (deactivated.Status, deactivated.Message));
        // The content lock, the lookup that finds no page, then the account, which the handler locks itself: these four
        // statements and no row of a space or a page.
        Assert.Equal(["advisory shared", "read Page.SpaceId", "UserAccount FOR SHARE", "set actor"], Atlas.Server.Tests.PageGate.LockTrace.Of(sent));
        Assert.Equal(4, sent.Count);
    }

    [Fact]
    public async Task Transform_revalidates_the_account_after_model_without_holding_database_locks()
    {
        await using var f = await AnswerFlowFixture.CreateAsync();
        await f.Index.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Quick, AnswerFlowFixture.ChatProfile()));
        await EnableWritingAsync(f);
        await f.Index.ProbeAsync();
        var page = await f.PublishAsync("Návod", "Krátký návod.");
        f.Fake.ChatAnswer = _ => """{"text":"Opravený návod."}""";
        var hold = f.Fake.Hold(AiProfileKind.Quick);
        f.Index.Host.Actor.Current = f.Writer;
        using var scope = f.Index.Host.Services.CreateScope();
        var pending = scope.ServiceProvider.GetRequiredService<AiWritingRunner>().TransformAsync(new(page, "Krátký návod.", "proofread"), default);
        await hold.Entered;
        await f.ExecuteAsync("""UPDATE "UserAccount" SET "SecurityStamp"='revoked' WHERE "Id"=@id""", ("id", f.Writer.Id));
        hold.Release();
        await AnswerFlowFixture.FailsAsync(401, null, () => pending);
    }


    [Fact]
    public async Task Inline_suggestions_past_their_budget_give_none_and_leave_the_profile_available()
    {
        await using var f = await AnswerFlowFixture.CreateAsync();
        await f.Index.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Quick, AnswerFlowFixture.ChatProfile()));
        await EnableWritingAsync(f);
        await f.Index.ProbeAsync();
        var page = await f.PublishAsync("Návod", "Krátký návod.");
        await f.Index.As<Atlas.Server.Features.Auth.Contracts.AiPreferencesView>(f.Writer,
            new Atlas.Server.Features.Auth.Commands.SaveAiPreferences(new("on", [], null)));
        f.Fake.ChatAnswer = _ => """{"suggestions":[]}""";
        f.Index.Host.Actor.Current = f.Writer;
        using var scope = f.Index.Host.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<AiWritingRunner>();
        // AI4-7: two expiries in a row used to open the breaker and take every chat feature away for a probe round.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var hold = f.Fake.Hold(AiProfileKind.Quick);
            var pending = runner.IntentsAsync(new(page, "Pavel Novák vám pomůže s instalací nového systému."), default);
            await hold.Entered.WaitAsync(TimeSpan.FromSeconds(30));
            f.Index.Clock.Advance(Atlas.Server.Features.Ai.Settings.AiTaskCatalog.EditorIntentBudget);
            Assert.Empty((await pending).Suggestions);
            hold.Release();
        }
        Assert.True(f.Index.Host.Services.GetRequiredService<Atlas.Server.Features.Ai.Availability.AiAvailability>().IsAvailable(AiProfileKind.Quick));
    }

    [Fact]
    public async Task Transient_writing_does_not_reserve_a_conversation_and_keeps_provenance()
    {
        await using var f = await AnswerFlowFixture.CreateAsync();
        await f.Index.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Quick, AnswerFlowFixture.ChatProfile()));
        await EnableWritingAsync(f);
        await f.Index.ProbeAsync();
        var page = await f.PublishAsync("Návod", "Krátký návod.");
        f.Fake.ChatAnswer = _ => """{"text":"Opravený návod."}""";
        f.Index.Host.Actor.Current = f.Writer;
        using var scope = f.Index.Host.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AiWritingRunner>().TransformAsync(new(page, "Krátký návod.", "proofread"), default);
        Assert.Equal("Opravený návod.", result.Text);
        Assert.Equal("company", result.Provenance.Location);
        Assert.Equal(0, await f.CountAsync("""SELECT count(*) FROM "AiMessage" """));
    }

    [Fact]
    public async Task Cloud_working_text_is_denied_on_the_actual_quick_profile_without_a_provider_call()
    {
        await using var f = await AnswerFlowFixture.CreateAsync();
        await f.Index.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Quick,
            AnswerFlowFixture.ChatProfile() with { Location = "cloud", BaseUrl = "https://api.openai.com/v1", ApiKey = "test-key" }));
        await EnableWritingAsync(f);
        await f.Index.ProbeAsync();
        var page = await f.PublishAsync("Návod", "Krátký návod.");
        var before = f.Fake.Calls.Count;
        f.Index.Host.Actor.Current = f.Writer;
        using var scope = f.Index.Host.Services.CreateScope();
        await AnswerFlowFixture.FailsAsync(503, "ai_unavailable", () => scope.ServiceProvider.GetRequiredService<AiWritingRunner>()
            .TransformAsync(new(page, "Krátký návod.", "proofread"), default));
        Assert.Equal(before, f.Fake.Calls.Count);
    }

    [Fact]
    public async Task Proposal_for_an_older_page_version_is_rejected_after_model()
    {
        await using var f = await AnswerFlowFixture.CreateAsync();
        await f.Index.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Quick, AnswerFlowFixture.ChatProfile()));
        await EnableWritingAsync(f);
        await f.Index.ProbeAsync();
        var page = await f.PublishAsync("Návod", "Krátký návod.");
        f.Fake.ChatAnswer = _ => """{"text":"Opravený návod."}""";
        var hold = f.Fake.Hold(AiProfileKind.Quick);
        f.Index.Host.Actor.Current = f.Writer;
        using var scope = f.Index.Host.Services.CreateScope();
        var pending = scope.ServiceProvider.GetRequiredService<AiWritingRunner>().TransformAsync(new(page, "Krátký návod.", "proofread"), default);
        await hold.Entered;
        await f.ExecuteAsync("""UPDATE "Page" SET "Version"="Version"+1 WHERE "Id"=@id""", ("id", page));
        hold.Release();
        await AnswerFlowFixture.FailsAsync(409, "version_conflict", () => pending);
    }

    [Fact]
    public async Task Tag_suggestions_resolve_only_existing_readable_space_tags_and_never_save_them()
    {
        await using var f = await AnswerFlowFixture.CreateAsync();
        await f.Index.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Quick, AnswerFlowFixture.ChatProfile()));
        await EnableWritingAsync(f);
        await f.Index.ProbeAsync();
        var source = await f.Index.PublishNewAsync("Zdroj", "Původní text.");
        var tags = await f.Index.As<Atlas.Server.Features.PageTags.PageTagState>(f.Writer,
            new Atlas.Server.Features.PageTags.SetPageTags(source.Id, new(["Postup", "Provoz"], [])));
        var target = await f.Index.PublishNewAsync("Cíl", "Pracovní text.");
        var draft = await f.Index.SaveDraftAsync(target.Id, "Cíl", "Nový pracovní postup.");
        var allowed = tags.Tags[0];
        var unknown = Guid.NewGuid();
        f.Fake.ChatAnswer = _ => System.Text.Json.JsonSerializer.Serialize(new { tagIds = new[] { allowed.Id, unknown, allowed.Id } });
        f.Index.Host.Actor.Current = f.Writer;
        using var scope = f.Index.Host.Services.CreateScope();
        var proposal = await scope.ServiceProvider.GetRequiredService<AiWritingRunner>().TagsAsync(new(target.Id, draft.Version), default);
        Assert.Equal([allowed], proposal.Tags);
        Assert.Equal(0, await f.CountAsync("""SELECT count(*) FROM "PageTag" WHERE "PageId"=@id""", ("id", target.Id)));
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outline_uses_only_the_template_revision_the_actor_may_read(bool canEditTemplate)
    {
        await using var f = await AnswerFlowFixture.CreateAsync();
        var template = await f.Index.PublishNewAsync("Template", "Body", null, ("Published heading", "published"));
        await f.Index.SaveDraftAsync(template.Id, "Template", "Body", ("Unpublished heading", "unpublished"));
        await f.ExecuteAsync("""UPDATE "Page" SET "IsTemplate"=true WHERE "Id"=@id""", ("id", template.Id));
        var target = await f.Index.PublishNewAsync("Target", "Body");
        f.Index.Host.Actor.Current = f.Admin;
        await f.Index.Host.InvokeAsync(new Atlas.Server.Features.PageAccess.Commands.SetPageGrant(target.Id, new(f.Reader.Id, null, "Writer", null)));
        if (canEditTemplate)
        {
            await f.Index.Host.InvokeAsync(new SetSpaceGrant(f.Index.SpaceId, new(f.Admin.Id, null, "Admin", null)));
            var grant = (await f.Index.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(f.Index.SpaceId))).Grants.Single(x => x.UserId == f.Reader.Id);
            await f.Index.Host.InvokeAsync(new SetSpaceGrant(f.Index.SpaceId, new(f.Reader.Id, null, "Admin", grant.Version)));
        }

        var visibleTemplate = await f.Index.As<Atlas.Server.Features.Pages.Contracts.PageDto>(f.Reader,
            new Atlas.Server.Features.Pages.Queries.GetPage(template.Id));
        Assert.Equal(canEditTemplate, visibleTemplate.CanEdit);
        var context = await f.Index.As<Atlas.Server.Features.AiWriting.Commands.WritingContext>(f.Reader,
            new Atlas.Server.Features.AiWriting.Commands.WritingRead(target.Id, "editor.outline", "Topic"));

        Assert.Contains(canEditTemplate ? "Unpublished heading" : "Published heading", context.Text);
        Assert.DoesNotContain(canEditTemplate ? "Published heading" : "Unpublished heading", context.Text);
        if (canEditTemplate)
        {
            var grant = (await f.Index.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(f.Index.SpaceId))).Grants.Single(x => x.UserId == f.Reader.Id);
            await f.Index.Host.InvokeAsync(new SetSpaceGrant(f.Index.SpaceId, new(f.Reader.Id, null, "Reader", grant.Version)));
            await AnswerFlowFixture.FailsAsync(409, "version_conflict", () =>
                f.Index.As<Atlas.Server.Features.AiWriting.Commands.WritingContext>(f.Reader,
                    new Atlas.Server.Features.AiWriting.Commands.WritingRead(target.Id, "editor.outline", "Topic", Expected: context)));
        }
    }

    private static async Task EnableWritingAsync(AnswerFlowFixture f)
    {
        var current = await f.Index.As<AiFeaturesView>(f.Admin, new Atlas.Server.Features.Ai.Settings.Queries.GetAiFeatures());
        await f.Index.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new(
            new[] { "editor.intent", "editor.transform", "editor.outline", "publish.check", "publish.change-summary", "page.tag-suggest" }
                .Select(task => new SaveAiFeatureItem(task, true, false)).ToArray(), current.Version)));
    }

    [Fact]
    public async Task Working_preparation_sends_only_safe_changed_blocks_and_template_headings()
    {
        await using var f = await AnswerFlowFixture.CreateAsync();
        await f.Index.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Quick, AnswerFlowFixture.ChatProfile()));
        await EnableWritingAsync(f);
        await f.Index.ProbeAsync();
        var template = await f.Index.PublishNewAsync("Šablona", "TEMPLATE_HIDDEN_BODY", null, ("Instalace", "install"));
        await f.ExecuteAsync("""UPDATE "Page" SET "IsTemplate"=true WHERE "Id"=@id""", ("id", template.Id));
        var target = await f.Index.PublishNewAsync("Cíl", "Původní postup.");
        var content = System.Text.Json.JsonSerializer.Serialize(new { format = "atlas-rich", version = 1,
            document = new { type = "doc", content = new[] {
                new { type = "paragraph", content = new[] { new { type = "text", text = "Nový postup." } } },
                new { type = "paragraph", content = new[] { new { type = "text", text = "heslo: Srv#2026!rack" } } } } } });
        var draft = await f.Index.As<Atlas.Server.Features.Pages.Contracts.PageDto>(f.Writer,
            new Atlas.Server.Features.Pages.Commands.SavePage(target.Id, new("Cíl", content, "Nový postup.\n\nheslo: Srv#2026!rack", target.Version)));
        f.Fake.ChatAnswer = call => call.Task switch
        {
            "editor.outline" => System.Text.Json.JsonSerializer.Serialize(new { text = "## Instalace\n\n> [!TIP]\n> Krátký tip." }),
            "publish.check" => """{"issues":[{"spanStart":0,"spanEnd":4,"original":"Nový","correction":"Novější"}]}""",
            "publish.change-summary" => """{"text":"Změněn postup."}""",
            _ => """{"suggestions":[{"intent":"contact.inline","spanStart":0,"spanEnd":5,"query":"Pavel","confidence":0.8}]}"""
        };
        var preference = await f.Index.As<Atlas.Server.Features.Auth.Contracts.AiPreferencesView>(f.Writer,
            new Atlas.Server.Features.Auth.Commands.SaveAiPreferences(new("rules", [], null)));
        f.Index.Host.Actor.Current = f.Writer;
        using var scope = f.Index.Host.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<AiWritingRunner>();
        const string block = "Pavel Novák vám pomůže s instalací nového systému.";
        await AnswerFlowFixture.FailsAsync(404, null, () => runner.IntentsAsync(new(target.Id, block), default));
        await f.Index.As<Atlas.Server.Features.Auth.Contracts.AiPreferencesView>(f.Writer,
            new Atlas.Server.Features.Auth.Commands.SaveAiPreferences(new("on", [], preference.Version)));
        Assert.Single((await runner.IntentsAsync(new(target.Id, block), default)).Suggestions);
        Assert.StartsWith("## Instalace", (await runner.OutlineAsync(new(target.Id, "Instalace"), default)).Text);
        Assert.Single((await runner.PublishCheckAsync(new(target.Id, draft.Version), default)).Issues);
        Assert.Equal("Změněn postup.", (await runner.ChangeSummaryAsync(new(target.Id, draft.Version), default)).Text);
        var calls = f.Fake.Calls.Where(x => x.Operation == "chat").ToArray();
        Assert.DoesNotContain(calls, x => x.Inputs.Any(t => t.Contains("TEMPLATE_HIDDEN_BODY") || t.Contains("Srv#2026!rack")));
        Assert.Contains(calls.Single(x => x.Task == "editor.outline").Inputs, text => text.Contains("Instalace"));
        Assert.Contains("Původní postup.", System.Text.Json.Nodes.JsonNode.Parse(calls.Single(x => x.Task == "publish.change-summary").Inputs.Last())!["text"]!.GetValue<string>());
    }
}
