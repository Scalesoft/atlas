using System.Diagnostics;
using Atlas.Server.Features.Hub;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Jobs;

/// <summary>The infrastructure loops that stay outside the jobs (3.3 plan §1 class I, §5 „Systémové služby“), read only for the wiki
/// administrator. Each loop beats its <see cref="SystemServiceHealth"/> id once a pass; the outbox is Wolverine's queue and its row
/// shows the counts of GetOutboxHealth instead.</summary>
public static class SystemServices
{
    public const string DatabaseInitializer = "database-initializer", LiveRefresh = "live-refresh", SecretRelease = "secret-release", Outbox = "outbox",
        ServerTokens = "server-tokens", SearchMisses = "search-misses", AiUsage = "ai-usage", AiProbe = "ai-probe", PageChanges = "page-changes";

    /// <param name="Interval">The pass of the loop; null for one that has no pass to wait for (a one-off at start, a standing connection).</param>
    public sealed record Info(string Id, string Title, string Description, TimeSpan? Interval);

    public static readonly IReadOnlyList<Info> All =
    [
        new(DatabaseInitializer, "Migrace databáze", "Při startu založí a upgraduje schéma databáze; když databáze neodpovídá, zkouší to znovu každých 5 s.", null),
        new(LiveRefresh, "Živé odběry", "Každých 5 s znovu ověří práva živých odběrů otevřených stránek a ukončí ty, které přežily své zobrazení.", TimeSpan.FromSeconds(5)),
        new(SecretRelease, "Odvolání vydaných hesel", "Každých 15 s a po každé změně přístupu odebere vydané heslo tomu, kdo ztratil právo ho číst.", TimeSpan.FromSeconds(15)),
        new(Outbox, "Doručování událostí", "Doručuje události z outboxu jejich konzumentům hned po uložení a selhané zkouší znovu.", null),
        new(ServerTokens, "Tokeny serveru", "Zajistí bootstrap a obnovovací token serveru, aby šel obnovit přístup i bez ostatních služeb.", TimeSpan.FromMinutes(1)),
        new(SearchMisses, "Neúspěšná hledání", "Každých 10 s zapíše do databáze hledání, která nic nenašla.", TimeSpan.FromSeconds(10)),
        new(AiUsage, "Spotřeba AI", "Každých 10 s zapíše do databáze spotřebu modelů AI.", TimeSpan.FromSeconds(10)),
        new(AiProbe, "Dostupnost AI", "Každých 5 s a po změně nastavení ověří, že modely AI odpovídají.", TimeSpan.FromSeconds(5)),
        new(PageChanges, "Změny stránek", "Drží spojení s databází a přeposílá změny stránek živým odběratelům.", null),
        new(HubHealthWatch.ServiceId, "Spojení s Hubem", "Každých 5 s zjistí, v jakém stavu je spojení s Hubem a ověřování identit.", TimeSpan.FromSeconds(5))
    ];

    private static readonly DateTime Started = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    /// <summary>Neodpovídá: no beat for three times the interval (counted from the start of the process before the first one).</summary>
    public static SystemServiceView View(Info info, SystemServiceBeat? beat, DateTime now, string? detail = null, string? error = null)
    {
        var state = info.Interval is { } interval && now - (beat?.At ?? Started) > interval * 3 ? "unresponsive"
            : (beat?.Error ?? error) is not null ? "failed"
            : beat is null && detail is null ? "waiting"
            : "ok";
        return new SystemServiceView(info.Id, info.Title, info.Description, info.Interval is { } every ? JobViews.Every(every) : null, state, beat?.At,
            beat?.Error ?? error, detail);
    }
}

/// <param name="State">ok, failed, unresponsive, or waiting for the first pass.</param>
/// <param name="Error">The first line of the last failure.</param>
/// <param name="Detail">What the loop's own state adds (the outbox: how many wait and how many are dead).</param>
public sealed record SystemServiceView(string Id, string Title, string Description, string? Interval, string State, DateTime? LastBeatAt, string? Error, string? Detail);
