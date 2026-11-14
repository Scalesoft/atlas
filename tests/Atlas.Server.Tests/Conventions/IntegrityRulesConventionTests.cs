using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Conventions;

/// <summary>
/// The append-only and no-delete rules of audit and access history hold in the application since 3.6 (migration 2026110945 dropped the
/// guard triggers): IntegrityRules for every NHibernate write (IntegrityRulesTests), and this test for native SQL, HQL and LINQ DML. It
/// reads every string literal of the server assembly and every migration from 2026110945 on: an audit fact is never updated, merged,
/// upserted, truncated or deleted, an access history row is only closed (SET "ActiveTo" alone, with "ActiveTo" IS NULL in the
/// statement's own WHERE) and never deleted, a row only the session can check is never written by native SQL, and assistant messages
/// are deleted in one place, which clears their feedback notes first. By the owner's decision these rules hold through the application
/// only, not for psql or other tools. What it cannot see: SQL put together from pieces, and the restore, which replaces a whole
/// installation (InstallationRestore).
/// </summary>
[Trait("Category", "Guard")]
public sealed partial class IntegrityRulesConventionTests
{
    /// <summary>Facts: inserted, never changed (were atlas_access_audit_immutable and atlas_ai_revision_immutable); the runtime database
    /// role holds no UPDATE, DELETE or TRUNCATE on them either.</summary>
    private static readonly string[] Facts = [.. RuntimeDatabaseRole.AppendOnly];

    /// <summary>Access history: an interval closes once and is never deleted (was atlas_access_audit_history_guard); the runtime database
    /// role may update only "ActiveTo".</summary>
    private static readonly string[] History = [.. RuntimeDatabaseRole.Intervals];

    /// <summary>Rows whose rules compare the old and the new state (IntegrityRules): written through the session only. A Hub identity
    /// state may be deleted (a replaced identity), as before.</summary>
    private static readonly string[] SessionOnly = ["SecurityConfiguration", "TrustedAddress", "VaultAccessRequest", "HubGroupBinding", "HubIdentityState"];

    /// <summary>The one deliberate rewrite of an audit fact, by its site and exact statement: the provider id was renamed in 5.0; the audit
    /// fact keeps its meaning (migration 2026111400 and its restore data migration in InstallationModules.DataMigrations).</summary>
    private const string ProviderRename = """UPDATE "AiProfileRevision" SET "Provider" = 'gateway' WHERE lower("Provider") = 'strata'""";

    private static readonly (string Site, string Statement)[] Renamed =
    [
        ("2026111400_gateway_provider_id.sql", ProviderRename),
        ("Atlas.Server.Features.Backups.Installation.InstallationModules..cctor", ProviderRename),
    ];

    /// <summary>The entity names of the guarded tables, for HQL and LINQ bulk writes.</summary>
    private static readonly string[] Entities =
    [
        "GroupMember", "SpaceGrant", "ResourceGrant", "UserApiPermission", "ProtectionSetting", "HubConnectionRevision", "McpSetting", "GroupAiPolicy",
        "UserAiPolicy", "AiProfileRevision", "VaultAccessRequest", "SecurityConfiguration", "TrustedAddress", "HubGroupBinding", "HubIdentityState",
        "PageSourceEditEvent",
    ];

