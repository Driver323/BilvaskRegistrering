#requires -version 5.1
<#!
  release_pack_NET8_selfcontained.ps1

  Publikuje Admin + Worker do Installer\publish\{Admin,Worker}
  jako SELF-CONTAINED dla .NET 8, czyli runtime jest juz w paczce
  i potem Inno Setup nie wymaga osobnej instalacji .NET Runtime.

  Domyslnie:
    - Configuration = Release
    - Runtime       = win-x64
    - SelfContained = true

  Przyklady:
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\release_pack_NET8_selfcontained.ps1
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\release_pack_NET8_selfcontained.ps1 -Runtime win-x64
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\release_pack_NET8_selfcontained.ps1 -FrameworkDependent
#>

param(
  [ValidateSet("Debug","Release")]
  [string]$Configuration = "Release",

  [string]$Runtime = "win-x64",

  [switch]$FrameworkDependent,

  [switch]$NoClean,

  [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$InstallerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SolutionDir  = Resolve-Path (Join-Path $InstallerDir "..")

$AdminProj  = Join-Path $SolutionDir "BilvaskRegistrering\BilvaskRegistrering.csproj"
$WorkerProj = Join-Path $SolutionDir "BilvaskRegistrering.Worker\BilvaskRegistrering.Worker.csproj"

$PublishRoot = Join-Path $InstallerDir "publish"
$AdminOut    = Join-Path $PublishRoot "Admin"
$WorkerOut   = Join-Path $PublishRoot "Worker"

$SelfContained = -not $FrameworkDependent.IsPresent

function Assert-Tool([string]$ToolName) {
  try {
    $null = Get-Command $ToolName -ErrorAction Stop
  }
  catch {
    throw "Nie znaleziono narzedzia '$ToolName' w PATH. Zainstaluj .NET 8 SDK i sprobuj ponownie."
  }
}

function Assert-Path([string]$PathToCheck, [string]$Label) {
  if (-not (Test-Path $PathToCheck)) {
    throw ("Brak {0}: {1}" -f $Label, $PathToCheck)
  }
}

function Reset-OutDir([string]$OutDir) {
  if ((Test-Path $OutDir) -and (-not $NoClean)) {
    Remove-Item $OutDir -Recurse -Force
  }
  New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
}

function Invoke-Publish([string]$ProjectPath, [string]$OutDir, [string]$Label) {
  Reset-OutDir $OutDir

  $args = @(
    "publish", $ProjectPath,
    "-c", $Configuration,
    "-o", $OutDir,
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:PublishTrimmed=false",
    "-p:PublishSingleFile=false"
  )

  if (-not [string]::IsNullOrWhiteSpace($Runtime)) {
    $args += @("-r", $Runtime)
  }

  if ($SelfContained) {
    if ([string]::IsNullOrWhiteSpace($Runtime)) {
      throw "Self-contained publish wymaga RuntimeIdentifier, np. -Runtime win-x64"
    }
    $args += @("--self-contained", "true")
  }
  elseif (-not [string]::IsNullOrWhiteSpace($Runtime)) {
    $args += @("--self-contained", "false")
  }

  if ($NoRestore) {
    $args += "--no-restore"
  }

  Write-Host ""
  Write-Host "[$Label] dotnet $($args -join ' ')" -ForegroundColor Cyan
  & dotnet @args

  if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for $Label (exit $LASTEXITCODE)"
  }

  $mainExe = Get-ChildItem -Path $OutDir -Filter *.exe -File -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -First 1
  if ($null -eq $mainExe) {
    throw "Publish zakonczyl sie bez pliku EXE w: $OutDir"
  }

  Write-Host "[$Label] OK -> $OutDir" -ForegroundColor Green
  Write-Host "[$Label] EXE -> $($mainExe.FullName)" -ForegroundColor Green
}

Assert-Tool "dotnet"
Assert-Path $AdminProj  "projektu Admin"
Assert-Path $WorkerProj "projektu Worker"

New-Item -ItemType Directory -Force -Path $PublishRoot | Out-Null

Write-Host "===============================================" -ForegroundColor Yellow
Write-Host " BilvaskRegistrering publish" -ForegroundColor Yellow
Write-Host " Configuration : $Configuration" -ForegroundColor Yellow
Write-Host " Runtime       : $Runtime" -ForegroundColor Yellow
Write-Host " SelfContained : $SelfContained" -ForegroundColor Yellow
Write-Host " Output root   : $PublishRoot" -ForegroundColor Yellow
Write-Host "===============================================" -ForegroundColor Yellow

Invoke-Publish -ProjectPath $AdminProj  -OutDir $AdminOut  -Label "Admin"
Invoke-Publish -ProjectPath $WorkerProj -OutDir $WorkerOut -Label "Worker"

Write-Host ""
Write-Host "DONE. Gotowe foldery:" -ForegroundColor Green
Write-Host " - $AdminOut"
Write-Host " - $WorkerOut"
Write-Host ""
Write-Host "Teraz mozesz kompilowac Inno Setup." -ForegroundColor Green
