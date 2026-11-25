using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Queries;
using Atlas.Server.Features.SpaceAi.Commands;
using Atlas.Server.Features.SpaceAi.Contracts;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PersonalLibrary;

/// <summary>Co se změnilo of the home dashboard (4.0): what a reader may see, how a change with AI is told, filters, paging and its cost.</summary>
public sealed class RecentChangesTests
{
    [PostgreSqlFact]
    public async Task A_reader_sees_changes_only_of_pages_they_may_read_and_mcp_none_of_a_space_out_of_ai()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var shared = await Published(f, "Shared");
        var restricted = await Published(f, "Restricted");
        await f.Execute("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=:id""", restricted.Id);
        var trashed = await Published(f, "Trashed");
        await f.As<OrganizationChange>(f.Writer, new TrashPage(trashed.Id, new TrashPageRequest(false, await Scalar<long>(f,
            """SELECT "Version" FROM "SpaceOrganization" WHERE "SpaceId"=:id""", f.SpaceId), 1)));
        await f.Create("Draft");
        var hiddenSpace = await f.Host.SeedSpaceAsync("Hidden", (f.Writer, SpaceRole.Writer));
        await Published(f, "Elsewhere", hiddenSpace);
        var outOfAi = await f.Host.SeedSpaceAsync("Out of AI", (f.Writer, SpaceRole.Writer), (f.Reader, SpaceRole.Reader));
        await Published(f, "Not for AI", outOfAi);
        await f.As<SpaceAiSettingsView>(f.Admin, new SaveSpaceAiSettings(outOfAi, new SaveSpaceAiSettingsRequest(false, null)));
        // Unpublished work on a published page: the writer's newest version, the reader's published one.
        await f.Pages.SaveAsync(shared.Id, new SavePageRequest("Shared", PersonalLibraryFixture.Rich("work"), null, 1), f.Writer, default);

        var reader = await List(f, f.Reader);
        Assert.Equal(["Not for AI", "Shared"], reader.Select(x => x.Title).Order());
        var readerShared = Assert.Single(reader, x => x.PageId == shared.Id);
        Assert.Equal((1, false), (readerShared.Version, readerShared.CanOpenHistory));
        var writerShared = Assert.Single(await List(f, f.Writer), x => x.PageId == shared.Id);
        Assert.Equal((2, true, 1), (writerShared.Version, writerShared.CanOpenHistory, writerShared.FurtherChanges));
        Assert.Equal(["Draft", "Elsewhere", "Not for AI", "Shared"], (await List(f, f.Writer)).Select(x => x.Title).Order());
        // A wiki administrator's list is every test's changes: followed by its cursor until the restricted page shows.
        var adminSees = false;
        string? cursor = null;
        do
        {
            var admin = await f.As<RecentChangeList>(f.Admin, new ListRecentChanges("all", cursor, 50));
            adminSees = admin.Items.Any(x => x.PageId == restricted.Id);
            cursor = admin.Next;
        } while (!adminSees && cursor is not null);
        Assert.True(adminSees);