    [Fact]
    public void Native_SQL_never_rewrites_audit_or_access_history()
    {
        var problems = new List<string>();
        var closes = 0;
        var renamed = new HashSet<(string Site, string Statement)>();
        foreach (var (site, text) in ServerIl.Literals.Select(literal => (literal.Site, literal.Text)).Concat(Migrations()))
        {
            foreach (Match statement in Write().Matches(text))
            {
                var verb = Regex.Replace(statement.Groups["verb"].Value.ToUpperInvariant(), @"\s+", " ");
                if (verb == "INSERT INTO" && !Upsert().IsMatch(statement.Value))
                {
                    continue;
                }
                var tables = verb.StartsWith("TRUNCATE", StringComparison.Ordinal)
                    ? QuotedName().Matches(statement.Value).Select(match => match.Groups[1].Value).ToArray()
                    : [statement.Groups["table"].Value];
                foreach (var table in tables)
                {
                    if (Facts.Contains(table) && Renamed.Contains((site, statement.Value)))
                    {
                        renamed.Add((site, statement.Value));
                    }
                    else if (Facts.Contains(table))
                    {
                        problems.Add($"{site}: {verb} of the audit fact {table}.");
                    }
                    else if (History.Contains(table) && verb == "UPDATE" && Closes(statement.Value))
                    {
                        closes++;
                    }
                    else if (History.Contains(table))
                    {
                        problems.Add($"{site}: {verb} of the access history {table} other than closing an open interval.");
                    }
                    else if (SessionOnly.Contains(table) && !(verb == "DELETE FROM" && table == "HubIdentityState"))
                    {
                        problems.Add($"{site}: native {verb} of {table}, whose rules only the session checks (IntegrityRules).");
                    }
                    else if (table is "AiConversation" or "AiMessage" && verb is "DELETE FROM" or "TRUNCATE" or "TRUNCATE TABLE" or "MERGE INTO"
                        && site != "Atlas.Server.Features.Assistant.Persistence.ConversationStore.DeleteAsync")
                    {
                        problems.Add($"{site}: deletes assistant messages outside ConversationStore.DeleteAsync, which clears their feedback notes.");
                    }
                }
            }
            foreach (Match bulk in Hql().Matches(text))
            {
                if (Entities.Contains(bulk.Groups["entity"].Value))
                {
                    problems.Add($"{site}: HQL bulk {bulk.Groups["verb"].Value} of {bulk.Groups["entity"].Value} bypasses IntegrityRules.");
                }
            }
        }
        // LINQ DML (session.Query<T>().Update/Delete/InsertInto...) runs as one statement past the session's listeners.
        problems.AddRange(ServerIl.Calls
            .Where(call => call.Callee.DeclaringType is { Namespace: "NHibernate.Linq" } && call.Callee.IsGenericMethod && Dml().IsMatch(call.Callee.Name)
                && Entities.Concat(Facts).Concat(History).Contains(call.Callee.GetGenericArguments()[0].Name))
            .Select(call => $"{call.Site}: LINQ {call.Callee.Name} of {call.Callee.GetGenericArguments()[0].Name} bypasses IntegrityRules."));
        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
        Assert.True(renamed.Count == Renamed.Length, "An exempted rewrite of an audit fact is gone, remove it from Renamed: "
            + string.Join(", ", Renamed.Except(renamed).Select(x => x.Site)));
        // The scan sees the closing statements of access history, so finding nothing else is not a scan that sees nothing.
        Assert.True(closes >= 15, $"The scan sees only {closes} closing statements of access history.");
    }

    /// <summary>What counts as closing an open interval, and what does not.</summary>
    [Theory]
    [InlineData("""UPDATE "GroupMember" SET "ActiveTo"=now() WHERE "Id"=:id AND "ActiveTo" IS NULL""", true)]
    [InlineData("""update only public."GroupMember" g set "ActiveTo"=now() where g."Id"=:id and g."ActiveTo" is null returning g."Id" """, true)]
    [InlineData("""WITH c AS (UPDATE "GroupMember" SET "ActiveTo"=now() WHERE "ActiveTo" IS NULL RETURNING "Id") SELECT * FROM c""", true)]
    [InlineData("""UPDATE "GroupMember" SET "ActiveTo"=now() WHERE "Id" IN (SELECT "Id" FROM "GroupMember" WHERE "ActiveTo" IS NULL)""", false)]
    [InlineData("""UPDATE "GroupMember" SET "ActiveTo"=now() WHERE "Id"=:id OR "ActiveTo" IS NULL""", false)]
    [InlineData("""UPDATE "GroupMember" SET "ActiveTo"=now(),"Role"=2 WHERE "ActiveTo" IS NULL""", false)]
    [InlineData("""UPDATE "GroupMember" SET "ActiveTo"=now() WHERE "Id"=:id""", false)]
    public void An_access_history_update_closes_only_with_an_open_interval_in_its_own_WHERE(string statement, bool closes)
        => Assert.Equal(closes, Closes(Write().Match(statement).Value));

    /// <summary>Every form of rewrite the scan must see, with the table it names.</summary>
    [Theory]
    [InlineData("""truncate table "AccessAuditEvent", "Other" """, "AccessAuditEvent")]
    [InlineData("""MERGE INTO "SpaceGrant" g USING x ON true WHEN MATCHED THEN DELETE""", "SpaceGrant")]
    [InlineData("""INSERT INTO "PageAccessEvent"("Id") VALUES (1) ON CONFLICT ("Id") DO UPDATE SET "Action"='x'""", "PageAccessEvent")]
    [InlineData("""delete from only "ResourceGrant" where true""", "ResourceGrant")]
    [InlineData("""UPDATE atlas."HubGroupBinding" SET "Enabled"=false""", "HubGroupBinding")]
    public void A_rewrite_of_a_guarded_table_is_seen(string statement, string table)
    {
        var match = Write().Match(statement);
        Assert.Equal(table, match.Groups["table"].Value);
        Assert.True(!match.Groups["verb"].Value.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || Upsert().IsMatch(match.Value));
    }

