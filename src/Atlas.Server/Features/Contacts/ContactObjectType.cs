using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Features.Contacts.Persistence;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Contacts;

/// <summary>
/// The contact (<c>atlas.contact</c>, objects spec §2.2): fields in the envelope's data, changed by <c>{ "fields": { … } }</c>, the
/// last writer winning per field (ContactFields). Access is the envelope's scope; the wiki directory is written by a WikiAdmin and
/// by everyone who writes in a space, since the directory belongs to the people who write the wiki, not only to its
/// administrators, while an account that only reads stays a reader of it too. A new contact with an address the scope already has
/// is refused with that contact (ContactDuplicates). Its projection is the plain text of its fields, one chunk.
/// </summary>
public sealed class ContactObjectType : IObjectType, IObjectChangeHandler, IWikiWriters
{
    public const string Id = "atlas.contact";

    private readonly ISession m_session;
    private readonly ObjectScopeAccess m_scopes;
    private readonly ContactDuplicates m_duplicates;
    private readonly ContactDirectory m_directory;

    public ContactObjectType(ISession session, ObjectScopeAccess scopes, ContactDuplicates duplicates, ContactDirectory directory)
    {
        m_session = session;
        m_scopes = scopes;
        m_duplicates = duplicates;
        m_directory = directory;
    }

    public string TypeId => Id;

    public ObjectEditingModel EditingModel => ObjectEditingModel.Fields;

    public async Task<ObjectAccess> AuthorizeAsync(ObjectActor actor, Guid objectId, CancellationToken ct)
    {
        var access = await m_scopes.AuthorizeAsync(actor, objectId, ct);
        return access == ObjectAccess.Read && (await m_session.GetAsync<ContentResource>(objectId, ct))!.Scope == ObjectScopes.Wiki && await MayWriteWikiAsync(actor, ct)
            ? ObjectAccess.Write
            : access;
    }

    public Task<bool> MayWriteWikiAsync(ObjectActor actor, CancellationToken ct) => m_scopes.IsWikiAdminOrSpaceWriterAsync(actor, ct);

    public async Task<ObjectProjection?> ProjectAsync(Guid objectId, CancellationToken ct)
    {
        if (await m_session.GetAsync<ContentResource>(objectId, ct) is not { TrashedAt: null, Data: { } json } envelope || envelope.TypeId != Id)
        {
            return null;
        }
        var data = JsonNode.Parse(json)!.AsObject();
        var title = ContactFields.Title(data);
        var text = ContactFields.PlainText(data);
        var markdown = string.Join("\n\n", data.Where(field => field.Key is not ("photo" or "directoryId" or ContactFields.AliasPicks or ContactDirectory.MergedInto))
            .Select(field => field.Value is JsonArray list ? string.Join(", ", list.Select(item => item!.GetValue<string>())) : field.Value!.GetValue<string>()));
        return new ObjectProjection(title, markdown, text, [new ObjectChunk(null, title, text)]);
    }

    public async Task<ObjectWrite> CreateAsync(ObjectChangeContext context, JsonObject data, CancellationToken ct)
    {
        var contact = ContactFields.Create(data);
        await RefuseDuplicateAsync(context, contact, ct);
        return new ObjectWrite(contact, ContactFields.Title(contact), contact.DeepClone());
    }

    public async Task<ObjectWrite> ApplyAsync(ObjectChangeContext context, JsonObject payload, CancellationToken ct)
    {
        // A person picked after a search (ContactPicker): the change path's Write access means only who may change the contact teaches
        // it an alias; aliases show on the card to every reader, so a reader's pick must not write them.
        if (payload.Count == 1 && payload["learnAlias"] is JsonValue learned && learned.TryGetValue<string>(out var query))
        {
            var (taught, learnedChange) = ContactFields.Learn(context.Data!, query);
            return new ObjectWrite(taught, ContactFields.Title(taught), new JsonObject { ["fields"] = learnedChange });
        }
        if (payload.Count != 1 || payload["fields"] is not JsonObject fields)
        {
            throw new ObjectChangeRejected("Změna kontaktu nastavuje pole: { \"fields\": { … } }.");
        }
        var (data, change) = ContactFields.Apply(context.Data!, fields, context.ObjectId);
        if (fields.Any(field => field.Key is "emails" or "directoryId"))
        {
            await RefuseDuplicateAsync(context, data, ct);
        }
        // A removed or replaced picture goes at once (privacy: neither served nor in a backup); only the field's own address keeps it.
        if (fields.ContainsKey("photo") && await m_session.GetAsync<ContactPhoto>(context.ObjectId, ct) is { } photo
            && data["photo"]?.GetValue<string>() != ContactPhotos.Url(context.ObjectId, photo.Sha256))
        {
            await m_session.DeleteAsync(photo, ct);
        }
        return new ObjectWrite(data, ContactFields.Title(data), new JsonObject { ["fields"] = change });
    }

    /// <summary>One person once (K1): a contact of the wiki or a space whose address or account another live one of them has is refused with
    /// that contact, on creation and on a change of the address; a private one only within its owner's contacts (ContactDuplicates).</summary>
    private async Task RefuseDuplicateAsync(ObjectChangeContext context, JsonObject contact, CancellationToken ct)
    {
        if (context.Scope == ObjectScopes.Private)
        {
            await m_duplicates.RefuseAsync(context, ContactFields.EmailKeys(contact), ct);
            return;
        }
        await m_directory.LockAsync(ct);
        if ((await m_directory.MatchUnfilteredAsync(contact, context.ObjectId, ct)).Strong is { } existing)
        {
            throw ContactErrors.Duplicate(await m_directory.ReadableIdAsync(context.Actor.Account, existing.Id, ct));
        }
    }
}
