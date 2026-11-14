using System.Text.Json.Nodes;

namespace Atlas.Server.Features.Contacts.Queries;

// The directory's reads (contacts spec 2026-10-30 §9) as queries: they write nothing, so they commit as reads (MessageNamespaceConventionTests).

/// <summary>What the directory has of a snapshot's person, without a write: the web asks before it inserts.</summary>
public sealed record MatchContact(JsonObject? Contact);

public sealed record LiveContacts(IReadOnlyList<Guid>? Ids);

public sealed record ListContactDuplicates;

/// <summary>The uploaded picture of a contact the actor may read (GET /api/contacts/{id}/photo); <see cref="IfNoneMatch"/> is the
/// request's header, whose current ETag spares reading the bytes.</summary>
public sealed record OpenContactPhoto(Guid ContactId, string? IfNoneMatch = null);

/// <summary>The open suggested pairs of an account and a contact (AccountContacts) for a wiki admin; empty for anyone else.</summary>
public sealed record ListAccountContactSuggestions;
