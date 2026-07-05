param(
    [string]$DocsDir = "$env:USERPROFILE\Documents\BilvaskRegistrering",
    [string]$ProgramDataDir = "$env:ProgramData\BilvaskRegistrering",
    [switch]$PreferProgramData
)

$ErrorActionPreference = 'Stop'

function Ensure-Dir([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
}

function Copy-IfMissing([string]$source, [string]$target) {
    if ((Test-Path -LiteralPath $source) -and (-not (Test-Path -LiteralPath $target))) {
        Copy-Item -LiteralPath $source -Destination $target -Force
        Write-Host "[OK] Skopiowano: $target"
    }
}

function Mirror-Newer([string]$a, [string]$b) {
    if ((Test-Path -LiteralPath $a) -and (Test-Path -LiteralPath $b)) {
        $ai = Get-Item -LiteralPath $a
        $bi = Get-Item -LiteralPath $b
        if ($ai.LastWriteTime -gt $bi.LastWriteTime) {
            Copy-Item -LiteralPath $a -Destination $b -Force
            Write-Host "[SYNC] $a -> $b"
        } elseif ($bi.LastWriteTime -gt $ai.LastWriteTime) {
            Copy-Item -LiteralPath $b -Destination $a -Force
            Write-Host "[SYNC] $b -> $a"
        }
    }
}

Ensure-Dir $DocsDir
Ensure-Dir $ProgramDataDir

$docsSettings = Join-Path $DocsDir 'settings.runtime.json'
$pdSettings = Join-Path $ProgramDataDir 'settings.runtime.json'
$docsCode = Join-Path $DocsDir 'install_code.txt'
$pdCode = Join-Path $ProgramDataDir 'install_code.txt'

if ($PreferProgramData) {
    Copy-IfMissing $pdSettings $docsSettings
    Copy-IfMissing $pdCode $docsCode
} else {
    Copy-IfMissing $docsSettings $pdSettings
    Copy-IfMissing $docsCode $pdCode
}

Mirror-Newer $docsSettings $pdSettings
Mirror-Newer $docsCode $pdCode

Write-Host ''
Write-Host '===== STATUS ====='
Write-Host ("Docs settings      : {0}" -f (Test-Path -LiteralPath $docsSettings))
Write-Host ("ProgramData settings: {0}" -f (Test-Path -LiteralPath $pdSettings))
Write-Host ("Docs install_code  : {0}" -f (Test-Path -LiteralPath $docsCode))
Write-Host ("ProgramData install_code: {0}" -f (Test-Path -LiteralPath $pdCode))
