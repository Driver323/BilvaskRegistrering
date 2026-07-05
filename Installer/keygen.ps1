param(
  [int]$Count = 5,
  [string]$Expires = "2030-12-31",
  [string]$PublicKeysDir = ".\_license_keys",
  [string]$PrivateKeyPath = $env:BILVASK_LICENSE_PRIVATEKEY,
  [switch]$AllowPrivateKeyInTree,
  [switch]$GenerateNewKeyPair
)

$ErrorActionPreference = 'Stop'

function Get-DefaultPrivateKeyPath {
  $userHome = [Environment]::GetFolderPath('UserProfile')
  return (Join-Path $userHome ".bilvask_license\private.xml")
}

function Ensure-KeyMaterial {
  param(
    [string]$pubDir,
    [string]$privPath,
    [bool]$allowInTree,
    [bool]$generateNew
  )

  if ([string]::IsNullOrWhiteSpace($privPath)) {
    $privPath = Get-DefaultPrivateKeyPath
  }

  New-Item -ItemType Directory -Force -Path $pubDir | Out-Null
  $pubPath = Join-Path $pubDir "public.xml"

  $fullPriv = [IO.Path]::GetFullPath($privPath)
  $fullInstaller = [IO.Path]::GetFullPath((Get-Location).Path)

  if (-not $allowInTree) {
    if ($fullPriv.StartsWith($fullInstaller, [System.StringComparison]::OrdinalIgnoreCase)) {
      throw "Dla bezpieczeństwa prywatny klucz NIE może leżeć w folderze Installer. Ustaw -PrivateKeyPath poza repo (np. $([Environment]::GetFolderPath('UserProfile'))\.bilvask_license\private.xml) lub użyj -AllowPrivateKeyInTree (niezalecane)."
    }
  }

  $privDir = Split-Path -Parent $fullPriv
  New-Item -ItemType Directory -Force -Path $privDir | Out-Null

  $hasPriv = Test-Path $fullPriv
  $hasPub  = Test-Path $pubPath

  if (-not $hasPriv -or -not $hasPub) {
    if (-not $generateNew) {
      $msg = @()
      if (-not $hasPriv) { $msg += "Brak klucza prywatnego: $fullPriv" }
      if (-not $hasPub)  { $msg += "Brak klucza publicznego: $pubPath" }
      $msg += ""
      $msg += "Aby generować kody instalacyjne musisz mieć ISTNIEJĄCY klucz prywatny odpowiadający kluczowi publicznemu używanemu w instalatorze/aplikacji."
      $msg += "Skopiuj private.xml do: $fullPriv (poza repo), lub wskaż go parametrem -PrivateKeyPath."
      $msg += ""
      $msg += "Jeśli naprawdę chcesz wygenerować NOWĄ parę kluczy, uruchom ponownie z -GenerateNewKeyPair."
      throw ($msg -join "`n")
    }

    Write-Host "Generuję NOWĄ parę kluczy RSA (2048)..." -ForegroundColor Yellow
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider 2048
    $privXml = $rsa.ToXmlString($true)
    $pubXml  = $rsa.ToXmlString($false)

    Set-Content -Path $fullPriv -Value $privXml -Encoding UTF8
    Set-Content -Path $pubPath  -Value $pubXml  -Encoding UTF8

    Write-Host "OK: private key -> $fullPriv" -ForegroundColor Green
    Write-Host "OK: public key  -> $pubPath" -ForegroundColor Green
    Write-Host ""
    Write-Host "UWAGA: Po wygenerowaniu nowej pary musisz zaktualizować klucz publiczny w instalatorze (public.xml)." -ForegroundColor Red
  }

  return @{ Priv = $fullPriv; Pub = $pubPath }
}

function Sign-ActivationCode {
  param(
    [string]$payloadJson,
    [string]$privateKeyPath
  )
  $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
  $rsa.FromXmlString((Get-Content -Raw -Path $privateKeyPath))
  $bytes = [Text.Encoding]::UTF8.GetBytes($payloadJson)
  # PKCS#1 v1.5 + SHA256 (must match verifier)
  $sig = $rsa.SignData($bytes, 'SHA256')
  return $sig
}

function To-Base64Url {
  param([byte[]]$Bytes)
  $b64 = [Convert]::ToBase64String($Bytes)
  $b64 = $b64.Replace('+','-').Replace('/','_')
  return $b64.TrimEnd('=')
}

function New-ActivationCode {
  param(
    [datetime]$expiresDate,
    [string]$privateKeyPath
  )

  $payload = @{
    p   = 'BilvaskRegistrering'
    v   = '1'
    exp = $expiresDate.ToString('yyyy-MM-dd')
    n   = [Guid]::NewGuid().ToString('N')
  } | ConvertTo-Json -Compress

  $payloadBytes = [Text.Encoding]::UTF8.GetBytes($payload)
  $sigBytes     = Sign-ActivationCode -payloadJson $payload -privateKeyPath $privateKeyPath

  $pB64u = To-Base64Url -Bytes $payloadBytes
  $sB64u = To-Base64Url -Bytes $sigBytes

  return "BVR1.$pB64u.$sB64u"
}

$keys = Ensure-KeyMaterial -pubDir $PublicKeysDir -privPath $PrivateKeyPath -allowInTree:$AllowPrivateKeyInTree.IsPresent -generateNew:$GenerateNewKeyPair.IsPresent
$exp  = [datetime]::ParseExact($Expires, "yyyy-MM-dd", $null)

Write-Host ""
Write-Host "Wygenerowane kody instalacyjne (wklej do instalatora):" -ForegroundColor Cyan

# Force array even when Count=1
$codes = @()
for ($i=0; $i -lt $Count; $i++) {
  $codes += (New-ActivationCode -expiresDate $exp -privateKeyPath $keys.Priv)
}

# pokaż na ekranie (jak chcesz)
Write-Host ""
Write-Host "Wygenerowane kody instalacyjne:" -ForegroundColor Cyan
$codes | ForEach-Object { Write-Host $_ }

# ZAPISZ DO PLIKU (pewne kopiowanie)
$codes | Set-Content -Encoding UTF8 -Path (Join-Path $PSScriptRoot "install_codes.txt")

# SKOPIUJ OSTATNI DO SCHOWKA (najwygodniej)
try {
  Set-Clipboard -Value ([string]$codes[-1]).Trim().TrimEnd('.')
} catch {
  Write-Host "UWAGA: Nie mogę skopiować do schowka (Set-Clipboard). Kod jest w install_codes.txt." -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Ostatni kod skopiowany do schowka + zapisany w install_codes.txt" -ForegroundColor Green
