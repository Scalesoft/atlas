#requires -Version 7
<#
.SYNOPSIS
    Atlas smoke check before a commit or handoff: server smoke tests, frontend typecheck and a vitest subset.

.DESCRIPTION
    Runs, in parallel:
      - server tests tagged [Trait("Category", "Smoke")] (needs ATLAS_TEST_CONNECTION, see README.md, "Testy");
      - vue-tsc over the whole frontend;
      - the vitest files listed in $VitestSmoke below.
    Prints one PASS/FAIL line per part and exits non-zero when any part failed; for a failed part it prints the last
    60 lines of its log (artifacts/smoke). The full suites (dotnet test, pnpm test) stay the check before merging to
    develop; browser tests stay in
    tests/browser/e2e-podman.ps1.

.EXAMPLE
    $env:ATLAS_TEST_CONNECTION = 'Host=127.0.0.1;Port=55450;Username=postgres;Password=atlas;Database=atlas_test'
    pwsh tests/smoke.ps1
#>
[CmdletBinding()]
param(
    # Skips the build when the Release test assembly is current (e.g. right after dotnet build).
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$web = Join-Path $root 'src/Atlas.Web'
$server = Join-Path $root 'tests/Atlas.Server.Tests'
$logs = Join-Path $root 'artifacts/smoke'

# Critical frontend paths: shell and routing, editor load and save, collaboration hydration, tree, page access, search, import fidelity.
$VitestSmoke = @(
    'src/App.test.ts'
    'src/components/DocumentEditor.test.ts'
    'src/components/DocumentEditor.collaboration.test.ts'
    'src/collaboration.test.ts'
    'src/savePage.test.ts'
    'src/editorBoundary.guard.test.ts'
    'src/components/PageTree.test.ts'
    'src/components/sharing/PageAccessPanel.test.ts'
    'src/components/SearchSuggestions.test.ts'
    'src/wikiImportGolden.test.ts'
)

if (-not $env:ATLAS_TEST_CONNECTION) {
    Write-Host 'FAIL  ATLAS_TEST_CONNECTION is not set; server smoke tests would be skipped. See README.md, "Testy".' -ForegroundColor Red
    exit 1
}
$missing = $VitestSmoke | Where-Object { -not (Test-Path (Join-Path $web $_)) }
if ($missing) {
    Write-Host "FAIL  vitest smoke list names missing files: $($missing -join ', ')" -ForegroundColor Red
    exit 1
}

$parts = [ordered]@{
    'server smoke' = {
        param($server, $noBuild)
        if (-not $noBuild) {
            dotnet build $server -c Release -m -v quiet -nologo
            if ($LASTEXITCODE) { "__EXIT__=$LASTEXITCODE"; return }
        }
        # A smoke test running for 2 minutes is hung: VSTest ends the run and names it (the full suite uses 5 minutes).
        dotnet test $server -c Release --no-build --filter 'Category=Smoke' --nologo --blame-hang-timeout 2m --blame-hang-dump-type none
        "__EXIT__=$LASTEXITCODE"
    }
    'vue-tsc' = {
        param($web)
        Set-Location $web
        node scripts/build.mjs --typecheck
        "__EXIT__=$LASTEXITCODE"
    }
    'vitest smoke' = {
        param($web, $files)
        Set-Location $web
        pnpm exec vitest run @files --experimental.fsModuleCache
        "__EXIT__=$LASTEXITCODE"
    }
}
$arguments = @{
    'server smoke' = @($server, [bool]$NoBuild)
    'vue-tsc'      = @($web)
    'vitest smoke' = @($web, $VitestSmoke)
}

. (Join-Path $PSScriptRoot 'TestParts.ps1')
$tree = Get-WorkingTree $root
$editor = Get-EditorState $root
$states = @{
    'server smoke' = Get-InputState $root $tree @('src/Atlas.Server', 'src/Atlas.YouTrack', 'src/Atlas.Markdown.Sync', 'src/Atlas.Yjs', 'src/Atlas.Markdown/schema', 'tests/Atlas.Server.Tests', 'Directory.Build.props', 'Directory.Build.targets', 'global.json', 'NuGet.Config')
    'vue-tsc'      = Get-InputState $root $tree @('src/Atlas.Web', 'src/Atlas.Markdown') $editor
    'vitest smoke' = Get-InputState $root $tree @('src/Atlas.Web', 'src/Atlas.Markdown') $editor
}
$passed = Invoke-TestParts -Parts $parts -Arguments $arguments -States $states -Logs $logs -Title 'Smoke' -BuildEditor
exit $(if ($passed) { 0 } else { 1 })
