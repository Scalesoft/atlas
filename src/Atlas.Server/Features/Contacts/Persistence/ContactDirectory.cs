using System.Text.Json.Nodes;
using Atlas.Server.Features.Entities;
using Atlas.Server.Features.Relations.Domain;
using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Contacts.Persistence;

/// <summary>A directory contact of the same person: a strong key in common (<see cref="Strong"/>) or only weak ones.</summary>
public sealed record ContactCandidate(Guid Id, string Name, JsonObject Data, bool Strong);

/// <summary>A field both have, with different values: never overwritten silently, reported with both.</summary>
public sealed record ContactConflict(string Field, JsonNode? Existing, JsonNode? New);

public sealed record ContactMatch(ContactCandidate? Strong, IReadOnlyList<ContactCandidate> Weak, IReadOnlyList<ContactConflict> Conflicts);

/// <summary>A directory contact as a reader of a reference gets it (contacts spec §4): ok with the live fields, else only its state.</summary>
public sealed record LiveContact(string State, Guid? ContactId = null, JsonObject? Contact = null);

/// <summary>A place a contact is used: a page (working or published revision) or a board.</summary>
public sealed record ContactUse(string Kind, Guid Id, string Title);

/// <summary>
/// The contact directory as one set (contacts spec 2026-10-30 §2, K1): the live contacts of the wiki and of every space, private ones
/// apart. Matches by <see cref="EntityIdentity"/>, follows merges (<c>mergedInto</c> in the merged contact's data), reads contacts and
/// where they are used as an account may see it (RelationNodes, ObjectUsage). Writers serialize on one advisory lock (<see cref="LockAsync"/>).
/// </summary>
public sealed class ContactDirectory(ISession session, IAtlasLocks locks, RelationNodes nodes, ObjectUsage usage)
{
    public const string MergedInto = "mergedInto";

    /// <summary>The fields a directory contact has; the rest of a snapshot (kind, internalUser, username, role) stays on the page.</summary>
    public static readonly string[] Fields = ["name", "title", "organization", "department", "emails", "phones", "website", "address", "photo", "directoryId"];

    private const int MaxHops = 8;

    public Task LockAsync(CancellationToken ct) => locks.AcquireAdvisoryAsync($"{ContactObjectType.Id}:identity", ct);

    /// <summary>
    /// <see cref="MatchUnfilteredAsync"/> as <paramref name="actor"/> may read it (RelationNodes: the space's
    /// role, on MCP and for the assistant no space switched out of AI): a strong match it may read comes first; one it may not read still
    /// links by its id, but carries only the snapshot's own name and no conflicts; weak ones it may not read are left out, then five kept.
    /// </summary>
    public async Task<ContactMatch> MatchAsync(UserAccount actor, JsonObject contact, CancellationToken ct)
    {
        var candidates = await CandidatesAsync(contact, null, ct);
        if (candidates.Count == 0)
        {
            return new ContactMatch(null, [], []);
        }
        var (readable, _) = await nodes.ReadAsync(actor, null, candidates.Select(x => x.Id).ToArray(), [ContactObjectType.Id], ct);
        bool Readable(Guid id) => readable.TryGetValue(id, out var node) && node.Visible;
        // OrderBy is stable: the oldest readable strong match, else the oldest one.
        if (candidates.Where(x => x.Strong).OrderBy(x => !Readable(x.Id)).FirstOrDefault() is { } strong)
        {
            return Readable(strong.Id) ? new ContactMatch(strong, [], Conflicts(strong.Data, contact))
                : new ContactMatch(strong with { Name = Text(contact["name"]), Data = new JsonObject() }, [], []);
        }
        return new ContactMatch(null, candidates.Where(x => Readable(x.Id)).Take(5).ToArray(), []);
    }

    /// <summary>The directory's match unfiltered, for system identity checks (a write, K1; the trash restore; the migration); an answer to an
    /// account uses <see cref="MatchAsync(UserAccount, JsonObject, CancellationToken)"/>. The oldest strong match, else every weak one.</summary>
    public async Task<ContactMatch> MatchUnfilteredAsync(JsonObject contact, Guid? except, CancellationToken ct)
    {
        var candidates = await CandidatesAsync(contact, except, ct);
        return candidates.FirstOrDefault(x => x.Strong) is { } strong ? new ContactMatch(strong, [], Conflicts(strong.Data, contact)) : new ContactMatch(null, candidates, []);
    }

