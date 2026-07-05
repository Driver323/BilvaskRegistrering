@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0unblock_installed_files.ps1" -InstallDir "C:\Program Files\BilvaskRegistrering" -Interactive
endlocal
