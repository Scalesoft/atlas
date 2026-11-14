using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi.Queries;

internal static class CurrentApiPermission
{
    public static Task<UserApiPermission?> LoadAsync(ISession session, Guid userId, CancellationToken ct)
    {
        return session.QueryOver<UserApiPermission>()
            .Where(x => x.UserId == userId && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct)!;
    }

    /// <summary>The current level; an account that never had a permission row has none.</summary>
    public static string Level(UserApiPermission? permission) => permission?.Level ?? ApiLevels.None;

    public static ApiPermissionDto ToDto(UserApiPermission? permission)
    {
        var level = Level(permission);
        return new ApiPermissionDto(level != ApiLevels.None, permission is null ? null : AdministrationVersion.Format(permission.ActiveFrom))
        {
            Level = level
        };
    }
}
