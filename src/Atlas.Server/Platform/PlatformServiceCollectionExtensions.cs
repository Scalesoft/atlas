using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Modules;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Atlas.Markdown.Sync;
using JasperFx.CodeGeneration.Model;
using Wolverine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddAtlasPlatform(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<AtlasDatabase>();
        services.TryAddSingleton<AtlasDatabaseInitializerOptions>();
        services.AddHostedService<AtlasDatabaseInitializer>();
        services.AddScoped<ISession>(provider => provider.GetRequiredService<AtlasDatabase>().OpenSession());
        services.AddScoped<TransactionIntent>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<Jobs.SystemServiceHealth>();
        // Every host can request and run jobs; only a durable one (JobQueue.UseAtlasDurability) queues and schedules them by itself.
        // The server binds the JobQuiet section first (AtlasServices); test hosts keep the defaults.
        services.TryAddSingleton(new Jobs.JobQuietOptions());
        services.AddSingleton<Jobs.JobQuiet>();
        services.AddSingleton<Jobs.JobRunner>();
        services.AddSingleton<Jobs.IJobRunner>(provider => provider.GetRequiredService<Jobs.JobRunner>());
        // The jobs and log pages hear runs and stored lines on /hubs/live instead of asking again.
        services.AddSingleton<Live.ILiveChannel, Jobs.JobLiveChannel>();
        services.AddSingleton<Live.ILiveChannel, Logs.LogLiveChannel>();
        services.AddScoped<Jobs.JobRequests>();
        services.AddScoped<Jobs.IJobRequests, Jobs.JobRequests>();
        services.AddScoped<Jobs.IJobControl, Jobs.JobRequests>();
        // A factory, not a constructor Wolverine would inline: the coordinator exists only with the collaboration module.
        services.AddScoped(provider => new AccessChanges(provider.GetRequiredService<ILogger<AccessChanges>>(),
            provider.GetService<DocumentAccessCoordinator>(), provider.GetServices<IAccessChangeObserver>()));
        // A factory too: the middleware and the handler share the message scope's one instance.
        services.AddSingleton<Events.OutboxConsumers>();
        services.AddScoped(provider => new Events.Outbox(provider.GetRequiredService<Events.OutboxConsumers>()));
        services.AddJob<Events.OutboxUpgradeJob>();
        // The server binds the Retention section first (AtlasServices); test hosts keep the defaults and call RunAsync themselves.
        services.TryAddSingleton(new Retention.RetentionOptions());
        services.AddSingleton<Retention.OptionalLogGate>();
        services.AddSingleton<Retention.OperationalRetention>();
        services.AddSingleton<Jobs.IJobDefinition>(provider => provider.GetRequiredService<Retention.OperationalRetention>());
        services.AddScoped<IAtlasLocks, AtlasLocks>();
        foreach (var module in AtlasModules.All)
        {
            module.AddPurge(services);
        }
        services.AddScoped<ICurrentActor, HttpCurrentActor>();
        services.AddScoped<IRequestChannel, HttpRequestChannel>();
        services.AddScoped<IContentAccess, ContentAccessService>();
        services.AddScoped<PageGate>();
        services.AddScoped<SpaceRoles>();
        services.AddScoped<ResourceGrantSnapshots>();
        services.AddScoped<PageListing>();
        // The linking engine's server side (audit engine D6): every Markdown save of pages links through it.
        Linking.LinkingServices.AddLinking(services);
        // A factory, so the generated handler code does not depend on which modules contribute participants.
        services.AddScoped(provider => new Atlas.Server.Platform.Resources.ContentParticipants(provider));
        services.AddScoped(provider => new Atlas.Server.Platform.Objects.ObjectTypes(provider));
        services.AddScoped<Atlas.Server.Platform.Objects.ObjectScopeAccess>();
        services.AddLive();
        services.AddLogging(logging => logging.SilenceMessageCategories(typeof(PlatformServiceCollectionExtensions).Assembly));
        return services;
    }

    /// <param name="modules">The startup's module catalog; without one, every optional module is off (hosts of tests that do not
    /// ask for a module).</param>
    public static WolverineOptions ConfigureAtlasWolverine(this WolverineOptions options, ModuleCatalog? modules = null)
    {
        // Explicit: the test host is a different entry assembly and must still find server handlers.
        options.Discovery.IncludeAssembly(typeof(PlatformServiceCollectionExtensions).Assembly);
        modules ??= ModuleCatalog.Build(new ConfigurationBuilder().Build(), AtlasModules.All);
        foreach (var module in modules.Disabled)
        {
            // A disabled module lives in the scanned assembly, so its handlers are excluded by namespace: no chain, so no message
            // of it can run, not even through another handler. Exact segments: "Features.Boards" must not hide "Features.BoardsX".
            var root = module.GetType().Namespace!;
            options.Discovery.CustomizeHandlerDiscovery(query => query.Excludes.WithCondition($"Handler of the disabled module {module.Descriptor.Id}",
                type => type.Namespace is { } ns && (ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal))));
        }
        foreach (var module in modules.Enabled)
        {
            module.ConfigureWolverine(options);
        }
        // Development and tests compile handlers at runtime; Wolverine 6 ships Roslyn apart and ManualOnly does not load it by itself.
        options.UseRuntimeCompilation();
        // Wolverine 6 refuses service location by default; Atlas registers factories (ISession, AccessChanges, Outbox, ContentParticipants,
        // ObjectTypes, IReleasePolicy) and injects IServiceProvider on purpose, so the per-handler warnings (~2600 in `codegen test`)
        // only flooded the system log. A handler that fails to generate or compile still fails.
        options.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;
        // A job's run is not one transaction: its steps make their own (JobRunner).
        options.Policies.AddMiddleware<NHibernateTransactionMiddleware>(chain => chain.MessageType != typeof(Jobs.RunJob));
        // Atlas only invokes handlers inline (IMessageBus.InvokeAsync), which never routes, and no handler returns a handled
        // message. Wolverine's convention would still start a local queue per handled message type (~260), each queueing
        // max(cores, 5) reader tasks on the thread pool at host start: 16 000 work items on 64 cores per host, and in the test
        // process, with dozens of hosts starting at once, 100 000 of them ahead of all other work.
        options.Policies.DisableConventionalLocalRouting();
        // Inline invocation logs neither of these, so they only shape a future queued or durable path.
        // Wolverine's per-message Information line is noise next to the endpoint's own request log.
        options.Policies.MessageSuccessLogLevel(LogLevel.Debug);
        options.Policies.MessageExecutionLogLevel(LogLevel.Debug);
        return options;
    }
}
