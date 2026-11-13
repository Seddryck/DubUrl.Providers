[CmdletBinding()]
param(
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $SourceCommit,
    [string] $DubUrlVersion = '0.65.0'
)

$ErrorActionPreference = 'Stop'
if (-not $SourceCommit) {
    Write-Host 'Using published Schema packages.'
    return
}

$root = Split-Path $PSScriptRoot
$source = Join-Path $root 'artifacts/schema-source'
$feed = Join-Path $root 'artifacts/schema-feed'
$inputs = [Text.Encoding]::UTF8.GetBytes("$SourceCommit/$DubUrlVersion")
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputs)).ToLowerInvariant()
$version = "0.0.0-schema-qa.build$($hash.Substring(0, 24))"

if (-not (Test-Path "$feed/DubUrl.Schema.$version.nupkg") -or
    -not (Test-Path "$feed/DubUrl.Schema.Documents.$version.nupkg")) {
    New-Item -ItemType Directory -Force $source | Out-Null
    git -C $source init --quiet
    if ($LASTEXITCODE) { throw 'Could not initialize the Schema source checkout.' }
    git -C $source fetch --depth 1 https://github.com/Seddryck/DubUrl.Schema.git $SourceCommit
    if ($LASTEXITCODE) { throw 'Could not fetch the requested Schema commit.' }
    git -C $source checkout --quiet --detach FETCH_HEAD
    if ($LASTEXITCODE) { throw 'Could not check out the requested Schema commit.' }

    foreach ($name in 'DubUrl.Schema', 'DubUrl.Schema.Documents') {
        dotnet pack (Join-Path $source $name) -c Release "-p:Version=$version" "-p:DubUrlVersion=$DubUrlVersion" -p:PackageIcon= -o $feed --nologo
        if ($LASTEXITCODE) { throw "Could not pack $name for live QA." }
    }
}

$env:SchemaPackageVersion = $version
if ($env:GITHUB_ENV) {
    "SchemaPackageVersion=$version" >> $env:GITHUB_ENV
}
Write-Host "Using Schema QA candidate $version for $SourceCommit."
