using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.Vaults.Kdbx;
using Atlas.Server.Features.Vaults.Runtime;
using Xunit;
using static Atlas.Server.Tests.Vaults.KdbxTestFiles;

namespace Atlas.Server.Tests.Vaults;

public sealed class VaultProtectionTests
{
    [Fact]
    public void Passwords_round_trip_per_purpose_and_an_unreadable_payload_is_null()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protection = new VaultProtection(provider);
        const string password = "Hlavní heslo ✓ 123";
        var kdbx = protection.ProtectKdbxPassword(password);
        var location = protection.ProtectLocationPassword(password);
        Assert.DoesNotContain(password, kdbx, StringComparison.Ordinal);
        Assert.NotEqual(kdbx, location);
        Assert.Equal(password, protection.UnprotectKdbxPassword(kdbx));
        Assert.Equal(password, protection.UnprotectLocationPassword(location));
        Assert.Equal(password, provider.CreateProtector(VaultProtection.KdbxPasswordPurpose).Unprotect(kdbx));
        // Purposes isolate the two secrets; another key ring (a lost DataProtection:KeysPath) reads neither.
        Assert.Null(protection.UnprotectLocationPassword(kdbx));
        Assert.Null(protection.UnprotectKdbxPassword(location));
        Assert.Null(new VaultProtection(new EphemeralDataProtectionProvider()).UnprotectKdbxPassword(kdbx));
        Assert.Null(protection.UnprotectKdbxPassword("garbage"));
        Assert.Null(protection.UnprotectKdbxPassword(null));
    }

    [Fact]
    public void Composite_key_opens_the_file_like_the_password_and_keyring_zeroes_dropped_keys()
    {
        var name = "argon2id-chacha20-none.kdbx";
        var key = KdbxReader.CompositeKey(Password(name));
        using (var database = Reader().ReadWithCompositeKey(Read(name), key, true, CancellationToken.None))
        {
            Assert.Equal(2, database.Entries.Count);
        }
        using var keyring = new VaultKeyring();
        var vault = Guid.NewGuid();
        keyring.Store(vault, key);
        var copy = keyring.Copy(vault)!;
        Assert.Equal(key, copy);
        Assert.NotSame(key, copy);
        keyring.Lock(vault);
        Assert.Null(keyring.Copy(vault));
        Assert.False(keyring.Contains(vault));
        Assert.Throws<ArgumentException>(() => Reader().ReadWithCompositeKey(Read(name), key.AsSpan(0, 16), true, CancellationToken.None));
    }

    [Fact]
    public void Generated_passwords_are_long_random_and_unambiguous()
    {
        var passwords = Enumerable.Range(0, 50).Select(_ => VaultPasswords.Generate()).ToArray();
        Assert.All(passwords, p => Assert.Equal(VaultPasswords.GeneratedLength, p.Length));
        Assert.All(passwords, p => Assert.DoesNotContain(p, c => "0O1lI".Contains(c)));
        Assert.Equal(passwords.Length, passwords.Distinct().Count());
    }
}
