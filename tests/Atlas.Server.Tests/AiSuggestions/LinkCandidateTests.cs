using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.AiSuggestions.Contracts;
using Atlas.Server.Features.Auth.Commands;
using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.AiSuggestions;

/// <summary>E2 (plan phase 2 §13, design §7.5 A): phrases of a finished paragraph that could link to a page. Exact titles and
/// aliases always (with assist), the meaning of multi-word phrases only with the <c>editorLinks</c> capability; the working text
/// reaches the model only when it may.</summary>
public sealed class LinkCandidateTests
{
    private static string Meaning(string text)
        => text.Contains("vzdálený přístup do sítě", StringComparison.Ordinal) || text.Contains("Vzdálená síť pro zaměstnance", StringComparison.Ordinal)
            ? "koncept vzdalena firemni sit"
            : text;

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Exact_titles_and_aliases_are_offered_for_single_words_and_meaning_only_for_phrases()
    {
        await using var f = await SuggestionFixture.CreateAsync(Meaning);
        var vpn = await f.PublishAsync("VPN");
        var remote = await f.PublishAsync("Vzdálená síť pro zaměstnance");
        var build = await f.PublishAsync("Jenkins");
        await f.Index.EditAndPublishAsync(build.Id, "Sestavovací server", "Obsah stránky.");
        await f.PublishAsync("Návod");
        var editing = await f.DraftAsync("Poznámky k síti");
        await f.ActivateAsync();
        string[] phrases = ["VPN", "vzdálený přístup do sítě", "návod", "Jenkins", "Sestavovací server", "server"];

        var result = await f.AsAsync(f.Writer, flow => flow.LinkCandidatesAsync(new(editing.Id, phrases), default));

        Assert.Equal(
        [
            new LinkCandidate("VPN", vpn.Id, f.SpaceId, "Engineering", "VPN", "title"),
            new LinkCandidate("Jenkins", build.Id, f.SpaceId, "Engineering", "Sestavovací server", "alias"),
            new LinkCandidate("Sestavovací server", build.Id, f.SpaceId, "Engineering", "Sestavovací server", "title"),
            new LinkCandidate("vzdálený přístup do sítě", remote.Id, f.SpaceId, "Engineering", "Vzdálená síť pro zaměstnance", "meaning"),
        ], result.Items);
        // One call, and only the multi-word phrase without an exact match left Atlas.
        Assert.Equal(["query: vzdálený přístup do sítě"], f.Sent(AiTaskCatalog.EditorLink));
        Assert.Single(f.Fake.Calls, x => x.Task == AiTaskCatalog.EditorLink);

        // Without the capability: the same exact matches, no meaning and no model call.
        await f.SetTaskAsync(AiTaskCatalog.EditorLink, false);
        var lexical = await f.AsAsync(f.Writer, flow => flow.LinkCandidatesAsync(new(editing.Id, ["VPN", "vzdálený přístup do Wi-Fi sítě"]), default));
        Assert.Equal([new LinkCandidate("VPN", vpn.Id, f.SpaceId, "Engineering", "VPN", "title")], lexical.Items);
        Assert.Single(f.Fake.Calls, x => x.Task == AiTaskCatalog.EditorLink);
    }

