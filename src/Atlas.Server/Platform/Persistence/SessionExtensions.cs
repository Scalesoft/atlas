using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Persistence;

public static class SessionExtensions
{
    /// <summary>The instance of this entity the session already holds, without loading it.</summary>
    public static T? Held<T>(this ISession session, object id) where T : class
    {
        var implementor = session.GetSessionImplementation();
        var persister = implementor.Factory.GetEntityPersister(typeof(T).FullName);
        return implementor.PersistenceContext.GetEntity(implementor.GenerateEntityKey(id, persister)) as T;
    }

    /// <summary>
    /// The mapped properties of an entity the session holds that differ from the row it read or last flushed; null while the session
    /// has not inserted it yet (a new entity). Ask before the flush that writes the change: afterwards the row equals the entity again.
    /// </summary>
    public static IReadOnlySet<string>? ChangedProperties(this ISession session, object entity)
    {
        var implementor = session.GetSessionImplementation();
        var entry = implementor.PersistenceContext.GetEntry(entity) ?? throw new InvalidOperationException("The session does not hold this entity.");
        if (!entry.ExistsInDatabase)
        {
            return null;
        }
        var persister = entry.Persister;
        var dirty = persister.FindDirty(persister.GetPropertyValues(entity), entry.LoadedState, entity, implementor) ?? [];
        return dirty.Select(index => persister.PropertyNames[index]).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The entity FOR UPDATE as it is under the lock, in one statement. A locking load of an instance the session already holds
    /// takes the lock but returns the instance as it was read before the lock, so such an instance is refreshed under the lock
    /// instead. An instance this transaction already locked FOR UPDATE or wrote is returned without a statement, as before.
    /// </summary>
    /// <remarks>
    /// "As it is under the lock" holds only while this is the one way an instance reaches LockMode.Upgrade: every locking load of a
    /// Page goes through here (PageLockingLoadConventionTests). And a page is never changed before its gate: the refresh drops
    /// changes that are not flushed yet, and flushing them first is no way out, because a written instance is returned without a
    /// statement, with its other columns as they were loaded before the lock.
    /// </remarks>
    public static async Task<T?> GetForUpdateAsync<T>(this ISession session, object id, CancellationToken ct) where T : class
    {
        if (session.Held<T>(id) is { } held && session.GetCurrentLockMode(held).LessThan(LockMode.Upgrade))
        {
            await session.RefreshAsync(held, LockMode.Upgrade, ct);
            return held;
        }
        return await session.GetAsync<T>(id, LockMode.Upgrade, ct);
    }

    /// <summary>
    /// The rows of a native read straight from the driver, in the session's transaction: for reads of tens of thousands of rows, where
    /// NHibernate's scalar materialization costs several times the statement itself (space contents, ContentsBudgetTests). Parameters
    /// are named as in NHibernate's SQL (:name). Nothing is flushed first: only for reads that need no pending change of the session.
    /// <paramref name="setup"/> is one SELECT sent in the same command before <paramref name="sql"/> (a set_config), its one result set skipped.
    /// </summary>
    public static async Task<List<T>> ReadRowsAsync<T>(this ISession session, string sql, IReadOnlyDictionary<string, object> parameters,
        Func<System.Data.Common.DbDataReader, T> read, CancellationToken ct, string? setup = null)
    {
        await using var command = session.Connection.CreateCommand();
        session.GetCurrentTransaction()?.Enlist(command);
        command.CommandText = setup is null ? sql : setup + ";\n" + sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        var rows = new List<T>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (setup is not null)
            {
                await reader.NextResultAsync(ct);
            }
            while (await reader.ReadAsync(ct))
            {
                rows.Add(read(reader));
            }
        }
        catch
        {
            await PreparedStatements.ForgetAsync(command);
            throw;
        }
        return rows;
    }
}
