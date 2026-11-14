using NHibernate.Criterion;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.References;

/// <summary>
/// Who may know a password reference (WP4 V6, spec 1.1 §6 "Oddělení editoru a zabezpečení"). A reference is usable in a document
/// and visible in any of its projections only to accounts that may Discover the referenced secret now (the V3 evaluator,
/// <see cref="VaultGrantSnapshot"/>); page rights give none. Decisions for any number of accounts and secrets take a constant
/// number of queries and are remembered for the scope, so a projection over many pages asks once per account and secret.
/// <para>
/// A reference is live while its secret exists, is not removed and its vault is not archived. A dead reference (the KeePass entry
/// was deleted, the vault archived, an unknown id) is Discover for nobody, WikiAdmin included, so it is never shown; but it hides
/// nothing either, so it never makes a page read-only or keeps anyone out of the live session. Only a live reference the account
/// may not Discover does (<see cref="HiddenAsync"/>).
/// </para>
/// </summary>
public sealed class SecretReferenceAccess
{
    public const string UnusableMessage = "Dokument odkazuje na heslo, které v Atlasu nemůžete použít.";
    public const string HiddenMessage = "Stránka obsahuje obsah, ke kterému nemáte přístup. Můžete ji číst, ale ne upravovat.";

    private readonly ISession m_session;
    private readonly VaultGrantSnapshots m_grants;
    private readonly Dictionary<(Guid User, Guid Secret), bool> m_decided = [];
    private readonly Dictionary<Guid, bool> m_live = [];

    public SecretReferenceAccess(ISession session, VaultGrantSnapshots grants)
    {
        m_session = session;
        m_grants = grants;
    }

