[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [ValidateLength(1, 128)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $ArtifactDirectory,

    [string] $Registry = 'ghcr.io'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A missing tag is an expected ORAS exit status.
$PSNativeCommandUseErrorActionPreference = $false

$root = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
& "$PSScriptRoot/../connectors/Test-ConnectorArchiveSet.ps1" -Version $Version -ArtifactDirectory $root
$catalog = Get-Content -LiteralPath (Join-Path $root 'connector-catalog.json') -Raw | ConvertFrom-Json
$artifactType = 'application/vnd.duburl.connector.v1'
$source = 'https://github.com/Seddryck/DubUrl.Providers'
$archives = foreach ($entry in $catalog.entries) {
    if ($entry.provider -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') {
        throw "Invalid canonical connector ID '$($entry.provider)'."
    }
    $name = [IO.Path]::GetFileName($entry.downloadUri)
    $sha256 = (Get-FileHash -LiteralPath (Join-Path $root $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sha256 -ne $entry.sha256) { throw "Connector archive '$name' has the wrong SHA-256 digest." }
    [pscustomobject]@{
        Name = $name
        Sha256 = $sha256
        Repository = "$Registry/seddryck/duburl-connectors/$($entry.provider)"
    }
}

$summary = @('### Connector OCI artifacts', '')
Push-Location -LiteralPath $root
try {
    foreach ($archive in $archives) {
        $tag = "$($archive.Repository):$Version"
        $digest = oras resolve $tag 2>&1
        if ($LASTEXITCODE -ne 0) {
            if (($digest -join "`n") -notmatch ': not found\s*$') {
                throw "Could not resolve '${tag}': $digest"
            }
            oras push --image-spec v1.1 --artifact-type $artifactType `
                --annotation "org.opencontainers.image.source=$source" $tag "$($archive.Name):application/zip"
            if ($LASTEXITCODE -ne 0) { throw "Could not publish '$tag'." }
            $digest = oras resolve $tag
            if ($LASTEXITCODE -ne 0) { throw "Could not resolve published tag '$tag'." }
        }
        if ([string]$digest -cnotmatch '^sha256:[0-9a-f]{64}$') { throw "Unexpected manifest digest for '$tag'." }

        $reference = "$($archive.Repository)@$digest"
        $manifestJson = oras manifest fetch $reference
        if ($LASTEXITCODE -ne 0) { throw "Could not fetch manifest '$reference'." }
        $manifest = $manifestJson | ConvertFrom-Json -AsHashtable
        if ($manifest.mediaType -cne 'application/vnd.oci.image.manifest.v1+json' -or
            $manifest.artifactType -cne $artifactType -or @($manifest.layers).Count -ne 1 -or
            $manifest.layers[0].mediaType -cne 'application/zip' -or
            $manifest.layers[0].digest -cne "sha256:$($archive.Sha256)" -or
            $manifest.layers[0].annotations.'org.opencontainers.image.title' -cne $archive.Name) {
            throw "Tag '$tag' points to a different connector artifact."
        }

        $archive | Add-Member -NotePropertyName Reference -NotePropertyValue $reference
        Write-Host "$tag -> $reference"
        $summary += '- `{0}`' -f $reference
    }
}
finally {
    Pop-Location
}

# The packaging job exercises installation. Check that the registry returns those same bytes.
$sample = $archives | Select-Object -First 1
$pullDirectory = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) "duburl-oci-$([guid]::NewGuid())")
try {
    oras pull $sample.Reference --output $pullDirectory.FullName
    if ($LASTEXITCODE -ne 0) { throw "Could not pull '$($sample.Reference)'." }
    $pulledZip = Join-Path $pullDirectory.FullName $sample.Name
    if ((Get-FileHash -LiteralPath $pulledZip -Algorithm SHA256).Hash -ne $sample.Sha256) {
        throw "The pulled ZIP '$($sample.Name)' does not match the release catalog."
    }
}
finally {
    $pullRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $pullDirectory.FullName.StartsWith($pullRoot, [StringComparison]::Ordinal)) {
        throw "Unexpected pull directory '$pullDirectory'."
    }
    Remove-Item -LiteralPath $pullDirectory.FullName -Recurse -Force
}

if ($env:GITHUB_STEP_SUMMARY) { $summary >> $env:GITHUB_STEP_SUMMARY }
