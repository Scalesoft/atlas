using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Atlas.YouTrack;

public sealed partial class YouTrackClient
{
    private const string IssueFields = "id,idReadable,summary,project(id),customFields($type,name,value(name))";
    private readonly HttpClient m_http;
    private readonly IOptions<YouTrackOptions> m_configuration;

    public YouTrackClient(HttpClient http, IOptions<YouTrackOptions> configuration)
    {
        m_http = http;
        m_configuration = configuration;
    }

    public Task<IReadOnlyList<YouTrackIssue>> SearchAsync(Guid spaceId, string query, CancellationToken cancellationToken = default)
    {
        return SearchAsync(m_configuration.Value, spaceId, query, cancellationToken);
    }

    public Task<IReadOnlyList<YouTrackIssue>> SearchAsync(YouTrackOptions options, Guid spaceId, string query, CancellationToken cancellationToken = default)
    {
        return SearchAsync(options, spaceId, query, ct => ProjectKeysAsync(options, spaceId, ct), cancellationToken);
    }

    // The host checks current wiki authorization before AND after these remote calls.
    // A user query is not an ACL: always filter returned stable project IDs.
    /// <param name="projectKeys">The allowed projects' short names (<see cref="ProjectKeysAsync"/>), asked for only by a text search,
    /// so that a host can keep them between queries.</param>
    public async Task<IReadOnlyList<YouTrackIssue>> SearchAsync(YouTrackOptions options, Guid spaceId, string query,
        Func<CancellationToken, Task<IReadOnlyList<string>>> projectKeys, CancellationToken cancellationToken = default)
    {
        var allowed = Allowed(options, spaceId);
        if (query.Length > 256 || query.Any(char.IsControl))
        {
            throw new ArgumentException("Query must be a single line of at most 256 characters.", nameof(query));
        }
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }
        if (IssueReference().IsMatch(query) || query.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || query.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var issue = await ResolveAsync(options, spaceId, query, cancellationToken);
            return issue is null ? [] : [issue];
        }
        // The instance-wide top 50 may all be other projects' issues: ask only the space's projects (their short names).
        var projects = await projectKeys(cancellationToken);
        if (projects.Count == 0)
        {
            return [];
        }
        var root = Root(options);
        var restricted = "project: " + string.Join(", ", projects) + " and (" + query + ")";
        var uri = new Uri(root, "api/issues?fields=" + IssueFields + "&$top=50&query=" + Uri.EscapeDataString(restricted));
        using var json = await RequestAsync(options, uri, false, cancellationToken);
        if (json?.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new YouTrackUnavailableException();
        }
        return json.RootElement.EnumerateArray().Select(item => ParseIssue(item, allowed, root)).OfType<YouTrackIssue>().Take(20).ToArray();
    }

    public async Task<YouTrackIssue?> ResolveAsync(YouTrackOptions options, Guid spaceId, string reference, CancellationToken cancellationToken = default)
    {
        var allowed = Allowed(options, spaceId);
        var root = Root(options);
        var id = ParseReference(root, reference);
        using var json = await RequestAsync(options, new Uri(root, "api/issues/" + Uri.EscapeDataString(id) + "?fields=" + IssueFields), true, cancellationToken);
        return json is null ? null : ParseIssue(json.RootElement, allowed, root);
    }

    /// <summary>Several issue keys (<see cref="IssueKey"/>) in one search of the allowed projects, whose short names
    /// <paramref name="projectKeys"/> are (<see cref="ProjectKeysAsync"/>). The answer holds only the keys that were asked: each with
    /// its issue, or null when the search did not find it. A key with another prefix (a word that only looks like an ID, a database ID
    /// such as "2-1", a project's former short name) is not asked and not in the answer; only <see cref="ResolveAsync"/> can tell
    /// what it names.</summary>
    public async Task<IReadOnlyDictionary<string, YouTrackIssue?>> ResolveManyAsync(YouTrackOptions options, Guid spaceId, IReadOnlyCollection<string> projectKeys,
        IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        var allowed = Allowed(options, spaceId);
        if (keys.Count > 100 || keys.Any(key => !IssueReference().IsMatch(key)))
        {
            throw new ArgumentException("Use at most 100 issue IDs.", nameof(keys));
        }
        var asked = keys.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(key => projectKeys.Contains(key[..key.LastIndexOf('-')], StringComparer.OrdinalIgnoreCase)).ToArray();
        var found = asked.ToDictionary(key => key, _ => (YouTrackIssue?)null, StringComparer.OrdinalIgnoreCase);
        if (asked.Length == 0)
        {
            return found;
        }
        var root = Root(options);
        var uri = new Uri(root, "api/issues?fields=" + IssueFields + "&$top=" + asked.Length + "&query=" + Uri.EscapeDataString("issue id: " + string.Join(", ", asked)));
        using var json = await RequestAsync(options, uri, false, cancellationToken);
        if (json?.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new YouTrackUnavailableException();
        }
        foreach (var issue in json.RootElement.EnumerateArray().Select(item => ParseIssue(item, allowed, root)).OfType<YouTrackIssue>())
        {
            if (found.ContainsKey(issue.Key))
            {
                found[issue.Key] = issue;
            }
        }
        return found;
    }

    /// <summary>The issue ID a reference (an ID or an issue URL of the configured YouTrack) names, upper-case, so that "px2-1", "PX2-1"
    /// and its URL are one issue.</summary>
    public static string IssueKey(YouTrackOptions options, string reference)
    {
        if (!IsConfigured(options))
        {
            throw new YouTrackUnavailableException();
        }
        return ParseReference(Root(options), reference).ToUpperInvariant();
    }

    public async Task<IReadOnlyList<YouTrackProject>> TestConnectionAsync(YouTrackOptions options, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured(options))
        {
            throw new YouTrackUnavailableException();
        }
        // This directory is admin-only. It is not the list of projects allowed for a wiki reader.
        using var json = await RequestAsync(options, new Uri(Root(options), "api/admin/projects?fields=id,name,shortName&$top=100"), false, cancellationToken);
        if (json?.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new YouTrackUnavailableException();
        }
        return json.RootElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new YouTrackProject(Text(item, "id"), Text(item, "name"), Text(item, "shortName")))
            .Where(p => Valid(p.Id) && Valid(p.Name) && Valid(p.ShortName)).Take(100).ToArray();
    }

    public static bool IsConfigured(YouTrackOptions options)
    {
        return options.Enabled && !string.IsNullOrWhiteSpace(options.Token)
            && !options.Token.Any(char.IsControl) && IsValidBaseUrl(options.BaseUrl);
    }

    public static bool IsValidBaseUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }

    private async Task<JsonDocument?> RequestAsync(YouTrackOptions options, Uri uri, bool allowMissing, CancellationToken ct)
    {
        // ResponseHeadersRead stops HttpClient's timeout at headers; keep the same budget for the body.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(m_http.Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await m_http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (allowMissing && response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new YouTrackUnavailableException();
            }
            await response.Content.LoadIntoBufferAsync(1_048_576, timeout.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 16 }, timeout.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException || exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new YouTrackUnavailableException();
        }
    }

    /// <summary>The integration is enabled, has a connection and allows at least one project for the space.</summary>
    public static bool IsConfiguredFor(YouTrackOptions options, Guid spaceId)
    {
        return IsConfigured(options) && options.SpaceProjects.TryGetValue(spaceId, out var projects) && projects.Length > 0;
    }

    private static HashSet<string> Allowed(YouTrackOptions options, Guid spaceId)
    {
        if (!IsConfiguredFor(options, spaceId))
        {
            throw new YouTrackUnavailableException();
        }
        return options.SpaceProjects[spaceId].ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The short names (issue ID prefixes) of the allowed projects, read by their stable IDs; a deleted project is left out.</summary>
    public async Task<IReadOnlyList<string>> ProjectKeysAsync(YouTrackOptions options, Guid spaceId, CancellationToken ct = default)
    {
        return (await ProjectShortNamesAsync(options, spaceId, ct)).Values.ToArray();
    }

    /// <summary><see cref="ProjectKeysAsync"/> by the project's stable ID, so that a host can store them.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ProjectShortNamesAsync(YouTrackOptions options, Guid spaceId, CancellationToken ct = default)
    {
        var allowed = Allowed(options, spaceId);
        var root = Root(options);
        var keys = await Task.WhenAll(allowed.Select(async id =>
        {
            using var json = await RequestAsync(options, new Uri(root, "api/admin/projects/" + Uri.EscapeDataString(id) + "?fields=id,shortName"), true, ct);
            return (Id: id, Key: json?.RootElement.ValueKind == JsonValueKind.Object && Text(json.RootElement, "id") == id ? Text(json.RootElement, "shortName") : "");
        }));
        return keys.Where(project => IsProjectKey(project.Key)).ToDictionary(project => project.Id, project => project.Key, StringComparer.Ordinal);
    }

    /// <summary>A project's short name, the prefix of its issue IDs.</summary>
    public static bool IsProjectKey(string value) => ProjectKey().IsMatch(value);

    private static Uri Root(YouTrackOptions options)
    {
        return new Uri(options.BaseUrl.TrimEnd('/') + "/");
    }

    private static string ParseReference(Uri root, string reference)
    {
        reference = reference.Trim();
        if (reference.Length > 2048 || reference.Any(char.IsControl))
        {
            throw new ArgumentException("Invalid issue reference.");
        }
        if (IssueReference().IsMatch(reference))
        {
            return reference;
        }
        if (Uri.TryCreate(reference, UriKind.Absolute, out var uri)
            && uri.Scheme == root.Scheme && uri.IdnHost == root.IdnHost && uri.Port == root.Port
            && uri.UserInfo.Length == 0
            && uri.AbsolutePath.StartsWith(root.AbsolutePath + "issue/", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(uri.AbsolutePath[(root.AbsolutePath.Length + 6)..].Split('/')[0]);
            if (IssueReference().IsMatch(id))
            {
                return id;
            }
        }
        throw new ArgumentException("Reference must be an issue ID or a URL from the configured YouTrack.");
    }

    private static YouTrackIssue? ParseIssue(JsonElement item, HashSet<string> allowed, Uri root)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.Object || !allowed.Contains(Text(project, "id")))
        {
            return null;
        }
        var id = Text(item, "id");
        var key = Text(item, "idReadable");
        var title = Text(item, "summary");
        if (!Valid(id) || !Valid(key) || !Valid(title))
        {
            return null;
        }
        string? state = null, dueDate = null;
        if (item.TryGetProperty("customFields", out var fields) && fields.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in fields.EnumerateArray())
            {
                if (field.ValueKind == JsonValueKind.Object && Text(field, "$type") == "StateIssueCustomField"
                    && field.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object && Valid(Text(value, "name")))
                {
                    state = Text(value, "name");
                }
                else if (field.ValueKind == JsonValueKind.Object && Text(field, "$type") == "DateIssueCustomField" && Text(field, "name") == "Due Date"
                    && field.TryGetProperty("value", out var due) && due.ValueKind == JsonValueKind.Number && due.TryGetInt64(out var epoch)
                    && epoch is > -62135596800000 and < 253402300799000)
                {
                    // Epoch milliseconds (a date is stored at UTC midnight); anything out of range is no date.
                    dueDate = DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }
        return new YouTrackIssue(id, key, title, new Uri(root, "issue/" + Uri.EscapeDataString(key)).AbsoluteUri, state, dueDate);
    }

    private static string Text(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private static bool Valid(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 2000 && !value.Any(char.IsControl);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_]*-[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IssueReference();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectKey();
}
