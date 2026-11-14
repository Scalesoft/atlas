using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts.Contracts;
using Atlas.Server.Features.Contacts.Queries;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Contacts.Commands;

/// <summary>
/// A wiki admin decides a suggested pair of an account and a contact (AccountContacts, owner decision R3): <c>link</c> makes the contact
/// the account's (a contact the account already has is merged into it, MergeContacts, so its uses follow); <c>dismiss</c> („Není to on“)
/// gives the account its own contact, so the decision is in the data and the account is never offered again; <c>restore</c> with that
/// contact's id („Zpět“) trashes it again. Nothing beside the contacts is stored (owner decision).
/// </summary>
public sealed record ResolveAccountContact(Guid AccountId, Guid ContactId, string? Decision);

public sealed class AccountContactSuggestionsHandler(ISession session, IContentAccess access, SpaceRoles roles, RelationNodes nodes)
{
    public const int MaxItems = 200;

    /// <summary>The pairs computed when read (<see cref="AccountContacts.Candidates"/>): of an account with candidates by e-mail those,
    /// else those by name; only contacts the admin may read, nothing for anyone else (only a wiki admin sees them).</summary>
    public async Task<AccountContactSuggestionList> Handle(ListAccountContactSuggestions query, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        if (!await roles.IsWikiAdminAsync(actor, ct))
        {
            return new AccountContactSuggestionList([]);
        }
        var rows = await session.CreateSQLQuery($"""
                SELECT k."AccountId", a."DisplayName", a."Email", a."IsActive", k."ContactId", c."Title", CAST(c."Data" AS text) AS "Data",
                  CASE WHEN k."ByEmail" THEN 'email' ELSE 'name' END AS "Reason", a."Username"
                FROM (SELECT x.*, bool_or(x."ByEmail") OVER (PARTITION BY x."AccountId") AS "AnyEmail" FROM ({AccountContacts.Candidates}) x) k
                JOIN "UserAccount" a ON a."Id" = k."AccountId" JOIN "ContentResource" c ON c."Id" = k."ContactId"
                WHERE k."ByEmail" OR NOT k."AnyEmail"
                ORDER BY a."DisplayName", a."Id", c."Title", c."Id"
                LIMIT :max
                """)
            .AddScalar("AccountId", NHibernateUtil.Guid).AddScalar("DisplayName", NHibernateUtil.String).AddScalar("Email", NHibernateUtil.String)
            .AddScalar("IsActive", NHibernateUtil.Boolean).AddScalar("ContactId", NHibernateUtil.Guid).AddScalar("Title", NHibernateUtil.String)
            .AddScalar("Data", NHibernateUtil.String).AddScalar("Reason", NHibernateUtil.String).AddScalar("Username", NHibernateUtil.String)
            .SetParameter("max", MaxItems).ListAsync<object[]>(ct);
        var (readable, _) = await nodes.ReadAsync(actor, null, rows.Select(row => (Guid)row[4]).Distinct(), [ContactObjectType.Id], ct);
        return new AccountContactSuggestionList(rows.Where(row => readable.TryGetValue((Guid)row[4], out var node) && node.Visible)
            .Select(row => new AccountContactSuggestion((Guid)row[0], (string)row[1], (string?)row[2], (bool)row[3],
                new ContactRef((Guid)row[4], (string?)row[5] ?? "", JsonNode.Parse((string)row[6])!.AsObject()), (string)row[7], null, (string?)row[8]))
            .ToArray());
    }
}

public sealed class ResolveAccountContactHandler(ISession session, IContentAccess access, SpaceRoles roles, ApplyObjectChangeHandler change, MergeContactsHandler merge,
    TrashObjectHandler trash, AccountContacts accounts)
{
    public async Task<ResolvedAccountContact> Handle(ResolveAccountContact command, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        if (!await roles.IsWikiAdminAsync(actor, ct))
        {
            throw new ApiException(403, "Účty s kontakty propojuje jen správce wiki.", "forbidden");
        }
        var decision = command.Decision ?? "link";
        var own = await session.CreateSQLQuery($"""SELECT ({AccountContacts.OwnContact} LIMIT 1) FROM "UserAccount" a WHERE a."Id" = :account""")
            .SetParameter("account", command.AccountId).UniqueResultAsync<Guid?>(ct);
        if (decision == "dismiss")
        {
            // An account with its contact is decided already: no pair of it is offered.
            if (own is not null || await session.GetAsync<UserAccount>(command.AccountId, ct) is not { } account)
            {
                throw Missing();
            }
            var created = await accounts.CreateOwnAsync(account, ct)
                ?? throw new ApiException(409, "Kontakt účtu se nepodařilo založit; zkuste to znovu.", "contact_refused");
            return new ResolvedAccountContact(command.AccountId, command.ContactId, decision, CreatedContactId: created);
        }
        if (decision == "restore")
        {
            // „Zpět“ of „Není to on“: the contact it created goes to the trash, the pairs are offered again.
            if (own != command.ContactId)
            {
                throw Missing();
            }
            await trash.Handle(new TrashObject(command.ContactId), ct);
            return new ResolvedAccountContact(command.AccountId, command.ContactId, decision);
        }
        if (decision != "link")
        {
            throw new ApiException(400, "Rozhodnutí je link, dismiss nebo restore.", "invalid_argument");
        }
        if (await session.GetAsync<UserAccount>(command.AccountId, ct) is null
            || await session.GetAsync<ContentResource>(command.ContactId, ct) is not { TypeId: ContactObjectType.Id, TrashedAt: null, Scope: not ObjectScopes.Private, Data: { } json })
        {
            throw Missing();
        }
        var directoryId = JsonNode.Parse(json)!["directoryId"]?.GetValue<string>();
        if (directoryId is not null && !string.Equals(directoryId, command.AccountId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(409, "Kontakt už patří jinému účtu.", "contact_linked");
        }
        Guid? merged = null;
        if (own is { } ownId && ownId != command.ContactId)
        {
            // The contact the account got meanwhile goes into the chosen one, its directoryId with it, and its uses follow (the merge's rights).
            await merge.Handle(new MergeContacts(ownId, command.ContactId), ct);
            merged = ownId;
        }
        else if (directoryId is null)
        {
            await change.Handle(new ApplyObjectChange(command.ContactId, Guid.NewGuid(), null,
                new JsonObject { ["fields"] = new JsonObject { ["directoryId"] = command.AccountId.ToString() } }), ct);
        }
        return new ResolvedAccountContact(command.AccountId, command.ContactId, "link", merged);
    }

    private static ApiException Missing() => new(404, "Návrh propojení nebyl nalezen.", "not_found");
}
