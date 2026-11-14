using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Availability;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.Ai;

/// <summary>
/// The real AddAi registrations without a database (A3): the configuration comes from a snapshot the test sets instead of the
/// AI tables, the fake provider, a fake clock and captured logs. The availability service is the pipeline's real outcome sink,
/// so calls through the fake reach the circuit breaker exactly as in production.
/// </summary>
internal sealed class AvailabilityHost : IDisposable
{
    private readonly ServiceProvider m_services;

    public AvailabilityHost()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddAi();
        services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
        services.Replace(ServiceDescriptor.Singleton<IAiConfigurationLoader>(Configuration));
        FakeAi.Register(services);
        m_services = services.BuildServiceProvider();
    }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
    public CaptureLoggerProvider Logs { get; } = new();
    public StaticConfiguration Configuration { get; } = new();
    public FakeAiClientFactory Fake => m_services.GetRequiredService<FakeAiClientFactory>();
    public AiAvailability Availability => m_services.GetRequiredService<AiAvailability>();
    public AiProbeRunner Runner => m_services.GetRequiredService<AiProbeRunner>();
    public AiConfigurationCache Cache => m_services.GetRequiredService<AiConfigurationCache>();

    /// <summary>Saves a configuration the way Administrace → AI would (master switch and the kinds' current revisions) and lets
    /// the cache load it.</summary>
    public async Task ConfigureAsync(bool enabled, params AiProfileSnapshot[] profiles)
    {
        Configuration.Snapshot = new AiConfigurationSnapshot(enabled, profiles.ToDictionary(x => x.Kind, StringComparer.Ordinal),
            CurrentAiSettings.ResolveTasks(null), null);
        m_services.GetRequiredService<IAiConfigurationChanged>().Changed();
        await Cache.GetAsync(default);
    }

    public void Dispose() => m_services.Dispose();

    internal sealed class StaticConfiguration : IAiConfigurationLoader
    {
        public AiConfigurationSnapshot Snapshot { get; set; } = AiConfigurationSnapshot.Off;

        public Task<AiConfigurationSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(Snapshot);
    }
}
