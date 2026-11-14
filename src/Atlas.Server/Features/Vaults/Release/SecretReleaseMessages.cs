using Atlas.Server.Features.Vaults.Runtime;

namespace Atlas.Server.Features.Vaults.Release;

// Commands, not queries: every release message may write audit rows, so its transaction commits durably (TransactionIntent).
// Fields are validated API names (ReleaseFields.Parse); every audit is one row per field. PageId is the page the client names; the
// handler keeps it only once it confirmed the page (SecretReleaseHandler.ConfirmedPageAsync).

/// <summary>Step 1 of a release: authorizes Čte afresh, evaluates the release policy and resolves the file. Denials are audited.</summary>
public sealed record BeginSecretRelease(Guid SecretId, IReadOnlyList<string> Fields, string Operation, Guid? ProofId, Guid? PageId = null);

/// <summary>Step 3, the release gate: authorizes Čte and evaluates the policy again, consumes the one proof when one is required
/// and writes the released audit rows in the same transaction; the values are returned only after this commits.</summary>
public sealed record CompleteSecretRelease(Guid SecretId, IReadOnlyList<string> Fields, string Operation, Guid? ProofId, Guid? PageId = null);

/// <summary>Audits a release that failed because the vault file could not be read (step 2).</summary>
public sealed record RecordReleaseFailure(Guid SecretId, IReadOnlyList<string> Fields, string Operation, ReleaseDecision Decision, Guid? PageId = null);

/// <summary>Audits a client report (shown or copied) of a secret the caller may read.</summary>
public sealed record ReportSecretRelease(Guid SecretId, IReadOnlyList<string> Fields, string Operation);

public enum ReleaseOutcome
{
    Continue,
    Released,
    NotFound,
    Forbidden,
    FieldEmpty,
    ReauthRequired,
    Unsupported,
    Unavailable
}

/// <summary>The answer of a release step. Continue carries how to reach the file (Target, with the decrypted SMB password in memory
/// only), the entry and the requested fields that are not empty; the other outcomes end the release and were audited.</summary>
public sealed record ReleaseStep(ReleaseOutcome Outcome, ReleaseDecision? Decision = null, VaultTarget? Target = null, Guid EntryUuid = default,
    IReadOnlyList<string>? Fields = null)
{
    public override string ToString() => $"Release step {Outcome}";
}
