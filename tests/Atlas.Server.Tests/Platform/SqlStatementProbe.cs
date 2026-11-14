using System.Diagnostics;

namespace Atlas.Server.Tests.Platform;

/// <summary>
/// The SQL statements of one HTTP request, for paths the Server-Timing header does not measure (it covers /api only).
/// Statements are Npgsql's command activities, attributed through the execution context like AtlasServerTiming does, so
/// those of other requests, other tests and background work never count. The site's TestServer has to carry the context
/// to the server: set <c>site.Server.PreserveExecutionContext = true</c> before creating the client.
/// </summary>
internal static class SqlStatementProbe
{
    private static readonly AsyncLocal<List<string>?> Current = new();
    private static readonly Lazy<ActivityListener> Listener = new(() =>
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => Current.Value is null ? ActivitySamplingResult.None : ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                // As in AtlasServerTiming: a physical connection open carries no query text and is no statement.
                if (Current.Value is { } statements && activity.Kind == ActivityKind.Client && activity.GetTagItem("db.query.text") is { } text)
                {
                    lock (statements)
                    {
                        statements.Add(text.ToString() ?? "");
                    }
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    });

    public static async Task<(HttpResponseMessage Response, IReadOnlyList<string> Statements)> GetAsync(HttpClient client, string path)
    {
        _ = Listener.Value;
        var statements = new List<string>();
        Current.Value = statements;
        try
        {
            var response = await client.GetAsync(path);
            lock (statements)
            {
                return (response, statements.ToArray());
            }
        }
        finally
        {
            Current.Value = null;
        }
    }
}
