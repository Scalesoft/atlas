# Contributing to Atlas

Thanks for helping. Issues and pull requests are welcome in English or Czech.

## Before you start

- For anything larger than a fix, open an issue first so we can agree on the approach.
- Build and run the project as described in the [README](README.md#development).
- [AGENTS.md](AGENTS.md) holds the detailed rules (UI, motion, testing, releases, editor); they apply to people and coding agents alike.

## How changes are accepted

This repository is the only place where Atlas and its editor are developed; the editor lives in it.

1. Branch from `develop` (maintainers in this repository, everyone else in a fork) and open a pull request into `develop`.
2. CI (`.github/workflows/ci.yml`) runs the release tooling, editor, web and server suites on the pull request; it must be green.
3. A maintainer listed in [CODEOWNERS](.github/CODEOWNERS) reviews and approves it. Pull requests are merged with a
   linear history (rebase or squash).

## Releases

`develop` is merged into `main` for a release; the version is bumped on `develop` first (see
[AGENTS.md](AGENTS.md#branches-and-releases-vydání)). A push to `main` runs CI again, tags `v<version>`, creates the GitHub
release and publishes the images `ghcr.io/scalesoft/atlas` and `ghcr.io/scalesoft/atlas-postgres`. Nothing is deployed
from this repository: every installation upgrades to a released version on its own schedule.

## Pull requests

- Keep each pull request to one change.
- Match the surrounding code: naming, file layout (features are vertical slices under `src/Atlas.Server/Features`),
  comment density. The build treats warnings as errors and enforces code style.
- Add or update tests that prove the change: server tests in `tests/Atlas.Server.Tests`, frontend tests next to the
  component (`*.test.ts`), editor tests in `src/Atlas.Markdown` (`pnpm --filter @atlas/markdown exec vitest run --configLoader runner`, from `src/Atlas.Web`).
- Every database migration is a new SQL file in `src/Atlas.Server/Persistence/Migrations`; never edit a released one,
  and add a server test that runs it over existing rows.
- Run the tests your change touches before you push (`pwsh tests/changed.ps1` runs them for the files you changed;
  `pwsh tests/smoke.ps1` is a quick check).
- Use fictional people and organizations and reserved `example.test` domains in examples. Never commit local configuration,
  real tokens, exported customer data or screenshots of an actual installation. CI scans sources, compressed fixtures,
  commit history and author/committer email addresses with Gitleaks; exceptions must name an exact synthetic value and file.

## UI

Reuse the existing components and layouts shown in the component gallery
(`src/Atlas.Web/src/components/dev/ComponentGallery.vue`, served at `/?gallery=1` by the Vite dev server) before
adding new ones. A genuinely new shared component gets a live gallery example that says when to choose it over the
nearest existing one. User-facing text is Czech and English: add it to the dictionaries in `src/Atlas.Web/src/i18n`.

For settings whose fields depend on each other, use progressive disclosure (gallery: “Postupně odkrývaný formulář”):
reveal the relevant fields inline as choices are made, keep earlier choices editable, separate advanced options, show a
live summary, handle incompatible dependent values explicitly and save only on confirmation. Reserve `StepFlow` for
genuinely separate phases.

## Editor

The editor is the package `src/Atlas.Markdown` (`@atlas/markdown`, a member of the `src/Atlas.Web` pnpm workspace) with the
server library `src/Atlas.Markdown.Sync`; see [docs/markdown](docs/markdown/README.md). Atlas imports its built `dist`
(`pnpm run build:editor`). Keep Atlas-specific behaviour out of the editor, and run pnpm only from `src/Atlas.Web`.

## License

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
