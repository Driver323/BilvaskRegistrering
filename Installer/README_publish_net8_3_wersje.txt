publish_net8_3_wersje.ps1

Uruchomienie:
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\publish_net8_3_wersje.ps1

Opcje:
  -Configuration Release|Debug
  -Runtime win-x64
  -FrameworkDependent   # zamiast self-contained
  -NoClean
  -NoRestore
  -NoZip
  -SkipPublish          # tylko przepakuje istniejace publish\Admin i publish\Worker

Co robi:
  1. publikuje Admin i Worker dla .NET 8
  2. tworzy 3 wersje:
     - Server-Admin
     - Admin-bez-DB
     - Worker
  3. tworzy gotowe pliki ZIP w Installerelease
