using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.AiSuggestions.Contracts;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Tests.AiIndex;
using Xunit;

namespace Atlas.Server.Tests.AiSuggestions;

/// <summary>P3 (design §7.8): a template of the space for the new title, by the dictionary without AI and by title meaning with it;
/// only templates the actor may use, only where the actor may create.</summary>
public sealed class TemplateSuggestionTests
{
    [PostgreSqlFact]
    public async Task Suggests_a_usable_template_by_dictionary_without_ai_and_by_meaning_with_it()
    {
        await using var f = await SuggestionFixture.CreateAsync(text =>
            text.Contains("Hlášení incidentu", StringComparison.Ordinal) || text.Contains("Výpadek serveru", StringComparison.Ordinal) ? "koncept incident vypadek" : text);
        var procedure = await f.PublishAsync("Provozní postup");
        await f.MakeTemplateAsync(procedure.Id);
        var contacts = await f.PublishAsync("Kontakty týmu");
        await f.MakeTemplateAsync(contacts.Id);
        // An unpublished template is its owner's (and WikiAdmins') only, as in the template list.
        var runbook = await f.Index.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Runbook incidentu", IndexFixture.Rich("x"), "x", IsTemplate: true), f.Admin, default);
        var incident = await f.PublishAsync("Hlášení incidentu");
        await f.MakeTemplateAsync(incident.Id);
        Task<TemplateSuggestion?> Suggest(UserAccount actor, string title, Guid? spaceId = null)
            => f.AsAsync(actor, flow => flow.TemplateAsync(title, spaceId ?? f.SpaceId, default));

        // Without any AI: the dictionary, in Czech forms too.
        Assert.Equal(new TemplateSuggestion(procedure.Id, "Provozní postup", "dictionary"), await Suggest(f.Writer, "Postup obnovy databáze"));
        Assert.Equal(new TemplateSuggestion(contacts.Id, "Kontakty týmu", "dictionary"), await Suggest(f.Writer, "Seznam kontaktů"));
        Assert.Null(await Suggest(f.Writer, "Runbook pro výpadek"));
        Assert.Equal(new TemplateSuggestion(runbook.Id, "Runbook incidentu", "dictionary"), await Suggest(f.Admin, "Runbook pro výpadek"));
        Assert.Null(await Suggest(f.Writer, "Výpadek serveru"));
        // Only where the actor may create: a reader of the space gets nothing.
        Assert.Null(await Suggest(f.Reader, "Postup obnovy databáze"));
        Assert.Empty(f.Fake.Calls);

        // With AI: the meaning of the template's title, after the dictionary.
        await f.ActivateAsync();
        Assert.Equal(new TemplateSuggestion(incident.Id, "Hlášení incidentu", "meaning"), await Suggest(f.Writer, "Výpadek serveru"));
        Assert.Equal(["query: Výpadek serveru"], f.Sent(AiTaskCatalog.PageCreateSuggest));
        Assert.Equal("dictionary", (await Suggest(f.Writer, "Postup při výpadku serveru"))!.MatchedBy);
        Assert.Null(await Suggest(f.Writer, "Výpadek serveru", f.OtherSpaceId));
        Assert.Null(await Suggest(f.Reader, "Výpadek serveru"));

        // The vector part follows the capability; the dictionary does not.
        await f.SetTaskAsync(AiTaskCatalog.PageCreateSuggest, false);
        Assert.Null(await Suggest(f.Writer, "Výpadek serveru"));
        Assert.Equal(procedure.Id, (await Suggest(f.Writer, "Postup obnovy databáze"))!.TemplateId);
    }
}