    [PostgreSqlFact]
    public async Task The_current_page_drafts_and_unreadable_pages_are_never_offered()
    {
        // Hidden pages mean exactly what the phrase means; the visible one only nearly, so a filter after the scan would lose it.
        await using var f = await SuggestionFixture.CreateAsync(text =>
            text.Contains("Tunel viditelný", StringComparison.Ordinal) ? FakeAi.Near("koncept tunel firma")
            : text.Contains("bezpečný tunel do firmy", StringComparison.Ordinal) || text.Contains("Tunel ", StringComparison.Ordinal) ? "koncept tunel firma"
            : text);
        var editing = await f.PublishAsync("Monitoring");
        var other = await f.PublishAsync("Monitoring", spaceId: f.OtherSpaceId);
        await f.DraftAsync("Proxy");
        var firewall = await f.PublishAsync("Firewall", spaceId: f.RestrictedSpaceId, author: f.Admin);
        var certificates = await f.PublishAsync("Certifikáty", author: f.Admin);
        var template = await f.PublishAsync("Incident");
        var trashed = await f.PublishAsync("Zálohy");
        var restrictedTunnel = await f.PublishAsync("Tunel A", spaceId: f.RestrictedSpaceId, author: f.Admin);
        await f.PublishAsync("Tunel B", spaceId: f.ExcludedSpaceId);
        var templateTunnel = await f.PublishAsync("Tunel C");
        var trashedTunnel = await f.PublishAsync("Tunel D");
        var draftTunnel = await f.DraftAsync("Tunel E");
        var visibleTunnel = await f.PublishAsync("Tunel viditelný");
        var generation = await f.ActivateAsync();
        // Changes the worker has not seen yet: the reads alone must hide them.
        await f.RestrictAsync(certificates.Id);
        await f.MakeTemplateAsync(template.Id);
        await f.MakeTemplateAsync(templateTunnel.Id);
        await f.TrashAsync(trashed.Id);
        await f.TrashAsync(trashedTunnel.Id);
        await f.ExcludeAsync(f.ExcludedSpaceId);
        await f.IndexDraftAsync(generation.Id, draftTunnel.Id, "Tunel E");
        string[] phrases = ["Monitoring", "Proxy", "Firewall", "Certifikáty", "Incident", "Zálohy", "bezpečný tunel do firmy"];

        var writer = await f.AsAsync(f.Writer, flow => flow.LinkCandidatesAsync(new(editing.Id, phrases), default));

        Assert.Equal(
        [
            new LinkCandidate("Monitoring", other.Id, f.OtherSpaceId, "Archive", "Monitoring", "title"),
            new LinkCandidate("bezpečný tunel do firmy", visibleTunnel.Id, f.SpaceId, "Engineering", "Tunel viditelný", "meaning"),
        ], writer.Items);

        // A WikiAdmin reads the restricted pages; drafts, templates, the trash and excluded spaces stay out for everyone.
        var admin = await f.AsAsync(f.Admin, flow => flow.LinkCandidatesAsync(new(editing.Id, phrases), default));
        Assert.Equal(new[] { other.Id, firewall.Id, certificates.Id }.Order(), admin.Items.Where(x => x.MatchedBy == "title").Select(x => x.PageId).Order());
        Assert.Equal(restrictedTunnel.Id, Assert.Single(admin.Items, x => x.MatchedBy == "meaning").PageId);

        // Only an editor of the page with assist on: anyone else meets a missing page.
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.AsAsync(f.Reader, flow => flow.LinkCandidatesAsync(new(editing.Id, phrases), default)))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.AsAsync(f.Writer, flow =>
            flow.LinkCandidatesAsync(new(editing.Id, Enumerable.Repeat("VPN", 41).ToArray()), default)))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.AsAsync(f.Writer, flow =>
            flow.LinkCandidatesAsync(new(editing.Id, [new string('x', 81)]), default)))).Status);

        // A page in a space excluded from AI keeps its exact matches, and its text never reaches the model.
        var excludedPage = await f.PublishAsync("Poznámky", spaceId: f.ExcludedSpaceId);
        var sent = f.SentWorkingText.Count;
        var excluded = await f.AsAsync(f.Writer, flow => flow.LinkCandidatesAsync(new(excludedPage.Id, ["Monitoring", "šifrované spojení do kanceláře"]), default));
        Assert.Equal(new[] { editing.Id, other.Id }.Order(), excluded.Items.Select(x => x.PageId).Order());
        Assert.Equal(sent, f.SentWorkingText.Count);

        await f.Index.As<AiPreferencesView>(f.Writer, new SaveAiPreferences(new SaveAiPreferencesRequest("off", [], null)));
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.AsAsync(f.Writer, flow => flow.LinkCandidatesAsync(new(editing.Id, phrases), default)))).Status);
    }

    [PostgreSqlFact]
    public async Task Working_text_reaches_a_cloud_embedding_profile_only_with_permission()
    {
        await using var f = await SuggestionFixture.CreateAsync(Meaning);
        var vpn = await f.PublishAsync("VPN");
        var remote = await f.PublishAsync("Vzdálená síť pro zaměstnance");
        var editing = await f.DraftAsync("Poznámky k síti");
        await f.ActivateAsync();
        await f.MoveProfileToCloudAsync();
        var request = new LinkCandidatesRequest(editing.Id, ["VPN", "vzdálený přístup do sítě"]);

        var refused = await f.AsAsync(f.Writer, flow => flow.LinkCandidatesAsync(request, default));

        Assert.Equal([new LinkCandidate("VPN", vpn.Id, f.SpaceId, "Engineering", "VPN", "title")], refused.Items);
        Assert.Empty(f.SentWorkingText);
        // E4 has no rule-based part: unavailable. Published content and search queries are not working text and still run.
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => f.AsAsync(f.Writer, flow =>
            flow.PageOrderAsync(new(editing.Id, "vzdálený přístup do sítě", [vpn.Id]), default)))).Status);
        Assert.Empty(f.SentWorkingText);

        await f.SetTaskAsync(AiTaskCatalog.EditorLink, true, cloudWorkingText: true);
        var allowed = await f.AsAsync(f.Writer, flow => flow.LinkCandidatesAsync(request, default));

        Assert.Equal(remote.Id, Assert.Single(allowed.Items, x => x.MatchedBy == "meaning").PageId);
        Assert.Equal(["query: vzdálený přístup do sítě"], f.SentWorkingText);
    }
}