    [Fact]
    public void Every_guarded_table_with_a_mapping_is_held_by_IntegrityRules_and_none_is_aged_by_retention()
    {
        var mapped = typeof(AtlasDatabase).Assembly.GetTypes()
            .Where(type => type.BaseType is { IsGenericType: true } baseType && baseType.GetGenericTypeDefinition() == typeof(NHibernate.Mapping.ByCode.Conformist.ClassMapping<>))
            .Select(type => ServerIl.Literals.FirstOrDefault(literal => literal.Type == type && literal.Text.StartsWith('`'))?.Text.Trim('`'))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        var guarded = Facts.Concat(History).Concat(SessionOnly).ToArray();
        Assert.Empty(guarded.Where(mapped.Contains).Except(IntegrityRules.GuardedTables));
        Assert.Contains("GroupMember", IntegrityRules.GuardedTables);
        Assert.Empty(Atlas.Server.Platform.Retention.OperationalRetention.Logs.Select(log => log.Table).Intersect(guarded));
    }

    /// <summary>The migrations from 2026110945 on (the guards hold in the application since), each named by its file.</summary>
    private static IEnumerable<(string Site, string Text)> Migrations([CallerFilePath] string path = "")
    {
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path))))!,
            "src", "Atlas.Server", "Persistence", "Migrations");
        var files = Directory.EnumerateFiles(folder, "*.sql")
            .Where(file => long.TryParse(Path.GetFileName(file).Split('_')[0], out var version) && version >= 2026110945).ToArray();
        Assert.Contains(files, file => Path.GetFileName(file).StartsWith("2026110945_", StringComparison.Ordinal));
        return files.Select(file => (Path.GetFileName(file), File.ReadAllText(file)));
    }

    /// <summary>SET "ActiveTo" and nothing else, on rows whose ActiveTo is still NULL by the statement's own WHERE: outside every
    /// parenthesis (a subquery, or a CTE around the statement) and with no OR beside it.</summary>
    private static bool Closes(string statement)
    {
        var set = Set().Match(statement);
        if (!set.Success || !Assignment().Matches(set.Groups["set"].Value).Select(match => match.Groups[1].Value).Distinct().SequenceEqual(["ActiveTo"]))
        {
            return false;
        }
        var own = new StringBuilder();
        var depth = 0;
        foreach (var c in statement[set.Index..])
        {
            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
            if (depth < 0)
            {
                break;
            }
            own.Append(depth == 0 && c != ')' ? c : ' ');
        }
        var where = OwnWhere().Match(own.ToString());
        return where.Success && OpenInterval().IsMatch(where.Groups["where"].Value) && !Or().IsMatch(where.Groups["where"].Value);
    }

    // One statement from the verb and its table to the end of the statement (a semicolon or the end of the text). TRUNCATE lists its
    // tables (QuotedName); an INSERT counts only as an upsert (Upsert).
    [GeneratedRegex("""(?<verb>UPDATE|DELETE\s+FROM|TRUNCATE(?:\s+TABLE)?|MERGE\s+INTO|INSERT\s+INTO)\s+(?:ONLY\s+)?(?:\w+\.)?"(?<table>\w+)"[^;]*""", RegexOptions.IgnoreCase)]
    private static partial Regex Write();

    [GeneratedRegex("\"(\\w+)\"")]
    private static partial Regex QuotedName();

    [GeneratedRegex("""\bON\s+CONFLICT\b[^;]*\bDO\s+UPDATE\b""", RegexOptions.IgnoreCase)]
    private static partial Regex Upsert();

    [GeneratedRegex("""\bSET\s+(?<set>.*?)(\bWHERE\b|\bFROM\b|\bRETURNING\b|$)""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Set();

    [GeneratedRegex("""(?:\w+\.)?"(\w+)"\s*=(?!=)""")]
    private static partial Regex Assignment();

    [GeneratedRegex("""\bWHERE\b(?<where>.*?)(\bRETURNING\b|$)""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex OwnWhere();

    [GeneratedRegex("""(?:\w+\.)?"ActiveTo"\s+IS\s+NULL""", RegexOptions.IgnoreCase)]
    private static partial Regex OpenInterval();

    [GeneratedRegex(@"\bOR\b", RegexOptions.IgnoreCase)]
    private static partial Regex Or();

    [GeneratedRegex("""\b(?<verb>update|delete)\s+(?:from\s+)?(?<entity>[A-Za-z]\w+)\b""", RegexOptions.IgnoreCase)]
    private static partial Regex Hql();

    [GeneratedRegex("^(Update|UpdateVersioned|UpdateBuilder|Delete|InsertInto|InsertBuilder)(Async)?$")]
    private static partial Regex Dml();
}
