using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.Vaults.Release;

namespace Atlas.Server.Tests.Vaults;

/// <summary>The WP4 V4 policy as a switchboard: any mode, re-authentication requirement and support; proofs are issued by the test
/// and consumed at most once, bound to the actor, secret and operation.</summary>
internal sealed class FakeReleasePolicy : IReleasePolicy
{
    private static readonly AsyncLocal<FakeReleasePolicy?> s_current = new();
    private readonly ConcurrentDictionary<Guid, (Guid Actor, Guid Secret, string Operation)> m_proofs = new();

    /// <summary>The switchboard of the running test (VaultFixture's release host serves several tests, each with its own).</summary>
    public static FakeReleasePolicy Current =>
        s_current.Value ?? throw new InvalidOperationException("No release policy for this test: create the fixture with VaultFixture.CreateAsync(release: ...).");

    /// <summary>Makes this the policy of the calling test; AsyncLocal, so it flows into the test's invocations only.</summary>
    public void Use() => s_current.Value = this;

    public ReleaseDecision Decision { get; set; } = new("never", "instance", false, true, null);

    public int Evaluations;

    public int Consumptions;

    public Guid IssueProof(UserAccount actor, Guid secretId, string operation)
    {
        var id = Guid.NewGuid();
        m_proofs[id] = (actor.Id, secretId, operation);
        return id;
    }

    public Task<ReleaseDecision> EvaluateAsync(UserAccount actor, Guid secretId, HttpContext http, CancellationToken ct)
    {
        Interlocked.Increment(ref Evaluations);
        return Task.FromResult(Decision);
    }

    public Task<bool> TryConsumeProofAsync(UserAccount actor, HttpContext http, Guid secretId, string operation, Guid proofId, CancellationToken ct)
    {
        Interlocked.Increment(ref Consumptions);
        var consumed = m_proofs.TryRemove(proofId, out var proof);
        return Task.FromResult(consumed && proof == (actor.Id, secretId, operation));
    }

    public static Action<IServiceCollection> Register(FakeReleasePolicy policy, RecordingSecurityEvents events) => services =>
    {
        services.AddSingleton<IReleasePolicy>(policy);
        services.AddSingleton<ISecurityEvents>(events);
    };
}

/// <summary>Records every live event with its recipients and its JSON payload, as SignalR would serialize it.</summary>
internal sealed class RecordingSecurityEvents : ISecurityEvents
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public ConcurrentQueue<(string Name, Guid[] Users, object Payload)> Sent { get; } = new();

    public IEnumerable<string> Payloads => Sent.Select(e => JsonSerializer.Serialize(e.Payload, e.Payload.GetType(), Web));

    public IReadOnlyList<(Guid[] Users, AccessRequestChanged Change)> RequestChanges =>
        Sent.Where(e => e.Name == "accessRequestChanged").Select(e => (e.Users, (AccessRequestChanged)e.Payload)).ToArray();

    public IReadOnlyList<(Guid User, Guid SecretId)> Revocations =>
        Sent.Where(e => e.Name == "secretAccessRevoked").Select(e => (e.Users[0], ((SecretAccessRevoked)e.Payload).SecretId)).ToArray();

    public Task AccessRequestChangedAsync(IReadOnlyCollection<Guid> userIds, AccessRequestChanged change)
    {
        Sent.Enqueue(("accessRequestChanged", userIds.ToArray(), change));
        return Task.CompletedTask;
    }

    public Task SecretAccessRevokedAsync(Guid userId, SecretAccessRevoked revoked)
    {
        Sent.Enqueue(("secretAccessRevoked", [userId], revoked));
        return Task.CompletedTask;
    }
}
