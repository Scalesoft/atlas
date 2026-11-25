using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Features.Objects.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Persistence;
using Atlas.Server.Platform.Resources;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Contacts;

/// <summary>
/// Every account has one directory contact (owner decision R3, 2026-10-31; audit engine §5 identity): the live wiki or space contact whose
/// <c>directoryId</c> is the account's id, as the editor's account contact stores it, so <c>user:</c> and <c>directory:</c> are one person.
/// Nothing beside the contacts is stored (owner decision: state lives in the data that carries it): the pairs a wiki admin decides are
/// <see cref="Candidates"/>, computed when read. <see cref="SweepAsync"/> takes the active accounts without that contact, the installation's
/// after the deploy and each new one (built-in invitation, first OIDC or Hub sign-in) a moment after it, in the background: exactly one
/// candidate by e-mail is linked; no candidate at all gives the account its own wiki contact; anything else waits for the admin, whose
/// „Není to on“ gives the account its own contact too (<see cref="CreateOwnAsync"/>), so it is never offered again. Two contacts are never
/// merged here (the admin's „Propojit“ does, ResolveAccountContactHandler). A deactivated account keeps its contact and is not matched anew.
/// </summary>
public sealed class AccountContacts(ISession session, IAtlasLocks locks, ContactObjectType contacts, Outbox outbox, ILogger<AccountContacts> logger)
{
    // shortcut: the candidates scan the live contacts by address and name for every account (no index), O(accounts x contacts), and small
    // batches keep the identity lock short; index the folded title and the addresses once the directory grows past ~10 000 contacts.
    public const int SweepBatch = 50;

    /// <summary>A contact is the account's: live, not private, its directoryId the account's id (any case).</summary>
    public const string OwnContact = """
        SELECT o."Id" FROM "ContentResource" o
        WHERE o."TypeId" = 'atlas.contact' AND o."TrashedAt" IS NULL AND o."Scope" <> 'private' AND o."Data"->>'directoryId' IS NOT NULL
          AND lower(o."Data"->>'directoryId') = CAST(a."Id" AS text)
        """;

    /// <summary>The contacts that may be an active account without its contact: live, not private, nobody's (no directoryId, so never
    /// one linked to another account), with the account's e-mail (<c>ByEmail</c>) or its name folded (case, diacritics, spaces).</summary>
    public const string Candidates = $"""
        SELECT a."Id" AS "AccountId", c."Id" AS "ContactId", m."ByEmail"
        FROM "UserAccount" a
        JOIN "ContentResource" c ON c."TypeId" = 'atlas.contact' AND c."TrashedAt" IS NULL AND c."Scope" <> 'private' AND c."Data" IS NOT NULL
          AND c."Data"->>'directoryId' IS NULL
        CROSS JOIN LATERAL (SELECT EXISTS (SELECT 1 FROM jsonb_array_elements_text(COALESCE(c."Data"->'emails', '[]')) e
          WHERE lower(btrim(e)) = lower(btrim(a."Email"))) AS "ByEmail") m
        WHERE a."IsActive" AND NOT EXISTS ({OwnContact})
          AND (m."ByEmail" OR btrim(regexp_replace(lower(public.unaccent(c."Title")), '\s+', ' ', 'g'))
            = btrim(regexp_replace(lower(public.unaccent(a."DisplayName")), '\s+', ' ', 'g')))
        """;

