param(
  [Parameter(Mandatory=$false)]
  [string]$InstallDir = "C:\Program Files\BilvaskRegistrering",
  [switch]$Interactive
)

$ErrorActionPreference = "Stop"

function Write-Log {
  param([string]$Message)
  $stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
  Write-Host "[$stamp] $Message"
}

if (-not (Test-Path -LiteralPath $InstallDir)) {
  Write-Log "Katalog ikke funnet: $InstallDir"
  exit 0
}

Write-Log "Starter unblocking i: $InstallDir"

$files = Get-ChildItem -LiteralPath $InstallDir -Recurse -Force -File -ErrorAction SilentlyContinue
$changed = 0
foreach ($file in $files) {
  try {
    $zone = Get-Item -LiteralPath $file.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue
    if ($null -ne $zone) {
      Unblock-File -LiteralPath $file.FullName -ErrorAction Stop
      $changed++
    }
  }
  catch {
    Write-Log (("Nie udalo sie odblokowac: {0} ({1})" -f $file.FullName, $_.Exception.Message))
  }
}

Write-Log (("Odblokowano plikow: {0}" -f $changed))
Write-Log "Uwaga: jesli pojawia sie SmartScreen, to nadal potrzebny bedzie podpis cyfrowy certyfikatem."

if ($Interactive) {
  Write-Host ""
  Write-Host "Gotowe. Nacisnij Enter, aby zamknac..."
  [void][System.Console]::ReadLine()
}

exit 0
