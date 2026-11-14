using Atlas.Server.Features.Vaults.Domain;

namespace Atlas.Server.Features.Vaults;

/// <summary>
/// The folders of vault locations on the Atlas server: a managed location keeps its files in its own folder under
/// <c>Vaults:DataPath</c> (default <c>.data/vaults</c>), and no local folder may lie inside wwwroot.
/// </summary>
public sealed class VaultFolders
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly string m_webRoot;

    public VaultFolders(IConfiguration configuration, IWebHostEnvironment environment)
    {
        ManagedRoot = Path.GetFullPath(configuration["Vaults:DataPath"] ?? Path.Combine(environment.ContentRootPath, ".data", "vaults"));
        m_webRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot")));
        if (IsPublished(ManagedRoot))
        {
            throw new InvalidOperationException("Vaults:DataPath must be a directory outside wwwroot.");
        }
    }

    public string ManagedRoot { get; }

    public string Managed(Guid locationId) => Path.Combine(ManagedRoot, locationId.ToString("N"));

    /// <summary>The folder a stored location reads and writes: derived for a managed one, as entered otherwise.</summary>
    public string Root(VaultLocation location) =>
        location.Kind == VaultLocation.ManagedKind ? Managed(location.Id) : location.Root;

    /// <summary>True for wwwroot and anything under it, where a KDBX file could be downloaded.</summary>
    public bool IsPublished(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.Equals(m_webRoot, PathComparison) || full.StartsWith(m_webRoot + Path.DirectorySeparatorChar, PathComparison);
    }
}
