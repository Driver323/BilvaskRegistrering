param(
  [Parameter(Mandatory=$true)][string]$ActivationCode,
  [string]$InstallType = 'unknown',
  [Parameter(Mandatory=$true)][string]$ServerHost,
  [string]$WorkerHost = '',
  [int]$PgPort = 5432,
  [string]$DbName = 'bilvask',
  [string]$AdminUser = 'bilvask_admin_app',
  [string]$WorkerUser = 'bilvask_worker_app',
  [string]$AdminPass = '',
  [string]$WorkerPass = '',
  [string]$DatabaseEnabled = 'true',
  [string]$UiAdminPassword = 'admin',
  [string]$UiWorkerPassword = 'worker',
  [string]$AnprRtspUrl = '',
  [string]$AnprApiToken = '',
  [string]$DahuaHost = '',
  [int]$DahuaPort = 37777,
  [string]$DahuaUser = 'admin',
  [string]$DahuaPassword = '',
  [string]$ItsApiHost = '',
  [int]$ItsApiPort = 7070,
  [string]$ItsApiPath = '/NotificationInfo/TollgateInfo',
  [string]$DokumentFolder = '',
  [string]$AutoRegisterOnPlate = 'false',
  [int]$DisplaySeconds = 10,
  [int]$WorkerRefreshSeconds = 5,
  [string]$ShowOnlyUnconfirmed = 'true',
  [string]$Cam2Enabled = 'true',
  [string]$Cam2Protocol = 'rtsp',
  [string]$Cam2Host = '',
  [int]$Cam2Port = 554,
  [string]$Cam2User = '',
  [string]$Cam2Password = '',
  [int]$Cam2Channel = 0,
  [string]$Cam2Path = '/axis-media/media.amp',
  [string]$Cam2RtspUrl = '',
  [string]$Cam2AutoRefreshOnFreeze = 'true',
  [string]$Cam3Enabled = 'true',
  [string]$Cam3Protocol = 'rtsp',
  [string]$Cam3Host = '',
  [int]$Cam3Port = 554,
  [string]$Cam3User = '',
  [string]$Cam3Password = '',
  [int]$Cam3Channel = 0,
  [string]$Cam3Path = '/axis-media/media.amp',
  [string]$Cam3RtspUrl = ''
)

$ErrorActionPreference = 'Stop'

function To-Bool([object]$value, [bool]$default = $false) {
  if ($value -is [bool]) { return [bool]$value }
  $s = ("$value").Trim().ToLowerInvariant()
  if ([string]::IsNullOrWhiteSpace($s)) { return $default }
  switch ($s) {
    '1' { return $true }
    'true' { return $true }
    'yes' { return $true }
    'y' { return $true }
    'on' { return $true }
    '0' { return $false }
    'false' { return $false }
    'no' { return $false }
    'n' { return $false }
    'off' { return $false }
    default { return $default }
  }
}

function Get-DeterministicPass([string]$code, [string]$salt, [int]$len = 32) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes("$code|$salt")
    $hash = $sha.ComputeHash($bytes)
    $hex = ($hash | ForEach-Object { $_.ToString('x2') }) -join ''
    if ($len -lt 8) { $len = 8 }
    if ($len -gt $hex.Length) { $len = $hex.Length }
    return $hex.Substring(0, $len)
  }
  finally { $sha.Dispose() }
}

function Ensure-Obj([psobject]$parent, [string]$name) {
  $prop = $parent.PSObject.Properties[$name]
  if ($prop -and $null -ne $prop.Value -and ($prop.Value -is [psobject])) { return $prop.Value }
  $child = [pscustomobject]@{}
  $parent | Add-Member -MemberType NoteProperty -Name $name -Value $child -Force
  return $child
}

function Set-Prop([psobject]$parent, [string]$name, $value) {
  $parent | Add-Member -MemberType NoteProperty -Name $name -Value $value -Force
}

function Build-Conn([string]$host, [int]$port, [string]$db, [string]$user, [string]$pass, [string]$sslMode = 'Prefer') {
  return "Host=$host;Port=$port;Database=$db;Username=$user;Password=$pass;Ssl Mode=$sslMode;Trust Server Certificate=true;"
}

function Load-ExistingRoot([string[]]$candidatePaths) {
  foreach ($p in $candidatePaths) {
    if (Test-Path $p) {
      try {
        $json = Get-Content -Raw -Path $p
        if (-not [string]::IsNullOrWhiteSpace($json)) { return ($json | ConvertFrom-Json) }
      }
      catch { }
    }
  }
  return [pscustomobject]@{}
}

function Ensure-DirWritable([string]$dir) {
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  try { icacls $dir /grant '*S-1-5-32-545:(OI)(CI)M' /T | Out-Null } catch { }
}

