namespace Atlas.Server.Features.ContentExport.Domain;

/// <summary>Enforces the export ceiling: 256 MiB, 500 articles or 2000 files, whichever comes first.</summary>
public sealed class ExportBudget
{
    public const long MaximumBytes = 256 * 1024 * 1024;

    public const int MaximumPages = 500;

    public const int MaximumFiles = 2000;

    private long m_bytes;

    private int m_files;

    public void AddBytes(long count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        if (count > MaximumBytes - m_bytes)
        {
            throw TooLarge();
        }
        m_bytes += count;
    }

    public void AddFile()
    {
        if (++m_files > MaximumFiles)
        {
            throw TooLarge();
        }
    }

    public static ApiException TooLarge()
    {
        return new ApiException(413, "Export překročil limit 256 MiB, 500 článků nebo 2000 souborů. Vyberte menší prostor nebo jednotlivý článek.");
    }
}
