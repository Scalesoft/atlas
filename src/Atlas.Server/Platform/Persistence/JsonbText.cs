using System.Data;
using System.Data.Common;
using NHibernate.Engine;
using NHibernate.SqlTypes;
using NHibernate.UserTypes;
using Npgsql;
using NpgsqlTypes;

namespace Atlas.Server.Platform.Persistence;

/// <summary>A string property stored in a jsonb column: Npgsql sends strings as text, which PostgreSQL does not assign to jsonb,
/// so the parameter is typed jsonb here. Read back as PostgreSQL writes jsonb (its own key order and spacing).</summary>
public sealed class JsonbText : IUserType
{
    public SqlType[] SqlTypes { get; } = [new StringClobSqlType()];

    public Type ReturnedType => typeof(string);

    public bool IsMutable => false;

    public new bool Equals(object? x, object? y) => string.Equals(x as string, y as string, StringComparison.Ordinal);

    public int GetHashCode(object x) => x.GetHashCode();

    public object? NullSafeGet(DbDataReader rs, string[] names, ISessionImplementor session, object owner)
    {
        var ordinal = rs.GetOrdinal(names[0]);
        return rs.IsDBNull(ordinal) ? null : rs.GetString(ordinal);
    }

    public void NullSafeSet(DbCommand cmd, object? value, int index, ISessionImplementor session)
    {
        var parameter = (NpgsqlParameter)cmd.Parameters[index];
        parameter.NpgsqlDbType = NpgsqlDbType.Jsonb;
        parameter.Value = value ?? DBNull.Value;
    }

    public object? DeepCopy(object? value) => value;

    public object? Replace(object? original, object? target, object? owner) => original;

    public object? Assemble(object? cached, object? owner) => cached;

    public object? Disassemble(object? value) => value;
}
