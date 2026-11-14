using System.Security.Cryptography;
using System.Text;
using Atlas.Server.Features.Vaults.Runtime;
using Wolverine;

namespace Atlas.Server.Features.Vaults.Release;

/// <summary>
/// Releases fields of one secret (WP4 V5, spec 4), in the three-step shape of VaultOperations so no database transaction is held
/// over SMB or key derivation:
/// <list type="number">
/// <item>rate limit per account, then <see cref="BeginSecretRelease"/>: Čte decided afresh, the release policy evaluated; a
/// re-authentication without a proof, or one the sign-in cannot provide, ends here with ReauthRefusal (403);</item>
/// <item>the requested non-empty fields are read from the file on demand in one pass (<see cref="VaultSnapshots.ReadFieldsAsync"/>);
/// an unavailable, locked or broken vault is 409 and nothing stale is served;</item>
/// <item>the gate <see cref="CompleteSecretRelease"/> decides Čte and the policy again, consumes the one proof and commits one audit
/// row per field; only then are the values returned. No committed audit, no value.</item>
/// </list>
/// Every denial on an existing secret is audited too. A value never enters a message, a log line or an event; its bytes are zeroed
/// after the response strings are built.
/// </summary>
public sealed class SecretReleaseService
{
    private readonly IMessageBus m_bus;
    private readonly ICurrentActor m_actor;
    private readonly VaultSnapshots m_snapshots;
    private readonly SecretReleaseThrottle m_throttle;
    private readonly SecretReleaseWatch m_watch;
    private readonly SecretReleaseOptions m_options;

    public SecretReleaseService(IMessageBus bus, ICurrentActor actor, VaultSnapshots snapshots, SecretReleaseThrottle throttle,
        SecretReleaseWatch watch, SecretReleaseOptions options)
    {
        m_bus = bus;
        m_actor = actor;
        m_snapshots = snapshots;
        m_throttle = throttle;
        m_watch = watch;
        m_options = options;
    }

    /// <summary>The values, or the re-authentication challenge (the endpoint answers ReauthRefusal, 403); every other denial is an ApiException.</summary>
    public async Task<(SecretValuesView? Values, ReauthChallenge? Reauth)> ReleaseAsync(Guid secretId, ReleaseSecretRequest request, CancellationToken ct)
    {
        var fields = ReleaseFields.Parse(request.Fields, request.Field);
        var operation = ReleaseFields.RequireOperation(request.Operation);
        var claimed = m_actor.Claimed();
        if (!m_throttle.TryAcquire(claimed.Id))
        {
            throw new ApiException(429, "Příliš mnoho požadavků na zobrazení hesel. Zkuste to za chvíli znovu.", "release_rate_limited");
        }
        var begin = await m_bus.InvokeAsync<ReleaseStep>(new BeginSecretRelease(secretId, fields, operation, request.ProofId, request.PageId), ct);
        if (Denied(begin) is { } challenge)
        {
            return (null, challenge);
        }
        var available = begin.Fields!;
        var read = await m_snapshots.ReadFieldsAsync(begin.Target!, begin.EntryUuid, available.Select(ReleaseFields.Flag).ToArray(), ct);
        try
        {
            if (read.Values is null)
            {
                await m_bus.InvokeAsync(new RecordReleaseFailure(secretId, available, operation, begin.Decision!, request.PageId), ct);
                throw read.State switch
                {
                    VaultRuntimeState.Fresh => new ApiException(409, "Záznam v souboru trezoru už není. Počkejte na obnovení trezoru.", "secret_stale"),
                    VaultRuntimeState.Locked or VaultRuntimeState.KeyChanged => new ApiException(409, "Trezor je zamčený; správce musí zadat hlavní heslo znovu.", "vault_locked"),
                    VaultRuntimeState.Unavailable => new ApiException(409, "Trezor teď není dostupný. Zkuste to později.", "vault_unavailable"),
                    _ => new ApiException(409, "Soubor trezoru nelze přečíst.", "vault_error")
                };
            }
            var gate = await m_bus.InvokeAsync<ReleaseStep>(new CompleteSecretRelease(secretId, available, operation, request.ProofId, request.PageId), ct);
            if (Denied(gate) is { } reauth)
            {
                return (null, reauth);
            }
            m_watch.Released(claimed.Id, secretId);
            var values = available.ToDictionary(name => name, name => Encoding.UTF8.GetString(read.Values[ReleaseFields.Flag(name)]));
            return (new SecretValuesView(values, m_options.ValueSeconds), null);
        }
        finally
        {
            foreach (var bytes in read.Values?.Values ?? [])
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    /// <summary>Null when the step lets the release continue; the re-authentication challenge; throws for the other denials.</summary>
    private static ReauthChallenge? Denied(ReleaseStep step) => step.Outcome switch
    {
        ReleaseOutcome.Continue or ReleaseOutcome.Released => null,
        ReleaseOutcome.ReauthRequired or ReleaseOutcome.Unsupported =>
            new ReauthChallenge(step.Decision!.Mode, step.Decision.ModeSource, step.Decision.FreshAuthSupported),
        ReleaseOutcome.Forbidden => throw new ApiException(403, "K zobrazení hesla potřebujete oprávnění číst heslo. Můžete o něj požádat."),
        ReleaseOutcome.FieldEmpty => throw new ApiException(404, "Vybraná pole záznamu jsou prázdná.", "secret_field_empty"),
        ReleaseOutcome.Unavailable => throw new ApiException(409, "Trezor teď není dostupný. Zkuste to později.", "vault_unavailable"),
        _ => throw new ApiException(404, "Heslo nebylo nalezeno.")
    };
}
