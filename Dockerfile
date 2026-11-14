# The build host may differ from the target architecture (e.g. an ARM64 runner building for Linux AMD64).
# Keep SDK/codegen on BUILDPLATFORM and cross-publish for TARGETARCH (no emulated .NET SDK).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0-resolute AS build

RUN apt-get update && apt-get install -y ca-certificates curl gnupg \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_26.x nodistro main" > /etc/apt/sources.list.d/nodesource.list \
    && apt-get update && apt-get install -y nodejs \
    && npm install -g pnpm \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Dependencies before the sources: with a build cache this layer is reused until a project
# file or the lockfile changes. The per-release arguments are declared after it, so they do not invalidate it.
COPY global.json Directory.Build.props Directory.Build.targets ./
COPY src/Atlas.Server/Atlas.Server.csproj src/Atlas.Server/
COPY src/Atlas.YouTrack/Atlas.YouTrack.csproj src/Atlas.YouTrack/
COPY src/Atlas.Markdown.Sync/Atlas.Markdown.Sync.csproj src/Atlas.Markdown.Sync/
COPY src/Atlas.Yjs/Atlas.Yjs.csproj src/Atlas.Yjs/
COPY src/Atlas.Web/package.json src/Atlas.Web/pnpm-lock.yaml src/Atlas.Web/pnpm-workspace.yaml src/Atlas.Web/
# The editor is a package of the frontend workspace; its sources come with COPY . . and the publish builds it.
COPY src/Atlas.Markdown/package.json src/Atlas.Markdown/
COPY src/Atlas.Web/patches src/Atlas.Web/patches/
RUN dotnet restore src/Atlas.Server/Atlas.Server.csproj \
    && (cd src/Atlas.Web && pnpm install --frozen-lockfile)

COPY . .

ARG TARGETARCH
ARG RELEASE_VERSION
ARG SOURCE_REVISION

# `codegen write` pre-generates the Wolverine handlers that the image loads; it refuses to start without them.
RUN dotnet build src/Atlas.Server/Atlas.Server.csproj --configuration Release \
    && ASPNETCORE_ENVIRONMENT=Production dotnet run --project src/Atlas.Server/Atlas.Server.csproj \
        --configuration Release --no-build --no-launch-profile -- codegen write \
    && dotnet publish src/Atlas.Server/Atlas.Server.csproj \
        --os linux --arch "$TARGETARCH" --self-contained false \
        --configuration Release --output /app/publish /p:UseAppHost=false \
        -p:Version="$RELEASE_VERSION" -p:SourceRevisionId="$SOURCE_REVISION"

