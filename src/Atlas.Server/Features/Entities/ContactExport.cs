using System.Text;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Features.Entities.Queries;

namespace Atlas.Server.Features.Entities;

/// <summary>
/// The contacts of an entity query as CSV and vCard 3.0 (plan §14.4): the merged entities the actor reads, with the plain-text
/// projection of their Markdown fields. CSV cells that a spreadsheet would run as a formula start with an apostrophe.
/// </summary>
public static class ContactExport
{
    public static string Csv(EntityList list)
    {
        var csv = new StringBuilder("Jméno,Funkce,Organizace,Oddělení,E-maily,Telefony,Web,Adresa,Výskyty\r\n");
        foreach (var item in list.Items)
        {
            var a = item.Attributes;
            csv.AppendJoin(',', new[]
            {
                Line(a, "name"), Line(a, "title"), Line(a, "organization"), Line(a, "department"), string.Join("; ", List(a, "emails")),
                string.Join("; ", List(a, "phones")), Text(a, "website"), Address(a), string.Join("; ", item.Sources.Select(source => source.Title).Distinct()),
            }.Select(Cell));
            csv.Append("\r\n");
        }
        return csv.ToString();
    }

    public static string VCard(EntityList list) => VCard(list.Items.Select(item => item.Attributes));

    /// <summary>Contacts as their fields (an entity's attributes, a contact block's data, a live contact): one card each.</summary>
    public static string VCard(IEnumerable<JsonObject> contacts)
    {
        var cards = new StringBuilder();
        foreach (var a in contacts)
        {
            cards.Append("BEGIN:VCARD\r\nVERSION:3.0\r\n");
            var name = Line(a, "name");
            cards.Append("FN:").Append(Escape(name)).Append("\r\n");
            cards.Append("N:").Append(Escape(name)).Append(";;;;\r\n");
            if (Line(a, "organization") is { Length: > 0 } organization)
            {
                cards.Append("ORG:").Append(Escape(organization));
                if (Line(a, "department") is { Length: > 0 } department)
                {
                    cards.Append(';').Append(Escape(department));
                }
                cards.Append("\r\n");
            }
            if (Line(a, "title") is { Length: > 0 } title)
            {
                cards.Append("TITLE:").Append(Escape(title)).Append("\r\n");
            }
            foreach (var email in List(a, "emails"))
            {
                cards.Append("EMAIL;TYPE=INTERNET:").Append(Escape(email)).Append("\r\n");
            }
            foreach (var phone in List(a, "phones"))
            {
                cards.Append("TEL:").Append(Escape(phone)).Append("\r\n");
            }
            if (Text(a, "website") is { Length: > 0 } website)
            {
                cards.Append("URL:").Append(Escape(website)).Append("\r\n");
            }
            if (Address(a) is { Length: > 0 } address)
            {
                cards.Append("ADR:;;").Append(Escape(address)).Append(";;;;\r\n");
            }
            cards.Append("END:VCARD\r\n");
        }
        return cards.ToString();
    }

    private static string Text(JsonObject data, string key) => data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";

    private static string Line(JsonObject data, string key) => ContactFields.InlinePlainText(Text(data, key));

    private static string Address(JsonObject data) => string.Join(", ", Markdig.Markdown.ToPlainText(Text(data, "address"))
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static IEnumerable<string> List(JsonObject data, string key)
        => (data[key] as JsonArray ?? []).Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "").Where(text => text.Length > 0);

    private static string Cell(string text)
    {
        if (text.Length > 0 && "=+-@\t\r\n".Contains(text[0]))
        {
            text = "'" + text;
        }
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>RFC 2426 text value: backslash, comma, semicolon and line breaks escaped.</summary>
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;").Replace("\r\n", "\\n").Replace("\n", "\\n");
}
