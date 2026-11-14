using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.PageHistory.Commands;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.Projector.Domain;
using Atlas.Server.Features.Projector.Persistence;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.Projector;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class PageHistoryTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Restore_appends_working_revision_preserves_publication_and_is_idempotent_for_same_content()
    {
        await using var f = await Fixture.CreateAsync();
        var a = await f.Create("Original");
        await f.Pages.PublishAsync(a.Id, new(1), f.Writer, default);
        await f.Pages.SaveAsync(a.Id, new("Draft", Fixture.Rich("Draft"), "draft", 1), f.Writer, default);
        var history = await f.History.ListAsync(a.Id, f.Writer);
        Assert.Equal(new[] { 2, 1 }, history.Items.Select(x => x.Version));
        var original = history.Items[1];
        Assert.True(original.IsPublished);
        Assert.NotNull(original.LastPublishedAt);
        // A revision stored while clients wrote the projection: the restore writes it again from the JSON.
        await f.Execute("UPDATE \"PageRevision\" SET \"Markdown\"='forged' WHERE \"Id\"=@id", original.Id);
        var restored = await f.History.RestoreAsync(a.Id, new(original.Id, 2), f.Writer);
        Assert.Equal(3, restored.Version);
        Assert.Equal("Original", restored.Title);
        Assert.Equal(Fixture.Rich("Original"), restored.ContentJson);
        Assert.Equal("Original", restored.Markdown);
        // The restored page is exactly the published one: the publication carries over to it (Zahodit změny), nothing waits for Publikovat.
        Assert.Equal(3, restored.PublishedVersion);
        Assert.Equal("Original", (await f.Pages.GetAsync(a.Id, f.Reader, default)).Title);
        var versions = await f.History.ListAsync(a.Id, f.Writer);
        Assert.Equal(new[] { 3, 2, 1 }, versions.Items.Select(x => x.Version));
        Assert.Equal(versions.Items[0].ActiveFrom, versions.Items[1].ActiveTo);
        Assert.Equal(versions.Items[1].ActiveFrom, versions.Items[2].ActiveTo);
        Assert.All(versions.Items, x => Assert.Equal(DateTimeKind.Utc, x.ActiveFrom.Kind));
        Assert.Equal(3, (await f.History.RestoreAsync(a.Id, new(original.Id, 3), f.Writer)).Version);
        Assert.Equal(3, (await f.History.ListAsync(a.Id, f.Writer)).Items.Count);
    }

    /// <summary>„Zahodit změny“: restoring the published revision returns the working version to what readers read and leaves nothing to
    /// publish; the discarded draft stays in the history and restoring it (Zpět) makes the page a draft again. Any other restore, and a
    /// page never published, keep the publication as it was.</summary>
    [PostgreSqlFact]
    public async Task Restoring_the_published_revision_discards_the_draft_and_keeps_it_in_the_history()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("Published");
        await f.Pages.PublishAsync(page.Id, new(1, "První"), f.Writer, default);
        await f.Pages.SaveAsync(page.Id, new("Draft", Fixture.Rich("Draft"), null, 1), f.Writer, default);
        f.Host.Actor.Current = f.Writer;
        var state = await f.Host.InvokeAsync<PagePublicationState>(new GetPagePublication(page.Id));
        Assert.True(state.UnpublishedChanges);

        // The right of editing: a reader of the published page may not, somebody who cannot read the page does not learn it exists.
        await Denied(403, () => f.History.RestoreAsync(page.Id, new(state.PublishedRevisionId!.Value, 2), f.Reader));
        var concept = await f.Create("Concept");
        var only = Assert.Single(await f.Revisions(concept.Id));
        await Denied(404, () => f.History.RestoreAsync(concept.Id, new(only.Id, 1), f.Reader));

        var discarded = await f.History.RestoreAsync(page.Id, new(state.PublishedRevisionId!.Value, 2), f.OtherWriter);
        Assert.Equal((3, 3, "Published", Fixture.Rich("Published")), (discarded.Version, discarded.PublishedVersion, discarded.Title, discarded.ContentJson));
        f.Host.Actor.Current = f.Writer;
        state = await f.Host.InvokeAsync<PagePublicationState>(new GetPagePublication(page.Id));
        // Readers saw no change: the publisher and the summary of the publication stay.
        Assert.Equal(("Writer", false), (state.PublishedBy, state.UnpublishedChanges));
        var revisions = await f.Revisions(page.Id);
        Assert.Equal(new[] { "Published", "Draft", "Published" }, revisions.Select(x => x.Title));
        Assert.Equal(revisions[0].Id, state.PublishedRevisionId);

        // Zpět: the discarded draft comes back as the working version, readers keep the published one.
        var undone = await f.History.RestoreAsync(page.Id, new(revisions[1].Id, 3), f.Writer);
        Assert.Equal((4, 3, "Draft"), (undone.Version, undone.PublishedVersion, undone.Title));
        Assert.Equal("Published", (await f.Pages.GetAsync(page.Id, f.Reader, default)).Title);

        // Never published: there is no published revision to return to, and a restore publishes nothing.
        await f.Pages.SaveAsync(concept.Id, new("Concept 2", Fixture.Rich("Concept 2"), null, 1), f.Writer, default);
        var first = (await f.Revisions(concept.Id))[1];
        var restored = await f.History.RestoreAsync(concept.Id, new(first.Id, 2), f.Writer);
        Assert.Equal((3, (int?)null), (restored.Version, restored.PublishedVersion));
        await Denied(404, () => f.Pages.GetAsync(concept.Id, f.Reader, default));
    }

    /// <summary>A revision an Atlas 2.x install stored (an empty list item; UX cycle 2, IM2) comes back repaired, as the editor shows it.</summary>
    [PostgreSqlFact]
    public async Task Restore_brings_back_an_Atlas_2_revision_repaired()
    {
        await using var f = await Fixture.CreateAsync();
        var a = await f.Create("Original");
        await f.Pages.SaveAsync(a.Id, new("Draft", Fixture.Rich("Draft"), "draft", 1), f.Writer, default);
        var original = (await f.Revisions(a.Id))[1];
        const string legacy = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"bulletList","content":[{"type":"listItem"}]}]}}""";
        await f.Execute("UPDATE \"PageRevision\" SET \"ContentJson\"='" + legacy + "' WHERE \"Id\"=@id", original.Id);
        var restored = await f.History.RestoreAsync(a.Id, new(original.Id, 2), f.Writer);
        Assert.Equal(legacy.Replace("""{"type":"listItem"}""", """{"type":"listItem","content":[{"type":"paragraph"}]}""", StringComparison.Ordinal), restored.ContentJson);
    }

    [PostgreSqlFact]
    public async Task Restore_refuses_over_unsaved_live_edits_of_others_and_supersedes_the_restorers_own()
    {
        await using var f = await Fixture.CreateAsync();
        var a = await f.Create("Original");
        await f.Pages.PublishAsync(a.Id, new(1), f.Writer, default);
        await f.Pages.SaveAsync(a.Id, new("Draft", Fixture.Rich("Draft"), "draft", 1), f.Writer, default);
        var original = (await f.History.ListAsync(a.Id, f.Writer)).Items[1];
        var opened = await f.Collaboration.OpenAsync(a.Id, f.OtherWriter);
        await f.Collaboration.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.OtherWriter.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 1 });

        var refused = await Assert.ThrowsAsync<ApiException>(() => f.History.RestoreAsync(a.Id, new(original.Id, 2), f.Writer));
        Assert.Equal(409, refused.Status);
        Assert.Equal("collaboration_unsaved", refused.Code);
        Assert.Equal(2, (await f.Pages.GetAsync(a.Id, f.Writer, default)).Version);
        Assert.False(await f.Collaboration.HasExternalChangeAsync(opened.DocumentId));

        // The other editor's restore discards only their own unsaved edits (the web confirmed that before restoring).
        Assert.Equal(3, (await f.History.RestoreAsync(a.Id, new(original.Id, 2), f.OtherWriter)).Version);
        Assert.True(await f.Collaboration.HasExternalChangeAsync(opened.DocumentId));
    }

    [PostgreSqlFact]
    public async Task Reader_foreign_revision_private_concept_and_revoked_actor_cannot_access_history()
    {
        await using var f = await Fixture.CreateAsync();
        var a = await f.Create("Private");
        var revision = Assert.Single((await f.History.ListAsync(a.Id, f.Writer)).Items);
        await Denied(404, () => f.History.ListAsync(a.Id, f.Reader));
        await Denied(404, () => f.History.GetRevisionAsync(a.Id, revision.Id, f.Reader));
        await Denied(404, () => f.History.RestoreAsync(a.Id, new(revision.Id, 1), f.Reader));
        await Denied(404, () => f.History.GetRevisionAsync(a.Id, revision.Id, f.OtherWriter));
        await Denied(404, () => f.History.RestoreAsync(a.Id, new(revision.Id, 1), f.OtherWriter));
        Assert.Equal(revision.Id, (await f.History.GetRevisionAsync(a.Id, revision.Id, f.Admin)).Id);
        var b = await f.Create("Other");
        await Denied(404, () => f.History.GetRevisionAsync(b.Id, revision.Id, f.Writer));
        await Denied(404, () => f.History.RestoreAsync(b.Id, new(revision.Id, 1), f.Writer));
        await f.Pages.PublishAsync(a.Id, new(1), f.Writer, default);
        Assert.Equal(revision.Id, (await f.History.GetRevisionAsync(a.Id, revision.Id, f.OtherWriter)).Id);
        await f.Execute("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=@id AND \"ActiveTo\" IS NULL", f.Writer.Id);
        await Denied(404, () => f.History.ListAsync(a.Id, f.Writer));
        await Denied(404, () => f.History.RestoreAsync(a.Id, new(revision.Id, 1), f.Writer));
        await f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='revoked' WHERE \"Id\"=@id", f.Admin.Id);
        await Denied(401, () => f.History.ListAsync(a.Id, f.Admin));
    }

    [PostgreSqlFact]
    public async Task History_shows_the_change_summary_of_each_published_revision_only()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("Summarized");
        await f.Pages.PublishAsync(page.Id, new(1, "A"), f.Writer, default);
        await f.Pages.SaveAsync(page.Id, new("Two", Fixture.Rich("Two"), "two", 1), f.Writer, default);
        await f.Pages.SaveAsync(page.Id, new("Three", Fixture.Rich("Three"), "three", 2), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new(3, "B"), f.Writer, default);
        // The publication sealed version 1; the saves 2 and 3 are one version, published with its newest revision.
        var items = (await f.History.ListAsync(page.Id, f.Writer)).Items;
        Assert.Equal(new[] { 3, 1 }, items.Select(x => x.Version));
        Assert.Equal(new[] { "B", "A" }, items.Select(x => x.ChangeSummary));
        foreach (var item in items)
        {
            Assert.Equal(item.ChangeSummary, (await f.History.GetRevisionAsync(page.Id, item.Id, f.Writer)).ChangeSummary);
        }
    }

    [PostgreSqlFact]
    public async Task Concurrent_restore_has_one_CAS_winner_and_preserves_all_old_content()
    {
        await using var f = await Fixture.CreateAsync();
        var a = await f.Create("A");
        await f.Pages.SaveAsync(a.Id, new("B", Fixture.Rich("B"), "B", 1), f.Writer, default);
        await f.Pages.SaveAsync(a.Id, new("C", Fixture.Rich("C"), "C", 2), f.Writer, default);
        var old = await f.Revisions(a.Id);
        async Task<bool> Attempt(Guid revisionId)
        {
            try { await f.History.RestoreAsync(a.Id, new(revisionId, 3), f.Writer); return true; }
            catch (ApiException ex) when (ex.Status == 409) { return false; }
        }
        Assert.Single(await Task.WhenAll(Attempt(old[1].Id), Attempt(old[2].Id)), x => x);
        var history = await f.History.ListAsync(a.Id, f.Writer);
        Assert.Equal(4, history.CurrentVersion);
        // The restore is a version of its own after the version of the three saves.
        Assert.Equal(new[] { 4, 3 }, history.Items.Select(x => x.Version));
        Assert.Equal(new[] { "C", "B", "A" }, (await f.Revisions(a.Id)).Skip(1).Select(x => x.Title));
    }

    [PostgreSqlFact]
    public async Task Restore_rechecks_the_locked_version_even_when_the_session_already_loaded_the_page()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("Loaded");
        var first = Assert.Single((await f.History.ListAsync(page.Id, f.Writer)).Items);
        using var scope = f.Host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        using var transaction = session.BeginTransaction();
        Assert.Equal(1, (await session.GetAsync<Page>(page.Id)).Version);

        await f.Pages.SaveAsync(page.Id, new("Theirs", Fixture.Rich("Theirs"), "theirs", 1), f.Writer, default);
        var handler = ActivatorUtilities.CreateInstance<RestorePageHandler>(scope.ServiceProvider);
        var error = await Assert.ThrowsAsync<ApiException>(() => handler.Handle(new RestorePage(page.Id, new(first.Id, 1)), default));
        Assert.Equal((409, "version_conflict"), (error.Status, error.Code));
    }

    [PostgreSqlFact]
    public async Task History_keyset_is_bounded_stable_and_missing_or_invalid_requests_fail()
    {
        await using var f = await Fixture.CreateAsync();
        var a = await f.Create("A");
        for (var version = 1; version < 4; version++)
            await f.Pages.SaveAsync(a.Id, new("V" + version, Fixture.Rich("V" + version), "V" + version, version), f.Writer, default);
        // One version of four saves: its steps page like the versions do.
        var group = Assert.Single((await f.History.ListAsync(a.Id, f.Writer)).Items).GroupId;
        var first = await f.History.ListAsync(a.Id, f.Writer, limit: 2, group: group);
        Assert.Equal(new[] { 4, 3 }, first.Items.Select(x => x.Version));
        Assert.Equal(3, first.NextBeforeVersion);
        await f.Pages.SaveAsync(a.Id, new("New", Fixture.Rich("New"), "New", 4), f.Writer, default);
        var next = await f.History.ListAsync(a.Id, f.Writer, first.NextBeforeVersion, 2, group);
        Assert.Equal(new[] { 2, 1 }, next.Items.Select(x => x.Version));
        Assert.Null(next.NextBeforeVersion);
        Assert.Empty((await f.History.ListAsync(a.Id, f.Writer, 1)).Items);
        await Denied(400, () => f.History.ListAsync(a.Id, f.Writer, 0));
        await Denied(400, () => f.History.ListAsync(a.Id, f.Writer, limit: int.MaxValue));
        await Denied(404, () => f.History.ListAsync(Guid.NewGuid(), f.Writer));
        await Denied(404, () => f.History.GetRevisionAsync(a.Id, Guid.NewGuid(), f.Writer));
    }

    /// <summary>3.6-C: people's saves are one version with every contributor; a change with AI, a named version and a publication
    /// each end the version, and every version knows the revision it is compared with and what it changed.</summary>
    [PostgreSqlFact]
    public async Task Saves_group_into_versions_that_AI_named_versions_and_publications_end()
    {
        await using var f = await Fixture.CreateAsync();
        var a = await f.Create("A");
        // Published, so the other writer sees the page; the publication ends version 1.
        await f.Pages.PublishAsync(a.Id, new(1), f.Writer, default);
        await f.Pages.SaveAsync(a.Id, new("B", Fixture.Rich("B"), null, 1), f.Writer, default);
        await f.Pages.SaveAsync(a.Id, new("C", Fixture.Rich("C"), null, 2), f.OtherWriter, default);
        // Written with the editor's AI (the channels are covered by RevisionVersionsTests).
        await f.Pages.SaveAsync(a.Id, new("D", Fixture.Rich("D"), null, 3, AiAssisted: true), f.Writer, default);
        await f.Pages.SaveAsync(a.Id, new("E", Fixture.Rich("E"), null, 4), f.Writer, default);
        f.Host.Actor.Current = f.Writer;
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<NamedVersion>(new NameVersion(a.Id, 5, new string('x', 501))))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<NamedVersion>(new NameVersion(a.Id, 4, "Hotovo")))).Status);
        Assert.Equal(5, (await f.Host.InvokeAsync<NamedVersion>(new NameVersion(a.Id, 5, " Hotovo "))).Version);
        await f.Pages.SaveAsync(a.Id, new("F", Fixture.Rich("F"), null, 5), f.Writer, default);
        await f.Pages.PublishAsync(a.Id, new(6), f.Writer, default);
        await f.Pages.SaveAsync(a.Id, new("G", Fixture.Rich("G"), null, 6), f.Writer, default);

        var versions = (await f.History.ListAsync(a.Id, f.Writer)).Items;
        Assert.Equal(new[] { 7, 6, 5, 4, 3, 1 }, versions.Select(x => x.Version));
        Assert.Equal(new[] { 1, 1, 1, 1, 2, 1 }, versions.Select(x => x.Checkpoints));
        Assert.Equal(new[] { false, false, false, true, false, false }, versions.Select(x => x.IsAi));
        Assert.Equal(new[] { null, null, "Hotovo", null, null, null }, versions.Select(x => x.Label));
        Assert.Equal(versions.Skip(1).Select(x => (Guid?)x.Id).Append(null), versions.Select(x => x.PreviousRevisionId));
        var people = versions[^2];
        Assert.Equal([("Writer", 1), ("Other Writer", 1)], people.Changes!.Contributors.Select(x => (x.Name!, x.Checkpoints)));
        Assert.Equal((1, 1, "A"), (people.Changes.Added, people.Changes.Removed, people.Changes.TitleBefore));
        var ai = versions[^3];
        Assert.Equal(("Writer", "web", "C"), (ai.AuthorName, ai.Channel!, ai.Changes!.TitleBefore!));
        Assert.Equal((1, 1, 1), (ai.Changes.Added, ai.Changes.Removed, ai.Changes.Blocks));
        Assert.True((await f.History.GetRevisionAsync(a.Id, ai.Id, f.Writer)).IsAi);

        var steps = (await f.History.ListAsync(a.Id, f.Writer, group: people.GroupId)).Items;
        Assert.Equal(new[] { 3, 2 }, steps.Select(x => x.Version));
        Assert.Equal([steps[1].Id, versions[^1].Id], steps.Select(x => x.PreviousRevisionId));
        var first = await f.History.ListAsync(a.Id, f.Writer, limit: 2);
        Assert.Equal(6, first.NextBeforeVersion);
        Assert.Equal(new[] { 5, 4 }, (await f.History.ListAsync(a.Id, f.Writer, first.NextBeforeVersion, 2)).Items.Select(x => x.Version));
    }

    /// <summary>Grouping keeps revisions immutable, so the history of a requirement still says who changed each state (3.6-0).</summary>
    [PostgreSqlFact]
    public async Task A_requirement_change_keeps_its_author_inside_a_shared_version()
    {
        await using var f = await Fixture.CreateAsync();
        // Requirement headings (3.6): a block-shaped page is converted when it is opened, so its saves would compare two shapes.
        const string First = "db", Second = "web";
        static string Content(string first, string second) => ProjectorDocuments.Rich(
            ProjectorDocuments.RequirementHeading(First, "Databáze", first), ProjectorDocuments.RequirementHeading(Second, "Web", second)).ToJsonString();
        var page = await f.Pages.CreateAsync(new(f.SpaceId, null, "Plán", Content("planned", "planned"), null), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        await f.Pages.SaveAsync(page.Id, new("Plán", Content("done", "planned"), null, 1), f.Writer, default);
        await f.Pages.SaveAsync(page.Id, new("Plán", Content("done", "blocked"), null, 2), f.OtherWriter, default);

        var version = (await f.History.ListAsync(page.Id, f.Writer)).Items[0];
        Assert.Equal(2, version.Checkpoints);
        Assert.Equal(["Databáze:planned>done", "Web:planned>blocked"], version.Changes!.States.Select(x => $"{x.Title}:{x.From}>{x.To}"));
        // Each checkpoint names the blocks it touched against the one before (not the version's start), so each block keeps its author.
        using var session = f.Host.OpenSession();
        var changes = await new RequirementIndex(session).LastChangesAsync(page.Id, int.MaxValue, true,
            RequirementTree.Read(JsonNode.Parse(Content("done", "blocked"))!.AsObject()), default);
        Assert.Equal([$"{First}:{f.Writer.Id}", $"{Second}:{f.OtherWriter.Id}"], changes.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value.ChangedById}"));
    }

    /// <summary>3.6: a revision stored in an older Markdown dialect reads in history as today's writer projects its content, so neither the
    /// revision view nor the next version's +/− counts show a change the dialect made.</summary>
    [PostgreSqlFact]
    public async Task An_old_revision_in_an_older_dialect_shows_no_spurious_change_against_its_successor()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Pages.CreateAsync(new(f.SpaceId, null, "Plán", Fixture.Rich("Beze změny"), null), f.Writer, default);
        var first = (await f.History.ListAsync(page.Id, f.Writer)).Items[0].Id;
        // Published, so the next save is a version of its own measured against this one.
        await f.Pages.PublishAsync(page.Id, new(page.Version), f.Writer, default);
        // The first revision's Markdown as an older writer stored it.
        await f.Host.ExecuteAsync("""UPDATE "PageRevision" SET "Markdown"='Beze změny  ' || chr(10) || chr(10) WHERE "Id"=:id""", first);

        await f.Pages.SaveAsync(page.Id, new("Plán nasazení", Fixture.Rich("Beze změny"), null, page.Version), f.Writer, default);

        var versions = (await f.History.ListAsync(page.Id, f.Writer)).Items;
        Assert.Equal((0, 0, "Plán"), (versions[0].Changes!.Added, versions[0].Changes!.Removed, versions[0].Changes!.TitleBefore));
        Assert.Equal((await f.History.GetRevisionAsync(page.Id, versions[0].Id, f.Writer)).Markdown,
            (await f.History.GetRevisionAsync(page.Id, first, f.Writer)).Markdown);
    }

    /// <summary>A character typed and deleted again (owner, 3.6): the version that ends exactly where it started is no version, its revisions
    /// stay stored, and the page it returned to is the published one again, with nothing to publish.</summary>
    [PostgreSqlFact]
    public async Task A_version_that_ends_where_it_started_is_no_version_and_leaves_nothing_to_publish()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("A");
        await f.Pages.PublishAsync(page.Id, new(1, "První"), f.Writer, default);
        await f.Pages.SaveAsync(page.Id, new("A", Fixture.Rich("Ax"), null, 1), f.Writer, default);
        var back = await f.Pages.SaveAsync(page.Id, new("A", Fixture.Rich("A"), null, 2), f.Writer, default);

        Assert.Equal((3, 3), (back.Version, back.PublishedVersion));
        var revisions = await f.Revisions(page.Id);
        Assert.Equal(3, revisions.Count);
        Assert.Equal(revisions[2].Id, revisions[0].SameAsRevisionId);
        var only = Assert.Single((await f.History.ListAsync(page.Id, f.Writer)).Items);
        Assert.Equal(1, only.Version);
        Assert.True(only.IsCurrent && only.IsPublished);
        Assert.Equal("První", only.ChangeSummary);

        // Typing on is a version again, measured against the page it returned to.
        await f.Pages.SaveAsync(page.Id, new("A", Fixture.Rich("Ay"), null, 3), f.Writer, default);
        var versions = (await f.History.ListAsync(page.Id, f.Writer)).Items;
        Assert.Equal(new[] { 4, 1 }, versions.Select(x => x.Version));
        Assert.Equal(new[] { true, false }, versions.Select(x => x.IsCurrent));
        Assert.Equal(new[] { false, true }, versions.Select(x => x.IsPublished));
        Assert.Equal(revisions[0].Id, versions[0].PreviousRevisionId);
    }

    /// <summary>The comparison is with the version before, not with any earlier one: a version that returns to the published page after another
    /// version changed it is listed, and only the publication moves to it (with its publisher and summary). A later version that ends where it
    /// started leaves it listed as current and published.</summary>
    [PostgreSqlFact]
    public async Task A_version_that_returns_to_the_published_page_is_listed_and_takes_over_the_publication()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("A");
        await f.Pages.PublishAsync(page.Id, new(1, "První"), f.Writer, default);
        await f.Pages.SaveAsync(page.Id, new("A", Fixture.Rich("B"), null, 1), f.Writer, default);
        f.Host.Actor.Current = f.Writer;
        await f.Host.InvokeAsync<NamedVersion>(new NameVersion(page.Id, 2, "Rozpracováno"));
        var back = await f.Pages.SaveAsync(page.Id, new("A", Fixture.Rich("A"), null, 2), f.OtherWriter, default);

        Assert.Equal((3, 3), (back.Version, back.PublishedVersion));
        var versions = (await f.History.ListAsync(page.Id, f.Writer)).Items;
        Assert.Equal(new[] { 3, 2, 1 }, versions.Select(x => x.Version));
        Assert.Equal(new[] { true, false, false }, versions.Select(x => x.IsPublished));
        Assert.Equal(new[] { "První", null, "První" }, versions.Select(x => x.ChangeSummary));
        f.Host.Actor.Current = f.Writer;
        var state = await f.Host.InvokeAsync<PagePublicationState>(new GetPagePublication(page.Id));
        Assert.Equal(("Writer", false), (state.PublishedBy, state.UnpublishedChanges));
        // The writer's „Publikovaná“ view opens the revision readers read.
        Assert.Equal(versions[0].Id, state.PublishedRevisionId);

        await f.Pages.SaveAsync(page.Id, new("A", Fixture.Rich("Ax"), null, 3), f.Writer, default);
        var again = await f.Pages.SaveAsync(page.Id, new("A", Fixture.Rich("A"), null, 4), f.Writer, default);
        Assert.Equal((5, 5), (again.Version, again.PublishedVersion));
        versions = (await f.History.ListAsync(page.Id, f.Writer)).Items;
        Assert.Equal(new[] { 3, 2, 1 }, versions.Select(x => x.Version));
        Assert.True(versions[0].IsCurrent && versions[0].IsPublished);
    }

    private static async Task Denied(int status, Func<Task> action)
        => Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AtlasTestHost m_host;

        private Fixture(AtlasTestHost host)
        {
            m_host = host;
            Pages = new TestPages(host);
            History = new TestPageHistory(host);
            Collaboration = new TestCollaboration(host);
        }

        public AtlasTestHost Host => m_host;
        public AtlasDatabase Database => m_host.Database;
        public TestPages Pages { get; }
        public TestPageHistory History { get; }
        public TestCollaboration Collaboration { get; }
        public UserAccount Admin { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("admin"), DisplayName = "Admin", IsWikiAdmin = true };
        public UserAccount Writer { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("writer"), DisplayName = "Writer" };
        public UserAccount OtherWriter { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("other"), DisplayName = "Other Writer" };
        public UserAccount Reader { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("reader"), DisplayName = "Reader" };
        public Guid SpaceId { get; } = Guid.NewGuid();

        public static string Rich(string text) => "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";

        public Task<PageDto> Create(string title) => Pages.CreateAsync(new(SpaceId, null, title, Rich(title), title), Writer, default);

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(await AtlasTestHost.CreateAsync("atlas_history_"));
            await f.m_host.SeedAsync(async session =>
            {
                foreach (var user in new[] { f.Admin, f.Writer, f.OtherWriter, f.Reader })
                {
                    await session.SaveAsync(user);
                }
                await session.SaveAsync(new Space { Id = f.SpaceId, Name = "History" });
                await session.FlushAsync();
                foreach (var user in new[] { f.Writer, f.OtherWriter, f.Reader })
                {
                    await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = user.Id, Role = user == f.Reader ? 1 : 2 });
                }
            });
            return f;
        }

        public Task Execute(string sql, Guid id) => m_host.ExecuteAsync(sql.Replace("@id", ":id"), id);

        /// <summary>Every revision of the page, newest first: the history lists versions, which group them.</summary>
        public async Task<IList<PageRevision>> Revisions(Guid pageId)
        {
            using var session = m_host.OpenSession();
            return await session.QueryOver<PageRevision>().Where(x => x.PageId == pageId).OrderBy(x => x.Version).Desc.ListAsync();
        }

        public ValueTask DisposeAsync() => m_host.DisposeAsync();
    }
}
