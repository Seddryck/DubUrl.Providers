[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $SourceCommit,

    [Parameter(Mandatory)]
    [string] $ArtifactDirectory
)

$ErrorActionPreference = 'Stop'

& "$PSScriptRoot/../connectors/Test-ConnectorArchiveSet.ps1" -Version $Version -ArtifactDirectory $ArtifactDirectory
$catalogPath = Join-Path $ArtifactDirectory 'connector-catalog.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
$assets = @{}
$assets['connector-catalog.json'] = (Get-Item -LiteralPath $catalogPath).FullName
foreach ($entry in $catalog.entries) {
    $name = [IO.Path]::GetFileName($entry.downloadUri)
    $path = (Get-Item -LiteralPath (Join-Path $ArtifactDirectory $name)).FullName
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "Connector archive '$name' has the wrong SHA-256 digest."
    }
    $assets[$name] = $path
}

$tag = "connectors-v$Version"
$tagRef = "refs/tags/$tag"
$refs = @(git ls-remote origin $tagRef "$tagRef^{}")
if ($LASTEXITCODE -ne 0) { throw "Could not inspect the release tag '$tag'." }
$peeled = @($refs | Where-Object { $_.EndsWith("`t$tagRef^{}") })
$commitRef = if ($peeled.Count) { $peeled[0] } elseif ($refs.Count) { $refs[0] }
if ($commitRef -and (($commitRef -split '\s+')[0] -cne $SourceCommit)) {
    throw "Release tag '$tag' belongs to a different commit."
}

$releaseOptions = if ($Version.Contains('-')) { @('--prerelease', '--latest=false') } else { @('--latest') }
$releaseJson = gh release view $tag --json isDraft,targetCommitish,assets 2>$null
if ($LASTEXITCODE -eq 0) {
    $release = $releaseJson | ConvertFrom-Json
    if (-not $refs.Count -and (-not $release.isDraft -or $release.targetCommitish -cne $SourceCommit)) {
        throw "Release '$tag' does not identify the tested commit."
    }
    foreach ($remote in $release.assets) {
        if (-not $assets.ContainsKey($remote.name)) {
            throw "Release '$tag' already contains unexpected asset '$($remote.name)'."
        }
        $digest = (Get-FileHash -LiteralPath $assets[$remote.name] -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($remote.digest -cne "sha256:$digest") {
            throw "Release '$tag' already contains different bytes for '$($remote.name)'."
        }
        $assets.Remove($remote.name)
    }
    if (-not $release.isDraft) {
        if ($assets.Count) { throw "Published release '$tag' is missing connector assets." }
        Write-Host "Reusing published connector release '$tag'."
        return
    }
    if ($assets.Count) {
        gh release upload $tag @($assets.Values)
        if ($LASTEXITCODE -ne 0) { throw "Could not finish uploading '$tag'." }
    }
    gh release edit $tag --draft=false @releaseOptions
} else {
    gh release create $tag @($assets.Values) --target $SourceCommit --title "DubUrl connectors $Version" `
        --notes "Connector packs and the installation catalog for DubUrl.Providers $Version." @releaseOptions
}
if ($LASTEXITCODE -ne 0) { throw "Could not publish connector release '$tag'." }
