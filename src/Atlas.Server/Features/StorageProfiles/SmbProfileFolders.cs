using Atlas.Server.Platform.Storage;
using SMBLibrary;
using SmbAttributes = SMBLibrary.FileAttributes;

namespace Atlas.Server.Features.StorageProfiles;

internal static class SmbProfileFolders
{
    /// <summary>Checks the folders down to the location for links; with <paramref name="create"/> creates the missing ones beneath the root.</summary>
    public static async Task EnsureAsync(FileStorageOptions options, string root, bool create, CancellationToken ct)
    {
        var destination = new Uri(options.Location);
        var parts = destination.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        var rootParts = new Uri(root).AbsolutePath.Trim('/').Split('/');
        using var connection = new SmbConnection();
        await connection.ConnectAsync(destination.IdnHost, parts[0], options, ct);
        for (var count = 2; count <= parts.Length; count++)
        {
            var path = string.Join('\\', parts.Take(count).Skip(1));
            // The configured root must exist. Only folders selected beneath it may be created.
            var disposition = count <= rootParts.Length || !create ? CreateDisposition.FILE_OPEN : CreateDisposition.FILE_OPEN_IF;
            var handle = await connection.CallAsync(() =>
            {
                var status = connection.Store.CreateFile(out var opened, out _, path,
                    AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SmbAttributes.Directory,
                    ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, disposition,
                    CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_OPEN_REPARSE_POINT | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
                if (!create && SmbConnection.IsMissing(status)) throw new DirectoryNotFoundException("The storage folder does not exist.");
                SmbConnection.Check(status, "open export folder");
                return opened;
            }, ct);
            try
            {
                var attributes = await connection.CallAsync(() =>
                {
                    SmbConnection.Check(connection.Store.GetFileInformation(out var info, handle, FileInformationClass.FileBasicInformation), "check export folder");
                    return ((FileBasicInformation)info).FileAttributes;
                }, ct);
                if ((attributes & SmbAttributes.ReparsePoint) != 0)
                    throw new ApiException(400, "Složka SMB úložiště nesmí procházet symbolickým odkazem.");
            }
            finally { await connection.CloseAsync(handle); }
        }
    }
}
