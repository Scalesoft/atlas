#requires -Version 7
<#
.SYNOPSIS
    Atlas check at the end of a branch: every suite at once, then the browser tests.

.DESCRIPTION
    1. One Release build of both test projects.
    2. In parallel: the whole server suite, the YouTrack and Markdown sync tests, the editor (src/Atlas.Markdown: typecheck, vitest,
       contracts), the whole vitest suite, vue-tsc, the fake AI server tests and the mocked settings
       browser specs (settings-p3, settings-real, settings-security: the settings pages against a mocked API on a Vite
       dev server, no wiki; they need `npm --prefix tests/browser ci` and its Playwright Chromium).
    3. Unless -SkipE2e: tests/browser/e2e-podman.ps1 (it reuses the previous publish when its inputs did not change).
       Its acceptance batches include `ai` (ai-search.spec.ts against the fake OpenAI-compatible model server tests/browser/fake-ai,
       a container of that batch's pod; see tests/browser/README.md, "AI"). The fake server's own checks run in step 2 (part fake-ai: node --test;
       the C# side of the vector fixture is FakeAiVectorParityTests in the server suite).
    A part whose inputs already passed is CACHED and skipped (tests/TestParts.ps1), so a rerun after a fix repeats only
    the parts the fix touched.
    Every part runs even when an earlier one failed, so one run reports everything. While working, use
    tests/changed.ps1 instead.

.EXAMPLE
    $env:ATLAS_TEST_CONNECTION = 'Host=127.0.0.1;Port=55450;Username=postgres;Password=atlas;Database=atlas_test'
    pwsh tests/full.ps1
#>
[CmdletBinding()]
param(
    # Skip the Podman browser tests (for example before merging a branch that another run already covered in e2e).
    # The mocked settings specs still run: they are part of the suites.
    [switch]$SkipE2e,
    # Passed to e2e-podman.ps1.
    [ValidateSet('package', 'source')]
    [string]$EditorMode = 'package'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$web = Join-Path $root 'src/Atlas.Web'
$browser = Join-Path $root 'tests/browser'
$mockedSpecs = @('settings-p3.spec.ts', 'settings-real.spec.ts', 'settings-security.spec.ts', 'settings-ai-profiles.spec.ts', 'settings-places.spec.ts')
$serverTests = Join-Path $root 'tests/Atlas.Server.Tests'
$youtrackTests = Join-Path $root 'tests/Atlas.YouTrack.Tests'
$markdownTests = Join-Path $root 'tests/Atlas.Markdown.Sync.Tests'
$yjsTests = Join-Path $root 'tests/Atlas.Yjs.Tests'
$editorRoot = Join-Path $root 'src/Atlas.Markdown'
$total = [System.Diagnostics.Stopwatch]::StartNew()

if (-not $env:ATLAS_TEST_CONNECTION) {
    Write-Host 'FAIL  ATLAS_TEST_CONNECTION is not set; server tests would be skipped. See README.md, "Testy".' -ForegroundColor Red
    exit 1
}

. (Join-Path $PSScriptRoot 'TestParts.ps1')
$tree = Get-WorkingTree $root
$buildFiles = @('Directory.Build.props', 'Directory.Build.targets', 'global.json')
$editor = Get-EditorState $root
$states = @{
    'server'   = Get-InputState $root $tree (@('src/Atlas.Server', 'src/Atlas.YouTrack', 'src/Atlas.Markdown.Sync', 'src/Atlas.Yjs', 'src/Atlas.Markdown/schema', 'tests/Atlas.Server.Tests') + $buildFiles)
    'youtrack' = Get-InputState $root $tree (@('src/Atlas.YouTrack', 'tests/Atlas.YouTrack.Tests') + $buildFiles)
    'markdown sync' = Get-InputState $root $tree (@('src/Atlas.Markdown.Sync', 'src/Atlas.Yjs', 'src/Atlas.Markdown/schema', 'tests/Atlas.Markdown.Sync.Tests') + $buildFiles)
    'yjs'      = Get-InputState $root $tree (@('src/Atlas.Yjs', 'src/Atlas.Markdown.Sync', 'src/Atlas.Markdown/schema', 'src/Atlas.Server/Platform/Validation/host-markdown-schema.json', 'tests/Atlas.Yjs.Tests', 'tests/Atlas.Markdown.Sync.Tests/Fixtures') + $buildFiles)
    'editor'   = Get-InputState $root $tree @('src/Atlas.Markdown', 'tests/Atlas.Markdown.Sync.Tests/Fixtures', 'src/Atlas.Web/pnpm-lock.yaml')
    'vitest'   = Get-InputState $root $tree @('src/Atlas.Web', 'src/Atlas.Markdown') $editor
    'vue-tsc'  = Get-InputState $root $tree @('src/Atlas.Web', 'src/Atlas.Markdown') $editor
    'fake-ai'  = Get-InputState $root $tree @('tests/browser/fake-ai', 'tests/fixtures/fake-ai-vectors.json')
    'settings-mocked' = Get-InputState $root $tree (@('src/Atlas.Web', 'src/Atlas.Markdown', 'tests/browser/playwright.config.ts', 'tests/browser/package-lock.json') + @($mockedSpecs | ForEach-Object { "tests/browser/$_" })) $editor
}

$parts = [ordered]@{
    'vitest' = {
        param($web)
        Set-Location $web
        pnpm exec vitest run --experimental.fsModuleCache
        "__EXIT__=$LASTEXITCODE"
    }
    'vue-tsc' = {
        param($web)
        Set-Location $web
        node scripts/build.mjs --typecheck
        "__EXIT__=$LASTEXITCODE"
    }
    'editor' = {
        param($web, $editor)
        # Through the workspace root (src/Atlas.Web): pnpm run inside src/Atlas.Markdown would install it on its own.
        Set-Location $web
        pnpm --filter '@atlas/markdown' run typecheck
        if ($LASTEXITCODE) { "__EXIT__=$LASTEXITCODE"; return }
        pnpm --filter '@atlas/markdown' exec vitest run --configLoader runner --experimental.fsModuleCache
        if ($LASTEXITCODE) { "__EXIT__=$LASTEXITCODE"; return }
        Set-Location $editor
        node tests/contracts/architecture.mjs
        if ($LASTEXITCODE) { "__EXIT__=$LASTEXITCODE"; return }
        node --test tests/package/bundle-budget.test.mjs
        "__EXIT__=$LASTEXITCODE"
    }
    'fake-ai' = {
        param($browser)
        # The browser tests' fake model server: vectors against the shared fixture, endpoints, modes. Plain Node, no install needed.
        Set-Location $browser
        node --test fake-ai/server.test.mjs
        "__EXIT__=$LASTEXITCODE"
    }
    'settings-mocked' = {
        param($web, $browser, $specs)
        if (-not (Test-Path (Join-Path $browser 'node_modules/@playwright/test/cli.js'))) {
            'tests/browser has no Playwright: npm --prefix tests/browser ci; npx --prefix tests/browser playwright install chromium'
            '__EXIT__=1'; return
        }
        # Its own Vite dev server on a free port; the specs answer every /api call themselves.
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
        # A job has no console: without a stdin of its own, Vite neither starts listening nor prints anything.
        $in = New-TemporaryFile; $out = New-TemporaryFile; $err = New-TemporaryFile
        $vite = Start-Process node -ArgumentList 'node_modules/vite/bin/vite.js', '--port', $port, '--strictPort', '--host', '127.0.0.1' -WorkingDirectory $web -NoNewWindow -PassThru -RedirectStandardInput $in.FullName -RedirectStandardOutput $out.FullName -RedirectStandardError $err.FullName
        try {
            $url = "http://127.0.0.1:$port"
            $ready = $false
            for ($i = 0; $i -lt 60 -and -not $ready -and -not $vite.HasExited; $i++) {
                try { Invoke-WebRequest $url -TimeoutSec 2 | Out-Null; $ready = $true } catch { Start-Sleep -Seconds 1 }
            }
            if (-not $ready) { 'The Vite dev server did not start:'; Get-Content $out.FullName, $err.FullName; '__EXIT__=1'; return }
            $env:ATLAS_URL = $url
            Set-Location $browser
            node node_modules/@playwright/test/cli.js test @specs --fully-parallel --workers 4 --reporter=list
            "__EXIT__=$LASTEXITCODE"
        } finally {
            Stop-Process -Id $vite.Id -Force -ErrorAction SilentlyContinue
            Remove-Item $in.FullName, $out.FullName, $err.FullName -ErrorAction SilentlyContinue
        }
    }
}
$arguments = @{ 'vitest' = @($web); 'vue-tsc' = @($web); 'editor' = @($web, $editorRoot); 'fake-ai' = @($browser); 'settings-mocked' = @($web, $browser, $mockedSpecs) }

# The build is skipped too when both .NET parts already passed with these inputs.
$built = $true
$dotnetCached = (Test-CachedPass $root 'Suites-server' $states['server']) -and (Test-CachedPass $root 'Suites-youtrack' $states['youtrack']) -and (Test-CachedPass $root 'Suites-markdown sync' $states['markdown sync']) -and (Test-CachedPass $root 'Suites-yjs' $states['yjs'])
if (-not $dotnetCached) {
    Write-Host 'Building the test projects (Release)'
    dotnet build $serverTests -c Release -m -v quiet -nologo
    $built = -not $LASTEXITCODE
    if ($built) { dotnet build $youtrackTests -c Release -m -v quiet -nologo; $built = -not $LASTEXITCODE }
    if ($built) { dotnet build $markdownTests -c Release -m -v quiet -nologo; $built = -not $LASTEXITCODE }
    if ($built) { dotnet build $yjsTests -c Release -m -v quiet -nologo; $built = -not $LASTEXITCODE }
}
if ($built) {
    $parts['server'] = {
        param($project)
        dotnet test $project -c Release --no-build --nologo
        "__EXIT__=$LASTEXITCODE"
    }
    $parts['youtrack'] = $parts['server']
    $parts['markdown sync'] = $parts['server']
    $parts['yjs'] = $parts['server']
    $arguments['server'] = @($serverTests)
    $arguments['youtrack'] = @($youtrackTests)
    $arguments['markdown sync'] = @($markdownTests)
    $arguments['yjs'] = @($yjsTests)
} else {
    Write-Host 'FAIL  build; server, YouTrack, Markdown sync and Yjs tests skipped' -ForegroundColor Red
}

$passed = (Invoke-TestParts -Parts $parts -Arguments $arguments -States $states -Logs (Join-Path $root 'artifacts/full') -Title 'Suites' -BuildEditor) -and $built

if (-not $SkipE2e) {
    # The published server plus the specs: the same inputs as e2e-podman.ps1's publish reuse, and tests/browser.
    $e2eState = Get-InputState $root $tree (@('src', 'tests/browser', 'Dockerfile') + $buildFiles) (Get-EditorState $root $EditorMode)
    if (Test-CachedPass $root 'e2e' $e2eState) {
        Write-Host 'CACHED e2e  (inputs unchanged since it passed)' -ForegroundColor DarkGreen
    } else {
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'browser/e2e-podman.ps1') -EditorMode $EditorMode
        $e2ePassed = -not $LASTEXITCODE
        Set-CachedPass $root 'e2e' $e2eState $e2ePassed
        if (-not $e2ePassed) { $passed = $false }
    }
}
Write-Host ("Full check finished in {0:hh\:mm\:ss}: {1}" -f $total.Elapsed, $(if ($passed) { 'PASSED' } else { 'FAILED' }))
exit $(if ($passed) { 0 } else { 1 })
