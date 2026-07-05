param(
  [Parameter(Mandatory=$true)][ValidateSet('Admin','Worker')][string]$Mode,
  [Parameter(Mandatory=$true)][string]$ServerHost,
  [int]$PgPort = 5432,
  [string]$DbName = 'bilvask',
  [string]$AdminUser = 'bilvask_admin_app',
  [string]$WorkerUser = 'bilvask_worker_app',
  [Parameter(Mandatory=$true)][string]$ActivationCode,
  [string]$UiAdminPassword = 'admin',
  [string]$UiWorkerPassword = 'worker'
)

$ErrorActionPreference = 'Stop'

function Get-DeterministicPass([string]$code, [string]$salt, [int]$len = 32) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $bytes = [System.Text.Encoding]::UTF8.GetBytes("$code|$salt")
  $hash = $sha.ComputeHash($bytes)
  $hex = ($hash | ForEach-Object { $_.ToString('x2') }) -join ''
  if ($len -lt 8) { $len = 8 }
  if ($len -gt $hex.Length) { $len = $hex.Length }
  return $hex.Substring(0, $len)
}

function Ensure-Obj([psobject]$parent, [string]$name) {
  $prop = $parent.PSObject.Properties[$name]
  if ($prop -and $prop.Value -ne $null -and ($prop.Value -is [psobject])) {
    return $prop.Value
  }
  $child = [pscustomobject]@{}
  $parent | Add-Member -MemberType NoteProperty -Name $name -Value $child -Force
  return $child
}

function Set-Prop([psobject]$parent, [string]$name, $value) {
  $parent | Add-Member -MemberType NoteProperty -Name $name -Value $value -Force
}

function Get-String([psobject]$parent, [string]$name) {
  $prop = $parent.PSObject.Properties[$name]
  if (-not $prop) { return "" }
  if ($null -eq $prop.Value) { return "" }
  return [string]$prop.Value
}

function Build-Conn([string]$host, [int]$port, [string]$db, [string]$user, [string]$pass) {
  return "Host=$host;Port=$port;Database=$db;Username=$user;Password=$pass;Ssl Mode=Prefer;Trust Server Certificate=true;"
}

function Write-InstallCodeFile([string]$baseFolder) {
  if ([string]::IsNullOrWhiteSpace($baseFolder)) { return }
  $dir = Join-Path $baseFolder 'BilvaskRegistrering'
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  try { icacls $dir /grant '*S-1-5-32-545:(OI)(CI)M' /T | Out-Null } catch { }
  $codePath = Join-Path $dir 'install_code.txt'
  $ActivationCode | Set-Content -Encoding UTF8 -Force $codePath
}

function Disable-ProgramDataRuntimeJson() {
  $programData = [Environment]::GetFolderPath('CommonApplicationData')
  if ([string]::IsNullOrWhiteSpace($programData)) { return }
  $dir = Join-Path $programData 'BilvaskRegistrering'
  $path = Join-Path $dir 'settings.runtime.json'
  if (Test-Path $path) {
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $backup = Join-Path $dir ("settings.runtime.json.disabled_{0}" -f $stamp)
    try {
      Move-Item -Force -Path $path -Destination $backup
      Write-Host "INFO: flyttet gammel ProgramData settings.runtime.json -> $backup"
    } catch {
      try {
        Remove-Item -Force -Path $path
        Write-Host "INFO: usunieto gammel ProgramData settings.runtime.json"
      } catch {
        Write-Warning "Kunne ikke fjerne ProgramData settings.runtime.json: $($_.Exception.Message)"
      }
    }
  }
}

$adminPass = Get-DeterministicPass $ActivationCode 'db-admin' 32
$workerPass = Get-DeterministicPass $ActivationCode 'db-worker' 32

function Write-SettingsFile([string]$baseFolder) {
  if ([string]::IsNullOrWhiteSpace($baseFolder)) { return }
  $dir = Join-Path $baseFolder 'BilvaskRegistrering'
  New-Item -ItemType Directory -Force -Path $dir | Out-Null

  try { icacls $dir /grant '*S-1-5-32-545:(OI)(CI)M' /T | Out-Null } catch { }

  $settingsPath = Join-Path $dir 'settings.runtime.json'

  $root = [pscustomobject]@{}
  if (Test-Path $settingsPath) {
    try {
      $json = Get-Content -Raw -Path $settingsPath
      if (-not [string]::IsNullOrWhiteSpace($json)) {
        $root = $json | ConvertFrom-Json
      }
    } catch {
      $root = [pscustomobject]@{}
    }
  }

  $install = Ensure-Obj $root 'Install'
  Set-Prop $install 'ActivationCode' $ActivationCode
  Set-Prop $install 'ActivatedAt' (Get-Date).ToString('s')

  $auth = Ensure-Obj $root 'Auth'
  Set-Prop $auth 'AdminPassword' $UiAdminPassword
  Set-Prop $auth 'WorkerPassword' $UiWorkerPassword

  $db = Ensure-Obj $root 'Database'
  Set-Prop $db 'Enabled' $true
  Set-Prop $db 'Host' $ServerHost
  Set-Prop $db 'WorkerHost' $ServerHost
  Set-Prop $db 'Port' $PgPort
  Set-Prop $db 'Database' $DbName
  Set-Prop $db 'SslMode' 'Prefer'
  Set-Prop $db 'TrustServerCertificate' $true

  if ([string]::IsNullOrWhiteSpace((Get-String $db 'AdminUser'))) { Set-Prop $db 'AdminUser' $AdminUser }
  if ([string]::IsNullOrWhiteSpace((Get-String $db 'AdminPassword'))) { Set-Prop $db 'AdminPassword' $adminPass }
  if ([string]::IsNullOrWhiteSpace((Get-String $db 'WorkerUser'))) { Set-Prop $db 'WorkerUser' $WorkerUser }
  if ([string]::IsNullOrWhiteSpace((Get-String $db 'WorkerPassword'))) { Set-Prop $db 'WorkerPassword' $workerPass }

  $adminConn = Build-Conn $ServerHost $PgPort $DbName (Get-String $db 'AdminUser') (Get-String $db 'AdminPassword')
  $workerConn = Build-Conn $ServerHost $PgPort $DbName (Get-String $db 'WorkerUser') (Get-String $db 'WorkerPassword')

  $root | Add-Member -MemberType NoteProperty -Name 'ConnectionStrings' -Value ([pscustomobject]@{
    Admin  = $adminConn
    Worker = $workerConn
  }) -Force

  $root | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 -Force $settingsPath
}

$doc = [Environment]::GetFolderPath('MyDocuments')
$programData = [Environment]::GetFolderPath('CommonApplicationData')

Write-SettingsFile $doc
Write-InstallCodeFile $doc
Write-InstallCodeFile $programData
Disable-ProgramDataRuntimeJson

Write-Host "OK: settings.runtime.json updated for $Mode"
Write-Host "INFO: runtime JSON skrives kun i Documents\\BilvaskRegistrering"
Write-Host "ServerHost: $ServerHost"
