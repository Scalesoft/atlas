namespace Atlas.Server.Features.Vaults.Release;

/// <summary>Release limits under <c>Vaults:Release:*</c>.</summary>
public sealed class SecretReleaseOptions
{
    public const string Section = "Vaults:Release";

    /// <summary>How long the client may show a released value (expiresInSeconds of the response); the server forgets it at once.</summary>
    public int ValueSeconds { get; set; } = 60;

    /// <summary>Release attempts per account and minute; over it the call answers 429 before any authorization or file read.</summary>
    public int PerMinute { get; set; } = 30;

    /// <summary>How often released values still shown are re-authorized, so an expired grant hides them (secretAccessRevoked).</summary>
    public int RevocationCheckSeconds { get; set; } = 15;

    public static SecretReleaseOptions Read(IConfiguration configuration)
    {
        var options = new SecretReleaseOptions();
        configuration.GetSection(Section).Bind(options);
        if (options.ValueSeconds is < 5 or > 3600 || options.PerMinute is < 1 or > 10_000 || options.RevocationCheckSeconds is < 1 or > 3600)
        {
            throw new InvalidOperationException($"{Section}: ValueSeconds must be 5–3600, PerMinute 1–10000 and RevocationCheckSeconds 1–3600.");
        }
        return options;
    }
}
