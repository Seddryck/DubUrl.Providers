[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('tabular', 'multidim')]
    [string] $Mode
)

$ErrorActionPreference = 'Stop'
$instanceName = $Mode.ToLowerInvariant()
$serviceName = 'MSOLAP$' + $instanceName
$sampleRelease = 'https://github.com/microsoft/sql-server-samples/releases/download/adventureworks-analysis-services'
if ($instanceName -eq 'tabular') {
    $serverMode = 'TABULAR'
    $archiveName = 'adventure-works-tabular-model-1200-full-database-backup.zip'
    $backupPath = 'Adventure Works Internet Sales Tabular Sample Database/Adventure Works Internet Sales Database.abf'
} else {
    $serverMode = 'MULTIDIMENSIONAL'
    $archiveName = 'adventure-works-multidimensional-model-full-database-backup.zip'
    $backupPath = 'Enterprise/AdventureWorksDW2019Multidimensional-EE.abf'
}

function Save-Download([string] $Uri, [string] $Destination) {
    & curl.exe --fail --location --retry 3 --silent --show-error --output $Destination $Uri
    if ($LASTEXITCODE -ne 0) {
        throw "Download failed with exit code $($LASTEXITCODE): $Uri"
    }
}

$staging = New-Item -ItemType Directory -Path (Join-Path $env:RUNNER_TEMP "ssas-$instanceName")
$iso = Join-Path $staging.FullName 'SQLServer2025-x64-ENU-EntDev.iso'
$backupDirectory = Join-Path $staging.FullName 'Backup'
$archive = Join-Path $staging.FullName $archiveName

Write-Host 'Downloading SQL Server 2025 Enterprise Developer and AdventureWorks.'
Save-Download 'https://download.microsoft.com/download/dea8c210-c44a-4a9d-9d80-0c81578860c5/ENU/SQLServer2025-x64-ENU-EntDev.iso' $iso
Save-Download "$sampleRelease/$archiveName" $archive
Expand-Archive -LiteralPath $archive -DestinationPath $staging.FullName

Write-Host "Installing Analysis Services in $serverMode mode."
$volume = Mount-DiskImage -ImagePath $iso -PassThru | Get-Volume
try {
    $runnerAccount = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $install = Start-Process -FilePath "$($volume.DriveLetter):\setup.exe" -Wait -PassThru -ArgumentList @(
        '/Q', '/ACTION=Install', '/FEATURES=AS', "/ASSERVERMODE=$serverMode",
        "/INSTANCENAME=$instanceName", '/IACCEPTSQLSERVERLICENSETERMS', '/UpdateEnabled=False',
        "/ASSVCACCOUNT=""NT Service\$serviceName""", '/ASSVCSTARTUPTYPE=Automatic',
        "/ASSYSADMINACCOUNTS=""$runnerAccount""", "/ASBACKUPDIR=""$backupDirectory"""
    )
    if ($install.ExitCode -ne 0) {
        $summary = Join-Path $env:ProgramFiles 'Microsoft SQL Server\170\Setup Bootstrap\Log\Summary.txt'
        if (Test-Path -LiteralPath $summary) {
            Get-Content -LiteralPath $summary -Tail 100
        }
        throw "Analysis Services setup failed with exit code $($install.ExitCode)."
    }
} finally {
    Dismount-DiskImage -ImagePath $iso | Out-Null
}

Start-Service -Name $serviceName
Set-Service -Name SQLBrowser -StartupType Manual
Start-Service -Name SQLBrowser

Write-Host "Restoring AdventureWorks on localhost\$instanceName."
Copy-Item -LiteralPath (Join-Path $staging.FullName $backupPath) -Destination (Join-Path $backupDirectory 'AdventureWorks.abf')
Install-PSResource -Name SqlServer -Version 22.4.5.1 -Repository PSGallery -TrustRepository
Import-Module SqlServer -RequiredVersion 22.4.5.1
Restore-ASDatabase -Server "localhost\$instanceName" -Name AdventureWorks -RestoreFile 'AdventureWorks.abf' -Security SkipMembership -Confirm:$false