    /// <summary>For each account, the secrets among <paramref name="secretIds"/> it may Discover now.</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> DiscoverableAsync(IReadOnlyCollection<UserAccount> accounts,
        IReadOnlyCollection<Guid> secretIds, CancellationToken ct)
    {
        var ids = secretIds.Distinct().ToArray();
        var users = accounts.DistinctBy(a => a.Id).ToArray();
        var open = users.SelectMany(u => ids.Where(id => !m_decided.ContainsKey((u.Id, id))).Select(id => (User: u, Secret: id))).ToArray();
        if (open.Length > 0)
        {
            var wanted = open.Select(x => x.Secret).Distinct().ToArray();
            var secrets = (await m_session.QueryOver<VaultSecret>()
                .Where(GrantQueryLimits.InChunks(Projections.Property<VaultSecret>(s => s.Id), wanted))
                .ListAsync(ct)).ToDictionary(s => s.Id);
            var vaultIds = secrets.Values.Select(s => s.VaultId).Distinct().ToArray();
            var vaults = vaultIds.Length == 0 ? new Dictionary<Guid, Vault>() : (await m_session.QueryOver<Vault>()
                .Where(GrantQueryLimits.InChunks(Projections.Property<Vault>(v => v.Id), vaultIds))
                .ListAsync(ct)).ToDictionary(v => v.Id);
            foreach (var secretId in wanted)
            {
                m_live[secretId] = secrets.TryGetValue(secretId, out var secret) && secret.RemovedAt is null
                    && vaults.TryGetValue(secret.VaultId, out var vault) && vault.ArchivedAt is null;
            }
            var snapshots = await m_grants.ForAccountsAsync(open.Select(x => x.User).DistinctBy(u => u.Id).ToArray(), vaults.Values.ToArray(), ct);
            var now = DateTime.UtcNow;
            foreach (var (user, secretId) in open)
            {
                m_decided[(user.Id, secretId)] = secrets.TryGetValue(secretId, out var secret) && vaults.TryGetValue(secret.VaultId, out var vault)
                    && snapshots[user.Id].AuthorizeSecret(vault, secret, VaultAccessLevel.Discover, now).Allowed;
            }
        }
        return users.ToDictionary(u => u.Id, u => (IReadOnlySet<Guid>)ids.Where(id => m_decided[(u.Id, id)]).ToHashSet());
    }

    /// <summary>The live references among <paramref name="secretIds"/> the account may not Discover. <see cref="Guid.Empty"/> (stored
    /// content that breaks the reference contract, see <see cref="ReferencesOf"/>) always counts as hidden: fail closed.</summary>
    public async Task<IReadOnlySet<Guid>> HiddenAsync(UserAccount account, IReadOnlySet<Guid> secretIds, CancellationToken ct)
    {
        if (secretIds.Count == 0)
        {
            return secretIds;
        }
        var visible = (await DiscoverableAsync([account], secretIds, ct))[account.Id];
        return secretIds.Where(id => id == Guid.Empty || (!visible.Contains(id) && m_live[id])).ToHashSet();
    }

    /// <summary>400 unless the author may Discover every one of <paramref name="added"/> (the references new content adds): a
    /// document gains a reference only through someone who may see it, as the picker offers. The message names no secret.</summary>
    public async Task RequireUsableAsync(UserAccount author, IReadOnlySet<Guid> added, CancellationToken ct)
    {
        if (added.Count > 0 && (await DiscoverableAsync([author], added, ct))[author.Id].Count != added.Count)
        {
            throw new ApiException(400, UnusableMessage, "secret_reference_unavailable");
        }
    }

    /// <summary>403 when stored content holds live references the editor may not Discover: the editor was served a copy without
    /// them, so saving it would silently drop what it cannot see. Reading stays possible.</summary>
    public async Task RequireEditableAsync(UserAccount editor, string storedJson, CancellationToken ct)
    {
        if ((await HiddenAsync(editor, ReferencesOf(storedJson), ct)).Count > 0)
        {
            throw new ApiException(403, HiddenMessage, "hidden_secret_references");
        }
    }

    /// <summary>The references of <paramref name="json"/> that <paramref name="previousJson"/> does not already hold.</summary>
    public static IReadOnlySet<Guid> Added(IReadOnlySet<Guid> referenced, string? previousJson)
    {
        return referenced.Count == 0 ? referenced : referenced.Except(ReferencesOf(previousJson)).ToHashSet();
    }

    /// <summary>The copy of stored rich JSON one reader may receive: references it may not Discover (live or dead) are removed with
    /// their secret ids. Hidden tells whether a live one was, which makes the copy read-only. Stored content that breaks the
    /// reference contract loses every reference.</summary>
    public async Task<(string Json, bool Hidden)> ForReaderAsync(UserAccount reader, string json, CancellationToken ct)
    {
        IReadOnlySet<Guid> ids;
        try
        {
            ids = SecretReferenceContent.Collect(json);
        }
        catch (InvalidDataException)
        {
            return (SecretReferenceContent.Strip(json, _ => false, out _), true);
        }
        if (ids.Count == 0)
        {
            return (json, false);
        }
        var visible = (await DiscoverableAsync([reader], ids, ct))[reader.Id];
        if (visible.Count == ids.Count)
        {
            return (json, false);
        }
        return (SecretReferenceContent.Strip(json, visible.Contains, out _), ids.Any(id => !visible.Contains(id) && m_live[id]));
    }

    /// <summary>The copy of a stored Markdown projection one reader may receive: <c>::secret{…}</c>/<c>:secret{…}</c> directives it
    /// may not Discover (live or dead), and any that break the contract, are removed with their secret ids.</summary>
    public async Task<string> MarkdownForReaderAsync(UserAccount reader, string markdown, CancellationToken ct)
    {
        var ids = SecretReferenceMarkdown.Directives(markdown).Where(d => d.SecretId is not null).Select(d => d.SecretId!.Value).ToHashSet();
        var visible = ids.Count == 0 ? (IReadOnlySet<Guid>)ids : (await DiscoverableAsync([reader], ids, ct))[reader.Id];
        return SecretReferenceMarkdown.Strip(markdown, visible.Contains, out _);
    }

    /// <summary>The referenced ids of stored content; content that breaks the contract counts as <see cref="Guid.Empty"/>, a hidden
    /// reference for everyone (fail closed).</summary>
    public static IReadOnlySet<Guid> ReferencesOf(string? storedJson)
    {
        try
        {
            return SecretReferenceContent.Collect(storedJson);
        }
        catch (InvalidDataException)
        {
            return new HashSet<Guid> { Guid.Empty };
        }
    }
}
