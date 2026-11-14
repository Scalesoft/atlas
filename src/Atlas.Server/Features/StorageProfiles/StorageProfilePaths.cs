using Atlas.Server.Platform.Storage;

namespace Atlas.Server.Features.StorageProfiles;

public static partial class StorageProfilePaths
{
    public static string NormalizeFolder(string folder)
    {
        if (folder is null) throw new ApiException(400, "Chybí relativní složka.");
        if (folder.Length == 0) return "";
        var normalized = folder.Replace('\\', '/');
        if (normalized.Length > 512 || normalized.Count(c => c == '/') >= 8)
            throw new ApiException(400, "Relativní složka smí mít nejvýše 512 znaků a 8 částí.");
        try
        {
            foreach (var segment in normalized.Split('/')) FileStorages.ValidateName(segment);
        }
        catch (ArgumentException) { throw new ApiException(400, "Složka musí být relativní cesta bez prázdných částí a bez . nebo .."); }
        return normalized;
    }

    public static string NormalizeDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) throw new ApiException(400, "Zadejte cílové úložiště.");
        var value = destination.Trim();
        try
        {
            // Construction validates syntax without opening a connection or creating folders.
            using var storage = FileStorages.Open(new(value, "", "", null));
            if (!value.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            var uri = new Uri(value);
            var parts = uri.AbsolutePath.Trim('/').Split('/').Select(part => Uri.EscapeDataString(Uri.UnescapeDataString(part)));
            return "smb://" + uri.IdnHost.ToLowerInvariant() + "/" + string.Join('/', parts);
        }
        catch (ArgumentException) { throw new ApiException(400, "Použijte absolutní místní složku nebo smb://server/share/složka bez přihlašovacích údajů v cestě."); }
    }

    public static string Destination(string root, string folder)
    {
        var relative = NormalizeFolder(folder);
        return root.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)
            ? root.TrimEnd('/') + (relative.Length == 0 ? "" : "/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString)))
            : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>A profile folder or file as people see it: <c>\\server\share\folder\name.pdf</c> for SMB, the server path otherwise.</summary>
    public static string Display(string root, string folder, string? name = null)
    {
        var location = Destination(root, folder);
        if (!location.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            return name is null ? location : Path.Combine(location, name);
        var uri = new Uri(location);
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString);
        return @"\\" + uri.Host + "\\" + string.Join('\\', name is null ? parts : parts.Append(name));
    }

    internal static string CanonicalRoot(string root)
        => NormalizeDestination(root).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    internal static bool Overlaps(string root, string canonicalRoot)
    {
        var existing = CanonicalRoot(root);
        return existing == canonicalRoot || existing.StartsWith(canonicalRoot + "/", StringComparison.Ordinal)
            || canonicalRoot.StartsWith(existing + "/", StringComparison.Ordinal);
    }

    /// <summary>Files under <paramref name="path"/> really land inside <paramref name="root"/>: links are judged by where they lead, so a
    /// junction above the root (a linked parent folder on a workstation) is fine and a link below it that leads out is refused.</summary>
    internal static void RequireLocalContainment(string root, string path)
    {
        if (!IsWithin(RealPath(path), RealPath(root)))
            throw new ApiException(400, "Složka úložiště nesmí přes symbolický odkaz nebo junction vést mimo kořen úložiště.");
    }

    internal static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, comparison) || path.StartsWith(prefix, comparison);
    }

    /// <summary>Where a local path really leads: each symbolic link or junction on its existing part replaced by its target. A volume
    /// mounted into a folder (a junction to Volume{GUID}) stays, as a Linux mount point does.</summary>
    internal static string RealPath(string path, int links = 0)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var real = Path.GetPathRoot(full)!;
        var parts = full[real.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            real = Path.Combine(real, parts[i]);
            var target = new DirectoryInfo(real).LinkTarget;
            if (target is null || OperatingSystem.IsWindows() && MountedVolume().IsMatch(target)) continue;
            if (links == 40) throw new ApiException(400, "Cesta úložiště vede přes příliš mnoho symbolických odkazů.");
            return RealPath(Path.Combine([Path.GetFullPath(target, Path.GetDirectoryName(real)!), .. parts[(i + 1)..]]), links + 1);
        }
        return real;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\\\\\?\\)?Volume\{[0-9a-fA-F-]{36}\}\\?$")]
    private static partial System.Text.RegularExpressions.Regex MountedVolume();
}
