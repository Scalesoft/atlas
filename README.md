# Atlas

Atlas is a self-hosted company wiki with a live collaborative Markdown editor. A single ASP.NET Core process serves both
the API and the Vue 3 frontend; all data lives in PostgreSQL (with pgvector).

The interface is available in **Czech and English**. Most design documents are written in Czech.

## Features

- Spaces, pages, roles and groups, drafts and publishing, page history with restore
- Live collaboration (Yjs over SignalR) in the project's own editor `@atlas/markdown` ([`src/Atlas.Markdown`](src/Atlas.Markdown/README.md))
- Full-text search, page outline, labels, icons, covers and a personal library
- Boards, contacts and background jobs
- Private attachments, PDF export, import from Wiki.js, GitHub wikis and Obsidian-style Markdown
- Sign-in with built-in accounts or OpenID Connect
- Optional AI: answers with citations over published pages, writing assistance and summaries, through OpenAI-compatible
  servers (LM Studio, OpenAI, an AI gateway) or Anthropic
- Personal API tokens and an MCP endpoint, a read-only YouTrack module, backups to Git and to files

## Repository layout

| Path | Contents |
| --- | --- |
| `src/Atlas.Server` | ASP.NET Core host: features as vertical slices (Wolverine, NHibernate, migrations) |
| `src/Atlas.Web` | Vue 3 frontend and pnpm workspace; `dotnet publish` puts it into the server's `wwwroot` |
| `src/Atlas.Markdown` | Markdown editor (`@atlas/markdown`), a member of the `src/Atlas.Web` workspace |
| `src/Atlas.Markdown.Sync` | .NET library for editor synchronisation (SignalR, contracts, document validation) |
| `src/Atlas.Yjs` | .NET library for working with Yjs state on the server |
| `src/Atlas.YouTrack` | YouTrack module (read-only) |
| `src/Atlas.AppHost` | .NET Aspire host for local development (PostgreSQL in a container) |
| `tests/` | Server, YouTrack, Yjs and synchronisation tests, and Playwright browser tests |
| `deploy/postgres` | Upgrade of a PostgreSQL 18 database volume to 19 ([description](deploy/postgres/README.md)) |
| `docs/` | Architecture, operations and feature documentation (in Czech) |
| `tooling/` | Release and CI tooling, generators of icons, covers and version marks |

## Quick start with Docker Compose

Every release publishes the image `ghcr.io/scalesoft/atlas:<version>` (and `latest`; `linux/amd64` only; released versions
are listed under [Releases](https://github.com/Scalesoft/atlas/releases)). The database is the official
`pgvector/pgvector` image (PostgreSQL 19) that [`docker-compose.yml`](docker-compose.yml) pins by digest.

```sh
docker compose up -d
```

Open http://localhost:8080. A new database starts the setup wizard; the one-time installation token is in the server log
(`docker compose logs atlas`, message `Atlas first installation token`).

`docker-compose.yml` is an example for trying Atlas out and for running it yourself. The database passwords in it are
placeholders: before the installation holds real data, create a `.env` file next to `docker-compose.yml` (never commit
it) with your own passwords and a pinned version:

```sh
ATLAS_IMAGE=ghcr.io/scalesoft/atlas:<version>
ATLAS_DB_OWNER_PASSWORD=<schema owner password, without a semicolon>
ATLAS_DB_APP_PASSWORD=<application role password, without a semicolon>
```

An existing database volume remembers the owner password from its first start; later it can only be changed with
`ALTER ROLE` ([docs/operations.md](docs/operations.md#záloha-upgrade-a-obnova)). Put Atlas behind HTTPS before exposing
it ([reverse proxy notes](docs/operations.md#https-za-reverzní-proxy)). Backup and restore are described in
[docs/backup-restore.md](docs/backup-restore.md).

## Development

You need the .NET SDK 11 (see `global.json`), Node.js 22.12+ (CI uses Node.js 26), pnpm (the version is in
`src/Atlas.Web/package.json`; `corepack enable`) and Podman or Docker for PostgreSQL.

```sh
# frontend and editor dependencies (one pnpm workspace) and the editor's dist, which the frontend imports
pnpm --dir src/Atlas.Web install --frozen-lockfile
pnpm --dir src/Atlas.Web run build:editor

# server + PostgreSQL + Vite through Aspire
dotnet run --project src/Atlas.AppHost --launch-profile https
```

With Podman, set `ASPIRE_CONTAINER_RUNTIME=podman` first. The installation token is in the Aspire dashboard (resource
`atlas-service`, Logs). The editor changes in the same commit as its use; always run pnpm from `src/Atlas.Web`.
`ATLAS_EDITOR_MODE=source` serves the editor sources with hot reload ([details](src/Atlas.Web/README.md)).
The component gallery is at `/?gallery=1` on the Vite development server.

Tests:

```sh
pwsh tests/changed.ps1      # only what your change touches
pwsh tests/smoke.ps1        # a quick check, about 40 s
pnpm --dir src/Atlas.Web test
dotnet test Atlas.slnx -p:SkipFrontendBuild=true   # the full suite, normally left to CI; needs ATLAS_TEST_CONNECTION
```

The server tests need PostgreSQL with pgvector; how to start it is described in [AGENTS.md](AGENTS.md#testing-testování)
and in [docs/operations.md](docs/operations.md#testy). CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs
the full suites on every pull request.

The production build is `dotnet publish src/Atlas.Server/Atlas.Server.csproj -c Release -o artifacts/publish`; it builds
the editor and the frontend as well. `docker build -t atlas .` builds the image. Further procedures are in
[docs/operations.md](docs/operations.md), the architecture in [docs/architecture.md](docs/architecture.md).

## Documentation

The documents below are in Czech unless noted otherwise.

- [Operations and development](docs/operations.md): publishing, OIDC, modules, HTTPS, backups, upgrades, tests
- [Architecture and decisions](docs/architecture.md) and the [product specification](docs/mvp-specification.md)
- [Access model](docs/access-model.md) (English), [administrator recovery](docs/administrator-recovery.md), [background jobs](docs/background-jobs.md)
- [AI assistant](docs/ai-assistant.md), [MCP and Claude Code](docs/mcp-claude-code.md), [PDF export](docs/pdf-export.md)
- [Backup and restore](docs/backup-restore.md), [backup to Git](docs/git-backup.md)
- [Interaction rules](docs/interaction-rules.md), [tests that need their own database or host](docs/testing/isolated-tests-4.1.0.md)
- [Editor documentation](docs/markdown/README.md)
- [Changelog](CHANGELOG.md)

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md); the rules for contributors and coding agents are in [AGENTS.md](AGENTS.md).
Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## License

The project's own code is available under the [MIT License](LICENSE). Dependencies and other third-party components keep
their own licences; the notices required for distribution are in [NOTICES.md](NOTICES.md)
and [src/Atlas.Markdown/NOTICES.md](src/Atlas.Markdown/NOTICES.md).
