$ErrorActionPreference = 'Stop'

$serviceIndex = Invoke-RestMethod 'https://api.nuget.org/v3/index.json'
$search = $serviceIndex.resources | Where-Object { $_.'@type' -eq 'SearchQueryService/3.5.0' } |
    Select-Object -First 1
if (-not $search) { throw 'NuGet.org did not advertise its package search service.' }

$result = Invoke-RestMethod "$($search.'@id')?q=packageid%3ADubUrl&prerelease=false"
$package = @($result.data | Where-Object id -eq 'DubUrl')
if ($package.Count -ne 1 -or $package[0].version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Could not resolve the latest listed stable DubUrl package from NuGet.org.'
}
$package[0].version
