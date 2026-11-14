using Atlas.Server.Features.Administration.Commands;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Administration.Queries;
using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.Builtin;
using Wolverine;

namespace Atlas.Server.Features.Administration;

public static class AdministrationEndpoints
{
    public static void MapAdministration(this IEndpointRouteBuilder app)
    {
        app.MapProfileAccount();
        var group = app.MapGroup("/api/admin").RequireAuthorization();
        group.MapGet("/users", async (string? q, int? offset, Guid? id, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<IReadOnlyList<UserSummary>>(new ListUsers(q, offset ?? 0, id), ct)));
        group.MapGet("/users/summary", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<UserDirectorySummary>(new GetUserDirectorySummary(), ct)));
        group.MapPost("/users", async (CreateBuiltinUser request, IMessageBus bus, CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<CreatedBuiltinUser>(new CreateUser(request), ct);
            return Results.Created($"/api/admin/users/{result.User.Id}", result);
        });
        group.MapPut("/users/{id:guid}", async (Guid id, UpdateUser request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<UserSummary>(new SaveUser(id, request), ct)));
        group.MapPost("/users/{id:guid}/reset-password", async (Guid id, ResetBuiltinPassword request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<IssuedAccountToken>(new ResetPassword(id, request), ct)));
        group.MapGet("/groups", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<IReadOnlyList<GroupSummary>>(new ListGroups(), ct)));
        group.MapPost("/groups", async (CreateGroup request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<GroupSummary>(new AddGroup(request), ct)));
        group.MapPut("/groups/{id:guid}", async (Guid id, UpdateGroup request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<GroupSummary>(new RenameGroup(id, request), ct)));
        group.MapGet("/groups/{id:guid}/members", async (Guid id, string? q, int? offset, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<IReadOnlyList<GroupMemberSummary>>(new ListGroupMembers(id, q, offset ?? 0), ct)));
        group.MapPut("/groups/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, UpdateMembership request, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetGroupMembership(id, userId, request), ct);
            return Results.NoContent();
        });
        app.MapGet("/api/spaces/{spaceId:guid}/access", async (Guid spaceId, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<SpaceAccessSummary>(new GetSpaceAccess(spaceId), ct))).RequireAuthorization();
        app.MapPut("/api/spaces/{spaceId:guid}/access", async (Guid spaceId, UpdateSpaceGrant request, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetSpaceGrant(spaceId, request), ct);
            return Results.NoContent();
        }).RequireAuthorization();
        app.MapGet("/api/profile/security", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<ProfileSecurity>(new GetProfileSecurity(), ct))).RequireAuthorization();
        app.MapPost("/api/profile/password", async (ChangeOwnPassword request, HttpContext context, IMessageBus bus,
            ISignInService signIn, CancellationToken ct) =>
        {
            ChangedPassword changed;
            try
            {
                changed = await bus.InvokeAsync<ChangedPassword>(new ChangePassword(request), ct);
            }
            catch (ApiException refusal) when (refusal.Code == BuiltinLoginThrottle.RefusalCode)
            {
                context.Response.Headers.RetryAfter = (string)refusal.Extensions!["retryAfter"]!;
                throw;
            }
            await signIn.RefreshSignInAsync(context, new UserAccount
            {
                Id = changed.Id,
                Username = changed.Username,
                DisplayName = changed.DisplayName,
                IsActive = true,
                IsWikiAdmin = changed.IsWikiAdmin,
                SecurityStamp = changed.SecurityStamp
            });
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("authentication");
        app.MapPost("/api/profile/password/local", async (SetOwnLocalPassword request, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetLocalPassword(request), ct);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("authentication");
    }

    public static void MapBuiltinActivation(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/activate", async (ActivateAccount request, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RedeemActivation(request), ct);
            return Results.NoContent();
        }).RequireRateLimiting("activation");
    }
}
