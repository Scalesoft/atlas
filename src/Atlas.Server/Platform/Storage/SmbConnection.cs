using System.Net.Sockets;
using SMBLibrary;
using SMBLibrary.Client;
using SmbAttributes = SMBLibrary.FileAttributes;

namespace Atlas.Server.Platform.Storage;

/// <summary>
/// One direct TCP SMB2/3 session and tree. SMBLibrary is synchronous, so every call runs on the pool with a
/// timeout; a timed-out or cancelled call poisons the connection.
/// </summary>
internal sealed class SmbConnection : IDisposable
{
    private readonly SMB2Client m_client = new(10000, true);
    private Task? m_pending;
    private bool m_failed;

    public ISMBFileStore Store { get; private set; } = null!;

    public async Task ConnectAsync(string host, string share, FileStorageOptions options, CancellationToken cancellationToken)
    {
        await CallAsync(() =>
        {
            if (!m_client.Connect(host, SMBTransportType.DirectTCPTransport))
            {
                throw new IOException("Cannot connect to the SMB server on TCP port 445. Check its hostname, firewall and SMB2/SMB3 support.");
            }
            return true;
        }, cancellationToken);
        await CallAsync(() => Check(m_client.Login(options.Domain, options.Username, options.Password ?? ""), "authenticate"), cancellationToken);
        Store = await CallAsync(() =>
        {
            var store = m_client.TreeConnect(share, out var status);
            Check(status, "connect to share");
            return store;
        }, cancellationToken);
    }

    public Task<object> OpenAsync(string path, AccessMask access, ShareAccess sharing, CreateDisposition disposition, CancellationToken cancellationToken)
        => CallAsync(() =>
        {
            Check(Store.CreateFile(out var handle, out _, path, access | AccessMask.SYNCHRONIZE,
                SmbAttributes.Normal, sharing, disposition,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null), "open file; ensure its folder exists");
            return handle;
        }, cancellationToken);

    public async Task<T> CallAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (m_failed)
        {
            throw new IOException("The SMB connection is no longer usable. Test the storage before retrying.");
        }
        var task = Task.Run(action, cancellationToken);
        m_pending = task;
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            m_failed = true;
            throw;
        }
        catch (Exception error) when (error is TimeoutException or SocketException or InvalidOperationException)
        {
            m_failed = true;
            throw new IOException("SMB communication failed or timed out. Check the server address, TCP port 445, server health and credentials.");
        }
    }

    public async Task CloseAsync(object handle)
    {
        if (!m_failed)
        {
            await CallAsync(() => Check(Store.CloseFile(handle), "close file"), CancellationToken.None);
        }
    }

    public static bool IsMissing(NTStatus status) =>
        status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE;

    public static bool Check(NTStatus status, string operation)
    {
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new IOException($"Cannot {operation} over SMB ({status}). Check the server, share/folder, credentials, available space and permissions.");
        }
        return true;
    }

    public void Dispose()
    {
        // A timed-out synchronous connect may finish later. Its worker must release the socket then.
        if (m_pending is { IsCompleted: false })
        {
            _ = m_pending.ContinueWith(task =>
            {
                _ = task.Exception;
                Disconnect();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else
        {
            Disconnect();
        }
    }

    private void Disconnect()
    {
        try
        {
            m_client.Disconnect();
        }
        catch (SocketException error)
        {
            System.Diagnostics.Trace.TraceWarning("SMB socket cleanup failed ({0}).", error.SocketErrorCode);
        }
    }
}
