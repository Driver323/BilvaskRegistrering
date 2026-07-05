param(
  [string]$InstallType = 'unknown',
  [string]$ActivationCode = '',
  [string]$ServerHost = '',
  [int]$PgPort = 5432,
  [string]$DokumentFolder = '',
  [string]$ShareName = 'BilvaskSCV'
)

$ErrorActionPreference = 'Stop'

function Ensure-Dir([string]$Path) {
  if ([string]::IsNullOrWhiteSpace($Path)) { return }
  New-Item -ItemType Directory -Force -Path $Path | Out-Null
  try { icacls $Path /grant '*S-1-5-32-545:(OI)(CI)M' /T | Out-Null } catch { }
}

function Write-TextFile([string]$Path, [string]$Value) {
  Ensure-Dir (Split-Path -Parent $Path)
  Set-Content -Path $Path -Value $Value -Encoding UTF8 -Force
}

function Copy-IfMissing([string]$Source, [string]$Target) {
  if ((Test-Path $Source) -and -not (Test-Path $Target)) {
    Ensure-Dir (Split-Path -Parent $Target)
    Copy-Item -Path $Source -Destination $Target -Force
    return $true
  }
  return $false
}

function Is-LocalDrivePath([string]$Path) {
  return ($Path -match '^[A-Za-z]:\\')
}

function Test-TcpPortSafe([string]$TargetHost, [int]$TargetPort) {
  if ([string]::IsNullOrWhiteSpace($TargetHost)) { return $false }
  try {
    $client = New-Object System.Net.Sockets.TcpClient
    $iar = $client.BeginConnect($TargetHost, $TargetPort, $null, $null)
    if (-not $iar.AsyncWaitHandle.WaitOne(2500, $false)) {
      $client.Close()
      return $false
    }
    $client.EndConnect($iar)
    $client.Close()
    return $true
  }
  catch {
    return $false
  }
}

function Enable-SmbFirewall {
  try {
    Get-NetFirewallRule -DisplayGroup 'File and Printer Sharing' -ErrorAction Stop |
      Where-Object { $_.Enabled -ne 'True' } |
      Enable-NetFirewallRule | Out-Null
  }
  catch { }
}

function Ensure-SmbShare([string]$FolderPath, [string]$Name) {
  Ensure-Dir $FolderPath
  try {
    $share = Get-SmbShare -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $share) {
      New-SmbShare -Name $Name -Path $FolderPath -FullAccess 'Administrators','SYSTEM' -ChangeAccess 'Users' | Out-Null
    }
    elseif ($share.Path -ne $FolderPath) {
      throw "Share $Name exists but points to $($share.Path) instead of $FolderPath"
    }
  }
  catch {
    cmd /c "net share $Name=$FolderPath /GRANT:Everyone,FULL" | Out-Null
  }
  try { icacls $FolderPath /grant 'Users:(OI)(CI)M' /T | Out-Null } catch { }
  Enable-SmbFirewall
}

$docDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'BilvaskRegistrering'
$pdDir = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'BilvaskRegistrering'
$docSettings = Join-Path $docDir 'settings.runtime.json'
$pdSettings = Join-Path $pdDir 'settings.runtime.json'
$docCode = Join-Path $docDir 'install_code.txt'
$pdCode = Join-Path $pdDir 'install_code.txt'
$tracePath = Join-Path $pdDir 'install_trace.log'
$verifyPath = Join-Path $pdDir 'post_install_verify.log'

Ensure-Dir $docDir
Ensure-Dir $pdDir

$copied = @()
if (Copy-IfMissing $docSettings $pdSettings) { $copied += 'settings Documents->ProgramData' }
if (Copy-IfMissing $pdSettings $docSettings) { $copied += 'settings ProgramData->Documents' }
if (Copy-IfMissing $docCode $pdCode) { $copied += 'install_code Documents->ProgramData' }
if (Copy-IfMissing $pdCode $docCode) { $copied += 'install_code ProgramData->Documents' }

if (-not (Test-Path $docCode) -and -not [string]::IsNullOrWhiteSpace($ActivationCode)) {
  Write-TextFile $docCode ($ActivationCode + [Environment]::NewLine)
  $copied += 'install_code created in Documents'
}
if (-not (Test-Path $pdCode) -and -not [string]::IsNullOrWhiteSpace($ActivationCode)) {
  Write-TextFile $pdCode ($ActivationCode + [Environment]::NewLine)
  $copied += 'install_code created in ProgramData'
}

