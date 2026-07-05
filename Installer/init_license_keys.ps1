param(
  [string]$PrivateKeyPath = $env:BILVASK_LICENSE_PRIVATEKEY,
  [string]$PublicKeysDir = ".\_license_keys",
  [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Get-DefaultPrivateKeyPath {
  $userHome = [Environment]::GetFolderPath('UserProfile')
  return (Join-Path $userHome ".bilvask_license\private.xml")
}

if ([string]::IsNullOrWhiteSpace($PrivateKeyPath)) {
  $PrivateKeyPath = Get-DefaultPrivateKeyPath
}

$PrivateKeyPath = [IO.Path]::GetFullPath($PrivateKeyPath)
$PublicKeysDir  = [IO.Path]::GetFullPath($PublicKeysDir)
$PublicKeyPath  = Join-Path $PublicKeysDir "public.xml"

# Safety: private key must not be inside Installer tree
$installerRoot = [IO.Path]::GetFullPath((Get-Location).Path)
if ($PrivateKeyPath.StartsWith($installerRoot, [StringComparison]::OrdinalIgnoreCase)) {
  throw "Dla bezpieczeństwa private.xml nie może leżeć w folderze Installer. Ustaw -PrivateKeyPath poza repo (np. $([Environment]::GetFolderPath('UserProfile'))\.bilvask_license\private.xml)."
}

if ((Test-Path $PrivateKeyPath) -and -not $Force) {
  throw "Plik private key już istnieje: $PrivateKeyPath. Użyj -Force jeśli chcesz go nadpisać (spowoduje to unieważnienie starych kodów instalacyjnych!)."
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $PrivateKeyPath) | Out-Null
New-Item -ItemType Directory -Force -Path $PublicKeysDir | Out-Null

Write-Host "Generuję nową parę kluczy RSA (2048)..." -ForegroundColor Yellow
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider 2048
$privXml = $rsa.ToXmlString($true)
$pubXml  = $rsa.ToXmlString($false)

Set-Content -Path $PrivateKeyPath -Value $privXml -Encoding UTF8
Set-Content -Path $PublicKeyPath  -Value $pubXml  -Encoding UTF8

Write-Host "OK: private key -> $PrivateKeyPath" -ForegroundColor Green
Write-Host "OK: public key  -> $PublicKeyPath" -ForegroundColor Green

# Update verifier in installer (PowerShell)
$verifyPath = Join-Path $PSScriptRoot "tools\license_verify.ps1"
$verifyText = Get-Content -Raw -Path $verifyPath
$verifyText2 = [Regex]::Replace(
  $verifyText,
  "\$PublicKeyXml\s*=\s*@'.*?'@",
  "`$PublicKeyXml = @'`n$pubXml`n'@",
  [System.Text.RegularExpressions.RegexOptions]::Singleline
)
Set-Content -Path $verifyPath -Value $verifyText2 -Encoding UTF8
Write-Host "OK: zaktualizowano public key w: $verifyPath" -ForegroundColor Green

# Update verifier in C# (Admin + Worker)
$csTargets = @(
  (Join-Path $PSScriptRoot "..\BilvaskRegistrering\Security\InstallCodeVerifier.cs")
  (Join-Path $PSScriptRoot "..\BilvaskRegistrering.Worker\Security\InstallCodeVerifier.cs")
)

foreach ($cs in $csTargets) {
  if (-not (Test-Path $cs)) {
    Write-Warning "Pomijam (nie znaleziono): $cs"
    continue
  }

  $t = Get-Content -Raw -Path $cs
  # PowerShell does NOT treat backslash as an escape for quotes, so we use
  # single-quoted strings (and .NET RegexOptions.Singleline) to avoid parser issues.
  $pattern = 'private\s+const\s+string\s+PublicKeyXml\s*=\s*@\"<RSAKeyValue>[\s\S]*?</RSAKeyValue>\";'
  $replacement = 'private const string PublicKeyXml = @"' + $pubXml + '";'
  $rx = New-Object System.Text.RegularExpressions.Regex($pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
  $t2 = $rx.Replace($t, $replacement)
  Set-Content -Path $cs -Value $t2 -Encoding UTF8
  Write-Host "OK: zaktualizowano public key w: $cs" -ForegroundColor Green
}

Write-Host "" 
Write-Host "Następny krok: wygeneruj kody instalacyjne:" -ForegroundColor Cyan
Write-Host "  .\keygen.ps1 -Count 5 -Expires \"2030-12-31\"" -ForegroundColor Cyan
Write-Host "" 
Write-Host "UWAGA: private key trzymaj offline i NIGDY go nie wysyłaj klientowi." -ForegroundColor DarkYellow
