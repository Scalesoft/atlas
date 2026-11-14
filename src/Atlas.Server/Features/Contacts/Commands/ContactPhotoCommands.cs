using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts.Contracts;
using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Features.Contacts.Queries;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Contacts.Commands;

/// <summary>Sets a contact's picture (Atlas 3.5 package G): the web's upload, PUT /api/v1/contacts/{id}/photo and atlas_upsert_contact.</summary>
public sealed record SetContactPhoto(Guid ContactId, byte[] Content);

/// <summary>
/// The picture is stored with the contact and its photo field names it, as one change of the contact through the object path, which
/// decides the access (404, 403 for a reader), locks the contact and tells its viewers; whoever may change the contact may change it.
/// Clearing or changing the photo field (an ordinary field change) deletes the picture (ContactObjectType.ApplyAsync).
/// </summary>
public sealed class SetContactPhotoHandler(ISession session, ApplyObjectChangeHandler change)
{
    public async Task<ContactPhotoSet> Handle(SetContactPhoto command, CancellationToken ct)
    {
        var (mimeType, sha256) = ContactPhotos.Validate(command.Content);
        if (await session.QueryOver<ContentResource>().Where(x => x.Id == command.ContactId).Select(x => x.TypeId).SingleOrDefaultAsync<string>(ct) != ContactObjectType.Id)
        {
            throw new ApiException(404, "Kontakt nebyl nalezen.", "contact_missing");
        }
        var url = ContactPhotos.Url(command.ContactId, sha256);
        await change.Handle(new ApplyObjectChange(command.ContactId, Guid.NewGuid(), null, new JsonObject { ["fields"] = new JsonObject { ["photo"] = url } }), ct);
        // The change deleted a previous picture of other bytes: flushed, the new one is a fresh row of the same id.
        await session.FlushAsync(ct);
        var photo = await session.GetAsync<ContactPhoto>(command.ContactId, ct) ?? new ContactPhoto { ContactId = command.ContactId };
        (photo.MimeType, photo.Sha256, photo.Content) = (mimeType, sha256, command.Content);
        await session.SaveOrUpdateAsync(photo, ct);
        return new ContactPhotoSet(command.ContactId, url);
    }
}

/// <summary>The picture for whoever may read the contact; 404 for anyone else, a trashed contact or one whose photo field does not
/// name this picture. The browser's current copy (If-None-Match) is answered without reading the bytes.</summary>
public sealed class OpenContactPhotoHandler(ISession session, IContentAccess access, ContactObjectType contacts)
{
    public async Task<ContactPhotoContent> Handle(OpenContactPhoto query, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        if (await session.GetAsync<ContentResource>(query.ContactId, ct) is not { TypeId: ContactObjectType.Id, TrashedAt: null, Data: { } data }
            || await contacts.AuthorizeAsync(new ObjectActor(actor), query.ContactId, ct) == ObjectAccess.None
            || await session.QueryOver<ContactPhoto>().Where(x => x.ContactId == query.ContactId).Select(x => x.Sha256).SingleOrDefaultAsync<string>(ct) is not { } sha256
            || JsonNode.Parse(data)?["photo"]?.GetValue<string>() != ContactPhotos.Url(query.ContactId, sha256))
        {
            throw new ApiException(404, "Kontakt nemá logo ani fotku.", "not_found");
        }
        if (query.IfNoneMatch?.Contains($"\"{sha256}\"", StringComparison.Ordinal) == true)
        {
            return new ContactPhotoContent("", sha256, null);
        }
        var photo = (await session.GetAsync<ContactPhoto>(query.ContactId, ct))!;
        return new ContactPhotoContent(photo.MimeType, sha256, photo.Content);
    }
}
