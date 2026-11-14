using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Atlas.Server.Features.Auth.Recovery;

/// <summary>
/// The two capability files on the server disk. Owner-only permissions, atomic publication and a
/// hard refusal to live inside the public web directory; a singleton because it owns no request state.
/// </summary>
public sealed class ServerTokenFiles
{
    private readonly string m_webRoot;

    public ServerTokenFiles(IConfiguration configuration, IHostEnvironment environment)
    {
        DirectoryPath = Path.GetFullPath(configuration["Security:DataPath"] ?? Path.Combine(environment.ContentRootPath, ".data", "security"));
        m_webRoot = Path.GetFullPath((environment as IWebHostEnvironment)?.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
    }

    public string DirectoryPath { get; }

    public string BootstrapPath => Path.Combine(DirectoryPath, "bootstrap-token.txt");

    public string RecoveryPath => Path.Combine(DirectoryPath, "administrator-recovery.json");

    public async Task<string> BootstrapAsync(string? configured, CancellationToken ct)
    {
        PrepareDirectory();
        if (!string.IsNullOrEmpty(configured))
        {
            if (configured.Length < 32)
            {
                throw new ApiException(503, "Setup:BootstrapToken musí mít alespoň 32 znaků.");
            }
            await WriteAtomicAsync(BootstrapPath, configured + Environment.NewLine, ct);
            return configured;
        }
        if (File.Exists(BootstrapPath))
        {
            return await ReadBootstrapAsync(ct);
        }
        var token = ServerAccessTokens.GenerateToken();
        try
        {
            // Publish only a complete, permission-restricted file. Competing processes read
            // the winning file; none can observe a half-written bootstrap capability.
            await WriteAtomicAsync(BootstrapPath, token + Environment.NewLine, ct, overwrite: false);
            return token;
        }
        catch (IOException) when (File.Exists(BootstrapPath))
        {
            return await ReadBootstrapAsync(ct);
        }
    }

    public async Task<RecoveryTokenFile?> ReadRecoveryAsync(CancellationToken ct)
    {
        if (!File.Exists(RecoveryPath))
        {
            return null;
        }
        PrepareDirectory();
        ProtectFile(RecoveryPath);
        try
        {
            return JsonSerializer.Deserialize<RecoveryTokenFile>(await File.ReadAllTextAsync(RecoveryPath, ct));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task WriteRecoveryAsync(RecoveryTokenFile value, CancellationToken ct)
    {
        return WriteAtomicAsync(RecoveryPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    private async Task<string> ReadBootstrapAsync(CancellationToken ct)
    {
        ProtectFile(BootstrapPath);
        var token = (await File.ReadAllTextAsync(BootstrapPath, ct)).Trim();
        if (token.Length < 32)
        {
            throw new ApiException(503, "Instalační soubor tokenu je neplatný. Správce jej musí opravit nebo odstranit a restartovat server.");
        }
        return token;
    }

    private async Task WriteAtomicAsync(string target, string text, CancellationToken ct, bool overwrite = true)
    {
        PrepareDirectory();
        var temporary = Path.Combine(DirectoryPath, ".token-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = new FileStream(temporary, CreateOptions(FileMode.CreateNew)))
            {
                await file.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
                await file.FlushAsync(ct);
            }
            ProtectFile(temporary);
            File.Move(temporary, target, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void PrepareDirectory()
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(DirectoryPath, m_webRoot, comparison) || DirectoryPath.StartsWith(Path.TrimEndingDirectorySeparator(m_webRoot) + Path.DirectorySeparatorChar, comparison))
        {
            throw new ApiException(503, "Security:DataPath nesmí být uvnitř veřejného webového adresáře.");
        }
        if (string.Equals(Path.TrimEndingDirectorySeparator(DirectoryPath), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(DirectoryPath)!), StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(503, "Security:DataPath musí být samostatný adresář pro serverové tokeny, ne kořen disku.");
        }
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(DirectoryPath);
            ProtectWindowsDirectory(DirectoryPath);
        }
        else
        {
            Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static FileStreamOptions CreateOptions(FileMode mode)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return options;
    }

    private static void ProtectFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            ProtectWindowsFile(path);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectWindowsDirectory(string path)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var identity in WindowsOwners())
        {
            acl.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectWindowsFile(string path)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var identity in WindowsOwners())
        {
            acl.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        }
        new FileInfo(path).SetAccessControl(acl);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier[] WindowsOwners()
    {
        return [WindowsIdentity.GetCurrent().User!, new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null)];
    }
}
