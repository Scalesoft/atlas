using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Server.Features.Contacts.Persistence;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;

namespace Atlas.Server.Features.Contacts;

/// <summary>The contact directory: the contact as an object type of the wiki (objects spec §2.3). Its objects live in the envelope
/// (ContentResource, ObjectChange), so the platform's and the Objects module's backups already hold them, their uploaded pictures (ContactPhoto)
/// with the platform's; the runs of its link migration are
/// the job contacts.link (JobRun; the ContactLinkRun table stays one release read-only); the account's suggested contacts are computed when read (AccountContacts).</summary>
public static class ContactsModule
{
    public static IServiceCollection AddContacts(this IServiceCollection services)
    {
        services.AddScoped<ContactDuplicates>();
        services.AddScoped<ContactDirectory>();
        services.AddScoped<ContactLinker>();
        services.AddScoped<ContactLinkMigration>();
        services.AddScoped<AccountContacts>();
        services.AddJob<LinkRun.ContactLinkJob>();
        services.AddJob<LinkRun.AccountContactsJob>();
        // The mentions of contacts in pages: derived by the ContentRevised consumer and rebuilt by its job.
        services.AddScoped<PageObjectMentions>();
        services.AddJob<PageObjectMentionRebuildJob>();
        // The object path's own handlers, run inside the linker's, the merge's and the page save's transaction.
        services.TryAddScoped<Objects.Commands.CreateObjectHandler>();
        services.TryAddScoped<Objects.Commands.TrashObjectHandler>();
        services.TryAddScoped<Objects.Commands.ApplyObjectChangeHandler>();
        // „Propojit“ merges the contact an account has into the chosen one (ResolveAccountContactHandler).
        services.TryAddScoped<Commands.MergeContactsHandler>();
        // A merge points the boards' cards at the target as a board batch (the services as the Boards module has them).
        services.TryAddScoped<Boards.Commands.BoardBatchWriter>();
        services.TryAddSingleton<Boards.BoardBroadcasts>();
        services.AddScoped<ContactObjectType>();
        services.AddScoped<IObjectType>(provider => provider.GetRequiredService<ContactObjectType>());
        services.AddScoped<IObjectRelationSource, ContactMentionRelations>();
        // The entity catalog of contacts (plan §14): its writer, used by the ContentRevised consumer.
        services.AddScoped<Atlas.Server.Features.Entities.EntityCatalog>();
        services.AddJob<Atlas.Server.Features.Entities.EntityCatalogRebuildJob>();
        return services;
    }
}
