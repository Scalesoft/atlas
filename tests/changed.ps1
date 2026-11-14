#requires -Version 7
<#
.SYNOPSIS
    Atlas check while working: only the tests the current change touches, in parallel.

.DESCRIPTION
    The change is everything that differs from the merge base with -Base (default origin/develop): commits, staged,
    unstaged and untracked files. From it:
      - server: one build, then the tests whose names match the touched feature folders (src/Atlas.Server/Features/<F>,
        tests/Atlas.Server.Tests/<F>) and test files. A change outside a feature folder (Platform, Persistence,
        Program.cs, project files) adds the Smoke category, not the whole suite;
      - YouTrack: its test project when src/Atlas.YouTrack or its tests changed;
      - Markdown sync (src/Atlas.Markdown.Sync, src/Atlas.Yjs): their test projects, plus the server's Smoke, Collaboration and Content tests;
      - frontend: vue-tsc, the vitest files that import a changed module (vitest --changed), the source scanners and the
        production build with its first-screen bundle budget;
      - editor (src/Atlas.Markdown): its typecheck, its vitest files that import a changed module, its architecture and
        bundle-size contracts, the frontend parts above and the Atlas tests that drive the editor;
      - server: the repository-wide guards (tests tagged Category=Guard) with every server change, the frontend ones
        (*.guard.test.ts) with every web change;
      - tooling (tooling, .github, Dockerfile, version.json, release notes): the node tests of the release and workflow rules that CI runs;
      - fixtures (tests/fixtures): the server, frontend and editor tests that read them.
    Untouched parts are skipped. The full suites and the browser tests run once, at the end: tests/full.ps1.

.EXAMPLE
    $env:ATLAS_TEST_CONNECTION = 'Host=127.0.0.1;Port=55450;Username=postgres;Password=atlas;Database=atlas_test'
    pwsh tests/changed.ps1
