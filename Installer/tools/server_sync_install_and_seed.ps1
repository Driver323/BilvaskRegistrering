param(
  [string]$CsvDir = 'C:\Bilvask\SCV',
  [string]$SyncDir = 'C:\Bilvask\sync',
  [string]$TaskName = 'Bilvask CSV->DB Sync',
  [string]$DbHost = 'localhost',
  [int]$DbPort = 5432,
  [string]$DbName = 'bilvask',
  [string]$DbUser = 'postgres',
  [string]$DbPass = ''
)

$ErrorActionPreference = 'Stop'

function Assert-Admin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  $p  = New-Object Security.Principal.WindowsPrincipal($id)
  if (-not $p.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    throw 'Uruchom jako Administrator.'
  }
}

function Write-PgPassFile([string]$Path, [string]$Host, [int]$Port, [string]$Db, [string]$User, [string]$Pass) {
  $line = "$Host`:$Port`:$Db`:$User`:$Pass"
  $dir = Split-Path -Parent $Path
  if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
  $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
  [System.IO.File]::WriteAllText($Path, $line + "`n", $utf8NoBom)
  try {
    icacls $Path /inheritance:r | Out-Null
    icacls $Path /grant 'SYSTEM:(R)' | Out-Null
    icacls $Path /grant 'BUILTIN\Administrators:(R)' | Out-Null
  } catch {}
}

Assert-Admin

$srcDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\ServerSync'
$srcDir = [System.IO.Path]::GetFullPath($srcDir)
if (!(Test-Path $srcDir)) { throw "Nie znaleziono folderu ServerSync: $srcDir" }
if (!(Test-Path $CsvDir)) { New-Item -ItemType Directory -Path $CsvDir -Force | Out-Null }
New-Item -ItemType Directory -Path $SyncDir -Force | Out-Null

Get-ChildItem -Path $srcDir -File | ForEach-Object {
  Copy-Item $_.FullName (Join-Path $SyncDir $_.Name) -Force
}

if ([string]::IsNullOrWhiteSpace($DbPass)) {
  throw 'Brak hasla postgres/DB do utworzenia pgpass i pierwszego sync.'
}

$pgpassPath = Join-Path $SyncDir 'pgpass.conf'
Write-PgPassFile -Path $pgpassPath -Host $DbHost -Port $DbPort -Db $DbName -User $DbUser -Pass $DbPass

$psExe = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$syncPs1 = Join-Path $SyncDir 'sync_all_csv_to_db.ps1'
$logPath = Join-Path $SyncDir 'autosync.log'
$seedLog = Join-Path $SyncDir 'seed_sync.log'

$cmd = "`\"$psExe`\" -NoProfile -ExecutionPolicy Bypass -Command `\"`$env:PGPASSFILE='${pgpassPath}'; `$env:PGCLIENTENCODING='UTF8'; try { chcp 65001 | Out-Null } catch {} ; & '${syncPs1}' -CsvDir '${CsvDir}' -DbHost '${DbHost}' -DbPort ${DbPort} -DbName '${DbName}' -DbUser '${DbUser}' >> '${logPath}' 2>&1`\""

schtasks /Query /TN "$TaskName" > $null 2>&1
if ($LASTEXITCODE -eq 0) {
  schtasks /Delete /TN "$TaskName" /F | Out-Null
}

schtasks /Create /F /SC MINUTE /MO 1 /TN "$TaskName" /RU 'SYSTEM' /RL HIGHEST /TR $cmd | Out-Null

$env:PGPASSFILE = $pgpassPath
$env:PGCLIENTENCODING = 'UTF8'
try { chcp 65001 | Out-Null } catch {}

& $syncPs1 -CsvDir $CsvDir -DbHost $DbHost -DbPort $DbPort -DbName $DbName -DbUser $DbUser *>> $seedLog
if ($LASTEXITCODE -ne 0) {
  throw "Pierwszy full sync nie powiodl sie. Sprawdz: $seedLog"
}

Write-Host "[Bilvask] ServerSync zainstalowany. Task: $TaskName"
Write-Host "[Bilvask] Seed log: $seedLog"
Write-Host "[Bilvask] AutoSync log: $logPath"
