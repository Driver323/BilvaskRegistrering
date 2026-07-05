param(
  [string]$PgHost = "127.0.0.1",
  [int]$PgPort = 5432,
  [string]$PgSuperUser = "postgres",
  [string]$PgSuperPass = "",
  [string]$DbName = "bilvask",
  [string]$AdminUser = "bilvask_admin_app",
  [string]$WorkerUser = "bilvask_worker_app",
  [string]$AdminPass = "",
  [string]$WorkerPass = "",
  [string]$UiAdminPassword = "admin",
  [string]$UiWorkerPassword = "worker",
  [string]$ActivationCode = ""
)

$ErrorActionPreference = 'Stop'

# --- Activation code verification (RSA-signed, offline) ---
if ([string]::IsNullOrWhiteSpace($ActivationCode)) {
  throw "Brak kodu instalacyjnego (ActivationCode)."
}

$verifyScript = Join-Path $PSScriptRoot "license_verify.ps1"
if (!(Test-Path $verifyScript)) { throw "Brak pliku license_verify.ps1" }

# run verifier; it returns exit code 0 if OK
& powershell.exe -ExecutionPolicy Bypass -File $verifyScript -ActivationCode $ActivationCode
if ($LASTEXITCODE -ne 0) {
  throw "Nieprawidłowy kod instalacyjny."
}


function New-RandomPass([int]$len=18) {
  $chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#%*_-" 
  -join (1..$len | ForEach-Object { $chars[(Get-Random -Minimum 0 -Maximum $chars.Length)] })
}

function Get-DeterministicPass([string]$code, [string]$salt, [int]$len = 32) {
  # Stable password derived from the activation code.
  # Using hex avoids quoting issues in SQL.
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $bytes = [System.Text.Encoding]::UTF8.GetBytes("$code|$salt")
  $hash = $sha.ComputeHash($bytes)
  $hex = ($hash | ForEach-Object { $_.ToString('x2') }) -join ''
  if ($len -lt 8) { $len = 8 }
  if ($len -gt $hex.Length) { $len = $hex.Length }
  return $hex.Substring(0, $len)
}

# If passwords are not explicitly provided, use deterministic ones so that
# Admin/Worker installers can recreate connection strings by asking only for Server IP.
if ([string]::IsNullOrWhiteSpace($AdminPass)) { $AdminPass = Get-DeterministicPass $ActivationCode 'db-admin' 32 }
if ([string]::IsNullOrWhiteSpace($WorkerPass)) { $WorkerPass = Get-DeterministicPass $ActivationCode 'db-worker' 32 }

$psql = $env:BILVASK_PSQL_PATH
if ([string]::IsNullOrWhiteSpace($psql)) { $psql = "psql.exe" }

try {
  $cmd = Get-Command $psql -ErrorAction Stop
  $psql = $cmd.Source
} catch {
  # Auto-detect common PostgreSQL installation paths
  $candidates = @()
  foreach ($root in @("C:\Program Files\PostgreSQL", "C:\Program Files (x86)\PostgreSQL")) {
    try {
      if (Test-Path $root) {
        $candidates += Get-ChildItem -Path (Join-Path $root "*\bin\psql.exe") -ErrorAction SilentlyContinue
      }
    } catch { }
  }
  $best = $candidates | Sort-Object FullName -Descending | Select-Object -First 1
  if ($best -and (Test-Path $best.FullName)) {
    $psql = $best.FullName
  } else {
    throw "Nie znaleziono psql.exe. Zainstaluj PostgreSQL lub ustaw zmienną środowiskową BILVASK_PSQL_PATH na pełną ścieżkę do psql.exe."
  }
}

$schema = Join-Path $PSScriptRoot "schema_full.sql"
if (!(Test-Path $schema)) { throw "Brak pliku schema_full.sql" }

$env:PGPASSWORD = $PgSuperPass

# 1) Create roles + DB (idempotent)
$sql = @"
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$AdminUser') THEN
    CREATE ROLE $AdminUser LOGIN PASSWORD '$AdminPass';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$WorkerUser') THEN
    CREATE ROLE $WorkerUser LOGIN PASSWORD '$WorkerPass';
  END IF;
END $$;

-- Ensure passwords are exactly what we expect (important for multi-PC installs)
ALTER ROLE $AdminUser WITH PASSWORD '$AdminPass';
ALTER ROLE $WorkerUser WITH PASSWORD '$WorkerPass';

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = '$DbName') THEN
    CREATE DATABASE $DbName OWNER $AdminUser;
  END IF;
END $$;
"@

& $psql -h $PgHost -p $PgPort -U $PgSuperUser -d postgres -v ON_ERROR_STOP=1 -c $sql

# 2) Apply schema to target DB
& $psql -h $PgHost -p $PgPort -U $PgSuperUser -d $DbName -v ON_ERROR_STOP=1 -f $schema

# 3) Grants
$grantSql = @"
GRANT USAGE ON SCHEMA public TO $AdminUser;
GRANT USAGE ON SCHEMA public TO $WorkerUser;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO $AdminUser;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO $WorkerUser;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO $AdminUser;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO $WorkerUser;
"@
& $psql -h $PgHost -p $PgPort -U $PgSuperUser -d $DbName -v ON_ERROR_STOP=1 -c $grantSql

# 4) Keep only activation code files. Full runtime settings are written later by write_runtime_settings_full_FIXED3.ps1
$doc = [Environment]::GetFolderPath('MyDocuments')
$programData = [Environment]::GetFolderPath('CommonApplicationData')

function Write-InstallCodeFile([string]$baseFolder) {
  if ([string]::IsNullOrWhiteSpace($baseFolder)) { return }
  $dir = Join-Path $baseFolder 'BilvaskRegistrering'
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  try { icacls $dir /grant *S-1-5-32-545:(OI)(CI)M /T | Out-Null } catch { }
  $codePath = Join-Path $dir 'install_code.txt'
  $ActivationCode | Set-Content -Encoding UTF8 $codePath
}

Write-InstallCodeFile $doc
Write-InstallCodeFile $programData

Write-Host 'OK: DB bootstrap complete. Runtime settings are not written here.'
Write-Host 'Install code files:'
Write-Host (Join-Path $doc 'BilvaskRegistrering\install_code.txt')
Write-Host (Join-Path $programData 'BilvaskRegistrering\install_code.txt')
Write-Host "AdminPass: $AdminPass"
Write-Host "WorkerPass: $WorkerPass"
