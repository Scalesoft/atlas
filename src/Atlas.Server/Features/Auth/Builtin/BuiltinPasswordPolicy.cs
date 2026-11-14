namespace Atlas.Server.Features.Auth.Builtin;

/// <summary>Short passwords are an explicit local-development option, never a production setting.</summary>
public sealed class BuiltinPasswordPolicy(bool development, bool allowDevelopmentPasswords)
{
    public int MinimumLength { get; } = development && allowDevelopmentPasswords ? 4 : 12;
    public void Validate(string? password)
    {
        if (password is null || password.Length < MinimumLength || password.Length > 256)
            throw new ApiException(400, "Heslo musí mít {minimumLength} až 256 znaků.", args: new Dictionary<string, object?> { ["minimumLength"] = MinimumLength });
    }
    public static BuiltinPasswordPolicy Production { get; } = new(false, false);
}
