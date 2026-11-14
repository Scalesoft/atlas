using System.Text.Json;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Settings.Queries;

/// <summary>The effective task switch: whether the task runs, for working-text tasks whether that text may reach a
/// cloud profile, and for limitable tasks the daily limit per user (null = none, the default, O10).</summary>
public sealed record AiTaskSwitch(bool Enabled, bool CloudWorkingText, int? DailyLimit = null);

/// <summary>
/// The current AI configuration rows and their admin views. Never stored means off: no master switch row is Vypnuto, no
/// assignment is an unconfigured profile, no feature row means the catalog defaults (AI-24). The configuration cache (A3)
/// reads through the same loaders.
/// </summary>
public static class CurrentAiSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task<AiSetting?> LoadSwitchAsync(ISession session, CancellationToken ct)
        => session.QueryOver<AiSetting>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync(ct)!;

    public static Task<AiProfileAssignment?> LoadAssignmentAsync(ISession session, string kind, CancellationToken ct)
        => session.QueryOver<AiProfileAssignment>().Where(x => x.Kind == kind && x.ActiveTo == null).SingleOrDefaultAsync(ct)!;

    public static Task<AiFeatureSettings?> LoadFeaturesAsync(ISession session, CancellationToken ct)
        => session.QueryOver<AiFeatureSettings>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync(ct)!;

    public static AiSwitchView ToView(AiSetting? row)
        => new(row?.Enabled ?? false, row is null ? null : AdministrationVersion.Format(row.ActiveFrom));

    public static AiProfileView ToView(string kind, AiProfileAssignment? assignment, AiProfileRevision? revision, bool modelLockedByIndex)
    {
        if (assignment is null || revision is null)
        {
            var (timeout, concurrency) = AiProfileRules.Defaults(kind);
            return new(kind, false, false, null, null, null, null, null,
                kind == AiProfileKind.Embeddings ? AiProfileRules.SupportedDimensions : null, "", "", timeout, concurrency,
                null, null, true, false, false, null, null, false, modelLockedByIndex, null);
        }
        return new(kind, true, revision.Enabled, revision.Provider, revision.Location, revision.BaseUrl, revision.Model,
            revision.ModelVersion, revision.Dimensions, revision.QueryPrefix, revision.DocumentPrefix, revision.TimeoutSeconds,
            revision.MaxConcurrency, revision.ReasoningEffort, revision.Temperature, revision.NativeJsonSchema,
            revision.RequestDimensions, revision.UseChatProfile, revision.InputPricePerMillion, revision.OutputPricePerMillion,
            revision.ProtectedApiKey.Length > 0, modelLockedByIndex, AdministrationVersion.Format(assignment.ActiveFrom));
    }

    /// <summary>Every catalog task with its effective switch. Tasks that are not switchable always run (their own owner
    /// governs them); a switchable task the stored JSON does not mention takes its default.</summary>
    public static IReadOnlyDictionary<string, AiTaskSwitch> ResolveTasks(string? tasksJson)
    {
        var stored = string.IsNullOrEmpty(tasksJson)
            ? new Dictionary<string, StoredTask>()
            : JsonSerializer.Deserialize<Dictionary<string, StoredTask>>(tasksJson, Json) ?? [];
        return AiTaskCatalog.All.ToDictionary(task => task.Code, task =>
        {
            if (!task.Switchable)
            {
                return new AiTaskSwitch(true, false);
            }
            stored.TryGetValue(task.Code, out var row);
            return new AiTaskSwitch(row?.Enabled ?? task.EnabledByDefault,
                task.Input == AiInputClass.WorkingText && (row?.CloudWorkingText ?? false),
                task.Limitable && row?.DailyLimit is >= 1 and <= AiTaskCatalog.MaxDailyLimit ? row.DailyLimit : null);
        }, StringComparer.Ordinal);
    }

    public static string SerializeTasks(IReadOnlyDictionary<string, AiTaskSwitch> tasks)
        => JsonSerializer.Serialize(AiTaskCatalog.All.Where(x => x.Switchable)
            .ToDictionary(x => x.Code, x => new StoredTask(tasks[x.Code].Enabled, tasks[x.Code].CloudWorkingText, tasks[x.Code].DailyLimit)), Json);

    public static AiFeaturesView ToView(AiFeatureSettings? row)
    {
        var tasks = ResolveTasks(row?.TasksJson);
        return new(AiTaskCatalog.All.Where(x => x.Switchable)
                .Select(x => new AiFeatureItem(x.Code, x.Group!, tasks[x.Code].Enabled, x.ProfileKind,
                    x.Input == AiInputClass.WorkingText ? tasks[x.Code].CloudWorkingText : null, x.Limitable, tasks[x.Code].DailyLimit))
                .ToArray(),
            row is null ? null : AdministrationVersion.Format(row.ActiveFrom));
    }

    /// <summary>A stored task; <see cref="DailyLimit"/> is left out of the JSON when there is none, so revisions written before
    /// phase 3 and those without a limit look the same.</summary>
    private sealed record StoredTask(bool? Enabled, bool? CloudWorkingText,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? DailyLimit = null);
}