    /// <summary>Links or creates the next accounts' contacts: an account without its contact with exactly one candidate by e-mail, or none at
    /// all (an ambiguous one is the admin's); the accounts it matched, 0 when none is left or the directory refused them all (the job stops there).</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        await locks.AcquireContentSharedAsync(ct);
        await locks.AcquireAdvisoryAsync("atlas.contact:account-sweep", ct);
        var accounts = await session.CreateSQLQuery($"""
                WITH k AS ({Candidates})
                SELECT a."Id", e."ContactId" FROM "UserAccount" a LEFT JOIN k e ON e."AccountId" = a."Id" AND e."ByEmail"
                WHERE a."IsActive" AND NOT EXISTS ({OwnContact})
                  AND (SELECT count(*) FROM k WHERE k."AccountId" = a."Id" AND k."ByEmail") = CASE WHEN e."ContactId" IS NULL THEN 0 ELSE 1 END
                  AND (e."ContactId" IS NOT NULL OR NOT EXISTS (SELECT 1 FROM k WHERE k."AccountId" = a."Id"))
                ORDER BY a."Id" LIMIT :batch
                """)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("ContactId", NHibernateUtil.Guid).SetParameter("batch", SweepBatch).ListAsync<object[]>(ct);
        // The batch's contacts in id order before the directory's lock, which its first link or creation takes for the whole transaction:
        // the order of an edit (its row, then the lock) and of a merge (both rows by id, then the lock); a row locked after it would deadlock.
        foreach (var contact in accounts.Select(row => row[1]).OfType<Guid>().Distinct().Order())
        {
            await session.GetForUpdateAsync<ContentResource>(contact, ct);
        }
        var matched = 0;
        foreach (var row in accounts)
        {
            var account = (await session.GetAsync<UserAccount>((Guid)row[0], ct))!;
            // A refusal (the address another contact's meanwhile, K1; a name the directory refuses) is asked again next sweep.
            if (row[1] is Guid contact ? await LinkAsync(account, contact, ct) : await CreateOwnAsync(account, ct) is not null)
            {
                matched++;
            }
        }
        return matched;
    }

    /// <summary>A wiki contact of the account (no candidate, or the admin's „Není to on“), created by the account; its address stays out
    /// when another contact holds it (one person per address, K1). Null when the directory refuses it.</summary>
    public async Task<Guid?> CreateOwnAsync(UserAccount account, CancellationToken ct)
    {
        var data = new JsonObject { ["name"] = account.DisplayName.Trim(), ["directoryId"] = account.Id.ToString() };
        var email = account.Email?.Trim() ?? "";
        if (email.Length > 0 && !await session.CreateSQLQuery("""
                SELECT EXISTS (SELECT 1 FROM "ContentResource" r WHERE r."TypeId" = :type AND r."TrashedAt" IS NULL AND r."Scope" <> 'private'
                  AND EXISTS (SELECT 1 FROM jsonb_array_elements_text(COALESCE(r."Data"->'emails', '[]')) e WHERE lower(btrim(e)) = :email)) AS held
                """)
            .AddScalar("held", NHibernateUtil.Boolean).SetParameter("type", ContactObjectType.Id).SetParameter("email", email.ToLowerInvariant())
            .UniqueResultAsync<bool>(ct))
        {
            data["emails"] = new JsonArray(email);
        }
        return await CreateAsync(account, data, ct);
    }

    /// <summary>Writes the account into the contact's directoryId, as the account itself (it is that person); false when it is someone's meanwhile.</summary>
    /// <summary>A new account made from a contact (a profile's „Vytvořit účet“): that contact becomes the account's, under the sweep's locks;
    /// false when the contact cannot be (trashed, private, someone's already, refused by the directory).</summary>
    public async Task<bool> LinkNewAsync(UserAccount account, Guid contactId, CancellationToken ct)
    {
        await locks.AcquireContentSharedAsync(ct);
        await locks.AcquireAdvisoryAsync("atlas.contact:account-sweep", ct);
        return await LinkAsync(account, contactId, ct);
    }

    private async Task<bool> LinkAsync(UserAccount account, Guid contactId, CancellationToken ct)
    {
        // The row before the directory's lock (ContactObjectType.ApplyAsync), as every change of a contact takes them; SweepAsync locked it already.
        var envelope = await session.GetForUpdateAsync<ContentResource>(contactId, ct);
        if (envelope is not { TrashedAt: null, Data: { } json } || envelope.Scope == "private" || JsonNode.Parse(json)!["directoryId"] is not null)
        {
            return false;
        }
        var payload = new JsonObject { ["fields"] = new JsonObject { ["directoryId"] = account.Id.ToString() } };
        ObjectWrite write;
        try
        {
            write = await contacts.ApplyAsync(new ObjectChangeContext(envelope.Id, new ObjectActor(account), JsonNode.Parse(json)!.AsObject(), envelope.Scope, envelope.SpaceId),
                payload, ct);
        }
        catch (ApiException)
        {
            // Another contact is the account's or holds the address meanwhile (K1): asked again next sweep.
            return false;
        }
        var now = DateTime.UtcNow;
        envelope.Version++;
        envelope.UpdatedAt = now;
        envelope.Data = ObjectEnvelopes.Serialize(write.Data!);
        outbox.ObjectRevised(envelope.Id, envelope.TypeId);
        await session.SaveAsync(new ObjectChange
        {
            ObjectId = envelope.Id, Version = envelope.Version, ChangeId = Guid.NewGuid(), AuthorId = account.Id, AppliedAt = now, PayloadJson = ObjectEnvelopes.Serialize(payload)
        }, ct);
        await session.FlushAsync(ct);
        return true;
    }

    /// <summary>A wiki contact of the account, created by the account; with only the name when the directory refuses its address.</summary>
    private async Task<Guid?> CreateAsync(UserAccount account, JsonObject data, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        ObjectWrite write;
        try
        {
            write = await contacts.CreateAsync(new ObjectChangeContext(id, new ObjectActor(account), null, ObjectScopes.Wiki, null), data, ct);
        }
        catch (Exception error) when (error is ObjectChangeRejected or ApiException && data.Remove("emails"))
        {
            return await CreateAsync(account, data, ct);
        }
        catch (Exception error) when (error is ObjectChangeRejected or ApiException)
        {
            // A name the directory refuses, or the account's contact created meanwhile: the next sweep asks again.
            logger.LogWarning("The contact of account {AccountId} was not created: {Message}", account.Id, error.Message);
            return null;
        }
        var now = DateTime.UtcNow;
        await session.SaveAsync(new ContentResource
        {
            Id = id, TypeId = ContactObjectType.Id, Scope = ObjectScopes.Wiki, Version = 1, CreatedAt = now, CreatedBy = account.Id, UpdatedAt = now,
            Title = ObjectEnvelopes.Title(write.Title), Data = ObjectEnvelopes.Serialize(write.Data!)
        }, ct);
        outbox.ObjectRevised(id, ContactObjectType.Id);
        await session.SaveAsync(new ObjectChange { ObjectId = id, Version = 1, ChangeId = id, AuthorId = account.Id, AppliedAt = now, PayloadJson = ObjectEnvelopes.Serialize(data) }, ct);
        // The next account's match reads the directory by SQL.
        await session.FlushAsync(ct);
        return id;
    }
}
