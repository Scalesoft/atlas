using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Vaults.References;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Collaboration;

public static class CollaborationModule
{
    public static IServiceCollection AddCollaboration(this IServiceCollection services)
    {
        services.AddScoped<CollaborationCheckpoints>();
        services.AddScoped<CollaborationAccess>();
        services.AddScoped<CollaborationLog>();
        services.AddScoped<CollaborationSecretReferences>();
        services.AddSingleton<RevisionSecretReferences>();
        services.AddSecretReferences();
        services.AddSingleton<CollaborationSessions>();
        services.AddSingleton<PostgresCollaborationStore>();
        services.AddSingleton<IDocumentStore>(provider => provider.GetRequiredService<PostgresCollaborationStore>());
        services.AddScoped<ICollabAuthenticator, AtlasCollaborationAuthenticator>();
        services.AddScoped<Pages.Live.IPageChangeObserver, CollaborationGenerationFence>();
        services.AddSignalR(options => options.MaximumReceiveMessageSize = 8 * 1024 * 1024)
            // A tab in the background (frozen or throttled by the browser) keeps its live connection for 2 minutes without a ping
            // instead of 30 s; the server still pings every 15 s, the interval the editor's client assumes (transport.ts).
            .AddHubOptions<MarkdownSyncHub>(options =>
            {
                options.ClientTimeoutInterval = TimeSpan.FromMinutes(2);
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            });
        // One update may be as large as the document may (MaxDocumentBytes, 4 MiB): a big paste is one update (a 750 KB document with a
        // link per paragraph is 1.3 MB), and the update limit never refuses what the document limit allows. The editor merges waiting
        // edits only up to 1 MiB; the rate limit (MaxUpdatesPerSecond) bounds how often.
        services.AddSingleton(new DocumentAuthorityOptions { MaxUpdateBytes = new MarkdownSyncOptions().MaxDocumentBytes });
        services.AddAtlasMarkdownSync(options =>
        {
            // Owner, 2026-11-08: revocation reaches live editors at once through AccessChanges after every committed access change;
            // the periodic refresh is the safety net (grant expiry, rows changed by hand), so a minute instead of 5 s.
            options.AccessRefreshInterval = TimeSpan.FromSeconds(60);
            // A page left for a coffee is still in memory when its editor comes back: no reload and full rejoin of the state.
            options.WarmKeep = TimeSpan.FromMinutes(10);
        });
        // WP3 Yjs boundary: every update, snapshot and seed is validated against the host manifest (HostDocumentValidator) before it is
        // stored or broadcast; violations are refused with [INVALID_CONTENT]. The default MaxDocumentBytes (4 MiB) matches
        // PostgresCollaborationStore.MaxStateBytes. WP4 V6: password references (reserved nodes) are admitted only within the
        // reference contract and when the author and every current peer may Discover the secret (SecretReferenceNodeGuard).
        // 3.6 retired the requirement block and Atlas 3.3's requirement summary (the content repair makes them headings and an overview);
        // a 3.5 tab still bound to a 3.5 generation (reconnected after the upgrade, or seeding a page the server could not) could insert them.
        services.AddAtlasMarkdownContentGuard(YjsContentPolicy.FromValidator(HostDocumentValidator.Instance, retired: ["requirement", "requirementSummary"]));
        services.AddSingleton<IReservedNodeGuard, SecretReferenceNodeGuard>();
        // 3.6-H: agents' writes into pages in live editing are collaborators' updates; 3.6: the server seeds every page's live state
        // (LiveSeeds), so the page editor never does. Both by the managed document authority (no Node.js on the server).
        services.AddScoped<LivePageWrites>();
        services.AddSingleton<LiveSeeds>().AddJob<LiveSeedsJob>();
        services.AddSingleton<ILiveWriteAuthority, ManagedLiveWriteAuthority>();
        services.AddSingleton<LiveRequirementsUpgrade>();
        return services;
    }
}
