using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Queries;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Contacts;

/// <summary>
/// The contact directory (objects spec §2.3): wiki contacts read by everyone and written by a WikiAdmin or anyone who writes in a
/// space, created once per e-mail address, changed field by field, found by any field and moved to the trash.
/// </summary>
public sealed class ContactObjectTests
{
    [PostgreSqlFact]
    [Trait("Category", "Smoke")]
    public async Task A_wiki_contact_is_written_by_space_writers_and_read_by_everyone_field_by_field()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var mail = $"jana@{Tag()}.example.com";
        var jana = await f.As<ObjectDto>(f.Writer, Create($$"""{"name":"**Jana** Nováková","emails":["{{mail}}"],"organization":"Modelsoft"}"""));
        Assert.Equal((ContactObjectType.Id, ObjectScopes.Wiki, "fields", true, "Jana Nováková"), (jana.TypeId, jana.Scope, jana.EditingModel, jana.CanEdit, jana.Title));
        await f.As<ObjectDto>(f.Admin, Create("""{"name":"Petr"}"""));
        // Writing in no space is reading the directory only.
        await BoardsFixture.Refused(403, () => f.As(f.Reader, Create("""{"name":"Reader's"}""")));
        await BoardsFixture.Refused(403, () => f.As(f.Outsider, Create("""{"name":"Outsider's"}""")));

        Assert.False((await f.As<ObjectDto>(f.Outsider, new GetObject(jana.Id))).CanEdit);
        await BoardsFixture.Refused(403, () => f.As(f.Reader, Change(jana.Id, """{"title":"x"}""")));
        await f.As<ObjectChangeResult>(f.Writer, Change(jana.Id, """{"title":"Vedoucí _provozu_"}"""));
        Assert.Equal(new ObjectChangeResult(3, false), await f.As<ObjectChangeResult>(f.Admin, Change(jana.Id, """{"organization":null,"phones":["777 123 456"]}""")));
        var read = await f.As<ObjectDto>(f.Reader, new GetObject(jana.Id));
        Assert.Equal(JsonNode.Parse($$"""{"name":"**Jana** Nováková","title":"Vedoucí _provozu_","emails":["{{mail}}"],"phones":["777 123 456"]}"""), read.Data,
            JsonNode.DeepEquals);
        await BoardsFixture.Refused(400, () => f.As(f.Writer, Change(jana.Id, """{"emails":["not an address"]}""")), "invalid_object_change");

