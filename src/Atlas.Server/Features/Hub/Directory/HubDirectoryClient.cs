using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Atlas.Server.Features.Hub.Directory;

public enum HubDirectoryAccountState { Active, Disabled, Missing }
public sealed record HubDirectoryGroup(string Id, string Name);
public sealed record HubUserObservation(string ExternalUserId, HubDirectoryAccountState State, ImmutableArray<HubDirectoryGroup> Groups);
public sealed record HubGroupSearchResult(ImmutableArray<HubDirectoryGroup> Groups, bool HasMore);

public sealed class HubDirectoryException(string code, TimeSpan? retryAfter = null) : Exception(code)
{
    public string Code { get; } = code;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

// The registered HTTP handler must disable redirects and cookies.
public sealed class HubDirectoryClient(HttpClient http, TimeProvider clock)
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const string PermissionFields = "id,banned,guest,transitiveProjectRoles(project(id,global),role(permissions(key)))";

    public Task<HubUserObservation> ReadUserAsync(Uri baseUrl, string token, string externalUserId, CancellationToken cancellationToken = default) =>
        ReadAsync(baseUrl, token, $"users/{EscapeId(externalUserId, rejectMe: true)}?fields={Uri.EscapeDataString("id,banned,guest,eraseTimestamp,transitiveGroups(id,name)")}",
            root => ReadUser(root, externalUserId), cancellationToken);

    public Task<HubDirectoryGroup> ReadGroupAsync(Uri baseUrl, string token, string externalGroupId, CancellationToken cancellationToken = default) =>
        ReadAsync(baseUrl, token, $"usergroups/{EscapeId(externalGroupId)}?fields=id%2Cname", root =>
        {
            var group = ReadGroup(root);
            if (!string.Equals(group.Id, externalGroupId, StringComparison.Ordinal))
                throw new HubDirectoryException("identity-mismatch");
            return group;
        }, cancellationToken);

    public Task<HubGroupSearchResult> SearchGroupsAsync(Uri baseUrl, string token, string query, CancellationToken cancellationToken = default) =>
        ReadAsync(baseUrl, token, $"usergroups?fields=id%2Cname&%24top=50&query={Uri.EscapeDataString(query)}", ReadGroupSearch, cancellationToken);

    private static HubGroupSearchResult ReadGroupSearch(JsonElement root)
    {
        RequireObject(root);
        var groups = ReadGroups(Required(root, "usergroups"), 50);
        RequireSearchPageWindow(root, groups.Length);
        if (root.TryGetProperty("total", out var total) &&
            (total.ValueKind != JsonValueKind.Number || !total.TryGetInt64(out var count) || count < groups.Length))
            throw InvalidResponse();
        var more = false;
        if (root.TryGetProperty("next", out var next))
        {
            if (next.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(next.GetString()))
                throw InvalidResponse();
            more = true;
        }
        return new HubGroupSearchResult(groups, more);
    }

    private static void RequireSearchPageWindow(JsonElement root, int groupCount)
    {
        var skip = Required(root, "skip");
        var top = Required(root, "top");
        if (skip.ValueKind != JsonValueKind.Number || !skip.TryGetInt32(out var offset) || offset != 0 ||
            top.ValueKind != JsonValueKind.Number || !top.TryGetInt32(out var size) || size is < 1 or > 50 || groupCount > size)
            throw InvalidResponse();
    }

    private async Task<T> ReadAsync<T>(Uri baseUrl, string token, string path, Func<JsonElement, T> parse, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!baseUrl.IsAbsoluteUri || baseUrl.Scheme != Uri.UriSchemeHttps ||
            baseUrl.UserInfo.Length != 0 || baseUrl.Query.Length != 0 || baseUrl.Fragment.Length != 0 ||
            string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw InvalidResponse();
        var api = new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/api/rest/");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using (var permissions = await GetAsync(new Uri(api, $"users/me?fields={Uri.EscapeDataString(PermissionFields)}"), token, linked.Token))
                RequirePermissions(permissions.RootElement);
            using var response = await GetAsync(new Uri(api, path), token, linked.Token);
            var result = parse(response.RootElement);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HubDirectoryException("timeout");
        }
        catch (HttpRequestException)
        {
            throw new HubDirectoryException("unavailable");
        }
        catch (IOException)
        {
            throw new HubDirectoryException("unavailable");
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
    }

