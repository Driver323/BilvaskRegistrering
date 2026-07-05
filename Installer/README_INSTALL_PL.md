# BilvaskRegistrering v1.0 – przygotowanie instalatora (Windows)

Poniżej masz gotowy szablon instalatora **Inno Setup** + skrypty do:
- publikacji buildów (Admin + Worker),
- inicjalizacji PostgreSQL (utworzenie DB + role + schemat),
- wpisania `settings.runtime.json` do Dokumenty\BilvaskRegistrering,
- wymagania „kodu instalacyjnego” (deterrent / zabezpieczenie).

## Wymagania
1) Visual Studio 2022 + .NET 8 SDK
2) Inno Setup (darmowy)
3) PostgreSQL na komputerze docelowym (albo dołączysz instalator Postgres jako prerequisite)

## Szybki proces (recommended)
1. Otwórz PowerShell w folderze `Installer` i uruchom:
   - `./build_publish.ps1`
   To wygeneruje:
   - `Installer/publish/Admin/*`
   - `Installer/publish/Worker/*`

2. (Opcjonalnie) Wygeneruj kody instalacyjne:
   - `./keygen.ps1 -Count 10`

3. Zainstaluj Inno Setup i otwórz plik:
   - `Installer/Setup_BilvaskRegistrering_v1.0.iss`

4. Zbuduj instalator w Inno (Build).

## Co robi instalator
- Kopiuje pliki Admin/Worker do `Program Files\BilvaskRegistrering`.
- Pyta o **kod instalacyjny**.
- Pyta o parametry PostgreSQL (host/port + hasło użytkownika `postgres`).
- Uruchamia `tools/db_bootstrap.ps1`, który:
  - tworzy role `bilvask_admin_app` i `bilvask_worker_app`,
  - tworzy bazę `bilvask`,
  - aplikuje schemat `schema_full.sql`,
  - zapisuje `settings.runtime.json` (w tym DB + hasła UI + kod instalacyjny).

## Bezpieczeństwo (uczciwie)
- Kod instalacyjny to „zabezpieczenie” typu **deterrent**. Ktoś techniczny może obejść.
- Żeby było mocniej, można zrobić token podpisany RSA (public key w aplikacji) – jeśli chcesz, dodamy to.



## Kod instalacyjny (wariant RSA – offline)

### 0) Jednorazowa inicjalizacja kluczy (na komputerze build)

Jeśli to Twój pierwszy raz (nie masz jeszcze prywatnego klucza), uruchom:

```powershell
cd Installer
.\init_license_keys.ps1
```

To wygeneruje **private key poza repo** (domyślnie `%%USERPROFILE%%\.bilvask_license\private.xml`) oraz automatycznie zaktualizuje **klucz publiczny** w:
- `Installer\tools\license_verify.ps1` (instalator)
- `BilvaskRegistrering\Security\InstallCodeVerifier.cs` (Admin)
- `BilvaskRegistrering.Worker\Security\InstallCodeVerifier.cs` (Worker)

Dopiero potem generuj kody instalacyjne.


- Kody są podpisane kluczem prywatnym (RSA). Aplikacja i instalator mają wbudowany klucz publiczny.
- Plik z kluczem prywatnym (`Installer/_license_keys/private.xml`) trzymaj offline i NIGDY go nie wysyłaj klientowi.
- Generowanie kodów:

```powershell
cd Installer
.\keygen.ps1 -Count 5 -Expires "2030-12-31"
```

Wygenerowany kod wpisujesz podczas instalacji.


## Bezpieczeństwo klucza prywatnego (RSA)

- **Nigdy nie wysyłaj** pliku `private.xml` klientowi i nie trzymaj go w repo.

- Trzymaj klucz prywatny lokalnie na komputerze build, np.:

  `%USERPROFILE%\.bilvask_license\private.xml`

- `keygen.ps1` domyślnie użyje tej ścieżki. Możesz też podać:

  `./keygen.ps1 -PrivateKeyPath "D:\sekrety\bilvask\private.xml"`

- Do dystrybucji potrzebny jest tylko klucz publiczny (`public.xml` lub public key w skrypcie verify).

- Do budowania instalatora używaj `release_pack.ps1` — przerwie build, jeśli wykryje sekrety w paczce.


## Paczka do wysyłki (ZIP) – bezpiecznie

Po zbudowaniu instalatora w Inno Setup (powstaje `Installer/output/*.exe`) utwórz paczkę do wysłania klientowi:

```powershell
cd Installer
./make_release_zip.ps1 -Version "v1.0" -IncludeChecksums
```

Skrypt:
- skanuje **całe repo** pod kątem sekretów (private keys, .pfx, .snk, itp.) i przerwie działanie, jeśli coś znajdzie,
- pakuje **tylko** instalator EXE + instrukcję instalacji do `Installer/dist/*`.



### Generowanie kodów (ważne)

`keygen.ps1` **nie wygeneruje nowej pary kluczy** przypadkowo. Jeśli brakuje `private.xml`, skrypt przerwie pracę i poprosi o wskazanie pliku.


Jeśli naprawdę chcesz wygenerować NOWĄ parę kluczy (rotacja), użyj:

`./keygen.ps1 -GenerateNewKeyPair`


Po rotacji musisz zaktualizować klucz publiczny w `Installer/tools/license_verify.ps1` (i ewentualnie w verifierze aplikacji).

