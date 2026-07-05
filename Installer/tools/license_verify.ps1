param(
  # Mange bruker (Get-Clipboard) som kan returnere String[] (flere linjer).
  # Vi tar object for å håndtere både String og String[] trygt.
  [Parameter(Mandatory=$true)][object]$ActivationCode,

  # Valgfritt: sti til public.xml (nyttig når scriptet kjøres fra {tmp})
  [string]$PublicKeyPath
)

$ErrorActionPreference = 'Stop'

function Convert-FromBase64Url([string]$s) {
  $s = $s.Replace('-', '+').Replace('_','/')
  switch ($s.Length % 4) {
    2 { $s += '==' }
    3 { $s += '=' }
  }
  [Convert]::FromBase64String($s)
}

function Extract-ActivationCode([object]$text) {
  if ($null -eq $text) { return '' }

  # Hvis vi fikk en String[] (f.eks. (Get-Clipboard) uten -Raw), slå sammen
  if ($text -is [System.Array]) {
    $text = ($text -join "`n")
  }

  $s = [string]$text
  if ([string]::IsNullOrWhiteSpace($s)) { return '' }

  # Fjern whitespace (Base64Url inneholder det aldri)
  $compact = ($s -replace '\s','')

  # Hvis det finnes flere koder, ta den SISTE (vanligvis den nyeste)
  $ms = [regex]::Matches($compact, 'BVR1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+')
  if ($ms.Count -gt 0) {
    return $ms[$ms.Count-1].Value
  }

  return $compact.Trim()
}

function Load-PublicKeyXml {
  # 1) Hvis installatøren sendte en eksplisitt nøkkelsti, bruk den
  if (-not [string]::IsNullOrWhiteSpace($PublicKeyPath)) {
    try {
      $p = [IO.Path]::GetFullPath($PublicKeyPath)
      if (Test-Path $p) {
        return (Get-Content -Raw -Path $p)
      }
    } catch { }
  }

  # 2) Standard: tools\license_verify.ps1 => PSScriptRoot = <Installer>\tools
  $pubPath = Join-Path $PSScriptRoot "..\_license_keys\public.xml"
  $pubPath = [IO.Path]::GetFullPath($pubPath)

  if (-not (Test-Path $pubPath)) {
    throw "Finner ikke offentlig nøkkel: $pubPath. Sørg for at _license_keys\public.xml følger med i installasjonspakken."
  }

  return (Get-Content -Raw -Path $pubPath)
}

function Test-ActivationCode([object]$code) {
  $code = Extract-ActivationCode $code
  if ([string]::IsNullOrWhiteSpace($code)) { return $false }

  $parts = $code.Split('.')
  if ($parts.Count -ne 3) { return $false }
  if ($parts[0] -ne 'BVR1') { return $false }

  try {
    $payload = Convert-FromBase64Url $parts[1]
    $sig     = Convert-FromBase64Url $parts[2]
  } catch {
    return $false
  }

  $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
  $rsa.FromXmlString((Load-PublicKeyXml)) | Out-Null

  $ok = $rsa.VerifyData($payload, 'SHA256', $sig)
  if (-not $ok) { return $false }

  try {
    $json = [Text.Encoding]::UTF8.GetString($payload)
    $obj  = $json | ConvertFrom-Json

    if ($obj.p -ne 'BilvaskRegistrering') { return $false }

    if ($obj.exp) {
      $exp = Get-Date $obj.exp
      if ((Get-Date).Date -gt $exp.Date) { return $false }
    }
  } catch {
    return $false
  }

  return $true
}

if (Test-ActivationCode $ActivationCode) {
  exit 0
} else {
  [Console]::Error.WriteLine('Ugyldig installasjonskode.')
  exit 2
}

