using System.Text;
using Atlas.Server.Platform.Backup;
using Xunit;

namespace Atlas.Server.Tests.Backups;

public sealed class BackupKeyTests
{
    [Fact]
    public void The_same_file_encrypts_to_the_same_bytes_and_only_its_key_and_path_open_it()
    {
        var key = BackupKey.Generate();
        var plain = Encoding.UTF8.GetBytes("{\"PasswordHash\": \"AQAAAA…\"}\n");
        var path = ".atlas/secure/identity/BuiltinCredential/1.json.enc";

        var first = key.Encrypt(path, plain);

        // Deterministic, so an unchanged secret is no Git change; another plaintext or path gives other bytes.
        Assert.Equal(first, key.Encrypt(path, plain));
        Assert.NotEqual(first, key.Encrypt(path, Encoding.UTF8.GetBytes("{\"PasswordHash\": \"other\"}\n")));
        Assert.NotEqual(first, key.Encrypt(path.Replace("1.json", "2.json"), plain));
        Assert.DoesNotContain("PasswordHash", Encoding.UTF8.GetString(first));
        Assert.Equal(plain, key.Decrypt(path, first));
        Assert.Equal(plain, BackupKey.Parse(key.Text).Decrypt(path, first));
        Assert.Equal(key.Id, BackupKey.Parse(" " + key.Text + "\n").Id);
        Assert.Throws<InvalidDataException>(() => BackupKey.Generate().Decrypt(path, first));
        // A file moved to another path does not decrypt there.
        Assert.Throws<InvalidDataException>(() => key.Decrypt(".atlas/secure/identity/BuiltinCredential/2.json.enc", first));
        var tampered = first.ToArray();
        tampered[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => key.Decrypt(path, tampered));
    }

    [Fact]
    public void A_key_is_43_characters_of_base64url_and_anything_else_is_refused()
    {
        var key = BackupKey.Generate();
        Assert.Equal(43, key.Text.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", key.Text);
        Assert.Matches("^[0-9a-f]{16}$", key.Id);
        Assert.NotEqual(key.Id, BackupKey.Generate().Id);
        foreach (var text in new[] { "", "short", key.Text + "AAAA", "!" + key.Text[1..] })
        {
            Assert.Equal(400, Assert.Throws<ApiException>(() => BackupKey.Parse(text)).Status);
        }
    }
}
