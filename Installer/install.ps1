param(
  # Valgfritt: kode kan gis som parameter
  [string]$ActivationCode
)

$ErrorActionPreference = 'Stop'

function Extract-InstallCode {
  param([object]$Text)

  if ($null -eq $Text) { return '' }

  if ($Text -is [System.Array]) {
    $Text = ($Text -join "`n")
  }

  $s = [string]$Text
  if ([string]::IsNullOrWhiteSpace($s)) { return '' }

  $compact = ($s -replace '\s','')

  $ms = [regex]::Matches($compact, 'BVR1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+')
  if ($ms.Count -gt 0) { return $ms[$ms.Count-1].Value }

  return $compact.Trim()
}

function Get-InstallCodeFromClipboard {
  try { return (Get-Clipboard -Raw) } catch { return '' }
}

function Verify-InstallCode {
  param([string]$Code)

  $verifier = Join-Path $PSScriptRoot 'tools\license_verify.ps1'
  if (-not (Test-Path $verifier)) {
    throw "Finner ikke verifiseringsskriptet: $verifier"
  }

  # Kjør verifisering stille (ingen ekstra utskrift)
  & $verifier -ActivationCode $Code 2>$null | Out-Null
  return ($LASTEXITCODE -eq 0)
}

function Require-ValidInstallCode {
  param([string]$ProvidedCode)

  $candidate = Extract-InstallCode $ProvidedCode

  if ([string]::IsNullOrWhiteSpace($candidate)) {
    $candidate = Extract-InstallCode (Get-InstallCodeFromClipboard)
  }

  while ([string]::IsNullOrWhiteSpace($candidate) -or -not (Verify-InstallCode $candidate)) {
    Write-Host ""
    Write-Host "Ugyldig eller manglende installasjonskode." -ForegroundColor Red
    Write-Host "Lim inn koden i formatet: BVR1.<...>.<...>" -ForegroundColor Yellow
    Write-Host "Du kan også lime inn hele e-posten — installasjonen henter ut riktig kode automatisk." -ForegroundColor DarkYellow
    Write-Host "Trykk Ctrl+C for å avbryte." -ForegroundColor DarkGray
    Write-Host ""

    $candidate = Extract-InstallCode (Read-Host "Lim inn installasjonskoden")
  }

  Write-Host "OK: Installasjonskoden er gyldig." -ForegroundColor Green
  return $candidate
}

# ==== START ====
$validCode = Require-ValidInstallCode -ProvidedCode $ActivationCode

# Her starter selve installasjonen (kopiering av filer, snarveier, osv.)
# Du har en gyldig kode i variabelen $validCode
