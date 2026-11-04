[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^0\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [string] $OutputDirectory = 'artifacts/nuget'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory, $root)
$projects = @(Get-ChildItem "$root/Providers" -Recurse -Filter 'DubUrl.Provider.*.csproj' |
    Where-Object BaseName -notmatch '\.(QA|Testing|BulkCopy)$')
$projects += Get-ChildItem "$root/DubUrl.Authentication.SqlServer", "$root/DubUrl.Authentication.Snowflake" -Filter '*.csproj'

foreach ($project in $projects) {
    dotnet pack $project.FullName -c Release -o $output "-p:Version=$Version" -p:IncludeSymbols=true -p:ContinuousIntegrationBuild=true --nologo
    if ($LASTEXITCODE) { throw "Packing $($project.BaseName) failed." }
}

& "$PSScriptRoot/Test-ProviderPackages.ps1" -Version $Version -NuGetDirectory $output
