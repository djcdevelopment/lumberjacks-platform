param(
  [int]$Port = 5433,
  [string]$Database = "game",
  [string]$User = "game",
  [string]$Password = "game",
  [string]$InitSqlPath = "C:\work\Lumberjacks\infra\docker\init.sql",
  [string]$ToolsRoot = "C:\work\tools",
  [string]$DataPath = "C:\work\fieldlab-postgres-data",
  [string]$LogPath = "C:\work\fieldlab-postgres.log",
  [switch]$ReloadSchema
)

$ErrorActionPreference = "Stop"

$version = "16.14-2"
$archiveName = "postgresql-$version-windows-x64-binaries.zip"
$downloadUrl = "https://get.enterprisedb.com/postgresql/$archiveName"
$installRoot = Join-Path $ToolsRoot "postgresql-$version"
$pgRoot = Join-Path $installRoot "pgsql"
$bin = Join-Path $pgRoot "bin"
$archivePath = Join-Path $ToolsRoot $archiveName

function Invoke-Checked {
  param([Parameter(Mandatory = $true)][scriptblock]$Command)

  & $Command
  if ($LASTEXITCODE -ne 0) {
    throw "Command failed with exit code $LASTEXITCODE."
  }
}

function Test-TcpPort {
  param(
    [Parameter(Mandatory = $true)]
    [string]$HostName,

    [Parameter(Mandatory = $true)]
    [int]$Port,

    [int]$TimeoutMs = 1000
  )

  $client = [System.Net.Sockets.TcpClient]::new()
  $asyncResult = $null
  try {
    $asyncResult = $client.BeginConnect($HostName, $Port, $null, $null)
    if (-not $asyncResult.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) {
      return $false
    }
    $client.EndConnect($asyncResult)
    return $client.Connected
  } catch {
    return $false
  } finally {
    if ($asyncResult) {
      $asyncResult.AsyncWaitHandle.Close()
    }
    $client.Close()
    $client.Dispose()
  }
}

function Write-TempSql {
  param(
    [Parameter(Mandatory = $true)]
    [string[]]$Lines
  )

  $path = Join-Path ([System.IO.Path]::GetTempPath()) "fieldlab-postgres-$([guid]::NewGuid()).sql"
  $Lines | Set-Content -Encoding UTF8 $path
  return $path
}

if (-not (Test-Path $InitSqlPath)) {
  throw "Init SQL not found: $InitSqlPath"
}

New-Item -ItemType Directory -Force $ToolsRoot | Out-Null

if (-not (Test-Path (Join-Path $bin "postgres.exe"))) {
  if (-not (Test-Path $archivePath)) {
    Write-Host "Downloading PostgreSQL portable binaries..."
    Invoke-Checked { & curl.exe -L $downloadUrl -o $archivePath }
  }

  Write-Host "Extracting PostgreSQL portable binaries..."
  New-Item -ItemType Directory -Force $installRoot | Out-Null
  Invoke-Checked { & tar.exe -xf $archivePath -C $installRoot }
}

if (-not (Test-Path (Join-Path $DataPath "PG_VERSION"))) {
  Write-Host "Initializing PostgreSQL data directory..."
  New-Item -ItemType Directory -Force $DataPath | Out-Null
  Invoke-Checked { & "$bin\initdb.exe" -D $DataPath -U postgres -A trust --encoding=UTF8 --locale=C }
}

Write-Host "Starting PostgreSQL on 127.0.0.1:$Port..."
& "$bin\pg_ctl.exe" -D $DataPath -m fast stop 2>$null | Out-Null
Invoke-Checked { & "$bin\pg_ctl.exe" -D $DataPath -o "`"-p`" `"$Port`" `"-h`" `"127.0.0.1`"" -l $LogPath start }

for ($i = 0; $i -lt 30; $i++) {
  if (Test-TcpPort -HostName "127.0.0.1" -Port $Port -TimeoutMs 1000) {
    break
  }
  Start-Sleep -Seconds 1
}

if (-not (Test-TcpPort -HostName "127.0.0.1" -Port $Port -TimeoutMs 1000)) {
  throw "PostgreSQL did not become reachable on 127.0.0.1:$Port. Inspect $LogPath."
}

$roleSql = @"
DO `$`$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '$User') THEN
    CREATE ROLE "$User" LOGIN PASSWORD '$Password';
  ELSE
    ALTER ROLE "$User" WITH LOGIN PASSWORD '$Password';
  END IF;
END
`$`$;
"@
$roleSqlPath = Write-TempSql -Lines @($roleSql)
try {
  Invoke-Checked { & "$bin\psql.exe" -h 127.0.0.1 -p $Port -U postgres -d postgres -v ON_ERROR_STOP=1 -f $roleSqlPath }
} finally {
  Remove-Item -LiteralPath $roleSqlPath -Force -ErrorAction SilentlyContinue
}

$databaseExists = (& "$bin\psql.exe" -h 127.0.0.1 -p $Port -U postgres -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$Database'").Trim()
if (-not $databaseExists) {
  Invoke-Checked { & "$bin\createdb.exe" -h 127.0.0.1 -p $Port -U postgres -O $User $Database }
}

$eventsTable = (& "$bin\psql.exe" -h 127.0.0.1 -p $Port -U postgres -d $Database -tAc "SELECT to_regclass('public.events')").Trim()
if ($ReloadSchema -or $eventsTable -notin @("events", "public.events")) {
  if ($ReloadSchema -and $databaseExists) {
    Invoke-Checked { & "$bin\dropdb.exe" -h 127.0.0.1 -p $Port -U postgres --if-exists $Database }
    Invoke-Checked { & "$bin\createdb.exe" -h 127.0.0.1 -p $Port -U postgres -O $User $Database }
  }

  Write-Host "Loading Lumberjacks schema from $InitSqlPath..."
  $schemaSqlPath = Join-Path ([System.IO.Path]::GetTempPath()) "fieldlab-lumberjacks-schema-$([guid]::NewGuid()).sql"
  Get-Content $InitSqlPath | Where-Object {
    $_ -notmatch '^\\(un)?restrict\b' -and $_ -notmatch '^SET transaction_timeout\b'
  } | Set-Content -Encoding UTF8 $schemaSqlPath

  try {
    Invoke-Checked { & "$bin\psql.exe" -h 127.0.0.1 -p $Port -U postgres -d $Database -v ON_ERROR_STOP=1 -f $schemaSqlPath }
  } finally {
    Remove-Item -LiteralPath $schemaSqlPath -Force -ErrorAction SilentlyContinue
  }
} else {
  Write-Host "Lumberjacks schema already appears to be loaded."
}

Invoke-Checked {
  & "$bin\psql.exe" -h 127.0.0.1 -p $Port -U postgres -d $Database -v ON_ERROR_STOP=1 -c "GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO `"$User`"; GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO `"$User`";"
}

Write-Host "Postgres is reachable from Windows at 127.0.0.1:$Port"
Write-Host "Connection string: Host=127.0.0.1;Port=$Port;Database=$Database;Username=$User;Password=$Password"
