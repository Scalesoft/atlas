<#
.SYNOPSIS
    Upgrade (data migration) test: data written by the deployed Atlas version must survive the new build.
    One Podman pod: the old server on its deployed database image (the old ref's deploy/postgres build with pgvector from 2.0
    to 5.0, postgres:18.6-alpine before, the image its docker-compose.yml pins after), then the image this tree's
    docker-compose.yml pins and the new server on the same volume, exactly as production upgrades.

.DESCRIPTION
    1. OLD build: a detached `git worktree` of the explicitly supplied deployed release tag or commit (-OldRef)
       is published in package mode with its own pinned editor. Cached per commit
       under artifacts/upgrade-podman/publish-old-<sha>. The main working tree is never checked out, stashed or reset.
    2. NEW build: the current working tree, published in package mode (as tests/browser/e2e-podman.ps1 does).
    3. Pod: -OldPostgresImage with named volumes for the database and for /data (keys, attachments, imports, backups).
       Default: the old ref's deploy/postgres/Dockerfile built for linux/amd64 by that ref's deploy/postgres/build.ps1 (2.0 to
       5.0); a later ref has none and pins the official image in its docker-compose.yml; an earlier one predates 2.0 and its
       pgvector requirement (both arrived in one commit) and ran on postgres:18-alpine: postgres:18.6-alpine.
       Old server -> the old ref's own first-run.spec.ts installs the wiki -> seed-old.mjs seeds realistic data
       through the old public API and a real browser (collaboration) and writes manifest.json.
    4. The old server stops; a table-by-table checksum (old columns only) is taken. The database container is replaced by
       -PostgresImage on the same volume, after deploy/postgres/upgrade.sh when the PostgreSQL major differs (18 -> 19)
       (checksums and every B-tree index re-checked with amcheck). The NEW server starts on the same volumes and migrates. Checksums are compared right after startup, SQL
       invariants are checked (pgvector installed), verify-new.mjs compares every manifest item through the new API and
       browser, then a smoke subset of the current tests/browser runs against the upgraded instance.
    5. Evidence under artifacts/upgrade-podman/<run>/ ; pod, volumes, secrets and the created worktree are removed.

.EXAMPLE
    pwsh tests/upgrade/upgrade-podman.ps1 -OldRef v1.4.0
.EXAMPLE
    pwsh tests/upgrade/upgrade-podman.ps1 -OldRef v4.1.0 -SkipSmoke
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    # Resolve the actual deployed release tag or commit; neither main nor develop identifies a successful deployment.
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [ValidateScript({ -not [string]::IsNullOrWhiteSpace($_) })]
    [string]$OldRef,
    # Reuse an existing checkout of the old ref (for example a prepared worktree) instead of creating one.
    [string]$OldWorktree = '',
    # Reuse publish outputs instead of publishing.
    [string]$OldPublishDir = '',
    [string]$NewPublishDir = '',
    # Run publish bytes copied into Linux image layers instead of reading assemblies over a host bind mount.
    # Each image supplies its own server entrypoint; the old checkout is still required for its installation spec.
    [string]$OldAppImage = '',
    [string]$NewAppImage = '',
    # Current tests/browser specs run against the upgraded instance (comma-separated); empty with -SkipSmoke.
    [string]$SmokeSpec = 'shell-surfaces.spec.ts,tree-navigation.spec.ts,search-filters.spec.ts,collaboration.spec.ts,page-history.spec.ts',
    [switch]$SkipSmoke,
    [int]$ThrottlePauseSeconds = 60,
    [int]$HostPort = 0,
    # Leave the pod and volumes for inspection.
    [switch]$Keep,
    # The database image production runs before the upgrade. Default: the old ref's deploy/postgres image (Atlas 2.0 to 5.0;
    # 2.0 and later require pgvector, migration 2026100520), the image a later ref's docker-compose.yml pins, or
    # postgres:18.6-alpine for a ref before 2.0; an image given here is used as-is.
    [string]$OldPostgresImage = '',
    # The database image after the upgrade. Default: the one docker-compose.yml pins; an image given here is used as-is.
    [string]$PostgresImage = '',
    [string]$AspNetImage = 'mcr.microsoft.com/dotnet/aspnet:11.0-resolute'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$browserTests = Join-Path $repo 'tests/browser'
$run = 'atlas-upgrade-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$outRoot = Join-Path $repo "artifacts/upgrade-podman/$run"
$outInPod = "/repo/artifacts/upgrade-podman/$run"
$total = [System.Diagnostics.Stopwatch]::StartNew()
New-Item -ItemType Directory -Force $outRoot | Out-Null
Start-Transcript -Path (Join-Path $outRoot 'run.log') -UseMinimalHeader | Out-Null

$results = [System.Collections.Generic.List[object]]::new()
function Add-Result([string]$check, [bool]$ok, [string]$detail = '') {
    $results.Add([pscustomobject]@{ check = $check; ok = $ok; detail = $detail })
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $check, $detail) -ForegroundColor $color
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port } finally { $listener.Stop() }
}

function New-Secret([int]$length) {
    $alphabet = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'.ToCharArray()
    -join (1..$length | ForEach-Object { $alphabet[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($alphabet.Length)] })
}

function Invoke-Podman {
    $output = & podman @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "podman $($args -join ' ') failed: $output" }
    $output
}

