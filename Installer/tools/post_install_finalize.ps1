param(
  [string]$Mode = "admin",
  [string]$ServerHost = "",
  [int]$PgPort = 5432,
  [string]$DocumentFolder = "",
  [string]$InstallCode = "",
  [string]$DocsRoot = "$env:USERPROFILE\Documents\BilvaskRegistrering",
  [string]$ProgramDataRoot = "$env:ProgramData\BilvaskRegistrering"
)

$ErrorActionPreference = 'Stop'

function Ensure-Dir {
  param([string]$Path)
  if (-not [string]::IsNullOrWhiteSpace($Path)) {
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
  }
}

function Copy-IfMissingOrDifferent {
  param(
    [string]$SourcePath,
    [string]$TargetPath
  )

  if (-not (Test-Path -LiteralPath $SourcePath)) { return $false }

  $copy = $false
  if (-not (Test-Path -LiteralPath $TargetPath)) {
    $copy = $true
  }
  else {
    $src = Get-FileHash -LiteralPath $SourcePath -Algorithm SHA256
    $dst = Get-FileHash -LiteralPath $TargetPath -Algorithm SHA256
    if ($src.Hash -ne $dst.Hash) { $copy = $true }
  }

  if ($copy) {
    Copy-Item -LiteralPath $SourcePath -Destination $TargetPath -Force
    return $true
  }

  return $false
}

function Test-TcpPortSafe {
  param(
    [string]$TargetHost,
    [int]$TargetPort
  )

  if ([string]::IsNullOrWhiteSpace($TargetHost) -or $TargetPort -le 0) {
    return $false
  }

  try {
    $result = Test-NetConnection -ComputerName $TargetHost -Port $TargetPort -WarningAction SilentlyContinue
    return [bool]$result.TcpTestSucceeded
  }
  catch {
    return $false
  }
}

function Test-SharePath {
  param([string]$PathToTest)
  if ([string]::IsNullOrWhiteSpace($PathToTest)) { return $false }
  try {
    return (Test-Path -LiteralPath $PathToTest)
  }
  catch {
    return $false
  }
}

Ensure-Dir -Path $DocsRoot
Ensure-Dir -Path $ProgramDataRoot

$docsSettings = Join-Path $DocsRoot 'settings.runtime.json'
$pdSettings = Join-Path $ProgramDataRoot 'settings.runtime.json'
$docsCode = Join-Path $DocsRoot 'install_code.txt'
$pdCode = Join-Path $ProgramDataRoot 'install_code.txt'
$verifyLog = Join-Path $ProgramDataRoot 'post_install_verify.log'
$traceLog = Join-Path $ProgramDataRoot 'install_trace.log'

$runtimeFilesOk = $true
$copiedDocsToPd = $false
$copiedPdToDocs = $false
$copiedCodeDocsToPd = $false
$copiedCodePdToDocs = $false

if ((Test-Path -LiteralPath $docsSettings) -and -not (Test-Path -LiteralPath $pdSettings)) {
  $copiedDocsToPd = Copy-IfMissingOrDifferent -SourcePath $docsSettings -TargetPath $pdSettings
}
elseif ((Test-Path -LiteralPath $pdSettings) -and -not (Test-Path -LiteralPath $docsSettings)) {
  $copiedPdToDocs = Copy-IfMissingOrDifferent -SourcePath $pdSettings -TargetPath $docsSettings
}
elseif ((Test-Path -LiteralPath $docsSettings) -and (Test-Path -LiteralPath $pdSettings)) {
  $copiedDocsToPd = Copy-IfMissingOrDifferent -SourcePath $docsSettings -TargetPath $pdSettings
}

