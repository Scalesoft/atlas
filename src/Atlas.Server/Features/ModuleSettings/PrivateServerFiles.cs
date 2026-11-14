using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Hosting;

namespace Atlas.Server.Features.ModuleSettings;

/// <summary>Deliberately static, against spec §7.7: OidcRuntimeLoader and the OIDC registration call it before
/// WebApplication.Build(), when no container exists yet.</summary>
public static class PrivateServerFiles
{
    public static void PrepareDirectory(string directory, IHostEnvironment environment)
    {
        var path = Path.GetFullPath(directory);
        var web = Path.GetFullPath((environment as IWebHostEnvironment)?.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!), comparison)
            || path.Equals(web, comparison)
            || path.StartsWith(Path.TrimEndingDirectorySeparator(web) + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException("Server key storage must be a dedicated private directory outside wwwroot.");
        }
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Server key storage must not be a symbolic link.");
        }
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            ProtectDirectoryWindows(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Server key storage must not be a symbolic link.");
        }
    }

    public static void ProtectFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Server key file must not be a symbolic link.");
        }
        if (OperatingSystem.IsWindows())
        {
            ProtectFileWindows(path);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static FileStreamOptions CreateOptions(FileMode mode)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return options;
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectDirectoryWindows(string path)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var owner in Owners())
        {
            acl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectFileWindows(string path)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var owner in Owners())
        {
            acl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        }
        new FileInfo(path).SetAccessControl(acl);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier[] Owners()
    {
        return
        [
            WindowsIdentity.GetCurrent().User!,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        ];
    }
}