    /// <summary>The live directory contacts sharing a key with <paramref name="contact"/>, those by an address or directory id first, each
    /// part oldest first (of at most 50 rows).</summary>
    private async Task<IReadOnlyList<ContactCandidate>> CandidatesAsync(JsonObject contact, Guid? except, CancellationToken ct)
    {
        var keys = EntityIdentity.Keys(contact);
        if (keys.Count == 0)
        {
            return [];
        }
        // A superset in SQL (addresses, directory ids, the last nine digits of a phone, the name), the identity decided in C#. The strong
        // keys' rows come first, so no number of older weak ones pushes the one K1 refuses out of the limit.
        var rows = await session.CreateSQLQuery("""
                SELECT r."Id", r."Title", CAST(r."Data" AS text) FROM "ContentResource" r
                CROSS JOIN LATERAL (SELECT EXISTS (SELECT 1 FROM jsonb_array_elements_text(COALESCE(r."Data" -> 'emails', '[]')) e WHERE lower(e) IN (:emails))
                    OR COALESCE(r."Data" ->> 'directoryId' IN (:directories), false) AS strong) k
                WHERE r."TypeId" = :type AND r."TrashedAt" IS NULL AND r."Scope" <> 'private' AND r."Data" IS NOT NULL AND r."Id" <> :except
                  AND (k.strong
                    OR EXISTS (SELECT 1 FROM jsonb_array_elements_text(COALESCE(r."Data" -> 'phones', '[]')) p WHERE right(regexp_replace(p, '\D', '', 'g'), 9) IN (:phones))
                    OR btrim(regexp_replace(lower(public.unaccent(r."Title")), '\s+', ' ', 'g')) IN (:names))
                ORDER BY k.strong DESC, r."CreatedAt", r."Id"
                LIMIT 50
                """)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("Title", NHibernateUtil.String).AddScalar("Data", NHibernateUtil.String)
            .SetParameter("type", ContactObjectType.Id).SetParameter("except", except ?? Guid.Empty)
            .SetParameterList("emails", Values(keys, "email:"))
            .SetParameterList("directories", Values(keys, "directory:").Concat(Values(keys, "user:")).ToArray())
            .SetParameterList("phones", Values(keys, "phone:").Select(phone => phone[^Math.Min(9, phone.Length)..]).ToArray())
            .SetParameterList("names", Values(keys, "name:").Select(name => name.Split('|')[0]).ToArray())
            .ListAsync<object[]>(ct);
        var mine = keys.ToDictionary(key => key.Value, key => key.Strong);
        var candidates = new List<ContactCandidate>();
        foreach (var row in rows)
        {
            var data = JsonNode.Parse((string)row[2])!.AsObject();
            // The directory id of an account's contact is the account: a snapshot's internal user is the same person.
            var theirs = EntityIdentity.Keys(data).SelectMany(key => key.Value.StartsWith("directory:", StringComparison.Ordinal)
                ? [key, key with { Value = "user:" + key.Value["directory:".Length..].ToLowerInvariant() }] : new[] { key }).ToArray();
            var shared = theirs.Where(key => mine.ContainsKey(key.Value)).ToArray();
            if (shared.Length == 0)
            {
                continue;
            }
            candidates.Add(new ContactCandidate((Guid)row[0], (string?)row[1] ?? "", data, shared.Any(key => key.Strong && mine[key.Value])));
        }
        return candidates;
    }

    /// <summary>The fields where the directory and a snapshot both have a value and differ: phones and e-mails the directory lacks,
    /// a different name (as identity), organization, function or department.</summary>
    public static IReadOnlyList<ContactConflict> Conflicts(JsonObject existing, JsonObject incoming)
    {
        var conflicts = new List<ContactConflict>();
        foreach (var (field, key) in new (string, Func<string, string?>)[] { ("phones", phone => EntityIdentity.Phone(phone) ?? phone), ("emails", email => email.ToLowerInvariant()) })
        {
            var have = Texts(existing[field]).Select(key).ToHashSet();
            if (have.Count > 0 && Texts(incoming[field]).Select(key).Any(value => !have.Contains(value!)))
            {
                conflicts.Add(new ContactConflict(field, existing[field]?.DeepClone(), incoming[field]?.DeepClone()));
            }
        }
        foreach (var field in new[] { "name", "organization", "title", "department" })
        {
            var (before, after) = (Text(existing[field]), Text(incoming[field]));
            if (before.Length > 0 && after.Length > 0 && EntityIdentity.Normalize(before) != EntityIdentity.Normalize(after))
            {
                conflicts.Add(new ContactConflict(field, before, after));
            }
        }
        return conflicts;
    }

