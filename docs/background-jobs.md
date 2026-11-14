# Úlohy na pozadí: API pro moduly a pluginy

Úloha je práce, kterou Atlas spouští sám: po události, při startu, v plánovaný čas nebo na žádost správce. Definice je kód, stav je
v tabulkách `Job` (pozastavení, příští termín) a `JobRun` (každý běh s průběhem, souhrnem, chybou a logem). Modul i plugin úlohu
registrují stejně a nepotřebují vlastní tabulku ani migraci (od Atlasu 3.3).

## Definice

```csharp
public sealed class RefreshDashboardsJob(DashboardStore store) : IJobDefinition
{
    public const string JobId = "dashboards.refresh";
    public string Id => JobId;                        // stabilní, klíčuje řádky
    public string Title => "Obnova nástěnek";           // česky, pro seznam úloh
    public string Description => "Načte data nástěnek s cache z jejich zdrojů.";
    public JobSchedule? Schedule => JobSchedule.Every(TimeSpan.FromMinutes(15));

    public async Task RunAsync(JobContext context, CancellationToken ct) { … }
}

// AddServices modulu:
services.AddJob<RefreshDashboardsJob>();
```

- **Modul a práva** se odvodí z namespace (jako u outboxu). Úlohu vidí a ovládá správce wiki. Úloha vypnutého modulu se nezobrazí
  a nespustí, protože její modul nic neregistruje.
- **`Schedule`**: `null` znamená, že úloha běží jen na žádost. `Every(interval)` a `Cron("30 1 * * *")` (Cronos, pět polí, UTC) jsou
  pevný plán. `AtStartup` běží při každém startu, `Once` při startu, dokud jednou neuspěje (`Job.CompletedAt`). `FromData` běží
  v čas, který vrátí `NextDueAsync` z dat funkce (čas zálohy, nejbližší `NextAttemptAt` fronty). Ptá se po startu, po každém běhu
  a po `RescheduleAsync`. Termín v minulosti běží hned, `null` znamená nic.
- **`Retry`** (`JobRetry(pokusy, první prodleva)`): prodleva se mezi pokusy zdvojnásobuje s náhodnou odchylkou ±20 %. Výchozí
  je 5 pokusů (30 s, 1 min, 2 min, 4 min). `JobRetry.None` znamená jeden pokus, hodí se pro pevný plán, jehož příští výskyt je
  opakováním. `JobFailedException(…, retry: false)` ukončí běh hned. Po posledním pokusu je běh `failed` a správci wiki dostanou
  oznámení „Selhala úloha na pozadí“, jedno nepřečtené na úlohu.
- **`RunsWhenQuiet`** (rozhodnutí vlastníka 8. 11. 2026): těžká periodická úloha ustoupí psaní. Její automatický běh začne, až
  nikdo minutu needitoval (živá změna ani uložení stránky, `JobQuiet`); jinak zůstane ve frontě a runner ho přes plánovanou zprávu
  Wolverinu zkusí za minutu znovu. Nejpozději 30 minut po zařazení do fronty běh začne i během psaní. Běh, o který požádal správce
  („Spustit teď“, „Zkusit znovu“), začne hned. Mají ho zálohy (Git, ZIP), `retention`, údržba indexu AI a úklid koše; meze
  nastavuje sekce `JobQuiet` (`Window`, `Postpone`, `Deadline`). Obsluha outboxu po uložení a checkpointy živých stránek ho nemají.

## Žádosti z handlerů

`IJobRequests` (scoped) zapisuje v transakci handleru: rollback nenechá nic, commit pustí běh hned.

- `RequestAsync(jobId, reason, key?, notBefore?)` vloží běh ve stavu `queued`. Žádosti téže úlohy a klíče se slučují, takže sto uložení
  dá nejvýš jeden další běh. `key` odliší nezávislé běhy jedné úlohy (jedna nástěnka), `notBefore` odloží start (usazení po uložení).
  Pozastavená úloha žádost uschová do obnovení. Úloha, kterou host neregistruje, žádost tiše zahodí (log na úrovni Debug).
- `RescheduleAsync(jobId)` po změně dat úlohy `FromData`. `NextDueAsync` se zeptá až po commitu, takže vidí, co handler zapsal.
- SQL migrace může běh vyžádat řádkem: `INSERT INTO "JobRun"("JobId","Trigger","Reason","State") VALUES ('…','event','…','queued')
  ON CONFLICT DO NOTHING`. Start serveru ho pustí.

`IJobControl` (pro UI, API a MCP; volající ověří práva): `RunNowAsync` („Spustit teď“, i u pozastavené úlohy), `RetryAsync` (selhaný
nebo zastavený běh znovu od jeho průběhu), `CancelAsync` (ve frontě hned, běžící u příštího kroku) a `SetPausedAsync`.

## Tělo úlohy

`RunAsync` neběží v transakci. Dlouhá práce je řada krátkých kroků a vzdálená volání (helpdesk, YouTrack, SMB, AI) běží mezi nimi.

- `context.StepAsync((session, ct) => …)`: jeden krok je jedna transakce ve vlastním scope. Vrácená hodnota se uloží jako nový
  `Progress` ve stejné transakci, takže práce a checkpoint se potvrdí spolu.