function Write-JsonSafely([string]$path, [string]$json) {
  $dir = Split-Path -Parent $path
  Ensure-DirWritable $dir
  $tmp = "$path.tmp"
  Set-Content -Path $tmp -Value $json -Encoding UTF8 -Force
  if (Test-Path $path) {
    try {
      $bak = "$path.bak"
      [System.IO.File]::Replace($tmp, $path, $bak, $true)
    }
    catch {
      Copy-Item -Force $tmp $path
      Remove-Item -Force $tmp -ErrorAction SilentlyContinue
    }
  }
  else {
    Move-Item -Force $tmp $path
  }
}

function Write-Trace([string]$path, [string[]]$lines) {
  $dir = Split-Path -Parent $path
  Ensure-DirWritable $dir
  Set-Content -Path $path -Value $lines -Encoding UTF8 -Force
}

if ([string]::IsNullOrWhiteSpace($ActivationCode)) { throw 'ActivationCode mangler.' }
if ([string]::IsNullOrWhiteSpace($ServerHost)) { throw 'ServerHost mangler.' }
if ([string]::IsNullOrWhiteSpace($WorkerHost)) { $WorkerHost = $ServerHost }
if ([string]::IsNullOrWhiteSpace($AdminPass)) { $AdminPass = Get-DeterministicPass $ActivationCode 'db-admin' 32 }
if ([string]::IsNullOrWhiteSpace($WorkerPass)) { $WorkerPass = Get-DeterministicPass $ActivationCode 'db-worker' 32 }
if ([string]::IsNullOrWhiteSpace($DahuaHost)) { $DahuaHost = $ServerHost }
if ([string]::IsNullOrWhiteSpace($ItsApiHost)) { $ItsApiHost = $ServerHost }
if ([string]::IsNullOrWhiteSpace($DokumentFolder)) { $DokumentFolder = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'BilvaskRegistrering' }
if ([string]::IsNullOrWhiteSpace($Cam2Path)) { $Cam2Path = '/axis-media/media.amp' }
if ([string]::IsNullOrWhiteSpace($Cam3Path)) { $Cam3Path = '/axis-media/media.amp' }

$docDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'BilvaskRegistrering'
$pdDir = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'BilvaskRegistrering'
$docSettings = Join-Path $docDir 'settings.runtime.json'
$pdSettings = Join-Path $pdDir 'settings.runtime.json'
$docCode = Join-Path $docDir 'install_code.txt'
$pdCode = Join-Path $pdDir 'install_code.txt'
$tracePath = Join-Path $pdDir 'install_trace.log'
$root = Load-ExistingRoot @($docSettings, $pdSettings)

Ensure-DirWritable $docDir
Ensure-DirWritable $pdDir
try { Ensure-DirWritable $DokumentFolder } catch { }

$install = Ensure-Obj $root 'Install'
Set-Prop $install 'ActivationCode' $ActivationCode
Set-Prop $install 'ActivatedAt' (Get-Date).ToString('s')

$auth = Ensure-Obj $root 'Auth'
Set-Prop $auth 'AdminPassword' $UiAdminPassword
Set-Prop $auth 'WorkerPassword' $UiWorkerPassword

$db = Ensure-Obj $root 'Database'
Set-Prop $db 'Enabled' (To-Bool $DatabaseEnabled $true)
Set-Prop $db 'Host' $ServerHost
Set-Prop $db 'WorkerHost' $WorkerHost
Set-Prop $db 'Port' $PgPort
Set-Prop $db 'Database' $DbName
Set-Prop $db 'AdminUser' $AdminUser
Set-Prop $db 'AdminPassword' $AdminPass
Set-Prop $db 'WorkerUser' $WorkerUser
Set-Prop $db 'WorkerPassword' $WorkerPass
Set-Prop $db 'SslMode' 'Prefer'
Set-Prop $db 'TrustServerCertificate' $true

$root | Add-Member -MemberType NoteProperty -Name 'ConnectionStrings' -Value ([pscustomobject]@{
  Admin = Build-Conn $ServerHost $PgPort $DbName $AdminUser $AdminPass 'Prefer'
  Worker = Build-Conn $WorkerHost $PgPort $DbName $WorkerUser $WorkerPass 'Prefer'
}) -Force

$anpr = Ensure-Obj $root 'Anpr'
Set-Prop $anpr 'RtspUrl' $AnprRtspUrl
Set-Prop $anpr 'ApiToken' $AnprApiToken
Set-Prop $anpr 'CameraRtspUrl' $AnprRtspUrl
Set-Prop $anpr 'PlateRecognizerApiToken' $AnprApiToken

$dahua = Ensure-Obj $root 'Dahua'
Set-Prop $dahua 'Host' $DahuaHost
Set-Prop $dahua 'Port' $DahuaPort
Set-Prop $dahua 'User' $DahuaUser
Set-Prop $dahua 'Password' $DahuaPassword
Set-Prop $dahua 'Username' $DahuaUser

