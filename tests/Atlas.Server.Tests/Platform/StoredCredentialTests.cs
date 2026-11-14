using Atlas.Server.Platform.Storage;
using Xunit;

namespace Atlas.Server.Tests.Platform;

/// <summary>SEC-T-2: a saved SMB password is reused only for the same server and account; a local folder sends none.</summary>
public sealed class StoredCredentialTests
{
    [Theory]
    [InlineData("smb://FILES.example.test/backups", "atlas", "EXAMPLE", true)]
    [InlineData("smb://evil.example.test/backups", "atlas", "EXAMPLE", false)]
    [InlineData("smb://files.example.test/other", "someone", "EXAMPLE", false)]
    [InlineData("smb://files.example.test/other", "atlas", "OTHER", false)]
    [InlineData("/var/backups", "", "", true)]
    public void A_saved_password_follows_only_its_server_and_account(string location, string username, string domain, bool reusable)
        => Assert.Equal(reusable, StoredCredential.Reusable("smb://files.example.test/archive", "atlas", "EXAMPLE", location, username, domain));
}
