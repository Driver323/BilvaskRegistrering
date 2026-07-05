# BilvaskRegistrering.Worker (wersja dla pracowników)

## Co to jest
Drugi program do monitorowania umytych pojazdów:
- pokazuje aktywną listę (widok `public.worker_active_washes`)
- pracownik wybiera siebie z listy `public.ansatter`
- kliknięcie **Jeg har vasket** zapisuje potwierdzenie do `public.wash_confirmations`

Program używa osobnego konta DB (`bilvask_worker_app`), które ma dostęp tylko do:
- SELECT: `public.worker_active_washes`
- SELECT: `public.ansatter`
- INSERT: `public.wash_confirmations`

## 1) SQL (jednorazowo, jako admin)
W folderze `BilvaskRegistrering.Worker/SQL/01_schema.sql` masz skrypt tworzący:
- `ansatter`
- `wash_confirmations`
- widok `worker_active_washes`

Uruchom go w pgAdmin w bazie: **bilvaskregistrering**.

## 2) Konfiguracja połączenia
W pliku `BilvaskRegistrering.Worker/appsettings.json` ustaw `ConnectionStrings:Worker`.

Możesz też nadpisać zmienną środowiskową:
- `BILVASK_WORKER_CONN`

## 3) Dodanie pracowników (przykład)
```sql
INSERT INTO public.ansatter (navn) VALUES
('Jan Kowalski'),
('Ola Nordmann');
```

## 4) Wskazówka
Jeśli Twoje `wash_events` mają inną strukturę (np. brak `vehicle_type`), popraw definicję widoku w SQL.
