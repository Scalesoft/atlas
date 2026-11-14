using System.Diagnostics;

namespace Atlas.Server.Tests.Platform;

/// <summary>
/// The SQL statements sent while a recording is open, in order, read from Npgsql's command activities: the source
/// <see cref="AtlasServerTiming"/> counts for the Server-Timing header, here with the statement text.
/// </summary>
/// <remarks>
/// A recording sees only the commands of its own async flow (a message invoked through <see cref="AtlasTestHost"/>), so tests
/// running in parallel do not mix. One entry is one round-trip that carried SQL; a physical connection opened on a cold pool
/// is an activity without query text and is left out. Transaction control (BEGIN, COMMIT) is not a command activity.
/// Recordings nest: one opened inside another does not end it, and the outer one sees the inner one's statements too.
/// </remarks>
internal sealed class SqlRecording : IDisposable
{
    private static readonly AsyncLocal<SqlRecording?> Current = new();
    private static readonly Lazy<ActivityListener> Listener = new(Listen);
    private readonly List<string> m_statements = [];
    private readonly SqlRecording? m_outer;

    private SqlRecording(SqlRecording? outer)
    {
        m_outer = outer;
    }

    public IReadOnlyList<string> Statements
    {
        get
        {
            lock (m_statements)
            {
                return m_statements.ToArray();
            }
        }
    }

    public static SqlRecording Start()
    {
        _ = Listener.Value;
        var recording = new SqlRecording(Current.Value);
        Current.Value = recording;
        return recording;
    }

    public void Dispose()
    {
        Current.Value = m_outer;
    }

    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            // As in AtlasServerTiming: only commands issued while a recording is open become activities at all.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => Current.Value is null ? ActivitySamplingResult.None : ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (Current.Value is { } recording && activity.Kind == ActivityKind.Client && activity.GetTagItem("db.query.text") is string sql)
                {
                    recording.Add(sql);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private void Add(string sql)
    {
        lock (m_statements)
        {
            m_statements.Add(sql);
        }
        m_outer?.Add(sql);
    }
}
