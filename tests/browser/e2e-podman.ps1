<#
.SYNOPSIS
    Disposable Atlas browser acceptance in one Podman pod: the production PostgreSQL image (18.6 with pgvector), the published
    server and the official Playwright image. Everything is removed afterwards.

.DESCRIPTION
    1. Publishes the server on the host (dotnet publish, which also builds the frontend with pnpm)
       unless -PublishDir or -AppImage is given. -EditorMode source builds the frontend from the
       editor sources (ATLAS_EDITOR_MODE=source; src/Atlas.Markdown or -EditorSource). The previous publish is reused when its
       inputs are unchanged (working tree of src/ and the build files, editor mode and, in source mode,
       the editor sources); -Republish forces a new one.
    2. Creates a pod with a throwaway PostgreSQL (tmpfs; deploy/postgres/Dockerfile built for linux/amd64 as
       localhost/atlas-postgres:<tag>, exactly the production database image), the server in the Dockerfile stage `runtime`
       (aspnet with git and OpenSSH, built as localhost/atlas-e2e-runtime; the publish is copied into an image of its own on
       top of it, localhost/atlas-e2e-app:<hash>, as the production image does; data directories on tmpfs), a disposable SSH
       git server (tests/browser/git-server) and
       generates the one-time bootstrap token and test password as Podman secrets. Nothing is written to
       versioned files. The server image must run git, ssh and ssh-keygen, or the run stops before it starts.
    3. Runs tests/browser inside mcr.microsoft.com/playwright:v<installed @playwright/test>-noble
       against http://localhost:8080 in the pod: first installation (first-run.spec.ts), then the
       README acceptance batches. The batches need nothing but the server image, so by default each
       runs in its own pod at the same time (-Sequential: one pod, one batch after another). After a
       batch with git-backup.spec.ts the script checks the pushed commits in the bare repository itself.
    The batch `ai` (ai-search.spec.ts) also gets the fake OpenAI-compatible model server tests/browser/fake-ai/server.mjs as a
    container of its pod (the Playwright image's Node, the directory mounted read-only): Atlas reaches it as
    http://fake-ai:1234/v1 (the pod resolves the name to its own loopback), the scenarios steer it on http://localhost:1234
    (/__mode, /__meaning, /__requests). No model is ever called. Run just that batch: -Batch ai.
    4. Stores reports under artifacts/e2e-podman/<run>/ and removes pod, containers, volume and secrets.
       The built images stay for the layer cache; app images of earlier publishes of the directory are removed.

    The browser tests' node_modules (@playwright/test, playwright, playwright-core) are plain
    JavaScript, so the host installation from `npm --prefix tests/browser ci` is used as-is.

.EXAMPLE
    pwsh tests/browser/e2e-podman.ps1
.EXAMPLE
    pwsh tests/browser/e2e-podman.ps1 -Batch ai
.EXAMPLE
    pwsh tests/browser/e2e-podman.ps1 -EditorMode source
.EXAMPLE
    pwsh tests/browser/e2e-podman.ps1 -PublishDir artifacts/e2e-podman/publish-package -Spec shell-surfaces.spec.ts,usability.spec.ts
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    # Frontend editor: the built dist of src/Atlas.Markdown, or its sources.
    [ValidateSet('package', 'source')]
    [string]$EditorMode = 'package',
    # Another editor package for -EditorMode source (default: src/Atlas.Markdown).
    [string]$EditorSource = '',
    # Reuse an existing publish output instead of publishing again.
    [string]$PublishDir = '',
    # Publish again even when the inputs of the previous publish are unchanged.
    [switch]$Republish,
    # Run a prebuilt server image (for example one built from the Dockerfile) instead of a publish directory.
    [string]$AppImage = '',
    # Specs after the first installation, comma-separated. Default: the README acceptance batches.
    [string]$Spec = '',
    # One named acceptance batch instead of all (the same specs as in the default run): collab, search, git-backup, ai, storage or projector.
    [ValidateSet('', 'collab', 'search', 'git-backup', 'ai', 'storage', 'projector')]
    [string]$Batch = '',
    # Only run the first installation (seeding) and stop.
    [switch]$InstallOnly,
    # Explicit prefers-reduced-motion; specs that test both values emulate them themselves.
    [ValidateSet('no-preference', 'reduce')]
    [string]$ReducedMotion = 'no-preference',
    # Pause between acceptance batches. The builtin login throttle counts only failed password proofs (ten per minute
    # per account), so the batches need none; a spec that adds many wrong passwords can ask for one.
    [int]$ThrottlePauseSeconds = 0,
    # Host port on 127.0.0.1 for the pod's server (0 = pick a free one). Useful with -Keep.
    [int]$HostPort = 0,
    # Leave the pod running for inspection; remove it later with `podman pod rm -f <name>`.
    [switch]$Keep,
    # Run the default acceptance batches one after another in one pod instead of one pod per batch at the same time.
    [switch]$Sequential,
    # ASP.NET environment. Development allows the auth cookie over plain HTTP inside the pod;
    # Production requires HTTPS (CookieSecurePolicy.Always + HTTPS redirection).
    [string]$AspNetEnvironment = 'Development',
    # Database image. Default: deploy/postgres/Dockerfile (postgres:18.6-alpine with pgvector, as in production) built for
    # linux/amd64 by deploy/postgres/build.ps1; an image given here is used as-is and needs pgvector (migration 2026100520).
    [string]$PostgresImage = '',
    # Server runtime the publish directory is copied onto. Default: the Dockerfile stage `runtime` (git, OpenSSH), built from
    # an empty context as localhost/atlas-e2e-runtime; an image given here is used as-is and must have git and ssh too.
    [string]$AspNetImage = '',
    # SSH git server of git-backup.spec.ts. Default: tests/browser/git-server/Containerfile, built as localhost/atlas-e2e-git-server.
    [string]$GitServerImage = '',
    # Physical repo-shaped directory with tests/browser/node_modules, for hosts whose checkout crosses a junction.
    # Only browser sources are copied here; publish output is still selected by -PublishDir.
    [string]$BrowserStageDir = '',
    # Disposable SMB server, only started for storage-export.spec.ts.
    [string]$SmbImage = 'docker.io/dperson/samba:latest'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$browserTests = Join-Path $repo 'tests/browser'
$browserRepo = $repo
if ($BrowserStageDir) {
    $browserRepo = (Resolve-Path -LiteralPath $BrowserStageDir).Path
    # Podman on Windows cannot mount a checkout beneath a junction, even if its leaf is a plain directory.
    if ($IsWindows) {
        $ancestor = Get-Item -LiteralPath $browserRepo
        while ($ancestor) {
            if ($ancestor.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw "BrowserStageDir crosses a junction: $($ancestor.FullName)" }
            $ancestor = $ancestor.Parent
        }
    }
    $stagedTests = Join-Path $browserRepo 'tests/browser'
    if (-not (Test-Path -LiteralPath (Join-Path $stagedTests 'node_modules/@playwright/test/package.json'))) {
        throw 'BrowserStageDir needs tests/browser/node_modules; install the browser dependencies in this physical directory first.'
    }
    foreach ($source in Get-ChildItem -LiteralPath $browserTests -Recurse -File) {
        $relative = [System.IO.Path]::GetRelativePath($browserTests, $source.FullName)
        if ($relative.Replace('\', '/').Split('/')[0] -in @('node_modules', 'test-results', 'playwright-report')) { continue }
        $target = Join-Path $stagedTests $relative
        # Child batches reuse the parent's completed copy; do not rewrite a source while another batch reads it.
        if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $source.FullName).Hash -eq (Get-FileHash -LiteralPath $target).Hash) { continue }
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath $source.FullName -Destination $target -Force
    }
}
$run = 'atlas-e2e-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 4)
# tests/browser/README.md, "Pořadí průchodů", plus csp.spec.ts. git-backup.spec.ts has a pod of its own: the repository then
# holds only its page and first-run.spec.ts's, all by the one admin, and the running backup cannot touch the other specs.
$defaultBatches = @(
    @('block-moves.spec.ts', 'collaboration-access.spec.ts', 'collaboration.spec.ts', 'cross-space-move.spec.ts', 'editor-integrations.spec.ts', 'library-export.spec.ts', 'live-readers.spec.ts', 'product-finishing.spec.ts'),
    @('search-filters.spec.ts', 'space-creation.spec.ts', 'space-image.spec.ts', 'tree-navigation.spec.ts', 'usability.spec.ts', 'wiki-import.spec.ts', 'workspace-consistency.spec.ts', 'csp.spec.ts'),
    @('git-backup.spec.ts'),
    # AI (phase 2): ai-search.spec.ts. Its pod also runs the fake model server; the spec configures AI itself through Administrace.
    @('ai-search.spec.ts'),
    @('storage-export.spec.ts'),
    # Projector v1 (requirement blocks in pages): one spec per story, the module switched on in the host below.
    @('projector-kbn-from-scratch.spec.ts', 'projector-copy-deployment.spec.ts', 'projector-waiting.spec.ts', 'projector-collaboration.spec.ts', 'projector-rights.spec.ts', 'projector-odd-cases.spec.ts', 'projector-keyboard.spec.ts')
)
$batchNames = @('collab', 'search', 'git-backup', 'ai', 'storage', 'projector')
if ($Batch -and -not $Spec) { $Spec = $defaultBatches[$batchNames.IndexOf($Batch)] -join ',' }
# The fake model server (tests/browser/fake-ai) runs in the pod of every batch that contains an ai-*.spec.ts.
$fakeAiPort = 1234
$specsToRun = if ($Spec) { @($Spec -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) } else { @($defaultBatches | ForEach-Object { $_ }) }
$needsFakeAi = -not $InstallOnly -and [bool]($specsToRun | Where-Object { $_ -like 'ai-*.spec.ts' })
# The SSH git server in the pod (tests/browser/git-server/Containerfile) and the author git-backup.spec.ts's revisions get:
# first-run.spec.ts creates "Test správce" (browser-admin) without an e-mail, so the address falls back to <username>@atlas.invalid
# unless an earlier batch set a contact e-mail (workspace-consistency.spec.ts); git-backup.json then carries it as authorEmail.
$gitRemote = 'ssh://git@localhost:2222/srv/git/atlas.git'
$gitBareRepository = '/srv/git/atlas.git'
$gitAuthor = @{ Name = 'Test správce'; Email = 'browser-admin@atlas.invalid' }
$outRoot = Join-Path $browserRepo "artifacts/e2e-podman/$run"
$needsSmb = -not $InstallOnly -and (-not $Spec -or (($Spec -split ',' | ForEach-Object { $_.Trim() }) -contains 'storage-export.spec.ts'))
$total = [System.Diagnostics.Stopwatch]::StartNew()

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port } finally { $listener.Stop() }
}

