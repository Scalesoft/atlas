using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Kdbx;
using Atlas.Server.Features.Vaults.Queries;
using Atlas.Server.Features.Vaults.Runtime;
using Xunit;
using Xunit.Abstractions;
using static Atlas.Server.Tests.Vaults.VaultReleaseTests;

namespace Atlas.Server.Tests.Vaults;

/// <summary>
/// The transformed key (KDF output) of an unlocked vault is kept in the keyring: a release with an unchanged header skips the
/// key derivation but still reads and verifies the current file; a new seed derives again; lock and replacement drop it.
/// </summary>
public sealed class VaultDerivedKeyTests(ITestOutputHelper output)
{
    private const string AesFixture = "aeskdf-aes-none.kdbx";

    [Fact]
    public void Keyring_hits_only_for_its_current_composite_key_and_kdf_identity_and_forgets_on_lock_and_replacement()
    {
        using var keyring = new VaultKeyring();
        var vault = Guid.NewGuid();
        byte[] key = [.. Enumerable.Repeat((byte)1, 32)], other = [.. Enumerable.Repeat((byte)2, 32)];
        byte[] identity = SHA256.HashData("kdf-a"u8), otherIdentity = SHA256.HashData("kdf-b"u8), transformed = RandomNumberGenerator.GetBytes(32);
        var cache = keyring.DerivedKeys(vault);

        // Locked: nothing is remembered.
        cache.Remember(key, identity, transformed);
        Assert.False(keyring.HasDerivedKey(vault));

        keyring.Store(vault, key);
        cache.Remember(key, identity, transformed);
        Assert.Equal(transformed, cache.Find(key, identity));
        Assert.Null(cache.Find(key, otherIdentity));
        Assert.Null(cache.Find(other, identity));
        Assert.Null(keyring.DerivedKeys(Guid.NewGuid()).Find(key, identity));
        // A read with a key the keyring no longer holds does not overwrite it.
        cache.Remember(other, otherIdentity, transformed);
        Assert.Null(cache.Find(key, otherIdentity));

        keyring.Lock(vault);
        Assert.False(keyring.HasDerivedKey(vault));
        Assert.Null(cache.Find(key, identity));

        keyring.Store(vault, key);
        cache.Remember(key, identity, transformed);
        keyring.Store(vault, other);
        Assert.False(keyring.HasDerivedKey(vault));

        cache.Remember(other, identity, transformed);
        keyring.Dispose();
        Assert.False(keyring.HasDerivedKey(vault));
    }

    [PostgreSqlFact]
    public async Task Second_release_reuses_the_derived_key_a_new_seed_derives_again_and_lock_clears_it()
    {
        // Own host: KdbxKeyDerivation.Derivations counts every derivation of the host, so other tests' attaches and releases would count.
        await using var f = await VaultFixture.CreateAsync(release: new FakeReleasePolicy(), isolated: true);
        var (vault, secret) = await AttachAesAsync(f, "provoz.kdbx", KdbxTestFiles.Read(AesFixture));
        var reader = await f.Host.SeedUserAsync("reader");
        await Grant(f, vault, reader, "read");
        var kdf = f.Host.Services.GetRequiredService<KdbxKeyDerivation>();

        var before = kdf.Derivations;
        Assert.Equal("aes-kdf-aes", (await Release(f, reader, secret)).Values!.Values["password"]);
        Assert.Equal(1, kdf.Derivations - before);
        Assert.True(f.Keyring.HasDerivedKey(vault));

        // Unchanged header: the file is read and verified again, the KDF does not run.
        before = kdf.Derivations;
        Assert.Equal("sa", (await Release(f, reader, secret, "userName")).Values!.Values["userName"]);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Release(f, reader, secret)));
        Assert.Equal(0, kdf.Derivations - before);

        // KeePass saved the database again with a new seed: one derivation, then the new key is reused.
        var reseeded = KdbxTestSealer.WithKdf(KdbxTestFiles.Read(AesFixture), KdbxTestFiles.Password(AesFixture), "S", RandomNumberGenerator.GetBytes(32));
        await File.WriteAllBytesAsync(Path.Combine(f.Folder, "provoz.kdbx"), reseeded);
        before = kdf.Derivations;
        Assert.Equal("aes-kdf-aes", (await Release(f, reader, secret)).Values!.Values["password"]);
        Assert.Equal(1, kdf.Derivations - before);
        before = kdf.Derivations;
        Assert.Equal("aes-kdf-aes", (await Release(f, reader, secret)).Values!.Values["password"]);
        Assert.Equal(0, kdf.Derivations - before);

        // Lock drops the transformed key with the composite key; the automatic unlock derives again.
        f.Keyring.Lock(vault);
        Assert.False(f.Keyring.HasDerivedKey(vault));
        before = kdf.Derivations;
        Assert.Equal("aes-kdf-aes", (await Release(f, reader, secret)).Values!.Values["password"]);
        Assert.True(kdf.Derivations - before >= 1);
        Assert.True(f.Keyring.HasDerivedKey(vault));
    }

    [PostgreSqlFact]
    public async Task Release_with_a_costly_kdf_is_fast_once_the_key_is_derived()
    {
        // Own host: KdbxKeyDerivation.Derivations counts every derivation of the host, so other tests' attaches and releases would count.
        await using var f = await VaultFixture.CreateAsync(release: new FakeReleasePolicy(), isolated: true);
        // A KDF that costs noticeably more than the rest of a release (the fixture's own rounds are tiny).
        var rounds = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(rounds, 2_000_000);
        var costly = KdbxTestSealer.WithKdf(KdbxTestFiles.Read(AesFixture), KdbxTestFiles.Password(AesFixture), "R", rounds);
        var (vault, secret) = await AttachAesAsync(f, "costly.kdbx", costly);
        var reader = await f.Host.SeedUserAsync("reader");
        await Grant(f, vault, reader, "read");
        var kdf = f.Host.Services.GetRequiredService<KdbxKeyDerivation>();

        var watch = Stopwatch.StartNew();
        await Release(f, reader, secret);
        var first = watch.Elapsed;
        var before = kdf.Derivations;
        var cached = TimeSpan.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            watch.Restart();
            Assert.Equal("aes-kdf-aes", (await Release(f, reader, secret)).Values!.Values["password"]);
            cached = TimeSpan.FromTicks(Math.Min(cached.Ticks, watch.Elapsed.Ticks));
        }
        output.WriteLine($"release with derivation {first.TotalMilliseconds:F0} ms, with the cached key {cached.TotalMilliseconds:F0} ms");
        Assert.Equal(0, kdf.Derivations - before);
        Assert.True(cached < TimeSpan.FromMilliseconds(250), $"A cached release took {cached.TotalMilliseconds:F0} ms.");
        Assert.True(cached < first, $"Cached {cached.TotalMilliseconds:F0} ms, first {first.TotalMilliseconds:F0} ms.");
    }

    private static async Task<(Guid Vault, Guid Secret)> AttachAesAsync(VaultFixture f, string fileName, byte[] content)
    {
        var location = await f.LocalLocationAsync();
        await File.WriteAllBytesAsync(Path.Combine(f.Folder, fileName), content);
        var vault = await f.Operate(f.SpaceAdmin, operations => operations.AttachAsync(
            new AttachVaultRequest(f.SpaceId, location.Id, "Databáze", fileName, KdbxTestFiles.Password(AesFixture)), CancellationToken.None));
        var secrets = await f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault.Id));
        return (vault.Id, secrets.Single(s => s.Title == "Database").SecretId);
    }
}
