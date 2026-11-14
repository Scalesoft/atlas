using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Contacts.Commands;
using Atlas.Server.Features.Contacts.Persistence;
using Atlas.Server.Features.Entities;
using Xunit;

namespace Atlas.Server.Tests.Contacts;

/// <summary>The one identity of a contact (contacts spec 2026-10-30 §1) and the pure rules around it: conflicts, merged fields, refusals.</summary>
public sealed class ContactIdentityTests
{
    [Theory]
    [InlineData("777 123 456", "+420777123456")]
    [InlineData("+420 777 123 456", "+420777123456")]
    [InlineData("00420 (777) 123-456", "+420777123456")]
    [InlineData("+421 905 123 456", "+421905123456")]
    [InlineData("+49 30 1234567", "+49301234567")]
    [InlineData("777.123.456", "+420777123456")]
    [InlineData("12 34", null)]
    [InlineData("777 123 456 #12", null)]
    [InlineData("077 123 456", null)]
    [InlineData("", null)]
    public void A_phone_is_e164_with_czech_default_or_left_out(string phone, string? expected) => Assert.Equal(expected, EntityIdentity.Phone(phone));

    [Fact]
    public void Keys_come_strong_first_and_the_catalog_key_prefers_a_strong_one_then_the_directory_object()
    {
        var contact = JsonNode.Parse("""
            {"name":"Jan  Kovařík","organization":"AB Klinika","phones":["777 123 456","linka 12"],"emails":["Jan@Example.com"],"directoryId":"D1",
             "internalUser":{"id":"U1"}}
            """)!.AsObject();
        Assert.Equal(["email:jan@example.com:True", "user:u1:True", "directory:D1:True", "phone:+420777123456:False", "name:jan kovarik|ab klinika:False"],
            EntityIdentity.Keys(contact).Select(key => $"{key.Value}:{key.Strong}"));
        Assert.Equal("email:jan@example.com", EntityIdentity.Of(contact));

        var phoneOnly = JsonNode.Parse("""{"name":"Petr","phones":["+420 777 000 111"],"objectId":"0b5c5d4e-1111-2222-3333-444455556666"}""")!.AsObject();
        Assert.Equal("object:0b5c5d4e-1111-2222-3333-444455556666", EntityIdentity.Of(phoneOnly));
        phoneOnly.Remove("objectId");
        Assert.Equal("phone:+420777000111", EntityIdentity.Of(phoneOnly));
        // The name alone is no key (only with the organization), but still the catalog's last resort.
        Assert.Empty(EntityIdentity.Keys(new JsonObject { ["name"] = "Petr" }));
        Assert.Equal("name:petr|", EntityIdentity.Of(new JsonObject { ["name"] = "Petr" }));
    }

    [Fact]
    public void A_conflict_is_a_field_both_have_with_different_values_and_never_a_new_spelling()
    {
        var existing = JsonNode.Parse("""{"name":"Jana Nováková","phones":["+420 777 123 456"],"emails":["jana@example.com"],"organization":"Modelsoft"}""")!.AsObject();
        Assert.Empty(ContactDirectory.Conflicts(existing, JsonNode.Parse("""{"name":"jana novakova","phones":["777123456"],"emails":["JANA@example.com"]}""")!.AsObject()));
        var conflicts = ContactDirectory.Conflicts(existing, JsonNode.Parse("""{"name":"Jana Nová","phones":["602 000 000"],"organization":"Jiná"}""")!.AsObject());
        Assert.Equal(["phones", "name", "organization"], conflicts.Select(conflict => conflict.Field));
        Assert.Equal("Jana Nová", conflicts[1].New!.GetValue<string>());
        // A field the directory lacks is no conflict.
        Assert.Empty(ContactDirectory.Conflicts(new JsonObject { ["name"] = "Jana Nováková" }, JsonNode.Parse("""{"name":"Jana Nováková","title":"PM"}""")!.AsObject()));
    }

    [Fact]
    public void A_merge_fills_the_targets_gaps_and_adds_the_sources_other_addresses_and_phones()
    {
        var target = JsonNode.Parse("""{"name":"Jana","emails":["jana@example.com"],"phones":["+420 777 123 456"]}""")!.AsObject();
        var source = JsonNode.Parse("""{"name":"Jana N.","emails":["JANA@example.com","jana@home.example.test"],"phones":["777123456","602 000 000"],"title":"PM"}""")!.AsObject();
        Assert.Equal(JsonNode.Parse("""{"title":"PM","emails":["jana@example.com","jana@home.example.test"],"phones":["+420 777 123 456","602 000 000"]}"""),
            MergeContactsHandler.Merged(target, source), JsonNode.DeepEquals);
    }

    [Fact]
    public void A_snapshot_the_directory_refuses_names_its_place_field_and_fix_while_a_relative_photo_stays_out()
    {
        var fields = ContactLinker.DirectoryFields(JsonNode.Parse("""{"name":"Jana","photo":"/api/attachments/x/content","role":"PM","kind":"external"}""")!.AsObject(), 0);
        Assert.Equal(JsonNode.Parse("""{"name":"Jana"}"""), fields, JsonNode.DeepEquals);

        var refused = Assert.Throws<ApiException>(() => ContactLinker.DirectoryFields(JsonNode.Parse("""{"name":"Jana","emails":["bez zavináče"]}""")!.AsObject(), 2));
        Assert.Equal(("invalid_contact", 2, "emails"), (refused.Code, refused.Extensions!["index"], refused.Extensions["field"]));
        Assert.Contains("Kontakt č. 3 Jana", refused.Message);
    }

    [Fact]
    public void A_refreshed_snapshot_takes_the_contacts_fields_and_keeps_the_pages_own_keys()
    {
        var snapshot = JsonNode.Parse("""{"name":"Jana","phones":["111"],"role":"PM projektu","kind":"external","objectId":"x"}""")!.AsObject();
        var contact = JsonNode.Parse("""{"name":"Jana Nováková","phones":["+420 777 123 456"],"objectId":"0b5c5d4e-1111-2222-3333-444455556666"}""")!.AsObject();
        Assert.Equal(JsonNode.Parse("""{"name":"Jana Nováková","phones":["+420 777 123 456"],"objectId":"0b5c5d4e-1111-2222-3333-444455556666","kind":"external","role":"PM projektu"}"""),
            ContactLinker.Refreshed(snapshot, contact), JsonNode.DeepEquals);
    }
}