function New-Secret([int]$length) {
    $alphabet = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'.ToCharArray()
    -join (1..$length | ForEach-Object { $alphabet[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($alphabet.Length)] })
}

. (Join-Path $PSScriptRoot '../TestParts.ps1')

function Invoke-Podman {
    $output = & podman @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "podman $($args -join ' ') failed: $output" }
    $output
}

# --- prerequisites -----------------------------------------------------------------------------
if (-not (Get-Command podman -ErrorAction SilentlyContinue)) { throw 'podman is not on PATH.' }
podman info --format '{{.Host.OS}}' *> $null
if ($LASTEXITCODE -ne 0) { throw 'Podman is not reachable. Start the machine: podman machine start' }

$testPackage = Join-Path $browserRepo 'tests/browser/node_modules/@playwright/test/package.json'
if (-not (Test-Path $testPackage)) { throw 'Install the browser tests first: npm --prefix tests/browser ci' }
$version = (Get-Content $testPackage -Raw | ConvertFrom-Json).version
$locked = ((Get-Content (Join-Path $browserTests 'package-lock.json') -Raw | ConvertFrom-Json -AsHashtable).packages['node_modules/@playwright/test']).version
if ($version -ne $locked) { throw "Installed @playwright/test $version differs from package-lock.json $locked. Run: npm --prefix tests/browser ci" }
$playwrightImage = "mcr.microsoft.com/playwright:v$version-noble"

