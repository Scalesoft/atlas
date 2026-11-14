namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// A current (non-history) KeePass entry. The title is metadata; user name, password, URL and notes are
/// decrypted values that <see cref="Dispose"/> zeroes.
/// </summary>
public sealed class KdbxEntry : IDisposable
{
    internal KdbxEntry(Guid uuid, string title, DateTime lastModificationTime,
        KdbxProtectedValue userName, KdbxProtectedValue password, KdbxProtectedValue url, KdbxProtectedValue notes)
    {
        Uuid = uuid;
        Title = title;
        LastModificationTime = lastModificationTime;
        UserName = userName;
        Password = password;
        Url = url;
        Notes = notes;
    }

    public Guid Uuid { get; }

    public string Title { get; }

    /// <summary>Group names from below the root group down to the entry's group; empty for root entries.</summary>
    public IReadOnlyList<string> GroupPath { get; internal set; } = [];

    public bool InRecycleBin { get; internal set; }

    /// <summary>UTC, whole seconds as stored by KDBX 4.</summary>
    public DateTime LastModificationTime { get; }

    public KdbxProtectedValue UserName { get; }

    public KdbxProtectedValue Password { get; }

    public KdbxProtectedValue Url { get; }

    public KdbxProtectedValue Notes { get; }

    public void Dispose()
    {
        UserName.Dispose();
        Password.Dispose();
        Url.Dispose();
        Notes.Dispose();
    }

    public override string ToString() => $"KeePass entry {Uuid:N}";
}
