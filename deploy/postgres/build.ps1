<#
.SYNOPSIS
    Builds the Atlas PostgreSQL image (deploy/postgres/Dockerfile) with Podman as localhost/atlas-postgres:<tag> and writes the
    image name to the pipeline. tests/browser/e2e-podman.ps1 and tests/upgrade/upgrade-podman.ps1 use it, so the acceptance
    runs exactly the Dockerfile of the working tree; the registry image comes from .github/workflows/release.yml.

.DESCRIPTION
    The tag is derived from the Dockerfile's ARG defaults (<POSTGRES_VERSION>-pgvector<PGVECTOR_VERSION>[-<IMAGE_REVISION>]).
    The build context is a copy in the temp directory: the Podman machine cannot always read the checkout (a Dev Drive
    mounted as a folder is invisible to WSL). On an ARM64 host the linux/amd64 build runs the short C build emulated.
    Full acceptance (versions, CREATE EXTENSION, HNSW, same text ordering as the base image):
        $env:ENGINE = 'podman'; bash deploy/postgres/verify.sh <image> linux/amd64

.EXAMPLE
    pwsh deploy/postgres/build.ps1
#>
[CmdletBinding()]
param([string]$Platform = 'linux/amd64')

$ErrorActionPreference = 'Stop'
$dockerfile = Join-Path $PSScriptRoot 'Dockerfile'
$text = Get-Content $dockerfile -Raw
function Get-Arg([string]$name) {
    $match = [regex]::Match($text, "(?m)^ARG $name=(.*)$")
    if (-not $match.Success) { throw "ARG $name is missing in $dockerfile" }
    $match.Groups[1].Value.Trim()
}
$revision = Get-Arg 'IMAGE_REVISION'
$tag = "$(Get-Arg 'POSTGRES_VERSION')-pgvector$(Get-Arg 'PGVECTOR_VERSION')$(if ($revision) { "-$revision" })"
$image = "localhost/atlas-postgres:$tag"

$context = Join-Path ([System.IO.Path]::GetTempPath()) ('atlas-postgres-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $context | Out-Null
try {
    Copy-Item $dockerfile $context
    Write-Host "Building $image ($Platform) from $dockerfile"
    $log = & podman build --platform $Platform --build-arg DOCKERFILE_BLOB=working-tree -t $image $context 2>&1
    if ($LASTEXITCODE -ne 0) { $log | Select-Object -Last 30 | ForEach-Object { Write-Host "  $_" }; throw "podman build of $image failed." }
} finally {
    Remove-Item $context -Recurse -Force -ErrorAction SilentlyContinue
}
$actual = (& podman image inspect $image --format '{{.Os}}/{{.Architecture}}').Trim()
if ($actual -ne $Platform) { throw "$image is $actual, expected $Platform." }
$image
