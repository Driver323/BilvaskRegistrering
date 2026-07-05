param(
  [Parameter(Mandatory=$true)]
  [string]$ActivationCode,

  # Optional explicit paths (the installer can pass these). If omitted, we compute defaults.
  [string]$UserDocsPath,
  [string]$ProgramDataPath
)

$ErrorActionPreference = 'Stop'

function To-PlainUtf8NoBom {
  param([string]$Text, [string]$Path)
  $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
  [System.IO.File]::WriteAllText($Path, $Text, $utf8NoBom)
}

function Ensure-Dir {
  param([string]$Path)
  if (-not (Test-Path -LiteralPath $Path)) {
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
  }
}

function Load-JsonOrEmpty {
  param([string]$Path)
  if (-not (Test-Path -LiteralPath $Path)) { return @{} }

  try {
    $raw = Get-Content -Raw -LiteralPath $Path
    if ([string]::IsNullOrWhiteSpace($raw)) { return @{} }
    return ($raw | ConvertFrom-Json -ErrorAction Stop)
  }
  catch {
    # if invalid JSON, rename and start fresh
    try {
      $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
      $backup = "$Path.invalid_$stamp"
      Move-Item -LiteralPath $Path -Destination $backup -Force
    } catch { }
    return @{}
  }
}

function Save-Json {
  param([object]$Obj, [string]$Path)
  $json = $Obj | ConvertTo-Json -Depth 20
  To-PlainUtf8NoBom -Text $json -Path $Path
}

function Upsert-ActivationCode {
  param([string]$TargetFile)

  $cfg = Load-JsonOrEmpty -Path $TargetFile

  if ($null -eq $cfg) { $cfg = @{} }

  if (-not $cfg.PSObject.Properties.Name.Contains('Install')) {
    $cfg | Add-Member -NotePropertyName Install -NotePropertyValue (@{})
  }

  # Ensure Install is a hashtable/object
  if ($cfg.Install -isnot [object]) {
    $cfg.Install = @{}
  }

  # Set/overwrite
  $cfg.Install | Add-Member -Force -NotePropertyName ActivationCode -NotePropertyValue $ActivationCode

  Save-Json -Obj $cfg -Path $TargetFile
}

# Defaults
if ([string]::IsNullOrWhiteSpace($UserDocsPath)) {
  $UserDocsPath = [Environment]::GetFolderPath('MyDocuments')
}
if ([string]::IsNullOrWhiteSpace($ProgramDataPath)) {
  $ProgramDataPath = [Environment]::GetFolderPath('CommonApplicationData')
}

$targets = @(
  (Join-Path $UserDocsPath "BilvaskRegistrering\settings.runtime.json"),
  (Join-Path $ProgramDataPath "BilvaskRegistrering\settings.runtime.json")
)

foreach ($t in $targets) {
  $dir = Split-Path -Parent $t
  Ensure-Dir -Path $dir
  Upsert-ActivationCode -TargetFile $t
}

Write-Host "OK: Activation code saved to:" -ForegroundColor Green
$targets | ForEach-Object { Write-Host " - $_" }
