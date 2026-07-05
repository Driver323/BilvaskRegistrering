param(
    [Parameter(Mandatory=$true)][string]$ServerHost,
    [string]$ShareName = 'BilvaskSCV',
    [int]$DbPort = 5432
)

$ErrorActionPreference = 'Stop'
$unc = "\\$ServerHost\\$ShareName"

Write-Host "UNC path : $unc"
Write-Host ("Share ok : {0}" -f (Test-Path -LiteralPath $unc))

try {
    $db = Test-NetConnection $ServerHost -Port $DbPort -WarningAction SilentlyContinue
    Write-Host ("DB port  : {0}" -f $db.TcpTestSucceeded)
} catch {
    Write-Host "DB port  : blad testu - $($_.Exception.Message)"
}

Write-Host ''
Write-Host 'Jesli Share ok = False, sprawdz na serwerze:'
Write-Host '1) czy share istnieje,'
Write-Host '2) czy File and Printer Sharing jest wlaczone,'
Write-Host '3) czy nazwa share jest dokladnie BilvaskSCV,'
Write-Host '4) czy masz prawa do udzialu.'