if (-not (Test-Path $docSettings) -and -not (Test-Path $pdSettings) -and -not [string]::IsNullOrWhiteSpace($ActivationCode)) {
  $hostValue = if ([string]::IsNullOrWhiteSpace($ServerHost)) { '127.0.0.1' } else { $ServerHost }
  $folderValue = if ([string]::IsNullOrWhiteSpace($DokumentFolder)) { $docDir } else { $DokumentFolder }
  $minimal = @{
    Database = @{ Enabled = $true; Host = $hostValue; WorkerHost = $hostValue; Port = $PgPort; Database = 'bilvask' }
    Dokument = @{ Folder = $folderValue; AutoRegisterOnPlate = $false; DisplaySeconds = 10 }
    WorkerUi = @{ RefreshSeconds = 5; ShowOnlyUnconfirmed = $true }
    Auth = @{ AdminPassword = 'admin'; WorkerPassword = 'worker' }
    Install = @{ ActivationCode = $ActivationCode; ActivatedAt = (Get-Date).ToString('s') }
  } | ConvertTo-Json -Depth 8
  Write-TextFile $docSettings $minimal
  Write-TextFile $pdSettings $minimal
  $copied += 'minimal settings created in Documents+ProgramData'
}

$shareCreated = $false
$uncToTest = ''
if ([string]::IsNullOrWhiteSpace($DokumentFolder)) {
  $DokumentFolder = $docDir
}

if (($InstallType -eq 'server') -or ($InstallType -eq 'full')) {
  if (Is-LocalDrivePath $DokumentFolder) {
    Ensure-SmbShare -FolderPath $DokumentFolder -Name $ShareName
    $shareCreated = $true
    $uncToTest = "\\$env:COMPUTERNAME\$ShareName"
  }
  elseif ($DokumentFolder.StartsWith('\\')) {
    $uncToTest = $DokumentFolder
  }
}
else {
  if ($DokumentFolder.StartsWith('\\')) {
    $uncToTest = $DokumentFolder
  }
}

$tcpOk = Test-TcpPortSafe -TargetHost $ServerHost -TargetPort $PgPort
$shareOk = $null
if (-not [string]::IsNullOrWhiteSpace($uncToTest)) {
  try { $shareOk = Test-Path $uncToTest } catch { $shareOk = $false }
}

$runtimeFilesOk = (Test-Path $docSettings) -and (Test-Path $pdSettings) -and (Test-Path $docCode) -and (Test-Path $pdCode)
$shareRequired = -not [string]::IsNullOrWhiteSpace($uncToTest)

$lines = @(
  ('Timestamp=' + (Get-Date).ToString('s')),
  ('Mode=' + $InstallType),
  ('ServerHost=' + $ServerHost),
  ('PgPort=' + $PgPort),
  ('DocumentFolder=' + $DokumentFolder),
  ('RuntimeFilesOk=' + $runtimeFilesOk),
  ('DbTcpOk=' + $tcpOk),
  ('ShareRequired=' + $shareRequired),
  ('ShareAccessible=' + $shareOk),
  ('DocsSettingsExists=' + (Test-Path $docSettings)),
  ('ProgramDataSettingsExists=' + (Test-Path $pdSettings)),
  ('DocsCodeExists=' + (Test-Path $docCode)),
  ('ProgramDataCodeExists=' + (Test-Path $pdCode)),
  ('CopiedOrCreated=' + ($(if ($copied.Count -gt 0) { $copied -join '; ' } else { 'none' })))
)
Write-TextFile $verifyPath ($lines -join [Environment]::NewLine)

try {
  $append = @(
    '--- Post install finalize ---',
    ('CopiedOrCreated=' + ($(if ($copied.Count -gt 0) { $copied -join '; ' } else { 'none' }))),
    ('ShareCreated=' + $shareCreated),
    ('SharePathTest=' + $uncToTest),
    ('ShareAccessible=' + $shareOk),
    ('Tcp5432=' + $tcpOk),
    ('VerifiedAt=' + (Get-Date).ToString('s'))
  )
  Add-Content -Path $tracePath -Value $append -Encoding UTF8
}
catch { }

if (-not (Test-Path $docSettings)) { throw 'Mangler settings.runtime.json i Documents etter finalize.' }
if (-not (Test-Path $pdSettings)) { throw 'Mangler settings.runtime.json i ProgramData etter finalize.' }
if (-not (Test-Path $docCode)) { throw 'Mangler install_code.txt i Documents etter finalize.' }
if (-not (Test-Path $pdCode)) { throw 'Mangler install_code.txt i ProgramData etter finalize.' }

Write-Host ('RuntimeFilesOk=' + $runtimeFilesOk)
Write-Host ('DbTcpOk=' + $tcpOk)
Write-Host ('ShareAccessible=' + $shareOk)