$timeZone = 'UTC'
$iana = $null
if ([TimeZoneInfo]::TryConvertWindowsIdToIanaId([TimeZoneInfo]::Local.Id, [ref]$iana)) { $timeZone = $iana }

# --- server build ------------------------------------------------------------------------------
if (-not $AppImage) {
    if ($PublishDir) {
        $PublishDir = (Resolve-Path $PublishDir).Path
    } else {
        $PublishDir = Join-Path $repo "artifacts/e2e-podman/publish-$EditorMode-linux-x64"
        $source = if ($EditorSource) { (Resolve-Path $EditorSource).Path } else { $env:ATLAS_EDITOR_SOURCE }
        $publishInputs = @('src', 'Directory.Build.props', 'Directory.Build.targets', 'global.json')
        $publishState = Get-InputState $repo (Get-WorkingTree $repo) $publishInputs (Get-EditorState $repo $EditorMode $source)
        $stateFile = "$PublishDir.state"
        if (-not $Republish -and (Test-Path (Join-Path $PublishDir 'wwwroot/index.html')) -and (Test-Path $stateFile) -and (Get-Content $stateFile -Raw).Trim() -eq $publishState) {
            Write-Host "Reusing $PublishDir (inputs unchanged; -Republish publishes again)"
        } else {
            $env:ATLAS_EDITOR_MODE = $EditorMode
            if ($source) { $env:ATLAS_EDITOR_SOURCE = $source }
            Write-Host "Publishing the server ($EditorMode editor) to $PublishDir"
            try {
                Remove-Item $stateFile -ErrorAction SilentlyContinue
                if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
                New-Item -ItemType Directory -Force (Split-Path $PublishDir) | Out-Null
                $publishLog = "$PublishDir.log"
                # Release publishes refuse editor source (Directory.Build.targets); this disposable test build opts in.
                $publishArgs = @('-c', 'Release', '--os', 'linux', '--arch', 'x64', '--self-contained', 'false', '-o', $PublishDir, '-p:UseAppHost=false')
                if ($EditorMode -eq 'source') { $publishArgs += '-p:AllowEditorSourcePublish=true' }
                dotnet publish (Join-Path $repo 'src/Atlas.Server/Atlas.Server.csproj') @publishArgs *> $publishLog
                if ($LASTEXITCODE -ne 0) { Get-Content $publishLog -Tail 30 | Write-Host; throw "dotnet publish failed; see $publishLog" }
                Set-Content $stateFile $publishState
            } finally {
                Remove-Item Env:ATLAS_EDITOR_MODE, Env:ATLAS_EDITOR_SOURCE -ErrorAction SilentlyContinue
            }
        }
    }
    if (-not (Test-Path (Join-Path $PublishDir 'wwwroot/index.html'))) { throw "$PublishDir has no wwwroot/index.html; publish with the frontend build." }
}

