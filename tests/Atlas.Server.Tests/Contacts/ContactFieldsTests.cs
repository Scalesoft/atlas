using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Platform.Objects;
using Xunit;

namespace Atlas.Server.Tests.Contacts;

/// <summary>The field rules of a contact (objects spec §1a, §2.2): Markdown text fields, identifier fields with their own
/// validation, per-field last-writer-wins patches, and its plain-text projection.</summary>
public sealed class ContactFieldsTests
{
    [Fact]
    public void A_new_contact_is_stored_canonically_with_trimmed_fields_and_without_empty_ones()
    {
        var data = ContactFields.Create(Json("""
            {"emails":[" jana@example.com ","JANA@example.com"],"name":"  **Jana** Nováková ","organization":"","title":"Vedoucí _provozu_",
             "phones":["+420 123 456 789"],"website":"https://example.test","address":"Ulice 1\r\nBrno","department":null}
            """));
        Assert.Equal("""{"name":"**Jana** Nováková","title":"Vedoucí _provozu_","emails":["jana@example.com"],"phones":["+420 123 456 789"],"website":"https://example.test","address":"Ulice 1\nBrno"}""",
            Text(data));
    }

    [Theory]
    [InlineData("""{"title":"x"}""", "Jméno")]
    [InlineData("""{"name":"   "}""", "Jméno")]
    [InlineData("""{"name":"two\nlines"}""", "Jméno")]
    [InlineData("""{"name":"x","organization":"bidi ‮"}""", "Organizace")]
    [InlineData("""{"name":"x","emails":["no-at-sign"]}""", "E-mail")]
    [InlineData("""{"name":"x","phones":["call me"]}""", "Telefon")]
    [InlineData("""{"name":"x","website":"javascript:alert(1)"}""", "Web")]
    [InlineData("""{"name":"x","website":"https://user:pw@example.com"}""", "Web")]
    [InlineData("""{"name":"x","photo":"/relative.png"}""", "Fotografie")]
    [InlineData("""{"name":"x","emails":"jana@example.com"}""", "E-mail")]
    [InlineData("""{"name":"x","username":"jana"}""", "Neznámé pole")]
    [InlineData("""{"name":"x","address":"tab\tseparated"}""", "Adresa")]
    public void An_invalid_field_is_refused_with_the_field_named(string json, string named)
    {
        var refused = Assert.Throws<ObjectChangeRejected>(() => ContactFields.Create(Json(json)));
        Assert.Contains(named, refused.Message);
    }

    [Fact]
    public void A_contact_field_keeps_the_text_of_a_contact_mention_never_the_mention()
    {
        var data = ContactFields.Create(Json("""
            {"name":"[Jana Nováková](a-contact:?name=Jana%20Nov%C3%A1kov%C3%A1&objectId=0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b)",
             "title":"Zástupce [Petr \\] Svoboda](a-contact:?name=Petr)","address":"U [Evy](a-contact:?name=Eva)\nBrno"}
            """));
        Assert.Equal("Jana Nováková", (string?)data["name"]);
        Assert.Equal("Zástupce Petr ] Svoboda", (string?)data["title"]);
        Assert.Equal("U Evy\nBrno", (string?)data["address"]);
    }

    [Fact]
    public void Lists_and_lengths_are_bounded()
    {
        Assert.Throws<ObjectChangeRejected>(() => ContactFields.Create(Json($$"""{"name":"x","emails":[{{string.Join(",", Enumerable.Range(0, 21).Select(i => $"\"a{i}@b.cz\""))}}]}""")));
        Assert.Throws<ObjectChangeRejected>(() => ContactFields.Create(Json($$"""{"name":"{{new string('x', ContactFields.MaxLine + 1)}}"}""")));
        Assert.NotNull(ContactFields.Create(Json($$"""{"name":"{{new string('x', ContactFields.MaxLine)}}"}""")));
    }

    [Fact]
    public void A_change_sets_only_the_fields_it_names_and_clears_with_null_or_empty_but_never_the_name()
    {
        var current = ContactFields.Create(Json("""{"name":"Jana","title":"Ředitelka","emails":["jana@example.com"]}"""));

        var (data, change) = ContactFields.Apply(current, Json("""{"title":null,"phones":[" 777 123 456 "],"organization":"Modelsoft"}"""));

        Assert.Equal("""{"name":"Jana","organization":"Modelsoft","emails":["jana@example.com"],"phones":["777 123 456"]}""", Text(data));
        // What the object's viewers hear: the named fields as stored, a cleared one as null.
        Assert.Equal("""{"title":null,"phones":["777 123 456"],"organization":"Modelsoft"}""", Text(change));
        Assert.Equal("""{"name":"Jana"}""", ContactFields.Apply(data, Json("""{"organization":"","emails":[],"phones":null}""")).Data.ToJsonString());
        Assert.Throws<ObjectChangeRejected>(() => ContactFields.Apply(current, Json("""{"name":null}""")));
        Assert.Throws<ObjectChangeRejected>(() => ContactFields.Apply(current, Json("{}")));
    }

