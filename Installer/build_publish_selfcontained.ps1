param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64"
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$script = Join-Path $here "build_publish.ps1"

Write-Host "Publishing self-contained (runtime included) ..."
powershell -NoProfile -ExecutionPolicy Bypass -File $script -Configuration $Configuration -Runtime $Runtime -SelfContained

Write-Host "DONE."