$its = Ensure-Obj $root 'ItsApi'
Set-Prop $its 'Host' $ItsApiHost
Set-Prop $its 'Port' $ItsApiPort
Set-Prop $its 'Path' $ItsApiPath
Set-Prop $its 'HostIp' $ItsApiHost

$dokument = Ensure-Obj $root 'Dokument'
Set-Prop $dokument 'Folder' $DokumentFolder
Set-Prop $dokument 'AutoRegisterOnPlate' (To-Bool $AutoRegisterOnPlate $false)
Set-Prop $dokument 'DisplaySeconds' $DisplaySeconds

$workerUi = Ensure-Obj $root 'WorkerUi'
Set-Prop $workerUi 'RefreshSeconds' $WorkerRefreshSeconds
Set-Prop $workerUi 'ShowOnlyUnconfirmed' (To-Bool $ShowOnlyUnconfirmed $true)

if (-not $root.PSObject.Properties['Sesongpriser']) {
  $root | Add-Member -MemberType NoteProperty -Name 'Sesongpriser' -Value ([pscustomobject]@{ StorSommer=0; StorVinter=0; LitenSommer=0; LitenVinter=0 }) -Force
}
if (-not $root.PSObject.Properties['SesongDatoer']) {
  $root | Add-Member -MemberType NoteProperty -Name 'SesongDatoer' -Value ([pscustomobject]@{ SommerStartMonth=4; SommerStartDay=1; VinterStartMonth=10; VinterStartDay=1 }) -Force
}

$preview = Ensure-Obj $root 'PreviewCameras'
$cam2 = Ensure-Obj $preview 'Camera2'
Set-Prop $cam2 'Enabled' (To-Bool $Cam2Enabled $false)
Set-Prop $cam2 'Protocol' $(if ([string]::IsNullOrWhiteSpace($Cam2Protocol)) { 'rtsp' } else { $Cam2Protocol })
Set-Prop $cam2 'Host' $Cam2Host
Set-Prop $cam2 'Port' $Cam2Port
Set-Prop $cam2 'Username' $Cam2User
Set-Prop $cam2 'Password' $Cam2Password
Set-Prop $cam2 'Channel' $Cam2Channel
Set-Prop $cam2 'Path' $Cam2Path
Set-Prop $cam2 'RtspUrl' $Cam2RtspUrl
Set-Prop $cam2 'AutoRefreshOnFreeze' (To-Bool $Cam2AutoRefreshOnFreeze $true)

$cam3 = Ensure-Obj $preview 'Camera3'
Set-Prop $cam3 'Enabled' (To-Bool $Cam3Enabled $false)
Set-Prop $cam3 'Protocol' $(if ([string]::IsNullOrWhiteSpace($Cam3Protocol)) { 'rtsp' } else { $Cam3Protocol })
Set-Prop $cam3 'Host' $Cam3Host
Set-Prop $cam3 'Port' $Cam3Port
Set-Prop $cam3 'Username' $Cam3User
Set-Prop $cam3 'Password' $Cam3Password
Set-Prop $cam3 'Channel' $Cam3Channel
Set-Prop $cam3 'Path' $Cam3Path
Set-Prop $cam3 'RtspUrl' $Cam3RtspUrl
Set-Prop $cam3 'AutoRefreshOnFreeze' $true

$jsonOut = $root | ConvertTo-Json -Depth 12
Write-JsonSafely $docSettings $jsonOut
Write-JsonSafely $pdSettings $jsonOut

$ActivationCode | Set-Content -Encoding UTF8 -Force $docCode
$ActivationCode | Set-Content -Encoding UTF8 -Force $pdCode

$trace = @(
  ('InstallType=' + $InstallType),
  ('DocumentsSettings=' + $docSettings),
  ('ProgramDataSettings=' + $pdSettings),
  ('DocumentsInstallCode=' + $docCode),
  ('ProgramDataInstallCode=' + $pdCode),
  ('DbHost=' + $ServerHost),
  ('WorkerHost=' + $WorkerHost),
  ('DokumentFolder=' + $DokumentFolder),
  ('DatabaseEnabled=' + (To-Bool $DatabaseEnabled $true)),
  ('WroteAt=' + (Get-Date).ToString('s'))
)
Write-Trace $tracePath $trace

$missing = @()
foreach ($p in @($docSettings,$pdSettings,$docCode,$pdCode,$tracePath)) {
  if (-not (Test-Path $p)) { $missing += $p }
}
if ($missing.Count -gt 0) {
  throw ('Mangler filer etter installasjon: ' + ($missing -join '; '))
}

Write-Host 'OK: runtime settings saved to Documents and ProgramData.'
Write-Host ('Documents settings:   ' + $docSettings)
Write-Host ('ProgramData settings: ' + $pdSettings)
Write-Host ('Trace:               ' + $tracePath)