    private async Task<JsonDocument> GetAsync(Uri uri, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var code = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "unauthorized",
                HttpStatusCode.Forbidden => "forbidden",
                HttpStatusCode.NotFound => "not-visible",
                HttpStatusCode.TooManyRequests => "rate-limited",
                >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => "invalid-response",
                _ => "unavailable"
            };
            throw new HubDirectoryException(code, RetryAfter(response.Headers.RetryAfter));
        }
        if (response.Content.Headers.ContentLength > MaximumBytes)
            throw new HubDirectoryException("response-too-large");
        using var body = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (body.Length + count > MaximumBytes)
                throw new HubDirectoryException("response-too-large");
            body.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length), new JsonDocumentOptions { MaxDepth = 32 });
    }

    private TimeSpan? RetryAfter(RetryConditionHeaderValue? header)
    {
        var duration = header?.Delta ?? (header?.Date - clock.GetUtcNow());
        return duration is null ? null : TimeSpan.FromSeconds(Math.Clamp(duration.Value.TotalSeconds, 1, 3600));
    }

    private static void RequirePermissions(JsonElement root)
    {
        RequireObject(root);
        _ = RequiredString(root, "id");
        var banned = RequiredBool(root, "banned");
        var guest = RequiredBool(root, "guest");
        var roles = RequiredArray(root, "transitiveProjectRoles");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projectRole in roles.EnumerateArray())
        {
            RequireObject(projectRole);
            var project = Required(projectRole, "project");
            RequireObject(project);
            var id = RequiredString(project, "id");
            var global = RequiredBool(project, "global");
            var role = Required(projectRole, "role");
            RequireObject(role);
            foreach (var permission in RequiredArray(role, "permissions").EnumerateArray())
            {
                RequireObject(permission);
                var key = RequiredString(permission, "key");
                if (global && id == "0") keys.Add(key);
            }
        }
        if (banned || guest || !keys.Contains("jetbrains.jetpass.user-read-basic") ||
            !keys.Contains("jetbrains.jetpass.group-read") || !keys.Contains("jetbrains.jetpass.role-read"))
            throw new HubDirectoryException("insufficient-permissions");
    }

    private static HubUserObservation ReadUser(JsonElement root, string expectedId)
    {
        RequireObject(root);
        var id = RequiredString(root, "id");
        if (!string.Equals(id, expectedId, StringComparison.Ordinal))
            throw new HubDirectoryException("identity-mismatch");
        var banned = RequiredBool(root, "banned");
        var guest = RequiredBool(root, "guest");
        var erased = false;
        if (root.TryGetProperty("eraseTimestamp", out var timestamp))
        {
            if (timestamp.ValueKind != JsonValueKind.Number || !timestamp.TryGetInt64(out var value))
                throw InvalidResponse();
            erased = value > 0;
        }
        var groups = ReadGroups(Required(root, "transitiveGroups"), 10000);
        return new HubUserObservation(id, banned || guest || erased ? HubDirectoryAccountState.Disabled : HubDirectoryAccountState.Active, groups);
    }

    private static ImmutableArray<HubDirectoryGroup> ReadGroups(JsonElement array, int maximum)
    {
        if (array.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        if (array.GetArrayLength() > maximum) throw new HubDirectoryException("response-too-large");
        var groups = ImmutableArray.CreateBuilder<HubDirectoryGroup>(array.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            var group = ReadGroup(item);
            if (!ids.Add(group.Id)) throw InvalidResponse();
            groups.Add(group);
        }
        return groups.MoveToImmutable();
    }

    private static HubDirectoryGroup ReadGroup(JsonElement value)
    {
        RequireObject(value);
        return new HubDirectoryGroup(RequiredString(value, "id"), RequiredString(value, "name"));
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw InvalidResponse();
    }

    private static JsonElement Required(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property : throw InvalidResponse();

    private static JsonElement RequiredArray(JsonElement value, string name)
    {
        var property = Required(value, name);
        return property.ValueKind == JsonValueKind.Array ? property : throw InvalidResponse();
    }

    private static string RequiredString(JsonElement value, string name)
    {
        var property = Required(value, name);
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()) ||
            property.GetString()!.Any(char.IsControl))
            throw InvalidResponse();
        return property.GetString()!;
    }

    private static bool RequiredBool(JsonElement value, string name) => Required(value, name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw InvalidResponse()
    };

    private static string EscapeId(string id, bool rejectMe = false)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(char.IsControl) || id is "." or ".." ||
            (rejectMe && string.Equals(id, "me", StringComparison.OrdinalIgnoreCase)))
            throw InvalidResponse();
        return Uri.EscapeDataString(id);
    }

    private static HubDirectoryException InvalidResponse() => new("invalid-response");
}