        // Out of AI is about what reaches a model: the human view keeps it, MCP never names it.
        var token = await TestApiTokens.SeedAsync(f.Host, f.Reader, "agent");
        f.Host.Channel.Current = new RequestChannel(ContentChannel.Mcp, token);
        Assert.Equal(["Shared"], (await List(f, f.Reader)).Select(x => x.Title));
        f.Host.Channel.Current = RequestChannel.Web;
    }

    [PostgreSqlFact]
    public async Task A_change_with_ai_names_its_channel_and_token_and_the_filters_narrow_the_list()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var agentPage = await Published(f, "Agent page");
        var token = await TestApiTokens.SeedAsync(f.Host, f.Writer, "Claude Code");
        f.Host.Channel.Current = new RequestChannel(ContentChannel.Api, token);
        await f.Pages.SaveAsync(agentPage.Id, new SavePageRequest("Agent page", PersonalLibraryFixture.Rich("by agent"), null, 1), f.Writer, default);
        f.Host.Channel.Current = RequestChannel.Web;
        await Published(f, "Human page", owner: f.OtherWriter);

        var mine = Assert.Single(await List(f, f.Writer), x => x.PageId == agentPage.Id);
        Assert.Equal(("api", true, "Claude Code", 1), (mine.Channel, mine.IsAi, mine.ApiToken?.Name, mine.FurtherChanges));
        // Only its owner and wiki administrators learn the token's name; everyone sees that it came over the API.
        var colleague = Assert.Single(await List(f, f.OtherWriter), x => x.PageId == agentPage.Id);
        Assert.Equal(("api", true, (string?)null), (colleague.Channel, colleague.IsAi, colleague.ApiToken?.Name));
        Assert.False(Assert.Single(await List(f, f.Writer), x => x.Title == "Human page").IsAi);

        var ai = Assert.Single(await List(f, f.OtherWriter, "ai"));
        Assert.Equal((agentPage.Id, 0), (ai.PageId, ai.FurtherChanges));
        Assert.Equal(["Human page"], (await List(f, f.OtherWriter, "mine")).Select(x => x.Title));
        Assert.Equal(["Agent page"], (await List(f, f.Writer, "mine")).Select(x => x.Title));
    }

    [PostgreSqlFact]
    public async Task Pages_follow_by_cursor_and_the_list_costs_the_same_statements_for_one_page_or_many()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        await Published(f, "Page 0");
        await f.As<RecentChangeList>(f.Reader, new ListRecentChanges("all", null, 10));
        IReadOnlyList<string> first;
        using (var recording = SqlRecording.Start())
        {
            await f.As<RecentChangeList>(f.Reader, new ListRecentChanges("all", null, 10));
            first = recording.Statements;
        }
        var one = first.Count;
        for (var i = 1; i < 5; i++)
        {
            await Published(f, "Page " + i);
        }
        using (var recording = SqlRecording.Start())
        {
            Assert.Equal(5, (await f.As<RecentChangeList>(f.Reader, new ListRecentChanges("all", null, 10))).Items.Count);
            Assert.True(recording.Statements.Count == one, string.Join("\n---\n", recording.Statements));
        }
        // The account lock's three (advisory lock, account, actor) and one statement for the whole page of changes.
        Assert.True(one == 4, string.Join("\n---\n", first));

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var list = await f.As<RecentChangeList>(f.Reader, new ListRecentChanges("all", cursor, 2));
            seen.AddRange(list.Items.Select(x => x.Title));
            cursor = list.Next;
        } while (cursor is not null);
        Assert.Equal(["Page 4", "Page 3", "Page 2", "Page 1", "Page 0"], seen);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.As<RecentChangeList>(f.Reader, new ListRecentChanges("all", "x_y", 2)))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.As<RecentChangeList>(f.Reader, new ListRecentChanges("some", null, 2)))).Status);
    }

    [PostgreSqlFact]
    public async Task A_persons_activity_is_only_their_changes_the_viewer_may_read_narrowed_by_space_and_on_mcp_none_out_of_ai()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        await Published(f, "Writer plan");
        await Published(f, "Colleague plan", owner: f.OtherWriter);
        var hiddenSpace = await f.Host.SeedSpaceAsync("Hidden", (f.Writer, SpaceRole.Writer));
        await Published(f, "Hidden plan", hiddenSpace);
        var outOfAi = await f.Host.SeedSpaceAsync("Out of AI", (f.Writer, SpaceRole.Writer), (f.Reader, SpaceRole.Reader));
        await Published(f, "Not for AI", outOfAi);
        await f.As<SpaceAiSettingsView>(f.Admin, new SaveSpaceAiSettings(outOfAi, new SaveSpaceAiSettingsRequest(false, null)));

        // The writer's work in a space the reader has no grant on stays the writer's own: no row, no count.
        Assert.Equal(["Not for AI", "Writer plan"], (await Of(f, f.Reader, f.Writer.Id)).Select(x => x.Title).Order());
        Assert.Equal(["Hidden plan", "Not for AI", "Writer plan"], (await Of(f, f.Writer, f.Writer.Id)).Select(x => x.Title).Order());
        Assert.Equal(["Colleague plan"], (await Of(f, f.Reader, f.OtherWriter.Id)).Select(x => x.Title));
        Assert.Equal(["Writer plan"], (await Of(f, f.Reader, f.Writer.Id, f.SpaceId)).Select(x => x.Title));
        Assert.Empty(await Of(f, f.Reader, f.Writer.Id, hiddenSpace));

        var token = await TestApiTokens.SeedAsync(f.Host, f.Reader, "agent");
        f.Host.Channel.Current = new RequestChannel(ContentChannel.Mcp, token);
        Assert.Equal(["Writer plan"], (await Of(f, f.Reader, f.Writer.Id)).Select(x => x.Title));
        Assert.Empty(await Of(f, f.Reader, f.Writer.Id, outOfAi));
        f.Host.Channel.Current = RequestChannel.Web;
    }

    private static async Task<PageDto> Published(PersonalLibraryFixture f, string title, Guid? spaceId = null, UserAccount? owner = null)
    {
        var author = owner ?? f.Writer;
        var page = await f.Pages.CreateAsync(new CreatePageRequest(spaceId ?? f.SpaceId, null, title, PersonalLibraryFixture.Rich(title), title), author, default);
        return await f.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), author, default);
    }

    private static async Task<IReadOnlyList<RecentChange>> List(PersonalLibraryFixture f, UserAccount actor, string filter = "all")
        => (await f.As<RecentChangeList>(actor, new ListRecentChanges(filter, null, 50))).Items;

    private static async Task<IReadOnlyList<RecentChange>> Of(PersonalLibraryFixture f, UserAccount viewer, Guid author, Guid? space = null)
        => (await f.As<RecentChangeList>(viewer, new ListRecentChanges("all", null, 50, author, space))).Items;

    private static async Task<T> Scalar<T>(PersonalLibraryFixture f, string sql, Guid id)
    {
        using var session = f.Host.OpenSession();
        return await session.CreateSQLQuery(sql).SetParameter("id", id).UniqueResultAsync<T>();
    }
}
