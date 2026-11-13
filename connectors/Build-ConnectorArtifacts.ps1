[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $CoreVersion,

    [string] $OutputDirectory = 'artifacts/connectors'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory, $repositoryRoot)
$stagingRoot = Join-Path $outputRoot '.staging'
$project = Join-Path $repositoryRoot 'Providers/Build/ProviderBinding.Build.csproj'
$matrixPath = Join-Path $PSScriptRoot 'connector-matrix.json'
$catalogPath = Join-Path $outputRoot 'connector-catalog.json'
$noticePath = Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md'
$epoch = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem


function Invoke-Checked([string] $Command, [string[]] $Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command '$Command' failed with exit code $LASTEXITCODE." }
}

function Get-Sha256([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Write-Utf8Json([string] $Path, [object] $Value) {
    $json = $Value | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($Path, "$json`n", [Text.UTF8Encoding]::new($false))
}

if ((Test-Path -LiteralPath $outputRoot) -and @(Get-ChildItem -LiteralPath $outputRoot -Force).Count) {
    throw "Connector output directory must be empty: $outputRoot"
}
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

$verifier = Join-Path $PSScriptRoot 'ProviderPackVerifier/ProviderPackVerifier.csproj'
Invoke-Checked dotnet @('build', $verifier, '--configuration', 'Release', '--framework', 'net8.0',
    '--nologo', "-p:DubUrlVersion=$CoreVersion")
$verifierAssembly = Join-Path $PSScriptRoot 'ProviderPackVerifier/bin/Release/net8.0/ProviderPackVerifier.dll'
$inventory = & dotnet $verifierAssembly core-dependencies
if ($LASTEXITCODE -ne 0) { throw "Could not read the dependency inventory from DubUrl $CoreVersion." }
$excluded = @('DubUrl.Core.dll', 'ConnectorDependencies.dll', 'ConnectorDependencies.deps.json')
$excluded += @($inventory | ForEach-Object { $_.Replace('\', '/') })

$matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
if ($matrix.schemaVersion -ne 2) { throw 'Expected connector matrix schema version 2.' }
$technologies = @{}
foreach ($technology in $matrix.technologies) { $technologies[$technology.name] = $technology }
$ready = @($matrix.connectors | Where-Object status -eq 'ready' | Sort-Object id)
if (@($ready.id | Select-Object -Unique).Count -ne $ready.Count) {
    throw 'Connector IDs must be unique.'
}
$definitionsPath = Join-Path $stagingRoot 'connector-catalog.json'
Invoke-Checked dotnet @(
    'run', '--project', (Join-Path $PSScriptRoot 'Catalog/ConnectorCatalog.Build.csproj'),
    '--configuration', 'Release', '--framework', 'net8.0', "-p:DubUrlVersion=$CoreVersion",
    "-p:Version=$Version", '--', $matrixPath, $definitionsPath, $Version, $CoreVersion
)
$definitionDocument = Get-Content -LiteralPath $definitionsPath -Raw | ConvertFrom-Json -AsHashtable
$definitions = $definitionDocument.entries
$catalogEntries = [Collections.Generic.List[object]]::new()

foreach ($manifest in $definitions) {
    $selection = @($ready | Where-Object id -CEQ $manifest.provider)
    if ($selection.Count -ne 1) { throw "Missing canonical matrix selection: $($manifest.provider)" }
    $connector = $selection[0]
    if (-not $technologies.ContainsKey($connector.technology)) {
        throw "Connector '$($connector.id)' uses unknown technology '$($connector.technology)'."
    }
    $technology = $technologies[$connector.technology]
    $publishRoot = Join-Path $stagingRoot "$($connector.id)/publish"
    $contentRoot = Join-Path $stagingRoot "$($connector.id)/content"
    New-Item -ItemType Directory -Path $publishRoot, $contentRoot -Force | Out-Null

    Invoke-Checked dotnet @(
        'publish', $project,
        '--configuration', 'Release',
        '--framework', 'net8.0',
        '--no-self-contained',
        '--output', $publishRoot,
        '--nologo',
        '--disable-build-servers',
        '-m:1',
        "-p:Version=$Version",
        "-p:DubUrlVersion=$CoreVersion",
        '-p:ContinuousIntegrationBuild=true',
        "-p:ConnectorFamily=$($connector.family)",
        "-p:ConnectorTechnology=$($technology.Key)"
    )

    foreach ($file in Get-ChildItem -LiteralPath $publishRoot -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($publishRoot, $file.FullName).Replace('\', '/')
        if ($relative -in $excluded -or $file.Extension -in @('.pdb', '.xml')) { continue }
        $destination = Join-Path $contentRoot $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    Copy-Item -LiteralPath $noticePath -Destination (Join-Path $contentRoot 'THIRD-PARTY-NOTICES.md')
    $members = @($ready | Where-Object { $_.id -eq $manifest.provider -or $_.id -in $manifest.aliases })
    $prerequisites = @($members | Where-Object { $null -ne $_.prerequisite } | ForEach-Object prerequisite | Sort-Object -Unique)
    if ($prerequisites.Count) {
        [IO.File]::WriteAllText((Join-Path $contentRoot 'PREREQUISITES.md'),
            "# External prerequisites`n`nInstall the matching driver for each database you use.`n`n$($prerequisites -join "`n`n")`n", [Text.UTF8Encoding]::new($false))
    }

    if (-not (Test-Path -LiteralPath (Join-Path $contentRoot $manifest.assembly) -PathType Leaf)) {
        throw "Connector '$($connector.id)' is missing its provider implementation '$($manifest.assembly)'."
    }
    & (Join-Path $PSScriptRoot 'Write-ConnectorDependencies.ps1') `
        -InputPath (Join-Path $publishRoot 'ConnectorDependencies.deps.json') `
        -OutputPath (Join-Path $contentRoot ([IO.Path]::ChangeExtension($manifest.assembly, '.deps.json'))) `
        -ImplementationAssembly $manifest.assembly
    $manifestPath = Join-Path $stagingRoot "$($connector.id)/provider-pack.json"
    Write-Utf8Json $manifestPath $manifest

    $archiveName = "duburl-connector-$($connector.id)-$Version.zip".ToLowerInvariant()
    $archivePath = Join-Path $outputRoot $archiveName
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entries = @([PSCustomObject]@{ Source = $manifestPath; Destination = 'provider-pack.json' })
        $entries += Get-ChildItem -LiteralPath $contentRoot -Recurse -File | ForEach-Object {
            [PSCustomObject]@{
                Source = $_.FullName
                Destination = [IO.Path]::GetRelativePath($contentRoot, $_.FullName).Replace('\', '/')
            }
        }
        foreach ($item in $entries | Sort-Object Destination) {
            $entry = $archive.CreateEntry($item.Destination, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $epoch
            $source = [IO.File]::OpenRead($item.Source)
            $destination = $entry.Open()
            try { $source.CopyTo($destination) }
            finally { $destination.Dispose(); $source.Dispose() }
        }
    }
    finally { $archive.Dispose() }

    # Enrich the exact archive definition after its immutable archive hash is available.
    $catalogEntry = [ordered]@{}
    foreach ($key in $manifest.Keys) { $catalogEntry[$key] = $manifest[$key] }
    $catalogEntry.downloadUri = "https://github.com/Seddryck/DubUrl.Providers/releases/download/connectors-v$Version/$archiveName"
    $catalogEntry.sha256 = Get-Sha256 $archivePath
    $catalogEntries.Add($catalogEntry)
}

Write-Utf8Json $catalogPath ([ordered]@{ schemaVersion = $definitionDocument.schemaVersion; entries = @($catalogEntries) })
Remove-Item -LiteralPath $stagingRoot -Recurse -Force
Write-Host "Created $($catalogEntries.Count) connector packs and catalog '$catalogPath'."
