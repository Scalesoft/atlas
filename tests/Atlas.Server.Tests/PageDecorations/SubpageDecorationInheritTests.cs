using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Tests.PersonalLibrary;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageDecorations;

public sealed class SubpageDecorationInheritTests
{
    [PostgreSqlFact]
    public async Task Subpage_copies_the_parent_icon_and_color_and_the_user_choice_wins()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var parent = await f.Create("Parent");
        await Set(f, parent.Id, "folder", "blue");
        var child = await f.Create("Child", parent.Id);
        var inherited = await Get(f, child.Id);
        Assert.Equal(("folder", "blue"), (inherited!.Icon, inherited.Color));
        // The create answer carries it, so the icon picker's first change does not answer 409.
        Assert.Equal(inherited.RevisionId, child.Decoration?.RevisionId);
        // The copy is the child's own: changing it leaves the parent alone.
        await Set(f, child.Id, "home", "rose", inherited.RevisionId);
        Assert.Equal("folder", (await Get(f, parent.Id))!.Icon);
        Assert.Equal("home", (await Get(f, child.Id))!.Icon);
    }

    [PostgreSqlFact]
    public async Task Subpage_of_an_undecorated_parent_gets_no_decoration()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var parent = await f.Create("Plain parent");
        var child = await f.Create("Child", parent.Id);
        Assert.Null(await Get(f, parent.Id));
        Assert.Null(await Get(f, child.Id));
    }

    [PostgreSqlFact]
    public async Task Existing_subpage_is_not_changed_when_the_parent_is_decorated_later()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var parent = await f.Create("Parent");
        var child = await f.Create("Child", parent.Id);
        await Set(f, parent.Id, "folder", "blue");
        Assert.Null(await Get(f, child.Id));
    }

    private static async Task<PageDecorationDto?> Get(PersonalLibraryFixture f, Guid id) =>
        (await f.As<PageDecorationState>(f.Writer, new GetPageDecoration(id))).Decoration;

    private static Task Set(PersonalLibraryFixture f, Guid id, string icon, string color, Guid? expected = null) =>
        f.As<PageDecorationState>(f.Writer, new SetPageDecoration(id, new(icon, color, expected)));
}
