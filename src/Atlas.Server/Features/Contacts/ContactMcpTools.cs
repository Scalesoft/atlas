using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;
using Atlas.Server.Features.Contacts.Commands;
using Atlas.Server.Features.Contacts.Contracts;
using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Features.Contacts.Queries;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Mcp.Tools;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Queries;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Queries;

namespace Atlas.Server.Features.Contacts;

/// <summary>
/// The contact directory over MCP (contacts spec 2026-10-30 §2, §5; AI parity): the web's link and merge as the token owner. The directory
/// changes for its readers at once, so both need the write scope; the merge only after a person confirmed it (<c>confirm</c>). Not a
/// Wolverine handler: the class name does not end with "Handler".
/// </summary>
[McpServerToolType]
public sealed class ContactMcpTools(IMessageBus bus)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "atlas_upsert_contact", Title = "Najít nebo založit kontakt", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Vrátí contactId osoby z adresáře podle e-mailu či účtu, jinak ji založí; s contactId změní uvedená pole. contact, contactId?.")]
    [Description(McpDraftTools.ContactRule + " Najde osobu v adresáři wiki a prostorů podle e-mailu nebo účtu a vrátí její contactId (created: false); " +
        "bez shody kontakt založí ve wiki (created: true). Shoda jen podle telefonu nebo jména s organizací kontakt založí a vrátí varování possible_duplicate " +
        "s id kandidáta; rozdílná pole existujícího kontaktu vrátí jako contact_conflict a nepřepíše je. Pole změníte výslovně: contactId a pole v contact. " +
        "contactId pak použijte v objectId vizitky (```a-contact) nebo zmínky a v kartě tabule (atlas_edit_board). Adresář se čtenářům změní hned. " +
        "Logo firmy nebo fotku osoby (malý obrázek v čipu před jménem) nahrajete s contactId v photoBase64 a photoSha256: PNG, JPEG nebo WebP, nejvýše 64 KB, " +
        "stačí čtverec 64 × 64 px; pole photo pak ukazuje na obrázek. photo: null obrázek odebere. Skutečné logo firmy nevymýšlejte ani nekreslete.")]
    public async Task<CallToolResult> UpsertAsync(
        [Description("Kontakt: {name, title?, organization?, department?, emails?: [], phones?: [], website?, address?, aliases?: [] (zkratky a přezdívky, jen s contactId)}.")] JsonObject contact,
        [Description("Volitelně contactId existujícího kontaktu: uvedená pole se v něm nastaví (null pole vymaže).")] string? contactId = null,
        [Description("Volitelně logo nebo fotka kontaktu jako standardní base64 s doplněním (=), bez mezer; PNG, JPEG nebo WebP do 64 KB.")] string? photoBase64 = null,
        [Description("S photoBase64 povinné: SHA-256 původních bajtů obrázku v hex zápisu (64 znaků). Neshoda = nic se neuloží.")] string? photoSha256 = null,
        CancellationToken ct = default)
    {
        var id = McpArguments.OptionalId(contactId, "contactId");
        byte[]? photo = null;
        if (photoBase64 is not null)
        {
            // Before any write: the contact named, the base64, the hash a model's retyping may have broken, and the image itself.
            var buffer = new byte[ContactPhotos.MaximumBytes];
            if (id is null || !Convert.TryFromBase64String(photoBase64, buffer, out var written))
            {
                return McpErrors.InvalidArgument("photoBase64 patří ke contactId existujícího kontaktu a je standardní base64 obrázku do 64 KB.");
            }
            photo = buffer[..written];
            if (photoSha256 is not { Length: 64 } || !Convert.ToHexStringLower(SHA256.HashData(photo)).Equals(photoSha256, StringComparison.OrdinalIgnoreCase))
            {
                return McpErrors.Error(400, "hash_mismatch", "photoSha256 musí být SHA-256 (64 hex znaků) přenesených bajtů obrázku; neshoduje se, nic se neuložilo.");
            }
            ContactPhotos.Validate(photo);
        }
        if (id is { } existing)
        {
            // Only a contact the owner may read here (G5: none of a space switched out of AI), as the merge's.
            if ((await bus.InvokeAsync<LiveContactsResult>(new LiveContacts([existing]), ct)).Items[existing] is not { State: "ok" } live || live.ContactId != existing)
            {
                return McpErrors.Error(404, "contact_missing", "Kontakt nebyl nalezen.");
            }
            var fields = new JsonObject(contact.Where(field => Persistence.ContactDirectory.Fields.Contains(field.Key) || field.Key == "aliases")
                .Select(field => KeyValuePair.Create(field.Key, field.Value?.DeepClone())));
            if (fields.Count > 0)
            {
                await bus.InvokeAsync<Objects.Contracts.ObjectChangeResult>(new ApplyObjectChange(existing, Guid.NewGuid(), null, new JsonObject { ["fields"] = fields }), ct);
            }
        }
        var resolved = await bus.InvokeAsync<ResolvedContact>(new ResolveContact(contact, id), ct);
        if (photo is not null && resolved.ContactId is { } photographed)
        {
            resolved = resolved with { Contact = resolved.Contact.DeepClone().AsObject() };
            resolved.Contact["photo"] = (await bus.InvokeAsync<ContactPhotoSet>(new SetContactPhoto(photographed, photo), ct)).Photo;
        }
        var text = new StringBuilder(resolved.Created ? "Kontakt založen v adresáři." : id is null ? "Kontakt už v adresáři je, použijte ho." : "Kontakt upraven.");
        text.Append("\n- contactId: ").Append(resolved.ContactId).Append("\n- created: ").Append(resolved.Created ? "true" : "false").Append('\n');
        if (photo is not null)
        {
            text.Append("- photo: ").Append(resolved.Contact["photo"]?.ToString()).Append('\n');
        }
        foreach (var warning in resolved.Warnings)
        {
            text.Append("Upozornění: ").Append(McpText.Line(warning.Message, 400)).Append('\n');
        }
        return McpText.Result(text.ToString(), new JsonObject
        {
            ["contactId"] = resolved.ContactId, ["created"] = resolved.Created, ["contact"] = resolved.Contact.DeepClone(),
            ["warnings"] = JsonSerializer.SerializeToNode(resolved.Warnings, Json)
        });
    }

    [McpServerTool(Name = "atlas_get_contact", Title = "Kontakt z adresáře", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true)]
    [McpToolScope(ApiScopes.Read)]
    [Description("Přečte kontakt adresáře podle contactId (z atlas_list_entities, atlas_upsert_contact nebo atlas_list_contact_duplicates): všechna pole " +
        "(name, title, organization, department, emails, phones, website, address, aliases: zkratky a přezdívky, podle kterých ho lidé hledají, photo: adresa loga nebo fotky) a na kolika místech se používá (usageCount: stránky včetně konceptů, tabule, strom). " +
        "Sloučený kontakt vrátí jako kontakt, do kterého byl sloučen (contactId ve výsledku). Kontakt v koši, smazaný nebo v prostoru vyloučeném z AI je contact_missing. " + McpText.TrustNotice)]
    public async Task<CallToolResult> GetAsync([Description("contactId kontaktu adresáře.")] string contactId, CancellationToken ct = default)
    {
        var id = McpArguments.RequiredId(contactId, "contactId");
        // The same read as the merge's and the upsert's: only a contact the owner may read here (G5), a merged one as its target.
        if (!(await bus.InvokeAsync<LiveContactsResult>(new LiveContacts([id]), ct)).Items.TryGetValue(id, out var live) || live is not { State: "ok", ContactId: { } resolved, Contact: { } contact })
        {
            return McpErrors.Error(404, "contact_missing", "Kontakt nebyl nalezen.");
        }
        var usage = await bus.InvokeAsync<UsageCount>(new CountObjectUsage(resolved), ct);
        // The aliases are the directory's own, never in a page's snapshot: read from the object.
        contact = contact.DeepClone().AsObject();
        if ((await bus.InvokeAsync<ObjectDto>(new GetObject(resolved), ct)).Data?["aliases"] is JsonArray aliases)
        {
            contact["aliases"] = aliases.DeepClone();
        }
        var body = new StringBuilder();
        foreach (var field in new[] { "name", "title", "organization", "department", "emails", "phones", "website", "address", "aliases", "photo" })
        {
            if (contact[field] is { } value && (value is not JsonArray list || list.Count > 0))
            {
                body.Append("- ").Append(field).Append(": ").Append(McpText.Line(value is JsonArray items ? string.Join(", ", items.Select(item => item?.ToString())) : value.ToString(), 500)).Append('\n');
            }
        }
        body.Append("- contactId: ").Append(resolved).Append("\n- usageCount: ").Append(usage.Total).Append('\n');
        return McpText.Result(McpText.Wrap($"Kontakt {McpText.Line(contact["name"]?.ToString(), 200)}.", body.ToString()),
            new JsonObject { ["contactId"] = resolved, ["contact"] = contact.DeepClone(), ["usageCount"] = usage.Total });
    }

    [McpServerTool(Name = "atlas_list_contact_duplicates", Title = "Možné duplicity v adresáři", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true)]
    [McpToolScope(ApiScopes.Read)]
    [Description("Vypíše dvojice kontaktů adresáře wiki a prostorů, které smíte číst a mohou být jedna osoba (stránka Možné duplicity v adresáři), nejvýš 200 (při dosažení limitu je výsledek truncated: true a může jich být víc): " +
        "reason phone (stejný telefon), name (jméno s organizací) nebo similar (jen jméno). Dvojici nejdřív porovnejte náhledem atlas_merge_contacts (bez confirm, nic nemění), " +
        "sloučí ji až atlas_merge_contacts s confirm: true po potvrzení člověka. Správci wiki vrátí i accounts: účty, které mohou být kontaktem adresáře " +
        "(účty bez kontaktu adresáře; reason email: kontakt s e-mailem účtu, jinak name: stejné jméno); " +
        "rozhodne je atlas_merge_contacts s accountId. " + McpText.TrustNotice)]
    public async Task<CallToolResult> ListDuplicatesAsync(CancellationToken ct = default)
    {
        var pairs = (await bus.InvokeAsync<ContactDuplicateList>(new ListContactDuplicates(), ct)).Pairs;
        var accounts = (await bus.InvokeAsync<AccountContactSuggestionList>(new ListAccountContactSuggestions(), ct)).Items;
        var body = new StringBuilder();
        foreach (var pair in pairs)
        {
            body.Append("- ").Append(McpText.Line(pair.A.Name, 200)).Append(" (").Append(pair.A.ContactId).Append(") a ")
                .Append(McpText.Line(pair.B.Name, 200)).Append(" (").Append(pair.B.ContactId).Append("), ").Append(pair.Reason).Append('\n');
        }
        foreach (var item in accounts)
        {
            body.Append("- účet ").Append(McpText.Line(item.AccountName, 200)).Append(" (accountId ").Append(item.AccountId).Append(") a kontakt ")
                .Append(McpText.Line(item.Contact.Name, 200)).Append(" (").Append(item.Contact.ContactId).Append("), ").Append(item.Reason).Append('\n');
        }
        // The handlers stop at their caps: reaching one means there may be more (they do not look further to say how many).
        var truncated = pairs.Count >= Commands.ContactDuplicatesHandler.MaxPairs || accounts.Count >= Commands.AccountContactSuggestionsHandler.MaxItems;
        return McpText.Result(McpText.Wrap($"Možné duplicity v adresáři: {pairs.Count}, účty k propojení: {accounts.Count}{(truncated ? " (truncated: limit je dosažen, může jich být víc)" : "")}.",
                pairs.Count + accounts.Count == 0 ? "Žádné možné duplicity." : body.ToString()),
            new JsonObject { ["pairs"] = JsonSerializer.SerializeToNode(pairs, Json), ["accounts"] = JsonSerializer.SerializeToNode(accounts, Json), ["truncated"] = truncated });
    }

    [McpServerTool(Name = "atlas_merge_contacts", Title = "Sloučit dva kontakty", Destructive = true, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Sloučí kontakt sourceId do targetId; bez confirm: true jen náhled. confirm nastavte až po potvrzení člověka.")]
    [Description("Sloučí dva kontakty téže osoby: cílový (targetId) doplní chybějící pole a další e-maily a telefony zdrojového (sourceId), zdrojový jde " +
        "do koše objektů a odkazy na něj (vizitky, zmínky, karty tabulí) ukážou cílový. Bez confirm: true vrátí jen náhled polí a nic nezmění; " +
        "confirm: true nastavte, až sloučení potvrdí člověk. Kandidáty vrací varování possible_duplicate a stránka Možné duplicity v adresáři. " +
        "Smí, kdo smí měnit oba kontakty. S accountId místo sourceId rozhodne správce wiki účet z accounts atlas_list_contact_duplicates: confirm: true " +
        "propojí účet s kontaktem targetId (kontakt, který účet už má, se do něj sloučí), dismiss: true (není to on) založí účtu vlastní kontakt, takže se už nenabídne.")]
    public async Task<CallToolResult> MergeAsync(
        [Description("contactId kontaktu, který zanikne (bez accountId povinné).")] string? sourceId = null,
        [Description("contactId kontaktu, který zůstane.")] string? targetId = null,
        [Description("true až po potvrzení člověka; jinak jen náhled.")] bool confirm = false,
        [Description("Volitelně accountId účtu z accounts, který se s kontaktem targetId propojí (jen správce wiki).")] string? accountId = null,
        [Description("S accountId: true odmítne návrh (není to tatáž osoba): účet dostane vlastní kontakt a znovu se nenabídne.")] bool dismiss = false,
        CancellationToken ct = default)
    {
        if (McpArguments.OptionalId(accountId, "accountId") is { } account)
        {
            var contact = McpArguments.RequiredId(targetId, "targetId");
            if (!confirm && !dismiss)
            {
                return McpText.Result("Náhled, nic se nezměnilo. Po potvrzení člověka zavolejte znovu s confirm: true (propojit), nebo s dismiss: true (není to on).",
                    new JsonObject { ["confirmed"] = false, ["accountId"] = account, ["targetId"] = contact });
            }
            var resolved = await bus.InvokeAsync<ResolvedAccountContact>(new ResolveAccountContact(account, contact, dismiss ? "dismiss" : "link"), ct);
            return McpText.Result(dismiss ? $"Návrh odmítnut: účet má vlastní kontakt {resolved.CreatedContactId} a znovu se nenabídne."
                    : $"Účet propojen s kontaktem.\n- contactId: {resolved.ContactId}\n{(resolved.MergedContactId is { } own ? $"- sloučený kontakt účtu: {own}\n" : "")}",
                JsonSerializer.SerializeToNode(resolved, Json)!.AsObject());
        }
        var (source, target) = (McpArguments.RequiredId(sourceId, "sourceId"), McpArguments.RequiredId(targetId, "targetId"));
        if (!confirm)
        {
            var live = await bus.InvokeAsync<LiveContactsResult>(new LiveContacts([source, target]), ct);
            if (live.Items.Values.Any(item => item.State != "ok"))
            {
                return McpErrors.Error(404, "contact_missing", "Kontakt nebyl nalezen.");
            }
            var fields = Commands.MergeContactsHandler.Merged(live.Items[target].Contact!, live.Items[source].Contact!);
            return McpText.Result($"Náhled sloučení, nic se nezměnilo. Cílový kontakt doplní pole: {(fields.Count == 0 ? "žádná" : string.Join(", ", fields.Select(field => field.Key)))}. " +
                "Po potvrzení člověka zavolejte znovu s confirm: true.",
                new JsonObject { ["confirmed"] = false, ["targetId"] = target, ["sourceId"] = source, ["fields"] = fields });
        }
        var merged = await bus.InvokeAsync<MergedContacts>(new MergeContacts(source, target), ct);
        return McpText.Result($"Kontakty sloučeny.\n- contactId: {merged.ContactId}\n- přesunuté karty tabulí: {merged.MovedCards}\n",
            new JsonObject { ["confirmed"] = true, ["contactId"] = merged.ContactId, ["contact"] = merged.Contact.DeepClone(), ["movedCards"] = merged.MovedCards });
    }
}
