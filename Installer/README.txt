Wklej te pliki do folderu Installer:
- Setup_BilvaskRegistrering_v2.1_NET8_selfcontained_UNBLOCK.iss
- tools\unblock_installed_files.ps1
- tools\unblock_bilvask_manual.cmd

Potem:
1) uruchom publish
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\release_pack_NET8_selfcontained_FIXED.ps1"
2) skompiluj installer
   & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" ".\Setup_BilvaskRegistrering_v2.1_NET8_selfcontained_UNBLOCK.iss"

Ta wersja po instalacji uruchamia Unblock-File na katalogu aplikacji i dodaje skrót do ręcznego odblokowania.
To pomaga na Zone.Identifier, ale nie zastępuje podpisu cyfrowego. SmartScreen nadal może ostrzegać przy niepodpisanych EXE.
