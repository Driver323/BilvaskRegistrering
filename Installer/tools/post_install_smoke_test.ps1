param(
  [string]$Mode = 'Unknown',
  [string]$ServerHost = '',
  [int]$ServerPort = 5432,
  [string]$ResultPath = ''
)

$ErrorActionPreference = 'Stop'
$status = 'OK'
$messages = New-Object System.Collections.Generic.List[string]

function Add-Message([string]$text) {
  [void]$messages.Add($text)
}

function Set-Status([string]$newStatus) {
  switch ($newStatus) {
    'FAIL' { $script:status = 'FAIL' }
    'WARN' { if ($script:status -ne 'FAIL') { $script:status = 'WARN' } }
    default { }
  }
}

function Save-Result {
  $lines = @("STATUS=$status") + $messages.ToArray()
  if (-not [string]::IsNullOrWhiteSpace($ResultPath)) {
    $dir = Split-Path -Parent $ResultPath
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $lines | Set-Content -Encoding UTF8 -Path $ResultPath
  }
  $lines | ForEach-Object { Write-Host $_ }
}

try {
  $docBase = [Environment]::GetFolderPath('MyDocuments')
  $pdBase  = [Environment]::GetFolderPath('CommonApplicationData')
  $docsDir = Join-Path $docBase 'BilvaskRegistrering'
  $pdDir   = Join-Path $pdBase 'BilvaskRegistrering'
  $docsSettings = Join-Path $docsDir 'settings.runtime.json'
  $pdSettings   = Join-Path $pdDir 'settings.runtime.json'

  Add-Message("Etterinstallasjonstest: $Mode")
  Add-Message("Forventet DB-endepunkt: ${ServerHost}:$ServerPort")

  if (-not (Test-Path $docsSettings)) {
    throw "Mangler Documents\\BilvaskRegistrering\\settings.runtime.json"
  }

  $jsonText = Get-Content -Raw -Path $docsSettings
  $json = $jsonText | ConvertFrom-Json
  Add-Message('OK: settings.runtime.json finnes i Documents.')

  $dbHost = ''
  $dbPort = 0
  try { $dbHost = [string]$json.Database.Host } catch { }
  try { if ($json.Database.Port) { $dbPort = [int]$json.Database.Port } } catch { }

  if ([string]::IsNullOrWhiteSpace($dbHost)) {
    Set-Status 'WARN'
    Add-Message('ADVARSEL: Database.Host er tom i settings.runtime.json.')
  } else {
    Add-Message("OK: Database.Host i settings = $dbHost")
  }

  if ($ServerHost -and $dbHost -and ($dbHost -ne $ServerHost)) {
    Set-Status 'WARN'
    Add-Message("ADVARSEL: settings peker til $dbHost, men installasjonen brukte $ServerHost.")
  }
  if ($dbPort -gt 0 -and $dbPort -ne $ServerPort) {
    Set-Status 'WARN'
    Add-Message("ADVARSEL: settings bruker port $dbPort, men installasjonen brukte $ServerPort.")
  }

  $markerPath = Join-Path $docsDir '_installer_write_test.txt'
  $marker = "Bilvask smoke test $(Get-Date -Format o) ${ServerHost}:$ServerPort"
  $marker | Set-Content -Encoding UTF8 -Path $markerPath
  $readBack = (Get-Content -Raw -Path $markerPath).Trim()
  if ($readBack -ne $marker) {
    throw 'Kunne ikke lese tilbake testfilen fra Documents.'
  }
  Remove-Item -Force -ErrorAction SilentlyContinue $markerPath
  Add-Message('OK: skriving og lesing i Documents fungerer.')

  $pdMarkerPath = Join-Path $pdDir '_installer_write_test.txt'
  try {
    New-Item -ItemType Directory -Force -Path $pdDir | Out-Null
    'ProgramData write test' | Set-Content -Encoding UTF8 -Path $pdMarkerPath
    $null = Get-Content -Raw -Path $pdMarkerPath
    Remove-Item -Force -ErrorAction SilentlyContinue $pdMarkerPath
    Add-Message('OK: ProgramData-mappen er tilgjengelig.')
  } catch {
    Set-Status 'WARN'
    Add-Message('ADVARSEL: ProgramData-mappen er ikke skrivbar for testfilen.')
  }

  if (Test-Path $pdSettings) {
    Set-Status 'WARN'
    Add-Message('ADVARSEL: ProgramData\\BilvaskRegistrering\\settings.runtime.json finnes fortsatt. Dette kan overstyre lagrede innstillinger etter restart.')
  } else {
    Add-Message('OK: ingen runtime JSON i ProgramData som kan overstyre lagrede innstillinger.')
  }

  if (-not [string]::IsNullOrWhiteSpace($ServerHost)) {
    $client = New-Object System.Net.Sockets.TcpClient
    $iar = $client.BeginConnect($ServerHost, $ServerPort, $null, $null)
    if (-not $iar.AsyncWaitHandle.WaitOne(2500, $false)) {
      try { $client.Close() } catch { }
      throw "Timeout ved kontakt mot ${ServerHost}:$ServerPort"
    }
    $client.EndConnect($iar)
    try { $client.Close() } catch { }
    Add-Message("OK: TCP-port ${ServerPort} svarer på ${ServerHost}.")
  }

  if ($status -eq 'OK') {
    Add-Message('RESULTAT: alt ser bra ut.')
    Save-Result
    exit 0
  } else {
    Add-Message('RESULTAT: installasjonen fungerer, men sjekk advarslene over.')
    Save-Result
    exit 2
  }
}
catch {
  Set-Status 'FAIL'
  Add-Message('FEIL: ' + $_.Exception.Message)
  Add-Message('RESULTAT: installeringskontrollen fant et problem som bør rettes før vanlig bruk.')
  Save-Result
  exit 1
}