if ((Test-Path -LiteralPath $docsCode) -and -not (Test-Path -LiteralPath $pdCode)) {
  $copiedCodeDocsToPd = Copy-IfMissingOrDifferent -SourcePath $docsCode -TargetPath $pdCode
}
elseif ((Test-Path -LiteralPath $pdCode) -and -not (Test-Path -LiteralPath $docsCode)) {
  $copiedCodePdToDocs = Copy-IfMissingOrDifferent -SourcePath $pdCode -TargetPath $docsCode
}
elseif ((Test-Path -LiteralPath $docsCode) -and (Test-Path -LiteralPath $pdCode)) {
  $copiedCodeDocsToPd = Copy-IfMissingOrDifferent -SourcePath $docsCode -TargetPath $pdCode
}

if (-not (Test-Path -LiteralPath $docsSettings) -and -not (Test-Path -LiteralPath $pdSettings)) { $runtimeFilesOk = $false }
if (-not (Test-Path -LiteralPath $docsCode) -and -not (Test-Path -LiteralPath $pdCode)) { $runtimeFilesOk = $false }
if (-not (Test-Path -LiteralPath $docsSettings)) { $runtimeFilesOk = $runtimeFilesOk -and (Test-Path -LiteralPath $pdSettings) }
if (-not (Test-Path -LiteralPath $pdSettings)) { $runtimeFilesOk = $runtimeFilesOk -and (Test-Path -LiteralPath $docsSettings) }
if (-not (Test-Path -LiteralPath $docsCode)) { $runtimeFilesOk = $runtimeFilesOk -and (Test-Path -LiteralPath $pdCode) }
if (-not (Test-Path -LiteralPath $pdCode)) { $runtimeFilesOk = $runtimeFilesOk -and (Test-Path -LiteralPath $docsCode) }

$dbTcpOk = $false
if (-not [string]::IsNullOrWhiteSpace($ServerHost) -and $PgPort -gt 0) {
  $dbTcpOk = Test-TcpPortSafe -TargetHost $ServerHost -TargetPort $PgPort
}

$shareRequired = $false
$shareAccessible = $false
if (-not [string]::IsNullOrWhiteSpace($DocumentFolder)) {
  if ($DocumentFolder.StartsWith('\\')) {
    $shareRequired = $true
    $shareAccessible = Test-SharePath -PathToTest $DocumentFolder
  }
  else {
    $shareAccessible = Test-SharePath -PathToTest $DocumentFolder
  }
}

$lines = @(
  ('Timestamp={0}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')),
  ('Mode={0}' -f $Mode),
  ('ServerHost={0}' -f $ServerHost),
  ('PgPort={0}' -f $PgPort),
  ('DocumentFolder={0}' -f $DocumentFolder),
  ('RuntimeFilesOk={0}' -f $runtimeFilesOk),
  ('DbTcpOk={0}' -f $dbTcpOk),
  ('ShareRequired={0}' -f $shareRequired),
  ('ShareAccessible={0}' -f $shareAccessible),
  ('DocsSettingsExists={0}' -f (Test-Path -LiteralPath $docsSettings)),
  ('ProgramDataSettingsExists={0}' -f (Test-Path -LiteralPath $pdSettings)),
  ('DocsCodeExists={0}' -f (Test-Path -LiteralPath $docsCode)),
  ('ProgramDataCodeExists={0}' -f (Test-Path -LiteralPath $pdCode)),
  ('CopiedDocsSettingsToProgramData={0}' -f $copiedDocsToPd),
  ('CopiedProgramDataSettingsToDocs={0}' -f $copiedPdToDocs),
  ('CopiedDocsCodeToProgramData={0}' -f $copiedCodeDocsToPd),
  ('CopiedProgramDataCodeToDocs={0}' -f $copiedCodePdToDocs)
)

Set-Content -LiteralPath $verifyLog -Value $lines -Encoding UTF8
Add-Content -LiteralPath $traceLog -Value $lines -Encoding UTF8

Write-Host ('RuntimeFilesOk={0}' -f $runtimeFilesOk)
Write-Host ('DbTcpOk={0}' -f $dbTcpOk)
Write-Host ('ShareAccessible={0}' -f $shareAccessible)
exit 0
