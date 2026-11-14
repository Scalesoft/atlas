#requires -Version 7
# Shared by tests/smoke.ps1, tests/changed.ps1, tests/full.ps1 and tests/browser/e2e-podman.ps1.
#
# Result cache: a part that passed remembers the state of its inputs (artifacts/test-cache). While those inputs stay the
# same, the part is reported as CACHED instead of running again, so a rerun after a fix repeats only what the fix touched.

# The git tree of the whole working tree (committed, staged, unstaged and untracked files, .gitignore applied), written
# through a throwaway copy of the index: the real index stays untouched and its stat cache makes this fast.
function Get-WorkingTree([string]$Dir) {
    $index = New-TemporaryFile
    try {
        Copy-Item (git -C $Dir rev-parse --path-format=absolute --git-path index) $index.FullName -Force
        $env:GIT_INDEX_FILE = $index.FullName
        git -C $Dir add -A -- . 2> $null
        $tree = git -C $Dir write-tree
        if ($LASTEXITCODE -ne 0) { throw "git write-tree failed in $Dir" }
        $tree
    } finally {
        Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
        Remove-Item $index.FullName -Force -ErrorAction SilentlyContinue
    }
}

# A short hash of the objects under $Paths in $Tree (from Get-WorkingTree) plus any extra text (options, modes).
function Get-InputState([string]$Dir, [string]$Tree, [string[]]$Paths, [string]$Extra = '') {
    $listing = (git -C $Dir ls-tree $Tree -- @Paths) -join "`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes("$listing`n$Extra")
    [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 16).ToLowerInvariant()
}

# How the frontend takes the editor: its built dist or, in source mode, the sources (src/Atlas.Markdown is part of the callers'
# input paths; another checkout's editor package, ATLAS_EDITOR_SOURCE, adds the state of its tree).
function Get-EditorState([string]$Root, [string]$Mode = $env:ATLAS_EDITOR_MODE, [string]$Source = $env:ATLAS_EDITOR_SOURCE) {
    if ($Mode -ne 'source') { return 'editor=package' }
    if (-not $Source) { return 'editor=source' }
    $dir = $Source
    'editor=source:' + (Get-InputState $dir (Get-WorkingTree $dir) @('.'))
}

function Get-CachePath([string]$Root, [string]$Key) {
    Join-Path $Root "artifacts/test-cache/$($Key -replace '[^\w.-]', '-').passed"
}

function Test-CachedPass([string]$Root, [string]$Key, [string]$State) {
    $file = Get-CachePath $Root $Key
    (Test-Path $file) -and (Get-Content $file -Raw).Trim() -eq $State
}

function Set-CachedPass([string]$Root, [string]$Key, [string]$State, [bool]$Passed) {
    $file = Get-CachePath $Root $Key
    if ($Passed) {
        New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
        Set-Content $file $State
    } else {
        Remove-Item $file -ErrorAction SilentlyContinue
    }
}

# Runs named parts in parallel jobs, writes each part's output to <Logs>/<name>.log and prints one PASS/FAIL/CACHED line
# per part (with the log tail of a failed one). Each script block must end by emitting "__EXIT__=<native exit code>";
# Receive-Job does not surface it otherwise. -States maps a part to its input state (Get-InputState): a part whose state
# already passed is skipped. Returns $true when every part passed.
function Invoke-TestParts {
    param(
        [Parameter(Mandatory)] [System.Collections.Specialized.OrderedDictionary]$Parts,
        [Parameter(Mandatory)] [hashtable]$Arguments,
        [Parameter(Mandatory)] [string]$Logs,
        [Parameter(Mandatory)] [string]$Title,
        [hashtable]$States = @{},
        [string]$Root = (Split-Path -Parent $PSScriptRoot),
        # The parts import the editor's dist: build it (when its sources changed) before they start.
        [switch]$BuildEditor
    )
    New-Item -ItemType Directory -Force $Logs | Out-Null
    $started = Get-Date
    $cached = @()
    # pnpm installs on its own before `pnpm exec` when package.json changed since the last install (a release version
    # bump does that). Inside a part it relinked node_modules under another part's Vite dev server, and every mocked
    # browser spec then failed to resolve its imports. So the parts start from an installed tree; a no-op otherwise.
    $web = Join-Path $Root 'src/Atlas.Web'
    $running = @($Parts.Keys | Where-Object { -not ($States[$_] -and (Test-CachedPass $Root "$Title-$_" $States[$_])) })
    if ($running.Count -and (Test-Path (Join-Path $web 'node_modules'))) {
        Push-Location $web
        try {
            pnpm install --frozen-lockfile --prefer-offline *> $null
            if ($LASTEXITCODE) { Write-Host "pnpm install --frozen-lockfile failed ($LASTEXITCODE); the parts run on the current node_modules." -ForegroundColor Yellow }
        } finally { Pop-Location }
    }
    if ($running.Count -and $BuildEditor) {
        $output = node (Join-Path $web 'scripts/build-editor.mjs') 2>&1
        if ($LASTEXITCODE) {
            Write-Host 'FAIL  editor build (pnpm run build:editor); no part started' -ForegroundColor Red
            $output | Select-Object -Last 60 | ForEach-Object { Write-Host "      $_" }
            return $false
        }
    }
    $jobs = foreach ($name in $Parts.Keys) {
        $state = $States[$name]
        if ($state -and (Test-CachedPass $Root "$Title-$name" $state)) { $cached += $name; continue }
        $log = Join-Path $Logs (($name -replace ' ', '-') + '.log')
        $job = Start-Job -Name $name -ScriptBlock $Parts[$name] -ArgumentList $Arguments[$name]
        [pscustomobject]@{ Name = $name; Job = $job; Log = $log; State = $state }
    }
    foreach ($entry in $jobs) {
        $output = @(Receive-Job $entry.Job -Wait -AutoRemoveJob -ErrorAction Continue 2>&1)
        $output | Out-File $entry.Log -Encoding utf8
        $marker = $output | Where-Object { "$_" -match '^__EXIT__=(\d+)$' } | Select-Object -Last 1
        $entry | Add-Member Ok ($marker -and "$marker" -eq '__EXIT__=0')
        $entry | Add-Member Elapsed ($entry.Job.PSEndTime - $entry.Job.PSBeginTime)
        if ($entry.State) { Set-CachedPass $Root "$Title-$($entry.Name)" $entry.State $entry.Ok }
    }
    Write-Host ''
    foreach ($name in $cached) { Write-Host ("CACHED {0}  (inputs unchanged since it passed)" -f $name) -ForegroundColor DarkGreen }
    $failed = $false
    foreach ($entry in $jobs) {
        if ($entry.Ok) {
            Write-Host ("PASS  {0}  ({1:mm\:ss})" -f $entry.Name, $entry.Elapsed) -ForegroundColor Green
        } else {
            $failed = $true
            Write-Host ("FAIL  {0}  ({1:mm\:ss}, log: {2})" -f $entry.Name, $entry.Elapsed, $entry.Log) -ForegroundColor Red
            Get-Content $entry.Log | Select-Object -Last 60 | ForEach-Object { Write-Host "      $_" }
        }
    }
    Write-Host ("{0} finished in {1:mm\:ss}: {2}" -f $Title, ((Get-Date) - $started), $(if ($failed) { 'FAILED' } else { 'PASSED' }))
    return -not $failed
}
