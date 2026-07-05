param(
  [Parameter(Mandatory=$true)][string]$ServerHost,
  [int]$Port = 5432,
  [int]$TimeoutMs = 2000,
  [string]$ResultPath = ""
)

$ErrorActionPreference = 'Stop'

function Write-Result([string]$text) {
  if (-not [string]::IsNullOrWhiteSpace($ResultPath)) {
    try { $text | Set-Content -Encoding UTF8 -Path $ResultPath } catch { }
  }
}

try {
  $client = New-Object System.Net.Sockets.TcpClient
  $iar = $client.BeginConnect($ServerHost, $Port, $null, $null)
  if (-not $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) {
    try { $client.Close() } catch { }
    throw "Timeout: ingen svar fra ${ServerHost}:$Port (etter ${TimeoutMs}ms)."
  }
  $client.EndConnect($iar)
  try { $client.Close() } catch { }

  $msg = "OK: TCP-kontakt etablert til ${ServerHost}:$Port. (Dette tester nettverk/port; innlogging testes ved første oppstart.)"
  Write-Result $msg
  Write-Host $msg
  exit 0
}
catch {
  $msg = "FEIL: Kan ikke koble til ${ServerHost}:$Port. " + $_.Exception.Message
  Write-Result $msg
  Write-Host $msg
  exit 1
}
