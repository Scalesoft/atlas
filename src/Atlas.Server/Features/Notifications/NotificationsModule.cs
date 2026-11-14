using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Notifications.Commands;
using Atlas.Server.Features.Notifications.Contracts;
using Atlas.Server.Features.Notifications.Queries;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Platform.Live;
using FluentMigrator;
using Wolverine;

namespace Atlas.Server.Features.Notifications;

[Migration(2026102960, "The inbox: notifications, preferences and the page mentions already told")]
public sealed class NotificationsMigration() : EmbeddedSchemaMigration("2026102960_notifications.sql");

[Migration(2026102970, "Page mentions read by ContactSnapshots: the SQL reader of account mentions dropped")]
public sealed class PageMentionReaderMigration() : EmbeddedSchemaMigration("2026102970_page_mention_reader.sql");

/// <summary>One inbox per account (inbox spec 2026-10-29): the store, its reads and the account's live channel. Producers enqueue
/// <see cref="NotificationRequested"/> with <see cref="NotificationOutbox.Notify"/>.</summary>
public static class NotificationsModule
{
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        services.AddScoped<NotificationReads>();
        return services;
    }

    public static void MapNotifications(this IEndpointRouteBuilder app)
    {
        MapRoutes(app.MapGroup("/api").RequireAuthorization(), false);
        MapRoutes(app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
        // An account's own settings: the web's only (inbox spec §9, decision 7).
        app.MapGet("/api/notifications/preferences", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<NotificationPreferences>(new GetNotificationPreferences(), ct)))
            .RequireAuthorization()
            .Produces<NotificationPreferences>();
        app.MapPut("/api/notifications/preferences/{kind}", async (string kind, SetNotificationPreferenceRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<NotificationPreferenceDto>(new SetNotificationPreference(kind, request.Enabled), ct)))
            .RequireAuthorization()
            .Produces<NotificationPreferenceDto>()
            .ProducesProblem(400);
    }

    private static void MapRoutes(RouteGroupBuilder group, bool personalApi)
    {
        var list = group.MapGet("/notifications", async (Guid? before, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<NotificationList>(new ListNotifications(before), ct)))
            .Produces<NotificationList>()
            .WithSummary("The caller's inbox (mentions, shares), newest event first, 30 rows a page after the item `before` (`next` of the previous page). " +
                "Only items whose object the caller may read now; unread is the visible unread count.");
        var unread = group.MapGet("/notifications/unread", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<UnreadNotifications>(new GetUnreadNotifications(), ct)))
            .Produces<UnreadNotifications>()
            .WithSummary("How many unread items the caller may see now, at most 100.");
        var read = group.MapPost("/notifications/{id:guid}/read", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<NotificationsMarked>(new MarkNotificationsRead(id), ct)))
            .Produces<NotificationsMarked>()
            .ProducesProblem(404)
            .WithSummary("Mark one item of the caller's inbox read.");
        var readAll = group.MapPost("/notifications/read-all", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<NotificationsMarked>(new MarkNotificationsRead(null), ct)))
            .Produces<NotificationsMarked>()
            .WithSummary("Mark every item of the caller's inbox read.");
        if (personalApi)
        {
            list.WithName("ListNotifications");
            unread.WithName("GetUnreadNotifications");
            // Personal state, not content: nothing other readers see changes.
            read.WithName("MarkNotificationRead").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.NotificationsRead);
            readAll.WithName("MarkAllNotificationsRead").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.NotificationsRead);
        }
    }
}
