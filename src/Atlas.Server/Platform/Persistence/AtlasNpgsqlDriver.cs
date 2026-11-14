using System.Data;
using System.Data.Common;
using Npgsql;

namespace Atlas.Server.Platform.Persistence;

/// <summary>
/// NHibernate's Npgsql driver whose commands forget their prepared statements when they fail (<see cref="PreparedStatements"/>).
/// </summary>
public sealed class AtlasNpgsqlDriver : NHibernate.Driver.NpgsqlDriver
{
    public override DbCommand CreateCommand() => new ForgettingCommand();

    private sealed class ForgettingCommand : NpgsqlCommand
    {
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            try
            {
                return base.ExecuteDbDataReader(behavior);
            }
            catch
            {
                PreparedStatements.Forget(this);
                throw;
            }
        }

        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            try
            {
                return await base.ExecuteDbDataReaderAsync(behavior, cancellationToken);
            }
            catch
            {
                await PreparedStatements.ForgetAsync(this);
                throw;
            }
        }

        public override int ExecuteNonQuery()
        {
            try
            {
                return base.ExecuteNonQuery();
            }
            catch
            {
                PreparedStatements.Forget(this);
                throw;
            }
        }

        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await base.ExecuteNonQueryAsync(cancellationToken);
            }
            catch
            {
                await PreparedStatements.ForgetAsync(this);
                throw;
            }
        }
    }
}

/// <summary>
/// Sessions auto-prepare (<see cref="AtlasDatabase.WithAutoPrepare"/>), and Npgsql 10.0.3 marks an auto-prepared statement prepared at
/// ParseComplete, before its row description: when the run that prepares it fails while PostgreSQL plans it (a cancelled request, a
/// timeout, an error at Bind), the statement stays "prepared" without a description, and every later run of it on that pooled
/// connection misreads the protocol ("Received unexpected backend message DataRow", "No resultset is currently being traversed").
/// So a command that fails closes its own prepared statements right away: the Close message also works in the aborted transaction,
/// and the next run prepares the statement afresh. Every command on a session's connection that can return rows goes through this:
/// NHibernate's (<see cref="AtlasNpgsqlDriver"/>) and the raw reads (<see cref="SessionExtensions.ReadRowsAsync"/>, RelationAnchor.ReadAllAsync,
/// ContentRowEffects). Remove it once an Npgsql release fixes the order (PlatformTests).
/// </summary>
public static class PreparedStatements
{
    public static void Forget(DbCommand command)
    {
        if (command is NpgsqlCommand { Connection.State: ConnectionState.Open } npgsql)
        {
            try
            {
                npgsql.Unprepare();
            }
            catch (Exception)
            {
                // The command's own failure is the one to report; a connection that breaks here is discarded by Npgsql.
            }
        }
    }

    public static async Task ForgetAsync(DbCommand command)
    {
        if (command is NpgsqlCommand { Connection.State: ConnectionState.Open } npgsql)
        {
            try
            {
                await npgsql.UnprepareAsync();
            }
            catch (Exception)
            {
                // The command's own failure is the one to report; a connection that breaks here is discarded by Npgsql.
            }
        }
    }
}
