namespace Atlas.Server.Features.Contacts.Domain;

public static class ContactErrors
{
    /// <summary>The directory has the person already (an address or an account, K1): the existing contact's id, so the client can offer it instead;
    /// none for a contact the actor may not read (<see cref="Persistence.ContactDirectory.ReadableIdAsync"/>).</summary>
    public static ApiException Duplicate(Guid? contactId) => new(409, "Tato osoba už v adresáři je (stejný e-mail nebo účet).", "duplicate_contact", Id(contactId));

    /// <summary>A contact in the trash whose address or account a live contact holds now (a merge target took it, or a new contact): restored,
    /// the directory would have the person twice. The holder's id as for <see cref="Duplicate"/>.</summary>
    public static ApiException RestoreDuplicate(Guid? contactId) => new(409,
        "Kontakt nejde obnovit: jeho e-mail nebo účet už má jiný kontakt v adresáři (třeba ten, do kterého byl sloučen). Odeberte je nejdřív z toho kontaktu.",
        "contact_restore_duplicate", Id(contactId));

    private static Dictionary<string, object?>? Id(Guid? contactId) => contactId is { } id ? new() { ["contactId"] = id } : null;
}
