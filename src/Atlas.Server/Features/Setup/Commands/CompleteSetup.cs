namespace Atlas.Server.Features.Setup.Commands;

public sealed record CompleteSetup(string BootstrapToken, string WorkspaceName, string Username, string DisplayName, string Password)
{
    public override string ToString() => "CompleteSetup { sensitive values redacted }";
}