# The runtime without the application: the production image below and the server container of tests/browser/e2e-podman.ps1
# (`podman build --target runtime`, needs no build context). The git backup runs the git CLI over SSH.
# The PDF export drives the Chromium headless shell over CDP from .NET (PuppeteerSharp): no Node.js in the image. The shell is the
# build of Playwright 1.64 (Chromium 156): Chrome for Testing for amd64, Playwright's own CDN build (revision 1248) for arm64.
FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute AS runtime
ARG TARGETARCH
ARG CHROMIUM_VERSION=156.0.8078.4
ARG CHROMIUM_REVISION=1248
# SHA-256 of each download; arm64 must be set (--build-arg) by whoever first fetches that build, the build fails without it.
ARG CHROMIUM_SHA256_amd64=c3e50596aef52c58f8fda3c6b3411c33d34c88b177b3ed1c2243359fd3b8ba20
ARG CHROMIUM_SHA256_arm64=
# The packages and fonts Playwright installs for Chromium on Ubuntu 26.04, so the print lays out as before.
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates unzip libatomic1 \
        libasound2t64 libatk-bridge2.0-0t64 libatk1.0-0t64 libatspi2.0-0t64 libcairo2 libcups2t64 libdbus-1-3 libdrm2 libgbm1 \
        libglib2.0-0t64 libnspr4 libnss3 libpango-1.0-0 libx11-6 libxcb1 libxcomposite1 libxdamage1 libxext6 libxfixes3 libxkbcommon0 libxrandr2 \
        fonts-noto-color-emoji fonts-unifont libfontconfig1 libfreetype6 xfonts-cyrillic xfonts-scalable fonts-liberation \
        fonts-ipafont-gothic fonts-wqy-zenhei fonts-tlwg-loma-otf fonts-freefont-ttf \
    && case "$TARGETARCH" in \
        amd64) url="https://storage.googleapis.com/chrome-for-testing-public/$CHROMIUM_VERSION/linux64/chrome-headless-shell-linux64.zip"; bin=chrome-headless-shell-linux64/chrome-headless-shell; sha="$CHROMIUM_SHA256_amd64" ;; \
        arm64) url="https://cdn.playwright.dev/dbazure/download/playwright/builds/chromium/$CHROMIUM_REVISION/chromium-headless-shell-linux-arm64.zip"; bin=chrome-linux/headless_shell; sha="$CHROMIUM_SHA256_arm64" ;; \
        *) echo "unsupported $TARGETARCH" >&2; exit 1 ;; esac \
    && { test -n "$sha" || { echo "CHROMIUM_SHA256_$TARGETARCH is not set" >&2; exit 1; }; } \
    && curl -fsSL "$url" -o /tmp/chromium.zip && echo "$sha  /tmp/chromium.zip" | sha256sum -c - \
    && unzip -q /tmp/chromium.zip -d /opt/chromium && rm /tmp/chromium.zip \
    && test -x "/opt/chromium/$bin" \
    && apt-get purge -y --auto-remove curl unzip && rm -rf /var/lib/apt/lists/* \
    && printf '#!/bin/sh\nexec nice -n 15 /opt/chromium/%s "$@"\n' "$bin" > /usr/local/bin/pdf-chromium && chmod 755 /usr/local/bin/pdf-chromium
# The export's Chromium, and every process it starts, runs niced: a long print yields the CPU to the wiki.
ENV ATLAS_PDF_BROWSER=/usr/local/bin/pdf-chromium

RUN apt-get update \
    && apt-get install -y --no-install-recommends git openssh-client \
    && rm -rf /var/lib/apt/lists/*

ENV Security__DataPath=/data/security \
    DataProtection__KeysPath=/data/keys \
    Authentication__OpenIdConnect__CertificateDirectory=/data/oidc \
    Attachments__DataPath=/data/attachments \
    ContentImport__DataPath=/data/import \
    ContentExport__DataPath=/data/export \
    Vaults__DataPath=/data/vaults \
    GitBackup__DataPath=/data/git-backup
# Atlas 3 ships with its modules on; a deployment switches one off with Modules__<id>__Enabled=false.
ENV Modules__boards__Enabled=true \
    Modules__projector__Enabled=true

FROM runtime
ARG RELEASE_VERSION
ARG SOURCE_REVISION
LABEL org.opencontainers.image.version=$RELEASE_VERSION \
      org.opencontainers.image.revision=$SOURCE_REVISION \
      org.opencontainers.image.source="https://github.com/Scalesoft/atlas"

WORKDIR /app

COPY --from=build /app/publish .
# The safety archive before a restore goes to .data/backup-safety under the content root when no backup folder is set.
RUN mkdir -p /app/.data && chown app:app /app/.data

# Atlas runs as the image's non-root user app ($APP_UID, 1654); the application files stay root's and read-only to it. The start is
# root only to hand the volume to that user: whatever under /data an earlier image wrote as root (an existing production volume) is
# given to app (only what app does not own yet, so a later start changes nothing), then setpriv drops to app for good. A deployment
# that runs the container as app itself (user: "1654:1654", after that hand-over) skips the step.
# bash, not sh: dash drops environment variables whose names hold a hyphen (ConnectionStrings__atlas-db) from what it starts.
RUN printf '%s\n' '#!/bin/bash' 'set -e' \
        'if [ "$(id -u)" = 0 ]; then' \
        '  mkdir -p /data && find /data -xdev ! -user app -exec chown -h app:app {} +' \
        '  exec setpriv --reuid=app --regid=app --init-groups env HOME=/home/app "$@"' \
        'fi' \
        'exec "$@"' > /usr/local/bin/atlas-start \
    && chmod 755 /usr/local/bin/atlas-start

EXPOSE 8080

ENTRYPOINT ["/usr/local/bin/atlas-start", "dotnet", "Atlas.Server.dll"]
