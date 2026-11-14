using System.Text.RegularExpressions;
using Atlas.Server.Features.Jobs.Queries;
using Atlas.Server.Platform.Localization;

namespace Atlas.Server.Features.Jobs;

/// <summary>
/// The job and service texts the administration shows (title, description, schedule, retry, interval) in the request's language.
/// The definitions stay Czech (MCP, the API of tokens and logs read them as they are); the cookie endpoints translate the answer:
/// titles and descriptions by the Czech text as the key (<see cref="ErrorTexts"/>), the numbered schedule texts by their pattern.
/// </summary>
public static partial class JobTexts
{
    private static readonly (Regex Pattern, string English)[] Patterns =
    [
        (EveryMinute(), "Every minute"), (EveryHour(), "Every hour"), (EveryDay(), "Every day"),
        (EverySeconds(), "Every $1 s"), (EveryMinutes(), "Every $1 min"), (EveryHours(), "Every $1 h"), (EveryDays(), "Every $1 days"),
        (ByPlan(), "On schedule $1"),
        (Retries(), "Up to $1 attempts, first delay $2, each further one doubled"),
        (UnknownDescription(), "Job $1, which this version of Atlas no longer knows; only its history remains.")
    ];

    private static readonly Dictionary<string, string> Fixed = new(StringComparer.Ordinal)
    {
        ["Po události nebo ručně"] = "On an event or by hand",
        ["Při startu serveru"] = "At server start",
        ["Jednou při startu, dokud neuspěje"] = "Once at start, until it succeeds",
        ["Podle nastavení funkce"] = "By the feature's setting",
        ["Bez opakování"] = "No retries",
        ["Neznámá úloha"] = "Unknown job"
    };

    public static string Text(string language, string czech)
    {
        if (language != RequestLanguage.English)
        {
            return czech;
        }
        if (Fixed.TryGetValue(czech, out var fixedText))
        {
            return fixedText;
        }
        foreach (var (pattern, english) in Patterns)
        {
            if (pattern.IsMatch(czech))
            {
                return pattern.Replace(czech, english);
            }
        }
        return ErrorTexts.Translate(language, czech);
    }

    public static JobView Translate(JobView job, string language)
        => language != RequestLanguage.English ? job : job with
        {
            Title = Text(language, job.Title),
            Description = Text(language, job.Description),
            Schedule = Text(language, job.Schedule),
            Retry = Text(language, job.Retry)
        };

    public static JobList Translate(JobList list, string language)
        => language != RequestLanguage.English ? list : list with
        {
            Jobs = list.Jobs.Select(job => Translate(job, language)).ToList(),
            SystemServices = list.SystemServices.Select(service => service with
            {
                Title = Text(language, service.Title),
                Description = Text(language, service.Description),
                Interval = service.Interval is null ? null : Text(language, service.Interval)
            }).ToList(),
            Summary = list.Summary with { Failures = list.Summary.Failures.Select(failure => failure with { JobTitle = Text(language, failure.JobTitle) }).ToList() }
        };

    public static JobRunList Translate(JobRunList list, string language)
        => language != RequestLanguage.English ? list : list with { Job = Translate(list.Job, language) };

    [GeneratedRegex(@"^Každou minutu$")] private static partial Regex EveryMinute();
    [GeneratedRegex(@"^Každou hodinu$")] private static partial Regex EveryHour();
    [GeneratedRegex(@"^Každý den$")] private static partial Regex EveryDay();
    [GeneratedRegex(@"^Každých (\d+) s$")] private static partial Regex EverySeconds();
    [GeneratedRegex(@"^Každých (\d+) min$")] private static partial Regex EveryMinutes();
    [GeneratedRegex(@"^Každých (\d+) h$")] private static partial Regex EveryHours();
    [GeneratedRegex(@"^Každých (\d+) dní$")] private static partial Regex EveryDays();
    [GeneratedRegex(@"^Podle plánu (.+)$")] private static partial Regex ByPlan();
    [GeneratedRegex(@"^Až (\d+) pokusů, první odklad (.+), každý další dvojnásobný$")] private static partial Regex Retries();
    [GeneratedRegex(@"^Úloha (.+), kterou tato verze Atlasu už nezná; zůstává jen její historie\.$")] private static partial Regex UnknownDescription();
}
