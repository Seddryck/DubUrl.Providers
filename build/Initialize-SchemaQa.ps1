[CmdletBinding()]
param(
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $SourceCommit = '7880413050c218150d5ec77f0636c9bfcf2b5ded',
    [string] $DubUrlVersion = '0.60.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$source = Join-Path $root 'artifacts/schema-source'
$feed = Join-Path $root 'artifacts/schema-feed'
$revision = Join-Path $feed 'schema-revision.txt'
$version = '0.101.3-extraction'

if ((Test-Path "$feed/DubUrl.Schema.$version.nupkg") -and
    (Test-Path "$feed/DubUrl.Schema.Documents.$version.nupkg") -and
    (Test-Path $revision) -and (Get-Content $revision) -eq "$SourceCommit/$DubUrlVersion") { return }

New-Item -ItemType Directory -Force $source | Out-Null
git -C $source init --quiet
if ($LASTEXITCODE) { throw 'Could not initialize the Schema source checkout.' }
git -C $source fetch --depth 1 https://github.com/Seddryck/DubUrl.Schema.git $SourceCommit
if ($LASTEXITCODE) { throw 'Could not fetch the pinned Schema commit.' }
git -C $source checkout --quiet --detach FETCH_HEAD
if ($LASTEXITCODE) { throw 'Could not check out the pinned Schema commit.' }

foreach ($name in 'DubUrl.Schema', 'DubUrl.Schema.Documents') {
    dotnet pack (Join-Path $source $name) -c Release "-p:Version=$version" "-p:DubUrlVersion=$DubUrlVersion" -p:PackageIcon= -o $feed --nologo
    if ($LASTEXITCODE) { throw "Could not pack $name for live QA." }
}
Set-Content -LiteralPath $revision -Value "$SourceCommit/$DubUrlVersion"
