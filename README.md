# Atlas

Atlas je firemní wiki, kterou si provozujete sami, s živým spolupracujícím editorem Markdownu. Jeden proces ASP.NET Core
obsluhuje API i frontend ve Vue 3; všechna data drží PostgreSQL (s pgvectorem).

Rozhraní je v **češtině a angličtině**, většina návrhových dokumentů je česky.

## Co umí

- Prostory, stránky, role a skupiny, koncepty a publikování, historie stránek s obnovou
- Živá spolupráce (Yjs přes SignalR) ve vlastním editoru `@atlas/markdown` ([`src/Atlas.Markdown`](src/Atlas.Markdown/README.md))
- Fulltextové hledání, osnova stránky, štítky, ikony, obálky a osobní knihovna
- Tabule, kontakty a úlohy na pozadí
- Soukromé přílohy, export do PDF, import z Wiki.js, GitHub wiki a Markdownu ve stylu Obsidianu
- Přihlášení vlastními účty nebo přes OpenID Connect
- Volitelná AI: odpovědi s citacemi nad publikovanými stránkami, pomoc při psaní a shrnutí, přes OpenAI-kompatibilní
  servery (LM Studio, OpenAI, AI gateway) nebo Anthropic
- Osobní API tokeny a koncový bod MCP, modul YouTrack (jen čtení), zálohy do Gitu a do souborů

## Struktura repozitáře

| Cesta | Obsah |
| --- | --- |
| `src/Atlas.Server` | Hostitel ASP.NET Core: funkce jako vertikální řezy (Wolverine, NHibernate, migrace) |
| `src/Atlas.Web` | Frontend ve Vue 3 a pnpm workspace; `dotnet publish` ho vloží do `wwwroot` serveru |
| `src/Atlas.Markdown` | Editor Markdownu (`@atlas/markdown`), člen workspace `src/Atlas.Web` |
| `src/Atlas.Markdown.Sync` | Knihovna .NET pro synchronizaci editoru (SignalR, smlouvy, validace dokumentů) |
| `src/Atlas.Yjs` | Knihovna .NET pro práci se stavem Yjs na serveru |
| `src/Atlas.YouTrack` | Modul YouTrack (jen čtení) |
| `src/Atlas.AppHost` | Hostitel .NET Aspire pro lokální vývoj (PostgreSQL v kontejneru) |
| `tests/` | Testy serveru, YouTrack, Yjs, synchronizace a prohlížečové testy Playwright |
| `deploy/postgres` | Image PostgreSQL + pgvector, který používá `docker-compose.yml` ([popis](deploy/postgres/README.md)) |
| `docs/` | Architektura, provoz a dokumentace funkcí (česky) |
| `tooling/` | Nástroje vydání, CI, generátor ikon, obálek a značek verzí |

## Rychlý start s Docker Compose

