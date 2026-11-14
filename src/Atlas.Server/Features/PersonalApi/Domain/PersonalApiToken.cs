namespace Atlas.Server.Features.PersonalApi.Domain;

public class PersonalApiToken
{
    public virtual Guid Id { get; set; }
    public virtual Guid UserId { get; set; }
    public virtual string Name { get; set; } = "";
    public virtual string TokenHash { get; set; } = "";
    public virtual string Prefix { get; set; } = "";
    public virtual string SecurityStamp { get; set; } = "";
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
    public virtual DateTime ExpiresAt { get; set; }
    /// <summary>read, write (ApiScopes); always read.</summary>
    public virtual ISet<string> Scopes { get; set; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>rest, mcp (ApiChannels); at least one.</summary>
    public virtual ISet<string> Channels { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    public override string ToString() => $"PersonalApiToken {{ Id = {Id}, credential redacted }}";
}