- `context.Progress<T>()` vrátí poslední potvrzený checkpoint. Po restartu serveru, „Zkusit znovu“ nebo dalším pokusu tělo
  pokračuje od něj, takže krok musí jít zopakovat.
- `context.Summarize(new { pages = 120 })` jsou čísla pro UI (`JobRun.Summary`). Uloží se s dalším krokem a na konci.
- `context.Log` je `ILogger`. Všechno jde do běžného logu a záznamy od Information výš i do `JobRun.Log` (nejvýš 500: prvních 50
  a posledních 450). Logujte počty a id, nikdy text stránek, protože čtenář logu obsah vidět nemusí.
- `context.Services` je scope běhu. Jeho `IMessageBus.InvokeAsync` je jedna transakce jako v handleru.
- `ct` se zruší na žádost „Zastavit“ i při vypnutí serveru. Co se potvrdilo, zůstane. Vypnutý běh pokračuje po startu.

Jedna úloha nikdy neběží dvakrát současně (druhý běh čeká ve frontě). Nejvýš čtyři úlohy běží najednou, aby nevyhladověly pool
připojení. Heartbeat zapisuje `HeartbeatAt` každých 30 s.

## Migrace dat mezi verzemi

DB migrace (FluentMigrator, `Persistence/Migrations`) mění schéma a dělá jen rychlé úpravy dat, které kód nové verze potřebuje hned
(NOT NULL, přejmenovaný sloupec, převod malé tabulky). Běží celá v jedné transakci, než server nastartuje. Převod dat, který trvá déle
nebo prochází obsah (přepis dokumentů, přepočet, přestavba katalogu či indexu), je úloha:

- **Vyžádá ji DB migrace**, která ji potřebuje, řádkem `JobRun` ve stavu `queued` (viz výše), takže je svázaná s verzí schématu.
  Nová instalace bez dat ji přeskočí (`… WHERE EXISTS (…)`, `HAVING count(*) > 0`). Co převod potřebuje ze starého stavu, uloží
  migrace do `Progress`. Vzory: `events.outbox-upgrade` (migrace `2026110421`) a `contacts.link` (migrace `2026110450`).
- **Běží po dávkách** v krátkých krocích (`StepAsync`) s checkpointem v `Progress`: restart, „Zkusit znovu“ i další pokus pokračují,
  kde skončila.
- **Jde pustit znovu.** Úloha přestavuje z aktuálního stavu a je idempotentní: další běh nic nepokazí a srovná, co se mezitím
  rozešlo. Správce ji tak může kdykoli pustit tlačítkem „Spustit teď“ jako opravu (vzor `entities.catalog-rebuild`), a proto po upgradu
  zůstává. Smaže se až v další verzi a jen jednorázový převod ze starého tvaru dat, který už nic nepotřebuje (`events.outbox-upgrade`).
- **Kód zvládá oba tvary dat**, dokud úloha neskončí, protože aplikace mezitím běží. Co kód potřebuje hned, patří do DB migrace.
- **Obsah stránek** převádí stejná oprava, jakou čte kód (`LegacyDocumentRepair`): pracovní a publikovanou verzi na místě (autor a čas
  zůstanou), historii nechá a stránku s neuloženými živými úpravami odloží a vyžádá další běh. Vzor: `pages.requirements-overview`
  (migrace `2026110480` a `2026110550`, i po obnově starší zálohy přes `InstallationModules.DataMigrations`). `pages.requirements-headings`
  převede vše hned po prvním startu: neuložené živé úpravy, do kterých posledních 10 minut nikdo nepsal, nejdřív uloží jako revizi
  jejich autora (`CheckpointLivePage`, kontrolní bod serveru) a stránku převede i s nimi; odloží jen stránku, na které se právě píše.
  `pages.requirement-systems` (migrace `2026111150`) doplní požadavkům bez služby (`system`) službu položky katalogu, ze které vznikly
  (`from`); vyplněné hodnoty nemění a nic nezačne, dokud čeká `pages.requirements-headings`. Stránku, na které se právě píše, odloží a další běh čte jen odložené stránky (vymazanou službu znovu nedoplní); opuštěné neuložené úpravy nejdřív uloží, a když to nejde, běh skončí bez opakování. Po doplnění katalogu ji správce spustí znovu; po obnově zálohy starší než migrace se vyžádá sama (`InstallationModules.DataMigrations`).
- **Test** jako u každé migrace: serverový test nad existujícími řádky (stav před migrací, migrace, `IJobRunner.RunToEndAsync`, výsledek)
  a druhý běh, který už nic nezmění.

## Testy

Testovací hosty bez durability (`AtlasTestHost.CreateAsync(…)`, `AtlasHttpSite`) nespouštějí nic na pozadí. Žádost zůstane `queued`
a test ji pustí pomocí `IJobRunner.RunToEndAsync(jobId, reason, key?)`. Ten běh sloučí s během ve frontě, pokusy pustí hned po sobě
a vrátí id běhu, i když selhal. Plány, opakování po prodlevě a restart ověřuje host s durabilitou
(`AtlasTestHost.CreateAsync(…, durable: true)`, u `AtlasHttpSite` nastavení `Jobs:Durable=true`).
