using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Ai;

/// <summary>
/// The real AddAi registrations (the test host adds them like Program.cs), with an ephemeral key ring, a recording
/// configuration-change hook (forwarding to the real configuration cache), an index guard and an active generation the test
/// sets, and captured logs. A wiki admin, a space admin and a reader of that space.
/// </summary>
internal sealed class AiSettingsFixture : IAsyncDisposable
{
    public const string Purpose = "ScaleAtlas.Ai.ApiKey.v1";

    private AiSettingsFixture(AtlasTestHost host, UserAccount admin, UserAccount spaceAdmin, UserAccount reader,
        RecordingConfigurationChanges changes, SettableIndexGuard index, SettableActiveGeneration generation, CaptureLoggerProvider logs)
    {
        Generation = generation;
        Logs = logs;
        Host = host;
        Admin = admin;
        SpaceAdmin = spaceAdmin;
        Reader = reader;
        Changes = changes;
        Index = index;
    }

    public AtlasTestHost Host { get; }
    public UserAccount Admin { get; }
    public UserAccount SpaceAdmin { get; }
    public UserAccount Reader { get; }
    public RecordingConfigurationChanges Changes { get; }
    public SettableIndexGuard Index { get; }
    /// <summary>What V2's generation table would answer; none until a test sets one (then <see cref="ConfigurationChanged"/>).</summary>
    public SettableActiveGeneration Generation { get; }
    public CaptureLoggerProvider Logs { get; }

    public static async Task<AiSettingsFixture> CreateAsync()
    {
        var changes = new RecordingConfigurationChanges();
        var index = new SettableIndexGuard();
        var generation = new SettableActiveGeneration();
        var logs = new CaptureLoggerProvider();
        // Private (no sharedAs): the tests change the installation's AI configuration (the switch in AiSetting, AiProfileAssignment of
        // each kind, AiFeatureSettings) and the host-wide availability breaker; the fixture's guard, generation and change counter are per test.
        var host = await AtlasTestHost.CreateAsync("atlas_ai_", services =>
        {
            // The module catalog comes from the production registration, so the "ai" descriptor is listed as in Program.cs.
            services.AddModuleSettings(new ConfigurationBuilder().Build());
            services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
            services.Replace(ServiceDescriptor.Singleton<IAiConfigurationChanged>(provider =>
            {
                changes.Forward = provider.GetRequiredService<AiConfigurationCache>();
                return changes;
            }));
            services.Replace(ServiceDescriptor.Scoped<IEmbeddingChangeGuard>(_ => index));
            services.Replace(ServiceDescriptor.Scoped<IActiveGenerationReader>(_ => generation));
            services.AddSingleton<ILoggerProvider>(logs);
        });
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        await host.GrantAiAsync(admin);
        var spaceAdmin = await host.SeedUserAsync("space-admin");
        var reader = await host.SeedUserAsync("reader");
        await host.SeedSpaceAsync("Engineering", (spaceAdmin, SpaceRole.Admin), (reader, SpaceRole.Reader));
        return new AiSettingsFixture(host, admin, spaceAdmin, reader, changes, index, generation, logs);
    }

    /// <summary>What V2 does after activating a generation: the configuration cache drops its snapshot.</summary>
    public void ConfigurationChanged() => Host.Services.GetRequiredService<IAiConfigurationChanged>().Changed();


    /// <summary>Firemní LM Studio with bge-m3, no key.</summary>
    public static SaveAiProfileRequest Embeddings() => new(true, AiProvider.OpenAiCompatible, AiLocation.Company,
        "http://lmstudio.example:1234/v1", "text-embedding-bge-m3", null, 1024, "", "", 30, 4, null, null, true, false, false,
        null, null, null, false, null);

    /// <summary>A cloud chat profile at an OpenAI-compatible endpoint; a key is needed to enable it.</summary>
    public static SaveAiProfileRequest Chat(string? apiKey) => new(true, AiProvider.OpenAiCompatible, AiLocation.Cloud,
        "https://api.openai.example/v1", "gpt-test", null, null, null, null, 120, 2, "low", 0.2f, true, false, false,
        2m, 10m, apiKey, false, null);

    public Task<T> As<T>(UserAccount actor, object message)
    {
        Host.Actor.Current = actor;
        return Host.InvokeAsync<T>(message);
    }

    public string Unprotect(string protectedKey)
        => Host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(protectedKey);

    public async Task<IList<AiProfileRevision>> RevisionsAsync(string kind)
    {
        using var session = Host.OpenSession();
        return await session.QueryOver<AiProfileRevision>().Where(x => x.Kind == kind).OrderBy(x => x.CreatedAt).Asc.ListAsync();
    }

    public static async Task<ApiException> Denied(int status, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(status, error.Status);
        return error;
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();

    internal sealed class RecordingConfigurationChanges : IAiConfigurationChanged
    {
        private int m_count;

        public int Count => m_count;

        public IAiConfigurationChanged? Forward { get; set; }

        public void Changed()
        {
            Interlocked.Increment(ref m_count);
            Forward?.Changed();
        }
    }

    internal sealed class SettableIndexGuard : IEmbeddingChangeGuard
    {
        public string? ActiveFingerprint { get; set; }

        public Task<string?> ActiveFingerprintAsync(CancellationToken ct) => Task.FromResult(ActiveFingerprint);
    }

    internal sealed class SettableActiveGeneration : IActiveGenerationReader
    {
        public AiActiveGeneration? Active { get; set; }

        public Task<AiActiveGeneration?> ReadAsync(CancellationToken ct) => Task.FromResult(Active);
    }
}