function Publish-Server([string]$source, [string]$target, [string]$label) {
    Write-Host "Publishing $label ($source) to $target"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    $log = "$target.log"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Remove-Item Env:ATLAS_EDITOR_MODE, Env:ATLAS_EDITOR_SOURCE -ErrorAction SilentlyContinue
    $project = Join-Path $source 'src/Atlas.Server/Atlas.Server.csproj'
    dotnet publish $project -c Release -o $target -p:UseAppHost=false *> $log
    if ($LASTEXITCODE -ne 0) { Get-Content $log -Tail 30 | Write-Host; throw "dotnet publish ($label) failed; see $log" }
    if (-not (Test-Path (Join-Path $target 'wwwroot/index.html'))) { throw "$target has no wwwroot/index.html" }
    Write-Host ("  {0} published in {1:mm\:ss}" -f $label, $watch.Elapsed)
}

# --- prerequisites -----------------------------------------------------------------------------
if (-not (Get-Command podman -ErrorAction SilentlyContinue)) { throw 'podman is not on PATH.' }
podman info --format '{{.Host.OS}}' *> $null
if ($LASTEXITCODE -ne 0) { throw 'Podman is not reachable. Start the machine: podman machine start' }
$testPackage = Join-Path $browserTests 'node_modules/@playwright/test/package.json'
if (-not (Test-Path $testPackage)) { throw 'Install the browser tests first: npm --prefix tests/browser ci' }
$playwrightVersion = (Get-Content $testPackage -Raw | ConvertFrom-Json).version
$playwrightImage = "mcr.microsoft.com/playwright:v$playwrightVersion-noble"
$busy = @(podman ps --format '{{.Names}}' | Where-Object { $_ -match '^atlas-(e2e|upgrade)-.*-app$' })
if ($busy) { Write-Warning "Other Atlas pods are running ($($busy -join ', ')); a ~6 GB Podman machine may run out of memory." }
$timeZone = 'UTC'
$iana = $null
if ([TimeZoneInfo]::TryConvertWindowsIdToIanaId([TimeZoneInfo]::Local.Id, [ref]$iana)) { $timeZone = $iana }
function Get-PinnedPostgresImage([string]$compose) {
    [regex]::Match($compose, 'ATLAS_POSTGRES_IMAGE:-([^}]+)\}').Groups[1].Value
}
if (-not $PostgresImage) { $PostgresImage = Get-PinnedPostgresImage (Get-Content (Join-Path $repo 'docker-compose.yml') -Raw) }
if (-not $PostgresImage) { throw 'docker-compose.yml pins no ATLAS_POSTGRES_IMAGE default.' }

# --- old and new builds ------------------------------------------------------------------------
$oldSha = (git -C $repo rev-parse --verify "$OldRef^{commit}" 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $oldSha) { throw "Unknown -OldRef $OldRef (git fetch origin first?)" }
$oldShort = $oldSha.Substring(0, 7)
$newHead = (git -C $repo rev-parse --short=7 HEAD)
$dirty = @(git -C $repo status --porcelain).Count
Write-Host "Old: $OldRef = $oldShort ($(git -C $repo log -1 --format='%ad %s' --date=short $oldSha))"
Write-Host "New: working tree at $newHead with $dirty uncommitted paths"
$createdWorktree = $null
if (-not $OldWorktree) {
    $OldWorktree = Join-Path ([System.IO.Path]::GetTempPath()) "$run-old"
    git -C $repo worktree add --detach $OldWorktree $oldSha 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "git worktree add failed for $oldSha" }
    $createdWorktree = $OldWorktree
}
$OldWorktree = (Resolve-Path $OldWorktree).Path
$worktreeSha = (git -C $OldWorktree rev-parse HEAD)
if ($worktreeSha -ne $oldSha) { throw "$OldWorktree is at $worktreeSha, expected $oldSha" }

# The database the old release ran on: from 2.0 (with the pgvector requirement) to 5.0 its own deploy/postgres build, later
# the official image its docker-compose.yml pins; before 2.0 postgres:18-alpine.
if (-not $OldPostgresImage) {
    git -C $repo cat-file -e "${oldSha}:deploy/postgres/Dockerfile" 2>$null
    if ($LASTEXITCODE -eq 0) {
        $OldPostgresImage = @(& (Join-Path $OldWorktree 'deploy/postgres/build.ps1') -Platform 'linux/amd64')[-1]
    } else {
        $OldPostgresImage = Get-PinnedPostgresImage "$(git -C $repo show "${oldSha}:docker-compose.yml" 2>$null)"
        if (-not $OldPostgresImage) { $OldPostgresImage = 'docker.io/library/postgres:18.6-alpine' }
    }
}
Write-Host "Database: $OldPostgresImage, then $PostgresImage on the same volume"

if (-not $OldAppImage -and -not $OldPublishDir) {
    $OldPublishDir = Join-Path $repo "artifacts/upgrade-podman/publish-old-$oldShort"
    if (-not (Test-Path (Join-Path $OldPublishDir 'wwwroot/index.html'))) { Publish-Server $OldWorktree $OldPublishDir "old $oldShort" }
    else { Write-Host "Reusing $OldPublishDir" }
}
if (-not $NewAppImage -and -not $NewPublishDir) {
    $NewPublishDir = Join-Path $repo 'artifacts/upgrade-podman/publish-new'
    Publish-Server $repo $NewPublishDir 'new working tree'
}
if (-not $OldAppImage) { $OldPublishDir = (Resolve-Path $OldPublishDir).Path }
if (-not $NewAppImage) { $NewPublishDir = (Resolve-Path $NewPublishDir).Path }
function Resolve-AppImage([string]$image) {
    $details = (Invoke-Podman image inspect $image | Out-String | ConvertFrom-Json)[0]
    if ($details.Os -ne 'linux' -or $details.Architecture -ne 'amd64') {
        throw "Upgrade acceptance requires linux/amd64, got $($details.Os)/$($details.Architecture) for $image."
    }
    return $details.Id
}
if ($OldAppImage) { $OldAppImage = Resolve-AppImage $OldAppImage }
if ($NewAppImage) { $NewAppImage = Resolve-AppImage $NewAppImage }
$expectedVersions = @(Get-ChildItem (Join-Path $repo 'src/Atlas.Server') -Recurse -Filter *.cs |
    Select-String -Pattern '\[Migration\((\d+)' -AllMatches | ForEach-Object { $_.Matches } | ForEach-Object { [long]$_.Groups[1].Value } | Sort-Object -Unique)

