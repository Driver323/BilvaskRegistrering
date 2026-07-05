#requires -version 5.1
<#
  publish_net8_3_wersje.ps1

  Publikuje projekt BilvaskRegistrering dla .NET 8 i przygotowuje 3 paczki:
    1) Serwer-Admin
    2) Admin bez DB
    3) Worker

  Domyslnie:
    - Configuration = Release
    - Runtime       = win-x64
    - SelfContained = true
    - Zip           = true

  Wyjscie:
    Installer\publish\Admin
    Installer\publish\Worker
    Installer\release\Server-Admin
    Installer\release\Admin-bez-DB
    Installer\release\Worker
    Installer\release\*.zip
#>

param(
  [ValidateSet("Debug","Release")]
  [string]$Configuration = "Release",

  [string]$Runtime = "win-x64",

  [switch]$FrameworkDependent,

  [switch]$NoClean,

  [switch]$NoRestore,

  [switch]$NoZip,

  [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$InstallerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SolutionDir  = Resolve-Path (Join-Path $InstallerDir "..")

$AdminProj  = Join-Path $SolutionDir "BilvaskRegistrering\BilvaskRegistrering.csproj"
$WorkerProj = Join-Path $SolutionDir "BilvaskRegistrering.Worker\BilvaskRegistrering.Worker.csproj"

$PublishRoot = Join-Path $InstallerDir "publish"
$ReleaseRoot = Join-Path $InstallerDir "release"
$AdminOut    = Join-Path $PublishRoot "Admin"
$WorkerOut   = Join-Path $PublishRoot "Worker"

$ServerAdminDir = Join-Path $ReleaseRoot "Server-Admin"
$AdminNoDbDir   = Join-Path $ReleaseRoot "Admin-bez-DB"
$WorkerDir      = Join-Path $ReleaseRoot "Worker"

$ToolsDir      = Join-Path $InstallerDir "tools"
$ServerSyncDir = Join-Path $SolutionDir "ServerSync"
$ReadmeDir     = Join-Path $InstallerDir "README_publish"

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
  if ($SkipPublish) {
    Assert-Path $OutDir "publikacji $Label"
    Write-Host "[$Label] SkipPublish => uzywam istniejacego folderu $OutDir" -ForegroundColor Yellow
    return
  }

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

function Copy-DirContent([string]$Source, [string]$Target) {
  if (-not (Test-Path $Source)) { return }
  New-Item -ItemType Directory -Force -Path $Target | Out-Null
  Copy-Item -Path (Join-Path $Source '*') -Destination $Target -Recurse -Force
}

function Write-TextFile([string]$Path, [string]$Content) {
  $dir = Split-Path -Parent $Path
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  Set-Content -Path $Path -Value $Content -Encoding UTF8
}

function Create-Zip([string]$SourceDir, [string]$ZipPath) {
  if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
  Compress-Archive -Path (Join-Path $SourceDir '*') -DestinationPath $ZipPath -Force
}

Assert-Tool "dotnet"
Assert-Path $AdminProj  "projektu Admin"
Assert-Path $WorkerProj "projektu Worker"

New-Item -ItemType Directory -Force -Path $PublishRoot | Out-Null
New-Item -ItemType Directory -Force -Path $ReleaseRoot | Out-Null

Write-Host "===============================================" -ForegroundColor Yellow
Write-Host " BilvaskRegistrering publish NET 8 / 3 wersje" -ForegroundColor Yellow
Write-Host " Configuration : $Configuration" -ForegroundColor Yellow
Write-Host " Runtime       : $Runtime" -ForegroundColor Yellow
Write-Host " SelfContained : $SelfContained" -ForegroundColor Yellow
Write-Host " Publish root  : $PublishRoot" -ForegroundColor Yellow
Write-Host " Release root  : $ReleaseRoot" -ForegroundColor Yellow
Write-Host "===============================================" -ForegroundColor Yellow

Invoke-Publish -ProjectPath $AdminProj  -OutDir $AdminOut  -Label "Admin"
Invoke-Publish -ProjectPath $WorkerProj -OutDir $WorkerOut -Label "Worker"

Reset-OutDir $ServerAdminDir
Reset-OutDir $AdminNoDbDir
Reset-OutDir $WorkerDir

# 1) Serwer-Admin
Copy-DirContent $AdminOut (Join-Path $ServerAdminDir 'Admin')
Copy-DirContent $ToolsDir (Join-Path $ServerAdminDir 'tools')
Copy-DirContent $ServerSyncDir (Join-Path $ServerAdminDir 'ServerSync')
Write-TextFile (Join-Path $ServerAdminDir 'README.txt') @"
BilvaskRegistrering - SERWER-ADMIN (.NET 8)

Zawartosc:
- Admin\              -> aplikacja BilvaskRegistrering (Admin)
- tools\              -> skrypty DB / runtime / post-install
- ServerSync\         -> synchronizacja CSV -> DB

To jest paczka dla komputera SERWERA.
Po uruchomieniu Admin na serwerze mozna pracowac lokalnie z PostgreSQL.
"@

# 2) Admin bez DB
Copy-DirContent $AdminOut (Join-Path $AdminNoDbDir 'Admin')
Write-TextFile (Join-Path $AdminNoDbDir 'README.txt') @"
BilvaskRegistrering - ADMIN bez DB (.NET 8)

Zawartosc:
- Admin\ -> aplikacja BilvaskRegistrering (Admin)

To jest paczka dla stanowiska klienckiego Admin.
DB jest na serwerze, a ten komputer ma laczyc sie z DB po runtime settings.
"@

# 3) Worker
Copy-DirContent $WorkerOut (Join-Path $WorkerDir 'Worker')
Write-TextFile (Join-Path $WorkerDir 'README.txt') @"
BilvaskRegistrering - WORKER (.NET 8)

Zawartosc:
- Worker\ -> aplikacja BilvaskRegistrering.Worker

To jest paczka dla stanowiska Worker.
Ustawienia DB, ITS API i dokumentfolder sa brane z runtime settings.
"@

$stamp = Get-Date -Format 'yyyyMMdd_HHmm'
if (-not $NoZip) {
  Create-Zip $ServerAdminDir (Join-Path $ReleaseRoot ("BilvaskRegistrering_Server-Admin_NET8_{0}.zip" -f $stamp))
  Create-Zip $AdminNoDbDir   (Join-Path $ReleaseRoot ("BilvaskRegistrering_Admin-bez-DB_NET8_{0}.zip" -f $stamp))
  Create-Zip $WorkerDir      (Join-Path $ReleaseRoot ("BilvaskRegistrering_Worker_NET8_{0}.zip" -f $stamp))
}

Write-Host "" 
Write-Host "DONE. Gotowe foldery:" -ForegroundColor Green
Write-Host " - $ServerAdminDir"
Write-Host " - $AdminNoDbDir"
Write-Host " - $WorkerDir"
if (-not $NoZip) {
  Write-Host "" 
  Write-Host "ZIP-y zapisane w: $ReleaseRoot" -ForegroundColor Green
}
