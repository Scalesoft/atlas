using System.Globalization;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Relations.RelationsFixture;

namespace Atlas.Server.Tests.Relations;

/// <summary>The pages of a space's contents (ListSpaceContentsHandler): the cursor continues in the order of the whole list, the filters
/// page the same way, and what a reader may not read is neither listed nor counted, on any page.</summary>
public sealed class SpaceContentsPagingTests
{
    private const int Contacts = 54;

    [PostgreSqlFact]
    public async Task Filtered_pages_follow_the_cursor_and_a_reader_never_gets_what_it_cannot_read()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var beta = await f.PageAsync("Beta");
        await f.PageAsync("Alfa", inline: [PageRef(beta.Id)]);
        // A draft only its author (and the wiki administrator) may read: the Reader's list and counts never hold it.
        await f.PageAsync("Skica", publish: false);
        for (var i = 0; i < Contacts; i++)
        {
            await f.ContactAsync(f.Writer, ObjectScopes.Space, $"Kontakt {i}");
        }
        var czech = StringComparer.Create(CultureInfo.GetCultureInfo("cs-CZ"), CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

        async Task<List<ContentRow>> AllAsync(Func<string?, Task<SpaceContents>> read, int expectedPages)
        {
            var rows = new List<ContentRow>();
            string? cursor = null;
            for (var pages = 0; pages < expectedPages; pages++)
            {
                var page = await read(cursor);
                Assert.True(page.Items.Count <= 50);
                rows.AddRange(page.Items);
                Assert.Equal(pages < expectedPages - 1, page.NextCursor is not null);
                cursor = page.NextCursor;
            }
            return rows;
        }

        // The whole list in the order of the first page: the second page continues it without a gap or a repeat.
        var reader = await AllAsync(cursor => f.ContentsAsync(f.Reader, cursor: cursor), 2);
        Assert.Equal(Contacts + 2, reader.Count);
        Assert.Equal(reader.Select(row => row.Title).OrderBy(title => title, czech), reader.Select(row => row.Title));
        Assert.DoesNotContain(reader, row => row.Title == "Skica");
        var writer = await AllAsync(cursor => f.ContentsAsync(f.Writer, cursor: cursor), 2);
        Assert.Equal(Contacts + 3, writer.Count);
        Assert.Contains(writer, row => row.Title == "Skica");

        // The counts are of what the reader reads, on the second page as on the first.
        foreach (var cursor in new[] { null, (await f.ContentsAsync(f.Reader)).NextCursor })
        {
            var summary = (await f.ContentsAsync(f.Reader, cursor: cursor)).Summary;
            Assert.Equal(new Dictionary<string, int> { ["atlas.page"] = 2, ["atlas.contact"] = Contacts }, summary.Types);
            Assert.Equal(new RelationCounts(Contacts, 1, 0, 0), summary.Relations);
        }
        Assert.Equal(new RelationCounts(Contacts, 2, 0, 0), (await f.ContentsAsync(f.Writer)).Summary.Relations);

        // Filters page too, and every sort continues its own order; titles in natural order („Kontakt 2“ before „Kontakt 10“).
        var contacts = await AllAsync(cursor => f.ContentsAsync(f.Reader, type: "atlas.contact", cursor: cursor), 2);
        Assert.Equal(Contacts, contacts.Count);
        Assert.Equal(Enumerable.Range(0, Contacts).Select(i => $"Kontakt {i}"), contacts.Select(row => row.Title));
        Assert.Equal(Contacts, (await AllAsync(cursor => f.ContentsAsync(f.Reader, "unused", cursor: cursor), 2)).Count);
        Assert.Equal(Contacts, (await AllAsync(cursor => f.ContentsAsync(f.Reader, q: "KONTAKT", cursor: cursor), 2)).Count);
        Assert.Equal(["Alfa", "Beta"], (await AllAsync(cursor => f.ContentsAsync(f.Reader, "mostUsed", cursor: cursor), 2)).Take(2).Select(row => row.Title).Order());
        var used = await AllAsync(cursor => f.ContentsAsync(f.Reader, sort: "used", cursor: cursor), 2);
        Assert.Equal("Beta", used[0].Title);
        Assert.Equal(Contacts + 2, used.Select(row => row.Id).Distinct().Count());
        var updated = await AllAsync(cursor => f.ContentsAsync(f.Reader, sort: "updated", cursor: cursor), 2);
        Assert.Equal(updated.OrderByDescending(row => row.UpdatedAt).Select(row => row.UpdatedAt), updated.Select(row => row.UpdatedAt));
    }
}
