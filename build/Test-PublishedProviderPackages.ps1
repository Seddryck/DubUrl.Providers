[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^0\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $SourceCommit,

    [Parameter(Mandatory)]
    [string] $NuGetDirectory
)

$ErrorActionPreference = 'Stop'
$suffix = ".$Version.nupkg"
$pending = @(Get-ChildItem -LiteralPath $NuGetDirectory -Filter '*.nupkg' -File | ForEach-Object {
    if (-not $_.Name.EndsWith($suffix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected package filename '$($_.Name)'."
    }
    $_.Name.Substring(0, $_.Name.Length - $suffix.Length).ToLowerInvariant()
})
if ($pending.Count -eq 0) { throw 'No packages to verify on NuGet.' }

$feedVersion = $Version.ToLowerInvariant()
for ($attempt = 1; $attempt -le 30; $attempt++) {
    $pending = @(foreach ($id in $pending) {
        $url = "https://api.nuget.org/v3-flatcontainer/$id/$feedVersion/$id.nuspec"
        $response = Invoke-WebRequest -Uri $url -TimeoutSec 15 -SkipHttpErrorCheck
        if ($response.StatusCode -in 404, 429 -or $response.StatusCode -ge 500) {
            $id
            continue
        }
        if ($response.StatusCode -ne 200) { throw "NuGet returned HTTP $($response.StatusCode) for $id $Version." }

        $metadata = ([xml]$response.Content).package.metadata
        if ($metadata.id -ne $id -or $metadata.version -ne $Version -or $metadata.repository.commit -ne $SourceCommit) {
            throw "Published package $id $Version does not match the tested source commit $SourceCommit."
        }
        Write-Host "Verified $id $Version from $SourceCommit."
    })
    if ($pending.Count -eq 0) { return }
    if ($attempt -lt 30) { Start-Sleep -Seconds 10 }
}
throw "NuGet has not indexed $Version for $($pending -join ', ')."
