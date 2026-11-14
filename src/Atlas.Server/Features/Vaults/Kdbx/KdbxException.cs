namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// The only exception a KDBX read reports for file content. Messages are fixed guidance texts: they never
/// contain passwords, keys, decrypted XML or field values, and no inner exception is attached, because parser
/// exceptions can quote the decrypted document.
/// </summary>
public sealed class KdbxException : Exception
{
    public KdbxException(KdbxFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public KdbxFailure Failure { get; }

    internal static KdbxException Corrupt() =>
        new(KdbxFailure.Corrupt, "The file is damaged, truncated or not a KeePass database.");

    internal static KdbxException Unsupported(string guidance) => new(KdbxFailure.Unsupported, guidance);

    internal static KdbxException Limit(string what, string option) =>
        new(KdbxFailure.LimitExceeded, $"The database exceeds the permitted {what}. Raise {VaultOptions.Section}:{option} only for a trusted file.");
}