# --- pod ---------------------------------------------------------------------------------------
if ($HostPort -eq 0) { $HostPort = Get-FreePort }
$base = "http://127.0.0.1:$HostPort"
$secrets = [ordered]@{ bootstrap = New-Secret 48; password = New-Secret 24; db = New-Secret 24 }
$connection = "Host=localhost;Port=5432;Database=atlas;Username=atlas;Password=$($secrets.db)"
$createdSecrets = @()
$failed = $false

function Save-Logs([string]$suffix = '') {
    foreach ($part in 'db', 'app') {
        try {
            podman container exists "$run-$part" *> $null
            if ($LASTEXITCODE -ne 0) { continue }
            $text = (& podman logs "$run-$part" 2>&1 | Out-String)
            foreach ($value in @($secrets.bootstrap, $secrets.password, "$($secrets.password)-reader", $secrets.db)) { $text = $text.Replace($value, '***') }
            $text = $text -replace '(installation token|recovery token|activation code): \S+', '$1: ***'
            Set-Content -LiteralPath (Join-Path $outRoot "$part$suffix.log") -Value $text
        } catch { Write-Warning "Could not save the $part log: $($_.Exception.Message)" }
    }
}

function Invoke-Sql([string]$sql) {
    $file = Join-Path ([System.IO.Path]::GetTempPath()) "$run-query.sql"
    [System.IO.File]::WriteAllText($file, $sql)
    try {
        Invoke-Podman cp $file "${run}-db:/tmp/query.sql" | Out-Null
        $output = & podman exec "$run-db" psql -X -v ON_ERROR_STOP=1 -U atlas -d atlas -At -F '|' -f /tmp/query.sql 2>&1
        if ($LASTEXITCODE -ne 0) { throw "psql failed: $output" }
        return @($output | ForEach-Object { "$_" })
    } finally { Remove-Item $file -Force -ErrorAction SilentlyContinue }
}
function Get-SqlValue([string]$sql) { (Invoke-Sql $sql | Select-Object -First 1) }

