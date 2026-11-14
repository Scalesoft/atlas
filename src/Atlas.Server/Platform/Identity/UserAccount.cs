namespace Atlas.Server.Platform.Identity;

public class UserAccount
{
    /// <summary>The sign-in name of an account an external sign-in created: nobody types it, so it can be replaced once.</summary>
    public const string ExternalUsernamePrefix = "external_";

    public static bool HasPlaceholderUsername(string username) => username.StartsWith(ExternalUsernamePrefix, StringComparison.Ordinal);

    public virtual Guid Id { get; set; }

    public virtual string Username { get; set; } = "";

    public virtual string DisplayName { get; set; } = "";

    public virtual string? Email { get; set; }

    public virtual bool IsWikiAdmin { get; set; }

    public virtual string? OidcAdminIssuer { get; set; }

    public virtual bool IsActive { get; set; } = true;

    public virtual string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
}
