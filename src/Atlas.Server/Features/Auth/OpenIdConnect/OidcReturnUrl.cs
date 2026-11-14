namespace Atlas.Server.Features.Auth.OpenIdConnect;

/// <summary>Fail-closed: anything that is not an unambiguous local path becomes the site root.</summary>
public static class OidcReturnUrl
{
    private const string Root = "/";
    private const int MaxLength = 2048;

    public static string Safe(string? value)
    {
        if (value is not { Length: > 0 } || value.Length > MaxLength)
        {
            return Root;
        }
        if (value[0] != '/')
        {
            return Root;
        }
        // A second slash or backslash makes the target protocol-relative, which means remote.
        if (value.Length > 1 && value[1] is '/' or '\\')
        {
            return Root;
        }
        if (value.Any(c => char.IsControl(c) || c == '\\'))
        {
            return Root;
        }
        // Percent-encoding could hide any of the above from this check.
        if (value.Contains('%'))
        {
            return Root;
        }
        return value;
    }
}
