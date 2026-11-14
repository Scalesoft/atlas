using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts.Persistence;

namespace Atlas.Server.Features.Contacts.Contracts;

// The reads and results of the contact directory as one entity (contacts spec 2026-10-30 §9); the writes are in Commands.


public sealed record ResolvedContact(Guid? ContactId, bool Created, JsonObject Contact, IReadOnlyList<ContactWarning> Warnings);

public sealed record ContactRef(Guid ContactId, string Name, JsonObject? Contact = null, IReadOnlyList<ContactUse>? UsedIn = null);

public sealed record ContactMatchResult(ContactRef? Strong, IReadOnlyList<ContactRef> Weak, IReadOnlyList<ContactConflict> Conflicts);

public sealed record LiveContactsResult(IReadOnlyDictionary<Guid, LiveContact> Items);

public sealed record ContactDuplicate(ContactRef A, ContactRef B, string Reason);

public sealed record ContactDuplicateList(IReadOnlyList<ContactDuplicate> Pairs);

public sealed record MergedContacts(Guid ContactId, JsonObject Contact, int MovedCards);

/// <summary>The contact's picture set: <see cref="Photo"/> is its photo field now (ContactPhotos.Url).</summary>
public sealed record ContactPhotoSet(Guid ContactId, string Photo);

/// <summary>OpenAPI-only multipart shape of the picture's upload; the endpoint reads and validates the actual form itself.</summary>
public sealed record ContactPhotoUploadForm(IFormFile File);

/// <summary>The bytes of a contact's picture for its reader; <see cref="Content"/> null when the reader's copy is current (304).</summary>
public sealed record ContactPhotoContent(string MimeType, string Sha256, byte[]? Content);

/// <summary>An account and a directory contact that may be one person: <see cref="Reason"/> <c>name</c> (the same name, the address
/// missing or different) or <c>email</c> (one of several contacts of its address), computed when read. <see cref="OwnContactId"/> is always
/// null now (an account with its contact is never offered); kept for the response shape.</summary>
public sealed record AccountContactSuggestion(Guid AccountId, string AccountName, string? AccountEmail, bool AccountActive, ContactRef Contact, string Reason, Guid? OwnContactId,
    string? AccountUsername = null);

public sealed record AccountContactSuggestionList(IReadOnlyList<AccountContactSuggestion> Items);

/// <summary>A wiki admin's decision on a suggested pair; <see cref="MergedContactId"/> the account's own contact merged into the chosen one,
/// <see cref="CreatedContactId"/> the account's own contact „Není to on“ created (restore with it undoes the dismissal).</summary>
public sealed record ResolvedAccountContact(Guid AccountId, Guid ContactId, string Decision, Guid? MergedContactId = null, Guid? CreatedContactId = null);