function Start-App([string]$label, [string]$publishDir, [string]$appImage = '') {
    Write-Host "Starting $label server from $(if ($appImage) { $appImage } else { $publishDir })"
    $appEnvironment = @(
        '-e', 'ASPNETCORE_ENVIRONMENT=Development', '-e', 'ASPNETCORE_HTTP_PORTS=8080', '-e', "TZ=$timeZone",
        '-e', 'Security__DataPath=/data/security', '-e', 'DataProtection__KeysPath=/data/keys',
        '-e', 'Authentication__OpenIdConnect__CertificateDirectory=/data/oidc', '-e', 'Attachments__DataPath=/data/attachments',
        '-e', 'ContentImport__DataPath=/data/import', '-e', 'ContentExport__DataPath=/data/export', '-e', 'Vaults__DataPath=/data/vaults',
        # Workstation GC keeps each server small next to other pods in a ~6 GB Podman machine; same for old and new.
        '-e', 'DOTNET_gcServer=0', '-e', 'DOTNET_GCConserveMemory=5',
        # seed-old.mjs writes Projector content (a deployment summary and board) where the old build has the module.
        '-e', 'Modules__Projector__Enabled=true',
        # A new publish carries no appsettings.Development.json: its two switches (developer sign-in, boards) are named here.
        '-e', 'Authentication__DeveloperSignIn__Enabled=true', '-e', 'Modules__boards__Enabled=true',
        '--secret', "$run-connection,type=env,target=ConnectionStrings__atlas-db",
        '--secret', "$run-bootstrap,type=env,target=Setup__BootstrapToken",
        # :U hands the volume to the image's non-root app user.
        '-v', "${run}-data:/data:U"
    )
    if ($appImage) {
        Invoke-Podman run -d --pod $run --name "$run-app" @appEnvironment $appImage | Out-Null
    } else {
        $serverAssembly = 'Atlas.Server.dll'
        # A baseline published before the move to .NET 11 runs on the runtime it was built for.
        $runtimeConfig = Get-Content -Raw -LiteralPath (Join-Path $publishDir ($serverAssembly -replace '\.dll$', '.runtimeconfig.json'))
        $image = if ($runtimeConfig -match '"version":\s*"10\.') { 'mcr.microsoft.com/dotnet/aspnet:10.0-noble' } else { $AspNetImage }
        Invoke-Podman run -d --pod $run --name "$run-app" @appEnvironment -v "${publishDir}:/app:ro" -w /app $image dotnet $serverAssembly | Out-Null
    }
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $status = $null
    while ($watch.Elapsed.TotalSeconds -lt 180) {
        try { $status = Invoke-RestMethod "$base/api/setup/status" -TimeoutSec 2; if ($status.databaseReady) { break } } catch { }
        if (-not (podman ps -q --filter "name=^$run-app$")) { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $status.databaseReady) { Save-Logs "-$label"; throw "The $label server did not become ready; see $outRoot\app-$label.log" }
    Write-Host ("  $label server ready in {0:N1} s (requiresSetup={1})" -f $watch.Elapsed.TotalSeconds, $status.requiresSetup)
    return $status
}
function Start-Db([string]$image) {
    # A new volume gets the collation of docker-compose.yml with this tree's image; the old image creates it as it did.
    $initdb = if ($image -eq $PostgresImage) { @('-e', 'POSTGRES_INITDB_ARGS=--locale-provider=builtin --builtin-locale=C.UTF-8') } else { @() }
    Invoke-Podman run -d --pod $run --name "$run-db" -e POSTGRES_USER=atlas -e POSTGRES_DB=atlas -e "TZ=$timeZone" @initdb `
        --secret "$run-db,type=env,target=POSTGRES_PASSWORD" -v "${run}-pg:/var/lib/postgresql" $image | Out-Null
    foreach ($attempt in 1..120) {
        # TCP, not the socket: the entrypoint's temporary init server listens on the socket only.
        podman exec "$run-db" pg_isready -h 127.0.0.1 -U atlas -d atlas *> $null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "PostgreSQL ($image) did not become ready."
}
function Stop-App([string]$label) {
    Invoke-Podman stop -t 20 "$run-app" | Out-Null
    Save-Logs "-$label"
    Invoke-Podman rm "$run-app" | Out-Null
}

function Invoke-Playwright([string]$phase, [string]$workdir, [string[]]$command, [string[]]$extraEnv = @(), [string[]]$extraMounts = @()) {
    $envArgs = @(
        '-e', 'ATLAS_URL=http://localhost:8080', '-e', 'ATLAS_E2E_USER=browser-admin', '-e', "TZ=$timeZone",
        '-e', 'PLAYWRIGHT_HTML_OPEN=never', '-e', "PLAYWRIGHT_HTML_OUTPUT_DIR=$outInPod/$phase/report",
        '-e', "PLAYWRIGHT_JSON_OUTPUT_NAME=$outInPod/$phase/results.json", '-e', "UPGRADE_OUT=$outInPod",
        '--secret', "$run-bootstrap,type=env,target=ATLAS_E2E_BOOTSTRAP_TOKEN",
        '--secret', "$run-password,type=env,target=ATLAS_E2E_PASSWORD"
    ) + $extraEnv
    Write-Host "`n=== $phase"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & podman run --rm --pod $run --name "$run-playwright" --init @envArgs -v "${repo}:/repo" @extraMounts -w $workdir $playwrightImage @command 2>&1 |
        Tee-Object -FilePath (Join-Path $outRoot "$phase.log") | Out-Host
    $code = $LASTEXITCODE
    Write-Host ("=== $phase finished in {0:mm\:ss} with exit code $code" -f $watch.Elapsed)
    return $code
}

# Checksums of every base table over the columns that existed in the old schema.
function Get-TableColumns {
    $rows = Invoke-Sql @"
SELECT c.table_name, string_agg(quote_ident(c.column_name::text), ',' ORDER BY c.ordinal_position)
FROM information_schema.columns c JOIN information_schema.tables t ON t.table_schema=c.table_schema AND t.table_name=c.table_name
WHERE c.table_schema='public' AND t.table_type='BASE TABLE' GROUP BY c.table_name ORDER BY 1;
"@
    $map = [ordered]@{}
    foreach ($row in $rows) { $name, $columns = $row -split '\|', 2; $map[$name] = $columns }
    return $map
}
function Get-TableChecksums($columns) {
    $sql = ($columns.Keys | ForEach-Object {
        "SELECT '$_', count(*), md5(coalesce(string_agg(x, E'\n' ORDER BY x), '')) FROM (SELECT (ROW($($columns[$_])))::text AS x FROM public.`"$_`") s;"
    }) -join "`n"
    $map = [ordered]@{}
    foreach ($row in (Invoke-Sql $sql)) { $name, $count, $hash = $row -split '\|'; $map[$name] = [pscustomobject]@{ count = [long]$count; md5 = $hash } }
    return $map
}

try {
    $values = [ordered]@{ bootstrap = $secrets.bootstrap; password = $secrets.password; db = $secrets.db; connection = $connection }
    foreach ($name in $values.Keys) {
        $file = Join-Path ([System.IO.Path]::GetTempPath()) "$run-$name"
        [System.IO.File]::WriteAllText($file, $values[$name])
        try { podman secret create "$run-$name" $file | Out-Null } finally { Remove-Item $file -Force }
        if ($LASTEXITCODE -ne 0) { throw "Could not create Podman secret $run-$name" }
        $createdSecrets += "$run-$name"
    }
    Invoke-Podman volume create "$run-pg" | Out-Null
    Invoke-Podman volume create "$run-data" | Out-Null
    Write-Host "Pod $run (server at $base, TZ=$timeZone)"
    Invoke-Podman pod create --name $run --shm-size 1g -p "127.0.0.1:${HostPort}:8080" | Out-Null
    Start-Db $OldPostgresImage

    # --- old server: install and seed ----------------------------------------------------------
    $phaseWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $status = Start-App 'old' $OldPublishDir $OldAppImage
    if (-not $status.requiresSetup) { throw 'The new database is unexpectedly already installed.' }
    $oldMounts = @('-v', "${OldWorktree}:/old", '-v', "$(Join-Path $browserTests 'node_modules'):/old/tests/browser/node_modules:ro")
    $code = Invoke-Playwright 'old-install' '/old/tests/browser' @('node', 'node_modules/@playwright/test/cli.js', 'test', 'first-run.spec.ts',
        '--reporter=list,json', "--output=$outInPod/old-install/test-results") @('-e', 'ATLAS_E2E_ALLOW_SETUP=1') $oldMounts
    $status = Invoke-RestMethod "$base/api/setup/status" -TimeoutSec 10
    # As in e2e-podman.ps1: the spec installs the wiki in its first steps; its later assertions do not gate the upgrade.
    Add-Result 'old: wiki installed by the old first-run.spec.ts' (-not $status.requiresSetup) "spec exit code $code (later assertions informational)"
    if ($status.requiresSetup) { throw 'First installation did not complete.' }
    if ($ThrottlePauseSeconds -gt 0) { Write-Host "Login throttle pause: $ThrottlePauseSeconds s"; Start-Sleep -Seconds $ThrottlePauseSeconds }

    $code = Invoke-Playwright 'old-seed' '/repo/tests/browser' @('node', '/repo/tests/upgrade/seed-old.mjs')
    Add-Result 'old: seed through old API and browser' ($code -eq 0) "manifest.json"
    if ($code -ne 0 -or -not (Test-Path (Join-Path $outRoot 'manifest.json'))) { throw 'Seeding failed; see old-seed.log' }
    # Collaboration left unsaved in the log (bound to the old schema).
    $manifest = Get-Content (Join-Path $outRoot 'manifest.json') -Raw | ConvertFrom-Json
    $collabPage = $manifest.collaboration.pageId
    $collabSql = "SELECT c.""Generation"", c.""SchemaJson"", (SELECT count(*) FROM ""CollaborationUpdate"" u WHERE u.""PageId""=c.""PageId""), c.""Sequence"", c.""SnapshotSequence"", c.""LastSavedSequence"" FROM ""PageCollaboration"" c WHERE c.""PageId""='$collabPage';"
    $collabBefore = Invoke-Sql $collabSql
    Write-Host "Collaboration state before upgrade: $collabBefore"
    $oldSeedTime = $phaseWatch.Elapsed

    # --- upgrade -------------------------------------------------------------------------------
    Stop-App 'old'
    $columns = Get-TableColumns
    $before = Get-TableChecksums $columns
    $oldVersions = @(Invoke-Sql 'SELECT "Version" FROM "SchemaVersion" ORDER BY 1;')
    $before | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $outRoot 'checksums-before.json')

    # Production order (README, "Upgrade na 2.0"): first the database image on the same volume, then the new Atlas.
    $oldServerVersion = Get-SqlValue 'SHOW server_version;'
    Invoke-Podman stop -t 60 "$run-db" | Out-Null
    Save-Logs '-old-image'
    Invoke-Podman rm "$run-db" | Out-Null
    # A new PostgreSQL major (18 -> 19) is moved on the volume as in production; the old cluster stays beside it.
    $majors = @($OldPostgresImage, $PostgresImage | ForEach-Object { "$(Invoke-Podman run --rm --entrypoint sh $_ -c 'echo $PG_MAJOR')".Trim() })
    if ($majors[0] -ne $majors[1]) {
        $env:ENGINE = 'podman'; $env:POSTGRES_USER = 'atlas'
        # Git Bash on Windows: `bash` on PATH may be the WSL launcher, which would run in the Podman machine.
        $bash = if ($IsWindows) { Join-Path (Split-Path (Split-Path (Get-Command git).Source)) 'bin/bash.exe' } else { 'bash' }
        & $bash (Join-Path $repo 'deploy/postgres/upgrade.sh') "$run-pg" $OldPostgresImage $PostgresImage 2>&1 | Tee-Object -FilePath (Join-Path $outRoot 'pg-upgrade.log') | Out-Host
        $upgradeCode = $LASTEXITCODE
        Remove-Item Env:ENGINE, Env:POSTGRES_USER
        Add-Result "database: PostgreSQL $($majors[0]) -> $($majors[1]) by deploy/postgres/upgrade.sh" ($upgradeCode -eq 0) 'pg-upgrade.log'
        if ($upgradeCode -ne 0) { throw 'deploy/postgres/upgrade.sh failed; see pg-upgrade.log' }
    }
    Start-Db $PostgresImage
    $swapped = Get-TableChecksums $columns
    $swapChanges = @($before.Keys | Where-Object { $before[$_].md5 -ne $swapped[$_].md5 })
    Add-Result 'database image: swapped on the same volume, every table unchanged' ($swapChanges.Count -eq 0) "$OldPostgresImage ($oldServerVersion) -> $PostgresImage ($(Get-SqlValue 'SHOW server_version;')); changed: $($swapChanges -join ', ')"
    # A different C library or collation would order text differently and silently break text B-tree indexes; amcheck proves
    # every index is ordered under the new image (after upgrade.sh they were built anew). Text sorts as before: by code point,
    # on musl and with the builtin provider alike (upgrade.sh compares an ordering probe).
    Add-Result 'database: collation builtin C.UTF-8 (libc-independent)' ((Get-SqlValue "SELECT datlocprovider || ' ' || coalesce(datlocale, '') FROM pg_database WHERE datname = 'atlas';") -eq 'b C.UTF-8') 'pg_database.datlocprovider'
    try {
        $checked = Invoke-Sql @"
CREATE EXTENSION amcheck;
SELECT count(bt_index_check(c.oid)) FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_am a ON a.oid=c.relam
  JOIN pg_namespace n ON n.oid=c.relnamespace WHERE a.amname='btree' AND n.nspname='public' AND c.relpersistence<>'t' AND i.indisvalid;
DROP EXTENSION amcheck;
"@
        # psql prints the command tags of CREATE/DROP EXTENSION around the one count.
        $checked = @($checked | Where-Object { $_ -match '^\d+$' })[0]
        Add-Result 'database image: every B-tree index valid under the new image (amcheck)' ([long]$checked -gt 50) "$checked indexes checked"
    } catch { Add-Result 'database image: every B-tree index valid under the new image (amcheck)' $false $_.Exception.Message }

    $phaseWatch.Restart()
    $status = Start-App 'new' $NewPublishDir $NewAppImage
    Add-Result 'new: starts on the old database' ($status.databaseReady -and -not $status.requiresSetup) "requiresSetup=$($status.requiresSetup)"
    $migrationTime = $phaseWatch.Elapsed
    # Over the old columns the new schema still has: a table or column a migration drops shows as a change of its table below.
    $now = Get-TableColumns
    $kept = [ordered]@{}
    foreach ($table in $columns.Keys) {
        if ($now.Contains($table)) { $kept[$table] = @($columns[$table] -split ',' | Where-Object { $_ -in ($now[$table] -split ',') }) -join ',' }
    }
    $after = Get-TableChecksums $kept
    $after | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $outRoot 'checksums-after-migration.json')
    $newVersions = @(Invoke-Sql 'SELECT "Version" FROM "SchemaVersion" ORDER BY 1;' | ForEach-Object { [long]$_ })
    $missing = @($expectedVersions | Where-Object { $_ -notin $newVersions })
    $applied = @($newVersions | Where-Object { "$_" -notin $oldVersions })
    Add-Result 'migrations: every migration of the new tree recorded' ($missing.Count -eq 0) "applied now: $($applied -join ', '); missing: $($missing -join ', ')"
    $appLog = (& podman logs "$run-app" 2>&1 | Out-String)
    $errors = @($appLog -split "`n" | Where-Object { $_ -match '\b(fail|crit|Unhandled|Exception)\b' })
    Add-Result 'migrations: no errors in the new server log after start' ($errors.Count -eq 0) ($errors | Select-Object -First 3 | Out-String).Trim()

    # Tables whose old-column content changed during migration/startup. SchemaVersion gains rows by design. 3.3 (jobs and the
    # outbox on Wolverine): OutboxMessage goes (its rows become the upgrade job's run), GitBackupSettings loses its retry
    # counter (Failures, NextAttemptAt) to the job's retries, Notification.LastMessageId becomes an empty uuid.
    # 3.4 from 3.3: 2026110470 deletes the deployment boards (Board, BoardItem, BoardOperation, BoardSearchDocument; the board's
    # ContentResource and its links in ContentReferenceSource go with it), the job pages.requirements-overview (2026110480; Job, JobRun)
    # converts working and published revisions in place (PageRevision; verify-new.mjs checks every other page's revisions), and
    # Wolverine's own tables change on every start.
    # 3.6 from 3.5.2: UserApiPermission (2026110684: every ceiling level draft/publish/direct becomes write), McpPublishRequest (2026110685)
    # and ProjectorRequirementChange (2026110690) are dropped, PageCollaboration (2026110670 drops the two projection columns; 2026110997
    # queues the job collaboration.live-seeds, which adds the live state of every page). Job, JobRun and SchemaVersion are covered above.
    # CollaborationUpdate: the live-seeds job resets the log of a fully saved page bound to the old host schema (PrepareLiveSeed.Decide -> Seed.Reset), so its rows go by design; unsaved pages keep theirs (collaboration check).
    # AppLog (4.0 and later): Atlas's own stored log, which the new server's start writes to.
    $expectedChanges = @('SchemaVersion', 'AppLog', 'OutboxMessage', 'GitBackupSettings', 'Notification',
        'Board', 'BoardItem', 'BoardOperation', 'BoardSearchDocument', 'ContentResource', 'ContentReferenceSource', 'Job', 'JobRun', 'PageRevision',
        'UserApiPermission', 'McpPublishRequest', 'ProjectorRequirementChange', 'PageCollaboration', 'CollaborationUpdate',
        'wolverine_incoming_envelopes', 'wolverine_node_records')
    $changed = @($before.Keys | Where-Object { $before[$_].md5 -ne $after[$_].md5 })
    $unexpected = @($changed | Where-Object { $_ -notin $expectedChanges })
    $changeDetail = ($changed | ForEach-Object { "$_ ($($before[$_].count)->$($after[$_].count))" }) -join ', '
    Add-Result 'data: every pre-existing table unchanged in its old columns' ($unexpected.Count -eq 0) "changed: $changeDetail"
    Add-Result 'data: tables compared' ($before.Count -gt 50) "$($before.Count) base tables, $(( $before.Values | Measure-Object count -Sum).Sum) rows"

    $invariants = [ordered]@{
        'item access: no page has OwnAccess'                  = @('SELECT count(*) FROM "Page" WHERE "OwnAccess";', '0')
        'item access: zero ResourceGrant rows'                = @('SELECT count(*) FROM "ResourceGrant";', '0')
        'item access: one current access-mode row per page'   = @('SELECT count(*) FROM "Page" p WHERE (SELECT count(*) FROM "PageAccessModeRevision" r WHERE r."PageId"=p."Id" AND r."ActiveTo" IS NULL AND NOT r."OwnAccess")<>1;', '0')
        # Migration 2026100520 (Atlas 2.0): required pgvector, in public, at least 0.8.1.
        'pgvector: extension installed in public, >= 0.8.1'   = @("SELECT string_to_array(e.extversion,'.')::int[] >= ARRAY[0,8,1] AND n.nspname='public' FROM pg_extension e JOIN pg_namespace n ON n.oid=e.extnamespace WHERE e.extname='vector';", 't')
        # Without an AI profile nothing is queued for the index (AI-24); counts as 0 until V2 adds the table.
        'ai index: no queued IndexTask after the upgrade'     = @("SELECT CASE WHEN to_regclass('`"IndexTask`"') IS NULL THEN '0' ELSE (xpath('/row/c/text()', query_to_xml('SELECT count(*) AS c FROM `"IndexTask`"', false, true, '')))[1]::text END;", '0')
        'grant validity: legacy grants stay unbounded'        = @('SELECT count(*) FROM "SpaceGrant" WHERE "StartAt" IS NOT NULL OR "ExpiresAt" IS NOT NULL;', '0')
        'oidc admin mapping: no provenance on legacy admins'  = @('SELECT count(*) FROM "UserAccount" WHERE "OidcAdminIssuer" IS NOT NULL;', '0')
        # Migration 2026100610 (Atlas 2.0, T2): rows written before it answer 'legacy' from the catalog (openings: no channel).
        'content channel: rows before 610 are legacy'          = @('WITH m AS (SELECT "AppliedAt" AS at FROM "SchemaVersion" WHERE "Version"=2026100610) SELECT (SELECT count(*) FROM "PageRevision", m WHERE "ActiveFrom"<m.at AND ("Channel"<>''legacy'' OR "ApiTokenId" IS NOT NULL)) + (SELECT count(*) FROM "PagePublication", m WHERE "ActiveFrom"<m.at AND ("Channel"<>''legacy'' OR "ApiTokenId" IS NOT NULL)) + (SELECT count(*) FROM "PageAccessEvent", m WHERE "OccurredAt"<m.at AND ("Channel" IS NOT NULL OR "ApiTokenId" IS NOT NULL));', '0')
        # Migration 2026100620 (Atlas 2.0, T3): no API write is invented for history, and the MCP module is off after the upgrade (O2).
        'api activity: no rows and MCP off after the upgrade' = @('SELECT (SELECT count(*) FROM "ApiActivity") + (SELECT count(*) FROM "McpSetting");', '0')
        # Migrations add no audit rows: none from before 1.1 (no table yet), the old release's own rows from 1.1 on.
        'page audit: no synthetic legacy backfill'            = @('SELECT count(*) FROM "PageAccessEvent";', "$(if ($before.Contains('PageAccessEvent')) { $before['PageAccessEvent'].count } else { 0 })")
        'EffectivePageAccess agrees with legacy space grants' = @(@'
SELECT count(*) FROM (
  (SELECT u."Id", p."Id", max(r) FROM "UserAccount" u CROSS JOIN "Page" p CROSS JOIN LATERAL (
     SELECT 3 AS r WHERE u."IsWikiAdmin"
     UNION ALL SELECT g."Role" FROM "SpaceGrant" g WHERE g."ActiveTo" IS NULL AND g."SpaceId"=p."SpaceId" AND g."UserId"=u."Id"
     UNION ALL SELECT g."Role" FROM "SpaceGrant" g JOIN "GroupMember" m ON m."GroupId"=g."GroupId" AND m."ActiveTo" IS NULL
       WHERE g."ActiveTo" IS NULL AND g."SpaceId"=p."SpaceId" AND m."UserId"=u."Id") x
   WHERE u."IsActive" GROUP BY u."Id", p."Id"
   EXCEPT SELECT "UserId", "PageId", "Role" FROM "EffectivePageAccess")
  UNION ALL
  (SELECT "UserId", "PageId", "Role" FROM "EffectivePageAccess"
   EXCEPT SELECT u."Id", p."Id", max(r) FROM "UserAccount" u CROSS JOIN "Page" p CROSS JOIN LATERAL (
     SELECT 3 AS r WHERE u."IsWikiAdmin"
     UNION ALL SELECT g."Role" FROM "SpaceGrant" g WHERE g."ActiveTo" IS NULL AND g."SpaceId"=p."SpaceId" AND g."UserId"=u."Id"
     UNION ALL SELECT g."Role" FROM "SpaceGrant" g JOIN "GroupMember" m ON m."GroupId"=g."GroupId" AND m."ActiveTo" IS NULL
       WHERE g."ActiveTo" IS NULL AND g."SpaceId"=p."SpaceId" AND m."UserId"=u."Id") x
   WHERE u."IsActive" GROUP BY u."Id", p."Id")) d;
'@, '0')
    }
    foreach ($name in $invariants.Keys) {
        try { $value = Get-SqlValue $invariants[$name][0]; Add-Result $name ($value -eq $invariants[$name][1]) "value $value" }
        catch { Add-Result $name $false $_.Exception.Message }
    }
    $pages = Get-SqlValue 'SELECT count(*) FROM "Page";'
    Write-Host "Pages: $pages"

    # --- verification through the new API and browser -----------------------------------------
    function Invoke-Verify([string]$phase) {
        $code = Invoke-Playwright "new-verify-$phase" '/repo/tests/browser' @('node', '/repo/tests/upgrade/verify-new.mjs', $phase)
        $verifyFile = Join-Path $outRoot "verify-$phase.json"
        if (Test-Path $verifyFile) {
            foreach ($item in (Get-Content $verifyFile -Raw | ConvertFrom-Json)) { Add-Result "verify: $($item.check)" ([bool]$item.ok) "$($item.detail)" }
        } else { Add-Result "verify: verify-new.mjs $phase produced verify-$phase.json" $false "exit $code" }
    }
    Invoke-Verify 'main'

    # Inspected before the backup restore, which legitimately resets live collaboration state.
    $collabAfter = Invoke-Sql $collabSql
    Write-Host "Collaboration state after opening and saving in the new build: $collabAfter"
    $b = "$collabBefore" -split '\|'; $a = "$collabAfter" -split '\|'
    $plugins = ([regex]::Matches($b[1], '"(?:id|Id)"\s*:\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }) -join ','
    # When a plugin's schema version moved between the releases (1.0 had nine plugins and no openapi; 3.4 raised atlas-requirements to 2)
    # the binding moves to the current schema with a new generation; on the same schema the unsaved updates keep their generation.
    $schemaMoved = $a[1] -ne $b[1]
    if ($b[1] -notmatch 'openapi') { Add-Result 'collaboration: page bound to the old 9-plugin schema before upgrade' (@($plugins -split ',').Count -eq 9) $plugins }
    else { Add-Result 'collaboration: old release already has openapi' $true $plugins }
    # Unsaved work before the upgrade: sequence beyond the last saved one, kept as log rows and/or an unsaved snapshot.
    Add-Result 'collaboration: unsaved updates persisted before upgrade (not in any revision)' ([long]$b[3] -gt [long]$b[5]) "sequence $($b[3]), snapshot $($b[4]), last saved $($b[5]), log rows $($b[2])"
    if ($schemaMoved) { Add-Result 'collaboration: page moved to the current schema with openapi' ($a[1] -match '"openapi"' -and $a[0] -ne $b[0]) ("generation $($b[0]) -> $($a[0]), sequence $($a[3]), last saved $($a[5])") }
    else { Add-Result 'collaboration: unsaved updates keep their generation on the same schema' ($a[1] -match '"openapi"' -and $a[0] -eq $b[0]) ("generation $($b[0]) -> $($a[0]), sequence $($a[3]), last saved $($a[5])") }

    Invoke-Verify 'backup'

    # --- smoke subset of the current browser acceptance ----------------------------------------
    if (-not $SkipSmoke -and $SmokeSpec) {
        if ($ThrottlePauseSeconds -gt 0) { Write-Host "Login throttle pause: $ThrottlePauseSeconds s"; Start-Sleep -Seconds $ThrottlePauseSeconds }
        $specs = @($SmokeSpec -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $flags = @('ATLAS_E2E_ALLOW_COLLAB', 'ATLAS_E2E_ALLOW_UPLOADS', 'ATLAS_E2E_ALLOW_IMPORT', 'ATLAS_E2E_ALLOW_TAGS') | ForEach-Object { @('-e', "$_=1") }
        $code = Invoke-Playwright 'new-smoke' '/repo/tests/browser' (@('node', 'node_modules/@playwright/test/cli.js', 'test') + $specs +
            @('--reporter=list,html,json', "--output=$outInPod/new-smoke/test-results")) $flags
        $smokeDetail = ''
        $json = Join-Path $outRoot 'new-smoke/results.json'
        if (Test-Path $json) { $stats = (Get-Content $json -Raw | ConvertFrom-Json).stats; $smokeDetail = "expected $($stats.expected), unexpected $($stats.unexpected), flaky $($stats.flaky), skipped $($stats.skipped)" }
        Add-Result "smoke: current tests/browser on the upgraded instance ($($specs -join ' '))" ($code -eq 0) $smokeDetail
    }
} catch {
    $failed = $true
    Add-Result 'harness' $false $_.Exception.Message
} finally {
    podman pod exists $run *> $null
    if ($LASTEXITCODE -eq 0) { Save-Logs '-final' }
    if ($Keep) {
        Write-Host "Left running: $base  (remove: podman pod rm -f $run; podman volume rm $run-pg $run-data; podman secret rm $($createdSecrets -join ' '))"
    } else {
        podman pod rm -f $run *> $null
        podman volume rm -f "$run-pg" "$run-data" *> $null
        foreach ($secret in $createdSecrets) { podman secret rm $secret *> $null }
        if ($createdWorktree) {
            # Untracked build output (node_modules, bin/obj) can survive `worktree remove`; delete it and prune the registration.
            git -C $repo worktree remove --force $createdWorktree 2>&1 | Out-Null
            if (Test-Path $createdWorktree) { Remove-Item -LiteralPath $createdWorktree -Recurse -Force -ErrorAction SilentlyContinue }
            git -C $repo worktree prune 2>&1 | Out-Null
        }
    }
    if (@($results | Where-Object { -not $_.ok }).Count -gt 0) { $failed = $true }
    $summary = [ordered]@{
        run = $run; oldRef = $OldRef; oldCommit = $oldSha; newHead = $newHead; uncommittedPaths = $dirty
        oldAppImage = $OldAppImage; newAppImage = $NewAppImage; oldPostgresImage = $OldPostgresImage; postgresImage = $PostgresImage
        oldSeedTime = "$oldSeedTime"; migrationStartTime = "$migrationTime"; total = "$($total.Elapsed)"; failed = $failed; results = $results
    }
    $summary | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $outRoot 'summary.json')
    Write-Host ("`nReports: $outRoot (total {0:hh\:mm\:ss}, {1}; {2} checks, {3} failed)" -f $total.Elapsed, $(if ($failed) { 'FAILED' } else { 'passed' }),
        $results.Count, @($results | Where-Object { -not $_.ok }).Count)
    Stop-Transcript | Out-Null
}
exit ([int]$failed)
