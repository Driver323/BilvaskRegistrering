param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here "..")

$publishDir = Join-Path $here "publish"
$adminOut = Join-Path $publishDir "Admin"
$workerOut = Join-Path $publishDir "Worker"

New-Item -ItemType Directory -Force -Path $adminOut | Out-Null
New-Item -ItemType Directory -Force -Path $workerOut | Out-Null

$sc = $false
if ($SelfContained) { $sc = $true }

Write-Host "Publishing Admin -> $adminOut"
dotnet publish (Join-Path $root "BilvaskRegistrering\BilvaskRegistrering.csproj") -c $Configuration -r $Runtime --self-contained:$sc -o $adminOut

Write-Host "Publishing Worker -> $workerOut"
dotnet publish (Join-Path $root "BilvaskRegistrering.Worker\BilvaskRegistrering.Worker.csproj") -c $Configuration -r $Runtime --self-contained:$sc -o $workerOut

Write-Host "DONE. Output: $publishDir"
