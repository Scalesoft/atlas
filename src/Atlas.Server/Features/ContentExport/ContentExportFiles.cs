using Atlas.Server.Features.ModuleSettings;

namespace Atlas.Server.Features.ContentExport;

/// <summary>Private staging directory for finished export archives; every file self-deletes on close.</summary>
public sealed class ContentExportFiles
{
    private readonly IWebHostEnvironment m_environment;

    public ContentExportFiles(IConfiguration configuration, IWebHostEnvironment environment)
    {
        m_environment = environment;
        Root = Path.GetFullPath(configuration["ContentExport:DataPath"] ?? Path.Combine(environment.ContentRootPath, ".data", "exports-tmp"));
    }

    public string Root { get; }

    public FileStream Create()
    {
        PrivateServerFiles.PrepareDirectory(Root, m_environment);
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip");
        var options = PrivateServerFiles.CreateOptions(FileMode.CreateNew);
        options.Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose;
        options.BufferSize = 65536;
        var stream = new FileStream(path, options);
        try
        {
            PrivateServerFiles.ProtectFile(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