    /// <summary><paramref name="id"/> when <paramref name="actor"/> may read that contact (RelationNodes: on MCP and for the assistant not of a
    /// space switched out of AI), else null: a refusal naming another contact tells no reader of an unreadable one that it exists.</summary>
    public async Task<Guid?> ReadableIdAsync(UserAccount actor, Guid id, CancellationToken ct)
        => (await nodes.ReadAsync(actor, null, [id], [ContactObjectType.Id], ct)).Nodes.TryGetValue(id, out var node) && node.Readable ? id : null;

    /// <summary>The contacts behind <paramref name="ids"/> as <paramref name="actor"/> may see them, each merge followed.</summary>
    public async Task<IReadOnlyDictionary<Guid, LiveContact>> LiveAsync(UserAccount actor, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, LiveContact>();
        var final = ids.Distinct().ToDictionary(id => id, id => id);
        var envelopes = new Dictionary<Guid, ContentResource>();
        // One read per hop level for every id still following a merge (the web asks for up to 100 every 30 s).
        var open = final.Keys.ToList();
        for (var hop = 0; hop < MaxHops && open.Count > 0; hop++)
        {
            var load = open.Select(id => final[id]).Where(id => !envelopes.ContainsKey(id)).Distinct().Cast<object>().ToArray();
            if (load.Length > 0)
            {
                foreach (var found in await session.QueryOver<ContentResource>().Where(x => x.TypeId == ContactObjectType.Id)
                             .WhereRestrictionOn(x => x.Id).IsIn(load).ListAsync(ct))
                {
                    envelopes[found.Id] = found;
                }
            }
            var next = new List<Guid>();
            foreach (var id in open)
            {
                if (envelopes.TryGetValue(final[id], out var envelope) && MergeTarget(envelope) is { } target)
                {
                    final[id] = target;
                    next.Add(id);
                }
            }
            open = next;
        }
        var (readable, _) = await nodes.ReadAsync(actor, null, final.Values.Distinct(), [ContactObjectType.Id], ct);
        foreach (var (id, target) in final)
        {
            result[id] = !envelopes.TryGetValue(target, out var envelope) ? new LiveContact("missing")
                : envelope.TrashedAt is not null ? new LiveContact("trashed", target)
                : !readable.TryGetValue(target, out var node) || !node.Readable ? new LiveContact("hidden", target)
                : new LiveContact("ok", target, Snapshot(envelope));
        }
        return result;
    }

    /// <summary>The contact as an editor's snapshot of it: its fields, the plain-text name of the library and its id.</summary>
    public static JsonObject Snapshot(ContentResource envelope)
    {
        var data = JsonNode.Parse(envelope.Data ?? "{}")!.AsObject();
        var snapshot = new JsonObject();
        foreach (var field in Fields.Where(data.ContainsKey))
        {
            snapshot[field] = data[field]!.DeepClone();
        }
        snapshot["name"] = envelope.Title is { Length: > 0 } title ? title : Text(data["name"]);
        snapshot["objectId"] = envelope.Id.ToString();
        return snapshot;
    }

    public static Guid? MergeTarget(ContentResource envelope)
    {
        return envelope.Data is { } json && JsonNode.Parse(json) is JsonObject data && Text(data[MergedInto]) is { Length: > 0 } target
            && Guid.TryParse(target, out var id) && id != envelope.Id ? id : null;
    }

    /// <summary>The places <paramref name="actor"/> may see where the contact is used (ObjectUsage, drafts included; merges followed by the
    /// relation sources), for the match's and the linker's „used in“. A contact the actor may not read has none to tell.</summary>
    public async Task<IReadOnlyList<ContactUse>> UsedInAsync(UserAccount actor, Guid contactId, CancellationToken ct)
    {
        try
        {
            return (await usage.ReadAsync(actor, contactId, true, ct)).Visible
                .Select(use => new ContactUse(use.Place.TypeId == "atlas.board" ? "board" : "page", use.Place.Id, use.Place.Title)).DistinctBy(use => use.Id)
                .OrderBy(use => use.Title, StringComparer.CurrentCulture).ToArray();
        }
        catch (ApiException error) when (error.Status == 404)
        {
            return [];
        }
    }

    private static string[] Values(IReadOnlyList<IdentityKey> keys, string prefix)
    {
        var values = keys.Where(key => key.Value.StartsWith(prefix, StringComparison.Ordinal)).Select(key => key.Value[prefix.Length..]).ToArray();
        // An empty IN list is invalid SQL; this value is no address, id, phone or name.
        return values.Length > 0 ? values : ["\u0001"];
    }

    private static IEnumerable<string> Texts(JsonNode? node) => node is JsonArray items ? items.Select(Text).Where(text => text.Length > 0) : [];

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";
}