        using var scope = f.Host.Services.CreateScope();
        var projection = await scope.ServiceProvider.GetRequiredService<ObjectTypes>().Find(ContactObjectType.Id)!.ProjectAsync(jana.Id, default);
        Assert.Equal(("Jana Nováková", $"Jana Nováková · Vedoucí provozu · {mail} · 777 123 456"), (projection!.Title, projection.PlainText));
        Assert.Equal(projection.PlainText, Assert.Single(projection.Chunks).Text);
    }

    [PostgreSqlFact]
    public async Task A_contact_with_an_address_already_in_the_directory_answers_the_existing_one()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var domain = Tag() + ".example.com";
        var jana = await f.As<ObjectDto>(f.Writer, Create($$"""{"name":"Jana","emails":["other@{{domain}}","jana@{{domain}}"]}"""));

        var duplicate = await BoardsFixture.Refused(409, () => f.As(f.Admin, Create($$"""{"name":"Jana N.","emails":["JANA@{{domain.ToUpperInvariant()}}"]}""")), "duplicate_contact");
        Assert.Equal(jana.Id, duplicate.Extensions!["contactId"]);
        // Only within the scope: a private contact of the same person is the owner's own business.
        await f.As<ObjectDto>(f.Reader, Create($$"""{"name":"Jana","emails":["jana@{{domain}}"]}""", ObjectScopes.Private));
        // A trashed contact no longer holds its address.
        await f.As(f.Writer, new TrashObject(jana.Id));
        await f.As<ObjectDto>(f.Admin, Create($$"""{"name":"Jana N.","emails":["jana@{{domain}}"]}"""));
    }

    [PostgreSqlFact]
    public async Task The_directory_finds_contacts_by_any_field_lists_their_data_and_says_who_may_add_one()
    {
        await using var f = await BoardsFixture.SharedAsync();
        // Every contact of the test holds its tag and every lookup asks for it too: the wiki directory is every test's.
        var tag = Tag();
        await f.As<ObjectDto>(f.Writer, Create($$"""{"name":"Jana","organization":"Modelsoft","emails":["jana@{{tag}}.example.com"]}"""));
        var petr = await f.As<ObjectDto>(f.Writer, Create($$"""{"name":"Petr","title":"Jednatel","phones":["+420 777 000 111"],"department":"{{tag}}"}"""));

        async Task<string> FindAsync(UserAccount actor, string q) =>
            string.Join(", ", (await f.As<ObjectList>(actor, new ListObjects(ContactObjectType.Id, ObjectScopes.Wiki, null, $"{q} {tag}"))).Items.Select(item => item.Title));
        Assert.Equal("Jana", await FindAsync(f.Reader, "modelsoft"));
        Assert.Equal("Jana", await FindAsync(f.Reader, "JANA@"));
        Assert.Equal("Petr", await FindAsync(f.Reader, "777 000"));
        // Diacritics and case aside, every word anywhere in the contact (the picker types „jan kovarik“).
        await f.As<ObjectDto>(f.Writer, Create($$"""{"name":"Jan Kovařík","organization":"AB Klinika","department":"{{tag}}"}"""));
        Assert.Equal("Jan Kovařík", await FindAsync(f.Reader, "kovarik"));
        Assert.Equal("Jan Kovařík", await FindAsync(f.Reader, "KOVAŘÍK klinika"));
        Assert.Equal("", await FindAsync(f.Reader, "kovarik modelsoft"));
        // Field names are no content.
        Assert.Equal("", await FindAsync(f.Reader, "organization"));

        // By the tag: the unfiltered directory is every test's and paged, so other tests' contacts would push Petr off the first page.
        var list = await f.As<ObjectList>(f.Writer, new ListObjects(ContactObjectType.Id, ObjectScopes.Wiki, null, tag));
        Assert.True(list.CanCreate);
        Assert.Equal("Jednatel", list.Items.Single(item => item.Id == petr.Id).Data!["title"]!.GetValue<string>());
        Assert.False((await f.As<ObjectList>(f.Reader, new ListObjects(ContactObjectType.Id, ObjectScopes.Wiki, null, tag))).CanCreate);

        await BoardsFixture.Refused(403, () => f.As(f.Reader, new TrashObject(petr.Id)));
        await f.As(f.Writer, new TrashObject(petr.Id));
        await BoardsFixture.Refused(404, () => f.As(f.Reader, new GetObject(petr.Id)));
        Assert.Equal("", await FindAsync(f.Writer, "Petr"));
        await BoardsFixture.Refused(404, () => f.As(f.Writer, new TrashObject(petr.Id)));
    }

    [PostgreSqlFact]
    public async Task Only_who_may_change_a_contact_teaches_it_an_alias_and_the_directory_finds_it_by_the_alias()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var mail = $"marek@{Tag()}.example.com";
        var martin = await f.As<ObjectDto>(f.Writer, Create($$"""{"name":"Marek Musil","emails":["{{mail}}"]}"""));
        ApplyObjectChange Learn(string query) => new(martin.Id, Guid.NewGuid(), null, new JsonObject { ["learnAlias"] = query });

        // A reader's pick teaches nothing: aliases show on the card to everyone.
        await BoardsFixture.Refused(403, () => f.As(f.Reader, Learn("MMu")));
        await f.As<ObjectChangeResult>(f.Writer, Learn("MMu"));
        await f.As<ObjectChangeResult>(f.Admin, Learn("mmu"));
        await BoardsFixture.Refused(400, () => f.As(f.Writer, Learn("Mare")), "invalid_object_change");
        var read = await f.As<ObjectDto>(f.Reader, new GetObject(martin.Id));
        Assert.Equal(JsonNode.Parse($$$"""{"name":"Marek Musil","emails":["{{{mail}}}"],"aliases":["MMu"],"aliasPicks":{"mmu":2}}"""), read.Data, JsonNode.DeepEquals);
        // The alias and the test's own address: the wiki directory is every test's.
        var found = await f.As<ObjectList>(f.Reader, new ListObjects(ContactObjectType.Id, ObjectScopes.Wiki, null, "mmu " + mail));
        Assert.Equal("Marek Musil", Assert.Single(found.Items).Title);
    }

    /// <summary>A token of one test: on the shared host the wiki directory is every test's and refuses a second contact of an address.</summary>
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static CreateObject Create(string data, string scope = ObjectScopes.Wiki)
    {
        return new CreateObject(ContactObjectType.Id, scope, null, JsonNode.Parse(data)!.AsObject());
    }

    private static ApplyObjectChange Change(Guid id, string fields)
    {
        return new ApplyObjectChange(id, Guid.NewGuid(), null, new JsonObject { ["fields"] = JsonNode.Parse(fields) });
    }
}
