using System.Text.Json;
using Atlas.Server.Features.Auth.Contracts;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.Queries;

/// <summary>A user's current AI preferences. Never stored means the defaults. Native SQL because the type list is jsonb.</summary>
public static class CurrentAiPreference
{
    public const string On = "on";
    public static readonly string[] Modes = [On, "rules", "off"];
    /// <summary>The suggestion types of the editor-assist catalogue, in catalogue order.</summary>
    public static readonly string[] SuggestionTypes = ["contact", "page-link", "issue", "structure", "link-mention"];

    public const string DefaultSearchAnswer = "auto";
    public const string DefaultAssistantScope = "context";
    public static readonly string[] SearchAnswers = [DefaultSearchAnswer, "ask", "never"];
    public static readonly string[] AssistantScopes = [DefaultAssistantScope, "wiki"];

    public sealed record Row(string EditorSuggestions, string[] DisabledSuggestionTypes, DateTime ActiveFrom, string SearchAnswer = DefaultSearchAnswer, string AssistantScope = DefaultAssistantScope);

    public static async Task<Row?> LoadAsync(ISession session, Guid userId, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery("""
            SELECT "EditorSuggestions", CAST("DisabledSuggestionTypes" AS text), "ActiveFrom", "SearchAnswer", "AssistantScope"
            FROM "UserAiPreference" WHERE "UserId"=:user AND "ActiveTo" IS NULL
            """).SetParameter("user", userId).ListAsync<object[]>(ct);
        if (rows.Count == 0)
        {
            return null;
        }
        var row = rows[0];
        var types = JsonSerializer.Deserialize<string[]>((string)row[1]) ?? [];
        return new Row((string)row[0], types, DateTime.SpecifyKind((DateTime)row[2], DateTimeKind.Utc), (string)row[3], (string)row[4]);
    }

    public static AiPreferencesView ToView(Row? row)
        => new(row?.EditorSuggestions ?? On, row?.DisabledSuggestionTypes ?? [], row is null ? null : AdministrationVersion.Format(row.ActiveFrom),
            row?.SearchAnswer ?? DefaultSearchAnswer, row?.AssistantScope ?? DefaultAssistantScope);
}
