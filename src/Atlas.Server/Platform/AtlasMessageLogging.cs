using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Atlas.Server.Platform;

/// <summary>
/// Wolverine logs a failed invocation as Error under a logger category equal to the message type
/// (Executor.InvokeAsync, hard-coded level, before any failure rule runs). An ApiException is an
/// expected answer — 401, 404, 409 — so those categories are silenced here. The client still receives
/// the problem body from RequestErrorHandling, which is also the one place that logs an unexpected
/// failure, with the trace identifier and without the message payload.
/// </summary>
public static partial class AtlasMessageLogging
{
    private const string Root = "Atlas.";

    /// <summary>Adds one filter rule per message type of the given assembly.</summary>
    public static ILoggingBuilder SilenceMessageCategories(this ILoggingBuilder logging, Assembly assembly)
    {
        foreach (var category in MessageCategories(assembly))
        {
            logging.AddFilter(category, LogLevel.None);
        }
        return logging;
    }

    /// <summary>
    /// Filter categories match by prefix, so the plain full name of RecordSignIn would also match the
    /// category of ILogger&lt;RecordSignInHandler&gt;. One wildcard makes the rule suffix anchored:
    /// the prefix is "Atlas." and the suffix is the rest of the full name, which no longer type
    /// name can end with. Types that are not messages (helpers in the same namespace) get a rule too;
    /// nothing logs under their category, so the rule is a no-op.
    /// </summary>
    internal static IEnumerable<string> MessageCategories(Assembly assembly)
    {
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.FullName is { } name && !name.EndsWith("Handler", StringComparison.Ordinal) && MessageType().IsMatch(name))
            {
                yield return Root + "*" + name[Root.Length..];
            }
        }
    }

    [GeneratedRegex(@"\AAtlas\.Server\.Features\.[A-Za-z0-9_.]+\.(Commands|Queries)\.[^.]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex MessageType();
}