#>
[CmdletBinding()]
param(
    [string]$Base = 'origin/develop'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$web = Join-Path $root 'src/Atlas.Web'
$serverTests = Join-Path $root 'tests/Atlas.Server.Tests'
$youtrackTests = Join-Path $root 'tests/Atlas.YouTrack.Tests'
$markdownTests = Join-Path $root 'tests/Atlas.Markdown.Sync.Tests'
$yjsTests = Join-Path $root 'tests/Atlas.Yjs.Tests'
$editorPackage = '@atlas/markdown'
# Atlas tests that drive the editor itself: vitest --changed cannot see editor changes, Atlas imports its built dist.
function Get-WebTests([string]$Pattern) {
    @(Get-ChildItem (Join-Path $web 'src') -Recurse -Filter '*.test.ts' | Where-Object { Select-String -Path $_.FullName -Pattern $Pattern -Quiet } |
        ForEach-Object { [IO.Path]::GetRelativePath($web, $_.FullName).Replace('\', '/') })
}
$editorConsumers = Get-WebTests '@atlas/markdown'
# The tests that read tests/fixtures are not imports of it either.
$fixtureConsumers = Get-WebTests 'tests/fixtures|fixtures/'
# The node tests of the release and workflow rules: the list CI runs in the workflow.
$toolingTests = ((Select-String -Path (Join-Path $root '.github/workflows/ci.yml') -Pattern 'run: node --test (.+)$').Matches[0].Groups[1].Value -split ' ')

$mergeBase = git -C $root merge-base HEAD $Base
if ($LASTEXITCODE) { throw "No merge base with $Base; fetch it first (git fetch origin)." }
$files = @(git -C $root diff --name-only $mergeBase 2> $null) + @(git -C $root ls-files --others --exclude-standard) | Sort-Object -Unique

$names = [System.Collections.Generic.SortedSet[string]]::new()
$smoke = $false
$youtrack = $false
$markdownSync = $false
$frontend = $false
$editorChanged = $false
$tooling = $false
$fixtures = $false
foreach ($file in $files) {
    switch -Regex ($file) {
        '^(src/Atlas\.Markdown\.Sync|tests/Atlas\.Markdown\.Sync\.Tests|src/Atlas\.Yjs|tests/Atlas\.Yjs\.Tests)/' { $markdownSync = $true; break }
        # The library embeds the editor's schema manifest.
        '^src/Atlas\.Markdown/schema/' { $editorChanged = $true; $markdownSync = $true; break }
        '^src/Atlas\.Markdown/' { $editorChanged = $true; break }
        '^src/Atlas\.Server/Features/([^/]+)/' { [void]$names.Add($Matches[1]); break }
        '^tests/Atlas\.Server\.Tests/([^/]+)/' { [void]$names.Add($Matches[1]); break }
        '^tests/Atlas\.Server\.Tests/([^/]+)\.cs$' { [void]$names.Add($Matches[1]); break }
        '^(src/Atlas\.Server/|tests/Atlas\.Server\.Tests/|Directory\.Build\.|global\.json|NuGet\.Config)' { $smoke = $true; break }
        '^(src/Atlas\.YouTrack|tests/Atlas\.YouTrack\.Tests)/' { $youtrack = $true; break }
        '^(tooling/|\.github/|Dockerfile$|version\.json$)' { $tooling = $true; break }
        # The release notes are web content the release rules check too.
        '^src/Atlas\.Web/release-notes/' { $tooling = $true; $frontend = $true; break }
        '^tests/fixtures/' { $fixtures = $true; break }
        '^src/Atlas\.Web/' { $frontend = $true; break }
    }
}
if ($markdownSync) { $smoke = $true; [void]$names.Add('Collaboration'); [void]$names.Add('Content') }
if ($editorChanged) { $frontend = $true }
if ($fixtures) { $smoke = $true; 'Content', 'ContentImport', 'Collaboration', 'AiIndex', 'ExternalSharing' | ForEach-Object { [void]$names.Add($_) } }
# A folder name without its plural "s" also matches the flat test classes of the feature (Attachments → AttachmentHttpTests).
. (Join-Path $PSScriptRoot 'TestParts.ps1')
# Repository-wide guards (row-lock text, message namespaces, native SQL, migration tests, table coverage, the route inventory) read
# every server file or type, so a change anywhere on the server can break them although no folder of theirs changed; they run with
# every server change. A new guard needs only [Trait("Category", "Guard")] (GuardTraitConventionTests checks it).
$serverGuards = @('Category=Guard')
$filter = @($names | ForEach-Object { "FullyQualifiedName~$($_ -replace 's$', '')" }) + @(if ($smoke) { 'Category=Smoke' }) +
    @(if ($names.Count -or $smoke) { $serverGuards }) -join '|'

Write-Host ("Changed since {0} ({1}): {2} files" -f $Base, $mergeBase.Substring(0, 9), $files.Count)
Write-Host ("  server:   {0}" -f $(if ($filter) { $filter } else { '-' }))
Write-Host ("  youtrack: {0}" -f $(if ($youtrack) { 'all' } else { '-' }))
Write-Host ("  markdown sync: {0}" -f $(if ($markdownSync) { 'all' } else { '-' }))
Write-Host ("  frontend: {0}" -f $(if ($frontend) { 'vue-tsc + vitest --changed + guards + bundle budget' } else { '-' }))
Write-Host ("  tooling:  {0}" -f $(if ($tooling) { 'node --test of the release and workflow rules' } else { '-' }))
Write-Host ("  fixtures: {0}" -f $(if ($fixtures) { 'the frontend and editor tests that read them' } else { '-' }))
Write-Host ("  editor:   {0}" -f $(if ($editorChanged) { 'typecheck + vitest --changed + contracts + Atlas editor tests' } else { '-' }))

$parts = [ordered]@{}
$arguments = @{}
if ($filter -or $youtrack -or $markdownSync) {
    if (-not $env:ATLAS_TEST_CONNECTION) {
        Write-Host 'FAIL  ATLAS_TEST_CONNECTION is not set; server tests would be skipped. See README.md, "Testy".' -ForegroundColor Red
        exit 1
    }
    # One part, so the test projects never build Atlas.YouTrack or Atlas.Markdown.Sync at the same time.
    $parts['server'] = {
        param($serverTests, $filter, $youtrackTests, $markdownTests, $yjsTests)
        # Analyzers and code style run in tests/smoke.ps1 and tests/full.ps1 (switching back recompiles, so they are not
        # skipped there); without them an incremental build takes about half as long.
        $fastBuild = @('-p:RunAnalyzers=false', '-p:EnforceCodeStyleInBuild=false')
        if ($filter) {
            dotnet build $serverTests -c Release -m -v quiet -nologo @fastBuild
            if ($LASTEXITCODE) { "__EXIT__=$LASTEXITCODE"; return }
            dotnet test $serverTests -c Release --no-build --filter $filter --nologo --blame-hang-timeout 2m --blame-hang-dump-type none
            if ($LASTEXITCODE) { "__EXIT__=$LASTEXITCODE"; return }
        }
        if ($youtrackTests) {
            dotnet test $youtrackTests -c Release --nologo @fastBuild
            if ($LASTEXITCODE) { "__EXIT__=$LASTEXITCODE"; return }
        }
        if ($markdownTests) {
            dotnet test $markdownTests -c Release --nologo @fastBuild
            if ($LASTEXITCODE) { "__EXIT__=$LASTEXITCODE"; return }
            dotnet test $yjsTests -c Release --nologo @fastBuild
            if ($LASTEXITCODE) { "__EXIT__=$LASTEXITCODE"; return }
        }
        '__EXIT__=0'
    }
    $arguments['server'] = @($serverTests, $filter, $(if ($youtrack) { $youtrackTests } else { '' }), $(if ($markdownSync) { $markdownTests } else { '' }), $(if ($markdownSync) { $yjsTests } else { '' }))
}
if ($frontend) {
    $parts['vue-tsc'] = {
        param($web)
        Set-Location $web
        node scripts/build.mjs --typecheck
        "__EXIT__=$LASTEXITCODE"
    }
    $arguments['vue-tsc'] = @($web)
    $parts['vitest changed'] = {
        param($web, $mergeBase)
        Set-Location $web
        pnpm exec vitest run --changed $mergeBase --passWithNoTests --experimental.fsModuleCache
        "__EXIT__=$LASTEXITCODE"
    }
    $arguments['vitest changed'] = @($web, $mergeBase)
    # Source scanners (motion tokens, icon names, the AI glyph and editor boundaries, lazy components, root modules, ...) import
    # no changed module, so vitest --changed never picks them; every *.guard.test.ts runs with every web change.
    $parts['vitest guards'] = {
        param($web)
        Set-Location $web
        pnpm exec vitest run .guard.test.ts --experimental.fsModuleCache
        "__EXIT__=$LASTEXITCODE"
    }
    $arguments['vitest guards'] = @($web)
    # The production build fails when the first screen's bundle passes its budget or loads a lazy-only library
    # (scripts/atlasBundle.mjs), as it would on main.
    $parts['web bundle'] = {
        param($web)
        Set-Location $web
        pnpm exec vite build --logLevel warn
        "__EXIT__=$LASTEXITCODE"
    }
    $arguments['web bundle'] = @($web)
}
if ($editorChanged) {
    # Through the workspace root (src/Atlas.Web): pnpm run inside src/Atlas.Markdown would install it on its own.
    $parts['editor typecheck'] = {
        param($web, $package)
        Set-Location $web
        pnpm --filter $package run typecheck
        "__EXIT__=$LASTEXITCODE"
    }
    $arguments['editor typecheck'] = @($web, $editorPackage)
    $parts['editor vitest changed'] = {
        param($web, $package, $mergeBase)
        Set-Location $web
        pnpm --filter $package exec vitest run --configLoader runner --changed $mergeBase --passWithNoTests --experimental.fsModuleCache
        "__EXIT__=$LASTEXITCODE"
    }
    $arguments['editor vitest changed'] = @($web, $editorPackage, $mergeBase)
    # The bundle budget reads the dist that Invoke-TestParts -BuildEditor built.
    $parts['editor contracts'] = {
        param($editor)
        Set-Location $editor
        node tests/contracts/architecture.mjs
        if ($LASTEXITCODE) { "__EXIT__=$LASTEXITCODE"; return }
        node --test tests/package/bundle-budget.test.mjs
        "__EXIT__=$LASTEXITCODE"
    }
    $arguments['editor contracts'] = @((Join-Path $root 'src/Atlas.Markdown'))
    $parts['vitest editor consumers'] = {
        param($web, $files)
        Set-Location $web
        pnpm exec vitest run @files --experimental.fsModuleCache
        "__EXIT__=$LASTEXITCODE"
    }
    $arguments['vitest editor consumers'] = @($web, $editorConsumers)
}
if ($fixtures) {
    $parts['vitest fixtures'] = {
        param($web, $files)
        Set-Location $web
        pnpm exec vitest run @files --experimental.fsModuleCache
        "__EXIT__=$LASTEXITCODE"
    }
    $arguments['vitest fixtures'] = @($web, $fixtureConsumers)
    # The editor reads the content corpus for its Yjs round-trips; the editor's vitest --changed cannot see a fixture.
    $parts['editor corpus'] = {
        param($web, $package)
        Set-Location $web
        pnpm --filter $package exec vitest run --configLoader runner collab/yjsCorpusDocuments collab/yjsMaterializerCorpus collab/yjsMergeCorpus --experimental.fsModuleCache
        "__EXIT__=$LASTEXITCODE"
    }
    $arguments['editor corpus'] = @($web, $editorPackage)
}
if ($tooling) {
    $parts['tooling'] = {
        param($root, $files)
        Set-Location $root
        node --test @files
        "__EXIT__=$LASTEXITCODE"
    }
    $arguments['tooling'] = @($root, $toolingTests)
}
if (-not $parts.Count) {
    Write-Host 'Nothing to test.'
    exit 0
}

$tree = Get-WorkingTree $root
$buildFiles = @('Directory.Build.props', 'Directory.Build.targets', 'global.json')
$editor = Get-EditorState $root
$webInputs = @('src/Atlas.Web', 'src/Atlas.Markdown')
$editorInputs = @('src/Atlas.Markdown', 'tests/Atlas.Markdown.Sync.Tests/Fixtures', 'src/Atlas.Web/pnpm-lock.yaml')
$states = @{
    'server'         = Get-InputState $root $tree (@('src/Atlas.Server', 'src/Atlas.YouTrack', 'src/Atlas.Markdown.Sync', 'src/Atlas.Yjs', 'src/Atlas.Markdown/schema', 'tests/Atlas.Server.Tests', 'tests/Atlas.YouTrack.Tests', 'tests/Atlas.Markdown.Sync.Tests', 'tests/Atlas.Yjs.Tests', 'tests/fixtures') + $buildFiles) "$filter|$youtrack|$markdownSync"
    'vue-tsc'        = Get-InputState $root $tree $webInputs $editor
    'vitest changed' = Get-InputState $root $tree $webInputs "$editor|$mergeBase"
    'vitest guards'  = Get-InputState $root $tree $webInputs $editor
    'web bundle'     = Get-InputState $root $tree $webInputs $editor
    'editor typecheck'        = Get-InputState $root $tree $editorInputs
    'editor vitest changed'   = Get-InputState $root $tree $editorInputs $mergeBase
    'editor contracts'        = Get-InputState $root $tree $editorInputs
    'vitest editor consumers' = Get-InputState $root $tree $webInputs $editor
    'vitest fixtures'         = Get-InputState $root $tree ($webInputs + 'tests/fixtures') $editor
    'editor corpus'           = Get-InputState $root $tree ($editorInputs + 'tests/fixtures')
    'tooling'                 = Get-InputState $root $tree @('tooling', '.github', 'Dockerfile', 'tests/upgrade', 'version.json', 'src/Atlas.Web/release-notes')
}
$passed = Invoke-TestParts -Parts $parts -Arguments $arguments -States $states -Logs (Join-Path $root 'artifacts/changed') -Title 'Changed' -BuildEditor:($frontend -or $fixtures)
exit $(if ($passed) { 0 } else { 1 })
