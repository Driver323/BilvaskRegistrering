# BilvaskRegistrering (NET8) - Dahua ITSAPI (Platform Access)

Ten projekt NIE używa Dahua SDK. Zamiast tego odbiera tablice z kamery przez **Platform Access -> ITSAPI** (HTTP push), dokładnie jak na Twoim zrzucie.

## 1) Konfiguracja w kamerze (WEB UI)
Wejdź w **Network -> Platform Access -> ITSAPI** i ustaw:

- **Enable** = ON
- **Registration** = ON
- **Heartbeat** = ON
- **Platform Server** = `http://<IP_TWOJEGO_PC>:7070`
- **Heartbeat Interface** = `/NotificationInfo/KeepAlive` (może zostać)
- **Data Type** = zaznacz **ANPR Info**
- **ANPR Info Interface** = `/NotificationInfo/TollgateInfo`
- **Uploading Info** = zaznacz co najmniej **Plate No.** (i ewentualnie Time)

Zapisz: **Apply**.

## 2) Firewall / uprawnienia HTTP (WAŻNE)
Aplikacja nasłuchuje na `http://+:7070/`.

Jeśli kamera nie może się połączyć, dodaj URLACL w Windows (uruchom CMD jako Administrator):

```
netsh http add urlacl url=http://+:7070/ user=Everyone
```

(Jeśli już istnieje, usuń i dodaj ponownie.)

## 3) appsettings.json
W folderze z EXE jest `appsettings.json`:
- `UiConfig.VideoRtspUrl` – RTSP do podglądu
- `DahuaConfig.ItsApiPort` – domyślnie 7070
- `DahuaConfig.ItsApiPath` – domyślnie `/NotificationInfo/TollgateInfo`

## 4) Jak testować
1. Uruchom aplikację
2. Kliknij **Start ANPR (ITSAPI)**
3. Zrób test tablicą (auto / kartka) przed kamerą
4. Jeśli wszystko działa, w polu u góry pojawi się numer, a status pokaże ostatnie zdarzenie.

Jeśli nadal nic nie przychodzi:
- sprawdź czy **Platform Server** wskazuje IP Twojego PC (nie routera)
- sprawdź czy port 7070 nie jest blokowany przez firewall
- w statusie aplikacji pojawi się informacja o odebranych requestach (`ITSAPI: request ...`)