Každé vydání publikuje image `ghcr.io/scalesoft/atlas:<verze>` (a `latest`) a `ghcr.io/scalesoft/atlas-postgres:<tag>`
(jen `linux/amd64`; vydané verze jsou v [Releases](https://github.com/Scalesoft/atlas/releases), tag databáze
v [`docker-compose.yml`](docker-compose.yml)).

```sh
docker compose up -d
```

Otevřete http://localhost:8080. Nová databáze spustí instalačního průvodce; jednorázový instalační token je v logu serveru
(`docker compose logs atlas`, zpráva `Atlas first installation token`).

`docker-compose.yml` je příklad pro vyzkoušení a vlastní provoz. Hesla databáze v něm jsou zástupné hodnoty: než instalace
bude držet skutečná data, vytvořte vedle `docker-compose.yml` soubor `.env` (nikdy ho nekomitujte) s vlastními hesly
a připnutou verzí:

```sh
ATLAS_IMAGE=ghcr.io/scalesoft/atlas:<verze>
ATLAS_DB_OWNER_PASSWORD=<heslo vlastníka schématu, bez středníku>
ATLAS_DB_APP_PASSWORD=<heslo aplikační role, bez středníku>
```

Heslo vlastníka si existující svazek databáze pamatuje z prvního startu; později ho změníte jen přes `ALTER ROLE`
([docs/operations.md](docs/operations.md#záloha-upgrade-a-obnova)). Před zveřejněním nasaďte Atlas za HTTPS
([poznámky k reverzní proxy](docs/operations.md#https-za-reverzní-proxy)). Zálohování a obnovu popisuje
[docs/backup-restore.md](docs/backup-restore.md).

## Vývoj

Potřebujete .NET SDK 11 (viz `global.json`), Node.js 22.12+ (CI používá Node.js 26), pnpm (verze v
`src/Atlas.Web/package.json`, `corepack enable`) a Podman nebo Docker pro PostgreSQL.

```sh
# závislosti frontendu a editoru (jeden pnpm workspace) a dist editoru, který frontend importuje
pnpm --dir src/Atlas.Web install --frozen-lockfile
pnpm --dir src/Atlas.Web run build:editor

# server + PostgreSQL + Vite přes Aspire
dotnet run --project src/Atlas.AppHost --launch-profile https
```

S Podmanem nejdřív nastavte `ASPIRE_CONTAINER_RUNTIME=podman`. Instalační token najdete v Aspire dashboardu (prostředek
`atlas-service`, Logs). Editor se mění ve stejném commitu jako jeho použití; pnpm spouštějte vždy z `src/Atlas.Web`.
`ATLAS_EDITOR_MODE=source` servíruje zdroje editoru s hot reloadem ([podrobnosti](src/Atlas.Web/README.md)).
Galerie komponent je na `/?gallery=1` na vývojovém serveru Vite.

Testy:

```sh
pwsh tests/changed.ps1      # jen to, čeho se týká vaše změna
pwsh tests/smoke.ps1        # rychlá kontrola, asi 40 s
dotnet test Atlas.slnx -p:SkipFrontendBuild=true   # vyžaduje ATLAS_TEST_CONNECTION, viz docs/operations.md#testy
pnpm --dir src/Atlas.Web test
```

Testy serveru potřebují PostgreSQL s pgvectorem; jak ho spustit, je v [AGENTS.md](AGENTS.md#testing-testování)
a v [docs/operations.md](docs/operations.md#testy). Celé sady spouští CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml))
na každém pull requestu.

Produkční build je `dotnet publish src/Atlas.Server/Atlas.Server.csproj -c Release -o artifacts/publish`; sestaví i editor
a frontend. Image sestaví `docker build -t atlas .`. Další postupy jsou v [docs/operations.md](docs/operations.md),
architektura v [docs/architecture.md](docs/architecture.md).

## Dokumentace

- [Provoz a vývoj](docs/operations.md): publikace, OIDC, moduly, HTTPS, zálohy, upgrade, testy
- [Architektura a rozhodnutí](docs/architecture.md) a [produktová specifikace](docs/mvp-specification.md)
- [Model přístupu](docs/access-model.md), [obnova správce](docs/administrator-recovery.md), [úlohy na pozadí](docs/background-jobs.md)
- [AI asistent](docs/ai-assistant.md), [MCP a Claude Code](docs/mcp-claude-code.md), [export do PDF](docs/pdf-export.md)
- [Zálohy a obnova](docs/backup-restore.md), [záloha do Gitu](docs/git-backup.md)
- [Pravidla interakce](docs/interaction-rules.md), [testy](docs/testing/isolated-tests-4.1.0.md)
- [Dokumentace editoru](docs/markdown/README.md)
- [Changelog](CHANGELOG.md)

## Přispívání a bezpečnost

Viz [CONTRIBUTING.md](CONTRIBUTING.md); pravidla pro přispěvatele a programovací agenty jsou v [AGENTS.md](AGENTS.md).
Zranitelnosti hlaste soukromě podle [SECURITY.md](SECURITY.md).

## Licence

Vlastní kód projektu je dostupný pod [licencí MIT](LICENSE). Závislosti a další převzaté součásti si ponechávají své
vlastní licence; oznámení vyžadovaná při distribuci jsou v [NOTICES.md](NOTICES.md)
a [src/Atlas.Markdown/NOTICES.md](src/Atlas.Markdown/NOTICES.md).
