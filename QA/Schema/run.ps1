[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('PostgreSql','Sqlite','DuckDb','SqlServer','Oracle','SingleStore','MySql')][string]$Database,
    [ValidateSet('net10.0')][string]$Framework = 'net10.0',
    [string]$DubUrlVersion = '0.67.0',
    [string]$Version = '0.0.0',
    [string]$ResultsDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
$project = Join-Path $PSScriptRoot "DubUrl.Schema.$Database.QA/DubUrl.Schema.$Database.QA.csproj"
$results = if ($ResultsDirectory) { [IO.Path]::GetFullPath($ResultsDirectory, $root) } else { Join-Path $root "artifacts/results/$Database/$Framework" }
if (Test-Path $results) { throw 'Choose a clean results directory before running QA again.' }
$compose = Join-Path $PSScriptRoot "infrastructure/$($Database.ToLowerInvariant())/compose.yaml"
$container = Test-Path $compose
$composeProject = 'schema-' + $Database.ToLowerInvariant() + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$password = [Guid]::NewGuid().ToString('N') + 'aA!'
$port = switch ($Database) { PostgreSql {5432}; SqlServer {1433}; Oracle {1521}; SingleStore {3306}; MySql {3306} }
if ($container) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
}
$env:POSTGRES_PASSWORD = $password
$env:MSSQL_SA_PASSWORD = $password
$env:ORACLE_PWD = $password
$env:ROOT_PASSWORD = $password
$env:MYSQL_ROOT_PASSWORD = $password
$env:DUBURL_POSTGRESQL_QA_PORT = "$port"
$env:DUBURL_SQLSERVER_QA_PORT = "$port"
$env:DUBURL_ORACLE_QA_PORT = "$port"
$env:DUBURL_SINGLESTORE_QA_PORT = "$port"
$env:DUBURL_MYSQL_QA_PORT = "$port"
$env:DUBURL_SCHEMA_MYSQL = "Server=127.0.0.1;Port=$port;Database=DubUrl;User ID=root;Password=$password"
$env:DUBURL_POSTGRESQL_QA_CONNECTION_STRING = "Host=127.0.0.1;Port=$port;Database=DubUrl;Username=postgres;Password=$password"
$env:DUBURL_POSTGRESQL_DISCOVERY_QA_CONNECTION_STRING = $env:DUBURL_POSTGRESQL_QA_CONNECTION_STRING
$env:DUBURL_SQLSERVER_DISCOVERY_QA_CONNECTION_STRING = "Server=127.0.0.1,$port;Database=master;User ID=sa;Password=$password;TrustServerCertificate=true"
$env:DUBURL_ORACLE_QA_CONNECTION_STRING = "Data Source=127.0.0.1:$port/FREEPDB1;User Id=system;Password=$password"
$env:DUBURL_SINGLESTORE_QA_CONNECTION_STRING = "Server=127.0.0.1;Port=$port;Database=DubUrl;User ID=root;Password=$password"
Push-Location $root
try {
    dotnet restore $project -p:TargetFramework=$Framework -p:DubUrlVersion=$DubUrlVersion -p:Version=$Version --nologo
    if ($LASTEXITCODE) { throw 'QA restore failed.' }
    dotnet build $project -c Release -f $Framework --no-restore -p:DubUrlVersion=$DubUrlVersion -p:Version=$Version --nologo
    if ($LASTEXITCODE) { throw 'QA build failed.' }
    if ($container) {
        docker compose -f $compose -p $composeProject up -d --wait --wait-timeout 900
        if ($LASTEXITCODE) { throw 'QA database did not become ready.' }
        if ($Database -eq 'SingleStore') {
            docker compose -f $compose -p $composeProject exec -T singlestore sh -c 'MYSQL_PWD="$ROOT_PASSWORD" singlestore -h 127.0.0.1 -u root -e "CREATE DATABASE IF NOT EXISTS DubUrl PARTITIONS 2"'
            if ($LASTEXITCODE) { throw 'Creating the SingleStore QA database failed.' }
        }
    }
    $fixtureResults = Join-Path $results 'fixtures'
    dotnet test $project -c Release -f $Framework --no-build --no-restore --logger trx --results-directory $fixtureResults --nologo
    if ($LASTEXITCODE) { throw 'Schema database tests failed.' }
    python QA/Schema/assert_results.py $fixtureResults --require-schema-baseline --require-shared-scenarios
    if ($LASTEXITCODE) { throw 'Required schema database results were missing or skipped.' }
}
finally {
    if ($container) {
        New-Item -ItemType Directory -Force $results | Out-Null
        docker compose -f $compose -p $composeProject logs --no-color 2>&1 |
            ForEach-Object { "$_".Replace($password, '[REDACTED]') } |
            Set-Content -LiteralPath (Join-Path $results 'database.log')
        docker compose -f $compose -p $composeProject down --volumes --remove-orphans
    }
    Pop-Location
}
