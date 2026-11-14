namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>Shared cleanup for both the periodic expiry sweep and a failed preview.</summary>
public static class ImportSourceFiles
{
    public static void Remove(ContentImportFiles files, ILogger logger, Guid id)
    {
        try
        {
            files.Remove(id);
        }
        catch (IOException)
        {
            logger.LogWarning("Could not remove expired import source {Id}", id);
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove expired import source {Id}", id);
        }
    }
}
