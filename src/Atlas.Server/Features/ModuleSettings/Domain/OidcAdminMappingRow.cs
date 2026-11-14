namespace Atlas.Server.Features.ModuleSettings.Domain;

public class OidcAdminMappingRow
{
    public virtual Guid SettingsId { get; set; }
    public virtual string ProviderId { get; set; } = "";
    public virtual string ClaimType { get; set; } = "";
    public virtual string ClaimValue { get; set; } = "";

    public override bool Equals(object? other) => other is OidcAdminMappingRow row
        && SettingsId == row.SettingsId && ProviderId == row.ProviderId
        && ClaimType == row.ClaimType && ClaimValue == row.ClaimValue;
    public override int GetHashCode() => HashCode.Combine(SettingsId, ProviderId, ClaimType, ClaimValue);
}
