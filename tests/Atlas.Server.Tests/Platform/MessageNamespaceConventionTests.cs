using System.Runtime.CompilerServices;
using Atlas.Server.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Runtime;
using Xunit;

namespace Atlas.Server.Tests.Platform;

[Trait("Category", "Guard")]
public sealed class MessageNamespaceConventionTests
{
    /// <summary>
    /// Two runtime behaviours hang on the namespace of a message type and nothing else: TransactionIntent commits a
    /// message declared in a "…Queries" namespace without waiting for the WAL flush, and AtlasMessageLogging silences
    /// Wolverine's Error log of an expected ApiException only for "Atlas.Server.Features.….Commands|Queries.Type".
    /// Moving a message between namespaces therefore changes durability or logging without any diff that shows it.
    /// This pins the handled messages that do not follow the Commands/Queries convention, with the behaviour each has
    /// today. The list is exact: a message that falls outside the convention without being listed, a listed one whose
    /// behaviour changed, and a listed one that is gone (moved, renamed, removed or now conventional) each fail the test and
    /// name the type. A new message belongs in a Commands or Queries namespace; pin it here only on purpose.
    /// The limit: messages inside the convention are not pinned. One moved from Commands to Queries (durable to non-durable,
    /// or the reverse) is conventional on both sides and passes; and a pinned message moved into Queries is reported only
    /// as gone from the list, not as a change of commit behaviour.
    /// Both rules are asked of the production code: the handled messages come from the Wolverine configuration the
    /// application uses (with the same extension discovery), commit intent from TransactionIntent and logging from the real
    /// category filter.
    /// The Smoke tag is deliberate: tests/changed.ps1 runs only the tests of the touched feature, so without it a moved
    /// message would meet this guard only in the full suite.
    /// </summary>
    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Messages_outside_the_Commands_and_Queries_namespaces_keep_their_commit_and_log_behaviour()
    {
        var server = typeof(PlatformServiceCollectionExtensions).Assembly;
        using var host = Host.CreateDefaultBuilder()
            // Every optional module on: their messages follow the same convention.
            .UseWolverine(options => options.ConfigureAtlasWolverine(TestModules.AllEnabled), ExtensionDiscovery.ManualOnly)
            .ConfigureLogging(logging => logging.ClearProviders())
            .Build();
        await host.StartAsync();
        // The calling assembly is discovered too (this one, with its probe messages); the application has only the server.
        var messages = ((WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>()).Handlers.Chains
            .Select(chain => chain.MessageType)
            .Where(type => type.Assembly == server)
            .Distinct()
            .ToList();
        using var categories = LoggerFactory.Create(logging => logging.AddProvider(new CaptureLoggerProvider()).SilenceMessageCategories(server));

        var outside = new SortedDictionary<string, Behaviour>(StringComparer.Ordinal);
        foreach (var type in messages)
        {
            var behaviour = Observe(type, categories);
            if (behaviour != Convention(type))
            {
                outside[type.FullName!] = behaviour;
            }
        }

        var problems = new List<string>();
        foreach (var (name, behaviour) in outside)
        {
            if (!Pinned.TryGetValue(name, out var pinned))
            {
                problems.Add($"{name}: not pinned, but outside the convention; as it stands it has a {behaviour}.");
            }
            else if (pinned != behaviour)
            {
                problems.Add($"{name}: pinned with a {pinned}, but now has a {behaviour}.");
            }
        }
        problems.AddRange(Pinned.Keys.Where(name => !outside.ContainsKey(name))
            .Select(name => $"{name}: pinned, but no longer a handled message outside the convention (moved, renamed, removed or now conventional)."));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>What a message type gets today: TransactionIntent and the error-log filter, both production code.</summary>
    private static Behaviour Observe(Type type, ILoggerFactory categories)
    {
        if (type.IsAbstract)
        {
            throw new InvalidOperationException($"{type.FullName} is handled as an abstract type, so its commit intent cannot be told from its namespace.");
        }
        var intent = new TransactionIntent();
        intent.Begin(RuntimeHelpers.GetUninitializedObject(type));
        // Wolverine logs under a category equal to the message type (see MessageLogFilterTests).
        return new Behaviour(intent.IsRead, !categories.CreateLogger(type).IsEnabled(LogLevel.Error));
    }

    /// <summary>The behaviour the convention promises, or null for a namespace that is not Commands or Queries.</summary>
    private static Behaviour? Convention(Type type)
    {
        if (type.Namespace?.EndsWith(".Commands", StringComparison.Ordinal) == true)
        {
            return new Behaviour(false, true);
        }
        return type.Namespace?.EndsWith(".Queries", StringComparison.Ordinal) == true ? new Behaviour(true, true) : null;
    }

    private readonly record struct Behaviour(bool CommitsWithoutWalFlush, bool ErrorLogSilenced)
    {
        public override string ToString()
        {
            return (CommitsWithoutWalFlush ? "commit without WAL flush" : "durable commit") + " and " + (ErrorLogSilenced ? "silenced" : "logged") + " expected failures";
        }
    }

    private static readonly Behaviour DurableAndLogged = new(false, false);

    /// <summary>Every handled message outside a Commands or Queries namespace, with its behaviour today. Pinned, not endorsed.</summary>
    private static readonly Dictionary<string, Behaviour> Pinned = new(StringComparer.Ordinal)
    {
        ["Atlas.Server.Features.Ai.Access.GetAiAccess"] = DurableAndLogged,
        ["Atlas.Server.Features.AppLogs.ListAppLog"] = DurableAndLogged,
        // Consumers' own deliveries of an outbox event, by the dispatcher after its producer's commit: not a person's command.
        ["Atlas.Server.Features.AiIndex.IndexTaskRevision"] = DurableAndLogged,
        ["Atlas.Server.Features.Backups.Git.GitBackupRevision"] = DurableAndLogged,
        ["Atlas.Server.Features.Collaboration.LiveSeedRevision"] = DurableAndLogged,
        ["Atlas.Server.Features.Entities.EntityCatalogRevision"] = DurableAndLogged,
        ["Atlas.Server.Features.Notifications.PageMentionRevision"] = DurableAndLogged,
        ["Atlas.Server.Features.PagePdfExports.PagePdfExportRevision"] = DurableAndLogged,
        ["Atlas.Server.Features.Projector.Persistence.ProjectorIndexRevision"] = DurableAndLogged,
        ["Atlas.Server.Features.PageLinks.PageLinkRevision"] = DurableAndLogged,
        ["Atlas.Server.Features.Contacts.PageObjectMentionRevision"] = DurableAndLogged,
        // The scheduled check whether a version of the page history closed by a pause (3.6-C): the system's, not a person's command.
        ["Atlas.Server.Features.Pages.Domain.VersionPauseCheck"] = DurableAndLogged,
        ["Atlas.Server.Platform.Linking.ContentReferenceRevision"] = DurableAndLogged,
        ["Atlas.Server.Platform.Linking.PlaceLexiconRevision"] = DurableAndLogged,
        // The outbox's health and retry for Správa wiki: platform messages, so outside the Features error-log filter.
        ["Atlas.Server.Platform.Events.Commands.RetryDeadOutboxMessages"] = DurableAndLogged,
        // The upgrade job's one send of the Atlas outbox's undelivered rows (migration 2026110421): the system's, not a person's command.
        ["Atlas.Server.Platform.Events.Commands.SendUpgradedOutboxMessages"] = DurableAndLogged,
        ["Atlas.Server.Platform.Events.Queries.GetOutboxHealth"] = new(true, false),
        // The job queues (3.3 plan §4.2): a run's attempt, a planned occurrence and a plan, by the system, not a person's command.
        ["Atlas.Server.Platform.Jobs.PlanJob"] = DurableAndLogged,
        ["Atlas.Server.Platform.Jobs.RequestJob"] = DurableAndLogged,
        ["Atlas.Server.Platform.Jobs.RunJob"] = DurableAndLogged,
        // An outbox message a producer enqueues for the inbox (inbox spec §3): not a person's command.
        ["Atlas.Server.Features.Notifications.NotificationRequested"] = DurableAndLogged,
        ["Atlas.Server.Features.Ai.Access.GetMyAiPolicy"] = DurableAndLogged,
        ["Atlas.Server.Features.Ai.Access.GetUserAiPolicy"] = DurableAndLogged,
        ["Atlas.Server.Features.Ai.Access.ReadAiPermission"] = DurableAndLogged,
        ["Atlas.Server.Features.Ai.Access.SaveAiAccess"] = DurableAndLogged,
        ["Atlas.Server.Features.Ai.Access.SaveUserAiPolicy"] = DurableAndLogged,
        ["Atlas.Server.Features.Ai.Settings.SavedProfiles.ActivateSavedProfile"] = DurableAndLogged,
        ["Atlas.Server.Features.Ai.Settings.SavedProfiles.BeginSavedProfileTest"] = DurableAndLogged,
        ["Atlas.Server.Features.Ai.Settings.SavedProfiles.CompleteSavedProfileTest"] = DurableAndLogged,
        ["Atlas.Server.Features.Ai.Settings.SavedProfiles.DeleteSavedProfile"] = DurableAndLogged,
        ["Atlas.Server.Features.Ai.Settings.SavedProfiles.GetSavedProfile"] = DurableAndLogged,
        ["Atlas.Server.Features.Ai.Settings.SavedProfiles.ListSavedProfiles"] = DurableAndLogged,
        ["Atlas.Server.Features.Ai.Settings.SavedProfiles.PrepareSavedModelDiscovery"] = DurableAndLogged,
        ["Atlas.Server.Features.Ai.Settings.SavedProfiles.SaveSavedProfile"] = DurableAndLogged,
        ["Atlas.Server.Features.Ai.Settings.SavedProfiles.SnapshotCurrentProfile"] = DurableAndLogged,
        ["Atlas.Server.Features.Backups.RequireBackupAdministrator"] = DurableAndLogged,
        ["Atlas.Server.Features.Backups.RevalidateBackupDownload"] = DurableAndLogged,
        ["Atlas.Server.Features.Hub.Migration.ApplyHubMigration"] = DurableAndLogged,
        ["Atlas.Server.Features.Hub.Migration.PrepareHubMigration"] = DurableAndLogged,
        ["Atlas.Server.Features.Hub.Migration.ReadHubMigrationInventory"] = DurableAndLogged,
        ["Atlas.Server.Features.KnowledgeMaintenance.DismissDuplicatePages"] = DurableAndLogged,
        ["Atlas.Server.Features.KnowledgeMaintenance.GetAiQualityReport"] = DurableAndLogged,
        ["Atlas.Server.Features.KnowledgeMaintenance.GetAiUsageReport"] = DurableAndLogged,
        ["Atlas.Server.Features.KnowledgeMaintenance.GetDuplicatePages"] = DurableAndLogged,
        ["Atlas.Server.Features.PageDecorations.GetPageDecoration"] = DurableAndLogged,
        ["Atlas.Server.Features.PageDecorations.SetPageDecoration"] = DurableAndLogged,
        ["Atlas.Server.Features.PagePdfExports.GetPagePdfExport"] = DurableAndLogged,
        ["Atlas.Server.Features.PagePdfExports.RunPagePdfExport"] = DurableAndLogged,
        ["Atlas.Server.Features.PagePdfExports.SavePagePdfExport"] = DurableAndLogged,
        ["Atlas.Server.Features.PageTags.SetPageTags"] = DurableAndLogged,
        ["Atlas.Server.Features.PageTags.ChangePageTags"] = DurableAndLogged,
        ["Atlas.Server.Features.PageTemplates.ListPageTemplates"] = DurableAndLogged,
        ["Atlas.Server.Features.PageTemplates.SetPageTemplate"] = DurableAndLogged,
        ["Atlas.Server.Features.PersonalLibrary.GetActivity"] = DurableAndLogged,
        ["Atlas.Server.Features.PersonalLibrary.GetActivityChanges"] = DurableAndLogged,
        ["Atlas.Server.Features.PersonalLibrary.GetPageReadership"] = DurableAndLogged,
        ["Atlas.Server.Features.PersonalLibrary.GetPopularPages"] = DurableAndLogged,
        ["Atlas.Server.Features.StorageProfiles.RequireStorageProfileAdministrator"] = DurableAndLogged,
        ["Atlas.Server.Features.Vaults.References.GetSecretReference"] = DurableAndLogged,
        ["Atlas.Server.Features.Vaults.References.SearchSecretReferences"] = DurableAndLogged,
        ["Atlas.Server.Features.Vaults.Release.BeginSecretRelease"] = DurableAndLogged,
        ["Atlas.Server.Features.Vaults.Release.CompleteSecretRelease"] = DurableAndLogged,
        ["Atlas.Server.Features.Vaults.Release.RecordReleaseFailure"] = DurableAndLogged,
        ["Atlas.Server.Features.Vaults.Release.ReportSecretRelease"] = DurableAndLogged,
        ["Atlas.Server.Features.Vaults.Requests.ApproveAccessRequest"] = DurableAndLogged,
        ["Atlas.Server.Features.Vaults.Requests.CancelAccessRequest"] = DurableAndLogged,
        ["Atlas.Server.Features.Vaults.Requests.RejectAccessRequest"] = DurableAndLogged,
        ["Atlas.Server.Features.Vaults.Requests.RequestSecretAccess"] = DurableAndLogged,
        ["Atlas.Server.Features.Vaults.Requests.RequestVaultAccess"] = DurableAndLogged,
    };
}
