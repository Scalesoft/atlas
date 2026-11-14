namespace Atlas.Server.Platform.Persistence;

/// <summary>How often the startup initializer retries while the database is unreachable.</summary>
public sealed class AtlasDatabaseInitializerOptions
{
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);
}
