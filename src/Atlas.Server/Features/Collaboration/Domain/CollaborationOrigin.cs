namespace Atlas.Server.Features.Collaboration.Domain;

public static class CollaborationOrigin
{
    public static bool IsAllowed(HttpRequest request)
    {
        if (request.Headers["Sec-Fetch-Site"] == "cross-site")
        {
            return false;
        }
        var origins = request.Headers.Origin;
        if (origins.Count == 0)
        {
            // Non-browser clients still need the authenticated session.
            return true;
        }
        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin))
        {
            return false;
        }
        return origin.Scheme == request.Scheme && origin.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase)
            && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0 && origin.UserInfo.Length == 0;
    }
}