# --- images ------------------------------------------------------------------------------------
# Built once per run (seconds from the layer cache) and handed to the batch pods as explicit images; kept afterwards.
function Build-Image([string]$tag, [string]$file, [string]$context, [string[]]$options = @()) {
    Write-Host "Building $tag from $file"
    $log = & podman build @options -t $tag -f $file $context 2>&1
    if ($LASTEXITCODE -ne 0) { $log | Select-Object -Last 30 | ForEach-Object { Write-Host "  $_" }; throw "podman build of $tag ($file) failed." }
}
if (-not $AppImage -and -not $AspNetImage) {
    $AspNetImage = 'localhost/atlas-e2e-runtime:latest'
    # The stage has no COPY and does not depend on the build stage: an empty context, no sources and no secrets.
    $context = Join-Path ([System.IO.Path]::GetTempPath()) "$run-runtime-context"
    New-Item -ItemType Directory -Force $context | Out-Null
    try { Build-Image $AspNetImage (Join-Path $repo 'Dockerfile') $context @('--target', 'runtime', '--platform', 'linux/amd64') } finally { Remove-Item $context -Recurse -Force -ErrorAction SilentlyContinue }
}
if (-not $PostgresImage) {
    $PostgresImage = @(& (Join-Path $repo 'deploy/postgres/build.ps1') -Platform 'linux/amd64')[-1]
}
if (-not $GitServerImage) {
    $GitServerImage = 'localhost/atlas-e2e-git-server:latest'
    Build-Image $GitServerImage (Join-Path $browserTests 'git-server/Containerfile') (Join-Path $browserTests 'git-server')
}
# The publish goes into an image layer instead of a bind mount: on the WSL machine the Windows directory is a 9p mount, and
# with five batch pods compiling Wolverine handlers at once, reading the reference assemblies there failed with "Cannot
# allocate memory" (JasperFx.dll, NHibernate.dll ...). The tag hashes the Containerfile (with the pinned runtime) and every
# published file, so the batch pods and later runs with the same publish share one build. Returns the image id.
function Build-AppImage([string]$runtime, [string]$publishDir) {
    $containerfile = "FROM $runtime`nWORKDIR /app`nCOPY . .`nEXPOSE 8080`nENTRYPOINT [`"dotnet`", `"Atlas.Server.dll`"]`n"
    $hash = [System.Security.Cryptography.IncrementalHash]::CreateHash('SHA256')
    $hash.AppendData([System.Text.Encoding]::UTF8.GetBytes($containerfile))
    $files = [string[]]@(Get-ChildItem -LiteralPath $publishDir -Recurse -File -Force | ForEach-Object { [System.IO.Path]::GetRelativePath($publishDir, $_.FullName).Replace('\', '/') })
    [Array]::Sort($files, [StringComparer]::Ordinal)
    foreach ($path in $files) {
        $stream = [System.IO.File]::OpenRead((Join-Path $publishDir $path))
        try { $content = [System.Security.Cryptography.SHA256]::HashData($stream) } finally { $stream.Dispose() }
        $hash.AppendData([System.Text.Encoding]::UTF8.GetBytes("`n$path`n"))
        $hash.AppendData($content)
    }
    $tag = 'localhost/atlas-e2e-app:' + [Convert]::ToHexString($hash.GetHashAndReset()).Substring(0, 16).ToLowerInvariant()
    $label = "atlas.e2e.publish-dir=$publishDir"
    podman image exists $tag *> $null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Reusing $tag (publish and runtime unchanged)"
    } else {
        $file = Join-Path ([System.IO.Path]::GetTempPath()) "$run-app.Containerfile"
        [System.IO.File]::WriteAllText($file, $containerfile)
        try { Build-Image $tag $file $publishDir @('--pull=never', '--platform', 'linux/amd64', '--label', $label) } finally { Remove-Item $file -Force -ErrorAction SilentlyContinue }
    }
    # Only the current publish of a directory is useful: drop the tags of its earlier ones (an image a container still uses
    # stays). An untagged duplicate of this tag may belong to a run building it at the same moment, so it is left alone.
    foreach ($old in & podman images --filter "label=$label" --format '{{.Repository}}:{{.Tag}}') {
        if ($old -ne $tag -and $old -notlike '*<none>*') { podman rmi $old *> $null }
    }
    (Invoke-Podman image inspect $tag --format '{{.Id}}' | Out-String).Trim()
}
# Parallel developer runs can rebuild the shared tags. Pin this run and its child batches to the resolved images.
if ($AppImage) {
    $AppImage = (Invoke-Podman image inspect $AppImage --format '{{.Id}}' | Out-String).Trim()
} else {
    $AspNetImage = (Invoke-Podman image inspect $AspNetImage --format '{{.Id}}' | Out-String).Trim()
    $AppImage = Build-AppImage $AspNetImage $PublishDir
}

if ($needsSmb) {
    podman image exists $SmbImage *> $null
    if ($LASTEXITCODE -ne 0) { Invoke-Podman pull --platform linux/amd64 $SmbImage | Out-Null }
    $smbPlatform = (Invoke-Podman image inspect $SmbImage --format '{{.Os}}/{{.Architecture}}' | Out-String).Trim()
    if ($smbPlatform -ne 'linux/amd64') { throw "SMB acceptance requires linux/amd64, got $smbPlatform." }
    $SmbImage = (Invoke-Podman image inspect $SmbImage --format '{{.Id}}' | Out-String).Trim()
}

# The git backup runs git and ssh in the server container: prove the image has them before a pod starts.
$tools = & podman run --rm --entrypoint sh $AppImage -c "git --version && ssh -V && ssh-keygen -q -t ed25519 -N '' -f /tmp/atlas-e2e-key && test -s /tmp/atlas-e2e-key.pub" 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Server image $AppImage cannot run git, ssh and ssh-keygen (the git backup needs them; see the Dockerfile stage runtime): $($tools -join ' ')"
}
Write-Host "Server image $AppImage has $((@($tools) | ForEach-Object { "$_".Trim() }) -join ', ')"

# --- one pod per batch -------------------------------------------------------------------------
if (-not $Spec -and -not $InstallOnly -and -not $Sequential -and -not $Keep) {
    $common = @('-ReducedMotion', $ReducedMotion, '-AspNetEnvironment', $AspNetEnvironment, '-PostgresImage', $PostgresImage,
        '-GitServerImage', $GitServerImage, '-SmbImage', $SmbImage, '-ThrottlePauseSeconds', $ThrottlePauseSeconds, '-Sequential',
        '-AppImage', $AppImage)
    if ($BrowserStageDir) { $common += @('-BrowserStageDir', $browserRepo) }
    $parts = [ordered]@{}
    $arguments = @{}
    for ($i = 0; $i -lt $defaultBatches.Count; $i++) {
        $parts["batch-$($i + 1)"] = {
            param($script, $parameters)
            # A native command receives each array element as its own argument.
            & pwsh -NoProfile -File $script ([string[]]$parameters)
            "__EXIT__=$LASTEXITCODE"
        }
        $arguments["batch-$($i + 1)"] = @($PSCommandPath, @($common + @('-Spec', ($defaultBatches[$i] -join ','))))
    }
    $passed = Invoke-TestParts -Parts $parts -Arguments $arguments -Logs $outRoot -Title 'Browser acceptance'
    Write-Host ("Logs: $outRoot, reports: one artifacts/e2e-podman/atlas-e2e-* run per batch (total {0:hh\:mm\:ss})" -f $total.Elapsed)
    exit $(if ($passed) { 0 } else { 1 })
}

# --- pod ---------------------------------------------------------------------------------------
if ($HostPort -eq 0) { $HostPort = Get-FreePort }
$secrets = [ordered]@{
    bootstrap = New-Secret 48
    password  = New-Secret 24
    db        = New-Secret 24
}
if ($needsSmb) { $secrets.smb = New-Secret 24 }
$connection = "Host=localhost;Port=5432;Database=atlas;Username=atlas;Password=$($secrets.db)"
New-Item -ItemType Directory -Force $outRoot | Out-Null
$failed = $false
$created = @()
# Shared by Playwright (git-backup.spec.ts writes the deploy key) and the git server (sshd reads authorized_keys).
$gitKeys = "$run-git-keys"
$smbShare = "$run-smb-share"
$appData = "$run-app-data"

function Save-Logs {
    param([string]$Suffix = '')
    $logParts = @('db', 'app', 'git')
    if ($needsFakeAi) { $logParts += 'fake-ai' }
    if ($needsSmb) { $logParts += 'smb' }
    foreach ($part in $logParts) {
        try {
            $text = (& podman logs "$run-$part" 2>&1 | Out-String)
            # The server logs one-time tokens by design; never keep them in reports.
            foreach ($value in $secrets.Values) { $text = $text.Replace($value, '***') }
            $text = $text -replace '(installation token|recovery token): \S+', '$1: ***'
            Set-Content -LiteralPath (Join-Path $outRoot "$part$Suffix.log") -Value $text
        } catch { Write-Warning "Could not save the $part log: $($_.Exception.Message)" }
    }
}

try {
    # Via a short-lived file: piping a string to a native command would append a newline.
    $values = [ordered]@{ bootstrap = $secrets.bootstrap; password = $secrets.password; db = $secrets.db; connection = $connection }
    if ($needsSmb) {
        $values.smb = $secrets.smb
        # dperson/samba accepts USER through the environment; no password goes into container command arguments.
        $values['smb-user'] = "atlas;$($secrets.smb)"
    }
    foreach ($name in $values.Keys) {
        $file = Join-Path ([System.IO.Path]::GetTempPath()) "$run-$name"
        [System.IO.File]::WriteAllText($file, $values[$name])
        try { podman secret create "$run-$name" $file | Out-Null } finally { Remove-Item $file -Force }
        if ($LASTEXITCODE -ne 0) { throw "Could not create Podman secret $run-$name" }
        $created += "$run-$name"
    }

    Write-Host "Pod $run (server at http://127.0.0.1:$HostPort, TZ=$timeZone)"
    # fake-ai resolves to the pod's own loopback: the AI profile's address is http://fake-ai:1234/v1, as in the plan.
    $podArguments = @('--name', $run, '--shm-size', '1g', '-p', "127.0.0.1:${HostPort}:8080")
    if ($needsFakeAi) { $podArguments += @('--add-host', 'fake-ai:127.0.0.1') }
    Invoke-Podman pod create @podArguments | Out-Null

    Invoke-Podman run -d --pod $run --name "$run-db" `
        -e POSTGRES_USER=atlas -e POSTGRES_DB=atlas -e "TZ=$timeZone" `
        --secret "$run-db,type=env,target=POSTGRES_PASSWORD" `
        --tmpfs /var/lib/postgresql:rw `
        $PostgresImage | Out-Null
    $ready = $false
    foreach ($attempt in 1..120) {
        # TCP, not the socket: the entrypoint's temporary init server listens on the socket only.
        podman exec "$run-db" pg_isready -h 127.0.0.1 -U atlas -d atlas *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'PostgreSQL did not become ready.' }

    Invoke-Podman volume create $gitKeys | Out-Null
    Invoke-Podman run -d --pod $run --name "$run-git" -v "${gitKeys}:/git-keys" $GitServerImage | Out-Null
    $listening = $false
    foreach ($attempt in 1..40) {
        if ((& podman logs "$run-git" 2>&1 | Out-String) -match 'Server listening on') { $listening = $true; break }
        if (-not (podman ps -q --filter "name=^$run-git$")) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $listening) { Save-Logs; throw "The SSH git server did not start; see $outRoot\git.log" }

    if ($needsFakeAi) {
        # The Playwright image already has Node (the scripts use no other); the server needs no dependencies.
        Invoke-Podman run -d --pod $run --name "$run-fake-ai" -e "PORT=$fakeAiPort" -v "$((Join-Path $browserRepo 'tests/browser/fake-ai')):/fake-ai:ro" $playwrightImage node /fake-ai/server.mjs | Out-Null
        $listening = $false
        foreach ($attempt in 1..40) {
            if ((& podman logs "$run-fake-ai" 2>&1 | Out-String) -match 'fake-ai listening') { $listening = $true; break }
            if (-not (podman ps -q --filter "name=^$run-fake-ai$")) { break }
            Start-Sleep -Milliseconds 250
        }
        if (-not $listening) { Save-Logs; throw "The fake AI server did not start; see $outRoot\fake-ai.log" }
        Write-Host "Fake AI server ready (Atlas: http://fake-ai:$fakeAiPort/v1)."
    }
    if ($needsSmb) {
        # Both mounts avoid copying image files into this disposable share.
        Invoke-Podman volume create $smbShare | Out-Null
        Invoke-Podman run -d --pod $run --name "$run-smb" -v "${smbShare}:/exports:nocopy" `
            --secret "$run-smb-user,type=env,target=USER" $SmbImage `
            -s 'exports;/exports;yes;no;no;atlas' -r -p -w WORKGROUP | Out-Null
        $listening = $false
        foreach ($attempt in 1..40) {
            podman exec "$run-smb" smbcontrol smbd ping *> $null
            if ($LASTEXITCODE -eq 0) { $listening = $true; break }
            if (-not (podman ps -q --filter "name=^$run-smb$")) { break }
            Start-Sleep -Milliseconds 250
        }
        if (-not $listening) { Save-Logs; throw "The SMB server did not start; see $outRoot\smb.log" }
        # Podman initializes ownership again on an empty named volume when Playwright mounts it, even readonly/nocopy.
        # A fixture marker makes initialization complete while each page's dedicated export folder remains absent.
        Invoke-Podman exec "$run-smb" sh -c 'umask 077; printf "Atlas disposable SMB fixture\n" > /exports/.atlas-test-share' | Out-Null
    }

    $appEnvironment = @(
        '-e', "ASPNETCORE_ENVIRONMENT=$AspNetEnvironment", '-e', 'ASPNETCORE_HTTP_PORTS=8080', '-e', "TZ=$timeZone",
        '-e', 'Security__DataPath=/data/security', '-e', 'DataProtection__KeysPath=/data/keys',
        '-e', 'Authentication__OpenIdConnect__CertificateDirectory=/data/oidc', '-e', 'Attachments__DataPath=/data/attachments',
        '-e', 'ContentImport__DataPath=/data/import', '-e', 'ContentExport__DataPath=/data/export', '-e', 'Vaults__DataPath=/data/vaults',
        '-e', 'GitBackup__DataPath=/data/git-backup', '-e', 'Modules__Projector__Enabled=true',
        # The publish carries no appsettings.Development.json: its two switches (developer sign-in, boards) are named here.
        '-e', 'Authentication__DeveloperSignIn__Enabled=true', '-e', 'Modules__boards__Enabled=true',
        '--secret', "$run-connection,type=env,target=ConnectionStrings__atlas-db",
        '--secret', "$run-bootstrap,type=env,target=Setup__BootstrapToken"
    )
    if ($needsSmb) {
        # Restart acceptance needs the same persisted DataProtection keys as production; tmpfs is reset on restart.
        Invoke-Podman volume create $appData | Out-Null
        $appEnvironment += @('-v', "${appData}:/data")
    } else {
        $appEnvironment += @('--tmpfs', '/data:rw')
    }
    Invoke-Podman run -d --pod $run --name "$run-app" @appEnvironment $AppImage | Out-Null
    $status = $null
    foreach ($attempt in 1..120) {
        try { $status = Invoke-RestMethod "http://127.0.0.1:$HostPort/api/setup/status" -TimeoutSec 2; if ($status.databaseReady) { break } } catch { }
        if (-not (podman ps -q --filter "name=^$run-app$")) { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $status.databaseReady) { Save-Logs; throw "The server did not become ready; see $outRoot\app.log" }
    if (-not $status.requiresSetup) { throw 'The new database is unexpectedly already installed.' }
    Write-Host 'Server ready; database empty.'

    # --- tests ---------------------------------------------------------------------------------
    function Invoke-Phase([string]$phase, [string[]]$specs, [string[]]$flags, [string[]]$extraEnvironment = @()) {
        $phaseOut = "/repo/artifacts/e2e-podman/$run/$phase"
        $envArgs = @(
            '-e', 'ATLAS_URL=http://localhost:8080', '-e', 'ATLAS_E2E_USER=browser-admin', '-e', "PLAYWRIGHT_REDUCED_MOTION=$ReducedMotion", '-e', "TZ=$timeZone",
            '-e', 'PLAYWRIGHT_HTML_OPEN=never', '-e', "PLAYWRIGHT_HTML_OUTPUT_DIR=$phaseOut/report",
            '-e', "PLAYWRIGHT_JSON_OUTPUT_NAME=$phaseOut/results.json",
            '-e', "ATLAS_E2E_GIT_REMOTE=$gitRemote", '-e', 'ATLAS_E2E_GIT_KEYS=/git-keys/authorized_keys',
            '-e', "ATLAS_E2E_GIT_EVIDENCE=$phaseOut/git-backup.json", '-v', "${gitKeys}:/git-keys",
            '--secret', "$run-bootstrap,type=env,target=ATLAS_E2E_BOOTSTRAP_TOKEN",
            '--secret', "$run-password,type=env,target=ATLAS_E2E_PASSWORD"
        )
        if ($needsFakeAi) { $envArgs += @('-e', "ATLAS_E2E_FAKE_AI_URL=http://fake-ai:$fakeAiPort/v1", '-e', "ATLAS_E2E_FAKE_AI_CONTROL=http://localhost:$fakeAiPort") }
        if ($needsSmb) {
            $envArgs += @(
                '-e', 'ATLAS_E2E_SMB_ROOT=/smb', '-e', 'ATLAS_E2E_SMB_DESTINATION=smb://localhost/exports',
                '-e', "ATLAS_E2E_STORAGE_RESTART_EVIDENCE=/repo/artifacts/e2e-podman/$run/storage-restart.json",
                '--secret', "$run-smb,type=env,target=ATLAS_E2E_SMB_PASSWORD", '-v', "${smbShare}:/smb:ro,nocopy"
            )
        }
        foreach ($variable in $extraEnvironment) { $envArgs += @('-e', $variable) }
        foreach ($flag in $flags) { $envArgs += @('-e', "$flag=1") }
        Write-Host "`n=== $phase : $($specs -join ' ')"
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        & podman run --rm --pod $run --name "$run-playwright" --init @envArgs `
            -v "${browserRepo}:/repo" -w /repo/tests/browser $playwrightImage `
            node node_modules/@playwright/test/cli.js test @specs --reporter=list,html,json "--output=$phaseOut/test-results" | Out-Host
        $code = $LASTEXITCODE
        Write-Host ("=== $phase finished in {0:hh\:mm\:ss} with exit code $code" -f $watch.Elapsed)
        return $code
    }

    # --- git backup: what Atlas pushed, read from the bare repository of the pod's git server ---------------------------
    function Invoke-BareGit {
        $output = & podman exec --user git "$run-git" git -C $gitBareRepository @args 2>&1
        [pscustomobject]@{ Code = $LASTEXITCODE; Lines = @($output | ForEach-Object { "$_" }) }
    }

    # $evidence is git-backup.json from git-backup.spec.ts: the marker in its page and the uploaded image.
    function Get-GitBackupProblems($evidence) {
        if ((Invoke-BareGit rev-parse --verify --quiet refs/heads/main).Code -ne 0) { return @('The bare repository has no branch main.') }
        $problems = @()
        $count = [int](Invoke-BareGit rev-list --count main).Lines[0]
        if ($count -lt 2) { $problems += "main has $count commit(s); expected at least 2." }
        if ((Invoke-BareGit cat-file -e main:.atlas/repository.json).Code -ne 0) { $problems += 'main has no .atlas/repository.json.' }
        $pages = @((Invoke-BareGit grep -l -F $evidence.marker main '--' '*.md').Lines | Where-Object { $_ -like 'main:*' } | ForEach-Object { $_.Substring(5) })
        if ($pages.Count -ne 1) { return $problems + "Expected one *.md containing '$($evidence.marker)' on main; found $($pages.Count): $($pages -join ', ')." }
        $markdown = $pages[0]
        $lines = (Invoke-BareGit show "main:$markdown").Lines
        $end = if ($lines.Count -gt 2) { [Array]::IndexOf($lines, '---', 1) } else { -1 }
        if ($lines[0] -ne '---' -or $end -lt 2 -or -not ($lines[1..($end - 1)] -match '^title: \S')) {
            $problems += "$markdown does not start with front matter containing title: (---, title: ..., ---)."
        }
        # Page commits are "docs: create|update <path without .md>", authored by whoever saved the revision.
        $subject = '^docs: (create|update) ' + [regex]::Escape(($markdown -replace '\.md$', '')) + '$'
        $email = if ($evidence.authorEmail) { $evidence.authorEmail } else { $gitAuthor.Email }
        $authored = (Invoke-BareGit log main '--format=%an%x1f%ae%x1f%s' '--' $markdown).Lines | Where-Object {
            $fields = $_ -split [char]0x1f
            $fields.Count -eq 3 -and $fields[0] -ceq $gitAuthor.Name -and $fields[1] -ceq $email -and $fields[2] -match $subject
        }
        if (-not $authored) { $problems += "No commit '$subject' of $markdown by $($gitAuthor.Name) <$email>." }
        # The commit the UI reported after Synchronizovat teď is on the remote branch.
        if ($evidence.lastCommit -and (Invoke-BareGit merge-base --is-ancestor $evidence.lastCommit main).Code -ne 0) {
            $problems += "The last commit shown after the sync ($($evidence.lastCommit)) is not on main."
        }
        $assets = $markdown -replace '\.md$', '.assets/'
        $tree = (Invoke-BareGit ls-tree -r main).Lines | ForEach-Object {
            $meta, $path = $_ -split "`t", 2
            [pscustomobject]@{ Oid = ($meta -split ' ')[2]; Path = $path }
        }
        $image = $tree | Where-Object { $_.Path -and $_.Path.StartsWith($assets) -and $_.Oid -eq $evidence.image.gitBlob } | Select-Object -First 1
        if (-not $image) {
            $present = ($tree | Where-Object { $_.Path -and $_.Path.StartsWith($assets) }).Path -join ', '
            $problems += "No blob $($evidence.image.gitBlob) (the uploaded $($evidence.image.name), $($evidence.image.size) B) under $assets; present: $(if ($present) { $present } else { 'nothing' })."
        } else {
            $size = [long](Invoke-BareGit cat-file -s $image.Oid).Lines[0]
            if ($size -ne $evidence.image.size) { $problems += "$($image.Path) has $size B; the upload had $($evidence.image.size) B." }
            # Relative links let a git host show the image (docs/git-backup.md, Obsah repozitáře).
            $link = ($assets.TrimEnd('/') -split '/')[-1] + '/' + ($image.Path -split '/')[-1]
            if (-not ($lines -match [regex]::Escape($link))) { $problems += "$markdown does not link the image as $link." }
        }
        $problems
    }

    function Test-GitBackupRepository([string]$phase) {
        $evidenceFile = Join-Path $outRoot "$phase/git-backup.json"
        if (-not (Test-Path $evidenceFile)) {
            Write-Host "GIT BACKUP CHECK FAILED: $evidenceFile is missing; git-backup.spec.ts did not get as far as the image upload." -ForegroundColor Red
            return $false
        }
        $evidence = Get-Content $evidenceFile -Raw | ConvertFrom-Json
        $encoding = [Console]::OutputEncoding
        # git prints the author in UTF-8; decoded with the Windows console default, "správce" would never match.
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        try {
            # The backup also runs in the background: give a push still under way time to land.
            $deadline = [DateTime]::UtcNow.AddSeconds(90)
            while ($true) {
                $problems = @(Get-GitBackupProblems $evidence)
                if ($problems.Count -eq 0 -or [DateTime]::UtcNow -gt $deadline) { break }
                Start-Sleep -Seconds 3
            }
            Write-Host "`n=== git log --stat -5 main ($gitRemote)"
            (Invoke-BareGit log --stat -5 main).Lines | Out-Host
        } finally { [Console]::OutputEncoding = $encoding }
        if ($problems.Count -gt 0) {
            $problems | ForEach-Object { Write-Host "GIT BACKUP CHECK FAILED: $_" -ForegroundColor Red }
            return $false
        }
        Write-Host 'Git backup check passed: branch main, .atlas/repository.json, the admin''s commit, Markdown with front matter and the uploaded image.'
        return $true
    }

    if ((Invoke-Phase 'install' @('first-run.spec.ts') @('ATLAS_E2E_ALLOW_SETUP')) -ne 0) { $failed = $true }
    # first-run.spec.ts installs the wiki in its first steps; later assertions may fail without
    # invalidating the installation. Only a wiki that is still uninstalled stops the run.
    $status = Invoke-RestMethod "http://127.0.0.1:$HostPort/api/setup/status" -TimeoutSec 10
    if ($status.requiresSetup) { throw 'First installation did not complete; later phases need the installed wiki.' }

    if (-not $InstallOnly) {
        $acceptanceFlags = @('ATLAS_E2E_ALLOW_COLLAB', 'ATLAS_E2E_ALLOW_UPLOADS', 'ATLAS_E2E_ALLOW_IMPORT')
        if ($Spec) {
            $batches = @(, $specsToRun)
        } else {
            $batches = $defaultBatches
        }
        for ($i = 0; $i -lt $batches.Count; $i++) {
            # Also before the first batch: first-run.spec.ts signs in several times.
            if ($ThrottlePauseSeconds -gt 0) { Write-Host "Login throttle pause: $ThrottlePauseSeconds s"; Start-Sleep -Seconds $ThrottlePauseSeconds }
            $phase = "batch-$($i + 1)"
            if ((Invoke-Phase $phase $batches[$i] $acceptanceFlags) -ne 0) { $failed = $true }
            if ($batches[$i] -contains 'storage-export.spec.ts') {
                # Preserve the original process logs before starting the timed restart preparation.
                Save-Logs -Suffix '-before-restart'
                if ((Invoke-Phase 'storage-restart-prepare' @('storage-export-restart.spec.ts') $acceptanceFlags @('ATLAS_E2E_STORAGE_RESTART_PHASE=prepare')) -ne 0) {
                    $failed = $true
                } else {
                    $evidence = Get-Content -LiteralPath (Join-Path $outRoot 'storage-restart.json') -Raw | ConvertFrom-Json
                    if ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - $evidence.queuedAt -ge 30000) {
                        throw 'Restart preparation exceeded the debounce; cannot prove a pending queue survived restart.'
                    }
                    Invoke-Podman restart "$run-app" | Out-Null
                    $ready = $false
                    foreach ($attempt in 1..120) {
                        try {
                            $status = Invoke-RestMethod "http://127.0.0.1:$HostPort/api/setup/status" -TimeoutSec 2
                            if ($status.databaseReady -and -not $status.requiresSetup) { $ready = $true; break }
                        } catch { }
                        if (-not (podman ps -q --filter "name=^$run-app$")) { break }
                        Start-Sleep -Milliseconds 500
                    }
                    if (-not $ready) { throw 'The application did not become ready after restart.' }
                    if ((Invoke-Phase 'storage-restart-verify' @('storage-export-restart.spec.ts') $acceptanceFlags @('ATLAS_E2E_STORAGE_RESTART_PHASE=verify')) -ne 0) { $failed = $true }
                }
            }
            if ($batches[$i] -match 'git-backup\.spec') { if (-not (Test-GitBackupRepository $phase)) { $failed = $true } }
        }
    }
} catch {
    $failed = $true
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    podman pod exists $run *> $null
    if ($LASTEXITCODE -eq 0) { Save-Logs }
    if ($Keep) {
        Write-Host "Pod left running: http://127.0.0.1:$HostPort  (git: podman exec --user git $run-git git -C $gitBareRepository log)"
        $volumes = if ($needsSmb) { "$gitKeys $smbShare $appData" } else { $gitKeys }
        Write-Host "  remove: podman pod rm -f $run; podman volume rm $volumes; podman secret rm $($created -join ' ')"
    } else {
        podman pod rm -f $run *> $null
        podman volume rm -f $gitKeys *> $null
        if ($needsSmb) { podman volume rm -f $smbShare $appData *> $null }
        foreach ($secret in $created) { podman secret rm $secret *> $null }
    }
    Write-Host ("Reports: $outRoot (total {0:hh\:mm\:ss}, {1})" -f $total.Elapsed, $(if ($failed) { 'FAILED' } else { 'passed' }))
}
exit ([int]$failed)
