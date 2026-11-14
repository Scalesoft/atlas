using Atlas.Server.Features.PageAccess.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Tests.Attachments;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageAccess;

/// <summary>The people directory of the page editor (contacts and @): every typed word, diacritics and case aside, in any order.</summary>
public sealed class PagePeopleSearchTests
{
    [PostgreSqlFact]
    public async Task People_are_found_by_every_word_whatever_the_diacritics()
    {
        await using var f = await BoardsFixture.SharedAsync();
        // The directory is the whole installation's: a token of this test in the login and the e-mail domain narrows every search to it.
        var token = Guid.NewGuid().ToString("N")[..10];
        await f.Host.SeedAsync(session => session.SaveAsync(new UserAccount
            { Id = Guid.NewGuid(), Username = "jkovarik-" + token, DisplayName = "Jan Kovařík", Email = $"jan.kovarik@{token}.example.com" }));
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Nasazení", AttachmentFixture.Rich("Nasazení"), "Nasazení")));

        async Task<string> FindAsync(string q) =>
            string.Join(", ", (await f.As<IReadOnlyList<PagePerson>>(f.Writer, new SearchPagePeople(page.Id, q + " " + token))).Select(person => person.Name));
        Assert.Equal("Jan Kovařík", await FindAsync("kovarik"));
        Assert.Equal("Jan Kovařík", await FindAsync("KOVAŘÍK jan"));
        Assert.Equal("Jan Kovařík", await FindAsync("example jkovarik"));
        Assert.Equal("", await FindAsync("kovarik petr"));
    }

    [PostgreSqlFact]
    public async Task The_roster_is_read_whole_up_to_the_limit_asked_for()
    {
        await using var f = await BoardsFixture.SharedAsync();
        // A token of this test in the logins keeps other tests' accounts out of the count.
        var token = Guid.NewGuid().ToString("N")[..10];
        await f.Host.SeedAsync(async session =>
        {
            for (var i = 0; i < 25; i++)
            {
                await session.SaveAsync(new UserAccount { Id = Guid.NewGuid(), Username = $"roster{i:00}-{token}", DisplayName = $"Rostr Osoba {i:00}" });
            }
        });
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Lidé", AttachmentFixture.Rich("Lidé"), "Lidé")));

        async Task<int> CountAsync(int? limit) =>
            (await f.As<IReadOnlyList<PagePerson>>(f.Writer, new SearchPagePeople(page.Id, "rostr " + token, limit))).Count;
        Assert.Equal(20, await CountAsync(null));
        Assert.Equal(25, await CountAsync(500));
        Assert.Equal(25, await CountAsync(100_000));
        Assert.Equal(1, await CountAsync(0));
    }
}