    [Fact]
    public void The_projection_is_plain_text_without_Markdown_and_inline_fields_never_read_as_blocks()
    {
        var data = ContactFields.Create(Json("""
            {"name":"1. **Jana** [Nováková](https://example.com)","title":"- vedoucí","organization":"# Modelsoft","emails":["jana@example.com"],
             "phones":["777 123 456"],"address":"*Ulice* 1\nBrno"}
            """));

        Assert.Equal("1. Jana Nováková", ContactFields.Title(data));
        Assert.Equal("1. Jana Nováková · - vedoucí · # Modelsoft · jana@example.com · 777 123 456 · Ulice 1 Brno", ContactFields.PlainText(data));
    }

    [Fact]
    public void Email_keys_are_the_lower_case_addresses_for_finding_a_duplicate()
    {
        Assert.Equal(["jana@example.com", "j.n@firma.example.test"],
            ContactFields.EmailKeys(ContactFields.Create(Json("""{"name":"Jana","emails":["Jana@Example.com","J.N@firma.example.test"]}"""))));
    }

    [Fact]
    public void Aliases_are_one_line_each_once_whatever_the_case_and_diacritics_and_bounded()
    {
        var data = ContactFields.Create(Json("""{"name":"Marek Musil","aliases":[" MMu ","mmu","Mára","Mara  Musil"]}"""));
        Assert.Equal("""["MMu","Mára","Mara Musil"]""", Text(data["aliases"]!));
        Assert.Equal("Marek Musil · MMu · Mára · Mara Musil", ContactFields.PlainText(data));
        Assert.Contains("Zkratky", Assert.Throws<ObjectChangeRejected>(() => ContactFields.Create(Json($$"""{"name":"x","aliases":["{{new string('x', ContactFields.MaxAlias + 1)}}"]}"""))).Message);
        Assert.Throws<ObjectChangeRejected>(() => ContactFields.Create(Json($$"""{"name":"x","aliases":[{{string.Join(",", Enumerable.Range(0, 21).Select(i => $"\"a{i}\""))}}]}""")));
        Assert.Contains("Neznámé pole", Assert.Throws<ObjectChangeRejected>(() => ContactFields.Create(Json("""{"name":"x","aliasPicks":{"mmu":9}}"""))).Message);
        // Removing an alias removes its count; the others keep theirs.
        var (taught, _) = ContactFields.Learn(data, "MMu");
        (taught, _) = ContactFields.Learn(taught, "mára");
        var (edited, change) = ContactFields.Apply(taught, Json("""{"aliases":["Mára"]}"""));
        Assert.Equal("""{"aliases":["Mára"]}""", Text(change));
        Assert.Equal("""{"mara":1}""", Text(edited[ContactFields.AliasPicks]!));
        Assert.Null(ContactFields.Apply(edited, Json("""{"aliases":null}""")).Data[ContactFields.AliasPicks]);
    }

    [Fact]
    public void A_pick_teaches_its_query_as_an_alias_and_counts_it_but_a_plain_prefix_teaches_nothing()
    {
        var martin = ContactFields.Create(Json("""{"name":"Marek Musil","emails":["marek.musil@example.com"]}"""));
        var (once, change) = ContactFields.Learn(martin, " MMu ");
        Assert.Equal("""{"aliases":["MMu"]}""", Text(change));
        var (twice, again) = ContactFields.Learn(once, "mmú");
        Assert.Equal("{}", Text(again));
        Assert.Equal("""{"name":"Marek Musil","emails":["marek.musil@example.com"],"aliases":["MMu"],"aliasPicks":{"mmu":2}}""", Text(twice));
        foreach (var prefix in new[] { "Mar", "mus", "marek mu", "marek.mus", "m" })
        {
            Assert.Throws<ObjectChangeRejected>(() => ContactFields.Learn(martin, prefix));
        }
    }

    [Fact]
    public void A_pick_teaches_nothing_the_contact_matches_anyway_by_any_field_or_in_any_order()
    {
        // The requirement's party picker knows only the name: what else the person is found by is decided here, as the web search matches.
        var jana = ContactFields.Create(Json("""{"name":"Jana Nováková","title":"Projektová _manažerka_","organization":"Modelsoft","department":"Vývoj","emails":["jn@firma.example.test"]}"""));
        foreach (var plain in new[] { "novakova jana", "Proj", "manaž", "models", "vyv", "firma", "JN@", "nov ja" })
        {
            Assert.Throws<ObjectChangeRejected>(() => ContactFields.Learn(jana, plain));
        }
        // Initials and nicknames are no plain match.
        foreach (var alias in new[] { "JNo", "Janička", "Jája" })
        {
            Assert.Equal(alias, ContactFields.Learn(jana, alias).Data["aliases"]![0]!.GetValue<string>());
        }
    }

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static string Text(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
