using BilvaskRegistrering.Dahua;
using BilvaskRegistrering.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using OpenCvSharp.Extensions;   // tylko konwersja Mat -> Bitmap
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.Configuration;
using System.Xml.Linq;
using System.IO.Compression;
using System.Diagnostics;
namespace BilvaskRegistrering
{
    // ---------------------------------------------
    // KONFIGURACJA APLIKACJI / KAMERY / ANPR
    // ---------------------------------------------
    internal static class AppConfig
{
    // Fired after runtime settings are saved (used for soft restart of camera preview)
    public static event Action? SettingsSaved;

    // Runtime settings:
    //  - Prefer ProgramData so every Admin PC can share the same DB config.
    //  - Keep Documents as a fallback (older installs / when ProgramData is not writable).
    private static readonly string _settingsFolderDocs =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BilvaskRegistrering");
    private static readonly string _settingsFileDocs =
        Path.Combine(_settingsFolderDocs, "settings.runtime.json");

    private static readonly string _settingsFolderProgramData =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BilvaskRegistrering");
    private static readonly string _settingsFileProgramData =
        Path.Combine(_settingsFolderProgramData, "settings.runtime.json");

    private static RuntimeSettings _settings = new RuntimeSettings();

    static AppConfig()
    {
        Directory.CreateDirectory(_settingsFolderDocs);
        try { Directory.CreateDirectory(_settingsFolderProgramData); } catch { /* ignore */ }
        LoadFromFile();
    }

    // --- mapowanie właściwości używanych przez resztę programu ---
    public static string DocFolder
    {
        get
        {
            var folder = _settings?.Dokument?.Folder;
            if (string.IsNullOrWhiteSpace(folder))
                folder = _settingsFolderDocs; // fallback

            return folder!;
        }
    }

    public static string CameraRtspUrl
    {
        get => _settings.Anpr.RtspUrl ?? "";
        private set => _settings.Anpr.RtspUrl = value ?? "";
    }

    // ----------------------
    // EXTRA PREVIEW CAMERAS (Kamera 2 / Kamera 3)
    // ----------------------
    public static CameraPreviewSettings Camera2Settings
    {
        get
        {
            _settings.PreviewCameras ??= new PreviewCamerasSettings();
            _settings.PreviewCameras.Camera2 ??= new CameraPreviewSettings();
            return _settings.PreviewCameras.Camera2;
        }
    }

    public static CameraPreviewSettings Camera3Settings
    {
        get
        {
            _settings.PreviewCameras ??= new PreviewCamerasSettings();
            _settings.PreviewCameras.Camera3 ??= new CameraPreviewSettings();
            return _settings.PreviewCameras.Camera3;
        }
    }

    public static string Camera2StreamUrl => BuildPreviewStreamUrl(Camera2Settings);
    public static string Camera3StreamUrl => BuildPreviewStreamUrl(Camera3Settings);

    private static string BuildPreviewStreamUrl(CameraPreviewSettings s)
    {
        if (s == null) return "";
        if (!s.Enabled) return "";

        var overrideUrl = s.RtspUrl?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(overrideUrl))
            return overrideUrl;

        var host = (s.Host ?? "").Trim();
        if (string.IsNullOrWhiteSpace(host)) return "";

        var protocol = (s.Protocol ?? "").Trim().ToLowerInvariant();

        var port = s.Port;
        if (port < 1 || port > 65535) port = 554;

        var path = (s.Path ?? "").Trim();
        if (string.IsNullOrWhiteSpace(path))
            path = protocol == "axis_http_mjpeg" ? "/axis-cgi/mjpg/video.cgi" : "/axis-media/media.amp";
        if (!path.StartsWith("/")) path = "/" + path;

        // IMPORTANT:
        // We intentionally do NOT embed username/password in the URL.
        // Axis devices often use Digest auth, and credentials in URL can break when password
        // contains special characters. We pass credentials as LibVLC / HTTP client options instead.

        // Protocol selection:
// - "axis_http_mjpeg" => HTTP MJPEG endpoint
// - otherwise => RTSP (default)
var useHttp = protocol == "axis_http_mjpeg";

if (useHttp)
{
    // Typical Axis MJPEG endpoint: /axis-cgi/mjpg/video.cgi?camera=N
    if (port == 554) port = 80;
    var url = $"http://{host}:{port}{path}";
    if (s.Channel > 0 && !url.Contains("camera=", StringComparison.OrdinalIgnoreCase))
        url += path.Contains("?") ? $"&camera={s.Channel}" : $"?camera={s.Channel}";
    return url;
}
else
{
    var url = $"rtsp://{host}:{port}{path}";
    // Axis devices often use ?camera=N for channel selection.
    if (s.Channel > 0 && !url.Contains("camera=", StringComparison.OrdinalIgnoreCase))
        url += path.Contains("?") ? $"&camera={s.Channel}" : $"?camera={s.Channel}";
    return url;
}

    }

    public static string PlateRecognizerApiToken
    {
        get => _settings.Anpr.ApiToken ?? "";
        private set => _settings.Anpr.ApiToken = value ?? "";
    }

    // Plate Recognizer endpoint (stały URL używany przez ANPR upload)
    public static string PlateRecognizerApiUrl =>
        "https://api.platerecognizer.com/v1/plate-reader/";

    public static string DahuaHost
    {
        get => _settings.Dahua.Host ?? "";
        private set => _settings.Dahua.Host = value ?? "";
    }

    public static int DahuaPort
    {
        get => _settings.Dahua.Port;
        private set => _settings.Dahua.Port = ClampPort(value, 37777);
    }

    public static string DahuaUsername
    {
        get => _settings.Dahua.User ?? "admin";
        private set => _settings.Dahua.User = string.IsNullOrWhiteSpace(value) ? "admin" : value;
    }

    public static string DahuaPassword
    {
        get => _settings.Dahua.Password ?? "";
        private set => _settings.Dahua.Password = value ?? "";
    }

    public static string ItsApiHostIp
    {
        get => _settings.ItsApi.Host ?? "0.0.0.0";
        set => _settings.ItsApi.Host = string.IsNullOrWhiteSpace(value) ? "0.0.0.0" : value;
    }

    public static int ItsApiPort
    {
        get => _settings.ItsApi.Port;
        set => _settings.ItsApi.Port = ClampPort(value, 7070);
    }

    public static string ItsApiPath
    {
        get => _settings.ItsApi.Path ?? "/NotificationInfo/TollgateInfo";
        set => _settings.ItsApi.Path = string.IsNullOrWhiteSpace(value) ? "/NotificationInfo/TollgateInfo" : value;
    }

    public static bool AutoRegisterOnPlate
    {
        get => _settings.Dokument.AutoRegisterOnPlate;
        set => _settings.Dokument.AutoRegisterOnPlate = value;
    }

    public static int DisplaySeconds
    {
        get => _settings.Dokument.DisplaySeconds;
        set => _settings.Dokument.DisplaySeconds = Math.Max(3, Math.Min(300, value));
    }

    // ----------------------
    // DATABASE (Azure Postgres) – values stored in settings.runtime.json
    // ----------------------
    public static bool DbEnabled
    {
        get => _settings.Database.Enabled;
        set => _settings.Database.Enabled = value;
    }

    public static string DbHost
    {
        get => _settings.Database.Host;
        set => _settings.Database.Host = value ?? "";
    }

    /// <summary>
    /// Separate host for the Worker app. If empty, falls back to <see cref="DbHost"/>.
    /// This makes it easy to point the Worker PCs at a different IP (e.g. Tailscale) than
    /// the Admin/management app.
    /// </summary>
    public static string DbWorkerHost
    {
        get => string.IsNullOrWhiteSpace(_settings.Database.WorkerHost) ? _settings.Database.Host : _settings.Database.WorkerHost;
        set => _settings.Database.WorkerHost = value ?? "";
    }

    public static int DbPort
    {
        get => _settings.Database.Port;
        set => _settings.Database.Port = ClampPort(value, 5432);
    }

    public static string DbName
    {
        get => _settings.Database.Database;
        set => _settings.Database.Database = value ?? "";
    }

    // Backwards-compatible alias used by some older UI code.
    public static string DbDatabase
    {
        get => DbName;
        set => DbName = value;
    }

    public static string DbAdminUser
    {
        get => _settings.Database.AdminUser;
        set => _settings.Database.AdminUser = value ?? "";
    }

    public static string DbAdminPassword
    {
        get => _settings.Database.AdminPassword;
        set => _settings.Database.AdminPassword = value ?? "";
    }

    public static string DbWorkerUser
    {
        get => _settings.Database.WorkerUser;
        set => _settings.Database.WorkerUser = value ?? "";
    }

    public static string DbWorkerPassword
    {
        get => _settings.Database.WorkerPassword;
        set => _settings.Database.WorkerPassword = value ?? "";
    }

    

    public static string DbSslMode
    {
        get
        {
            var mode = _settings.Database.SslMode?.Trim();

            // Safer default: prefer encrypted connection when possible, but still allow
            // fallback to non-SSL if the server does not require encryption.
            // This avoids pg_hba.conf errors like: "no encryption" on Admin PCs.
            if (string.IsNullOrWhiteSpace(mode))
                return "Prefer";

            return mode;
        }
        set => _settings.Database.SslMode = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    }

    private static bool IsPrivateOrLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        host = host.Trim();
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.StartsWith("127.")) return true;
        if (host.StartsWith("10.")) return true;
        if (host.StartsWith("192.168.")) return true;
        if (host.StartsWith("172.")) return true;
        // Tailscale IPv4 range
        if (host.StartsWith("100.")) return true;
        return false;
    }
public static bool DbTrustServerCertificate
    {
        get => _settings.Database.TrustServerCertificate;
        set => _settings.Database.TrustServerCertificate = value;
    }

public static int WorkerRefreshSeconds
    {
        get => _settings.WorkerUi.RefreshSeconds;
        set => _settings.WorkerUi.RefreshSeconds = Math.Max(1, Math.Min(3600, value));
    }

    public static bool WorkerShowOnlyUnconfirmed
    {
        get => _settings.WorkerUi.ShowOnlyUnconfirmed;
        set => _settings.WorkerUi.ShowOnlyUnconfirmed = value;
    }

public static string UiAdminPassword
{
    get => _settings?.Auth?.AdminPassword ?? "admin";
    set
    {
        _settings.Auth ??= new AuthSettings();
        _settings.Auth.AdminPassword = value ?? "";
    }
}

public static string UiWorkerPassword
{
    get => _settings?.Auth?.WorkerPassword ?? "worker";
    set
    {
        _settings.Auth ??= new AuthSettings();
        _settings.Auth.WorkerPassword = value ?? "";
    }
}

public static string BuildAdminConnectionString()
    {
        return $"Host={DbHost};Port={DbPort};Database={DbName};Username={DbAdminUser};Password={DbAdminPassword};Ssl Mode={DbSslMode};Trust Server Certificate={DbTrustServerCertificate.ToString().ToLowerInvariant()};";
    }

    public static string BuildWorkerConnectionString()
    {
        var host = string.IsNullOrWhiteSpace(DbWorkerHost) ? DbHost : DbWorkerHost;
        return $"Host={host};Port={DbPort};Database={DbName};Username={DbWorkerUser};Password={DbWorkerPassword};Ssl Mode={DbSslMode};Trust Server Certificate={DbTrustServerCertificate.ToString().ToLowerInvariant()};";
    }

    public static decimal PrisStorSommer
    {
        get => (decimal)_settings.Sesongpriser.StorSommer;
        set => _settings.Sesongpriser.StorSommer = (double)value;
    }
    public static decimal PrisStorVinter
    {
        get => (decimal)_settings.Sesongpriser.StorVinter;
        set => _settings.Sesongpriser.StorVinter = (double)value;
    }
    public static decimal PrisLitenSommer
    {
        get => (decimal)_settings.Sesongpriser.LitenSommer;
        set => _settings.Sesongpriser.LitenSommer = (double)value;
    }
    public static decimal PrisLitenVinter
    {
        get => (decimal)_settings.Sesongpriser.LitenVinter;
        set => _settings.Sesongpriser.LitenVinter = (double)value;
    }

    // --- Sesong datoer (fleksibelt) ---
    // Standard: Sommer fra 01.04, Vinter fra 01.10
    public static int SommerStartMonth
    {
        get => _settings.SesongDatoer?.SommerStartMonth is >= 1 and <= 12 ? _settings.SesongDatoer.SommerStartMonth : 4;
        set
        {
            _settings.SesongDatoer ??= new SesongDatoerSettings();
            _settings.SesongDatoer.SommerStartMonth = Math.Max(1, Math.Min(12, value));
            // keep day valid for the selected month
            _settings.SesongDatoer.SommerStartDay = ClampDay(_settings.SesongDatoer.SommerStartMonth, _settings.SesongDatoer.SommerStartDay);
        }
    }

    public static int SommerStartDay
    {
        get => _settings.SesongDatoer?.SommerStartDay is >= 1 and <= 31 ? _settings.SesongDatoer.SommerStartDay : 1;
        set
        {
            _settings.SesongDatoer ??= new SesongDatoerSettings();
            _settings.SesongDatoer.SommerStartDay = ClampDay(SommerStartMonth, value);
        }
    }

    public static int VinterStartMonth
    {
        get => _settings.SesongDatoer?.VinterStartMonth is >= 1 and <= 12 ? _settings.SesongDatoer.VinterStartMonth : 10;
        set
        {
            _settings.SesongDatoer ??= new SesongDatoerSettings();
            _settings.SesongDatoer.VinterStartMonth = Math.Max(1, Math.Min(12, value));
            _settings.SesongDatoer.VinterStartDay = ClampDay(_settings.SesongDatoer.VinterStartMonth, _settings.SesongDatoer.VinterStartDay);
        }
    }

    public static int VinterStartDay
    {
        get => _settings.SesongDatoer?.VinterStartDay is >= 1 and <= 31 ? _settings.SesongDatoer.VinterStartDay : 1;
        set
        {
            _settings.SesongDatoer ??= new SesongDatoerSettings();
            _settings.SesongDatoer.VinterStartDay = ClampDay(VinterStartMonth, value);
        }
    }

    /// <summary>
    /// Determines season for a local (Oslo) date using configurable cutover dates.
    /// Sommer: [SommerStart .. VinterStart), Vinter: otherwise.
    /// </summary>
    public static string DetermineSeason(DateTime localDate)
    {
        // Build cutover dates in the same year.
        var y = localDate.Year;
        var sommerStart = new DateTime(y, SommerStartMonth, ClampDay(SommerStartMonth, SommerStartDay));
        var vinterStart = new DateTime(y, VinterStartMonth, ClampDay(VinterStartMonth, VinterStartDay));

        // Typical case: SommerStart < VinterStart (e.g. 01.04 -> 01.10)
        if (sommerStart <= vinterStart)
        {
            return (localDate >= sommerStart && localDate < vinterStart) ? "Sommer" : "Vinter";
        }

        // If user sets an inverted window (rare), handle wrap-around.
        // Example: SommerStart=10.01, VinterStart=04.01
        return (localDate >= sommerStart || localDate < vinterStart) ? "Sommer" : "Vinter";
    }

    // --- aktualizacja z formularza ustawień ---
    public static void UpdateAll(
        string cameraUrl,
        string apiToken,
        CameraPreviewSettings? cam2,
        CameraPreviewSettings? cam3,
        string dahuaHost,
        int dahuaPort,
        string dahuaUsername,
        string dahuaPassword,
        int itsApiPort,
        string itsApiPath,
        string itsApiHostIp,
        string docFolder,
        bool autoRegisterOnPlate,
        int displaySeconds)
    {
        CameraRtspUrl = cameraUrl ?? "";
        PlateRecognizerApiToken = apiToken ?? "";

        // preview cameras
        try
        {
            _settings.PreviewCameras ??= new PreviewCamerasSettings();
            if (cam2 != null) _settings.PreviewCameras.Camera2 = cam2;
            if (cam3 != null) _settings.PreviewCameras.Camera3 = cam3;
        }
        catch { /* ignore */ }

        DahuaHost = dahuaHost ?? "";
        DahuaPort = dahuaPort;
        DahuaUsername = dahuaUsername;
        DahuaPassword = dahuaPassword ?? "";

        ItsApiPort = itsApiPort;
        ItsApiPath = itsApiPath;
        ItsApiHostIp = itsApiHostIp;

        if (!string.IsNullOrWhiteSpace(docFolder))
        {
            _settings.Dokument.Folder = docFolder;
            try { Directory.CreateDirectory(docFolder); } catch { }
        }

        AutoRegisterOnPlate = autoRegisterOnPlate;
        DisplaySeconds = displaySeconds;

        SaveToFile();
    }

    public static void UpdateCamera(string cameraUrl, string apiToken)
    {
        CameraRtspUrl = cameraUrl ?? "";
        PlateRecognizerApiToken = apiToken ?? "";
        SaveToFile();
    }

    // Backward compatible alias used by older parts of the UI
    public static void Update(string cameraUrl, string apiToken) =>
        UpdateCamera(cameraUrl, apiToken);

    public static void LoadFromFile()
    {
        try
        {
            Directory.CreateDirectory(_settingsFolderDocs);
            try { Directory.CreateDirectory(_settingsFolderProgramData); } catch { /* ignore */ }

            // Read BOTH runtime files when available.
            // Prefer the one that points to a non-local DB host, then the newest,
            // otherwise fall back to Documents (user-writable).
            var readPath = ResolvePreferredSettingsPath();

            // Migracja: jeśli istnieje AppConfig.ini (stare), a JSON nie istnieje
            var legacyIni = Path.Combine(_settingsFolderDocs, "AppConfig.ini");
            if (!File.Exists(_settingsFileProgramData) && !File.Exists(_settingsFileDocs) && File.Exists(legacyIni))
            {
                var migrated = RuntimeSettings.CreateDefault();
                foreach (var line in File.ReadAllLines(legacyIni))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("#")) continue;
                    var parts = trimmed.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    var key = parts[0].Trim();
                    var val = parts[1].Trim();

                    switch (key)
                    {
                        case "CameraRtspUrl": migrated.Anpr.RtspUrl = val; break;
                        case "PlateRecognizerApiToken": migrated.Anpr.ApiToken = val; break;
                        case "DahuaHost": migrated.Dahua.Host = val; break;
                        case "DahuaPort": if (int.TryParse(val, out var dp)) migrated.Dahua.Port = ClampPort(dp, 37777); break;
                        case "DahuaUsername": migrated.Dahua.User = val; break;
                        case "DahuaPassword": migrated.Dahua.Password = val; break;
                        case "ItsApiPort": if (int.TryParse(val, out var ip)) migrated.ItsApi.Port = ClampPort(ip, 7070); break;
                        case "ItsApiPath": migrated.ItsApi.Path = val; break;
                        case "ItsApiHostIp": migrated.ItsApi.Host = val; break;
                        case "DocFolder": migrated.Dokument.Folder = val; break;
                        case "AutoRegisterOnPlate": if (bool.TryParse(val, out var b)) migrated.Dokument.AutoRegisterOnPlate = b; break;
                        case "DisplaySeconds": if (int.TryParse(val, out var ds)) migrated.Dokument.DisplaySeconds = Math.Max(3, Math.Min(300, ds)); break;
                    }
                }
                _settings = migrated;
                SaveToFile();
                return;
            }

            if (!File.Exists(readPath))
            {
                _settings = RuntimeSettings.CreateDefault();
                SaveToFile();
                return;
            }

            var json = File.ReadAllText(readPath);
            var loaded = JsonSerializer.Deserialize<RuntimeSettings>(json);
            _settings = loaded ?? RuntimeSettings.CreateDefault();

            // sanity defaults
            _settings.Dahua.Port = ClampPort(_settings.Dahua.Port, 37777);
            _settings.ItsApi.Port = ClampPort(_settings.ItsApi.Port, 7070);
            _settings.Dokument.DisplaySeconds = Math.Max(3, Math.Min(300, _settings.Dokument.DisplaySeconds));
            _settings.Database.Port = ClampPort(_settings.Database.Port, 5432);
            _settings.WorkerUi.RefreshSeconds = Math.Max(1, Math.Min(3600, _settings.WorkerUi.RefreshSeconds));

            // preview cameras sanity
            _settings.PreviewCameras ??= new PreviewCamerasSettings();
            _settings.PreviewCameras.Camera2 ??= new CameraPreviewSettings();
            _settings.PreviewCameras.Camera3 ??= new CameraPreviewSettings();

            // protocol defaults (backward compatible)
            if (string.IsNullOrWhiteSpace(_settings.PreviewCameras.Camera2.Protocol)) _settings.PreviewCameras.Camera2.Protocol = "rtsp";
            if (string.IsNullOrWhiteSpace(_settings.PreviewCameras.Camera3.Protocol)) _settings.PreviewCameras.Camera3.Protocol = "rtsp";

            // If user selected Axis HTTP MJPEG, default port/path differ.
            var c2Proto = (_settings.PreviewCameras.Camera2.Protocol ?? "rtsp").Trim().ToLowerInvariant();
            var c3Proto = (_settings.PreviewCameras.Camera3.Protocol ?? "rtsp").Trim().ToLowerInvariant();

            _settings.PreviewCameras.Camera2.Port = ClampPort(_settings.PreviewCameras.Camera2.Port, c2Proto == "axis_http_mjpeg" ? 80 : 554);
            _settings.PreviewCameras.Camera3.Port = ClampPort(_settings.PreviewCameras.Camera3.Port, c3Proto == "axis_http_mjpeg" ? 80 : 554);

            if (string.IsNullOrWhiteSpace(_settings.PreviewCameras.Camera2.Path))
                _settings.PreviewCameras.Camera2.Path = c2Proto == "axis_http_mjpeg" ? "/axis-cgi/mjpg/video.cgi" : "/axis-media/media.amp";
            if (string.IsNullOrWhiteSpace(_settings.PreviewCameras.Camera3.Path))
                _settings.PreviewCameras.Camera3.Path = c3Proto == "axis_http_mjpeg" ? "/axis-cgi/mjpg/video.cgi" : "/axis-media/media.amp";

// IMPORTANT:
// Do not inject hard-coded preview camera defaults here.
// Installer / saved runtime settings should control whether Camera2/Camera3 are empty.

            // sanity for season cutover dates
            _settings.SesongDatoer ??= new SesongDatoerSettings();
            _settings.SesongDatoer.SommerStartMonth = Math.Max(1, Math.Min(12, _settings.SesongDatoer.SommerStartMonth));
            _settings.SesongDatoer.VinterStartMonth = Math.Max(1, Math.Min(12, _settings.SesongDatoer.VinterStartMonth));
            _settings.SesongDatoer.SommerStartDay = ClampDay(_settings.SesongDatoer.SommerStartMonth, _settings.SesongDatoer.SommerStartDay);
            _settings.SesongDatoer.VinterStartDay = ClampDay(_settings.SesongDatoer.VinterStartMonth, _settings.SesongDatoer.VinterStartDay);

            if (!string.IsNullOrWhiteSpace(_settings.Dokument.Folder))
            {
                try
                {
                    Directory.CreateDirectory(_settings.Dokument.Folder);
                }
                catch
                {
                    // UNC / share path may be temporarily unavailable. Keep settings and
                    // let repositories fall back gracefully instead of resetting everything.
                }
            }
        }
        catch
        {
            try
            {
                var bak1 = _settingsFileProgramData + ".bak";
                var bak2 = _settingsFileDocs + ".bak";
                var bak = File.Exists(bak1) ? bak1 : (File.Exists(bak2) ? bak2 : null);

                if (!string.IsNullOrWhiteSpace(bak) && File.Exists(bak!))
                {
                    var bakJson = File.ReadAllText(bak);
                    var bakLoaded = JsonSerializer.Deserialize<RuntimeSettings>(bakJson);
                    if (bakLoaded != null)
                    {
                        _settings = bakLoaded;
                        return;
                    }
                }
            }
            catch
            {
                // ignore and fall back to defaults below
            }

            _settings = RuntimeSettings.CreateDefault();
            SaveToFile();
        }
    }

    private static string ResolvePreferredSettingsPath()
    {
        bool pdExists = File.Exists(_settingsFileProgramData);
        bool docsExists = File.Exists(_settingsFileDocs);

        if (!pdExists && !docsExists)
            return _settingsFileDocs;
        if (pdExists && !docsExists)
            return _settingsFileProgramData;
        if (docsExists && !pdExists)
            return _settingsFileDocs;

        var pd = TryParseRuntimeFile(_settingsFileProgramData);
        var docs = TryParseRuntimeFile(_settingsFileDocs);

        if (pd is null && docs is null)
            return _settingsFileDocs;
        if (pd is null)
            return _settingsFileDocs;
        if (docs is null)
            return _settingsFileProgramData;

        bool pdLocal = LooksLikeLocalDb(pd);
        bool docsLocal = LooksLikeLocalDb(docs);

        if (!docsLocal && pdLocal)
            return _settingsFileDocs;
        if (!pdLocal && docsLocal)
            return _settingsFileProgramData;

        var pdWrite = SafeGetLastWriteUtc(_settingsFileProgramData);
        var docsWrite = SafeGetLastWriteUtc(_settingsFileDocs);

        if (docsWrite > pdWrite)
            return _settingsFileDocs;
        if (pdWrite > docsWrite)
            return _settingsFileProgramData;

        // On equal timestamps prefer ProgramData, because it is the intended shared/runtime location.
        return _settingsFileProgramData;
    }

    private static RuntimeSettings? TryParseRuntimeFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<RuntimeSettings>(json);
        }
        catch
        {
            return null;
        }
    }

    private static DateTime SafeGetLastWriteUtc(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private static bool LooksLikeLocalDb(RuntimeSettings s)
    {
        try
        {
            var host = s?.Database?.Host?.Trim() ?? "";
            var workerHost = s?.Database?.WorkerHost?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(workerHost))
                return true;

            if (string.IsNullOrWhiteSpace(workerHost))
                workerHost = host;

            return IsPrivateOrLocalHost(host) && IsPrivateOrLocalHost(workerHost);
        }
        catch
        {
            return true;
        }
    }

    private static void SaveToFile()
    {
        try
        {
            Directory.CreateDirectory(_settingsFolderDocs);
            try { Directory.CreateDirectory(_settingsFolderProgramData); } catch { /* ignore */ }

            // Keep ConnectionStrings in sync so the Worker app can reliably start even if it
            // reads only ConnectionStrings:Worker from settings.runtime.json.
            // (Without this, the default empty strings could override appsettings.json.)
            try
            {
                _settings.ConnectionStrings ??= new ConnectionStringsSettings();

                if (_settings.Database.Enabled &&
                    !string.IsNullOrWhiteSpace(_settings.Database.Host) &&
                    !string.IsNullOrWhiteSpace(_settings.Database.Database) &&
                    !string.IsNullOrWhiteSpace(_settings.Database.AdminUser) &&
                    !string.IsNullOrWhiteSpace(_settings.Database.WorkerUser))
                {
                    _settings.ConnectionStrings.Admin = BuildAdminConnectionString();
                    _settings.ConnectionStrings.Worker = BuildWorkerConnectionString();
                }
            }
            catch
            {
                // ignore – settings will still be written
            }

            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });

            // Write to ProgramData first (preferred), then Documents (fallback/backup)
            TryWriteRuntimeJson(_settingsFileProgramData, json);
            TryWriteRuntimeJson(_settingsFileDocs, json);
        }
        catch
        {
            // ignore
        }
    }

    private static void TryWriteRuntimeJson(string path, string json)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            // Atomic-ish write to avoid producing partial/corrupt JSON if the process is killed
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(path))
            {
                var bak = path + ".bak";
                try
                {
                    File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
                }
                catch
                {
                    File.Copy(tmp, path, overwrite: true);
                    File.Delete(tmp);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch
        {
            // ignore
        }
    }

    public static void Save()
    {
        SaveToFile();
        try { SettingsSaved?.Invoke(); } catch { /* ignore */ }
    }

    private static int ClampPort(int port, int fallback)
    {
        if (port < 1 || port > 65535) return fallback;
        return port;
    }

    private static int ClampDay(int month, int day)
    {
        try
        {
            var max = DateTime.DaysInMonth(2024, Math.Max(1, Math.Min(12, month))); // leap-year safe
            if (day < 1) return 1;
            if (day > max) return max;
            return day;
        }
        catch
        {
            return 1;
        }
    }
}

// ---------------------------------------------
// ANPR klient (PlateRecognizer) (PlateRecognizer)
    // ---------------------------------------------
    internal static class AnprClient
    {
        public static async Task<string> RecognizePlateAsync(byte[] imageBytes)
        {
            if (string.IsNullOrWhiteSpace(AppConfig.PlateRecognizerApiToken))
            {
                throw new InvalidOperationException(
                    "API-nøkkel for ANPR er ikke satt. Åpne 'Kamerainnstillinger...' og legg inn token.");
            }

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Token", AppConfig.PlateRecognizerApiToken);

                using (var form = new MultipartFormDataContent())
                {
                    var content = new ByteArrayContent(imageBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    form.Add(content, "upload", "frame.jpg");

                    var response = await client.PostAsync(AppConfig.PlateRecognizerApiUrl, form);
                    var json = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                        throw new Exception("Feil fra ANPR-tjenesten (" +
                                            (int)response.StatusCode + "): " + json);

                    return ExtractPlateFromJson(json);
                }
            }
        }

        // bardzo prosty parser JSON – szuka pierwszego pola "plate"
        private static string ExtractPlateFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";

            string key = "\"plate\"";
            int idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";

            idx = json.IndexOf(':', idx);
            if (idx < 0) return "";

            idx = json.IndexOf('"', idx);
            if (idx < 0) return "";

            int start = idx + 1;
            int end = json.IndexOf('"', start);
            if (end < 0 || end <= start) return "";

            return json.Substring(start, end - start);
        }
    }

    // ---------------------------------------------
    // KAMERA / ANPR – USTAWIENIA (RTSP + TOKEN)
    // ---------------------------------------------
    public class KameraOppsettForm : Form
    {
        private TextBox _txtRtsp;
        private TextBox _txtToken;
        private Button _btnOk;
        private Button _btnCancel;

        public KameraOppsettForm()
        {
            Text = "Kamerainnstillinger / ANPR";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(650, 220);
            Font = new Font(FontFamily.GenericSansSerif, 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            ByggUi();
            LastVerdier();
        }

        private void ByggUi()
        {
            int xLabel = 20;
            int xVal = 20;
            int y = 20;
            int dy = 50;

            var lblRtsp = new Label
            {
                Text = "RTSP-adresse til kamera:",
                Location = new Point(xLabel, y),
                AutoSize = true
            };
            _txtRtsp = new TextBox
            {
                Location = new Point(xVal, y + 18),
                Width = 600
            };
            y += dy;

            var lblToken = new Label
            {
                Text = "PlateRecognizer API-token:",
                Location = new Point(xLabel, y),
                AutoSize = true
            };
            _txtToken = new TextBox
            {
                Location = new Point(xVal, y + 18),
                Width = 600
            };
            y += dy;

            _btnOk = new Button
            {
                Text = "Lagre",
                Width = 100,
                Height = 30,
                Location = new Point(ClientSize.Width - 220, ClientSize.Height - 50),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnOk.Click += BtnOk_Click;

            _btnCancel = new Button
            {
                Text = "Avbryt",
                Width = 100,
                Height = 30,
                Location = new Point(ClientSize.Width - 110, ClientSize.Height - 50),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnCancel.Click += (s, e) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            Controls.Add(lblRtsp);
            Controls.Add(_txtRtsp);
            Controls.Add(lblToken);
            Controls.Add(_txtToken);
            Controls.Add(_btnOk);
            Controls.Add(_btnCancel);
        }

        private void LastVerdier()
        {
            _txtRtsp.Text = AppConfig.CameraRtspUrl;
            _txtToken.Text = AppConfig.PlateRecognizerApiToken;
        }

        private void BtnOk_Click(object sender, EventArgs e)

        {
            AppConfig.Update(_txtRtsp.Text.Trim(), _txtToken.Text.Trim());
            DialogResult = DialogResult.OK;
            Close();
        }
    }


    // ---------------------------------------------
    // MODELE
    // ---------------------------------------------

    public enum SelskapType
    {
        Ukjent = 0,
        Ambulanse,
        Hospitaldrift,
        Røde_Kors,
        Politi,
        Tide

    }

    public class KjoretoyInfo
    {
        public string Registreringsnummer { get; set; } = "";
        public SelskapType Selskap { get; set; } = SelskapType.Ukjent;
        public string TypeKjoretoy { get; set; } = "";
        public bool Unntak { get; set; }
        public string Kommentar { get; set; } = "";

        // ---- pola używane przez Egen Flåte ----
        public string Internnr { get; set; } = "";
        public string Merke { get; set; } = "";
        public string Vin { get; set; } = "";
        public string Lengde { get; set; } = "";
        /// <summary>Tekstowa nazwa firmy z pliku (np. "Tide")</summary>
        public string SelskapNavn { get; set; } = "";
    }




    public class VaskeHendelse
    {
        public int Id { get; set; }
        public DateTime DatoTid { get; set; }
        public string Registreringsnummer { get; set; } = "";
        public SelskapType Selskap { get; set; } = SelskapType.Ukjent;
        public string TypeKjoretoy { get; set; } = "";
        public string Sesong { get; set; } = "";
        public string Status { get; set; } = "";
        public decimal Kostnad { get; set; }

        // --- Compatibility aliases ---
        // PostgresSink (and other newer parts of the app) use a newer naming scheme
        // (OccurredAtUtc/Plate/VehicleType/Season/Cost). The UI layer originally used
        // the Norwegian property names above. These aliases keep both parts working
        // even if a file accidentally references the legacy VaskeHendelse type.
        public DateTime OccurredAtUtc
        {
            get => DatoTid;
            set => DatoTid = value;
        }

        public string Plate
        {
            get => Registreringsnummer;
            set => Registreringsnummer = value;
        }

        public string VehicleType
        {
            get => TypeKjoretoy;
            set => TypeKjoretoy = value;
        }

        public string Season
        {
            get => Sesong;
            set => Sesong = value;
        }

        public decimal Cost
        {
            get => Kostnad;
            set => Kostnad = value;
        }
    }

    public class EgenFirmaInfo
    {
        public string Navn { get; set; } = "";
        public string Avdeling { get; set; } = "";
        public string OrgNr { get; set; } = "";
        public string BedrNr { get; set; } = "";
        public string Adresse1 { get; set; } = "";
        public string Adresse2 { get; set; } = "";
        public string Telefon { get; set; } = "";
        public string Epost { get; set; } = "";
    }

    public class KundeFirmaInfo
    {
        public SelskapType Selskap { get; set; } = SelskapType.Ukjent;
        public string Navn { get; set; } = "";
        public string Adresse1 { get; set; } = "";
        public string Adresse2 { get; set; } = "";
        public string PostnrBy { get; set; } = "";
        public string Telefon { get; set; } = "";
        public string Epost { get; set; } = "";
        public string OrgNr { get; set; } = "";
        public string FakturaMerke { get; set; } = "";
    }

    public class FakturaArkivPost
    {
        public string FakturaNr { get; set; } = "";
        public DateTime Dato { get; set; }
        public SelskapType Selskap { get; set; } = SelskapType.Ukjent;
        public string KundeNavn { get; set; } = "";
        public DateTime PeriodeFra { get; set; }
        public DateTime PeriodeTil { get; set; }
        public int AntallVask { get; set; }
        public decimal Netto { get; set; }
        public decimal Mva { get; set; }
        public decimal Total { get; set; }
        public string PdfFil { get; set; } = "";
        public bool Annullert { get; set; }
    }

    // ---------------------------------------------
    // „EXCEL” – CSV
    // ---------------------------------------------
    public class ExcelRepository
    {
        private string _folder;
        private string _kjoretoyFile;
        private string _sesongPriserFile;
        private string _loggFile;
        private string _egenFirmaFile;
        private string _kundeFirmaFile;
        private string _logoFile;
        private string _fakturaArkivFile;
        private readonly CultureInfo _culture = new CultureInfo("nb-NO");
        private string _egenFlateFile;
        private readonly PlateDedupeWindow _logDedupe = new PlateDedupeWindow(TimeSpan.FromMinutes(30));

        public string LogoPath => _logoFile;
        public static string NormalizeRegnr(string regnr)
        {
            if (string.IsNullOrWhiteSpace(regnr))
                return "";

            // usuwamy spacje i myślniki, zamieniamy na wielkie litery
            var chars = regnr
                .Where(c => !char.IsWhiteSpace(c) && c != '-')
                .ToArray();

            return new string(chars).ToUpperInvariant();
        }

        public static bool IsStrictNorwegianPlate(string? regnr)
        {
            var s = NormalizeRegnr(regnr ?? "");
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (s.Length != 7) return false;
            if (!char.IsLetter(s[0]) || !char.IsLetter(s[1])) return false;
            for (int i = 2; i < 7; i++)
                if (!char.IsDigit(s[i])) return false;
            return true;
        }

        public ExcelRepository()
        {
            // Keep data together with the rest of the app docs (logo/images/config).
            _folder = AppConfig.DocFolder;

            _kjoretoyFile = Path.Combine(_folder, "Kjoretoyregister.csv");
            _egenFlateFile = Path.Combine(_folder, "EgenFlate.csv");   // <--- NOWE
            _sesongPriserFile = Path.Combine(_folder, "SesongPriser.csv");
            _loggFile = Path.Combine(_folder, "KameraLogg.csv");
            _egenFirmaFile = Path.Combine(_folder, "EgenFirma.csv");
            _kundeFirmaFile = Path.Combine(_folder, "KundeFirmaer.csv");
            _logoFile = Path.Combine(_folder, "Firmalogo.png");
            _fakturaArkivFile = Path.Combine(_folder, "FakturaArkiv.csv");

            EnsureFiles();
        }

        public void ReloadPathsFromAppConfig()
        {
            _folder = AppConfig.DocFolder;

            _kjoretoyFile = Path.Combine(_folder, "Kjoretoyregister.csv");
            _egenFlateFile = Path.Combine(_folder, "EgenFlate.csv");
            _sesongPriserFile = Path.Combine(_folder, "SesongPriser.csv");
            _loggFile = Path.Combine(_folder, "KameraLogg.csv");
            _egenFirmaFile = Path.Combine(_folder, "EgenFirma.csv");
            _kundeFirmaFile = Path.Combine(_folder, "KundeFirmaer.csv");
            _logoFile = Path.Combine(_folder, "Firmalogo.png");
            _fakturaArkivFile = Path.Combine(_folder, "FakturaArkiv.csv");

            EnsureFiles();
        }


        private void EnsureFiles()
        {
            try
            {
                if (!Directory.Exists(_folder))
                    Directory.CreateDirectory(_folder);
            }
            catch
            {
                // Network share unavailable or no permission. Fall back to local documents
                // so the app still starts without wiping runtime settings.
                _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BilvaskRegistrering");
                if (!Directory.Exists(_folder))
                    Directory.CreateDirectory(_folder);

                _kjoretoyFile = Path.Combine(_folder, "Kjoretoyregister.csv");
                _egenFlateFile = Path.Combine(_folder, "EgenFlate.csv");
                _sesongPriserFile = Path.Combine(_folder, "SesongPriser.csv");
                _loggFile = Path.Combine(_folder, "KameraLogg.csv");
                _egenFirmaFile = Path.Combine(_folder, "EgenFirma.csv");
                _kundeFirmaFile = Path.Combine(_folder, "KundeFirmaer.csv");
                _logoFile = Path.Combine(_folder, "Firmalogo.png");
                _fakturaArkivFile = Path.Combine(_folder, "FakturaArkiv.csv");
            }

            string snapshotFolder = Path.Combine(_folder, "Snapshots");
            try
            {
                if (!Directory.Exists(snapshotFolder))
                    Directory.CreateDirectory(snapshotFolder);
            }
            catch
            {
                // keep going - snapshots are optional
            }

            if (!File.Exists(_kjoretoyFile))
            {
                File.WriteAllLines(_kjoretoyFile, new[]
                {
                    "Registreringsnummer;Selskap;Type kjøretøy;Unntak;Kommentar",
                    "EF 15130;Ambulanser;Liten;false;",
                    "EF 38914;Ambulanser;Stor;false;"
                });
            }

            if (!File.Exists(_egenFlateFile))
            {
                File.WriteAllLines(_egenFlateFile, new[]
                {
        "Internnr;Registreringsnummer;Marke;VIN;Lengde;Selskap;Type kjøretøy;Unntak;Kommentar"
               });
            }

            if (!File.Exists(_sesongPriserFile))
            {
                if (!File.Exists(_sesongPriserFile))
                {
                    File.WriteAllLines(_sesongPriserFile, new[]
                    {
                        "Type;Sommer;Vinter",
                        "Stor;894,00;949,00",
                        "Liten;572,00;629,00"
                    });
                }

            }

            if (!File.Exists(_loggFile))
            {
                File.WriteAllLines(_loggFile, new[]
                {
                    "Id;DatoTid;Registreringsnummer;Selskap;TypeKjøretøy;Sesong;Status;BeregnetKostnad"
                });
            }

            if (!File.Exists(_egenFirmaFile))
            {
                File.WriteAllLines(_egenFirmaFile, new[]
                {
                    "Navn;Avdeling;OrgNr;BedrNr;Adresse1;Adresse2;Telefon;Epost",
                    "Tide Buss AS;avd. Bergen Sentrum;910 500 805;925 845 477;Nattlandsveien 91;5094 Bergen;;"
                });
            }

            if (!File.Exists(_kundeFirmaFile))
            {
                File.WriteAllLines(_kundeFirmaFile, new[]
                {
                    "Selskap;Navn;Adresse1;Adresse2;PostnrBy;Telefon;Epost;OrgNr;FakturaMerke",
                    "Ambulanser;Røde Kors Ambulansen Bergen;Sandbrekkevegen 95;5225 Nesttun;;+47 123 00 456;faktura@rkab.no;95990819203;A3034TL"
                });
            }

            if (!File.Exists(_fakturaArkivFile))
            {
                File.WriteAllLines(_fakturaArkivFile, new[]
                {
                    "FakturaNr;Dato;Selskap;KundeNavn;PeriodeFra;PeriodeTil;AntallVask;Netto;Mva;Total;PdfFil;Annullert"
                });
            }
        }

        private SelskapType ParseSelskap(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return SelskapType.Ukjent;

            s = s.Trim();

            if (s.Equals("Ambulanser", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("Ambulanse", StringComparison.OrdinalIgnoreCase))
                return SelskapType.Ambulanse;

            if (s.Equals("Hospitaldrifter", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("Hospitaldrift", StringComparison.OrdinalIgnoreCase))
                return SelskapType.Hospitaldrift;

            if (s.Equals("Politi", StringComparison.OrdinalIgnoreCase))
                return SelskapType.Politi;

            if (s.Equals("Røde Kors", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("Røde", StringComparison.OrdinalIgnoreCase))
                return SelskapType.Røde_Kors;

            // własna flåta – Tide jako osobny type
            if (s.Equals("Tide", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("Tide ", StringComparison.OrdinalIgnoreCase))
                return SelskapType.Tide;

            if (Enum.TryParse<SelskapType>(s, true, out var selskap))
                return selskap;

            return SelskapType.Ukjent;
        }




        private string FormatSelskap(SelskapType s)
        {
            switch (s)
            {
                case SelskapType.Ambulanse:
                    return "Ambulanser";
                case SelskapType.Hospitaldrift:
                    return "Hospitaldrifter";
                case SelskapType.Politi:
                    return "Politi";
                case SelskapType.Røde_Kors:
                    return "Røde Kors";
                case SelskapType.Tide:
                    return "Tide";
                default:
                    return "Ukjent";
            }
        }

        // ---- KJØRETØYREGISTER ----
        public List<KjoretoyInfo> LastAlleKjoretoy()
        {
            var liste = new List<KjoretoyInfo>();
            if (!File.Exists(_kjoretoyFile)) return liste;

            foreach (var line in File.ReadLines(_kjoretoyFile).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(';');
                if (parts.Length < 2) continue;

                string regnr = parts[0].Trim();
                if (string.IsNullOrWhiteSpace(regnr)) continue;

                string selskapStr = parts.Length > 1 ? parts[1].Trim() : "";
                string typeKj = parts.Length > 2 ? parts[2].Trim() : "";
                bool unntak = false;
                string kommentar = "";

                if (parts.Length == 4)
                {
                    kommentar = parts[3].Trim();
                }
                else if (parts.Length >= 5)
                {
                    bool.TryParse(parts[3].Trim(), out unntak);
                    kommentar = parts[4].Trim();
                }

                var info = new KjoretoyInfo
                {
                    Registreringsnummer = regnr,
                    Selskap = ParseSelskap(selskapStr),
                    TypeKjoretoy = typeKj,
                    Unntak = unntak,
                    Kommentar = kommentar,
                    SelskapNavn = selskapStr
                };


                liste.Add(info);
            }

            return liste;
        }

        public Dictionary<string, KjoretoyInfo> LastKjoretoyRegister()
        {
            var dict = new Dictionary<string, KjoretoyInfo>(StringComparer.OrdinalIgnoreCase);

            // 1) standardowy register
            foreach (var info in LastAlleKjoretoy())
            {
                if (!string.IsNullOrWhiteSpace(info.Registreringsnummer))
                {
                    var key = NormalizeRegnr(info.Registreringsnummer);
                    dict[key] = info;
                }
            }


            // 2) EgenFlate – uzupełnia / nadpisuje dane
            foreach (var info in LastAlleEgenFlateKjoretoy())
            {
                if (string.IsNullOrWhiteSpace(info.Registreringsnummer))
                    continue;

                var key = NormalizeRegnr(info.Registreringsnummer);
                if (dict.TryGetValue(key, out var existing))
                {
                    // Internnr i szczegóły floty
                    if (!string.IsNullOrWhiteSpace(info.Internnr))
                        existing.Internnr = info.Internnr;
                    if (!string.IsNullOrWhiteSpace(info.Merke))
                        existing.Merke = info.Merke;
                    if (!string.IsNullOrWhiteSpace(info.Vin))
                        existing.Vin = info.Vin;
                    if (!string.IsNullOrWhiteSpace(info.Lengde))
                        existing.Lengde = info.Lengde;

                    if (!string.IsNullOrWhiteSpace(info.SelskapNavn))
                    {
                        existing.SelskapNavn = info.SelskapNavn;
                        if (existing.Selskap == SelskapType.Ukjent)
                            existing.Selskap = info.Selskap;
                    }

                    if (string.IsNullOrWhiteSpace(existing.TypeKjoretoy) &&
                        !string.IsNullOrWhiteSpace(info.TypeKjoretoy))
                        existing.TypeKjoretoy = info.TypeKjoretoy;

                    if (!existing.Unntak && info.Unntak)
                        existing.Unntak = true;

                    if (string.IsNullOrWhiteSpace(existing.Kommentar) &&
                        !string.IsNullOrWhiteSpace(info.Kommentar))
                        existing.Kommentar = info.Kommentar;
                }
                else
                {
                    dict[key] = info;
                }
            }

            return dict;
        }


        public void LagreAlleKjoretoy(List<KjoretoyInfo> liste)
        {
            var lines = new List<string>
            {
                "Registreringsnummer;Selskap;Type kjøretøy;Unntak;Kommentar"
            };

            foreach (var info in liste)
            {
                string selskapNavn = FormatSelskap(info.Selskap);
                string line = string.Join(";", new[]
                {
                    info.Registreringsnummer ?? "",
                    selskapNavn,
                    info.TypeKjoretoy ?? "",
                    info.Unntak.ToString().ToLowerInvariant(),
                    info.Kommentar ?? ""
                });
                lines.Add(line);
            }

            File.WriteAllLines(_kjoretoyFile, lines);
        }

        public List<KjoretoyInfo> LastAlleEgenFlateKjoretoy()
        {
            var liste = new List<KjoretoyInfo>();
            if (!File.Exists(_egenFlateFile)) return liste;

            foreach (var line in File.ReadLines(_egenFlateFile).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split(';');
                if (parts.Length < 2) continue;

                var info = new KjoretoyInfo
                {
                    Internnr = parts.Length > 0 ? parts[0].Trim() : "",
                    Registreringsnummer = parts.Length > 1 ? parts[1].Trim() : "",
                    Merke = parts.Length > 2 ? parts[2].Trim() : "",
                    Vin = parts.Length > 3 ? parts[3].Trim() : "",
                    Lengde = parts.Length > 4 ? parts[4].Trim() : "",
                    SelskapNavn = parts.Length > 5 ? parts[5].Trim() : "",
                    TypeKjoretoy = parts.Length > 6 ? parts[6].Trim() : ""
                };

                bool unntak = false;
                if (parts.Length > 7)
                    bool.TryParse(parts[7].Trim(), out unntak);
                info.Unntak = unntak;

                if (parts.Length > 8)
                    info.Kommentar = parts[8].Trim();

                info.Selskap = ParseSelskap(info.SelskapNavn);

                liste.Add(info);
            }

            return liste;
        }

        public void LagreAlleEgenFlateKjoretoy(List<KjoretoyInfo> liste)
        {
            var lines = new List<string>
    {
        "Internnr;Registreringsnummer;Marke;VIN;Lengde;Selskap;Type kjøretøy;Unntak;Kommentar"
    };

            foreach (var info in liste)
            {
                string selskapNavn = string.IsNullOrWhiteSpace(info.SelskapNavn)
                    ? FormatSelskap(info.Selskap)
                    : info.SelskapNavn;

                string line = string.Join(";", new[]
                {
            info.Internnr ?? "",
            info.Registreringsnummer ?? "",
            info.Merke ?? "",
            info.Vin ?? "",
            info.Lengde ?? "",
            selskapNavn ?? "",
            info.TypeKjoretoy ?? "",
            info.Unntak.ToString().ToLowerInvariant(),
            info.Kommentar ?? ""
        });
                lines.Add(line);
            }

            File.WriteAllLines(_egenFlateFile, lines);
        }

        // ---- SESONGPRISER ----
        public Dictionary<string, decimal> LastSesongPriser()
        {
            var resultat = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(_sesongPriserFile)) return resultat;

            var linjer = File.ReadAllLines(_sesongPriserFile);
            if (linjer.Length < 2) return resultat;

            var header = linjer[0].Split(';');

            // NOWY FORMAT:
            // Type;Sommer;Vinter
            // Stor;894,00;949,00
            // Liten;572,00;629,00
            if (header.Length >= 2 && header[0].Trim().Equals("Type", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 1; i < linjer.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(linjer[i])) continue;

                    var parts = linjer[i].Split(';');
                    if (parts.Length < 2) continue;

                    string typeNavn = parts[0].Trim();
                    if (string.IsNullOrWhiteSpace(typeNavn)) continue;

                    for (int j = 1; j < header.Length && j < parts.Length; j++)
                    {
                        string sesongNavn = header[j].Trim();
                        if (string.IsNullOrWhiteSpace(sesongNavn)) continue;

                        if (decimal.TryParse(parts[j].Trim(), NumberStyles.Any, _culture, out var pris))
                        {
                            // klucze: "Sommer_Stor", "Vinter_Liten" itd.
                            string key = $"{sesongNavn}_{typeNavn}";
                            resultat[key] = pris;
                        }
                    }
                }
            }
            else
            {
                // STARY FORMAT – fallback:
                // Sommer;Vinter
                // 894,00;949,00
                var values = linjer[1].Split(';');

                for (int i = 0; i < header.Length && i < values.Length; i++)
                {
                    string navn = header[i].Trim();
                    if (string.IsNullOrWhiteSpace(navn)) continue;

                    if (decimal.TryParse(values[i].Trim(), NumberStyles.Any, _culture, out var pris))
                        resultat[navn] = pris;   // "Sommer", "Vinter"
                }
            }

            return resultat;
        }

        public void LagreSesongPriserDetaljert(
    decimal storSommer, decimal storVinter,
    decimal litenSommer, decimal litenVinter)
        {
            var lines = new[]
            {
        "Type;Sommer;Vinter",
        $"Stor;{storSommer.ToString(_culture)};{storVinter.ToString(_culture)}",
        $"Liten;{litenSommer.ToString(_culture)};{litenVinter.ToString(_culture)}"
    };

            try
            {
                var dir = Path.GetDirectoryName(_sesongPriserFile);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllLines(_sesongPriserFile, lines);
                return;
            }
            catch
            {
                // Network share may be read-only for some Admin PCs.
                // Fall back to a local copy so saving settings never fails completely.
            }

            var fallbackDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BilvaskRegistrering");
            Directory.CreateDirectory(fallbackDir);
            var fallbackFile = Path.Combine(fallbackDir, "SesongPriser.csv");
            File.WriteAllLines(fallbackFile, lines);
        }


        public void LagreSesongPriser(decimal sommer, decimal vinter)
        {
            if (!Directory.Exists(_folder))
                Directory.CreateDirectory(_folder);

            var lines = new[]
            {
        "Sommer;Vinter",
        sommer.ToString(_culture) + ";" + vinter.ToString(_culture)
    };

            File.WriteAllLines(_sesongPriserFile, lines);
        }
        // ---- LOGG ----
        public void LoggVask(VaskeHendelse hendelse)
        {
            var plateKey = NormalizeRegnr(hendelse.Registreringsnummer);

            // Last safety net: never save incomplete / invalid ANPR reads to KameraLogg.csv.
            // Example to ignore: "IF1134" (2 letters + only 4 digits).
            if (!IsStrictNorwegianPlate(plateKey))
                return;

            // Suppress repeated registrations of the same vehicle when it stays in front of the camera.
            // This keeps KameraLogg.csv clean (only 1 row per plate per 30 minutes).
            try
            {
                var utcNow = DateTime.SpecifyKind(hendelse.DatoTid, DateTimeKind.Local).ToUniversalTime();
                if (!_logDedupe.ShouldProcess(plateKey, utcNow))
                    return;
            }
            catch
            {
                // If dedupe fails for any reason, fall back to logging.
            }

            if (!File.Exists(_loggFile))
            {
                File.WriteAllLines(_loggFile, new[]
                {
                    "Id;DatoTid;Registreringsnummer;Selskap;TypeKjøretøy;Sesong;Status;BeregnetKostnad"
                });
            }

            int existing = File.ReadLines(_loggFile).Skip(1).Count();
            hendelse.Id = existing + 1;

            string line = string.Join(";", new[]
            {
                hendelse.Id.ToString(_culture),
                hendelse.DatoTid.ToString("yyyy-MM-dd HH:mm:ss", _culture),
                hendelse.Registreringsnummer ?? "",
                FormatSelskap(hendelse.Selskap),
                hendelse.TypeKjoretoy ?? "",
                hendelse.Sesong ?? "",
                hendelse.Status ?? "",
                hendelse.Kostnad.ToString(_culture)
            });

            File.AppendAllText(_loggFile, line + Environment.NewLine);
        }

        public List<VaskeHendelse> LastAlleVask()
        {
            // Når DB er aktiv: les vasker fra Postgres slik at Admin ser det Worker har lagret.
            // (CSV brukes fortsatt som lokal logg/back-up.)
            try
            {
                if (DbConfig.Enabled && !string.IsNullOrWhiteSpace(DbConfig.ConnectionString))
                {
                    var fraDb = LastAlleVaskFraDb(DbConfig.ConnectionString);
                    if (fraDb.Count > 0)
                        return fraDb;
                }

                // Fallback: if Admin DB settings are not enabled/complete, but Worker DB credentials exist,
                // allow Loggkontroll/rapporter to read the same wash_events from DB via the Worker connection.
                if ((!DbConfig.Enabled || string.IsNullOrWhiteSpace(DbConfig.ConnectionString)) &&
                    !string.IsNullOrWhiteSpace(AppConfig.DbHost) &&
                    !string.IsNullOrWhiteSpace(AppConfig.DbName) &&
                    !string.IsNullOrWhiteSpace(AppConfig.DbWorkerUser))
                {
                    var workerCs = AppConfig.BuildWorkerConnectionString();
                    if (!string.IsNullOrWhiteSpace(workerCs))
                    {
                        var fraDbWorker = LastAlleVaskFraDb(workerCs);
                        if (fraDbWorker.Count > 0)
                            return fraDbWorker;
                    }
                }
            }
            catch
            {
                // Hvis DB feiler / mangler kolonner, fall tilbake til CSV.
            }

            var liste = new List<VaskeHendelse>();
            if (!File.Exists(_loggFile)) return liste;

            foreach (var line in File.ReadLines(_loggFile).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(';');
                if (parts.Length < 8) continue;

                var h = new VaskeHendelse();

                if (int.TryParse(parts[0], NumberStyles.Integer, _culture, out var id))
                    h.Id = id;

                if (DateTime.TryParse(parts[1], _culture, DateTimeStyles.None, out var dt))
                    h.DatoTid = dt;
                else
                    h.DatoTid = DateTime.MinValue;

                h.Registreringsnummer = parts[2].Trim();
                if (!IsStrictNorwegianPlate(h.Registreringsnummer))
                    continue;

                h.Selskap = ParseSelskap(parts[3].Trim());
                h.TypeKjoretoy = parts[4].Trim();
                h.Sesong = parts[5].Trim();
                h.Status = parts[6].Trim();

                if (decimal.TryParse(parts[7], NumberStyles.Any, _culture, out var kost))
                    h.Kostnad = kost;

                liste.Add(h);
            }

            return liste;
        }

        private List<VaskeHendelse> LastAlleVaskFraDb(string connectionString)
        {
            var liste = new List<VaskeHendelse>();
            if (string.IsNullOrWhiteSpace(connectionString)) return liste;

            // Best-effort fallbacks from local CSV files (so older DB schemas can still show company/type/cost)
            Dictionary<string, KjoretoyInfo>? register = null;
            Dictionary<string, decimal>? sesongPriser = null;
            try { register = LastKjoretoyRegister(); } catch { /* ignore */ }
            try { sesongPriser = LastSesongPriser(); } catch { /* ignore */ }

            using (var conn = new Npgsql.NpgsqlConnection(connectionString))
            {
                conn.Open();

                // Detect schema variants (new vs legacy) and optional columns.
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using var colCmd = new Npgsql.NpgsqlCommand(@"
SELECT column_name
FROM information_schema.columns
WHERE table_schema='public' AND table_name='wash_events';", conn);
                    using var cr = colCmd.ExecuteReader();
                    while (cr.Read())
                    {
                        if (!cr.IsDBNull(0))
                            cols.Add(cr.GetString(0));
                    }
                }
                catch
                {
                    // If we cannot inspect schema, we still try the newest query below.
                }

                // timestamp column (new: occurred_at, legacy: occurred_at_utc)
                string tsCol = cols.Contains("occurred_at") ? "occurred_at"
                    : cols.Contains("occurred_at_utc") ? "occurred_at_utc"
                    : cols.Contains("ts") ? "ts"
                    : "occurred_at";
                bool tsIsUtc = tsCol.Equals("occurred_at_utc", StringComparison.OrdinalIgnoreCase);

                // plate column (new: plate, legacy: registreringsnummer)
                string plateCol = cols.Contains("plate") ? "plate"
                    : cols.Contains("registreringsnummer") ? "registreringsnummer"
                    : "plate";

                // Optional fields (use NULL/empty placeholders when columns are missing)
                string selskapExpr = cols.Contains("selskap") ? "COALESCE(selskap,'')" : "''::text";

                string vehicleExpr = cols.Contains("vehicle_type") ? "COALESCE(vehicle_type,'')"
                    : cols.Contains("type_kjoretoy") ? "COALESCE(type_kjoretoy,'')"
                    : "''::text";

                string seasonExpr = cols.Contains("season") ? "COALESCE(season,'')" : "''::text";
                string statusExpr = cols.Contains("status") ? "COALESCE(status,'')" : "''::text";

                string costExpr = cols.Contains("cost") ? "cost"
                    : cols.Contains("beregnetkostnad") ? "beregnetkostnad"
                    : "NULL::numeric";

                // Use only columns that exist. This keeps Loggkontroll working even with older DB schemas.
                string sql = $@"
                SELECT
                  id,
                  {tsCol} AS ts,
                  {plateCol} AS plate,
                  {selskapExpr} AS selskap,
                  {vehicleExpr} AS vehicle_type,
                  {seasonExpr} AS season,
                  {statusExpr} AS status,
                  {costExpr} AS cost
                FROM public.wash_events
                WHERE regexp_replace({plateCol}, '\s', '', 'g') ~ '^[A-Z]{2}[0-9]{5}$'
                ORDER BY {tsCol} DESC;";

                using (var cmd = new Npgsql.NpgsqlCommand(sql, conn))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var h = new VaskeHendelse();

                        // id (BIGSERIAL) -> int
                        try
                        {
                            var id = r.GetInt64(0);
                            h.Id = id > int.MaxValue ? int.MaxValue : (int)id;
                        }
                        catch { h.Id = 0; }

                        // ts -> local DateTime (prefer DateTimeOffset for timestamptz)
                        try
                        {
                            if (!r.IsDBNull(1))
                            {
                                DateTime dtLocal;
                                try
                                {
                                    var dto = r.GetFieldValue<DateTimeOffset>(1);
                                    dtLocal = dto.LocalDateTime;
                                }
                                catch
                                {
                                    var dt = r.GetDateTime(1);
                                    if (tsIsUtc) dtLocal = DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
                                    else if (dt.Kind == DateTimeKind.Utc) dtLocal = dt.ToLocalTime();
                                    else dtLocal = dt;
                                }
                                h.DatoTid = dtLocal;
                            }
                            else h.DatoTid = DateTime.MinValue;
                        }
                        catch { h.DatoTid = DateTime.MinValue; }

                        var plateRaw = (r.IsDBNull(2) ? "" : r.GetString(2));
                        h.Registreringsnummer = FormatPlateForDisplayLoose(plateRaw);

                        var selskapStr = (r.IsDBNull(3) ? "" : r.GetString(3));
                        h.Selskap = ParseSelskap(selskapStr);

                        h.TypeKjoretoy = r.IsDBNull(4) ? "" : r.GetString(4);
                        h.Sesong = r.IsDBNull(5) ? "" : r.GetString(5);
                        h.Status = r.IsDBNull(6) ? "" : r.GetString(6);

                        if (!r.IsDBNull(7))
                        {
                            try { h.Kostnad = r.GetDecimal(7); }
                            catch { /* ignore */ }
                        }

                        // Fill missing company/type/status from local register (CSV) if available.
                        try
                        {
                            var key = NormalizeRegnr(plateRaw);
                            if (register != null && register.TryGetValue(key, out var info) && info != null)
                            {
                                if (h.Selskap == SelskapType.Ukjent && info.Selskap != SelskapType.Ukjent)
                                    h.Selskap = info.Selskap;

                                if (!string.IsNullOrWhiteSpace(info.TypeKjoretoy))
                                {
                                    // Prefer local register as the source of truth.
                                    // This fixes cases where DB defaulted vehicle_type (e.g. 'Buss') for Worker inserts.
                                    if (!string.Equals(h.TypeKjoretoy?.Trim(), info.TypeKjoretoy.Trim(), StringComparison.OrdinalIgnoreCase))
                                    {
                                        h.TypeKjoretoy = info.TypeKjoretoy;
                                        // Force cost recompute later if it was derived from a wrong vehicle type.
                                        h.Kostnad = 0m;
                                    }
                                }

                                if (string.IsNullOrWhiteSpace(h.Status))
                                    h.Status = info.Unntak ? "Unntak" : "Normal";
                            }
                        }
                        catch { /* ignore */ }

                        // Best-effort season fallback (DB might not have it in older schemas)
                        if (string.IsNullOrWhiteSpace(h.Sesong))
                        {
                            var d = (h.DatoTid == DateTime.MinValue ? DateTime.Now : h.DatoTid);
                            h.Sesong = AppConfig.DetermineSeason(d);
                        }

                        // Compute cost if DB doesn't store it (AdminDbEventSink does not write cost).
                        // Unntak is always 0.
                        if (!string.IsNullOrWhiteSpace(h.Status) && h.Status.Equals("Unntak", StringComparison.OrdinalIgnoreCase))
                        {
                            h.Kostnad = 0m;
                        }
                        else if (h.Kostnad == 0m && sesongPriser != null)
                        {
                            try
                            {
                                var pris = HentSesongPrisLocal(sesongPriser, h.Sesong, h.TypeKjoretoy);
                                if (pris > 0m) h.Kostnad = pris;
                            }
                            catch { /* ignore */ }
                        }

                        liste.Add(h);
                    }
                }
            }

            return liste;
        }

        private static decimal HentSesongPrisLocal(Dictionary<string, decimal> priser, string sesong, string typeKjoretoy)
        {
            if (priser == null) return 0m;

            string s = (sesong ?? "").Trim();
            string type = (typeKjoretoy ?? "").Trim();

            string typeKey = "";
            if (!string.IsNullOrEmpty(type))
            {
                if (type.IndexOf("stor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    type.IndexOf("buss", StringComparison.OrdinalIgnoreCase) >= 0)
                    typeKey = "Stor";
                else if (type.IndexOf("liten", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         type.IndexOf("person", StringComparison.OrdinalIgnoreCase) >= 0)
                    typeKey = "Liten";
                else
                    typeKey = type;
            }

            // detailed keys: "Sommer_Stor", "Vinter_Liten", ...
            if (!string.IsNullOrEmpty(s) && !string.IsNullOrEmpty(typeKey))
            {
                string k = $"{s}_{typeKey}";
                if (priser.TryGetValue(k, out var prisCombo))
                    return prisCombo;
            }

            // legacy keys: only "Sommer" / "Vinter"
            if (!string.IsNullOrEmpty(s) && priser.TryGetValue(s, out var pris))
                return pris;

            return 0m;
        }

        private static string FormatPlateForDisplayLoose(string reg)
        {
            var raw = NormalizeRegnr(reg);
            if (raw.Length == 7 && char.IsLetter(raw[0]) && char.IsLetter(raw[1]) && raw.Skip(2).All(char.IsDigit))
                return raw.Substring(0, 2) + " " + raw.Substring(2);
            return string.IsNullOrWhiteSpace(reg) ? raw : reg;
        }

        // ---- EGET FIRMA ----
        public EgenFirmaInfo LastEgenFirma()
        {
            var info = new EgenFirmaInfo();

            if (!File.Exists(_egenFirmaFile))
                return info;

            var linjer = File.ReadAllLines(_egenFirmaFile);
            if (linjer.Length < 2) return info;

            var parts = linjer[1].Split(';');
            if (parts.Length > 0) info.Navn = parts[0].Trim();
            if (parts.Length > 1) info.Avdeling = parts[1].Trim();
            if (parts.Length > 2) info.OrgNr = parts[2].Trim();
            if (parts.Length > 3) info.BedrNr = parts[3].Trim();
            if (parts.Length > 4) info.Adresse1 = parts[4].Trim();
            if (parts.Length > 5) info.Adresse2 = parts[5].Trim();
            if (parts.Length > 6) info.Telefon = parts[6].Trim();
            if (parts.Length > 7) info.Epost = parts[7].Trim();

            return info;
        }

        public void LagreEgenFirma(EgenFirmaInfo info)
        {
            var line = string.Join(";", new[]
            {
                info.Navn ?? "",
                info.Avdeling ?? "",
                info.OrgNr ?? "",
                info.BedrNr ?? "",
                info.Adresse1 ?? "",
                info.Adresse2 ?? "",
                info.Telefon ?? "",
                info.Epost ?? ""
            });

            File.WriteAllLines(_egenFirmaFile, new[]
            {
                "Navn;Avdeling;OrgNr;BedrNr;Adresse1;Adresse2;Telefon;Epost",
                line
            });
        }

        // ---- KUNDEFIRMAER ----
        public List<KundeFirmaInfo> LastAlleKundeFirma()
        {
            var liste = new List<KundeFirmaInfo>();
            if (!File.Exists(_kundeFirmaFile)) return liste;

            foreach (var line in File.ReadLines(_kundeFirmaFile).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(';');

                var info = new KundeFirmaInfo();
                if (parts.Length > 0)
                    info.Selskap = ParseSelskap(parts[0]);
                if (parts.Length > 1)
                    info.Navn = parts[1].Trim();
                if (parts.Length > 2)
                    info.Adresse1 = parts[2].Trim();
                if (parts.Length > 3)
                    info.Adresse2 = parts[3].Trim();
                if (parts.Length > 4)
                    info.PostnrBy = parts[4].Trim();
                if (parts.Length > 5)
                    info.Telefon = parts[5].Trim();
                if (parts.Length > 6)
                    info.Epost = parts[6].Trim();
                if (parts.Length > 7)
                    info.OrgNr = parts[7].Trim();
                if (parts.Length > 8)
                    info.FakturaMerke = parts[8].Trim();

                liste.Add(info);
            }

            return liste;
        }

        public void LagreAlleKundeFirma(List<KundeFirmaInfo> liste)
        {
            var lines = new List<string>
            {
                "Selskap;Navn;Adresse1;Adresse2;PostnrBy;Telefon;Epost;OrgNr;FakturaMerke"
            };

            foreach (var info in liste)
            {
                string selskapNavn = FormatSelskap(info.Selskap);
                string line = string.Join(";", new[]
                {
                    selskapNavn,
                    info.Navn ?? "",
                    info.Adresse1 ?? "",
                    info.Adresse2 ?? "",
                    info.PostnrBy ?? "",
                    info.Telefon ?? "",
                    info.Epost ?? "",
                    info.OrgNr ?? "",
                    info.FakturaMerke ?? ""
                });
                lines.Add(line);
            }

            File.WriteAllLines(_kundeFirmaFile, lines);
        }

        // ---- FAKTURA-ARKIV ----
        public List<FakturaArkivPost> LastFakturaArkiv()
        {
            var liste = new List<FakturaArkivPost>();
            if (!File.Exists(_fakturaArkivFile)) return liste;

            foreach (var line in File.ReadLines(_fakturaArkivFile).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(';');
                if (parts.Length < 4) continue;

                var p = new FakturaArkivPost();
                int i = 0;

                if (parts.Length > i) p.FakturaNr = parts[i++].Trim();

                if (parts.Length > i)
                {
                    if (DateTime.TryParse(parts[i++].Trim(), out var d))
                        p.Dato = d;
                }

                if (parts.Length > i)
                    p.Selskap = ParseSelskap(parts[i++]);

                if (parts.Length > i)
                    p.KundeNavn = parts[i++].Trim();

                if (parts.Length > i)
                {
                    if (DateTime.TryParse(parts[i++].Trim(), out var d))
                        p.PeriodeFra = d;
                }

                if (parts.Length > i)
                {
                    if (DateTime.TryParse(parts[i++].Trim(), out var d))
                        p.PeriodeTil = d;
                }

                if (parts.Length > i)
                {
                    if (int.TryParse(parts[i++].Trim(), out var a))
                        p.AntallVask = a;
                }

                if (parts.Length > i)
                {
                    if (decimal.TryParse(parts[i++].Trim(), NumberStyles.Any, _culture, out var v))
                        p.Netto = v;
                }

                if (parts.Length > i)
                {
                    if (decimal.TryParse(parts[i++].Trim(), NumberStyles.Any, _culture, out var v))
                        p.Mva = v;
                }

                if (parts.Length > i)
                {
                    if (decimal.TryParse(parts[i++].Trim(), NumberStyles.Any, _culture, out var v))
                        p.Total = v;
                }

                if (parts.Length > i)
                    p.PdfFil = parts[i++].Trim();

                if (parts.Length > i)
                {
                    if (bool.TryParse(parts[i++].Trim(), out var b))
                        p.Annullert = b;
                }

                liste.Add(p);
            }

            return liste;
        }

        public void LagreFakturaArkiv(List<FakturaArkivPost> liste)
        {
            var lines = new List<string>
            {
                "FakturaNr;Dato;Selskap;KundeNavn;PeriodeFra;PeriodeTil;AntallVask;Netto;Mva;Total;PdfFil;Annullert"
            };

            foreach (var p in liste)
            {
                string line = string.Join(";", new[]
                {
                    p.FakturaNr ?? "",
                    p.Dato.ToString("yyyy-MM-dd", _culture),
                    FormatSelskap(p.Selskap),
                    p.KundeNavn ?? "",
                    p.PeriodeFra.ToString("yyyy-MM-dd", _culture),
                    p.PeriodeTil.ToString("yyyy-MM-dd", _culture),
                    p.AntallVask.ToString(_culture),
                    p.Netto.ToString(_culture),
                    p.Mva.ToString(_culture),
                    p.Total.ToString(_culture),
                    p.PdfFil ?? "",
                    p.Annullert.ToString().ToLowerInvariant()
                });

                lines.Add(line);
            }

            File.WriteAllLines(_fakturaArkivFile, lines);
        }

        public void LeggTilFakturaArkiv(FakturaArkivPost post)
        {
            if (!File.Exists(_fakturaArkivFile))
            {
                File.WriteAllLines(_fakturaArkivFile, new[]
                {
                    "FakturaNr;Dato;Selskap;KundeNavn;PeriodeFra;PeriodeTil;AntallVask;Netto;Mva;Total;PdfFil;Annullert"
                });
            }

            string line = string.Join(";", new[]
            {
                post.FakturaNr ?? "",
                post.Dato.ToString("yyyy-MM-dd", _culture),
                FormatSelskap(post.Selskap),
                post.KundeNavn ?? "",
                post.PeriodeFra.ToString("yyyy-MM-dd", _culture),
                post.PeriodeTil.ToString("yyyy-MM-dd", _culture),
                post.AntallVask.ToString(_culture),
                post.Netto.ToString(_culture),
                post.Mva.ToString(_culture),
                post.Total.ToString(_culture),
                post.PdfFil ?? "",
                post.Annullert.ToString().ToLowerInvariant()
            });

            File.AppendAllText(_fakturaArkivFile, line + Environment.NewLine);
        }
    }

    // ---------------------------------------------
    // STATYSTYKA
    // ---------------------------------------------
    public class StatistikkService
    {
        private readonly ExcelRepository _repo;
        public StatistikkService(ExcelRepository repo) { _repo = repo; }

        public IEnumerable<(int Year, int Month, SelskapType Company, int Count, decimal TotalCost)>
            LagMånedsStatistikk()
        {
            var alle = _repo.LastAlleVask();

            var query = alle
                .Where(h => h.DatoTid != DateTime.MinValue)
                .GroupBy(h => new { h.DatoTid.Year, h.DatoTid.Month, h.Selskap })
                .Select(g => (
                    Year: g.Key.Year,
                    Month: g.Key.Month,
                    Company: g.Key.Selskap,
                    Count: g.Count(),
                    TotalCost: g.Sum(x => x.Kostnad)
                ))
                .OrderBy(x => x.Year).ThenBy(x => x.Month).ThenBy(x => x.Company);

            return query;
        }
    }

    // ---------------------------------------------
    // USTAWIENIA UKŁADU UI
    // ---------------------------------------------
    public sealed class UiLayoutSettings
    {
        // Left panel
        public int LeftPanelWidthPx { get; set; } = 0; // 0 = auto percent
        public double LeftPanelPercent { get; set; } = 0.26;
        public int LeftPanelMinPx { get; set; } = 340;
        public int LeftPanelMaxPx { get; set; } = 480;

        // Keep enough room for cameras
        public int RightPanelMinPx { get; set; } = 760;

        // Heights
        public int HeaderHeightPx { get; set; } = 0; // 0 = auto
        public int PlateHeightPx { get; set; } = 0; // 0 = auto
        public int BottomBarHeightPx { get; set; } = 90;

        // Cameras
        public double CameraMainPercent { get; set; } = 0.58;
        public string Cam1Mode { get; set; } = "crop"; // crop (fill) or fit
        public string Cam2Mode { get; set; } = "crop";
        public string Cam3Mode { get; set; } = "crop";

        // Fonts (0 = auto scale)
        public float HeaderTitleFontSize { get; set; } = 0f;
        public float InternnrFontSize { get; set; } = 0f;
        public float InfoFontSize { get; set; } = 0f;

        // Overlay limits (0 = default)
        public float Overlay1Max { get; set; } = 0f;
        public float Overlay1Min { get; set; } = 0f;
        public float Overlay2Max { get; set; } = 0f;
        public float Overlay2Min { get; set; } = 0f;
        public float Overlay3Max { get; set; } = 0f;
        public float Overlay3Min { get; set; } = 0f;

        public static UiLayoutSettings CreateDefault() => new UiLayoutSettings();

        public static string GetLayoutPath(string? folder)
        {
            var fallbackDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BilvaskRegistrering");
            var baseDir = folder;
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir))
            {
                baseDir = fallbackDir;
            }

            try
            {
                Directory.CreateDirectory(baseDir);
            }
            catch
            {
                baseDir = fallbackDir;
                Directory.CreateDirectory(baseDir);
            }

            return Path.Combine(baseDir, "ui.layout.json");
        }

        public static UiLayoutSettings Load(string? folder)
        {
            var path = GetLayoutPath(folder);
            if (!File.Exists(path))
                return CreateDefault();

            try
            {
                var json = File.ReadAllText(path);
                var s = JsonSerializer.Deserialize<UiLayoutSettings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return s ?? CreateDefault();
            }
            catch
            {
                return CreateDefault();
            }
        }

        public static void Save(string? folder, UiLayoutSettings settings)
        {
            var path = GetLayoutPath(folder);
            var json = JsonSerializer.Serialize(settings ?? CreateDefault(), new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(path, json);
        }
    }

    // ---------------------------------------------
    // GŁÓWNE OKNO – TABLICA, PODGLĄD KAMERY, INFO O POJEŹDZIE
    // ---------------------------------------------
    public class    HovedForm : Form
    {
        public static HovedForm? Current { get; private set; }

        private UiLayoutSettings _uiLayout = UiLayoutSettings.CreateDefault();



        // Folder where images/docs live (plate background, logo, etc.)
        // Folder where images/docs live (plate background, logo, config.txt, etc.)
        // Prefer AppConfig.DocFolder (set from Kamerainnstillinger). Falls back to Documents\Mykjeregistrering.
        private static string GetDocDir()
        {
            var cfg = AppConfig.DocFolder;
            if (!string.IsNullOrWhiteSpace(cfg) && Directory.Exists(cfg))
                return cfg;

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BilvaskRegistrering");
        }

        private static string DocPath(string fileName) => Path.Combine(GetDocDir(), fileName);

        private readonly ExcelRepository _repo;
        private readonly StatistikkService _statService;
        
        private Dictionary<string, KjoretoyInfo> _register;
        private Dictionary<string, decimal> _sesongPriser;

        private TextBox _txtRegnr;
        private Label _lblStatus;
        private Label _lblDbStatus;
                private Label? _lblHeaderTitle;
        private Panel? _headerPanel;
        private Panel? _plateHostPanel;
        private TableLayoutPanel? _bottomBar;
private FileSystemWatcher _snapshotWatcher;
        private PictureBox _plateBox;
        private PictureBox _LogoBox;

        // Containers used for responsive docking layout (left content vs. camera/right content)
        private Panel? _leftPanel;
        private Panel? _rightPanel;
        // PODGLĄD KAMERY + INFO
        // PODGLĄD KAMERY (LibVLC)
        private Panel _camPreviewBox;
        private VideoView _videoView1;
        private Panel? _camSmallRow;
        private Panel? _camPreviewBox2;
        private Panel? _camPreviewBox3;
        private VideoView? _videoView2;
        private VideoView? _videoView3;

        // HTTP snapshot preview (used mainly for older Axis encoders on port 80)
        private PictureBox? _picView2;
        private PictureBox? _picView3;
        private System.Windows.Forms.Timer? _snapTimer2;
        private System.Windows.Forms.Timer? _snapTimer3;
        private HttpClient? _http2;
        private HttpClient? _http3;
        private bool _snapBusy2;
        private bool _snapBusy3;
        private int _snapFail2;
        private int _snapFail3;
        // Used to invalidate in-flight async snapshot fetches on restart/close
        private int _snapSession2;
        private int _snapSession3;

        private Label? _camOverlay1;
        private Label? _camOverlay2;
        private Label? _camOverlay3;

        private LibVLC? _vlc;
        private MediaPlayer? _mp1;
        private MediaPlayer? _mp2;
        private MediaPlayer? _mp3;


        // Kamera 2 freeze watchdog (RTSP/H.264 can occasionally stall)
        private System.Windows.Forms.Timer? _cam2FreezeTimer;
        private long _cam2LastTimeMs = -1;
        private DateTime _cam2LastProgressUtc = DateTime.MinValue;
        private string? _cam2Url;
        private string? _cam2User;
        private string? _cam2Pass;

        private Panel _infoPanel;
        private FlowLayoutPanel _camButtonsPanel;

        // Small status bar between Kamera 1 and Kamera 2/3 (matches screenshot)
        private Label? _lblKameraAktiv;

        private Button _btnCamOnOff;
        private Button _btnCamRead;
        private RichTextBox _lblKjoretoyInfo;

        private Label _lblInternnr;
                private PictureBox _warnIcon;
        private readonly System.Windows.Forms.Timer _autoClearTimer = new();
        private DateTime _lastShowTime = DateTime.MinValue;
        private readonly System.Windows.Forms.Timer _latestAdminEntryTimer = new();
        private string _lastAdminShownEntryKey = "";
        private int _currentDisplaySeconds = 10;
        private ItsApiServer? _itsApi;
        private bool _previewRunning;
        // Prevent UI from freezing when RTSP connect/close blocks.
        private readonly CancellationTokenSource _cts = new();
        private Task? _camStartTask;
        private Task? _shutdownTask;
	    private string? _pendingStatus;
	    private string? _pendingPlate;
	    private bool _pendingSettingsSaved;

        // Used by the legacy "read single frame" button (kept for compatibility)
        private Bitmap? _lastCamFrame;
        private DbStatusService? _dbStatus;
        private readonly System.Windows.Forms.Timer _dbTimer = new();

        // Debounced soft restart of camera preview when settings change
        private readonly System.Windows.Forms.Timer _softRestartPreviewTimer = new();

        // De-dupe ANPR events (camera can send same plate multiple times in the same second)
        private readonly object _dedupeLock = new();
        private string? _lastSavedPlate;
        private DateTime _lastSavedAt = DateTime.MinValue;
		// Timestamp used for de-duplicating plate saves across rapid consecutive reads.
		// Stored in UTC to avoid local time issues.
		private DateTime _lastSavedPlateAtUtc = DateTime.MinValue;

	    private volatile bool _isClosing;
        // Jeśli gdzieś używasz regnrDisplay jako string:
        private string regnrDisplay = string.Empty;

        // Startuje odliczanie (np. po wczytaniu tablicy)
        private void StartAutoClearCountdown(int seconds = 0)
        {
            _currentDisplaySeconds = seconds > 0 ? seconds : AppConfig.DisplaySeconds;
            if (_currentDisplaySeconds < 1)
                _currentDisplaySeconds = 1;

            _lastShowTime = DateTime.Now;
            regnrDisplay = _txtRegnr?.Text ?? string.Empty;

            _autoClearTimer.Stop();
            _autoClearTimer.Start();
        }

        private void AutoClearTimer_Tick(object sender, EventArgs e)
        {
            if ((DateTime.Now - _lastShowTime).TotalSeconds >= _currentDisplaySeconds)
            {
                _autoClearTimer.Stop();
                ClearVehicleDisplay();
                regnrDisplay = string.Empty;
            }
        }

        private void SetWarningIcon(bool show)
        {
            if (_warnIcon == null) return;
            _warnIcon.Visible = show;
        }

        private void ClearVehicleDisplay()
        {
            if (_txtRegnr != null)
                _txtRegnr.Text = "";

            if (_lblInternnr != null)
                _lblInternnr.Text = "";

            if (_lblKjoretoyInfo != null)
            {
                _lblKjoretoyInfo.Clear();
                _lblKjoretoyInfo.SelectionFont = new Font(_lblKjoretoyInfo.Font, FontStyle.Bold);
                _lblKjoretoyInfo.SelectionColor = _lblKjoretoyInfo.ForeColor;
                _lblKjoretoyInfo.AppendText("Kjøretøyinformasjon");
            }

            if (_lblStatus != null)
                _lblStatus.Text = "";

            SetWarningIcon(false);
        }

        private void ShowLatestAdminLogEntryIfNeeded()
        {
            try
            {
                var latest = _repo.LastAlleVask()
                    .Where(v => v != null &&
                                v.DatoTid != DateTime.MinValue &&
                                !string.IsNullOrWhiteSpace(v.Registreringsnummer))
                    .OrderByDescending(v => v.DatoTid)
                    .FirstOrDefault();

                if (latest == null)
                    return;

                string plateKey = ExcelRepository.NormalizeRegnr(latest.Registreringsnummer);
                string entryKey = latest.DatoTid.ToString("yyyyMMddHHmmssfff") + "|" + plateKey;

                if (string.Equals(_lastAdminShownEntryKey, entryKey, StringComparison.OrdinalIgnoreCase))
                    return;

                _lastAdminShownEntryKey = entryKey;

                LastData();

                string displayPlate = FormatPlateForDisplay(latest.Registreringsnummer);

                if (_txtRegnr != null)
                    _txtRegnr.Text = displayPlate;

                if (_lblStatus != null)
                {
                    string statusText = string.IsNullOrWhiteSpace(latest.Status)
                        ? "registrert"
                        : latest.Status;

                    _lblStatus.Text = $"Siste registrering: {displayPlate} ({statusText})";
                }

                StartAutoClearCountdown(AppConfig.DisplaySeconds);
            }
            catch
            {
                // cicho - Admin UI ma nie wywalać błędu
            }
        }

        public HovedForm()
        {
            Current = this;
            try { _uiLayout = UiLayoutSettings.Load(GetDocDir()); } catch { _uiLayout = UiLayoutSettings.CreateDefault(); }





            Text = "Bilvask Registrering - kameralog";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1600, 720);
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(1100, 650);
            Font = new Font(FontFamily.GenericSansSerif, 12f);

            _repo = new ExcelRepository();
            _statService = new StatistikkService(_repo);

            LastData();
            ByggUi();
            SetupSnapshotWatcher();

            // Soft restart preview when settings are saved (no app restart needed)
            _softRestartPreviewTimer.Interval = 450;
            _softRestartPreviewTimer.Tick += (_, __) =>
            {
                _softRestartPreviewTimer.Stop();
                if (_isClosing || IsDisposed) return;
                try { StopCameraPreview(); } catch { }
                try { StartCameraPreview(); } catch (Exception ex) { SafeSetStatus($"Kamera restart-feil: {ex.Message}"); }
            };

            AppConfig.SettingsSaved += OnRuntimeSettingsSaved;

            // DB status indicator (Azure Postgres)
            if (DbConfig.Enabled && !string.IsNullOrWhiteSpace(DbConfig.ConnectionString))
            {
                _dbStatus = new DbStatusService(DbConfig.ConnectionString);

                _dbTimer.Interval = 15000; // 15 sek
                _dbTimer.Tick += async (_, _) => await RefreshDbStatusAsync();
                _dbTimer.Start();

                _ = RefreshDbStatusAsync();
            }
            else if (_lblDbStatus != null)
            {
                _lblDbStatus.Text = "DB: Disabled";
                _lblDbStatus.ForeColor = Color.Gray;
            }

	            
            _autoClearTimer.Interval = 500;
            _autoClearTimer.Tick += AutoClearTimer_Tick;
            _latestAdminEntryTimer.Interval = 1000;
            _latestAdminEntryTimer.Tick += (_, __) => ShowLatestAdminLogEntryIfNeeded();
            _latestAdminEntryTimer.Start();
                // Start Dahua ITS API receiver (camera POST -> plate events)
	            // NOTE: starting Kestrel/HTTP server in the constructor can fire callbacks
	            // before the form handle exists, which can throw InvalidOperationException
	            // when we call BeginInvoke/Invoke. Start it after the form is shown.
	            Shown += (_, __) =>
	            {
	                try
	                {
	                    StartItsApiServer();
	                    // Start camera preview automatically
	                    try { StartCameraPreview(); } catch (Exception ex2) { SafeSetStatus($"Kamera preview-feil: {ex2.Message}"); }
	                    // apply anything that arrived before handle was ready
	                    if (!string.IsNullOrWhiteSpace(_pendingPlate)) _txtRegnr.Text = _pendingPlate;
	                    if (!string.IsNullOrWhiteSpace(_pendingStatus)) _lblStatus.Text = _pendingStatus;
	                    if (_pendingSettingsSaved)
	                    {
	                        _pendingSettingsSaved = false;
	                        try { OnRuntimeSettingsSaved(); } catch { }
	                    }

                    ShowLatestAdminLogEntryIfNeeded();
	                }
	                catch (Exception ex) { SafeSetStatus($"ITSAPI start-feil: {ex.Message}"); }
	            };

            // Responsywny układ (po zmianie rozmiaru okna)
            Resize += (_, __) => ApplyResponsiveLayout();
            ApplyResponsiveLayout();

            FormClosing += HovedForm_FormClosing;
        

        }

        private void OnRuntimeSettingsSaved()
        {
            if (_isClosing || IsDisposed) return;

            // If settings are saved very early (before the form handle exists),
            // BeginInvoke/Invoke will throw. Defer until the form is shown.
            if (!IsHandleCreated)
            {
                _pendingSettingsSaved = true;
                return;
            }

            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(OnRuntimeSettingsSaved)); } catch { }
                return;
            }

            try
            {
                // Refresh repository paths immediately (e.g. changed Dokument.Folder)
                try
                {
                    _repo.ReloadPathsFromAppConfig();
                    LastData();
                }
                catch { }

                // Reload DB config immediately so DB can start working without full app restart
                try
                {
                    var cfg = new ConfigurationBuilder()
                        .SetBasePath(AppContext.BaseDirectory)
                        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                        .Build();

                    DbConfig.Load(cfg);

                    if (DbConfig.Enabled && !string.IsNullOrWhiteSpace(DbConfig.ConnectionString))
                    {
                        _dbStatus = new DbStatusService(DbConfig.ConnectionString);
                        _ = RefreshDbStatusAsync();
                    }
                    else if (_lblDbStatus != null)
                    {
                        _lblDbStatus.Text = "DB: Disabled";
                        _lblDbStatus.ForeColor = Color.Gray;
                    }
                }
                catch { }

                if (_lblKameraAktiv != null)
                {
                    _lblKameraAktiv.Text = "Kamera: Oppdaterer...";
                    _lblKameraAktiv.BackColor = Color.DarkOrange;
                }

                _softRestartPreviewTimer.Stop();
                _softRestartPreviewTimer.Start();
                SafeSetStatus("Kamera / DB: oppdaterer...");
                try { ShowLatestAdminLogEntryIfNeeded(); } catch { }
            }
            catch { }
        }


        private async Task RefreshDbStatusAsync()
        {
            if (_dbStatus is null || _lblDbStatus is null) return;

            try
            {
                _dbTimer.Stop();

                var (ok, msg) = await _dbStatus.PingAsync();

                _lblDbStatus.Text = ok ? "DB: Connected" : "DB: Offline";
                _lblDbStatus.ForeColor = ok ? Color.Green : Color.Red;
            }
            catch
            {
                _lblDbStatus.Text = "DB: Offline";
                _lblDbStatus.ForeColor = Color.Red;
            }
            finally
            {
                if (!_isClosing) _dbTimer.Start();
            }
        }
        
        public void ApplyUiLayout(UiLayoutSettings s)
        {
            _uiLayout = s ?? UiLayoutSettings.CreateDefault();
            try { ApplyResponsiveLayout(); } catch { }
            try
            {
                if (_mp1 != null) ApplyVideoMode(_mp1, _videoView1, _uiLayout.Cam1Mode);
                if (_mp2 != null && _videoView2 != null) ApplyVideoMode(_mp2, _videoView2, _uiLayout.Cam2Mode);
                if (_mp3 != null && _videoView3 != null) ApplyVideoMode(_mp3, _videoView3, _uiLayout.Cam3Mode);
            }
            catch { }
        }

public void ReloadUiLayout()
        {
            try { _uiLayout = UiLayoutSettings.Load(GetDocDir()); }
            catch { _uiLayout = UiLayoutSettings.CreateDefault(); }
            try { ApplyResponsiveLayout(); } catch { }
            try
            {
                // re-apply video mode to remove/keep black bars depending on user choice
                if (_mp1 != null) ApplyVideoMode(_mp1, _videoView1, _uiLayout.Cam1Mode);
                if (_mp2 != null && _videoView2 != null) ApplyVideoMode(_mp2, _videoView2, _uiLayout.Cam2Mode);
                if (_mp3 != null && _videoView3 != null) ApplyVideoMode(_mp3, _videoView3, _uiLayout.Cam3Mode);
            }
            catch { }
        }




        private void ApplyResponsiveLayout()
        {
            if (_isClosing || IsDisposed) return;
            if (_leftPanel == null || _rightPanel == null || _plateBox == null || _txtRegnr == null) return;

            // LEFT panel width ~ 40% but keep sane limits
            var w = ClientSize.Width;
            var h = ClientSize.Height;
            int maxLeft = Math.Max(_uiLayout.LeftPanelMinPx, w - _uiLayout.RightPanelMinPx); // keep room for camera area
            int leftW;
            if (_uiLayout.LeftPanelWidthPx > 0)
                leftW = _uiLayout.LeftPanelWidthPx;
            else
                leftW = (int)Math.Round(w * _uiLayout.LeftPanelPercent);

            leftW = (int)Math.Clamp(leftW, _uiLayout.LeftPanelMinPx, _uiLayout.LeftPanelMaxPx);
            _leftPanel.Width = Math.Min(leftW, maxLeft);

            // Header + DB label: keep readable on all widths (avoid "Re" truncation)
            if (_lblDbStatus != null)
                _lblDbStatus.Width = (int)Math.Clamp(_leftPanel.Width * 0.32, 120, 220);

            if (_headerPanel != null)
                _headerPanel.Height = _uiLayout.HeaderHeightPx > 0 ? _uiLayout.HeaderHeightPx : (int)Math.Clamp(h * 0.085, 56, 92);

            if (_lblHeaderTitle != null)
            {
                // Fit header text to available width (next to DB label)
                FitLabelToWidth(_lblHeaderTitle, maxSize: (float)Math.Clamp(h * 0.035, 16, 28), minSize: 12f);
            }

            if (_plateHostPanel != null)
                _plateHostPanel.Height = _uiLayout.PlateHeightPx > 0 ? _uiLayout.PlateHeightPx : (int)Math.Clamp(_leftPanel.Width * 0.34, 120, 180);

            if (_lblStatus != null)
            {
                _lblStatus.AutoEllipsis = true;
                FitLabelToWidth(_lblStatus, maxSize: 18f, minSize: 11f);
            }

            // Header font scales with window height
            foreach (Control c in _leftPanel.Controls)
            {
                if (c is Label lbl && lbl.Name == "_hdr")
                {
                    float fs = _uiLayout.HeaderTitleFontSize > 0 ? _uiLayout.HeaderTitleFontSize : (float)Math.Clamp(h * 0.035, 16, 32);
                    lbl.Font = new Font(FontFamily.GenericSansSerif, fs, FontStyle.Bold);
                }
            }

            // Plate font scales to fit inside the plate image
            LayoutPlateTextBox();

            // Info fonts (internnr + details)
            if (_lblInternnr != null)
            {
                float fs = _uiLayout.InternnrFontSize > 0 ? _uiLayout.InternnrFontSize : (float)Math.Clamp(h * 0.06, 24, 56);
                _lblInternnr.Font = new Font(FontFamily.GenericSansSerif, fs, FontStyle.Bold);
            }
            if (_lblKjoretoyInfo != null)
            {
                float fs = _uiLayout.InfoFontSize > 0 ? _uiLayout.InfoFontSize : (float)Math.Clamp(h * 0.035, 16, 34);
                _lblKjoretoyInfo.Font = new Font(FontFamily.GenericSansSerif, fs);
            }

            if (_warnIcon != null && _lblKjoretoyInfo != null)
            {
                int m = 250;

                _warnIcon.Left = m;

                // Wyśrodkowanie pionowe względem tekstu ostrzeżenia
                _warnIcon.Top =
                    _lblKjoretoyInfo.Top +
                    (_lblKjoretoyInfo.Height / 2) -
                    (_warnIcon.Height / 2);

                _warnIcon.BringToFront();
            }
            // Camera previews: keep stable proportions on all resolutions (as in the reference screenshot).
            // We don't hard-enforce 16:9 on the containers; VideoView will letterbox as needed.
            if (_camPreviewBox != null && _camButtonsPanel != null && _camSmallRow != null)
            {
                int rpH = _rightPanel.ClientSize.Height;
                int statusH = (_lblKameraAktiv != null && _lblKameraAktiv.Visible) ? _lblKameraAktiv.Height : 0;
                int buttonsH = (_bottomBar != null ? _bottomBar.Height : _camButtonsPanel.Height);

                if (_bottomBar != null && _uiLayout.BottomBarHeightPx > 0)
                    _bottomBar.Height = _uiLayout.BottomBarHeightPx;


                int padding = 24;
                int availH = rpH - buttonsH - statusH - padding;
                if (availH < 360) availH = 360;

                // Main (Kamera 1) ~58%, bottom row (Kamera 2/3) ~42%
                double mainPct = Math.Clamp(_uiLayout.CameraMainPercent, 0.30, 0.80);
                int mainH = (int)Math.Round(availH * mainPct);
                int smallH = availH - mainH;

                mainH = Math.Max(220, mainH);
                smallH = Math.Max(160, smallH);

                _camPreviewBox.Height = mainH;
                _camSmallRow.Height = smallH;
                _camSmallRow.Visible = true;
            }

            // Fit overlay text so it doesn't wrap awkwardly on small windows
            FitOverlayLabel(_camOverlay1, _uiLayout.Overlay1Max > 0 ? _uiLayout.Overlay1Max : 44f, _uiLayout.Overlay1Min > 0 ? _uiLayout.Overlay1Min : 18f);
            FitOverlayLabel(_camOverlay2, _uiLayout.Overlay2Max > 0 ? _uiLayout.Overlay2Max : 40f, _uiLayout.Overlay2Min > 0 ? _uiLayout.Overlay2Min : 14f);
            FitOverlayLabel(_camOverlay3, _uiLayout.Overlay3Max > 0 ? _uiLayout.Overlay3Max : 40f, _uiLayout.Overlay3Min > 0 ? _uiLayout.Overlay3Min : 14f);


            try { if (_mp1 != null) ApplyVideoMode(_mp1, _videoView1, _uiLayout.Cam1Mode); } catch { }
            try { if (_mp2 != null && _videoView2 != null) ApplyVideoMode(_mp2, _videoView2, _uiLayout.Cam2Mode); } catch { }
            try { if (_mp3 != null && _videoView3 != null) ApplyVideoMode(_mp3, _videoView3, _uiLayout.Cam3Mode); } catch { }
}


        private static void FitOverlayLabel(Label? lbl, float maxSize, float minSize)
        {
            if (lbl == null) return;
            if (lbl.Width < 60 || lbl.Height < 60) return;

            // Keep original font family/style but adjust size
            var family = lbl.Font?.FontFamily ?? FontFamily.GenericSansSerif;
            var style = lbl.Font?.Style ?? FontStyle.Bold;

            var lines = (lbl.Text ?? "").Split('\n');
            if (lines.Length == 0) return;

            var longest = lines.OrderByDescending(s => s.Length).FirstOrDefault() ?? "";
            // if empty, just keep default
            if (string.IsNullOrWhiteSpace(longest)) return;

            // Reserve some padding
            int targetW = Math.Max(10, lbl.Width - 30);
            int targetH = Math.Max(10, lbl.Height - 30);

            for (float size = maxSize; size >= minSize; size -= 2f)
            {
                using var f = new Font(family, size, style);
                // measure the longest line as single line (avoid mid-word wraps)
                var m = TextRenderer.MeasureText(longest, f, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

                // approximate multi-line height
                var lineH = TextRenderer.MeasureText("Hg", f, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height;

                int totalH = lineH * lines.Length + (lines.Length - 1) * (int)Math.Round(size * 0.15f);

                if (m.Width <= targetW && totalH <= targetH)
                {
                    lbl.Font = new Font(family, size, style);
                    return;
                }
            }

            // fallback
            lbl.Font = new Font(family, minSize, style);
        }

        

private static void FitLabelToWidth(Label lbl, float maxSize, float minSize)
{
    if (lbl == null) return;
    if (lbl.Width < 80) return;

    var family = lbl.Font?.FontFamily ?? FontFamily.GenericSansSerif;
    var style = lbl.Font?.Style ?? FontStyle.Bold;
    var text = lbl.Text ?? "";
    if (string.IsNullOrWhiteSpace(text)) return;

    int targetW = Math.Max(10, lbl.Width - 16);

    for (float size = maxSize; size >= minSize; size -= 1f)
    {
        using var f = new Font(family, size, style);
        var m = TextRenderer.MeasureText(text, f, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        if (m.Width <= targetW)
        {
            lbl.Font = new Font(family, size, style);
            return;
        }
    }

    lbl.Font = new Font(family, minSize, style);
}
/// <summary>
        /// Keeps the registration number (license plate text) readable and centered
        /// on top of the plate background at any window size.
        /// </summary>
        private void LayoutPlateTextBox()
        {
            if (_plateBox == null || _txtRegnr == null) return;

            // Left blue strip with flag typically takes ~15-20% width; keep within sane bounds.
            int stripW = (int)Math.Clamp(_plateBox.Width * 0.17, 80, 140);
            const int padX = 18;
            const int padY = 10;

            int x = stripW + padX;
            int w = Math.Max(60, _plateBox.Width - x - padX);
            int h = Math.Max(30, _plateBox.Height - padY * 2);

            _txtRegnr.Multiline = true;
            _txtRegnr.WordWrap = false; // do NOT wrap (otherwise numbers disappear on small widths)
            _txtRegnr.ScrollBars = ScrollBars.None;
            _txtRegnr.BorderStyle = BorderStyle.None;
            _txtRegnr.TextAlign = HorizontalAlignment.Center;

            _txtRegnr.SetBounds(x, padY, w, h);

            // Fit font to available box, then re-center vertically for a clean look.
            FitTextBoxFontToBounds(_txtRegnr, _txtRegnr.ClientSize, maxSize: 180f, minSize: 20f);

            int targetH = Math.Min(_plateBox.Height - padY * 2, Math.Max(30, _txtRegnr.Font.Height + 14));
            _txtRegnr.Height = targetH;
            _txtRegnr.Top = Math.Max(padY, (_plateBox.Height - _txtRegnr.Height) / 2);
        }

        private static void FitTextBoxFontToBounds(TextBox tb, Size bounds, float maxSize, float minSize)
        {
            // Keep the license plate text readable and inside the plate area.
            // IMPORTANT: do NOT call CreateGraphics() before the handle exists (can break rendering).
            if (!tb.IsHandleCreated) return;

            tb.TextAlign = HorizontalAlignment.Center;

            // padding inside plate where the text can live
            const int padX = 18;
            const int padY = 10;
            int targetW = Math.Max(10, bounds.Width - padX * 2);
            int targetH = Math.Max(10, bounds.Height - padY * 2);

            // If empty, keep a reasonable size
            string text = string.IsNullOrWhiteSpace(tb.Text) ? "ABC123" : tb.Text;

            float size = maxSize;
            for (; size >= minSize; size -= 2f)
            {
                using var f = new Font(tb.Font.FontFamily, size, FontStyle.Bold);
                var s = TextRenderer.MeasureText(
                    text,
                    f,
                    new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
                );
                if (s.Width <= targetW && s.Height <= targetH)
                {
                    tb.Font = new Font(tb.Font.FontFamily, size, FontStyle.Bold);
                    return;
                }
            }

            tb.Font = new Font(tb.Font.FontFamily, minSize, FontStyle.Bold);
        }


        private void LastData()
        {
            _register = _repo.LastKjoretoyRegister();
            _sesongPriser = _repo.LastSesongPriser();
        }

		        private void SafeSetStatus(string msg)
		        {
		            _pendingStatus = msg;
		            if (_isClosing || IsDisposed || !IsHandleCreated) return;
		            try { BeginInvoke((Action)(() => _lblStatus.Text = msg)); } catch { }
		        }

	        private void SafeSetPlate(string plate)
	        {
	            _pendingPlate = plate;
	            if (_isClosing || IsDisposed || !IsHandleCreated) return;
	            try
	            {
	                BeginInvoke((Action)(() =>
	                {
	                    _txtRegnr.Text = FormatPlateForDisplay(plate);
	                    _lblStatus.Text = $"ANPR: {plate}";
	                
                    StartAutoClearCountdown();
                    }
                    )
                    );

            }
	            catch { }
	        }

            private static string NormalizePlate(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                // Some cameras send "SX 29708" while others send "SX29708".
                // We normalize so our de-dupe and matching works.
                return s.Trim().Replace(" ", "").ToUpperInvariant();
            }

            /// <summary>
            /// Canonicalize a plate candidate from ANPR/ITS so we can validate consistently.
            /// - Trims + uppercases
            /// - Removes any non-alphanumeric characters (some cameras include '-' or '.')
            /// - Fixes common OCR confusions in the numeric part (O->0, I->1)
            /// </summary>
            private static string CanonicalizePlateCandidate(string? plate)
            {
                var s = NormalizePlate(plate);
                if (string.IsNullOrWhiteSpace(s)) return string.Empty;

                // Strip any non-alphanumeric characters.
                var sb = new StringBuilder(s.Length);
                foreach (var ch in s)
                    if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                s = sb.ToString();

                // Apply OCR fixes only if it starts like a normal Norwegian plate (2 letters + digits).
                if (s.Length >= 6 && char.IsLetter(s[0]) && char.IsLetter(s[1]))
                {
                    var sb2 = new StringBuilder(s.Length);
                    sb2.Append(s[0]);
                    sb2.Append(s[1]);
                    for (int i = 2; i < s.Length; i++)
                    {
                        char c = s[i];
                        if (c == 'O') c = '0';
                        else if (c == 'I') c = '1';
                        sb2.Append(c);
                    }
                    s = sb2.ToString();
                }

                return s;
            }

            /// <summary>
            /// Accept only the strict fleet format used by this solution:
            /// 2 letters + 5 digits (for example AB12345).
            /// Anything shorter, such as IF1134, must be ignored.
            /// </summary>
            private static bool IsLikelyNorwegianPlate(string plate)
            {
                if (string.IsNullOrWhiteSpace(plate)) return false;

                // Common false positives from the SKYSS logo.
                if (plate.Contains("SKYSS", StringComparison.OrdinalIgnoreCase)) return false;

                return ExcelRepository.IsStrictNorwegianPlate(plate);
            }

        private void StartItsApiServer()
        {
            try { _itsApi?.Stop(); } catch { }
            _itsApi = null;

            var port = AppConfig.ItsApiPort <= 0 ? 7070 : AppConfig.ItsApiPort;
            var path = string.IsNullOrWhiteSpace(AppConfig.ItsApiPath)
                ? "/NotificationInfo/TollgateInfo"
                : AppConfig.ItsApiPath;

            _itsApi = new ItsApiServer(port, path);

	            _itsApi.Debug += (_, msg) => SafeSetStatus(msg);

	            _itsApi.PlateDetected += (_, plate) =>
	            {
	                // Normalize + de-dupe (the camera may send the same plate multiple times)
	                var normalized = CanonicalizePlateCandidate(plate);
	                if (string.IsNullOrWhiteSpace(normalized)) return;

	                // Filter out obvious non-plates (e.g. SKYSS logo reads)
	                if (!IsLikelyNorwegianPlate(normalized))
	                {
	                    // Keep it quiet, but show a short status so operator understands why nothing happened.
	                    SafeSetStatus($"ANPR: ignorert '{normalized}' (ikke skilt)");
	                    return;
	                }

	                var now = DateTime.UtcNow;
	                lock (_dedupeLock)
	                {
	                    if (!string.IsNullOrEmpty(_lastSavedPlate)
	                        && string.Equals(_lastSavedPlate, normalized, StringComparison.OrdinalIgnoreCase)
	                        && (now - _lastSavedPlateAtUtc) <= TimeSpan.FromSeconds(2))
	                    {
	                        return;
	                    }
	
	                    _lastSavedPlate = normalized;
	                    _lastSavedPlateAtUtc = now;
	                }

	                SafeSetPlate(normalized);
	                if (AppConfig.AutoRegisterOnPlate && IsHandleCreated)
	                {
	                    try { BeginInvoke((Action)(() => RegistrerVaskFraTekstboksen())); } catch { }
	                }
	            };

	            _itsApi.Start();
            SafeSetStatus($"ITSAPI: lytter på http://{AppConfig.ItsApiHostIp}:{port}{path}");
        }

        private void StopItsApiServer()
        {
            var its = _itsApi;
            _itsApi = null;
            if (its == null) return;

            // Don't block UI thread on shutdown.
            _ = Task.Run(() =>
            {
                try { its.Stop(); } catch { }
            });
        }

        private void HovedForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            _isClosing = true;
            try { AppConfig.SettingsSaved -= OnRuntimeSettingsSaved; } catch { }
            try { _softRestartPreviewTimer.Stop(); } catch { }
            if (_snapshotWatcher != null)
            {
                _snapshotWatcher.EnableRaisingEvents = false;
                _snapshotWatcher.Dispose();
                _snapshotWatcher = null;
            }

            try { _latestAdminEntryTimer.Stop(); } catch { }
            try { StopItsApiServer(); } catch { }
            try { StopCameraPreview(); } catch { }
        }

        private void ByggUi()
        {
            SuspendLayout();
            Controls.Clear();

            // Root panels: left (plate/info), right (preview/buttons)
            _leftPanel = new Panel
            {
                Dock = DockStyle.Left,
                Padding = new Padding(20),
                Width = 620,
                BackColor = SystemColors.Control
            };

            _rightPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20),
                BackColor = SystemColors.Control
            };

            Controls.Add(_rightPanel);
            Controls.Add(_leftPanel);

            _headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 70
            };

            _lblHeaderTitle = new Label
            {
                Name = "_hdr",
                AutoEllipsis = true,
                Text = "Registrering av bilvaskemaskin (kamerapålogging)",
                Font = new Font(FontFamily.GenericSansSerif, 22f, FontStyle.Bold),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _lblDbStatus = new Label
            {
                Name = "lblDbStatus",
                Text = "DB: ...",
                AutoSize = false,
                Dock = DockStyle.Right,
                Width = 260,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.Gray
            };

            _headerPanel!.Controls.Add(_lblDbStatus);
            _headerPanel!.Controls.Add(_lblHeaderTitle!);


            var lblRegnr = new Label
            {
                Text = "Registreringsnummer:",
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 32
            };

            // Plate area (PictureBox + overlay TextBox)
            _plateHostPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 150
            };

            _plateBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = Color.White
            };

            try
            {
                string platePath = Path.Combine(AppConfig.DocFolder, "SkiltN.png");
                if (File.Exists(platePath))
                {
                    _plateBox.Image = Image.FromFile(platePath);
                }
            }
            catch { }

            _txtRegnr = new TextBox
            {
                // Multiline=true lets us control height so the text can be vertically centered.
                // WordWrap=false keeps the license plate on one line.
                Multiline = true,
                WordWrap = false,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ForeColor = Color.Black,
                TextAlign = HorizontalAlignment.Center,
                ScrollBars = ScrollBars.None
            };

            _txtRegnr.TextChanged += (s, e) =>
            {
                string reg = _txtRegnr.Text.Trim().ToUpperInvariant();
                OppdaterKjoretoyInfo(reg);

                // Re-fit plate font whenever the plate changes (not only on resize).
                if (!_isClosing) ApplyResponsiveLayout();
            };

            _lblStatus = new Label
            {
                Text = "",
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 44,
                Font = new Font(FontFamily.GenericSansSerif, 18f, FontStyle.Bold)
            };


            // LOGO w prawym dolnym rogu
            _LogoBox = new PictureBox
            {
                Size = new Size(50, 50),
                BorderStyle = BorderStyle.None,
                SizeMode = PictureBoxSizeMode.StretchImage,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            string LogoPath = Path.Combine(AppConfig.DocFolder, "Firmalogo.png");
            if (File.Exists(LogoPath))
            {
                _LogoBox.Image = Image.FromFile(LogoPath);
            }

            // Camera preview (right)
            _camPreviewBox = new Panel
            {
                Dock = DockStyle.Top,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.Transparent,
                Height = 480,
                MinimumSize = new Size(320, 180)
            };

            _videoView1 = new VideoView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };

            _camOverlay1 = new Label
            {
                Text = "KAMERA 1 ANPR",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Red,
                BackColor = Color.Transparent,
                Font = new Font(FontFamily.GenericSansSerif, 44f, FontStyle.Bold)
            };

            _camPreviewBox.Controls.Add(_videoView1);
            _camPreviewBox.Controls.Add(_camOverlay1);
            _camOverlay1.BringToFront();

            _lblKameraAktiv = new Label
            {
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "Kamera: Ikke aktiv",
                BackColor = Color.DarkGray,
                ForeColor = Color.White
            };

            // Small preview row (KAMERA 2 + KAMERA 3)
            _camSmallRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = 240,
                Padding = new Padding(0, 10, 0, 0)
            };

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _camPreviewBox2 = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 10, 0)
            };

            _videoView2 = new VideoView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };

            _picView2 = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom,
                Visible = false
            };

            _camOverlay2 = new Label
            {
                Text = "KAMERA 2",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Red,
                BackColor = Color.Transparent,
                Font = new Font(FontFamily.GenericSansSerif, 40f, FontStyle.Bold)
            };

            _camPreviewBox2.Controls.Add(_videoView2);
            _camPreviewBox2.Controls.Add(_picView2);
            _camPreviewBox2.Controls.Add(_camOverlay2);
            _camOverlay2.BringToFront();

            _camPreviewBox3 = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.Transparent,
                Margin = new Padding(10, 0, 0, 0)
            };

            _videoView3 = new VideoView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };

            _picView3 = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom,
                Visible = false
            };

            _camOverlay3 = new Label
            {
                Text = "KAMERA 3",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Red,
                BackColor = Color.Transparent,
                Font = new Font(FontFamily.GenericSansSerif, 30f, FontStyle.Bold)
            };

            _camPreviewBox3.Controls.Add(_videoView3);
            _camPreviewBox3.Controls.Add(_picView3);
            _camPreviewBox3.Controls.Add(_camOverlay3);
            _camOverlay3.BringToFront();

            table.Controls.Add(_camPreviewBox2, 0, 0);
            table.Controls.Add(_camPreviewBox3, 1, 0);
            _camSmallRow.Controls.Add(table);

            // (LibVLC) podglad jest w VideoView, bez rysowania placeholdera

            var btnLogg = new Button
            {
                Text = "Loggkontroll",
                Width = 150,
                Height = 40,
                Margin = new Padding(5)
            };
            btnLogg.Click += (s, e) =>
            {
                using (var f = new LoggForm(_repo))
                    f.ShowDialog(this);
            };

            var btnTideRapport = new Button
            {
                Text = "Tide rapporter",
                Width = 150,
                Height = 40,
                Margin = new Padding(5)
            };
            btnTideRapport.Click += (s, e) =>
            {
                using (var f = new TideRapportForm(_repo))
                    f.ShowDialog(this);
            };

            var btnKjoretoy = new Button
            {
                Text = "Kjøretøyregister",
                Width = 150,
                Height = 40,
                Margin = new Padding(5)
            };
            btnKjoretoy.Click += (s, e) =>
            {
                using (var f = new KjoretoyForm(_repo))
                    f.ShowDialog(this);
                LastData();
            };

            var btnEgenFlate = new Button
            {
                Text = "Egen flåte",
                Width = 150,
                Height = 40,
                Margin = new Padding(5)
            };
            btnEgenFlate.Click += (s, e) =>
            {
                using (var f = new EgenFlateForm(_repo))
                    f.ShowDialog(this);
                LastData();
            };

            var btnStat = new Button
            {
                Text = "Statistikk / Rapporterektura",
                Width = 250,
                Height = 40,
                Margin = new Padding(5)
            };
            btnStat.Click += (s, e) =>
            {
                using (var f = new StatForm(_statService, _repo))
                    f.ShowDialog(this);
            };

            var btnArkiv = new Button
            {
                Text = "Redigering av rapporter",
                Width = 200,
                Height = 40,
                Margin = new Padding(5)
            };
            btnArkiv.Click += (s, e) =>
            {
                using (var f = new FakturaArkivForm(_repo))
                    f.ShowDialog(this);
            };

            var btnCamSettings = new Button
            {
                Text = "Innstillinger",
                Width = 150,
                Height = 40,
                Margin = new Padding(5)
            };
            btnCamSettings.Click += (s, e) =>
            {
                if (!PasswordPrompt.Require(this, "Admin passord", AppConfig.UiAdminPassword,
                        "Skriv admin-passord for å åpne Innstillinger:"))
                    return;

                using (var f = new FirmaForm(_repo, 2))
                    f.ShowDialog(this);
            };

            var btnAnsatte = new Button
            {
                Text = "Ansatte",
                Width = 200,
                Height = 40,
                Margin = new Padding(5)
            };
            btnAnsatte.Click += (s, e) =>
            {
                if (!DbConfig.Enabled || string.IsNullOrWhiteSpace(DbConfig.ConnectionString))
                {
                    MessageBox.Show(this, "DB er ikke aktivert.\nAktiver DB i Innstillinger først.", "Info",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                using var f = new AnsatteForm(DbConfig.ConnectionString);
                f.ShowDialog(this);
            };

            var btnArbeiderliste = new Button
            {
                Text = "Arbeiderliste",
                Width = 200,
                Height = 40,
                Margin = new Padding(5)
            };
            btnArbeiderliste.Click += (s, e) =>
            {
                if (!DbConfig.Enabled || string.IsNullOrWhiteSpace(DbConfig.ConnectionString))
                {
                    MessageBox.Show(this, "DB er ikke aktivert.\nAktiver DB i Innstillinger først.", "Info",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                using var f = new ArbeiderlisteForm(DbConfig.ConnectionString);
                f.ShowDialog(this);
            };


            // Buttons panel under the preview (no overlap)
            _camButtonsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                AutoSize = false,
                AutoScroll = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

_camButtonsPanel.Controls.AddRange(new Control[]
            {
    // Manual buttons removed (auto-start): _btnCamOnOff, _btnCamRead, btnKamera
    btnLogg,
    btnArbeiderliste,
    btnStat,
    btnArkiv,
    btnTideRapport,
    btnKjoretoy,
    btnEgenFlate,
    btnAnsatte,
    btnCamSettings,
   
            });


            // PANEL Z INFORMACJĄ O POJEŹDZIE
            _infoPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None
            };

            _lblInternnr = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 150,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(FontFamily.GenericSansSerif, 50f, FontStyle.Bold),
                Text = ""
            };

            _warnIcon = new PictureBox
            {
                Size = new Size(120, 120),
                SizeMode = PictureBoxSizeMode.StretchImage,
                Image = SystemIcons.Warning.ToBitmap(),
                Visible = false,
                BackColor = Color.Transparent
            };


            _lblKjoretoyInfo = new RichTextBox
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                //TextAlign = ContentAlignment.TopCenter,
                BorderStyle = BorderStyle.None,
                Font = new Font(FontFamily.GenericSansSerif, 30f),
                Text = "Kjøretøyinformasjon"
            };

            _lblKjoretoyInfo.ReadOnly = true;
            _lblKjoretoyInfo.BorderStyle = BorderStyle.None;
            _lblKjoretoyInfo.BackColor = this.BackColor;
            _lblKjoretoyInfo.ScrollBars = RichTextBoxScrollBars.None;
            _lblKjoretoyInfo.TabStop = false;


            _infoPanel.Controls.Add(_lblKjoretoyInfo);
            _infoPanel.Controls.Add(_warnIcon);
            _infoPanel.Controls.Add(_lblInternnr);


            // Compose left side
            _plateHostPanel.Controls.Add(_plateBox);
            _plateHostPanel.Controls.Add(_txtRegnr);
            _txtRegnr.BringToFront();

            _leftPanel.Controls.Add(_infoPanel);
            _leftPanel.Controls.Add(_lblStatus);
            _leftPanel.Controls.Add(_plateHostPanel);
            _leftPanel.Controls.Add(lblRegnr);
            _leftPanel.Controls.Add(_headerPanel!);

            // Compose right side
            _rightPanel.Controls.Add(new Panel { Dock = DockStyle.Fill });

            // Bottom bar: buttons on the left, logo on the right (prevents overlap)
            _bottomBar = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 90,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(0),
                Margin = new Padding(0),
                BackColor = Color.Transparent
            };
            _bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100f));
            _bottomBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _camButtonsPanel.Dock = DockStyle.Fill;
            _camButtonsPanel.Margin = new Padding(0);
            _camButtonsPanel.Padding = new Padding(0);

            _LogoBox.Margin = new Padding(10, 0, 0, 0);
            _LogoBox.Anchor = AnchorStyles.None;

            _bottomBar.Controls.Add(_camButtonsPanel, 0, 0);
            _bottomBar.Controls.Add(_LogoBox, 1, 0);

            _rightPanel.Controls.Add(_bottomBar);
            if (_camSmallRow != null) _rightPanel.Controls.Add(_camSmallRow);
            if (_lblKameraAktiv != null) _rightPanel.Controls.Add(_lblKameraAktiv);
            _rightPanel.Controls.Add(_camPreviewBox);
            ResumeLayout(true);
            ApplyResponsiveLayout();
        }

        
        // ------------ PODGLĄD KAMERY (start/stop) -----------------

		private void StartCameraPreview()
        {
            if (_previewRunning) return;

            var rtsp1 = AppConfig.CameraRtspUrl;
            var rtsp2 = AppConfig.Camera2StreamUrl;
            var rtsp3 = AppConfig.Camera3StreamUrl;

            if (string.IsNullOrWhiteSpace(rtsp1) && string.IsNullOrWhiteSpace(rtsp2) && string.IsNullOrWhiteSpace(rtsp3))
            {
                _lblStatus.Text = "Mangler kamera-URL i konfigurasjonen";
                return;
            }

            try
            {
                Core.Initialize();
                StopCameraPreview();

                if (_lblKameraAktiv != null)
                {
                    _lblKameraAktiv.Text = "ANPR : Ikke aktiv";
                    _lblKameraAktiv.BackColor = Color.DarkGray;
                }

                // Low-latency options; adjust if needed
                _vlc = new LibVLC(
                    "--no-video-title-show",
                    "--rtsp-tcp",
                    "--network-caching=150",
                    "--live-caching=150",
                    "--file-caching=150",
                    "--clock-jitter=0",
                    "--clock-synchro=0"
                );

                // Start streams (each gets its own MediaPlayer)
                StartStream(rtsp1, _videoView1, ref _mp1, "ANPR Kamera",
                    user: null, pass: null);

                // Kamera 2: Axis encoder often works best as HTTP snapshot preview
                if (ShouldUseSnapshot(AppConfig.Camera2Settings, rtsp2))
                {
                    if (_camOverlay2 != null) { _camOverlay2.Text = "Inngang til vaskehallen"; _camOverlay2.Visible = true; }
                    StartSnapshotPreview2(AppConfig.Camera2Settings);
                }
                else if (_videoView2 != null)
                {
                    if (_picView2 != null) _picView2.Visible = false;
                    _videoView2.Visible = true;
                    StartStream(rtsp2, _videoView2, ref _mp2, "Inngang til vaskehallen",
                        user: AppConfig.Camera2Settings.Username,
                        pass: AppConfig.Camera2Settings.Password);
                }

                // Kamera 3: default RTSP (but allow snapshot if user chose HTTP)
                if (ShouldUseSnapshot(AppConfig.Camera3Settings, rtsp3))
                {
                    if (_camOverlay3 != null) { _camOverlay3.Text = "Avgang fra vaskehallen"; _camOverlay3.Visible = true; }
                    StartSnapshotPreview3(AppConfig.Camera3Settings);
                }
                else if (_videoView3 != null)
                {
                    if (_picView3 != null) _picView3.Visible = false;
                    _videoView3.Visible = true;
                    StartStream(rtsp3, _videoView3, ref _mp3, "Avgang fra vaskehallen",
                        user: AppConfig.Camera3Settings.Username,
                        pass: AppConfig.Camera3Settings.Password);
                }

                _previewRunning = true;
                _lblStatus.Text = "Kamera: visning startet";
            }
            catch (Exception ex)
            {
                _previewRunning = false;
                _lblStatus.Text = "Feil ved visning: " + ex.Message;
            }
        }

        private void StartStream(string? url, VideoView view, ref MediaPlayer? mp, string name, string? user, string? pass)
        {
            if (view == null) return;

            // DO NOT capture ref/out parameters in lambdas (C# restriction).
            // Use a local "player" variable for handlers.
            MediaPlayer? player = null;

            // LibVLC callbacks run on worker threads -> always marshal UI changes
            void Ui(Action a)
            {
                if (_isClosing || IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(a); } catch { }
            }

            // Dispose previous player (if any)
            try { mp?.Stop(); } catch { }
            try { mp?.Dispose(); } catch { }
            mp = null;

            if (string.IsNullOrWhiteSpace(url))
            {
                try { view.MediaPlayer = null; } catch { }
                Ui(() =>
                {
                    try
                    {
                        if (name.StartsWith("ANPR Kamera", StringComparison.OrdinalIgnoreCase) && _camOverlay1 != null) _camOverlay1.Visible = true;
                        if (name.StartsWith("Inngang til vaskehallen", StringComparison.OrdinalIgnoreCase) && _camOverlay2 != null) _camOverlay2.Visible = true;
                        if (name.StartsWith("Avgang fra vaskehallen", StringComparison.OrdinalIgnoreCase) && _camOverlay3 != null) _camOverlay3.Visible = true;
                    }
                    catch { }
                });
                return;
            }

            player = new MediaPlayer(_vlc!);
            mp = player; // assign once (do not use mp in lambdas)

            // Remember camera 2 connection details for watchdog restarts
            if (name.StartsWith("Inngang til vaskehallen", StringComparison.OrdinalIgnoreCase))
            {
                _cam2Url = url;
                _cam2User = (user ?? "").Trim();
                _cam2Pass = pass ?? "";
            }

            // show placeholders until stream really starts
            Ui(() =>
            {
                try
                {
                    if (name.StartsWith("ANPR Kamera", StringComparison.OrdinalIgnoreCase) && _camOverlay1 != null) _camOverlay1.Visible = true;
                    if (name.StartsWith("Inngang til vaskehallen", StringComparison.OrdinalIgnoreCase) && _camOverlay2 != null) _camOverlay2.Visible = true;
                    if (name.StartsWith("Avgang fra vaskehallen", StringComparison.OrdinalIgnoreCase) && _camOverlay3 != null) _camOverlay3.Visible = true;
                }
                catch { }
            });

            // Kamera 1 status bar
            if (name.StartsWith("ANPR Kamera", StringComparison.OrdinalIgnoreCase) && _lblKameraAktiv != null)
            {
                Ui(() =>
                {
                    try
                    {
                        _lblKameraAktiv.Text = "ANPR Kamera: Kobler til...";
                        _lblKameraAktiv.BackColor = Color.DarkOrange;
                    }
                    catch { }
                });
            }

            player.EncounteredError += (_, __) =>
            {
                SafeSetStatus($"{name}: feil ved tilkobling (sjekk URL/bruker/passord).");

                Ui(() =>
                {
                    try
                    {
                        if (name.StartsWith("ANPR Kamera", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_lblKameraAktiv != null)
                            {
                                _lblKameraAktiv.Text = "ANPR Kamera: Feil";
                                _lblKameraAktiv.BackColor = Color.DarkRed;
                            }
                            if (_camOverlay1 != null)
                            {
                                _camOverlay1.Text = "ANPR KAMERA\n(ingen bilde)";
                                _camOverlay1.Visible = true;
                            }
                        }
                        else if (name.StartsWith("Inngang til vaskehallen", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_camOverlay2 != null)
                            {
                                _camOverlay2.Text = "Ingang til vaskehallen\n(ingen bilde)";
                                _camOverlay2.Visible = true;
                            }
                        }
                        else if (name.StartsWith("Avgang fra vaskehallen", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_camOverlay3 != null)
                            {
                                _camOverlay3.Text = "Avgang fra vaskehallen\n(ingen bilde)";
                                _camOverlay3.Visible = true;
                            }
                        }
                    }
                    catch { }
                });
            };

            player.Playing += (_, __) =>
            {
                SafeSetStatus($"{name}: tilkoblet");

                Ui(() =>
                {
                    try
                    {
                        if (name.StartsWith("ANPR Kamera", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_lblKameraAktiv != null)
                            {
                                _lblKameraAktiv.Text = "ANPR Kamera: Aktiv";
                                _lblKameraAktiv.BackColor = Color.DarkGreen;
                            }
                            if (_camOverlay1 != null) _camOverlay1.Visible = false;
                        }
                        else if (name.StartsWith("Inngang til vaskehallen", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_camOverlay2 != null) _camOverlay2.Visible = false;
                        }
                        else if (name.StartsWith("Avgang fra vaskehallen", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_camOverlay3 != null) _camOverlay3.Visible = false;
                        }
                    }
                    catch { }
                });

                // Remove black bars by cropping to the current view aspect ratio
                try { ApplyCropFill(player, view); } catch { }

                // Arm freeze watchdog for Kamera 2 only when enabled in settings
                if (name.StartsWith("Inngang til vaskehallen", StringComparison.OrdinalIgnoreCase))
                {
                    if (AppConfig.Camera2Settings.AutoRefreshOnFreeze)
                        StartCam2FreezeWatchdog();
                    else
                        StopCam2FreezeWatchdog();
                }
            };

            // When stream stops/disposes, stop watchdog if it's Kamera 2
            player.Stopped += (_, __) =>
            {
                if (name.StartsWith("Inngang til vaskehallen", StringComparison.OrdinalIgnoreCase))
                    StopCam2FreezeWatchdog();
            };

            view.MediaPlayer = player;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                SafeSetStatus($"{name}: ugyldig URL");
                return;
            }

            using var media = new Media(_vlc!, uri);

            var scheme = (uri.Scheme ?? "").ToLowerInvariant();
            var isRtsp = scheme == "rtsp" || scheme == "rtsps";
            var isHttp = scheme == "http" || scheme == "https";

            user = (user ?? "").Trim();

            if (isRtsp)
            {
                media.AddOption(":rtsp-tcp");
                if (!string.IsNullOrWhiteSpace(user))
                {
                    media.AddOption($":rtsp-user={user}");
                    media.AddOption($":rtsp-pwd={pass ?? ""}");
                }
            }

            if (isHttp)
            {
                if (!string.IsNullOrWhiteSpace(user))
                {
                    media.AddOption($":http-user={user}");
                    media.AddOption($":http-pwd={pass ?? ""}");
                }
                media.AddOption(":http-reconnect");
            }

            // Default caching
            media.AddOption(":network-caching=250");
            media.AddOption(":live-caching=250");
            media.AddOption(":clock-jitter=0");
            media.AddOption(":clock-synchro=0");

            // Kamera 2: more robust options (AXIS RTSP/H.264 can stall)
            if (name.StartsWith("Inngang til vaskehallen", StringComparison.OrdinalIgnoreCase) && isRtsp)
            {
                media.AddOption(":network-caching=800");
                media.AddOption(":live-caching=800");
                media.AddOption(":rtsp-frame-buffer-size=1000000");
                media.AddOption(":avcodec-hw=none");
                media.AddOption(":drop-late-frames");
                media.AddOption(":skip-frames");
            }

            player.Play(media);
        }


        private static string PickNearestAspect(int w, int h)
        {
            if (w <= 0 || h <= 0) return "16:9";
            double r = (double)w / h;
            (string a, double v)[] common =
            {
                ("16:9", 16.0/9.0),
                ("4:3", 4.0/3.0),
                ("3:2", 3.0/2.0),
                ("5:4", 5.0/4.0),
                ("1:1", 1.0),
            };
            string best = "16:9";
            double bestDiff = double.MaxValue;
            foreach (var (a, v) in common)
            {
                var d = Math.Abs(r - v);
                if (d < bestDiff) { bestDiff = d; best = a; }
            }
            return best;
        }

        private static void VlcTrySet(object target, string propName, object? value)
        {
            try
            {
                var p = target.GetType().GetProperty(propName);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(target, value);
                    return;
                }
            }
            catch { }
        }

        private static void VlcTryCall(object target, string methodName, params object?[] args)
        {
            try
            {
                var m = target.GetType().GetMethod(methodName);
                if (m != null)
                {
                    m.Invoke(target, args);
                    return;
                }
            }
            catch { }
        }

        
        private void ApplyVideoMode(MediaPlayer player, Control view, string? mode)
        {
            var m = (mode ?? "crop").Trim().ToLowerInvariant();
            if (m == "fit" || m == "tilpass")
            {
                ClearCrop(player);
                // keep aspect, no forced crop
                VlcTrySet(player, "AspectRatio", null);
                VlcTrySet(player, "Scale", 0f);
                VlcTryCall(player, "SetVideoScale", 0f);
                return;
            }

            // default: crop/fill
            ApplyCropFill(player, view);
        }

        private void ClearCrop(MediaPlayer player)
        {
            try { VlcTrySet(player, "CropGeometry", ""); } catch { }
            try { VlcTryCall(player, "SetVideoCropGeometry", ""); } catch { }
            try { VlcTryCall(player, "SetCropGeometry", ""); } catch { }
        }

private void ApplyCropFill(MediaPlayer player, Control view)
        {
            if (player == null || view == null) return;
            var crop = PickNearestAspect(view.Width, view.Height);

            // Try property first (LibVLCSharp >= 3)
            VlcTrySet(player, "CropGeometry", crop);

            // Fallback to method if property is unavailable
            VlcTryCall(player, "SetVideoCropGeometry", crop);
            VlcTryCall(player, "SetCropGeometry", crop);

            // Ensure default aspect/scale is auto (avoid extra letterboxing)
            VlcTrySet(player, "AspectRatio", null);
            VlcTrySet(player, "Scale", 0f);
            VlcTryCall(player, "SetVideoScale", 0f);
        }

        private void StartCam2FreezeWatchdog()
        {
            if (!AppConfig.Camera2Settings.AutoRefreshOnFreeze)
            {
                StopCam2FreezeWatchdog();
                return;
            }

            if (_cam2FreezeTimer == null)
            {
                _cam2FreezeTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                _cam2FreezeTimer.Tick += (_, __) =>
                {
                    if (!AppConfig.Camera2Settings.AutoRefreshOnFreeze)
                    {
                        StopCam2FreezeWatchdog();
                        return;
                    }

                    if (_isClosing || IsDisposed) return;
                    if (_mp2 == null) return;

                    try
                    {
                        // Only monitor when playing
                        var state = _mp2.GetType().GetProperty("State")?.GetValue(_mp2);
                        if (state != null && state.ToString() != null &&
                            !state.ToString()!.Contains("Playing", StringComparison.OrdinalIgnoreCase))
                            return;
                    }
                    catch { }

                    long t;
                    try { t = (long)(_mp2.GetType().GetProperty("Time")?.GetValue(_mp2) ?? 0L); }
                    catch { t = 0L; }

                    var now = DateTime.UtcNow;
                    if (t > _cam2LastTimeMs + 200)
                    {
                        _cam2LastTimeMs = t;
                        _cam2LastProgressUtc = now;
                        return;
                    }

                    if (_cam2LastProgressUtc == DateTime.MinValue)
                        _cam2LastProgressUtc = now;

                    if (now - _cam2LastProgressUtc < TimeSpan.FromSeconds(8))
                        return;

                    // Freeze detected -> restart stream
                    _cam2LastProgressUtc = now;
                    SafeSetStatus("Inngang til vaskehallen: restart (bilde står)");

                    if (!string.IsNullOrWhiteSpace(_cam2Url) && _videoView2 != null)
                    {
                        try
                        {
                            StartStream(_cam2Url, _videoView2, ref _mp2, "Inngang til vaskehallen", _cam2User, _cam2Pass);
                        }
                        catch { }
                    }
                };
            }

            _cam2LastTimeMs = -1;
            _cam2LastProgressUtc = DateTime.UtcNow;
            try { _cam2FreezeTimer.Start(); } catch { }
        }

        private void StopCam2FreezeWatchdog()
        {
            try { _cam2FreezeTimer?.Stop(); } catch { }
            _cam2LastTimeMs = -1;
            _cam2LastProgressUtc = DateTime.MinValue;
        }


        private void StopCameraPreview()
        {
            StopCam2FreezeWatchdog();
            // Stop snapshot timers first (they may still be downloading)
            StopSnapshotPreview2();
            StopSnapshotPreview3();

            void StopOne(ref MediaPlayer? player, VideoView? view)
            {
                try { player?.Stop(); } catch { }
                try { player?.Dispose(); } catch { }
                player = null;
                try { if (view != null) view.MediaPlayer = null; } catch { }
            }

            StopOne(ref _mp1, _videoView1);
            StopOne(ref _mp2, _videoView2);
            StopOne(ref _mp3, _videoView3);

            try { _vlc?.Dispose(); } catch { }
            _vlc = null;

            try
            {
                if (_lblKameraAktiv != null)
                {
                    _lblKameraAktiv.Text = "Kamera: Ikke aktiv";
                    _lblKameraAktiv.BackColor = Color.DarkGray;
                }
            }
            catch { }

            _previewRunning = false;
        }

        // ------------ HTTP SNAPSHOT PREVIEW (AXIS / older encoders) -----------------

        private static bool ShouldUseSnapshot(CameraPreviewSettings? s, string? url)
        {
            if (s == null || !s.Enabled) return false;
            var proto = (s.Protocol ?? "").Trim().ToLowerInvariant();
            if (proto == "axis_http_mjpeg") return true; // our "port 80" mode
            if (!string.IsNullOrWhiteSpace(url))
            {
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string BuildAxisSnapshotUrl(CameraPreviewSettings s)
        {
            // If user provided an override URL and it already points to image.cgi / jpg, use it.
            var overrideUrl = (s.RtspUrl ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(overrideUrl) &&
                (overrideUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 overrideUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                (overrideUrl.Contains("image.cgi", StringComparison.OrdinalIgnoreCase) ||
                 overrideUrl.Contains("jpg", StringComparison.OrdinalIgnoreCase)))
            {
                return overrideUrl;
            }

            var host = (s.Host ?? "").Trim();
            if (string.IsNullOrWhiteSpace(host)) return "";

            var port = s.Port;
            if (port < 1 || port > 65535) port = 80;
            if (port == 554) port = 80;

            // Default snapshot endpoint for Axis
            var path = (s.Path ?? "").Trim();
            string snapPath;

            if (!string.IsNullOrWhiteSpace(path) &&
                (path.Contains("image.cgi", StringComparison.OrdinalIgnoreCase) ||
                 path.Contains("/jpg/", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
            {
                // User already points to a snapshot endpoint (cgi or static jpg)
                snapPath = path;
            }
            else if (!string.IsNullOrWhiteSpace(path) && path.Contains("axis-cgi", StringComparison.OrdinalIgnoreCase))
            {
                // Common Axis CGI snapshot
                snapPath = "/axis-cgi/jpg/image.cgi";
            }
            else if (!string.IsNullOrWhiteSpace(path) &&
                     (path.Contains("mjpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mjpg", StringComparison.OrdinalIgnoreCase)))
            {
                // Older Axis firmwares sometimes expose snapshot as static jpg
                snapPath = "/jpg/image.jpg";
            }
            else
            {
                // Safe default for Axis
                snapPath = "/axis-cgi/jpg/image.cgi";
            }

            if (!snapPath.StartsWith("/")) snapPath = "/" + snapPath;

            var url = $"http://{host}:{port}{snapPath}";
            if (s.Channel > 0 && !url.Contains("camera=", StringComparison.OrdinalIgnoreCase))
                url += snapPath.Contains("?") ? $"&camera={s.Channel}" : $"?camera={s.Channel}";
            return url;
        }

        private void StartSnapshotPreview2(CameraPreviewSettings s)
        {
            if (_picView2 == null) return;
            StopSnapshotPreview2();

            // New snapshot session (invalidates any in-flight async work)
            _snapSession2++;
            var session = _snapSession2;

            var url = BuildAxisSnapshotUrl(s);
            if (string.IsNullOrWhiteSpace(url)) return;

            _snapFail2 = 0;
            _snapBusy2 = false;

            try
            {
                var handler = new HttpClientHandler
                {
                    UseProxy = false,
                    PreAuthenticate = true
                };
                var user = (s.Username ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(user))
                    handler.Credentials = new NetworkCredential(user, s.Password ?? "");

                _http2 = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(4)
                };
            }
            catch
            {
                _http2 = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            }

            _picView2.Visible = true;
            if (_videoView2 != null) _videoView2.Visible = false;

            _snapTimer2 = new System.Windows.Forms.Timer { Interval = 250 };
            _snapTimer2.Tick += async (_, __) =>
            {
                if (_snapBusy2 || _http2 == null) return;
                if (_isClosing || IsDisposed || !IsHandleCreated) return;
                if (session != _snapSession2) return;
                _snapBusy2 = true;
                try
                {
                    var bytes = await _http2.GetByteArrayAsync(url);
                    using var ms = new MemoryStream(bytes);
                    using var img = Image.FromStream(ms);
                    var bmp = new Bitmap(img);

                    // After await, the form might be closing or the session invalidated.
                    if (_isClosing || IsDisposed || !IsHandleCreated || session != _snapSession2)
                    {
                        bmp.Dispose();
                        return;
                    }

                    try
                    {
                        var old = _picView2.Image;
                        _picView2.Image = bmp;
                        old?.Dispose();
                        if (_camOverlay2 != null) _camOverlay2.Visible = false;
                    }
                    catch
                    {
                        bmp.Dispose();
                    }

                    _snapFail2 = 0;
                }
                catch (Exception ex)
                {
                    _snapFail2++;
                    if (_snapFail2 >= 6)
                    {
                        if (!_isClosing && !IsDisposed && IsHandleCreated && session == _snapSession2)
                        {
                            if (_camOverlay2 != null)
                            {
                                var msg = ex.Message;
                                if (msg.Length > 80) msg = msg.Substring(0, 80) + "...";
                                _camOverlay2.Text = "Inngang til vaskehallen\n(ingen tilkobling)\n" + msg;
                                _camOverlay2.Visible = true;
                            }
                        }
                    }
                }
                finally
                {
                    _snapBusy2 = false;
                }
            };
            _snapTimer2.Start();
        }

        private void StopSnapshotPreview2()
        {
            // Invalidate any in-flight async snapshot fetch
            _snapSession2++;
            try { _snapTimer2?.Stop(); } catch { }
            try { _snapTimer2?.Dispose(); } catch { }
            _snapTimer2 = null;
            try { _http2?.Dispose(); } catch { }
            _http2 = null;
            _snapBusy2 = false;

            try
            {
                if (_picView2 != null)
                {
                    var old = _picView2.Image;
                    _picView2.Image = null;
                    old?.Dispose();
                    _picView2.Visible = false;
                }
            }
            catch { }
        }

        private void StartSnapshotPreview3(CameraPreviewSettings s)
        {
            if (_picView3 == null) return;
            StopSnapshotPreview3();

            // New snapshot session (invalidates any in-flight async work)
            _snapSession3++;
            var session = _snapSession3;

            var url = BuildAxisSnapshotUrl(s);
            if (string.IsNullOrWhiteSpace(url)) return;

            _snapFail3 = 0;
            _snapBusy3 = false;

            try
            {
                var handler = new HttpClientHandler
                {
                    UseProxy = false,
                    PreAuthenticate = true
                };
                var user = (s.Username ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(user))
                    handler.Credentials = new NetworkCredential(user, s.Password ?? "");

                _http3 = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(4)
                };
            }
            catch
            {
                _http3 = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            }

            _picView3.Visible = true;
            if (_videoView3 != null) _videoView3.Visible = false;

            _snapTimer3 = new System.Windows.Forms.Timer { Interval = 250 };
            _snapTimer3.Tick += async (_, __) =>
            {
                if (_snapBusy3 || _http3 == null) return;
                if (_isClosing || IsDisposed || !IsHandleCreated) return;
                if (session != _snapSession3) return;
                _snapBusy3 = true;
                try
                {
                    var bytes = await _http3.GetByteArrayAsync(url);
                    using var ms = new MemoryStream(bytes);
                    using var img = Image.FromStream(ms);
                    var bmp = new Bitmap(img);

                    if (_isClosing || IsDisposed || !IsHandleCreated || session != _snapSession3)
                    {
                        bmp.Dispose();
                        return;
                    }

                    try
                    {
                        var old = _picView3.Image;
                        _picView3.Image = bmp;
                        old?.Dispose();
                        if (_camOverlay3 != null) _camOverlay3.Visible = false;
                    }
                    catch
                    {
                        bmp.Dispose();
                    }

                    _snapFail3 = 0;
                }
                catch (Exception ex)
                {
                    _snapFail3++;
                    if (_snapFail3 >= 6)
                    {
                        if (!_isClosing && !IsDisposed && IsHandleCreated && session == _snapSession3)
                        {
                            if (_camOverlay3 != null)
                            {
                                var msg = ex.Message;
                                if (msg.Length > 80) msg = msg.Substring(0, 80) + "...";
                                _camOverlay3.Text = "KAMERA 3\n(ingen tilkobling)\n" + msg;
                                _camOverlay3.Visible = true;
                            }
                        }
                    }
                }
                finally
                {
                    _snapBusy3 = false;
                }
            };
            _snapTimer3.Start();
        }

        private void StopSnapshotPreview3()
        {
            // Invalidate any in-flight async snapshot fetch
            _snapSession3++;
            try { _snapTimer3?.Stop(); } catch { }
            try { _snapTimer3?.Dispose(); } catch { }
            _snapTimer3 = null;
            try { _http3?.Dispose(); } catch { }
            _http3 = null;
            _snapBusy3 = false;

            try
            {
                if (_picView3 != null)
                {
                    var old = _picView3.Image;
                    _picView3.Image = null;
                    old?.Dispose();
                    _picView3.Visible = false;
                }
            }
            catch { }
        }

        // ------------ AUTOMATYCZNE CZYTANIE TABLICY Z PODGLĄDU ---------

        private async void BtnCamRead_Click(object sender, EventArgs e)
        {
            if (_lastCamFrame == null)
            {
                MessageBox.Show(this, "Ingen bilde fra kamera.", "Info",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                byte[] imageBytes;
                using (var ms = new MemoryStream())
                {
                    _lastCamFrame.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                    imageBytes = ms.ToArray();
                }

                string plate = await AnprClient.RecognizePlateAsync(imageBytes);
                if (!string.IsNullOrEmpty(plate))
                {
                    var rawPlate = CanonicalizePlateCandidate(plate);
                    if (!IsLikelyNorwegianPlate(rawPlate))
                    {
                        if (_lblStatus != null)
                            _lblStatus.Text = $"ANPR: ignorert '{rawPlate}' (ufullstendig/ugyldig skilt)";
                        return;
                    }

                    var displayPlate = FormatPlateForDisplay(rawPlate);

                    // 1) pokaż tablicę (z odstępem)
                    _txtRegnr.Text = displayPlate;

                    // 2) od razu rejestruj mycie
                    RegistrerVaskFraTekstboksen();
                }

                else
                {
                    MessageBox.Show(this,
                        "Fant ingen skilt i svaret fra ANPR-tjenesten.",
                        "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Feil ved automatisk lesing:\n\n" + ex.ToString(),
                    "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string FormatPlateForDisplay(string regnr)
        {
            if (string.IsNullOrWhiteSpace(regnr)) return "";

            // usuń spacje, myślniki, kropki
            var s = new string(regnr.Trim()
                .Replace(" ", "")
                .Replace("-", "")
                .Replace(".", "")
                .ToUpperInvariant()
                .ToCharArray());

            // standard NO used in this solution: 2 letters + 5 digits (e.g. AE41646, EB90663)
            if (ExcelRepository.IsStrictNorwegianPlate(s))
            {
                // wstaw spację po 2 literach, reszta bez zmian
                return s.Substring(0, 2) + " " + s.Substring(2);
            }

            return regnr.Trim();
        }

        // ------------ INFO O POJEŹDZIE -----------------
        private void OppdaterKjoretoyInfo(string regnr)
        {
            if (_lblKjoretoyInfo == null) return;

            _lblKjoretoyInfo.Clear();
            SetWarningIcon(false);

            if (string.IsNullOrWhiteSpace(regnr))
            {
                _lblInternnr.Text = "";
                _lblKjoretoyInfo.SelectionFont = new Font(_lblKjoretoyInfo.Font, FontStyle.Bold);
                _lblKjoretoyInfo.SelectionColor = _lblKjoretoyInfo.ForeColor;
                _lblKjoretoyInfo.AppendText("Kjøretøyinformasjon");
                return;
            }

            var key = ExcelRepository.NormalizeRegnr(regnr);

            void AddLine(string caption, string value)
            {
                _lblKjoretoyInfo.SelectionColor = _lblKjoretoyInfo.ForeColor;
                _lblKjoretoyInfo.SelectionFont = new Font(_lblKjoretoyInfo.Font, FontStyle.Regular);
                _lblKjoretoyInfo.AppendText(caption);

                _lblKjoretoyInfo.SelectionColor = _lblKjoretoyInfo.ForeColor;
                _lblKjoretoyInfo.SelectionFont = new Font(_lblKjoretoyInfo.Font, FontStyle.Bold);
                _lblKjoretoyInfo.AppendText(value + Environment.NewLine);
            }

            void AddStatus(string text, Color color)
            {
                _lblKjoretoyInfo.AppendText(Environment.NewLine);
                _lblKjoretoyInfo.SelectionFont = new Font(_lblKjoretoyInfo.Font, FontStyle.Bold);
                _lblKjoretoyInfo.SelectionColor = color;
                _lblKjoretoyInfo.AppendText(text + Environment.NewLine);
                _lblKjoretoyInfo.SelectionColor = _lblKjoretoyInfo.ForeColor;
            }

            if (_register != null && _register.TryGetValue(key, out var info))
            {
                // keep textbox display consistent (space after letters)
                var disp = !string.IsNullOrWhiteSpace(info.Registreringsnummer)
                    ? FormatPlateForDisplay(info.Registreringsnummer)
                    : FormatPlateForDisplay(regnr);

                if (_txtRegnr != null &&
                    !string.Equals(_txtRegnr.Text.Trim(), disp, StringComparison.OrdinalIgnoreCase))
                {
                    // Updating the textbox triggers TextChanged -> OppdaterKjoretoyInfo again.
                    // Return here so the outer call does not append the same vehicle details twice.
                    _txtRegnr.Text = disp;
                    return;
                }

                _lblInternnr.Text = !string.IsNullOrWhiteSpace(info.Internnr)
                    ? $"Internnr: {info.Internnr}"
                    : "";

                string selskapVisning = !string.IsNullOrWhiteSpace(info.SelskapNavn)
                    ? info.SelskapNavn
                    : info.Selskap.ToString();

                string type = string.IsNullOrWhiteSpace(info.TypeKjoretoy) ? "(ukjent type)" : info.TypeKjoretoy;

                AddLine("Reg. nr: ", disp);

                if (!string.IsNullOrWhiteSpace(info.Merke)) AddLine("Merke: ", info.Merke);
                if (!string.IsNullOrWhiteSpace(info.Vin)) AddLine("VIN: ", info.Vin);
                if (!string.IsNullOrWhiteSpace(info.Lengde)) AddLine("Lengde: ", info.Lengde);

                AddLine("Selskap: ", selskapVisning);
                AddLine("Type kjøretøy: ", type);

                if (info.Unntak)
                    AddStatus("UNNTAK – vask er gratis.", Color.DarkGreen);
                else
                    AddStatus("Ordinær pris.", Color.DarkGreen);

                if (!string.IsNullOrWhiteSpace(info.Kommentar))
                    AddLine("Kommentar: ", info.Kommentar);
            }
            else
            {
                _lblInternnr.Text = "";
                var disp = FormatPlateForDisplay(regnr);

                AddLine("Reg. nr: ", disp);

                // red + bold warning like on screenshot
                _lblKjoretoyInfo.AppendText(Environment.NewLine);
                _lblKjoretoyInfo.SelectionFont = new Font(_lblKjoretoyInfo.Font, FontStyle.Bold);
                _lblKjoretoyInfo.SelectionColor = Color.DarkRed;
                _lblKjoretoyInfo.AppendText("Kjøretøyet finnes ikke i registeret\n\n\n(Uregistrert kjøretøy).");
                _lblKjoretoyInfo.SelectionColor = _lblKjoretoyInfo.ForeColor;

                SetWarningIcon(true);
            }
        }

        // ---------------------------------------------
        // TIDE – RAPPORT (daglig / ukentlig / månedlig / kvartalsvis / årlig)
        // ---------------------------------------------
        public class TideRapportForm : Form
        {
            private readonly ExcelRepository _repo;
            private Dictionary<string, KjoretoyInfo> _register = new();
            private List<VaskeHendelse> _alleVask;
            private List<TideRapportRad> _filtrert = new();

            private sealed class TideRapportRad
            {
                public DateTime DatoTid { get; set; }
                public string Registreringsnummer { get; set; } = "";
                public string Internnr { get; set; } = "";
                public string Skift { get; set; } = "";
                public int SkiftNr { get; set; }
                public DateTime SkiftStart { get; set; }
                public DateTime SkiftSlutt { get; set; }
                public string TypeKjoretoy { get; set; } = "";
                public string Sesong { get; set; } = "";
                public string Status { get; set; } = "";
                public decimal Kostnad { get; set; }
            }

            private sealed class RapportFilterInfo
            {
                public DateTime Fra { get; set; }
                public DateTime Til { get; set; }
                public Func<TideRapportRad, bool> Match { get; set; } = _ => true;
                public string OppsummeringTekst { get; set; } = "";
                public string UtskriftTekst { get; set; } = "";
            }

            private ComboBox _cmbType;
            private ComboBox _cmbSkift;
            private DateTimePicker _dtpStart;
            private DataGridView _grid;
            private Label _lblSum;

            private System.Windows.Forms.Timer? _autoRefreshTimer;
            private bool _autoRefreshBusy;
            private int _lastSigCount;
            private DateTime _lastSigMax = DateTime.MinValue;
            private PrintDocument _printDoc;
            private int _printIndex;
            private bool _printSummaryPending;

            public TideRapportForm(ExcelRepository repo)
            {
                _repo = repo;

                Text = "Vaske-rapport";
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(1100, 700);
                Font = new Font(FontFamily.GenericSansSerif, 12f);

                ByggUi();

                _printDoc = new PrintDocument();
                _printDoc.DefaultPageSettings.Landscape = false;
                _printDoc.DefaultPageSettings.Margins = new Margins(40, 40, 40, 40);
                _printDoc.BeginPrint += (_, __) => ResetPrintState();
                _printDoc.PrintPage += PrintDoc_PrintPage;

                _alleVask = _repo.LastAlleVask();
                _register = _repo.LastKjoretoyRegister();
                try
                {
                    _lastSigCount = _alleVask?.Count ?? 0;
                    _lastSigMax = (_alleVask != null && _alleVask.Count > 0) ? _alleVask.Max(v => v.DatoTid) : DateTime.MinValue;
                }
                catch { _lastSigCount = 0; _lastSigMax = DateTime.MinValue; }

                _cmbType.SelectedIndex = 1; // Ukentlig
                Oppdater();
            }

            private void ByggUi()
            {
                var lblType = new Label
                {
                    Text = "Rapporttype:",
                    AutoSize = true
                };

                _cmbType = new ComboBox
                {
                    Width = 90,
                    Height = 30
                };
                _cmbType.Items.AddRange(new object[]
                {
                    "Skift",
                    "Daglig",
                    "Ukentlig",
                    "Månedlig",
                    "Kvartalsvis",
                    "Årlig",
                });
                _cmbType.SelectedIndexChanged += (s, e) => Oppdater();

                var lblStart = new Label
                {
                    Text = "Dato:",
                    Width = 60,
                    Height = 30
                };

                _dtpStart = new DateTimePicker
                {
                    Width = 120,
                    Format = DateTimePickerFormat.Short,
                    Value = DateTime.Today
                };
                _dtpStart.ValueChanged += (s, e) => Oppdater();

                var lblSkift = new Label
                {
                    Text = "Skift:",
                    Width = 50,
                    Height = 30
                };

                _cmbSkift = new ComboBox
                {
                    Width = 80,
                    Height = 30
                };
                _cmbSkift.Items.AddRange(new object[]
                {
                    "Alle skift",
                    "Skift 1",
                    "Skift 2",
                    "Skift 3"
                });
                _cmbSkift.SelectedIndexChanged += (s, e) => Oppdater();

                var btnOppdater = new Button
                {
                    Text = "Oppdater",
                    Width = 95,
                    Height = 30
                };
                btnOppdater.Click += (s, e) => Oppdater();

                var btnPrint = new Button
                {
                    Text = "Forhåndsvisning / PDF",
                    Width = 180,
                    Height = 30
                };
                btnPrint.Click += BtnPrint_Click;

                var btnLukk = new Button
                {
                    Text = "Lukk",
                    Width = 80,
                    Height = 30
                };
                btnLukk.Click += (s, e) => Close();

                _lblSum = new Label
                {
                    Location = new Point(20, 50),
                    AutoSize = false,
                    Width = ClientSize.Width - 40,
                    Height = 40,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                _grid = new DataGridView
                {
                    Location = new Point(20, 90),
                    Size = new Size(ClientSize.Width - 40, ClientSize.Height - 110),
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                    ReadOnly = true,
                    AllowUserToAddRows = false,
                    AllowUserToDeleteRows = false,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                    AutoGenerateColumns = false
                };

                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Skift",
                    DataPropertyName = "Skift",
                    FillWeight = 40
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Dato",
                    DataPropertyName = "DatoTid",
                    FillWeight = 115,
                    DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd HH:mm" }
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Reg.nr",
                    DataPropertyName = "Registreringsnummer",
                    FillWeight = 90
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Internnr.",
                    DataPropertyName = "Internnr",
                    FillWeight = 85
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Type kjøretøy",
                    DataPropertyName = "TypeKjoretoy",
                    FillWeight = 150
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Sesong",
                    DataPropertyName = "Sesong",
                    FillWeight = 75
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Status",
                    DataPropertyName = "Status",
                    FillWeight = 75
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Kostnad",
                    DataPropertyName = "Kostnad",
                    FillWeight = 85,
                    DefaultCellStyle = new DataGridViewCellStyle { Format = "0.00" }
                });

                int x0 = _grid.Left + _grid.RowHeadersWidth;
                const int yLabel = 20;
                const int yCtrl = 15;
                const int gap = 8;
                const int gapBlock = 18;

                lblType.Location = new Point(x0, yLabel);
                _cmbType.Location = new Point(lblType.Right + gap, yCtrl);

                lblStart.Location = new Point(_cmbType.Right + gapBlock, yLabel);
                _dtpStart.Location = new Point(lblStart.Right + gap, yCtrl);

                lblSkift.Location = new Point(_dtpStart.Right + gapBlock, yLabel);
                _cmbSkift.Location = new Point(lblSkift.Right + gap, yCtrl);

                btnOppdater.Location = new Point(_cmbSkift.Right + gapBlock, yCtrl);
                btnPrint.Location = new Point(btnOppdater.Right + gap, yCtrl);
                btnLukk.Location = new Point(btnPrint.Right + gap, yCtrl);

                Controls.Add(lblType);
                Controls.Add(_cmbType);
                Controls.Add(lblStart);
                Controls.Add(_dtpStart);
                Controls.Add(lblSkift);
                Controls.Add(_cmbSkift);
                Controls.Add(btnOppdater);
                Controls.Add(btnPrint);
                Controls.Add(btnLukk);
                Controls.Add(_lblSum);
                Controls.Add(_grid);
            }

            private void Oppdater()
            {
                try { _alleVask = _repo.LastAlleVask(); } catch { }
                if (_alleVask == null) return;

                var filter = LagRapportFilter();

                // Refresh vehicle register (for Internnr column)
                try
                {
                    var reg = _repo.LastKjoretoyRegister();
                    if (reg != null) _register = reg;
                }
                catch { }

                _filtrert = _alleVask
                    .Where(v => v.Selskap == SelskapType.Tide &&
                                v.DatoTid >= filter.Fra &&
                                v.DatoTid <= filter.Til)
                    .Select(v =>
                    {
                        var key = ExcelRepository.NormalizeRegnr(v.Registreringsnummer);
                        string intern = "";
                        try
                        {
                            if (_register != null && _register.TryGetValue(key, out var info) && info != null)
                                intern = info.Internnr ?? "";
                        }
                        catch { }

                        var skift = TurnusHelper.GetShift(v.DatoTid);

                        return new TideRapportRad
                        {
                            Skift = skift.Display,
                            SkiftNr = skift.Nr,
                            SkiftStart = skift.StartLocal,
                            SkiftSlutt = skift.EndLocal,
                            DatoTid = v.DatoTid,
                            Registreringsnummer = v.Registreringsnummer,
                            Internnr = intern,
                            TypeKjoretoy = v.TypeKjoretoy,
                            Sesong = v.Sesong,
                            Status = v.Status,
                            Kostnad = v.Kostnad
                        };
                    })
                    .Where(v => filter.Match(v))
                    .OrderByDescending(v => v.DatoTid)
                    .ToList();

                _grid.DataSource = new BindingList<TideRapportRad>(_filtrert);

                int antall = _filtrert.Count;
                decimal sum = _filtrert.Sum(v => v.Kostnad);
                _lblSum.Text = $"{filter.OppsummeringTekst}, Vask: {antall}, Sum: {sum:0.00} kr";
            }

            private int? HentValgtSkiftNr()
            {
                string sel = _cmbSkift?.SelectedItem as string ?? "Alle skift";
                if (string.Equals(sel, "Skift 1", StringComparison.OrdinalIgnoreCase)) return 1;
                if (string.Equals(sel, "Skift 2", StringComparison.OrdinalIgnoreCase)) return 2;
                if (string.Equals(sel, "Skift 3", StringComparison.OrdinalIgnoreCase)) return 3;
                return null;
            }

            private string HentValgtSkiftTekst()
            {
                return _cmbSkift?.SelectedItem as string ?? "Alle skift";
            }

            private string HentValgtRapporttype()
            {
                return _cmbType?.SelectedItem as string ?? "Daglig";
            }

            private bool ErSkiftRapporttype()
            {
                return string.Equals(HentValgtRapporttype(), "Skift", StringComparison.OrdinalIgnoreCase);
            }

            private static bool ErHelg(DateTime dato)
            {
                return dato.DayOfWeek == DayOfWeek.Saturday || dato.DayOfWeek == DayOfWeek.Sunday;
            }

            private (DateTime fra, DateTime til, string display) BeregnEksaktSkiftPeriode(DateTime skiftdato, int skiftNr)
            {
                bool helg = ErHelg(skiftdato);

                switch (skiftNr)
                {
                    case 1:
                        {
                            DateTime start = helg ? skiftdato.AddHours(9) : skiftdato.AddHours(7);
                            DateTime slutt = helg ? skiftdato.AddHours(17) : skiftdato.AddHours(14).AddMinutes(30);
                            return (start, slutt, $"Skift 1, {start:yyyy-MM-dd HH:mm} – {slutt:yyyy-MM-dd HH:mm}");
                        }

                    case 2:
                        {
                            DateTime start = skiftdato.AddHours(15).AddMinutes(40);
                            DateTime slutt = skiftdato.AddHours(22);
                            string suffix = helg ? " (helg: ingen ordinær Skift 2)" : "";
                            return (start, slutt, $"Skift 2, {start:yyyy-MM-dd HH:mm} – {slutt:yyyy-MM-dd HH:mm}{suffix}");
                        }

                    case 3:
                        {
                            DateTime start = skiftdato.AddHours(22);
                            DateTime slutt = skiftdato.AddDays(1).AddHours(6);
                            return (start, slutt, $"Skift 3, {start:yyyy-MM-dd HH:mm} – {slutt:yyyy-MM-dd HH:mm}");
                        }

                    default:
                        {
                            DateTime start = skiftdato;
                            DateTime slutt = skiftdato.AddDays(1).AddTicks(-1);
                            return (start, slutt, $"{start:yyyy-MM-dd HH:mm} – {slutt:yyyy-MM-dd HH:mm}");
                        }
                }
            }

            private RapportFilterInfo LagRapportFilter()
            {
                DateTime d = _dtpStart.Value.Date;
                int? valgtSkiftNr = HentValgtSkiftNr();

                if (ErSkiftRapporttype())
                {
                    if (valgtSkiftNr.HasValue)
                    {
                        var (fraSkift, tilSkift, display) = BeregnEksaktSkiftPeriode(d, valgtSkiftNr.Value);
                        return new RapportFilterInfo
                        {
                            Fra = fraSkift,
                            Til = tilSkift,
                            Match = v => v.SkiftNr == valgtSkiftNr.Value && v.SkiftStart.Date == d,
                            OppsummeringTekst = $"Skift-periode: {display}",
                            UtskriftTekst = $"Skift-periode: {display}"
                        };
                    }

                    DateTime fraAlle = d;
                    DateTime tilAlle = d.AddDays(1).AddHours(6).AddTicks(-1);
                    string tekst = $"Skiftdato: {d:yyyy-MM-dd} (alle skift)";

                    return new RapportFilterInfo
                    {
                        Fra = fraAlle,
                        Til = tilAlle,
                        Match = v => v.SkiftStart.Date == d,
                        OppsummeringTekst = tekst,
                        UtskriftTekst = tekst
                    };
                }

                var (fra, til) = BeregnPeriode();
                string summary = $"Periode: {fra:yyyy-MM-dd} – {til:yyyy-MM-dd}";
                string valgtSkift = HentValgtSkiftTekst();
                if (!string.IsNullOrWhiteSpace(valgtSkift) &&
                    !valgtSkift.StartsWith("Alle", StringComparison.OrdinalIgnoreCase))
                {
                    summary += $", {valgtSkift}";
                }

                return new RapportFilterInfo
                {
                    Fra = fra,
                    Til = til,
                    Match = v => !valgtSkiftNr.HasValue || v.SkiftNr == valgtSkiftNr.Value,
                    OppsummeringTekst = summary,
                    UtskriftTekst = summary
                };
            }

            private (DateTime fra, DateTime til) BeregnPeriode()
            {
                DateTime d = _dtpStart.Value.Date;
                string type = HentValgtRapporttype();

                switch (type)
                {
                    case "Daglig":
                        return (d, d.AddDays(1).AddTicks(-1));

                    case "Ukentlig":
                        int delta = (int)d.DayOfWeek - (int)DayOfWeek.Monday;
                        if (delta < 0) delta += 7;
                        DateTime mandag = d.AddDays(-delta);
                        DateTime sondag = mandag.AddDays(7).AddTicks(-1);
                        return (mandag, sondag);

                    case "Månedlig":
                        DateTime first = new DateTime(d.Year, d.Month, 1);
                        DateTime last = first.AddMonths(1).AddTicks(-1);
                        return (first, last);

                    case "Kvartalsvis":
                        int q = (d.Month - 1) / 3;
                        int startMonth = q * 3 + 1;
                        DateTime qStart = new DateTime(d.Year, startMonth, 1);
                        DateTime qEnd = qStart.AddMonths(3).AddTicks(-1);
                        return (qStart, qEnd);

                    case "Årlig":
                        DateTime yStart = new DateTime(d.Year, 1, 1);
                        DateTime yEnd = yStart.AddYears(1).AddTicks(-1);
                        return (yStart, yEnd);

                    case "Skift":
                        int? valgtSkiftNr = HentValgtSkiftNr();
                        if (valgtSkiftNr.HasValue)
                        {
                            var (fraSkift, tilSkift, _) = BeregnEksaktSkiftPeriode(d, valgtSkiftNr.Value);
                            return (fraSkift, tilSkift);
                        }
                        return (d, d.AddDays(1).AddHours(6).AddTicks(-1));

                    default:
                        return (d, d.AddDays(1).AddTicks(-1));
                }
            }

            private void BtnPrint_Click(object sender, EventArgs e)
            {
                if (_filtrert == null || _filtrert.Count == 0)
                {
                    MessageBox.Show(this, "Ingen data for valgt periode/skift.", "Info",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                ResetPrintState();

                using (var preview = new PrintPreviewDialog())
                {
                    preview.Document = _printDoc;
                    preview.Width = 1100;
                    preview.Height = 800;
                    preview.StartPosition = FormStartPosition.CenterParent;
                    preview.ShowIcon = false;
                    preview.ShowDialog(this);
                }

                // PDF: ved utskrift kan du velge systemskriver som "Microsoft Print to PDF"
                // eller en annen virtuell PDF-skriver.
            }

            private void ResetPrintState()
            {
                _printIndex = 0;
                _printSummaryPending = false;
            }

            private void PrintDoc_PrintPage(object sender, PrintPageEventArgs e)
            {
                Graphics g = e.Graphics;
                var culture = new CultureInfo("nb-NO");
                using Font fontTitle = new Font("Arial", 18, FontStyle.Bold);
                using Font fontHeader = new Font("Arial", 12, FontStyle.Bold);
                using Font font = new Font("Arial", 12f);

                var filter = LagRapportFilter();

                int top = e.MarginBounds.Top;
                int left = e.MarginBounds.Left;
                int y = top;

                string title = "Vaske rapport";
                SizeF titleSize = g.MeasureString(title, fontTitle);
                int titleX = left + (e.MarginBounds.Width - (int)titleSize.Width) / 2;
                g.DrawString(title, fontTitle, Brushes.Black, titleX, y);
                y += (int)titleSize.Height + 8;

                string periodeTxt = filter.UtskriftTekst;
                g.DrawString(periodeTxt, fontHeader, Brushes.Black, left, y);
                y += fontHeader.Height + 10;

                if (!_printSummaryPending)
                {
                    int tableLeft = left;
                    int[] colWidths = { 62, 140, 85, 72, 120, 72, 72, 76 };
                    int[] colX = new int[colWidths.Length + 1];
                    colX[0] = tableLeft;
                    for (int i = 1; i < colX.Length; i++)
                        colX[i] = colX[i - 1] + colWidths[i - 1];

                    int tableRight = colX[colX.Length - 1];
                    int rowHeight = font.Height + 6;
                    int curY = y;

                    g.DrawLine(Pens.Black, tableLeft, curY, tableRight, curY);
                    curY += 3;
                    g.DrawString("Skift", fontHeader, Brushes.Black, colX[0] + 4, curY);
                    g.DrawString("Dato", fontHeader, Brushes.Black, colX[1] + 4, curY);
                    g.DrawString("Reg.nr", fontHeader, Brushes.Black, colX[2] + 4, curY);
                    g.DrawString("Internnr.", fontHeader, Brushes.Black, colX[3] + 4, curY);
                    g.DrawString("Type kjøretøy", fontHeader, Brushes.Black, colX[4] + 4, curY);
                    g.DrawString("Sesong", fontHeader, Brushes.Black, colX[5] + 4, curY);
                    g.DrawString("Status", fontHeader, Brushes.Black, colX[6] + 4, curY);
                    g.DrawString("Kostnad", fontHeader, Brushes.Black, colX[7] + 4, curY);

                    curY = y + rowHeight;
                    g.DrawLine(Pens.Black, tableLeft, curY, tableRight, curY);

                    while (_printIndex < _filtrert.Count)
                    {
                        if (curY + rowHeight > e.MarginBounds.Bottom - 70)
                        {
                            for (int i = 0; i < colX.Length; i++)
                                g.DrawLine(Pens.Black, colX[i], y, colX[i], curY);

                            e.HasMorePages = true;
                            return;
                        }

                        var v = _filtrert[_printIndex++];

                        curY += 2;
                        g.DrawString(v.Skift, font, Brushes.Black, colX[0] + 4, curY);
                        g.DrawString(v.DatoTid.ToString("yyyy-MM-dd HH:mm", culture), font, Brushes.Black, colX[1] + 4, curY);
                        g.DrawString(v.Registreringsnummer, font, Brushes.Black, colX[2] + 4, curY);
                        g.DrawString(v.Internnr, font, Brushes.Black, colX[3] + 4, curY);
                        g.DrawString(v.TypeKjoretoy, font, Brushes.Black, colX[4] + 4, curY);
                        g.DrawString(v.Sesong, font, Brushes.Black, colX[5] + 4, curY);
                        g.DrawString(v.Status, font, Brushes.Black, colX[6] + 4, curY);

                        string belop = v.Kostnad.ToString("0.00", culture);
                        float belopWidth = g.MeasureString(belop, font).Width;
                        g.DrawString(belop, font, Brushes.Black, colX[8] - belopWidth - 4, curY);

                        curY += rowHeight - 2;
                        g.DrawLine(Pens.Black, tableLeft, curY, tableRight, curY);
                    }

                    for (int i = 0; i < colX.Length; i++)
                        g.DrawLine(Pens.Black, colX[i], y, colX[i], curY);

                    y = curY + 10;
                }

                int totalCount = _filtrert?.Count ?? 0;
                decimal totalSum = _filtrert?.Sum(v => v.Kostnad) ?? 0m;

                if (y + fontHeader.Height * 2 + 8 > e.MarginBounds.Bottom)
                {
                    _printSummaryPending = true;
                    e.HasMorePages = true;
                    return;
                }

                _printSummaryPending = false;
                g.DrawString($"Antall vask: {totalCount}", fontHeader, Brushes.Black, left, y);
                y += fontHeader.Height + 2;
                g.DrawString($"Sum: {totalSum:0.00} kr", fontHeader, Brushes.Black, left, y);

                e.HasMorePages = false;
            }
        }


        // ------------ SNAPSHOT WATCHER -----------------

        private void SetupSnapshotWatcher()
        {
            try
            {
                string dokumenter = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string folder = Path.Combine(dokumenter, "BilvaskRegistrering", "Snapshots");
                Directory.CreateDirectory(folder);

                _snapshotWatcher = new FileSystemWatcher(folder, "*.jpg");
                _snapshotWatcher.Created += SnapshotWatcher_Created;
                _snapshotWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Feil ved oppstart av snapshot-overvåking:\n\n" + ex.Message,
                    "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void SnapshotWatcher_Created(object sender, FileSystemEventArgs e)
        {
            try
            {
                // poczekaj aż plik się zapisze w całości
                await Task.Delay(700);
                await BehandleNyttSnapshotAsync(e.FullPath);
            }
            catch
            {
                // ignorujemy pojedyncze błędy
            }
        }

        private async Task BehandleNyttSnapshotAsync(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath)) return;

                byte[] imageBytes = File.ReadAllBytes(fullPath);
                string plate = await AnprClient.RecognizePlateAsync(imageBytes);
                if (string.IsNullOrEmpty(plate))
                    return;

                string regnrRaw = CanonicalizePlateCandidate(plate);
                if (!IsLikelyNorwegianPlate(regnrRaw))
                {
                    SafeSetStatus($"ANPR: ignorert '{regnrRaw}' (ufullstendig/ugyldig skilt)");
                    return;
                }

                string regnrDisplayLocal = FormatPlateForDisplay(regnrRaw);
                string key = ExcelRepository.NormalizeRegnr(regnrRaw);

                // <<< WAŻNE: info zainicjalizowane na null, żeby kompilator był zadowolony
                KjoretoyInfo info = null;
                bool finnes = _register != null && _register.TryGetValue(key, out info);

                // jeżeli znamy pojazd – do logu zapisujemy wersję z bazy (ze spacją)
                string regnr = finnes && !string.IsNullOrWhiteSpace(info.Registreringsnummer)
                ? FormatPlateForDisplay(info.Registreringsnummer)
                : regnrDisplayLocal;

                SelskapType selskap = finnes ? info.Selskap : SelskapType.Ukjent;
                string typeKjoretoy = finnes ? info.TypeKjoretoy : "";

                DateTime now = DateTime.Now;
                string sesong = BestemSesong(now);

                string status;
                if (!finnes) status = "UregistrertKjøretøy";
                else if (info.Unntak) status = "Unntak";
                else status = "Normal";

                decimal pris = HentSesongPris(sesong, typeKjoretoy);
                decimal kostnad = status == "Unntak" ? 0m : pris;

                var hendelse = new VaskeHendelse
                {
                    DatoTid = now,
                    Registreringsnummer = regnr,
                    Selskap = selskap,
                    TypeKjoretoy = typeKjoretoy,
                    Sesong = sesong,
                    Status = status,
                    Kostnad = kostnad
                };

                _repo.LoggVask(hendelse);

                // Also persist the camera event to DB (so Worker/Admin can see it even if one PC is off).
                try
                {
                    var sink = AdminDbEventSink.TryCreateFromConfig();
                    if (sink != null)
                    {
                        string? intern = (finnes && info != null && !string.IsNullOrWhiteSpace(info.Internnr)) ? info.Internnr : null;
                        string? selskapDb = null;
                        if (finnes && info != null)
                        {
                            if (!string.IsNullOrWhiteSpace(info.SelskapNavn))
                                selskapDb = info.SelskapNavn;
                            else if (selskap != SelskapType.Ukjent)
                                selskapDb = selskap.ToString();
                        }

                        await sink.TryInsertAsync(
                            occurredAt: new DateTimeOffset(now),
                            plate: ExcelRepository.NormalizeRegnr(regnrRaw).ToUpperInvariant(),
                            internnr: intern,
                            vehicleType: string.IsNullOrWhiteSpace(typeKjoretoy) ? null : typeKjoretoy,
                            season: string.IsNullOrWhiteSpace(sesong) ? null : sesong,
                            status: string.IsNullOrWhiteSpace(status) ? null : status,
                            note: null,
                            selskap: selskapDb);
                    }
                }
                catch
                {
                    // Never crash snapshot pipeline if DB is unavailable.
                }
            }
            catch (Exception ex)
            {
                try
                {
                    string dokumenter = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    string logFolder = Path.Combine(dokumenter, "BilvaskRegistrering");
                    Directory.CreateDirectory(logFolder);
                    string logPath = Path.Combine(logFolder, "SnapshotErrors.log");
                    File.AppendAllText(logPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - " + ex + Environment.NewLine);
                }
                catch
                {
                    // ignorujemy błąd logowania
                }
            }
        }


        private decimal HentSesongPris(string sesong, string typeKjoretoy)
        {
            if (_sesongPriser == null) return 0m;

            string type = (typeKjoretoy ?? "").Trim();
            string typeKey = "";

            if (!string.IsNullOrEmpty(type))
            {
                if (type.IndexOf("stor", StringComparison.OrdinalIgnoreCase) >= 0)
                    typeKey = "Stor";
                else if (type.IndexOf("liten", StringComparison.OrdinalIgnoreCase) >= 0)
                    typeKey = "Liten";
                else
                    typeKey = type; // fallback – gdybyś miał inne typy
            }

            // 1) nowy format: "Sommer_Stor", "Vinter_Liten" itd.
            if (!string.IsNullOrEmpty(typeKey))
            {
                string comboKey = $"{sesong}_{typeKey}";
                if (_sesongPriser.TryGetValue(comboKey, out var prisCombo))
                    return prisCombo;
            }

            // 2) stary format – tylko Sommer/Vinter
            if (_sesongPriser.TryGetValue(sesong, out var pris))
                return pris;

            return 0m;
        }

        private string BestemSesong(DateTime dato)
        {
            return AppConfig.DetermineSeason(dato);
        }
        // ------------ REJESTRACJA VASK – z textboxa (sensor bramy / ANPR) -------------
        private void RegistrerVaskFraTekstboksen()
        {

            if (_txtRegnr == null)
                return;

            string regText = _txtRegnr.Text.Trim();
            if (string.IsNullOrWhiteSpace(regText))
            {
                MessageBox.Show(this,
                    "Ingen registreringsnummer oppgitt.",
                    "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // RAW – for logic/matching (no spaces)
            string regnrRaw = ExcelRepository.NormalizeRegnr(regText).ToUpperInvariant();
            regnrRaw = CanonicalizePlateCandidate(regnrRaw);

            // Suppress obvious non-plates (e.g. camera misread of SKYSS logo)
            if (!IsLikelyNorwegianPlate(regnrRaw))
            {
                MessageBox.Show(this,
                    "Ugyldig registreringsnummer. Sjekk at det er 2 bokstaver + 5 sifre (f.eks. AB12345).",
                    "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // DISPLAY – zawsze ładny format: "AE 41646"
            string regnrDisplay = FormatPlateForDisplay(regnrRaw);

            // klucz do rejestru
            string key = ExcelRepository.NormalizeRegnr(regnrRaw);


            KjoretoyInfo info = null;
            bool finnes = _register != null && _register.TryGetValue(key, out info);

            // jeżeli znamy pojazd – używamy formatu z bazy (np. \"EB 78024\")
            string regnr = finnes && !string.IsNullOrWhiteSpace(info.Registreringsnummer)
                ? FormatPlateForDisplay(info.Registreringsnummer)
                : regnrDisplay;


            SelskapType selskap = finnes ? info.Selskap : SelskapType.Ukjent;
            string typeKjoretoy = finnes ? (info.TypeKjoretoy ?? "") : "";

            DateTime now = DateTime.Now;
            string sesong = BestemSesong(now);

            string status;
            if (!finnes)
                status = "UregistrertKjøretøy";
            else if (info.Unntak)
                status = "Unntak";
            else
                status = "Normal";

            decimal pris = HentSesongPris(sesong, typeKjoretoy);
            decimal kostnad = (status == "Unntak") ? 0m : pris;

            var hendelse = new VaskeHendelse
            {
                DatoTid = now,
                Registreringsnummer = regnr,
                Selskap = selskap,
                TypeKjoretoy = typeKjoretoy,
                Sesong = sesong,
                Status = status,
                Kostnad = kostnad
            };

            try
            {
                _repo.LoggVask(hendelse);

                // Dual-write: save also to Postgres (does not block UI; CSV remains local log)
                // IMPORTANT: "WorkerOnlyUnntak" is a Worker-side filter only. Admin should always insert
                // all events so the control lists/statistics can show them.
                if (DbConfig.Enabled && !string.IsNullOrWhiteSpace(DbConfig.ConnectionString))
                {
                    string? intern = (finnes && info != null && !string.IsNullOrWhiteSpace(info.Internnr)) ? info.Internnr : null;
                    string? selskapDb = null;
                    if (finnes && info != null)
                    {
                        // Prefer explicit company name from register; fall back to enum if meaningful.
                        if (!string.IsNullOrWhiteSpace(info.SelskapNavn))
                            selskapDb = info.SelskapNavn;
                        else if (selskap != SelskapType.Ukjent)
                            selskapDb = selskap.ToString();
                    }

                    var sink = AdminDbEventSink.TryCreateFromConfig();
                    if (sink != null)
                        _ = sink.TryInsertAsync(
                        occurredAt: new DateTimeOffset(now),
                        plate: ExcelRepository.NormalizeRegnr(regnrRaw).ToUpperInvariant(),
                        internnr: intern,
                        vehicleType: string.IsNullOrWhiteSpace(typeKjoretoy) ? null : typeKjoretoy,
                        season: string.IsNullOrWhiteSpace(sesong) ? null : sesong,
                        status: string.IsNullOrWhiteSpace(status) ? null : status,
                        note: null,
                        selskap: selskapDb
                    );
                }

                // odśwież info o pojeździe (Internnr, firma, itp.)
                OppdaterKjoretoyInfo(regnr);

                

                StartAutoClearCountdown();
// komunikat na dole pod tablicą
                string tekst;
                if (status == "Unntak")
                    tekst = $"Vask registrert for {regnr} – UNNTAK (gratis).";
                else if (status == "UregistrertKjøretøy")
                    tekst = $"Vask registrert for {regnr} – UREGISTRERT kjøretøy.";
                else
                    tekst = $"Vask registrert for {regnr} ({sesong}) – {kostnad:0.00} kr.";

                if (_lblStatus != null)
                    _lblStatus.Text = tekst;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Feil ved logging av vask:\n\n" + ex.ToString(),
                    "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ------------ REJESTRACJA VASK -----------------

        private void BtnRegistrerVask_Click(object sender, EventArgs e)
        {
            RegistrerVaskFraTekstboksen();
        }


        // ---------------------------------------------
        // KJØRETØYREGISTER
        // ---------------------------------------------
        public class KjoretoyForm : Form
        {
            private readonly ExcelRepository _repo;
            private BindingList<KjoretoyInfo> _liste = new BindingList<KjoretoyInfo>();
            private DataGridView _grid = null!;

            public KjoretoyForm(ExcelRepository repo)
            {
                _repo = repo;
                Text = "Kjøretøyregister";
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(1200, 700);
                Font = new Font(FontFamily.GenericSansSerif, 11f);

                ByggUi();
                LastData();
            }

            private void ByggUi()
            {
                _grid = new DataGridView
                {
                    Location = new Point(20, 20),
                    Size = new Size(1150, 580),
                    AllowUserToAddRows = false,
                    AutoGenerateColumns = false,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
                };

                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Registreringsnummer",
                    DataPropertyName = "Registreringsnummer",
                    Width = 180
                });

                _grid.Columns.Add(new DataGridViewComboBoxColumn
                {
                    HeaderText = "Selskap",
                    DataPropertyName = "Selskap",
                    DataSource = Enum.GetValues(typeof(SelskapType)),
                    Width = 170
                });

                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Type kjøretøy",
                    DataPropertyName = "TypeKjoretoy",
                    Width = 140
                });

                _grid.Columns.Add(new DataGridViewCheckBoxColumn
                {
                    HeaderText = "Unntak",
                    DataPropertyName = "Unntak",
                    Width = 80
                });

                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Kommentar",
                    DataPropertyName = "Kommentar"
                });

                var btnLeggTil = new Button
                {
                    Text = "Legg til",
                    Location = new Point(20, 620),
                    Width = 100,
                    Height = 35
                };
                btnLeggTil.Click += (_, __) => _liste.Add(new KjoretoyInfo());

                var btnSlett = new Button
                {
                    Text = "Slett",
                    Location = new Point(130, 620),
                    Width = 100,
                    Height = 35
                };
                btnSlett.Click += (_, __) =>
                {
                    if (_grid.CurrentRow?.DataBoundItem is KjoretoyInfo k)
                        _liste.Remove(k);
                };

                var btnLagre = new Button
                {
                    Text = "Lagre",
                    Location = new Point(240, 620),
                    Width = 100,
                    Height = 35
                };
                btnLagre.Click += (_, __) =>
                {
                    _repo.LagreAlleKjoretoy(_liste.ToList());
                    MessageBox.Show(this, "Lagret.", "OK", MessageBoxButtons.OK, MessageBoxIcon.Information);
                };

                var btnLukk = new Button
                {
                    Text = "Lukk",
                    Location = new Point(350, 620),
                    Width = 100,
                    Height = 35
                };
                btnLukk.Click += (_, __) => Close();

                Controls.Add(_grid);
                Controls.Add(btnLeggTil);
                Controls.Add(btnSlett);
                Controls.Add(btnLagre);
                Controls.Add(btnLukk);
            }

            private void LastData()
            {
                var liste = _repo.LastAlleKjoretoy();
                _liste = new BindingList<KjoretoyInfo>(liste);
                _grid.DataSource = _liste;
            }
        }
        // ---------------------------------------------
        // EGEN FLÅTE – edycja Internnr / VIN / itp.
        // ---------------------------------------------
        public class EgenFlateForm : Form
        {
            private readonly ExcelRepository _repo;
            private BindingList<KjoretoyInfo> _liste;
            private DataGridView _grid;

            public EgenFlateForm(ExcelRepository repo)
            {
                _repo = repo;
                Text = "Egen flåte";
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(1200, 700);
                Font = new Font(FontFamily.GenericSansSerif, 11f);

                ByggUi();
                LastData();
            }




            private void ByggUi()
            {
                _grid = new DataGridView
                {
                    Location = new Point(20, 20),
                    Size = new Size(1150, 580),
                    AllowUserToAddRows = false,
                    AutoGenerateColumns = false,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
                };

                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Internnr",
                    DataPropertyName = "Internnr",
                    Width = 80
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Registreringsnummer",
                    DataPropertyName = "Registreringsnummer",
                    Width = 120
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Merke",
                    DataPropertyName = "Merke",
                    Width = 120
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "VIN",
                    DataPropertyName = "Vin",
                    Width = 200
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Lengde",
                    DataPropertyName = "Lengde",
                    Width = 80
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Selskap",
                    DataPropertyName = "SelskapNavn",
                    Width = 120
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Type kjøretøy",
                    DataPropertyName = "TypeKjoretoy",
                    Width = 120
                });
                _grid.Columns.Add(new DataGridViewCheckBoxColumn
                {
                    HeaderText = "Unntak",
                    DataPropertyName = "Unntak",
                    Width = 60
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Kommentar",
                    DataPropertyName = "Kommentar"
                });

                var btnLeggTil = new Button
                {
                    Text = "Legg til",
                    Location = new Point(20, 620),
                    Width = 100,
                    Height = 35
                };
                btnLeggTil.Click += (s, e) => _liste.Add(new KjoretoyInfo());

                var btnSlett = new Button
                {
                    Text = "Slett",
                    Location = new Point(130, 620),
                    Width = 100,
                    Height = 35
                };
                btnSlett.Click += (s, e) =>
                {
                    if (_grid.CurrentRow != null && _grid.CurrentRow.DataBoundItem is KjoretoyInfo info)
                        _liste.Remove(info);
                };

                var btnLagre = new Button
                {
                    Text = "Lagre",
                    Location = new Point(240, 620),
                    Width = 100,
                    Height = 35
                };
                btnLagre.Click += (s, e) =>
                {
                    try
                    {
                        _repo.LagreAlleEgenFlateKjoretoy(new List<KjoretoyInfo>(_liste));
                        MessageBox.Show(this, "Egen flåte lagret.", "Info",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Feil ved lagring: " + ex.Message, "Feil",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };

                var btnLukk = new Button
                {
                    Text = "Lukk",
                    Location = new Point(350, 620),
                    Width = 100,
                    Height = 35
                };
                btnLukk.Click += (s, e) => Close();

                Controls.Add(_grid);
                Controls.Add(btnLeggTil);
                Controls.Add(btnSlett);
                Controls.Add(btnLagre);
                Controls.Add(btnLukk);
            }

            private void LastData()
            {
                var liste = _repo.LastAlleEgenFlateKjoretoy();
                _liste = new BindingList<KjoretoyInfo>(liste);
                _grid.DataSource = _liste;
            }
        }


        // ---------------------------------------------
        // LOGG – PODGLĄD MIESIĄCA z filtrami
        // ---------------------------------------------
        public class LoggForm : Form
        {
            private readonly ExcelRepository _repo;
            private Dictionary<string, KjoretoyInfo> _register;
            private List<VaskeHendelse> _alleVask;

            private DateTimePicker _dtpMnd;
            private CheckBox _chkBareUnntak;
            private CheckBox _chkBareAmbulanse;
            private CheckBox _chkBareHospitaldrift;
            private CheckBox _chkBarePoliti;
            private CheckBox _chkBareBrann;
            private CheckBox _chkBareKlargjører;
            private CheckBox _chkBareBuss;
            private CheckBox _chkBareTide;
            private DataGridView _grid;

            
            private System.Windows.Forms.Timer? _autoRefreshTimer;
            private bool _autoRefreshBusy;
            private int _lastSigCount;
            private DateTime _lastSigMax = DateTime.MinValue;
            public LoggForm(ExcelRepository repo)
            {
                _repo = repo;
                Text = "Loggkontroll";
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(1300, 900);
                Font = new Font(FontFamily.GenericSansSerif, 11f);

                ByggUi();
                LastData();
                OppdaterListe();

                StartAutoRefresh();
                FormClosed += (_, __) => StopAutoRefresh();
            }



            private void ByggUi()
            {
                // etykieta
                var lblInfo = new Label
                {
                    Text = "Velg måned for kontroll:",
                    AutoSize = true,
                    Location = new Point(40, 15)
                };
                Controls.Add(lblInfo);

                // wybór miesiąca
                _dtpMnd = new DateTimePicker
                {
                    Format = DateTimePickerFormat.Custom,
                    CustomFormat = "yyyy-MM",
                    ShowUpDown = true,
                    Width = 110,
                    Location = new Point(lblInfo.Right + 5, 10) // tuż za etykietą
                };
                _dtpMnd.ValueChanged += (s, e) => OppdaterListe();
                Controls.Add(_dtpMnd);

                // ---- CHECKBOXY -----------------------------------

                _chkBareUnntak = new CheckBox
                {
                    Text = "Vis bare Unntak-kjøretøy",
                    AutoSize = true,
                    Margin = new Padding(3, 3, 15, 3)
                };
                _chkBareUnntak.CheckedChanged += FilterCheckBox_CheckedChanged;

                _chkBareAmbulanse = new CheckBox
                {
                    Text = "Vis bare Ambulanse",
                    AutoSize = true,
                    Margin = new Padding(3, 3, 15, 3)
                };
                _chkBareAmbulanse.CheckedChanged += FilterCheckBox_CheckedChanged;

                _chkBareHospitaldrift = new CheckBox
                {
                    Text = "Vis bare Hospitaldrift",
                    AutoSize = true,
                    Margin = new Padding(3, 3, 15, 3)
                };
                _chkBareHospitaldrift.CheckedChanged += FilterCheckBox_CheckedChanged;

                _chkBarePoliti = new CheckBox
                {
                    Text = "Vis bare Politi",
                    AutoSize = true,
                    Margin = new Padding(3, 3, 15, 3)
                };
                _chkBarePoliti.CheckedChanged += FilterCheckBox_CheckedChanged;

                _chkBareBrann = new CheckBox
                {
                    Text = "Vis bare Røde Kors",
                    AutoSize = true,
                    Margin = new Padding(3, 3, 15, 3)
                };
                _chkBareBrann.CheckedChanged += FilterCheckBox_CheckedChanged;

                _chkBareKlargjører = new CheckBox
                {
                    Text = "Vis bare Klargjørerbil",
                    AutoSize = true,
                    Margin = new Padding(3, 3, 15, 3)
                };
                _chkBareKlargjører.CheckedChanged += FilterCheckBox_CheckedChanged;

                
                _chkBareKlargjører.Visible = false;
                _chkBareKlargjører.Enabled = false;
                _chkBareBuss = new CheckBox
                {
                    Text = "Vis bare Buss",
                    AutoSize = true,
                    Margin = new Padding(3, 3, 15, 3)
                };
                _chkBareBuss.CheckedChanged += FilterCheckBox_CheckedChanged;

                
                _chkBareBuss.Visible = false;
                _chkBareBuss.Enabled = false;
                _chkBareTide = new CheckBox
                {
                    Text = "Vis bare Tide",
                    AutoSize = true,
                    Margin = new Padding(3, 3, 15, 3)
                };
                _chkBareTide.CheckedChanged += FilterCheckBox_CheckedChanged;

                // panel z checkboxami – ustawiamy go zaraz za DateTimePicker
                int filtersX = _dtpMnd.Right + 15;

                var panelFilters = new FlowLayoutPanel
                {
                    Location = new Point(filtersX, 10),
                    Height = 60,   // << BYŁO 28 – teraz 2 linie są widoczne
                    Width = ClientSize.Width - filtersX - 20,
                    AutoSize = false,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                panelFilters.Controls.Add(_chkBareUnntak);
                panelFilters.Controls.Add(_chkBareAmbulanse);
                panelFilters.Controls.Add(_chkBareHospitaldrift);
                panelFilters.Controls.Add(_chkBarePoliti);
                panelFilters.Controls.Add(_chkBareBrann);
                // _chkBareKlargjører and _chkBareBuss are intentionally hidden in this view
                // (kept in code for backwards compatibility).
                panelFilters.Controls.Add(_chkBareTide);

                Controls.Add(panelFilters);

                // ---- GRID ----------------------------------------

                int gridTop = panelFilters.Bottom + 10;  // grid zaczyna się POD całym panelem

                _grid = new DataGridView
                {
                    Location = new Point(20, gridTop),
                    Size = new Size(ClientSize.Width - 40, ClientSize.Height - (gridTop + 20)),
                    ReadOnly = true,
                    AllowUserToAddRows = false,
                    AllowUserToDeleteRows = false,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
                };

                Controls.Add(_grid);
            }

            // tylko jeden checkbox "Vis bare ..." ma być włączony
            private void FilterCheckBox_CheckedChanged(object sender, EventArgs e)
            {
                if (!(sender is CheckBox changed))
                    return;

                if (changed.Checked)
                {
                    if (changed != _chkBareUnntak) _chkBareUnntak.Checked = false;
                    if (changed != _chkBareAmbulanse) _chkBareAmbulanse.Checked = false;
                    if (changed != _chkBareHospitaldrift) _chkBareHospitaldrift.Checked = false;
                    if (changed != _chkBarePoliti) _chkBarePoliti.Checked = false;
                    if (changed != _chkBareBrann) _chkBareBrann.Checked = false;
                    if (changed != _chkBareKlargjører) _chkBareKlargjører.Checked = false;
                    if (changed != _chkBareBuss) _chkBareBuss.Checked = false;
                    if (changed != _chkBareTide) _chkBareTide.Checked = false;
                }

                OppdaterListe();
            }


            private void LastData()
            {
                _register = _repo.LastKjoretoyRegister();
                _alleVask = _repo.LastAlleVask();
            }

            private void OppdaterListe()
            {
                // Refresh from DB/CSV on every update so new washes registered in Worker
                // become visible immediately in Loggkontroll without restarting the form.
                try
                {
                    var reg = _repo.LastKjoretoyRegister();
                    if (reg != null) _register = reg;
                }
                catch { /* ignore */ }

                try
                {
                    var fresh = _repo.LastAlleVask();
                    if (fresh != null) _alleVask = fresh;
                }
                catch { /* ignore */ }

                if (_alleVask == null) return;

                int year = _dtpMnd.Value.Year;
                int month = _dtpMnd.Value.Month;

                var query = _alleVask
                    .Where(v => v.DatoTid != DateTime.MinValue &&
                                v.DatoTid.Year == year &&
                                v.DatoTid.Month == month);

                var unntaksReg = new HashSet<string>(
        (_register ?? new Dictionary<string, KjoretoyInfo>()).Values.Where(k => k.Unntak)
            .Select(k => ExcelRepository.NormalizeRegnr(k.Registreringsnummer)),
        StringComparer.OrdinalIgnoreCase);


                if (_chkBareUnntak.Checked)
                {
                    query = query.Where(v =>
                        unntaksReg.Contains(ExcelRepository.NormalizeRegnr(v.Registreringsnummer)) ||
                        string.Equals(v.Status, "Unntak", StringComparison.OrdinalIgnoreCase));
                }
                else if (_chkBareAmbulanse.Checked)
                {
                    query = query.Where(v =>
                        v.Selskap == SelskapType.Ambulanse &&
                        !unntaksReg.Contains(ExcelRepository.NormalizeRegnr(v.Registreringsnummer)) &&
                        !string.Equals(v.Status, "Unntak", StringComparison.OrdinalIgnoreCase));
                }
                else if (_chkBareHospitaldrift.Checked)
                {
                    query = query.Where(v =>
                        v.Selskap == SelskapType.Hospitaldrift &&
                        !unntaksReg.Contains(ExcelRepository.NormalizeRegnr(v.Registreringsnummer)) &&
                        !string.Equals(v.Status, "Unntak", StringComparison.OrdinalIgnoreCase));
                }
                else if (_chkBarePoliti.Checked)
                {
                    query = query.Where(v =>
                        v.Selskap == SelskapType.Politi &&
                        !unntaksReg.Contains(ExcelRepository.NormalizeRegnr(v.Registreringsnummer)) &&
                        !string.Equals(v.Status, "Unntak", StringComparison.OrdinalIgnoreCase));
                }
                else if (_chkBareBrann.Checked)
                {
                    query = query.Where(v =>
                        v.Selskap == SelskapType.Røde_Kors &&
                        !unntaksReg.Contains(ExcelRepository.NormalizeRegnr(v.Registreringsnummer)) &&
                        !string.Equals(v.Status, "Unntak", StringComparison.OrdinalIgnoreCase));
                }
                
                else if (_chkBareTide.Checked)
                {
                    // Tide filter: show ALL Tide events (including "Unntak")
                    query = query.Where(v => v.Selskap == SelskapType.Tide);
                }
                else
                {
                    // domyślnie: Ambulanse/Hospitaldrift + Politi + Røde_Kors + Klargjører + Ukjent
                    // bez Buss / Tide i bez Unntak
                    query = query.Where(v =>
                        (v.Selskap == SelskapType.Ambulanse ||
                         v.Selskap == SelskapType.Hospitaldrift ||
                         v.Selskap == SelskapType.Politi ||
                         v.Selskap == SelskapType.Røde_Kors ||
                         v.Selskap == SelskapType.Ukjent) &&
                        !unntaksReg.Contains(ExcelRepository.NormalizeRegnr(v.Registreringsnummer)) &&
                        !string.Equals(v.Status, "Unntak", StringComparison.OrdinalIgnoreCase));
                }

                var filtered = query.ToList();

                // Signature to avoid rebinding when nothing changed (prevents flicker).
                try
                {
                    int c = filtered.Count;
                    DateTime m = c > 0 ? filtered.Max(v => v.DatoTid) : DateTime.MinValue;
                    if (c == _lastSigCount && m == _lastSigMax)
                        return;
                    _lastSigCount = c;
                    _lastSigMax = m;
                }
                catch { /* ignore */ }

                var view = filtered
    .OrderByDescending(v => v.DatoTid)
    .Select(v => new
    {
        v.DatoTid,
        Internnr = (_register != null &&
                   _register.TryGetValue(ExcelRepository.NormalizeRegnr(v.Registreringsnummer), out var k)
                   ? (k.Internnr ?? "")
                   : ""),
        v.Registreringsnummer,
        Selskap = v.Selskap.ToString(),
        v.TypeKjoretoy,
        v.Sesong,
        v.Status,
        v.Kostnad
    })
    .ToList();

                _grid.DataSource = view;

                try
                {
                    if (_grid.Columns["DatoTid"] != null)
                        _grid.Columns["DatoTid"].DefaultCellStyle.Format = "dd.MM.yyyy HH:mm";
                    if (_grid.Columns["Internnr"] != null)
                        _grid.Columns["Internnr"].HeaderText = "Internnr.";
                }
                catch { }
            }

            private void StartAutoRefresh()
            {
                if (_autoRefreshTimer != null) return;

                _autoRefreshTimer = new System.Windows.Forms.Timer
                {
                    Interval = 2000 // 2s: enough to see Worker inserts quickly without stressing DB
                };
                _autoRefreshTimer.Tick += (_, __) =>
                {
                    if (_autoRefreshBusy) return;
                    _autoRefreshBusy = true;
                    try
                    {
                        OppdaterListe();
                    }
                    finally
                    {
                        _autoRefreshBusy = false;
                    }
                };
                _autoRefreshTimer.Start();
            }

            private void StopAutoRefresh()
            {
                try
                {
                    _autoRefreshTimer?.Stop();
                    _autoRefreshTimer?.Dispose();
                }
                catch { }
                finally
                {
                    _autoRefreshTimer = null;
                }
            }


        }

        // ---------------------------------------------
        // STATISTIKK + rapportere (wydruk + podgląd + archiwum)
        // ---------------------------------------------
        public class StatForm : Form
        {
            private readonly StatistikkService _statService;
            private readonly ExcelRepository _repo;
            private readonly CultureInfo _culture = new CultureInfo("nb-NO");

            private List<VaskeHendelse> _alleVask;

            private DateTimePicker _dtpFra;
            private DateTimePicker _dtpTil;
            private ComboBox _cmbSelskap;
            private DataGridView _grid;
            private Label _lblSum;

            private System.Windows.Forms.Timer? _autoRefreshTimer;
            private bool _autoRefreshBusy;
            private int _lastSigCount;
            private DateTime _lastSigMax = DateTime.MinValue;

            private BindingList<VisningRad> _rows;

            private PrintDocument _printDoc;
            private bool _invoiceMode;

            private string _invoiceNumber;
            private string _invoiceCustomerNumber;
            private DateTime _invoiceFra;
            private DateTime _invoiceTil;
            private SelskapType _invoiceCompanyKey;

            private EgenFirmaInfo _egenFirma;
            private List<KundeFirmaInfo> _kundeFirmaer;
            private KundeFirmaInfo _invoiceKunde;
            private Image _firmaLogo;

            private Panel _reportOptionsPanel;
            private Button _btnReportPdf;
            private Button _btnReportExcel;
            private Button _btnReportEmail;

            private class VisningRad
            {
                public DateTime DatoTid { get; set; }
                public string Registreringsnummer { get; set; }
                public string Selskap { get; set; }
                public string TypeKjoretoy { get; set; }
                public string Sesong { get; set; }
                public string Status { get; set; }
                public decimal Kostnad { get; set; }
            }

            public StatForm(StatistikkService statService, ExcelRepository repo)
            {
                _statService = statService;
                _repo = repo;

                Text = "Statistikk og rapportere";
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(1300, 900);
                Font = new Font(FontFamily.GenericSansSerif, 11f);

                ByggUi();

                _printDoc = new PrintDocument();
                _printDoc.PrintPage += PrintDoc_PrintPage;

                _alleVask = _repo.LastAlleVask();

                try
                {
                    _lastSigCount = _alleVask?.Count ?? 0;
                    _lastSigMax = (_alleVask != null && _alleVask.Count > 0) ? _alleVask.Max(v => v.DatoTid) : DateTime.MinValue;
                }
                catch { _lastSigCount = 0; _lastSigMax = DateTime.MinValue; }

                _egenFirma = _repo.LastEgenFirma();
                _kundeFirmaer = _repo.LastAlleKundeFirma();

                try
                {
                    if (File.Exists(_repo.LogoPath))
                        _firmaLogo = Image.FromFile(_repo.LogoPath);
                }
                catch { }

                OppdaterView();

                StartAutoRefresh();
                FormClosed += (_, __) => StopAutoRefresh();
            }

            private void StartAutoRefresh()
            {
                if (_autoRefreshTimer != null) return;

                _autoRefreshTimer = new System.Windows.Forms.Timer
                {
                    Interval = 3000 // 3s: update list automatically (Admin sees Worker inserts)
                };
                _autoRefreshTimer.Tick += (_, __) =>
                {
                    if (_autoRefreshBusy) return;
                    _autoRefreshBusy = true;
                    try
                    {
                        var fresh = _repo.LastAlleVask();
                        if (fresh != null)
                        {
                            int c = fresh.Count;
                            DateTime m = DateTime.MinValue;
                            try { if (c > 0) m = fresh.Max(v => v.DatoTid); } catch { }

                            if (c != _lastSigCount || m != _lastSigMax)
                            {
                                _alleVask = fresh;
                                _lastSigCount = c;
                                _lastSigMax = m;
                                OppdaterView();
                            }
                        }
                    }
                    catch
                    {
                        // ignore transient DB errors while polling
                    }
                    finally
                    {
                        _autoRefreshBusy = false;
                    }
                };

                _autoRefreshTimer.Start();
            }

            private void StopAutoRefresh()
            {
                try
                {
                    _autoRefreshTimer?.Stop();
                    _autoRefreshTimer?.Dispose();
                }
                catch { }
                finally
                {
                    _autoRefreshTimer = null;
                }
            }


            private void ByggUi()
            {
                var lblFra = new Label
                {
                    Text = "Fra:",
                    AutoSize = true,
                    Location = new Point(20, 20)
                };

                _dtpFra = new DateTimePicker
                {
                    Location = new Point(60, 20),
                    Width = 120,
                    Format = DateTimePickerFormat.Short,
                    Value = DateTime.Today.AddDays(-30)
                };
                _dtpFra.ValueChanged += (s, e) => OppdaterView();

                var lblTil = new Label
                {
                    Text = "Til:",
                    AutoSize = true,
                    Location = new Point(200, 20)
                };

                _dtpTil = new DateTimePicker
                {
                    Location = new Point(240, 20),
                    Width = 120,
                    Format = DateTimePickerFormat.Short,
                    Value = DateTime.Today
                };
                _dtpTil.ValueChanged += (s, e) => OppdaterView();

                var lblSelskap = new Label
                {
                    Text = "Selskap:",
                    AutoSize = true,
                    Location = new Point(380, 20)
                };

                _cmbSelskap = new ComboBox
                {
                    Location = new Point(450, 20),
                    Width = 180,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };
                _cmbSelskap.Items.Add("Alle");
                foreach (SelskapType s in Enum.GetValues(typeof(SelskapType)))
                {
                    if (s == SelskapType.Ukjent) continue;
                    _cmbSelskap.Items.Add(s);
                }
                _cmbSelskap.SelectedIndex = 0;
                _cmbSelskap.SelectedIndexChanged += (s, e) => OppdaterView();

                var btnOppdater = new Button
                {
                    Text = "Oppdater",
                    Location = new Point(650, 15),
                    Width = 100,
                    Height = 30
                };
                btnOppdater.Click += (s, e) => OppdaterView();

                var btnFaktura = new Button
                {
                    Text = "Rapportere for selskap",
                    Location = new Point(760, 15),
                    Width = 220,
                    Height = 30
                };
                btnFaktura.Click += BtnFaktura_Click;

                _reportOptionsPanel = new Panel
                {
                    Location = new Point(760, 50),
                    Size = new Size(380, 36),
                    Visible = false
                };

                _btnReportPdf = new Button
                {
                    Text = "PDF",
                    Width = 110,
                    Height = 30,
                    Location = new Point(0, 0)
                };
                _btnReportPdf.Click += BtnReportPdf_Click;

                _btnReportExcel = new Button
                {
                    Text = "EXCEL",
                    Width = 110,
                    Height = 30,
                    Location = new Point(125, 0)
                };
                _btnReportExcel.Click += BtnReportExcel_Click;

                _btnReportEmail = new Button
                {
                    Text = "E-MAIL",
                    Width = 110,
                    Height = 30,
                    Location = new Point(250, 0)
                };
                _btnReportEmail.Click += BtnReportEmail_Click;

                _reportOptionsPanel.Controls.Add(_btnReportPdf);
                _reportOptionsPanel.Controls.Add(_btnReportExcel);
                _reportOptionsPanel.Controls.Add(_btnReportEmail);

                _lblSum = new Label
                {
                    Text = "",
                    AutoSize = false,
                    Location = new Point(20, 90),
                    Width = 1100,
                    Height = 24
                };

                _grid = new DataGridView
                {
                    Location = new Point(20, 120),
                    Size = new Size(1260, 760),
                    ReadOnly = true,
                    AllowUserToAddRows = false,
                    AllowUserToDeleteRows = false,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
                };

                Controls.Add(lblFra);
                Controls.Add(_dtpFra);
                Controls.Add(lblTil);
                Controls.Add(_dtpTil);
                Controls.Add(lblSelskap);
                Controls.Add(_cmbSelskap);
                Controls.Add(btnOppdater);
                Controls.Add(btnFaktura);
                Controls.Add(_reportOptionsPanel);
                Controls.Add(_lblSum);
                Controls.Add(_grid);
            }

            private string FormatSelskap(SelskapType s)
            {
                switch (s)
                {
                    case SelskapType.Ambulanse:
                        return "Ambulanser";
                    case SelskapType.Hospitaldrift:
                        return "Hospitaldrifter";
                    case SelskapType.Politi:
                        return "Politi";
                    case SelskapType.Røde_Kors:
                        return "Røde Kors";
                    case SelskapType.Tide:
                        return "Tide";
                    default:
                        return "Ukjent";
                }
            }


            private void OppdaterView()
            {
                if (_alleVask == null) return;

                DateTime fra = _dtpFra.Value.Date;
                DateTime til = _dtpTil.Value.Date.AddDays(1).AddTicks(-1);

                IEnumerable<VaskeHendelse> query = _alleVask.Where(v => v.DatoTid >= fra && v.DatoTid <= til);

                object selItem = _cmbSelskap.SelectedItem;
                var selectedTypes = new List<SelskapType>();

                if (selItem is SelskapType selType)
                {
                 selectedTypes.Add(selType);
        
                }

                if (selectedTypes.Count > 0)
                {
                    query = query.Where(v => selectedTypes.Contains(v.Selskap));
                }

                var list = query
                    .OrderByDescending(v => v.DatoTid)
                    .Select(v => new VisningRad
                    {
                        DatoTid = v.DatoTid,
                        Registreringsnummer = v.Registreringsnummer,
                        Selskap = FormatSelskap(v.Selskap),
                        TypeKjoretoy = v.TypeKjoretoy,
                        Sesong = v.Sesong,
                        Status = v.Status,
                        Kostnad = v.Kostnad
                    })
                    .ToList();

                _rows = new BindingList<VisningRad>(list);
                _grid.DataSource = _rows;

                int count = _rows.Count;
                decimal sum = _rows.Sum(r => r.Kostnad);

                string selskapTekst;
                if (selectedTypes.Count == 0)
                {
                    selskapTekst = "alle selskap";
                }
                else if (selectedTypes.Count == 2 &&
                         selectedTypes.Contains(SelskapType.Ambulanse) &&
                         selectedTypes.Contains(SelskapType.Hospitaldrift))
                {
                    selskapTekst = "Ambulanse / Hospitaldrift (Røde Kors)";
                }
                else
                {
                    var navnListe = selectedTypes.Select(FormatSelskap).ToList();
                    selskapTekst = string.Join(" / ", navnListe.ToArray());
                }

                _lblSum.Text =
                    $"Periode: {fra:yyyy-MM-dd} – {til:yyyy-MM-dd}, Selskap: {selskapTekst}, Antall vask: {count}, Sum: {sum:0.00} kr";
            }

            private void BtnFaktura_Click(object sender, EventArgs e)
            {
                if (_reportOptionsPanel != null)
                    _reportOptionsPanel.Visible = !_reportOptionsPanel.Visible;
            }

            private bool PrepareSelectedCompanyForReport(out SelskapType selType)
            {
                selType = SelskapType.Ukjent;

                if (!(_cmbSelskap.SelectedItem is SelskapType selected))
                {
                    MessageBox.Show(this,
                        "Velg et konkret selskap (ikke 'Alle') for å lage rapport.",
                        "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }

                if (_rows == null || _rows.Count == 0)
                {
                    MessageBox.Show(this,
                        "Ingen data i valgt periode for dette selskapet.",
                        "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }

                selType = selected;
                _invoiceCompanyKey = selType;
                _egenFirma = _repo.LastEgenFirma();
                _kundeFirmaer = _repo.LastAlleKundeFirma();
                _invoiceKunde = _kundeFirmaer.Find(k => k.Selskap == _invoiceCompanyKey);

                try
                {
                    if (File.Exists(_repo.LogoPath))
                    {
                        if (_firmaLogo != null) _firmaLogo.Dispose();
                        _firmaLogo = Image.FromFile(_repo.LogoPath);
                    }
                }
                catch { }

                _invoiceMode = true;
                _invoiceFra = _dtpFra.Value.Date;
                _invoiceTil = _dtpTil.Value.Date;
                _invoiceNumber = DateTime.Now.ToString("yyyyMMdd-HHmm");
                _invoiceCustomerNumber = _invoiceKunde?.OrgNr ?? "";

                return true;
            }

            private void BtnReportPdf_Click(object sender, EventArgs e)
            {
                if (!PrepareSelectedCompanyForReport(out _))
                    return;

                using (var preview = new PrintPreviewDialog())
                {
                    preview.Document = _printDoc;
                    preview.Width = 1000;
                    preview.Height = 800;
                    preview.StartPosition = FormStartPosition.CenterParent;
                    preview.ShowIcon = false;

                    try
                    {
                        preview.ShowDialog(this);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this,
                            "Feil ved visning av forhåndsvisning: " + ex.Message,
                            "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                int count = _rows.Count;
                decimal netto = _rows.Sum(r => r.Kostnad);
                decimal mva = Math.Round(netto * 0.25m, 2);
                decimal total = netto + mva;

                var post = new FakturaArkivPost
                {
                    FakturaNr = _invoiceNumber,
                    Dato = DateTime.Today,
                    Selskap = _invoiceCompanyKey,
                    KundeNavn = _invoiceKunde?.Navn ?? FormatSelskap(_invoiceCompanyKey),
                    PeriodeFra = _invoiceFra,
                    PeriodeTil = _invoiceTil,
                    AntallVask = count,
                    Netto = netto,
                    Mva = mva,
                    Total = total,
                    PdfFil = "",
                    Annullert = false
                };

                try
                {
                    _repo.LeggTilFakturaArkiv(post);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        "Feil ved lagring til fakturaarkiv: " + ex.Message,
                        "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            private void BtnReportExcel_Click(object sender, EventArgs e)
            {
                if (!PrepareSelectedCompanyForReport(out var selType))
                    return;

                try
                {
                    string path = BuildReportXlsx(selType);
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        "Feil ved oppretting av Excel-rapport: " + ex.Message,
                        "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            private void BtnReportEmail_Click(object sender, EventArgs e)
            {
                if (!PrepareSelectedCompanyForReport(out var selType))
                    return;

                try
                {
                    string path = BuildReportXlsx(selType);
                    string subject = $"Bilvask rapport - {FormatSelskap(selType)} - {_invoiceFra:yyyy-MM-dd} til {_invoiceTil:yyyy-MM-dd}";
                    string body = $"Hei,%0D%0A%0D%0AVedlagt er rapport for {FormatSelskap(selType)} for perioden {_invoiceFra:yyyy-MM-dd} - {_invoiceTil:yyyy-MM-dd}.%0D%0A%0D%0AMvh";

                    try
                    {
                        var outlookType = Type.GetTypeFromProgID("Outlook.Application");
                        if (outlookType != null)
                        {
                            dynamic outlook = Activator.CreateInstance(outlookType);
                            dynamic mail = outlook.CreateItem(0);
                            mail.Subject = subject;
                            mail.Body = $"Hei,\r\n\r\nVedlagt er rapport for {FormatSelskap(selType)} for perioden {_invoiceFra:yyyy-MM-dd} - {_invoiceTil:yyyy-MM-dd}.\r\n\r\nMvh";
                            mail.Attachments.Add(path);
                            mail.Display();
                            return;
                        }
                    }
                    catch
                    {
                        // fallback below
                    }

                    Process.Start(new ProcessStartInfo($"mailto:?subject={Uri.EscapeDataString(subject)}&body={body}") { UseShellExecute = true });
                    Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        "Feil ved oppretting av e-postrapport: " + ex.Message,
                        "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            private string BuildReportXlsx(SelskapType selType)
            {
                string reportsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BilvaskRegistrering", "Rapporter");
                Directory.CreateDirectory(reportsDir);

                string safeCompany = FormatSelskap(selType).Replace("/", "-").Replace(" ", "_");
                string filePath = Path.Combine(reportsDir, $"BilvaskRapport_{safeCompany}_{_invoiceFra:yyyyMMdd}_{_invoiceTil:yyyyMMdd}.xlsx");

                WriteSimpleXlsx(filePath, _rows?.ToList() ?? new List<VisningRad>(), selType);
                return filePath;
            }

            private void WriteSimpleXlsx(string filePath, List<VisningRad> rows, SelskapType selType)
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);

                using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
                {
                    void WriteEntry(string name, string content)
                    {
                        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                        writer.Write(content);
                    }

                    static string Esc(string s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";

                    var rowXml = new StringBuilder();
                    int r = 1;

                    void AddRow(params string[] values)
                    {
                        rowXml.Append($"<row r=\"{r}\">");
                        int col = 0;
                        foreach (var v in values)
                        {
                            col++;
                            string cellRef = $"{(char)('A' + col - 1)}{r}";
                            rowXml.Append($"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t>{Esc(v)}</t></is></c>");
                        }
                        rowXml.Append("</row>");
                        r++;
                    }

                    AddRow("DatoTid", "Registreringsnummer", "Selskap", "TypeKjoretoy", "Sesong", "Status", "Kostnad");

                    foreach (var row in rows.OrderByDescending(x => x.DatoTid))
                    {
                        AddRow(
                            row.DatoTid.ToString("dd.MM.yyyy HH:mm"),
                            row.Registreringsnummer ?? "",
                            row.Selskap ?? FormatSelskap(selType),
                            row.TypeKjoretoy ?? "",
                            row.Sesong ?? "",
                            row.Status ?? "",
                            row.Kostnad.ToString("0.00", _culture)
                        );
                    }

                    string sheetXml = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                        "<sheetData>" + rowXml + "</sheetData></worksheet>";

                    WriteEntry("[Content_Types].xml",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                        "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
                        "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>" +
                        "</Types>");

                    WriteEntry("_rels/.rels",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
                        "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>" +
                        "</Relationships>");

                    WriteEntry("xl/workbook.xml",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                        "<sheets><sheet name=\"Rapport\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");

                    WriteEntry("xl/_rels/workbook.xml.rels",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                        "</Relationships>");

                    WriteEntry("xl/styles.xml",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                        "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                        "<fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>" +
                        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                        "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
                        "</styleSheet>");

                    WriteEntry("xl/worksheets/sheet1.xml", sheetXml);

                    WriteEntry("docProps/core.xml",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                        "<dc:title>Bilvask rapport</dc:title><dc:creator>BilvaskRegistrering</dc:creator></cp:coreProperties>");

                    WriteEntry("docProps/app.xml",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
                        "<Application>BilvaskRegistrering</Application></Properties>");
                }
            }

            private void PrintDoc_PrintPage(object sender, PrintPageEventArgs e)
            {
                if (!_invoiceMode)
                {
                    e.HasMorePages = false;
                    return;
                }

                Graphics g = e.Graphics;
                Font fontTitle = new Font("Arial", 26, FontStyle.Bold);
                Font fontHeader = new Font("Arial", 12, FontStyle.Bold);
                Font font = new Font("Arial", 10);
                Font fontSmall = new Font("Arial", 9);

                int globalOffsetX = -70;

                int left = e.MarginBounds.Left + globalOffsetX;
                int top = e.MarginBounds.Top;
                int right = e.MarginBounds.Right + globalOffsetX;

                // ===== 1. GÓRNY NAGŁÓWEK – 3 POLA =====
                int headerBoxWidth = 300;
                int headerBoxHeight = fontSmall.Height * 2 + 10;
                Rectangle headerRect = new Rectangle(left, top, headerBoxWidth, headerBoxHeight);
                g.DrawRectangle(Pens.Black, headerRect);

                int cellWidth = headerBoxWidth / 3;
                int hTop = top;
                int hBottom = top + headerBoxHeight;

                g.DrawLine(Pens.Black, left + cellWidth, hTop, left + cellWidth, hBottom);
                g.DrawLine(Pens.Black, left + 2 * cellWidth, hTop, left + 2 * cellWidth, hBottom);

                int hY1 = top + 2;
                g.DrawString("Fakturanr", fontSmall, Brushes.Black, left + 4, hY1);
                g.DrawString("Kundenr", fontSmall, Brushes.Black, left + cellWidth + 4, hY1);
                g.DrawString("Dato", fontSmall, Brushes.Black, left + 2 * cellWidth + 4, hY1);

                int hY2 = top + fontSmall.Height + 2;
                g.DrawString(_invoiceNumber ?? "", fontSmall, Brushes.Black, left + 4, hY2);
                g.DrawString(_invoiceCustomerNumber ?? "", fontSmall, Brushes.Black, left + cellWidth + 4, hY2);
                g.DrawString(DateTime.Today.ToString("dd.MM.yyyy"), fontSmall, Brushes.Black, left + 2 * cellWidth + 4, hY2);

                // ===== 2. NASZA FIRMA + LOGO =====
                int xRight = right - 40;
                int yRight = top;

                if (_firmaLogo != null)
                {
                    g.DrawImage(_firmaLogo, xRight, yRight, 80, 80);
                    yRight += 85;
                }

                if (!string.IsNullOrWhiteSpace(_egenFirma.Navn))
                {
                    g.DrawString(_egenFirma.Navn, fontHeader, Brushes.Black, xRight, yRight);
                    yRight += fontHeader.Height + 2;
                }
                if (!string.IsNullOrWhiteSpace(_egenFirma.Avdeling))
                {
                    g.DrawString(_egenFirma.Avdeling, font, Brushes.Black, xRight, yRight);
                    yRight += font.Height + 2;
                }
                if (!string.IsNullOrWhiteSpace(_egenFirma.Adresse1))
                {
                    g.DrawString(_egenFirma.Adresse1, font, Brushes.Black, xRight, yRight);
                    yRight += font.Height + 2;
                }
                if (!string.IsNullOrWhiteSpace(_egenFirma.Adresse2))
                {
                    g.DrawString(_egenFirma.Adresse2, font, Brushes.Black, xRight, yRight);
                    yRight += font.Height + 2;
                }
                if (!string.IsNullOrWhiteSpace(_egenFirma.OrgNr))
                {
                    g.DrawString("Org nr: " + _egenFirma.OrgNr, font, Brushes.Black, xRight, yRight);
                    yRight += font.Height + 2;
                }
                if (!string.IsNullOrWhiteSpace(_egenFirma.BedrNr))
                {
                    g.DrawString("Bedr nr: " + _egenFirma.BedrNr, font, Brushes.Black, xRight, yRight);
                    yRight += font.Height + 2;
                }

                DateTime forfall = DateTime.Today.AddDays(14);
                g.DrawString("Forfall: " + forfall.ToString("dd.MM.yyyy"),
                    font, Brushes.Black, xRight, yRight);
                yRight += font.Height + 2;

                // ===== 3. TYTUŁ WYŚRODKOWANY =====
                string title = "Vaskefaktura";
                SizeF titleSize = g.MeasureString(title, fontTitle);
                int titleX = e.MarginBounds.Left + (e.MarginBounds.Width - (int)titleSize.Width) / 2;
                int titleY = top + headerBoxHeight + 30;
                g.DrawString(title, fontTitle, Brushes.Black, titleX, titleY);

                // ===== 4. FAKTURA-ADRESSE =====
                int addrTopLabel = titleY + fontTitle.Height + 20;
                g.DrawString("Fakturaadresse:", fontHeader, Brushes.Black, left, addrTopLabel);

                int addrBoxLeft = left;
                int addrBoxTop = addrTopLabel + fontHeader.Height + 2;
                int addrBoxWidth = 230;

                int y = addrBoxTop + 4;
                int xAddr = addrBoxLeft + 10;

                if (_invoiceKunde != null)
                {
                    void DrawAddrLine(string s)
                    {
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            g.DrawString(s, font, Brushes.Black, xAddr, y);
                            y += font.Height + 2;
                        }
                    }

                    DrawAddrLine(_invoiceKunde.Navn);
                    DrawAddrLine(_invoiceKunde.Adresse1);
                    DrawAddrLine(_invoiceKunde.Adresse2);
                    DrawAddrLine(_invoiceKunde.PostnrBy);

                    if (!string.IsNullOrWhiteSpace(_invoiceKunde.Telefon))
                        DrawAddrLine("Tel: " + _invoiceKunde.Telefon);
                    if (!string.IsNullOrWhiteSpace(_invoiceKunde.Epost))
                        DrawAddrLine("E-post: " + _invoiceKunde.Epost);
                    if (!string.IsNullOrWhiteSpace(_invoiceKunde.OrgNr))
                        DrawAddrLine("Org nr: " + _invoiceKunde.OrgNr);
                    if (!string.IsNullOrWhiteSpace(_invoiceKunde.FakturaMerke))
                        DrawAddrLine("Faktura merkes: " + _invoiceKunde.FakturaMerke);
                }

                int addrBoxBottom = y + 4;
                g.DrawRectangle(Pens.Black,
                    new Rectangle(addrBoxLeft, addrBoxTop, addrBoxWidth, addrBoxBottom - addrBoxTop));

                // Periode
                y = addrBoxBottom + 8;
                g.DrawString(
                    $"Periode: {_invoiceFra:yyyy-MM-dd} – {_invoiceTil:yyyy-MM-dd}",
                    font, Brushes.Black, left, y);
                y += font.Height + 10;

                // ===== 5. TABELA POZYCJI =====
                int tableLeft = left;
                int[] colWidths = { 120, 80, 300, 40, 60, 80, 80 };

                int[] colX = new int[colWidths.Length + 1];
                colX[0] = tableLeft;
                for (int i = 1; i < colX.Length; i++)
                    colX[i] = colX[i - 1] + colWidths[i - 1];

                int tableRight = colX[colX.Length - 1];
                int tableTop = y;
                int rowHeight = fontSmall.Height + 6;
                int curY = tableTop;

                g.DrawLine(Pens.Black, tableLeft, curY, tableRight, curY);
                curY += 3;

                g.DrawString("Dato", fontSmall, Brushes.Black, colX[0] + 4, curY);
                g.DrawString("Reg.nr", fontSmall, Brushes.Black, colX[1] + 4, curY);
                g.DrawString("Tekst", fontSmall, Brushes.Black, colX[2] + 4, curY);
                g.DrawString("Enh", fontSmall, Brushes.Black, colX[3] + 4, curY);
                g.DrawString("Antall", fontSmall, Brushes.Black, colX[4] + 4, curY);
                g.DrawString("Pris", fontSmall, Brushes.Black, colX[5] + 4, curY);
                g.DrawString("Beløp", fontSmall, Brushes.Black, colX[6] + 4, curY);

                curY = tableTop + rowHeight;
                g.DrawLine(Pens.Black, tableLeft, curY, tableRight, curY);

                decimal netto = 0m;
                int count = 0;

                foreach (var r in _rows)
                {
                    if (curY + rowHeight > e.MarginBounds.Bottom - 100)
                        break;

                    curY += 2;

                    string datoStr = r.DatoTid.ToString("dd.MM.yyyy-HHmm", _culture);
                    string tekst = $"{r.TypeKjoretoy} ({r.Sesong})";
                    string prisStr = r.Kostnad.ToString("0.00", _culture);
                    string belopStr = prisStr;

                    g.DrawString(datoStr, fontSmall, Brushes.Black, colX[0] + 4, curY);
                    g.DrawString(r.Registreringsnummer, fontSmall, Brushes.Black, colX[1] + 4, curY);
                    g.DrawString(tekst, fontSmall, Brushes.Black, colX[2] + 4, curY);
                    g.DrawString("stk", fontSmall, Brushes.Black, colX[3] + 4, curY);
                    g.DrawString("1,00", fontSmall, Brushes.Black, colX[4] + 4, curY);

                    float prisWidth = g.MeasureString(prisStr, fontSmall).Width;
                    float belopWidth = g.MeasureString(belopStr, fontSmall).Width;
                    g.DrawString(prisStr, fontSmall, Brushes.Black, colX[6] - prisWidth - 4, curY);
                    g.DrawString(belopStr, fontSmall, Brushes.Black, colX[7] - belopWidth - 4, curY);

                    netto += r.Kostnad;
                    count++;

                    curY += rowHeight - 2;
                    g.DrawLine(Pens.Black, tableLeft, curY, tableRight, curY);
                }

                int tableBottom = curY;

                for (int i = 0; i < colX.Length; i++)
                {
                    g.DrawLine(Pens.Black, colX[i], tableTop, colX[i], tableBottom);
                }

                // ===== 6. PODSUMOWANIE =====
                decimal mva = Math.Round(netto * 0.25m, 2);
                decimal total = netto + mva;

                int summaryWidth = 240;
                int summaryLeft = tableRight - summaryWidth;
                int summaryTop = tableBottom + 15;
                int summaryHeight = 90;

                Rectangle summaryRect = new Rectangle(summaryLeft, summaryTop, summaryWidth, summaryHeight);
                g.DrawRectangle(Pens.Black, summaryRect);

                int sy = summaryTop + 8;
                int sxLabel = summaryLeft + 10;
                int sxVal = summaryLeft + summaryWidth - 90;

                g.DrawString("Antall vask:", font, Brushes.Black, sxLabel, sy);
                g.DrawString(count.ToString(), font, Brushes.Black, sxVal, sy);
                sy += font.Height + 2;

                g.DrawString("Netto:", font, Brushes.Black, sxLabel, sy);
                g.DrawString(netto.ToString("0.00", _culture) + " kr",
                    font, Brushes.Black, sxVal, sy);
                sy += font.Height + 2;

                g.DrawString("MVA 25%:", font, Brushes.Black, sxLabel, sy);
                g.DrawString(mva.ToString("0.00", _culture) + " kr",
                    font, Brushes.Black, sxVal, sy);
                sy += font.Height + 2;

                g.DrawString("Total:", fontHeader, Brushes.Black, sxLabel, sy);
                g.DrawString(total.ToString("0.00", _culture) + " kr",
                    fontHeader, Brushes.Black, sxVal, sy);

                _invoiceMode = false;
                e.HasMorePages = false;
            }
        }

        // ---------------------------------------------
        // KAMERA – PODGLĄD + „LES SKILT AUTOMATISK”
        // ---------------------------------------------
        public class KameraForm : Form
        {
            private readonly string _rtspUrl;
            private OpenCvSharp.VideoCapture _capture;
            private System.Windows.Forms.Timer _timer;

            // podgląd z kamery u góry
            private PictureBox _picture;

            // mała tablica z tyłu pola tekstowego
            private PictureBox _plateBox;

            // tekst rejestracji na tablicy
            private TextBox _txtRegnr;

            // przyciski
            private Button _btnOk;
            private Button _btnCancel;
            private Button _btnAuto;

            private Bitmap _lastFrame;

            public string Registreringsnummer { get; private set; } = "";

            public KameraForm(string rtspUrl)
            {
                _rtspUrl = rtspUrl;
                Text = "Kamera – les skilt";
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(1300, 900);
                Font = new Font(FontFamily.GenericSansSerif, 11f);

                ByggUi();

                Load += KameraForm_Load;
                FormClosing += KameraForm_FormClosing;
            }




            private void ByggUi()
            {
                // duży obszar na podgląd z kamery
                _picture = new PictureBox
                {
                    Location = new Point(20, 20),
                    Size = new Size(1250, 740),
                    BorderStyle = BorderStyle.FixedSingle,
                    SizeMode = PictureBoxSizeMode.Zoom
                };

                // opis nad „małą tablicą”
                var lblInfo = new Label
                {
                    Text = "Oppgi registreringsnummer (eller bruk automatisk lesing):",
                    AutoSize = true,
                    Location = new Point(20, 780)
                };

                // MAŁA TABLICA ZA POLEM TEKSTOWYM
                _plateBox = new PictureBox
                {
                    Location = new Point(20, 805),     // miejsce jak na screenie
                    Size = new Size(320, 80),
                    BorderStyle = BorderStyle.None,
                    SizeMode = PictureBoxSizeMode.StretchImage
                };
                try
                {
                string platePath = Path.Combine(AppConfig.DocFolder, "SkiltN.png");
                    if (File.Exists(platePath))
                    {
                        _plateBox.Image = Image.FromFile(platePath);
                    }
                }
                catch
                {
                    // jak się nie uda wczytać, po prostu zostanie puste tło
                }

                // TEKST REJESTRACJI NA TABLICY
                _txtRegnr = new TextBox
                {
                    Location = new Point(_plateBox.Left + 45, _plateBox.Top + 2),
                    Width = _plateBox.Width - 50,
                    Font = new Font("Consolas", 48f, FontStyle.Bold),
                    BorderStyle = BorderStyle.None,
                    BackColor = Color.White,
                    ForeColor = Color.Black

                };


                // przyciski obok tablicy
                _btnOk = new Button
                {
                    Text = "Bruk",
                    Location = new Point(_plateBox.Right + 20, _plateBox.Top + 15),
                    Width = 100,
                    Height = 40
                };
                _btnOk.Click += (s, e) =>
                {
                    Registreringsnummer = _txtRegnr.Text.Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(Registreringsnummer))
                    {
                        MessageBox.Show(this, "Oppgi registreringsnummer.", "Info",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    DialogResult = DialogResult.OK;
                    Close();
                };

                _btnCancel = new Button
                {
                    Text = "Avbryt",
                    Location = new Point(_btnOk.Right + 10, _plateBox.Top + 15),
                    Width = 100,
                    Height = 40
                };
                _btnCancel.Click += (s, e) =>
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                };

                _btnAuto = new Button
                {
                    Text = "Les skilt automatisk",
                    Location = new Point(_btnCancel.Right + 10, _plateBox.Top + 15),
                    Width = 200,
                    Height = 40
                };
                _btnAuto.Click += BtnAuto_Click;

                // dodajemy wszystkie kontrolki na formularz
                Controls.Add(_picture);
                Controls.Add(lblInfo);
                Controls.Add(_plateBox);
                Controls.Add(_txtRegnr);
                _txtRegnr.BringToFront();
                Controls.Add(_btnOk);
                Controls.Add(_btnCancel);
                Controls.Add(_btnAuto);
            }

            private void KameraForm_Load(object sender, EventArgs e)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(_rtspUrl))
                    {
                        _capture = new OpenCvSharp.VideoCapture(_rtspUrl);
                        if (!_capture.IsOpened())
                        {
                            MessageBox.Show(this, "Kunne ikke åpne kamerastream. Kontroller RTSP-adressen.",
                                "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        _timer = new System.Windows.Forms.Timer();
                        _timer.Interval = 100;
                        _timer.Tick += Timer_Tick;
                        _timer.Start();
                    }
                    else
                    {
                        MessageBox.Show(this,
                            "Kamera-URL er ikke satt. Sett AppConfig.CameraRtspUrl i koden.",
                            "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Feil ved oppstart av kamera:\n\n" + ex.ToString(),
                        "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            private void Timer_Tick(object sender, EventArgs e)
            {
                if (_capture == null) return;

                var frame = new OpenCvSharp.Mat();
                if (!_capture.Read(frame) || frame.Empty())
                {
                    frame.Dispose();
                    return;
                }

                using (frame)
                {
                    using (Bitmap bmp = BitmapConverter.ToBitmap(frame))
                    {
                        var old = _picture.Image;
                        _picture.Image = (Bitmap)bmp.Clone();

                        if (_lastFrame != null) _lastFrame.Dispose();
                        _lastFrame = (Bitmap)bmp.Clone();

                        if (old != null) old.Dispose();
                    }
                }
            }

            private async void BtnAuto_Click(object sender, EventArgs e)
            {
                if (_lastFrame == null)
                {
                    MessageBox.Show(this, "Ingen bilde fra kamera.", "Info",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try
                {
                    byte[] imageBytes;
                    using (var ms = new MemoryStream())
                    {
                        _lastFrame.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                        imageBytes = ms.ToArray();
                    }

                    string plate = await AnprClient.RecognizePlateAsync(imageBytes);
                    if (!string.IsNullOrEmpty(plate))
                    {
                        var plateNorm = ExcelRepository.NormalizeRegnr(plate).ToUpperInvariant();
                        if (!ExcelRepository.IsStrictNorwegianPlate(plateNorm))
                        {
                            MessageBox.Show(this,
                                $"Ignorert ugyldig/ufullstendig skilt: {plateNorm}",
                                "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }

                        _txtRegnr.Text = FormatPlateForDisplay(plateNorm);
                    }
                    else
                    {
                        MessageBox.Show(this,
                            "Fant ingen skilt i svaret fra ANPR-tjenesten.",
                            "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Feil ved automatisk lesing:\n\n" + ex.ToString(),
                        "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            private void KameraForm_FormClosing(object sender, FormClosingEventArgs e)
            {
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Dispose();
                    _timer = null;
                }

                if (_capture != null)
                {
                    _capture.Release();
                    _capture.Dispose();
                    _capture = null;
                }

                if (_picture.Image != null)
                {
                    _picture.Image.Dispose();
                    _picture.Image = null;
                }

                if (_lastFrame != null)
                {
                    _lastFrame.Dispose();
                    _lastFrame = null;
                }
            }
        }


        // ---------------------------------------------
        // FIRMAOPPLYSNINGER – EGET FIRMA + KUNDER + INNSTILLINGER
        // ---------------------------------------------
        public class FirmaForm : Form
        {
            private readonly ExcelRepository _repo;

            private TabControl _tabs;

            // Eget firma
            private TextBox _txtNavn;
            private TextBox _txtAvdeling;
            private TextBox _txtOrgNr;
            private TextBox _txtBedrNr;
            private TextBox _txtAdr1;
            private TextBox _txtAdr2;
            private TextBox _txtTel;
            private TextBox _txtEpost;
            private PictureBox _logoBox;

            // Kunder
            private DataGridView _gridKunder;
            private BindingList<KundeFirmaInfo> _kundeListe;

            // Innstillinger – kamera + sesongpriser
            private TextBox _txtCamRtsp;
            private TextBox _txtCamToken;

            // Preview cameras (Kamera 2 / Kamera 3)
            private CheckBox _chkCam2Enabled;
            private CheckBox _chkCam2AutoRefresh;
            private ComboBox _cmbCam2Protocol;
            private TextBox _txtCam2Host;
            private NumericUpDown _numCam2Port;
            private TextBox _txtCam2User;
            private TextBox _txtCam2Pass;
            private NumericUpDown _numCam2Channel;
            private TextBox _txtCam2Path;
            private TextBox _txtCam2RtspOverride;

            private CheckBox _chkCam3Enabled;
            private ComboBox _cmbCam3Protocol;
            private TextBox _txtCam3Host;
            private NumericUpDown _numCam3Port;
            private TextBox _txtCam3User;
            private TextBox _txtCam3Pass;
            private NumericUpDown _numCam3Channel;
            private TextBox _txtCam3Path;
            private TextBox _txtCam3RtspOverride;
            private TextBox _txtDahuaHost;
            private NumericUpDown _numDahuaPort;
            private TextBox _txtDahuaUser;
            private TextBox _txtDahuaPass;

            private TextBox _txtItsApiHostIp;
            private NumericUpDown _numItsApiPort;
            private TextBox _txtItsApiPath;

            private TextBox _txtDocFolder;
	        private Button _btnBrowseDocFolder;
            private CheckBox _chkAutoRegister;
            private NumericUpDown _numStorSommer;
            private NumericUpDown _numStorVinter;
            private NumericUpDown _numLitenSommer;
            private NumericUpDown _numLitenVinter;

            // Sesong datoer (startdatoer)
            private DateTimePicker _dtSommerStart;
            private DateTimePicker _dtVinterStart;
            private DateTimePicker _dtRecalcSeasonFrom;
            private Button _btnRecalcSeason;

            
            // Utseende/Layout
            private NumericUpDown? _numLayoutLeftWidth;
            private NumericUpDown? _numLayoutLeftMin;
            private NumericUpDown? _numLayoutLeftMax;
            private NumericUpDown? _numLayoutRightMin;
            private NumericUpDown? _numLayoutHeaderH;
            private NumericUpDown? _numLayoutPlateH;
            private NumericUpDown? _numLayoutBottomH;
            private NumericUpDown? _numLayoutCamMainPct;
            private NumericUpDown? _numLayoutInternFont;
            private NumericUpDown? _numLayoutInfoFont;
            private NumericUpDown? _numLayoutHeaderFont;
            private ComboBox? _cmbCam1Mode;
            private ComboBox? _cmbCam2Mode;
            private ComboBox? _cmbCam3Mode;
            private CheckBox? _chkLayoutLive;

// Innstillinger – database (Azure Postgres) + worker UI options
            private CheckBox _chkDbEnabled;
            private TextBox _txtDbHost;
            private TextBox _txtDbWorkerHost;
            private NumericUpDown _numDbPort;
            private TextBox _txtDbName;
            private TextBox _txtDbAdminUser;
            private TextBox _txtDbAdminPass;
            private TextBox _txtDbWorkerUser;
            private TextBox _txtDbWorkerPass;
            private NumericUpDown _numWorkerRefresh;
            private CheckBox _chkWorkerOnlyUnconfirmed;

            // UI passord (Admin/Worker)
            private TextBox _txtUiAdminPassword;
            private TextBox _txtUiWorkerPassword;

            
            private NumericUpDown _numDisplaySeconds;
            public FirmaForm(ExcelRepository repo)
            {
                _repo = repo;
                Text = "Innstillinger";
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(1080, 800);
                Font = new Font(FontFamily.GenericSansSerif, 11f);

                ByggUi();
                LastEgetFirma();
                LastKunder();
                LastInnstillinger();
                LastDatabase();
                Lastsesongpriser();
            }
            public FirmaForm(ExcelRepository repo, int initialTabIndex)
        : this(repo)
            {
                // Jeśli indeks poprawny – ustaw aktywną kartę
                if (_tabs != null &&
                    initialTabIndex >= 0 &&
                    initialTabIndex < _tabs.TabPages.Count)
                {
                    _tabs.SelectedIndex = initialTabIndex;
                }
            }




            private void ByggUi()
            {
                _tabs = new TabControl
                {
                    Dock = DockStyle.Fill
                };

                // Bunnlinje: Lagre + Lukk (slik at du slipper å bruke X-knappen).
                var bottomBar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 58,
                    Padding = new Padding(10, 10, 10, 10),
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false
                };

                var btnLukk = new Button
                {
                    Text = "Lukk",
                    Width = 120,
                    Height = 36,
                    Margin = new Padding(10, 0, 0, 0)
                };
                btnLukk.Click += (_, __) => Close();

                var btnLagre = new Button
                {
                    Text = "Lagre",
                    Width = 120,
                    Height = 36
                };
                btnLagre.Click += BtnLagreInnstillinger_Click;

                bottomBar.Controls.Add(btnLukk);
                bottomBar.Controls.Add(btnLagre);

                AcceptButton = btnLagre;
                CancelButton = btnLukk;


                var tabEget = new TabPage("Eget firma");
                var tabKunder = new TabPage("Kunder");
                var tabInnst = new TabPage("Innstillinger ANPR Kamera");
                var tabKameraer = new TabPage("Kameraer (forhåndsvisning)");
                var tabUtseende = new TabPage("Utseende / Layout");
                var tabseDatabase = new TabPage("Database");
                var tabsesongpriser = new TabPage("Sesong Priser og dato");

                ByggEgetTab(tabEget);
                ByggKunderTab(tabKunder);
                ByggInnstillingerTab(tabInnst);
                ByggPreviewKameraTab(tabKameraer);
                ByggUtseendeTab(tabUtseende);
                ByggDatabaseTab(tabseDatabase);
                ByggsesongpriserTab(tabsesongpriser);

                _tabs.TabPages.Add(tabEget);
                _tabs.TabPages.Add(tabKunder);
                _tabs.TabPages.Add(tabInnst);
                _tabs.TabPages.Add(tabKameraer);
                _tabs.TabPages.Add(tabUtseende);
                _tabs.TabPages.Add(tabseDatabase);
                _tabs.TabPages.Add(tabsesongpriser);

                Controls.Add(bottomBar);
                Controls.Add(_tabs);
            }

            // ---------- EGET FIRMA ----------

            private void ByggEgetTab(TabPage tab)
            {
                int xLabel = 20;
                int xEdit = 180;
                int y = 20;
                int dy = 30;

                tab.Controls.Add(new Label { Text = "Navn:", Location = new Point(xLabel, y), AutoSize = true });
                _txtNavn = new TextBox { Location = new Point(xEdit, y - 3), Width = 300 };
                tab.Controls.Add(_txtNavn);
                y += dy;

                tab.Controls.Add(new Label { Text = "Avdeling:", Location = new Point(xLabel, y), AutoSize = true });
                _txtAvdeling = new TextBox { Location = new Point(xEdit, y - 3), Width = 300 };
                tab.Controls.Add(_txtAvdeling);
                y += dy;

                tab.Controls.Add(new Label { Text = "Org nr:", Location = new Point(xLabel, y), AutoSize = true });
                _txtOrgNr = new TextBox { Location = new Point(xEdit, y - 3), Width = 200 };
                tab.Controls.Add(_txtOrgNr);
                y += dy;

                tab.Controls.Add(new Label { Text = "Bedr nr:", Location = new Point(xLabel, y), AutoSize = true });
                _txtBedrNr = new TextBox { Location = new Point(xEdit, y - 3), Width = 200 };
                tab.Controls.Add(_txtBedrNr);
                y += dy;

                tab.Controls.Add(new Label { Text = "Adresse linje 1:", Location = new Point(xLabel, y), AutoSize = true });
                _txtAdr1 = new TextBox { Location = new Point(xEdit, y - 3), Width = 300 };
                tab.Controls.Add(_txtAdr1);
                y += dy;

                tab.Controls.Add(new Label { Text = "Adresse linje 2:", Location = new Point(xLabel, y), AutoSize = true });
                _txtAdr2 = new TextBox { Location = new Point(xEdit, y - 3), Width = 300 };
                tab.Controls.Add(_txtAdr2);
                y += dy;

                tab.Controls.Add(new Label { Text = "Telefon:", Location = new Point(xLabel, y), AutoSize = true });
                _txtTel = new TextBox { Location = new Point(xEdit, y - 3), Width = 200 };
                tab.Controls.Add(_txtTel);
                y += dy;

                tab.Controls.Add(new Label { Text = "E-post:", Location = new Point(xLabel, y), AutoSize = true });
                _txtEpost = new TextBox { Location = new Point(xEdit, y - 3), Width = 300 };
                tab.Controls.Add(_txtEpost);
                y += dy + 10;

                _logoBox = new PictureBox
                {
                    Location = new Point(550, 30),
                    Size = new Size(200, 200),
                    BorderStyle = BorderStyle.FixedSingle,
                    SizeMode = PictureBoxSizeMode.Zoom
                };
                tab.Controls.Add(_logoBox);

                var btnVelgLogo = new Button
                {
                    Text = "Velg logo...",
                    Location = new Point(550, 240),
                    Width = 120,
                    Height = 30
                };
                btnVelgLogo.Click += BtnVelgLogo_Click;
                tab.Controls.Add(btnVelgLogo);

                var btnLagre = new Button
                {
                    Text = "Lagre",
                    Location = new Point(20, 320),
                    Width = 100,
                    Height = 35
                };
                btnLagre.Click += BtnLagreEget_Click;
                tab.Controls.Add(btnLagre);
            }

            private void BtnVelgLogo_Click(object sender, EventArgs e)
            {
                using (var dlg = new OpenFileDialog
                {
                    Title = "Velg logo",
                    Filter = "Bildefiler|*.png;*.jpg;*.jpeg;*.bmp"
                })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        try
                        {
                            File.Copy(dlg.FileName, _repo.LogoPath, true);
                            _logoBox.Image = Image.FromFile(_repo.LogoPath);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(this, "Feil ved lagring av logo: " + ex.Message,
                                "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            }

            private void BtnLagreEget_Click(object sender, EventArgs e)
            {
                try
                {
                    var info = new EgenFirmaInfo
                    {
                        Navn = _txtNavn.Text.Trim(),
                        Avdeling = _txtAvdeling.Text.Trim(),
                        OrgNr = _txtOrgNr.Text.Trim(),
                        BedrNr = _txtBedrNr.Text.Trim(),
                        Adresse1 = _txtAdr1.Text.Trim(),
                        Adresse2 = _txtAdr2.Text.Trim(),
                        Telefon = _txtTel.Text.Trim(),
                        Epost = _txtEpost.Text.Trim()
                    };

                    _repo.LagreEgenFirma(info);
                    MessageBox.Show(this, "Opplysninger lagret.", "Info",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Feil ved lagring: " + ex.Message,
                        "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            private void LastEgetFirma()
            {
                var info = _repo.LastEgenFirma();
                _txtNavn.Text = info.Navn;
                _txtAvdeling.Text = info.Avdeling;
                _txtOrgNr.Text = info.OrgNr;
                _txtBedrNr.Text = info.BedrNr;
                _txtAdr1.Text = info.Adresse1;
                _txtAdr2.Text = info.Adresse2;
                _txtTel.Text = info.Telefon;
                _txtEpost.Text = info.Epost;

                try
                {
                    if (File.Exists(_repo.LogoPath))
                        _logoBox.Image = Image.FromFile(_repo.LogoPath);
                }
                catch { }
            }

            // ---------- KUNDER ----------

            private void ByggKunderTab(TabPage tab)
            {
                _gridKunder = new DataGridView
                {
                    Location = new Point(20, 20),
                    Size = new Size(1000, 500),
                    AllowUserToAddRows = false,
                    AutoGenerateColumns = false,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
                };

                var colSelskap = new DataGridViewComboBoxColumn
                {
                    HeaderText = "Selskap-type",
                    DataPropertyName = "Selskap",
                    DataSource = Enum.GetValues(typeof(SelskapType)),
                    Width = 120
                };
                _gridKunder.Columns.Add(colSelskap);

                _gridKunder.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Navn",
                    DataPropertyName = "Navn"
                });
                _gridKunder.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Adresse1",
                    DataPropertyName = "Adresse1"
                });
                _gridKunder.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Adresse2",
                    DataPropertyName = "Adresse2"
                });
                _gridKunder.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Postnr/by",
                    DataPropertyName = "PostnrBy"
                });
                _gridKunder.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Telefon",
                    DataPropertyName = "Telefon"
                });
                _gridKunder.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "E-post",
                    DataPropertyName = "Epost"
                });
                _gridKunder.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Org nr",
                    DataPropertyName = "OrgNr"
                });
                _gridKunder.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Faktura merkes",
                    DataPropertyName = "FakturaMerke"
                });

                var btnLeggTil = new Button
                {
                    Text = "Legg til",
                    Location = new Point(20, 530),
                    Width = 100,
                    Height = 35
                };
                btnLeggTil.Click += (s, e) => _kundeListe.Add(new KundeFirmaInfo());

                var btnSlett = new Button
                {
                    Text = "Slett",
                    Location = new Point(130, 530),
                    Width = 100,
                    Height = 35
                };
                btnSlett.Click += (s, e) =>
                {
                    if (_gridKunder.CurrentRow != null &&
                        _gridKunder.CurrentRow.DataBoundItem is KundeFirmaInfo info)
                    {
                        _kundeListe.Remove(info);
                    }
                };

                var btnLagre = new Button
                {
                    Text = "Lagre",
                    Location = new Point(240, 530),
                    Width = 100,
                    Height = 35
                };
                btnLagre.Click += BtnLagreKunder_Click;

                tab.Controls.Add(_gridKunder);
                tab.Controls.Add(btnLeggTil);
                tab.Controls.Add(btnSlett);
                tab.Controls.Add(btnLagre);
            }

            private void BtnLagreKunder_Click(object sender, EventArgs e)
            {
                try
                {
                    _repo.LagreAlleKundeFirma(new List<KundeFirmaInfo>(_kundeListe));
                    MessageBox.Show(this, "Kunder lagret.", "Info",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Feil ved lagring: " + ex.Message,
                        "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            private void LastKunder()
            {
                var liste = _repo.LastAlleKundeFirma();
                _kundeListe = new BindingList<KundeFirmaInfo>(liste);
                _gridKunder.DataSource = _kundeListe;
            }

            // ---------- INNSTILLINGER ----------
private void ByggInnstillingerTab(TabPage tab)
{
    // Always keep "Lagre" visible by using a bottom-docked bar + a scrollable content panel.
    tab.SuspendLayout();
    tab.Controls.Clear();
    tab.AutoScroll = false;

    var bottomBar = new FlowLayoutPanel
    {
        Dock = DockStyle.Bottom,
        Height = 58,
        Padding = new Padding(10, 10, 10, 10),
        FlowDirection = FlowDirection.RightToLeft,
        WrapContents = false
    };

    var content = new Panel
    {
        Dock = DockStyle.Fill,
        AutoScroll = true
    };

    tab.Controls.Add(content);
    tab.Controls.Add(bottomBar);

    // --- sørg for at feltene finnes ---
    _txtCamRtsp ??= new TextBox();
    _txtCamToken ??= new TextBox();
    _chkAutoRegister ??= new CheckBox();

    _txtDahuaHost ??= new TextBox();
    _numDahuaPort ??= new NumericUpDown();
    _txtDahuaUser ??= new TextBox();
    _txtDahuaPass ??= new TextBox();

    _txtItsApiHostIp ??= new TextBox();
    _numItsApiPort ??= new NumericUpDown();
    _txtItsApiPath ??= new TextBox();

    _txtDocFolder ??= new TextBox();
    _btnBrowseDocFolder ??= new Button();

    _numDisplaySeconds ??= new NumericUpDown();

    // Porty muszą mieć zakres 1..65535
    _numDahuaPort.Minimum = 1;
    _numDahuaPort.Maximum = 65535;
    _numDahuaPort.Increment = 1;

    _numItsApiPort.Minimum = 1;
    _numItsApiPort.Maximum = 65535;
    _numItsApiPort.Increment = 1;

    _numDisplaySeconds.Minimum = 3;
    _numDisplaySeconds.Maximum = 300;
    _numDisplaySeconds.Increment = 1;

    // --- grupa: kamera / ANPR ---
    var grpAnpr = new GroupBox
    {
        Text = "ANPR – INNSTILLINGER (RTSP + TOKEN)",
        Location = new Point(10, 10),
        Size = new Size(1000, 180)
    };

    int xLabel = 15;
    int xVal = 15;
    int y = 25;

    grpAnpr.Controls.Add(new Label { Text = "RTSP URL:", Location = new Point(xLabel, y), AutoSize = true });
    _txtCamRtsp.Location = new Point(xVal, y + 25);
    _txtCamRtsp.Width = 980;
    grpAnpr.Controls.Add(_txtCamRtsp);

    y += 70;
    grpAnpr.Controls.Add(new Label { Text = "ANPR API Token:", Location = new Point(xLabel, y), AutoSize = true });
    _txtCamToken.Location = new Point(xVal, y + 25);
    _txtCamToken.Width = 980;
    grpAnpr.Controls.Add(_txtCamToken);

    _chkAutoRegister.Text = "Auto-registrer ved skiltgjenkjenning";
    _chkAutoRegister.Location = new Point(xVal, y + 60);
    _chkAutoRegister.AutoSize = true;
    grpAnpr.Controls.Add(_chkAutoRegister);

    content.Controls.Add(grpAnpr);

    // --- grupa: Dahua / ITS / dokumentmappe ---
    var grpSystem = new GroupBox
    {
        Text = "Dahua / ITS API / Dokumentmappe",
        Location = new Point(10, grpAnpr.Bottom + 10),
        Size = new Size(1000, 260)
    };

    int y2 = 25;
    int xCol1 = 15;
    int xCol2 = 260;
    int xCol3 = 520;
    int xCol4 = 765;

    // Dahua
    grpSystem.Controls.Add(new Label { Text = "Dahua Host:", Location = new Point(xCol1, y2), AutoSize = true });
    _txtDahuaHost.Location = new Point(xCol1, y2 + 25);
    _txtDahuaHost.Width = 230;
    grpSystem.Controls.Add(_txtDahuaHost);

    grpSystem.Controls.Add(new Label { Text = "Port:", Location = new Point(xCol2, y2), AutoSize = true });
    _numDahuaPort.Location = new Point(xCol2, y2 + 25);
    _numDahuaPort.Width = 120;
    grpSystem.Controls.Add(_numDahuaPort);

    grpSystem.Controls.Add(new Label { Text = "Bruker:", Location = new Point(xCol3, y2), AutoSize = true });
    _txtDahuaUser.Location = new Point(xCol3, y2 + 25);
    _txtDahuaUser.Width = 230;
    grpSystem.Controls.Add(_txtDahuaUser);

    grpSystem.Controls.Add(new Label { Text = "Passord:", Location = new Point(xCol4, y2), AutoSize = true });
    _txtDahuaPass.Location = new Point(xCol4, y2 + 25);
    _txtDahuaPass.Width = 230;
    grpSystem.Controls.Add(_txtDahuaPass);

    y2 += 70;

    // ITS API (server on PC)
    grpSystem.Controls.Add(new Label { Text = "ITS API Host (PC):", Location = new Point(xCol1, y2), AutoSize = true });
    _txtItsApiHostIp.Location = new Point(xCol1, y2 + 25);
    _txtItsApiHostIp.Width = 230;
    grpSystem.Controls.Add(_txtItsApiHostIp);

    grpSystem.Controls.Add(new Label { Text = "ITS API Port:", Location = new Point(xCol2, y2), AutoSize = true });
    _numItsApiPort.Location = new Point(xCol2, y2 + 25);
    _numItsApiPort.Width = 120;
    grpSystem.Controls.Add(_numItsApiPort);

    grpSystem.Controls.Add(new Label { Text = "ITS API Path:", Location = new Point(xCol3, y2), AutoSize = true });
    _txtItsApiPath.Location = new Point(xCol3, y2 + 25);
    _txtItsApiPath.Width = 475;
    grpSystem.Controls.Add(_txtItsApiPath);

    y2 += 70;

    // Dokumentmappe
    grpSystem.Controls.Add(new Label { Text = "Dokumentmappe:", Location = new Point(xCol1, y2), AutoSize = true });
    _txtDocFolder.Location = new Point(xCol1, y2 + 25);
    _txtDocFolder.Width = 450;
    grpSystem.Controls.Add(_txtDocFolder);

    _btnBrowseDocFolder.Text = "Velg....";
    _btnBrowseDocFolder.Width = 100;
    _btnBrowseDocFolder.Height = _txtDocFolder.Height;
    _btnBrowseDocFolder.Location = new Point(xCol3, y2 + 25);

    _btnBrowseDocFolder.Click -= BtnBrowseDocFolder_Click;
    _btnBrowseDocFolder.Click += BtnBrowseDocFolder_Click;
    grpSystem.Controls.Add(_btnBrowseDocFolder);

    // Visningstid
    grpSystem.Controls.Add(new Label { Text = "Visningstid (sek):", Location = new Point(xCol4, y2), AutoSize = true });
    _numDisplaySeconds.Location = new Point(xCol4, y2 + 25);
    _numDisplaySeconds.Width = 230;
    grpSystem.Controls.Add(_numDisplaySeconds);

    content.Controls.Add(grpSystem);

    // Lagre-knapp (samler alle innstillinger) – always visible in bottom bar
    var btnLagre = new Button
    {
        Text = "Lagre",
        Width = 120,
        Height = 36,
        Location = new Point(130,245),
    };
    btnLagre.Click += BtnLagreInnstillinger_Click;
    bottomBar.Controls.Add(btnLagre);

                

    tab.ResumeLayout(true);
}

            private void ByggPreviewKameraTab(TabPage tab)
            {
                // Scrollable content + bottom save bar (consistent with other tabs)
                tab.SuspendLayout();
                tab.Controls.Clear();
                tab.AutoScroll = false;

                var bottomBar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 58,
                    Padding = new Padding(10, 10, 10, 10),
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false
                };

                var content = new Panel
                {
                    Dock = DockStyle.Fill,
                    AutoScroll = true
                };

                tab.Controls.Add(content);
                tab.Controls.Add(bottomBar);

                // Ensure controls exist
                _chkCam2Enabled ??= new CheckBox();
                _chkCam2AutoRefresh ??= new CheckBox();
                _cmbCam2Protocol ??= new ComboBox();
                _txtCam2Host ??= new TextBox();
                _numCam2Port ??= new NumericUpDown();
                _txtCam2User ??= new TextBox();
                _txtCam2Pass ??= new TextBox();
                _numCam2Channel ??= new NumericUpDown();
                _txtCam2Path ??= new TextBox();
                _txtCam2RtspOverride ??= new TextBox();

                _chkCam3Enabled ??= new CheckBox();
                _cmbCam3Protocol ??= new ComboBox();
                _txtCam3Host ??= new TextBox();
                _numCam3Port ??= new NumericUpDown();
                _txtCam3User ??= new TextBox();
                _txtCam3Pass ??= new TextBox();
                _numCam3Channel ??= new NumericUpDown();
                _txtCam3Path ??= new TextBox();
                _txtCam3RtspOverride ??= new TextBox();

                void SetupPort(NumericUpDown n)
                {
                    n.Minimum = 1;
                    n.Maximum = 65535;
                    n.Increment = 1;
                }

                void SetupChannel(NumericUpDown n)
                {
                    n.Minimum = 0;
                    n.Maximum = 32;
                    n.Increment = 1;
                }

                SetupPort(_numCam2Port);
                SetupPort(_numCam3Port);
                SetupChannel(_numCam2Channel);
                SetupChannel(_numCam3Channel);

                _txtCam2Pass.UseSystemPasswordChar = true;
                _txtCam3Pass.UseSystemPasswordChar = true;

                _txtCam2RtspOverride.Width = 980;
                _txtCam3RtspOverride.Width = 980;

                void SetupProtocol(ComboBox c)
                {
                    c.DropDownStyle = ComboBoxStyle.DropDownList;
                    c.Items.Clear();
                    c.Items.Add("RTSP (H.264)");
                    c.Items.Add("AXIS HTTP MJPEG (port 80)");
                    if (c.SelectedIndex < 0) c.SelectedIndex = 0;
                }

                SetupProtocol(_cmbCam2Protocol);
                SetupProtocol(_cmbCam3Protocol);

                string ProtocolKeyFromUi(ComboBox c)
                {
                    var t = (c.SelectedItem?.ToString() ?? "").Trim();
                    return t.StartsWith("AXIS", StringComparison.OrdinalIgnoreCase)
                        ? "axis_http_mjpeg"
                        : "rtsp";
                }

                string BuildRtsp(string host, int port, string user, string pass, int channel, string path)
                {
                    host = (host ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(host)) return "";
                    if (port < 1 || port > 65535) port = 554;
                    path = (path ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(path)) path = "/axis-media/media.amp";
                    if (!path.StartsWith("/")) path = "/" + path;

                    // Do NOT embed credentials in URL (Digest auth / special chars in password)
                    var url = $"rtsp://{host}:{port}{path}";
                    if (channel > 0 && !url.Contains("camera=", StringComparison.OrdinalIgnoreCase))
                        url += path.Contains("?") ? $"&camera={channel}" : $"?camera={channel}";
                    return url;
                }

                string BuildAxisHttpMjpeg(string host, int port, string user, string pass, int channel, string path)
                {
                    host = (host ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(host)) return "";
                    if (port < 1 || port > 65535) port = 80;
                    if (port == 554) port = 80;

                    path = (path ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(path)) path = "/axis-cgi/mjpg/video.cgi";
                    if (!path.StartsWith("/")) path = "/" + path;

                    // Do NOT embed credentials in URL (Digest auth / special chars in password)
                    var url = $"http://{host}:{port}{path}";
                    if (channel > 0 && !url.Contains("camera=", StringComparison.OrdinalIgnoreCase))
                        url += path.Contains("?") ? $"&camera={channel}" : $"?camera={channel}";
                    return url;
                }

                string BuildStreamUrl(ComboBox protoBox, string host, int port, string user, string pass, int channel, string path)
                {
                    var key = ProtocolKeyFromUi(protoBox);
                    return key == "axis_http_mjpeg"
                        ? BuildAxisHttpMjpeg(host, port, user, pass, channel, path)
                        : BuildRtsp(host, port, user, pass, channel, path);
                }

                // --- Camera 2 ---
                var grp2 = new GroupBox
                {
                    Text = "Inngang til vaskehallen (AXIS P1335)",
                    Location = new Point(10, 10),
                    Size = new Size(1000, 310)
                };

                _chkCam2Enabled.Text = "Aktiv";
                _chkCam2Enabled.Location = new Point(15, 30);
                _chkCam2Enabled.AutoSize = true;
                grp2.Controls.Add(_chkCam2Enabled);

                grp2.Controls.Add(new Label { Text = "Type:", Location = new Point(110, 30), AutoSize = true });
                _cmbCam2Protocol.Location = new Point(160, 26);
                _cmbCam2Protocol.Width = 220;
                _cmbCam2Protocol.SelectedIndexChanged += (_, __) =>
                {
                    try
                    {
                        var isHttp = _cmbCam2Protocol.SelectedIndex == 1;
                        if (isHttp)
                        {
                            if (_numCam2Port.Value == 554) _numCam2Port.Value = 80;
                            var p = (_txtCam2Path.Text ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(p) || p.Equals("/axis-media/media.amp", StringComparison.OrdinalIgnoreCase))
                                _txtCam2Path.Text = "/axis-cgi/mjpg/video.cgi";
                        }
                        else
                        {
                            if (_numCam2Port.Value == 80) _numCam2Port.Value = 554;
                            var p = (_txtCam2Path.Text ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(p) || p.Equals("/axis-cgi/mjpg/video.cgi", StringComparison.OrdinalIgnoreCase))
                                _txtCam2Path.Text = "/axis-media/media.amp";
                        }
                    }
                    catch { }
                };
                grp2.Controls.Add(_cmbCam2Protocol);

                _chkCam2AutoRefresh.Text = "Auto-oppfrisk ved heng (kun Kamera 2)";
                _chkCam2AutoRefresh.Location = new Point(405, 30);
                _chkCam2AutoRefresh.AutoSize = true;
                grp2.Controls.Add(_chkCam2AutoRefresh);

                grp2.Controls.Add(new Label { Text = "IP/Vert:", Location = new Point(15, 65), AutoSize = true });
                _txtCam2Host.Location = new Point(15, 90);
                _txtCam2Host.Width = 240;
                grp2.Controls.Add(_txtCam2Host);

                grp2.Controls.Add(new Label { Text = "Port:", Location = new Point(270, 65), AutoSize = true });
                _numCam2Port.Location = new Point(270, 90);
                _numCam2Port.Width = 120;
                grp2.Controls.Add(_numCam2Port);

                grp2.Controls.Add(new Label { Text = "Bruker:", Location = new Point(405, 65), AutoSize = true });
                _txtCam2User.Location = new Point(405, 90);
                _txtCam2User.Width = 180;
                grp2.Controls.Add(_txtCam2User);

                grp2.Controls.Add(new Label { Text = "Passord:", Location = new Point(600, 65), AutoSize = true });
                _txtCam2Pass.Location = new Point(600, 90);
                _txtCam2Pass.Width = 180;
                grp2.Controls.Add(_txtCam2Pass);

                grp2.Controls.Add(new Label { Text = "Kanal (camera=1..):", Location = new Point(795, 65), AutoSize = true });
                _numCam2Channel.Location = new Point(795, 90);
                _numCam2Channel.Width = 180;
                grp2.Controls.Add(_numCam2Channel);

                grp2.Controls.Add(new Label { Text = "Sti:", Location = new Point(15, 130), AutoSize = true });
                _txtCam2Path.Location = new Point(15, 155);
                _txtCam2Path.Width = 500;
                grp2.Controls.Add(_txtCam2Path);

                var btnGen2 = new Button
                {
                    Text = "Generer URL",
                    Location = new Point(530, 153),
                    Width = 140,
                    Height = 30
                };
                btnGen2.Click += (_, __) =>
                {
                    _txtCam2RtspOverride.Text = BuildStreamUrl(
                        _cmbCam2Protocol,
                        _txtCam2Host.Text,
                        (int)_numCam2Port.Value,
                        _txtCam2User.Text,
                        _txtCam2Pass.Text,
                        (int)_numCam2Channel.Value,
                        _txtCam2Path.Text);
                };
                grp2.Controls.Add(btnGen2);

                grp2.Controls.Add(new Label
                {
                    Text = "Full URL (valgfritt – hvis du fyller inn her, ignoreres feltene over):",
                    Location = new Point(15, 195),
                    AutoSize = true
                });
                _txtCam2RtspOverride.Location = new Point(15, 220);
                _txtCam2RtspOverride.Width = 960;
                grp2.Controls.Add(_txtCam2RtspOverride);

                grp2.Controls.Add(new Label
                {
                    Text = "Tips AXIS: RTSP er vanligvis /axis-media/media.amp (port 554). For MJPEG over HTTP: /axis-cgi/mjpg/video.cgi (port 80) og camera=4 (Input Number).",
                    Location = new Point(15, 255),
                    Size = new Size(960, 40)
                });

                content.Controls.Add(grp2);

                // --- Camera 3 ---
                var grp3 = new GroupBox
                {
                    Text = "Avgang fra vaskehallen (AXIS P1335)",
                    Location = new Point(10, grp2.Bottom + 10),
                    Size = new Size(1000, 310)
                };

                _chkCam3Enabled.Text = "Aktiv";
                _chkCam3Enabled.Location = new Point(15, 30);
                _chkCam3Enabled.AutoSize = true;
                grp3.Controls.Add(_chkCam3Enabled);

                grp3.Controls.Add(new Label { Text = "Type:", Location = new Point(110, 30), AutoSize = true });
                _cmbCam3Protocol.Location = new Point(160, 26);
                _cmbCam3Protocol.Width = 220;
                _cmbCam3Protocol.SelectedIndexChanged += (_, __) =>
                {
                    try
                    {
                        var isHttp = _cmbCam3Protocol.SelectedIndex == 1;
                        if (isHttp)
                        {
                            if (_numCam3Port.Value == 554) _numCam3Port.Value = 80;
                            var p = (_txtCam3Path.Text ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(p) || p.Equals("/axis-media/media.amp", StringComparison.OrdinalIgnoreCase))
                                _txtCam3Path.Text = "/axis-cgi/mjpg/video.cgi";
                        }
                        else
                        {
                            if (_numCam3Port.Value == 80) _numCam3Port.Value = 554;
                            var p = (_txtCam3Path.Text ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(p) || p.Equals("/axis-cgi/mjpg/video.cgi", StringComparison.OrdinalIgnoreCase))
                                _txtCam3Path.Text = "/axis-media/media.amp";
                        }
                    }
                    catch { }
                };
                grp3.Controls.Add(_cmbCam3Protocol);

                grp3.Controls.Add(new Label { Text = "IP/Vert:", Location = new Point(15, 65), AutoSize = true });
                _txtCam3Host.Location = new Point(15, 90);
                _txtCam3Host.Width = 240;
                grp3.Controls.Add(_txtCam3Host);

                grp3.Controls.Add(new Label { Text = "Port:", Location = new Point(270, 65), AutoSize = true });
                _numCam3Port.Location = new Point(270, 90);
                _numCam3Port.Width = 120;
                grp3.Controls.Add(_numCam3Port);

                grp3.Controls.Add(new Label { Text = "Bruker:", Location = new Point(405, 65), AutoSize = true });
                _txtCam3User.Location = new Point(405, 90);
                _txtCam3User.Width = 180;
                grp3.Controls.Add(_txtCam3User);

                grp3.Controls.Add(new Label { Text = "Passord:", Location = new Point(600, 65), AutoSize = true });
                _txtCam3Pass.Location = new Point(600, 90);
                _txtCam3Pass.Width = 180;
                grp3.Controls.Add(_txtCam3Pass);

                grp3.Controls.Add(new Label { Text = "Kanal (valgfritt):", Location = new Point(795, 65), AutoSize = true });
                _numCam3Channel.Location = new Point(795, 90);
                _numCam3Channel.Width = 180;
                grp3.Controls.Add(_numCam3Channel);

                grp3.Controls.Add(new Label { Text = "Sti:", Location = new Point(15, 130), AutoSize = true });
                _txtCam3Path.Location = new Point(15, 155);
                _txtCam3Path.Width = 500;
                grp3.Controls.Add(_txtCam3Path);

                var btnGen3 = new Button
                {
                    Text = "Generer URL",
                    Location = new Point(530, 153),
                    Width = 140,
                    Height = 30
                };
                btnGen3.Click += (_, __) =>
                {
                    _txtCam3RtspOverride.Text = BuildStreamUrl(
                        _cmbCam3Protocol,
                        _txtCam3Host.Text,
                        (int)_numCam3Port.Value,
                        _txtCam3User.Text,
                        _txtCam3Pass.Text,
                        (int)_numCam3Channel.Value,
                        _txtCam3Path.Text);
                };
                grp3.Controls.Add(btnGen3);

                grp3.Controls.Add(new Label
                {
                    Text = "Full URL (valgfritt – hvis du fyller inn her, ignoreres feltene over):",
                    Location = new Point(15, 195),
                    AutoSize = true
                });
                _txtCam3RtspOverride.Location = new Point(15, 220);
                _txtCam3RtspOverride.Width = 960;
                grp3.Controls.Add(_txtCam3RtspOverride);

                grp3.Controls.Add(new Label
                {
                    Text = "Tips Axis: /axis-media/media.amp",
                    Location = new Point(15, 255),
                    Size = new Size(960, 40)
                });

                content.Controls.Add(grp3);

                // Save button
                var btnLagre = new Button
                {
                    Text = "Lagre",
                    Width = 120,
                    Height = 36,
                };
                btnLagre.Click += BtnLagreInnstillinger_Click;
                bottomBar.Controls.Add(btnLagre);

                tab.ResumeLayout(true);
            }


            private void ByggByggDatabaseTab(TabPage tab)
            {
                // grupa: Dahua / ITS API / dokumenty
                var grpSystem = new GroupBox
                {
                    Text = "Dahua / ITS API / Dokumentmappe",
                    Location = new Point(10,10),
                    Size = new Size(1000, 310)
                };
                int y2 = 25;
                int y3 = 70;
                int xCol1 = 15;
                int xCol2 = 260;
                int xCol3 = 520;
                int xCol4 = 765;
                int xCol5 = 1000;

                // Dahua


                grpSystem.Controls.Add(new Label { Text = "Dahua Host:", Location = new Point(xCol1, y2), AutoSize = true });
                _txtDahuaHost.Location = new Point(xCol1, y2 + 25);
                _txtDahuaHost.Width = 230;
                grpSystem.Controls.Add(_txtDahuaHost);

                grpSystem.Controls.Add(new Label { Text = "Port:", Location = new Point(xCol2, y2), AutoSize = true });
                _numDahuaPort.Location = new Point(xCol2, y2 + 25);
                _numDahuaPort.Width = 120;
                grpSystem.Controls.Add(_numDahuaPort);

                grpSystem.Controls.Add(new Label { Text = "Bruker:", Location = new Point(xCol3, y2), AutoSize = true });
                _txtDahuaUser.Location = new Point(xCol3, y2 + 25);
                _txtDahuaUser.Width = 230;
                grpSystem.Controls.Add(_txtDahuaUser);

                grpSystem.Controls.Add(new Label { Text = "Passord:", Location = new Point(xCol4, y2), AutoSize = true });
                _txtDahuaPass.Location = new Point(xCol4, y2 + 25);
                _txtDahuaPass.Width = 230;
                grpSystem.Controls.Add(_txtDahuaPass);

                y2 += 70;

                // ITS API (server on PC)
                grpSystem.Controls.Add(new Label { Text = "ITS API Host (PC):", Location = new Point(xCol1, y2), AutoSize = true, });
                _txtItsApiHostIp.Location = new Point(xCol1, y2 + 25);
                _txtItsApiHostIp.Width = 230;
                grpSystem.Controls.Add(_txtItsApiHostIp);

                grpSystem.Controls.Add(new Label { Text = "ITS API Port:", Location = new Point(xCol2, y2), AutoSize = true });
                _numItsApiPort.Location = new Point(xCol2, y2 + 25);
                _numItsApiPort.Width = 120;
                grpSystem.Controls.Add(_numItsApiPort);

                grpSystem.Controls.Add(new Label { Text = "ITS API Path:", Location = new Point(xCol3, y2), AutoSize = true });
                _txtItsApiPath.Location = new Point(xCol3, y2 + 25);
                _txtItsApiPath.Width = 475;
                grpSystem.Controls.Add(_txtItsApiPath);

                y2 += 70;
                y3 += 170;

                // Dokumentmappe
                grpSystem.Controls.Add(new Label { Text = "Dokumentmappe:", Location = new Point(xCol1, y2), AutoSize = true });
                _txtDocFolder.Location = new Point(xCol1, y2 + 25);
                _txtDocFolder.Width = 450;
                grpSystem.Controls.Add(_txtDocFolder);

                // Browse button (choose folder)
                _btnBrowseDocFolder.Text = "Velg....";
                _btnBrowseDocFolder.Width = 100;
                _btnBrowseDocFolder.Height = _txtDocFolder.Height;
                _btnBrowseDocFolder.UseVisualStyleBackColor = true;
                _btnBrowseDocFolder.Location = new Point(xCol3, y2 + 25);
                _btnBrowseDocFolder.Enabled = true;
                _btnBrowseDocFolder.TabStop = true;

                // avoid multiple subscriptions when the tab is rebuilt
                _btnBrowseDocFolder.Click -= BtnBrowseDocFolder_Click;
                _btnBrowseDocFolder.Click += BtnBrowseDocFolder_Click;
                grpSystem.Controls.Add(_btnBrowseDocFolder);

                // Visningstid (sek) – hvor lenge data skal vises før skjermen nullstilles
                grpSystem.Controls.Add(new Label { Text = "Visningstid (sek):", Location = new Point(xCol1, y3), AutoSize = true });
                _numDisplaySeconds.Location = new Point(xCol1, y3 + 25);
                _numDisplaySeconds.Width = 230;
                _numDisplaySeconds.Minimum = 3;
                _numDisplaySeconds.Maximum = 300;
                grpSystem.Controls.Add(_numDisplaySeconds);
            }
private void ByggDatabaseTab(TabPage tab)
{
    // Always keep "Lagre" visible by using a bottom-docked bar + a scrollable content panel.
    tab.SuspendLayout();
    tab.Controls.Clear();
    tab.AutoScroll = false;

    var bottomBar = new FlowLayoutPanel
    {
        Dock = DockStyle.Bottom,
        Height = 58,
        Padding = new Padding(10, 10, 10, 10),
        FlowDirection = FlowDirection.RightToLeft,
        WrapContents = false
    };

    var content = new Panel
    {
        Dock = DockStyle.Fill,
        AutoScroll = true
    };

    tab.Controls.Add(content);
    tab.Controls.Add(bottomBar);

    // ensure controls
    _chkDbEnabled ??= new CheckBox();
    _txtDbHost ??= new TextBox();
    _txtDbWorkerHost ??= new TextBox();
    _numDbPort ??= new NumericUpDown();
    _txtDbName ??= new TextBox();
    _txtDbAdminUser ??= new TextBox();
    _txtDbAdminPass ??= new TextBox();
    _txtDbWorkerUser ??= new TextBox();
    _txtDbWorkerPass ??= new TextBox();
    _numWorkerRefresh ??= new NumericUpDown();
    _chkWorkerOnlyUnconfirmed ??= new CheckBox();
    _txtUiAdminPassword ??= new TextBox();
    _txtUiWorkerPassword ??= new TextBox();

    _numDbPort.Minimum = 1;
    _numDbPort.Maximum = 65535;
    _numDbPort.Increment = 1;

    _numWorkerRefresh.Minimum = 1;
    _numWorkerRefresh.Maximum = 3600;
    _numWorkerRefresh.Increment = 1;

    var grpDb = new GroupBox
    {
        Text = "Database (Azure Postgres)",
        Location = new Point(10, 10),
        Size = new Size(1000, 250)
    };

    int dxLabel = 15;
    int dxVal1 = 260;
    int dxVal2 = 640;
    int dy = 30;
    // row 1 is taller because we show separate host fields for Admin and Worker
    int rowH = 85;

    _chkDbEnabled.Text = "Aktivert";
    _chkDbEnabled.Location = new Point(dxLabel, dy + 20);
    _chkDbEnabled.AutoSize = true;
    grpDb.Controls.Add(_chkDbEnabled);

    grpDb.Controls.Add(new Label { Text = "Admin-host:", Location = new Point(dxVal1, dy), AutoSize = true });
    _txtDbHost.Location = new Point(dxVal1, dy + 20);
    _txtDbHost.Width = 340;
    grpDb.Controls.Add(_txtDbHost);

    grpDb.Controls.Add(new Label { Text = "Worker-host:", Location = new Point(dxVal1, dy + 45), AutoSize = true });
    _txtDbWorkerHost.Location = new Point(dxVal1, dy + 65);
    _txtDbWorkerHost.Width = 340;
    grpDb.Controls.Add(_txtDbWorkerHost);

    grpDb.Controls.Add(new Label { Text = "Port:", Location = new Point(dxVal2, dy), AutoSize = true });
    _numDbPort.Location = new Point(dxVal2, dy + 20);
    _numDbPort.Width = 120;
    grpDb.Controls.Add(_numDbPort);

    // row 2
    dy += rowH;
    grpDb.Controls.Add(new Label { Text = "Database:", Location = new Point(dxLabel, dy), AutoSize = true });
    _txtDbName.Location = new Point(dxLabel, dy + 20);
    _txtDbName.Width = 220;
    grpDb.Controls.Add(_txtDbName);

    grpDb.Controls.Add(new Label { Text = "Admin-bruker:", Location = new Point(dxVal1, dy), AutoSize = true });
    _txtDbAdminUser.Location = new Point(dxVal1, dy + 20);
    _txtDbAdminUser.Width = 160;
    grpDb.Controls.Add(_txtDbAdminUser);

    grpDb.Controls.Add(new Label { Text = "Admin-passord:", Location = new Point(dxVal1 + 180, dy), AutoSize = true });
    _txtDbAdminPass.Location = new Point(dxVal1 + 180, dy + 20);
    _txtDbAdminPass.Width = 160;
    _txtDbAdminPass.UseSystemPasswordChar = true;
    grpDb.Controls.Add(_txtDbAdminPass);

    grpDb.Controls.Add(new Label { Text = "Worker-bruker:", Location = new Point(dxVal2, dy), AutoSize = true });
    _txtDbWorkerUser.Location = new Point(dxVal2, dy + 20);
    _txtDbWorkerUser.Width = 160;
    grpDb.Controls.Add(_txtDbWorkerUser);

    grpDb.Controls.Add(new Label { Text = "Worker-passord:", Location = new Point(dxVal2 + 180, dy), AutoSize = true });
    _txtDbWorkerPass.Location = new Point(dxVal2 + 180, dy + 20);
    _txtDbWorkerPass.Width = 160;
    _txtDbWorkerPass.UseSystemPasswordChar = true;
    grpDb.Controls.Add(_txtDbWorkerPass);

    // row 3 – worker UI
    dy += rowH;
    grpDb.Controls.Add(new Label { Text = "Worker refresh (sek):", Location = new Point(dxLabel, dy), AutoSize = true });
    _numWorkerRefresh.Location = new Point(dxLabel, dy + 20);
    _numWorkerRefresh.Width = 120;
    grpDb.Controls.Add(_numWorkerRefresh);

    _chkWorkerOnlyUnconfirmed.Text = "Vis kun ubekreftet";
    _chkWorkerOnlyUnconfirmed.Location = new Point(dxVal1, dy + 20);
    _chkWorkerOnlyUnconfirmed.AutoSize = true;
    grpDb.Controls.Add(_chkWorkerOnlyUnconfirmed);

    content.Controls.Add(grpDb);

    // UI passord (Admin/Worker)
    var grpAuth = new GroupBox
    {
        Text = "Passord (UI)",
        Location = new Point(10, grpDb.Bottom + 10),
        Size = new Size(1000, 140)
    };

    grpAuth.Controls.Add(new Label { Text = "Admin passord:", Location = new Point(15, 32), AutoSize = true });
    _txtUiAdminPassword.Location = new Point(150, 28);
    _txtUiAdminPassword.Width = 260;
    _txtUiAdminPassword.UseSystemPasswordChar = true;
    grpAuth.Controls.Add(_txtUiAdminPassword);

    grpAuth.Controls.Add(new Label { Text = "Worker passord:", Location = new Point(15, 72), AutoSize = true });
    _txtUiWorkerPassword.Location = new Point(150, 68);
    _txtUiWorkerPassword.Width = 260;
    _txtUiWorkerPassword.UseSystemPasswordChar = true;
    grpAuth.Controls.Add(_txtUiWorkerPassword);

    grpAuth.Controls.Add(new Label
    {
        Text = "Brukes for å åpne beskyttede dialoger. (Worker trenger admin-passord for Innstillinger.)",
        Location = new Point(450, 32),
        Size = new Size(520, 80)
    });

    content.Controls.Add(grpAuth);


    var btnLagre = new Button
    {
        Text = "Lagre",
        Width = 120,
        Height = 36,
    };
    btnLagre.Click += BtnLagreInnstillinger_Click;
    bottomBar.Controls.Add(btnLagre);
    tab.ResumeLayout(true);
}

            private void ByggUtseendeTab(TabPage tab)
            {
                tab.AutoScroll = true;

                int xLabel = 20;
                int xVal = 320;
                int y = 20;
                int dy = 34;

                tab.Controls.Add(new Label { Text = "Juster layout og skrifttyper for Kameralog.", Location = new Point(xLabel, y), AutoSize = true });
                y += dy;

                _chkLayoutLive = new CheckBox { Text = "Forhåndsvis endringer (live)", Location = new Point(xLabel, y), AutoSize = true, Checked = true };
                tab.Controls.Add(_chkLayoutLive);
                y += dy + 6;

                // Left panel
                var grpLeft = new GroupBox { Text = "Venstre panel (info)", Location = new Point(10, y), Size = new Size(980, 170) };
                y += grpLeft.Height + 10;

                grpLeft.Controls.Add(new Label { Text = "Bredde (px) (0 = auto):", Location = new Point(15, 32), AutoSize = true });
                _numLayoutLeftWidth = new NumericUpDown { Location = new Point(220, 28), Width = 100, Minimum = 0, Maximum = 1200, Increment = 10 };
                grpLeft.Controls.Add(_numLayoutLeftWidth);

                grpLeft.Controls.Add(new Label { Text = "Min (px):", Location = new Point(360, 32), AutoSize = true });
                _numLayoutLeftMin = new NumericUpDown { Location = new Point(430, 28), Width = 90, Minimum = 200, Maximum = 1200, Increment = 10 };
                grpLeft.Controls.Add(_numLayoutLeftMin);

                grpLeft.Controls.Add(new Label { Text = "Max (px):", Location = new Point(540, 32), AutoSize = true });
                _numLayoutLeftMax = new NumericUpDown { Location = new Point(615, 28), Width = 90, Minimum = 200, Maximum = 1600, Increment = 10 };
                grpLeft.Controls.Add(_numLayoutLeftMax);

                grpLeft.Controls.Add(new Label { Text = "Min høyre (px):", Location = new Point(725, 32), AutoSize = true });
                _numLayoutRightMin = new NumericUpDown { Location = new Point(840, 28), Width = 90, Minimum = 400, Maximum = 3000, Increment = 20 };
                grpLeft.Controls.Add(_numLayoutRightMin);

                grpLeft.Controls.Add(new Label { Text = "Header høyde (px) (0 = auto):", Location = new Point(15, 78), AutoSize = true });
                _numLayoutHeaderH = new NumericUpDown { Location = new Point(220, 74), Width = 100, Minimum = 0, Maximum = 200, Increment = 2 };
                grpLeft.Controls.Add(_numLayoutHeaderH);

                grpLeft.Controls.Add(new Label { Text = "Skilt-høyde (px) (0 = auto):", Location = new Point(360, 78), AutoSize = true });
                _numLayoutPlateH = new NumericUpDown { Location = new Point(560, 74), Width = 100, Minimum = 0, Maximum = 260, Increment = 2 };
                grpLeft.Controls.Add(_numLayoutPlateH);

                tab.Controls.Add(grpLeft);

                // Cameras
                var grpCam = new GroupBox { Text = "Kameraer", Location = new Point(10, y), Size = new Size(980, 170) };
                y += grpCam.Height + 10;

                grpCam.Controls.Add(new Label { Text = "Kamera 1 høyde (% av høyre):", Location = new Point(15, 32), AutoSize = true });
                _numLayoutCamMainPct = new NumericUpDown { Location = new Point(220, 28), Width = 80, Minimum = 30, Maximum = 80, Increment = 1 };
                grpCam.Controls.Add(_numLayoutCamMainPct);

                grpCam.Controls.Add(new Label { Text = "Bunnlinje (knapper) høyde (px):", Location = new Point(360, 32), AutoSize = true });
                _numLayoutBottomH = new NumericUpDown { Location = new Point(610, 28), Width = 90, Minimum = 60, Maximum = 160, Increment = 2 };
                grpCam.Controls.Add(_numLayoutBottomH);

                grpCam.Controls.Add(new Label { Text = "Visning Kamera 1:", Location = new Point(15, 78), AutoSize = true });
                _cmbCam1Mode = new ComboBox { Location = new Point(220, 74), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
                _cmbCam1Mode.Items.AddRange(new object[] { "Fyll (crop) - uten svarte felt", "Tilpass (fit) - uten kutt" });
                grpCam.Controls.Add(_cmbCam1Mode);

                grpCam.Controls.Add(new Label { Text = "Kamera 2:", Location = new Point(380, 78), AutoSize = true });
                _cmbCam2Mode = new ComboBox { Location = new Point(450, 74), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
                _cmbCam2Mode.Items.AddRange(new object[] { "Fyll (crop) - uten svarte felt", "Tilpass (fit) - uten kutt" });
                grpCam.Controls.Add(_cmbCam2Mode);

                grpCam.Controls.Add(new Label { Text = "Kamera 3:", Location = new Point(610, 78), AutoSize = true });
                _cmbCam3Mode = new ComboBox { Location = new Point(680, 74), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
                _cmbCam3Mode.Items.AddRange(new object[] { "Fyll (crop) - uten svarte felt", "Tilpass (fit) - uten kutt" });
                grpCam.Controls.Add(_cmbCam3Mode);

                tab.Controls.Add(grpCam);

                // Fonts
                var grpFont = new GroupBox { Text = "Skrifttyper", Location = new Point(10, y), Size = new Size(980, 160) };
                y += grpFont.Height + 10;

                grpFont.Controls.Add(new Label { Text = "Overskrift (px/pt):", Location = new Point(15, 32), AutoSize = true });
                _numLayoutHeaderFont = new NumericUpDown { Location = new Point(220, 28), Width = 90, Minimum = 0, Maximum = 60, Increment = 1 };
                grpFont.Controls.Add(_numLayoutHeaderFont);

                grpFont.Controls.Add(new Label { Text = "Internnr (px/pt):", Location = new Point(360, 32), AutoSize = true });
                _numLayoutInternFont = new NumericUpDown { Location = new Point(470, 28), Width = 90, Minimum = 0, Maximum = 90, Increment = 1 };
                grpFont.Controls.Add(_numLayoutInternFont);

                grpFont.Controls.Add(new Label { Text = "Info tekst (px/pt):", Location = new Point(610, 32), AutoSize = true });
                _numLayoutInfoFont = new NumericUpDown { Location = new Point(740, 28), Width = 90, Minimum = 0, Maximum = 60, Increment = 1 };
                grpFont.Controls.Add(_numLayoutInfoFont);

                grpFont.Controls.Add(new Label { Text = "(0 = auto-skaler etter skjerm)", Location = new Point(15, 78), AutoSize = true });

                tab.Controls.Add(grpFont);

                // Buttons
                var btnApply = new Button { Text = "Bruk nå", Location = new Point(20, y), Width = 120, Height = 34 };
                var btnReset = new Button { Text = "Tilbakestill", Location = new Point(150, y), Width = 120, Height = 34 };
                tab.Controls.Add(btnApply);
                tab.Controls.Add(btnReset);

                btnApply.Click += (_, __) => ApplyLayoutPreview(save: false);
                btnReset.Click += (_, __) =>
                {
                    var def = UiLayoutSettings.CreateDefault();
                    SetLayoutControls(def);
                    ApplyLayoutPreview(save: false);
                };

                void wire(Control? c)
                {
                    if (c == null) return;
                    if (c is NumericUpDown nud) nud.ValueChanged += (_, __) => { if (_chkLayoutLive != null && _chkLayoutLive.Checked) ApplyLayoutPreview(save: false); };
                    if (c is ComboBox cb) cb.SelectedIndexChanged += (_, __) => { if (_chkLayoutLive != null && _chkLayoutLive.Checked) ApplyLayoutPreview(save: false); };
                }

                wire(_numLayoutLeftWidth);
                wire(_numLayoutLeftMin);
                wire(_numLayoutLeftMax);
                wire(_numLayoutRightMin);
                wire(_numLayoutHeaderH);
                wire(_numLayoutPlateH);
                wire(_numLayoutBottomH);
                wire(_numLayoutCamMainPct);
                wire(_numLayoutInternFont);
                wire(_numLayoutInfoFont);
                wire(_numLayoutHeaderFont);
                wire(_cmbCam1Mode);
                wire(_cmbCam2Mode);
                wire(_cmbCam3Mode);

                tab.ResumeLayout(true);
            }

            private void SetLayoutControls(UiLayoutSettings s)
            {
                if (_numLayoutLeftWidth != null) _numLayoutLeftWidth.Value = Math.Max(_numLayoutLeftWidth.Minimum, Math.Min(_numLayoutLeftWidth.Maximum, s.LeftPanelWidthPx));
                if (_numLayoutLeftMin != null) _numLayoutLeftMin.Value = Math.Max(_numLayoutLeftMin.Minimum, Math.Min(_numLayoutLeftMin.Maximum, s.LeftPanelMinPx));
                if (_numLayoutLeftMax != null) _numLayoutLeftMax.Value = Math.Max(_numLayoutLeftMax.Minimum, Math.Min(_numLayoutLeftMax.Maximum, s.LeftPanelMaxPx));
                if (_numLayoutRightMin != null) _numLayoutRightMin.Value = Math.Max(_numLayoutRightMin.Minimum, Math.Min(_numLayoutRightMin.Maximum, s.RightPanelMinPx));
                if (_numLayoutHeaderH != null) _numLayoutHeaderH.Value = Math.Max(_numLayoutHeaderH.Minimum, Math.Min(_numLayoutHeaderH.Maximum, s.HeaderHeightPx));
                if (_numLayoutPlateH != null) _numLayoutPlateH.Value = Math.Max(_numLayoutPlateH.Minimum, Math.Min(_numLayoutPlateH.Maximum, s.PlateHeightPx));
                if (_numLayoutBottomH != null) _numLayoutBottomH.Value = Math.Max(_numLayoutBottomH.Minimum, Math.Min(_numLayoutBottomH.Maximum, s.BottomBarHeightPx));

                if (_numLayoutCamMainPct != null)
                {
                    var pct = (int)Math.Round(Math.Clamp(s.CameraMainPercent, 0.30, 0.80) * 100);
                    _numLayoutCamMainPct.Value = Math.Max(_numLayoutCamMainPct.Minimum, Math.Min(_numLayoutCamMainPct.Maximum, pct));
                }

                if (_numLayoutHeaderFont != null) _numLayoutHeaderFont.Value = (decimal)Math.Max(0, s.HeaderTitleFontSize);
                if (_numLayoutInternFont != null) _numLayoutInternFont.Value = (decimal)Math.Max(0, s.InternnrFontSize);
                if (_numLayoutInfoFont != null) _numLayoutInfoFont.Value = (decimal)Math.Max(0, s.InfoFontSize);

                int pick(string? mode)
                {
                    var m = (mode ?? "crop").Trim().ToLowerInvariant();
                    return (m == "fit" || m == "tilpass") ? 1 : 0;
                }

                if (_cmbCam1Mode != null) _cmbCam1Mode.SelectedIndex = pick(s.Cam1Mode);
                if (_cmbCam2Mode != null) _cmbCam2Mode.SelectedIndex = pick(s.Cam2Mode);
                if (_cmbCam3Mode != null) _cmbCam3Mode.SelectedIndex = pick(s.Cam3Mode);
            }

            private UiLayoutSettings GetLayoutFromControls()
            {
                var s = UiLayoutSettings.CreateDefault();

                if (_numLayoutLeftWidth != null) s.LeftPanelWidthPx = (int)_numLayoutLeftWidth.Value;
                if (_numLayoutLeftMin != null) s.LeftPanelMinPx = (int)_numLayoutLeftMin.Value;
                if (_numLayoutLeftMax != null) s.LeftPanelMaxPx = (int)_numLayoutLeftMax.Value;
                if (_numLayoutRightMin != null) s.RightPanelMinPx = (int)_numLayoutRightMin.Value;
                if (_numLayoutHeaderH != null) s.HeaderHeightPx = (int)_numLayoutHeaderH.Value;
                if (_numLayoutPlateH != null) s.PlateHeightPx = (int)_numLayoutPlateH.Value;
                if (_numLayoutBottomH != null) s.BottomBarHeightPx = (int)_numLayoutBottomH.Value;

                if (_numLayoutCamMainPct != null) s.CameraMainPercent = (double)_numLayoutCamMainPct.Value / 100.0;

                if (_numLayoutHeaderFont != null) s.HeaderTitleFontSize = (float)_numLayoutHeaderFont.Value;
                if (_numLayoutInternFont != null) s.InternnrFontSize = (float)_numLayoutInternFont.Value;
                if (_numLayoutInfoFont != null) s.InfoFontSize = (float)_numLayoutInfoFont.Value;

                string mode(ComboBox? cb)
                {
                    if (cb == null) return "crop";
                    return cb.SelectedIndex == 1 ? "fit" : "crop";
                }

                s.Cam1Mode = mode(_cmbCam1Mode);
                s.Cam2Mode = mode(_cmbCam2Mode);
                s.Cam3Mode = mode(_cmbCam3Mode);

                return s;
            }

            private void ApplyLayoutPreview(bool save)
            {
                try
                {
                    var s = GetLayoutFromControls();

                    if (save)
                    {
                        var folder = _txtDocFolder?.Text?.Trim();
                        if (string.IsNullOrWhiteSpace(folder)) folder = AppConfig.DocFolder;
                        UiLayoutSettings.Save(folder, s);
                    }

                    // Apply immediately if main form is open
                    try
                    {
                        HovedForm.Current?.ApplyUiLayout(s);
                    }
                    catch { }
                }
                catch { }
            }




            private void BtnBrowseDocFolder_Click(object sender, EventArgs e)
            {
                try
                {
                    using var dlg = new FolderBrowserDialog
                    {
                        Description = "Velg dokumentmappe",
                        UseDescriptionForTitle = true,
                        ShowNewFolderButton = true
                    };

                    var current = _txtDocFolder?.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                        dlg.SelectedPath = current;
                    else if (!string.IsNullOrWhiteSpace(AppConfig.DocFolder) && Directory.Exists(AppConfig.DocFolder))
                        dlg.SelectedPath = AppConfig.DocFolder;

                    if (dlg.ShowDialog(this) == DialogResult.OK)
                        _txtDocFolder.Text = dlg.SelectedPath;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Kunne ikke åpne mappevelger: " + ex.Message, "Feil",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            private void LastInnstillinger()
            {
                // kamera
                if (_txtCamRtsp != null) _txtCamRtsp.Text = AppConfig.CameraRtspUrl;
                if (_txtCamToken != null) _txtCamToken.Text = AppConfig.PlateRecognizerApiToken;

                // preview cameras
                try
                {
                    var c2 = AppConfig.Camera2Settings;
                    var c3 = AppConfig.Camera3Settings;

                    void SetProto(ComboBox box, string? proto)
                    {
                        if (box == null) return;
                        var p = (proto ?? "rtsp").Trim().ToLowerInvariant();
                        box.SelectedIndex = p == "axis_http_mjpeg" ? 1 : 0;
                    }

                    if (_chkCam2Enabled != null) _chkCam2Enabled.Checked = c2.Enabled;
                    if (_chkCam2AutoRefresh != null) _chkCam2AutoRefresh.Checked = c2.AutoRefreshOnFreeze;
                    if (_cmbCam2Protocol != null) SetProto(_cmbCam2Protocol, c2.Protocol);
                    if (_txtCam2Host != null) _txtCam2Host.Text = c2.Host ?? "";
                    if (_numCam2Port != null) _numCam2Port.Value = Math.Max(_numCam2Port.Minimum, Math.Min(_numCam2Port.Maximum, c2.Port));
                    if (_txtCam2User != null) _txtCam2User.Text = c2.Username ?? "";
                    if (_txtCam2Pass != null) _txtCam2Pass.Text = c2.Password ?? "";
                    if (_numCam2Channel != null) _numCam2Channel.Value = Math.Max(_numCam2Channel.Minimum, Math.Min(_numCam2Channel.Maximum, c2.Channel));
                    if (_txtCam2Path != null)
                    {
                        var def = (c2.Protocol ?? "rtsp").Trim().ToLowerInvariant() == "axis_http_mjpeg" ? "/axis-cgi/mjpg/video.cgi" : "/axis-media/media.amp";
                        _txtCam2Path.Text = string.IsNullOrWhiteSpace(c2.Path) ? def : c2.Path;
                    }
                    if (_txtCam2RtspOverride != null) _txtCam2RtspOverride.Text = c2.RtspUrl ?? "";

                    if (_chkCam3Enabled != null) _chkCam3Enabled.Checked = c3.Enabled;
                    if (_cmbCam3Protocol != null) SetProto(_cmbCam3Protocol, c3.Protocol);
                    if (_txtCam3Host != null) _txtCam3Host.Text = c3.Host ?? "";
                    if (_numCam3Port != null) _numCam3Port.Value = Math.Max(_numCam3Port.Minimum, Math.Min(_numCam3Port.Maximum, c3.Port));
                    if (_txtCam3User != null) _txtCam3User.Text = c3.Username ?? "";
                    if (_txtCam3Pass != null) _txtCam3Pass.Text = c3.Password ?? "";
                    if (_numCam3Channel != null) _numCam3Channel.Value = Math.Max(_numCam3Channel.Minimum, Math.Min(_numCam3Channel.Maximum, c3.Channel));
                    if (_txtCam3Path != null)
                    {
                        var def = (c3.Protocol ?? "rtsp").Trim().ToLowerInvariant() == "axis_http_mjpeg" ? "/axis-cgi/mjpg/video.cgi" : "/axis-media/media.amp";
                        _txtCam3Path.Text = string.IsNullOrWhiteSpace(c3.Path) ? def : c3.Path;
                    }
                    if (_txtCam3RtspOverride != null) _txtCam3RtspOverride.Text = c3.RtspUrl ?? "";
                }
                catch { }

                _txtDahuaHost.Text = AppConfig.DahuaHost;
                _numDahuaPort.Value = Math.Max(_numDahuaPort.Minimum, Math.Min(_numDahuaPort.Maximum, AppConfig.DahuaPort));
                _txtDahuaUser.Text = AppConfig.DahuaUsername;
                _txtDahuaPass.Text = AppConfig.DahuaPassword;

                _txtItsApiHostIp.Text = AppConfig.ItsApiHostIp;
                _numItsApiPort.Value = Math.Max(_numItsApiPort.Minimum, Math.Min(_numItsApiPort.Maximum, AppConfig.ItsApiPort));
                _txtItsApiPath.Text = AppConfig.ItsApiPath;

                _txtDocFolder.Text = AppConfig.DocFolder;

                // UI layout
                try
                {
                    var folder = _txtDocFolder?.Text?.Trim();
                    if (string.IsNullOrWhiteSpace(folder)) folder = AppConfig.DocFolder;
                    var lay = UiLayoutSettings.Load(folder);
                    SetLayoutControls(lay);
                }
                catch { }

                _chkAutoRegister.Checked = AppConfig.AutoRegisterOnPlate;
                _numDisplaySeconds.Value = Math.Max(_numDisplaySeconds.Minimum, Math.Min(_numDisplaySeconds.Maximum, AppConfig.DisplaySeconds));
            }
            private void LastDatabase()
            {
                // database
                _chkDbEnabled.Checked = AppConfig.DbEnabled;
                _txtDbHost.Text = AppConfig.DbHost;
                if (_txtDbWorkerHost != null) _txtDbWorkerHost.Text = AppConfig.DbWorkerHost;
                _numDbPort.Value = Math.Max(_numDbPort.Minimum, Math.Min(_numDbPort.Maximum, AppConfig.DbPort));
                _txtDbName.Text = AppConfig.DbDatabase;
                _txtDbAdminUser.Text = AppConfig.DbAdminUser;
                _txtDbAdminPass.Text = AppConfig.DbAdminPassword;
                _txtDbWorkerUser.Text = AppConfig.DbWorkerUser;
                _txtDbWorkerPass.Text = AppConfig.DbWorkerPassword;
                _numWorkerRefresh.Value = Math.Max(_numWorkerRefresh.Minimum, Math.Min(_numWorkerRefresh.Maximum, AppConfig.WorkerRefreshSeconds));
                _chkWorkerOnlyUnconfirmed.Checked = AppConfig.WorkerShowOnlyUnconfirmed;

                if (_txtUiAdminPassword != null) _txtUiAdminPassword.Text = AppConfig.UiAdminPassword;
                if (_txtUiWorkerPassword != null) _txtUiWorkerPassword.Text = AppConfig.UiWorkerPassword;


            }

            // ---------- EGET SESONG PRISER ----------

           
            private void ByggsesongpriserTab(TabPage tab)
{
    // Always keep "Lagre" visible by using a bottom-docked bar + a scrollable content panel.
    tab.SuspendLayout();
    tab.Controls.Clear();
    tab.AutoScroll = false;

    var bottomBar = new FlowLayoutPanel
    {
        Dock = DockStyle.Bottom,
        Height = 58,
        Padding = new Padding(10, 10, 10, 10),
        FlowDirection = FlowDirection.RightToLeft,
        WrapContents = false
    };

    var content = new Panel
    {
        Dock = DockStyle.Fill,
        AutoScroll = true
    };

    tab.Controls.Add(content);
    tab.Controls.Add(bottomBar);

    _numStorSommer ??= new NumericUpDown();
    _numStorVinter ??= new NumericUpDown();
    _numLitenSommer ??= new NumericUpDown();
    _numLitenVinter ??= new NumericUpDown();

    var grpSesong = new GroupBox
    {
        Text = "Sesongpriser - bruk av vaskehall",
        Location = new Point(10, 10),
        Size = new Size(1000, 180)
    };

    int sxLabel = 15;
    int sxVal = 260;
    int sy = 30;

    grpSesong.Controls.Add(new Label { Text = "Stor - Sommer (kr pr. vask):", Location = new Point(sxLabel, sy), AutoSize = true });
    _numStorSommer = new NumericUpDown { Location = new Point(sxVal, sy - 3), Width = 100, DecimalPlaces = 2, Minimum = 0, Maximum = 100000, ThousandsSeparator = true };
    grpSesong.Controls.Add(_numStorSommer);
    sy += 30;

    grpSesong.Controls.Add(new Label { Text = "Stor - Vinter (kr pr. vask):", Location = new Point(sxLabel, sy), AutoSize = true });
    _numStorVinter = new NumericUpDown { Location = new Point(sxVal, sy - 3), Width = 100, DecimalPlaces = 2, Minimum = 0, Maximum = 100000, ThousandsSeparator = true };
    grpSesong.Controls.Add(_numStorVinter);
    sy += 40;

    grpSesong.Controls.Add(new Label { Text = "Liten - Sommer (kr pr. vask):", Location = new Point(sxLabel, sy), AutoSize = true });
    _numLitenSommer = new NumericUpDown { Location = new Point(sxVal, sy - 3), Width = 100, DecimalPlaces = 2, Minimum = 0, Maximum = 100000, ThousandsSeparator = true };
    grpSesong.Controls.Add(_numLitenSommer);
    sy += 30;

    grpSesong.Controls.Add(new Label { Text = "Liten - Vinter (kr pr. vask):", Location = new Point(sxLabel, sy), AutoSize = true });
    _numLitenVinter = new NumericUpDown { Location = new Point(sxVal, sy - 3), Width = 100, DecimalPlaces = 2, Minimum = 0, Maximum = 100000, ThousandsSeparator = true };
    grpSesong.Controls.Add(_numLitenVinter);

    content.Controls.Add(grpSesong);

    // --- Sesong datoer ---
    _dtSommerStart ??= new DateTimePicker();
    _dtVinterStart ??= new DateTimePicker();

    var grpDatoer = new GroupBox
    {
        Text = "Sesong-datoer (grenser)",
        Location = new Point(10, grpSesong.Bottom + 15),
        Size = new Size(1000, 140)
    };

    var lblInfo = new Label
    {
        Text = "Sommer gjelder fra og med Sommer-start, til (men ikke inkl.) Vinter-start.",
        Location = new Point(15, 28),
        AutoSize = true
    };
    grpDatoer.Controls.Add(lblInfo);

    grpDatoer.Controls.Add(new Label { Text = "Sommer start (dd.MM):", Location = new Point(15, 60), AutoSize = true });
    _dtSommerStart = new DateTimePicker
    {
        Location = new Point(220, 56),
        Width = 120,
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd.MM",
        ShowUpDown = true
    };
    grpDatoer.Controls.Add(_dtSommerStart);

    grpDatoer.Controls.Add(new Label { Text = "Vinter start (dd.MM):", Location = new Point(15, 95), AutoSize = true });
    _dtVinterStart = new DateTimePicker
    {
        Location = new Point(220, 91),
        Width = 120,
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd.MM",
        ShowUpDown = true
    };
    grpDatoer.Controls.Add(_dtVinterStart);

    content.Controls.Add(grpDatoer);

    // --- Recalculate season in DB ---
    _dtRecalcSeasonFrom ??= new DateTimePicker();
    _btnRecalcSeason ??= new Button();

    var grpRecalc = new GroupBox
    {
        Text = "Rekalkuler sesong i DB",
        Location = new Point(10, grpDatoer.Bottom + 15),
        Size = new Size(1000, 160)
    };

    var lblRecalc = new Label
    {
        Text = "Overskriver kolonnen 'season' i tabellen wash_events for registreringer fra valgt dato (lokalt Oslo), ved å bruke gjeldende sesonggrenser fra disse innstillingene.",
        Location = new Point(15, 28),
        AutoSize = true,
        MaximumSize = new Size(960, 0)
    };
    grpRecalc.Controls.Add(lblRecalc);

    grpRecalc.Controls.Add(new Label { Text = "Fra dato (yyyy-MM-dd):", Location = new Point(15, 75), AutoSize = true });

    _dtRecalcSeasonFrom = new DateTimePicker
    {
        Location = new Point(220, 71),
        Width = 140,
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd"
    };
    grpRecalc.Controls.Add(_dtRecalcSeasonFrom);

    _btnRecalcSeason = new Button
    {
        Text = "Rekalkuler",
        Location = new Point(380, 70),
        Width = 120,
        Height = 30
    };
    _btnRecalcSeason.Click += BtnRecalcSeason_Click;
    grpRecalc.Controls.Add(_btnRecalcSeason);

    content.Controls.Add(grpRecalc);

    var btnLagre = new Button
    {
        Text = "Lagre",
        Width = 120,
        Height = 36,
        Location = new Point(150, 200),
    };
    btnLagre.Click += BtnLagreInnstillinger_Click;
    bottomBar.Controls.Add(btnLagre);
    tab.ResumeLayout(true);
}


            private void Lastsesongpriser()
            {
                // sesongpriser
                var priser = _repo.LastSesongPriser();

                decimal storSommer = 0m, storVinter = 0m, litenSommer = 0m, litenVinter = 0m;

                if (priser.TryGetValue("Sommer_Stor", out var v)) storSommer = v;
                if (priser.TryGetValue("Vinter_Stor", out v)) storVinter = v;
                if (priser.TryGetValue("Sommer_Liten", out v)) litenSommer = v;
                if (priser.TryGetValue("Vinter_Liten", out v)) litenVinter = v;

                // fallback z bardzo starego formatu (jedna cena na sesong)
                if (storSommer == 0m && priser.TryGetValue("Sommer", out v)) storSommer = v;
                if (storVinter == 0m && priser.TryGetValue("Vinter", out v)) storVinter = v;
                if (litenSommer == 0m) litenSommer = storSommer;
                if (litenVinter == 0m) litenVinter = storVinter;

                _numStorSommer.Value = storSommer;
                _numStorVinter.Value = storVinter;
                _numLitenSommer.Value = litenSommer;
                _numLitenVinter.Value = litenVinter;

                // sesong datoer (bruk "år 2000" bare for å vise dd.MM i picker)
                try
                {
                    if (_dtSommerStart != null)
                        _dtSommerStart.Value = new DateTime(2000, AppConfig.SommerStartMonth, AppConfig.SommerStartDay);
                    if (_dtVinterStart != null)
                        _dtVinterStart.Value = new DateTime(2000, AppConfig.VinterStartMonth, AppConfig.VinterStartDay);
                    if (_dtRecalcSeasonFrom != null)
                        _dtRecalcSeasonFrom.Value = DateTime.Today.AddMonths(-1);
                }
                catch { }


            }



            private void BtnLagreInnstillinger_Click(object sender, EventArgs e)
            {
                try
                {
                    // sesong datoer
                    if (_dtSommerStart != null)
                    {
                        AppConfig.SommerStartMonth = _dtSommerStart.Value.Month;
                        AppConfig.SommerStartDay = _dtSommerStart.Value.Day;
                    }
                    if (_dtVinterStart != null)
                    {
                        AppConfig.VinterStartMonth = _dtVinterStart.Value.Month;
                        AppConfig.VinterStartDay = _dtVinterStart.Value.Day;
                    }

                    // all settings (camera + Dahua/ITS + doc folder)
                    var cam2 = new CameraPreviewSettings
                    {
                        Protocol = _cmbCam2Protocol != null && (_cmbCam2Protocol.SelectedIndex == 1) ? "axis_http_mjpeg" : "rtsp",
                        Enabled = _chkCam2Enabled != null && _chkCam2Enabled.Checked,
                        Host = _txtCam2Host?.Text?.Trim() ?? "",
                        Port = _numCam2Port != null ? (int)_numCam2Port.Value : 554,
                        Username = _txtCam2User?.Text?.Trim() ?? "",
                        Password = _txtCam2Pass?.Text ?? "",
                        Channel = _numCam2Channel != null ? (int)_numCam2Channel.Value : 0,
                        Path = _txtCam2Path?.Text?.Trim() ?? "/axis-media/media.amp",
                        RtspUrl = _txtCam2RtspOverride?.Text?.Trim() ?? "",
                        AutoRefreshOnFreeze = _chkCam2AutoRefresh == null || _chkCam2AutoRefresh.Checked
                    };

                    var cam3 = new CameraPreviewSettings
                    {
                        Protocol = _cmbCam3Protocol != null && (_cmbCam3Protocol.SelectedIndex == 1) ? "axis_http_mjpeg" : "rtsp",
                        Enabled = _chkCam3Enabled != null && _chkCam3Enabled.Checked,
                        Host = _txtCam3Host?.Text?.Trim() ?? "",
                        Port = _numCam3Port != null ? (int)_numCam3Port.Value : 554,
                        Username = _txtCam3User?.Text?.Trim() ?? "",
                        Password = _txtCam3Pass?.Text ?? "",
                        Channel = _numCam3Channel != null ? (int)_numCam3Channel.Value : 0,
                        Path = _txtCam3Path?.Text?.Trim() ?? "/axis-media/media.amp",
                        RtspUrl = _txtCam3RtspOverride?.Text?.Trim() ?? ""
                    };

                    AppConfig.UpdateAll(
                        cameraUrl: _txtCamRtsp.Text.Trim(),
                        apiToken: _txtCamToken.Text.Trim(),
                        cam2: cam2,
                        cam3: cam3,
                        dahuaHost: _txtDahuaHost.Text.Trim(),
                        dahuaPort: (int)_numDahuaPort.Value,
                        dahuaUsername: _txtDahuaUser.Text.Trim(),
                        dahuaPassword: _txtDahuaPass.Text,
                        itsApiPort: (int)_numItsApiPort.Value,
                        itsApiPath: _txtItsApiPath.Text.Trim(),
                        itsApiHostIp: _txtItsApiHostIp.Text.Trim(),
                        docFolder: _txtDocFolder.Text.Trim(),
                        autoRegisterOnPlate: _chkAutoRegister.Checked,
                        displaySeconds: (int)_numDisplaySeconds.Value);

                    // database + worker UI
                    AppConfig.DbEnabled = _chkDbEnabled.Checked;
                    AppConfig.DbHost = _txtDbHost.Text.Trim();
                    if (_txtDbWorkerHost != null) AppConfig.DbWorkerHost = _txtDbWorkerHost.Text.Trim();
                    AppConfig.DbPort = (int)_numDbPort.Value;
                    AppConfig.DbDatabase = _txtDbName.Text.Trim();
                    AppConfig.DbAdminUser = _txtDbAdminUser.Text.Trim();
                    AppConfig.DbAdminPassword = _txtDbAdminPass.Text;
                    AppConfig.DbWorkerUser = _txtDbWorkerUser.Text.Trim();
                    AppConfig.DbWorkerPassword = _txtDbWorkerPass.Text;
                    AppConfig.WorkerRefreshSeconds = (int)_numWorkerRefresh.Value;
                    AppConfig.WorkerShowOnlyUnconfirmed = _chkWorkerOnlyUnconfirmed.Checked;

                    // UI passord
                    if (_txtUiAdminPassword != null) AppConfig.UiAdminPassword = _txtUiAdminPassword.Text;
                    if (_txtUiWorkerPassword != null) AppConfig.UiWorkerPassword = _txtUiWorkerPassword.Text;
                    AppConfig.Save();
                    try { AppConfig.LoadFromFile(); } catch { }

                    // layout settings
                    try
                    {
                        ApplyLayoutPreview(save: true);
                        HovedForm.Current?.ReloadUiLayout();
                    }
                    catch { }

                    // sesongpriser
                    decimal storSommer = _numStorSommer.Value;
                    decimal storVinter = _numStorVinter.Value;
                    decimal litenSommer = _numLitenSommer.Value;
                    decimal litenVinter = _numLitenVinter.Value;

                    string? warning = null;
                    try
                    {
                        _repo.LagreSesongPriserDetaljert(storSommer, storVinter, litenSommer, litenVinter);
                    }
                    catch (Exception ex)
                    {
                        warning = "Ustawienia zostały zapisane, ale nie udało się zapisać SesongPriser.csv: " + ex.Message;
                    }

                    if (string.IsNullOrWhiteSpace(warning))
                    {
                        MessageBox.Show(this, "Innstillinger lagret.", "Info",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show(this,
                            "Innstillinger lagret.Advarsel:" + warning,
                            "Advarsel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Feil ved lagring av innstillinger: " + ex.Message,
                        "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            private async void BtnRecalcSeason_Click(object sender, EventArgs e)
            {
                try
                {
                    if (!AppConfig.DbEnabled ||
                        string.IsNullOrWhiteSpace(AppConfig.DbHost) ||
                        string.IsNullOrWhiteSpace(AppConfig.DbAdminUser) ||
                        string.IsNullOrWhiteSpace(AppConfig.DbName))
                    {
                        MessageBox.Show(this,
                            "Database er ikke konfigurert (Innstillinger → Database).",
                            "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    var fromDate = _dtRecalcSeasonFrom != null ? _dtRecalcSeasonFrom.Value.Date : DateTime.Today;

                    var confirm = MessageBox.Show(this,
                        $"Rekalkulere sesong i DB fra {fromDate:yyyy-MM-dd}?\nDette vil overskrive eksisterende verdier i kolonnen 'season'.",
                        "Bekreft", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                    if (confirm != DialogResult.Yes)
                        return;

                    if (_btnRecalcSeason != null) _btnRecalcSeason.Enabled = false;
                    Cursor = Cursors.WaitCursor;

                    var writer = new PostgresWriter(AppConfig.BuildAdminConnectionString());
                    var rows = await writer.RecalculateSeasonAsync(
                        fromDate,
                        AppConfig.SommerStartMonth, AppConfig.SommerStartDay,
                        AppConfig.VinterStartMonth, AppConfig.VinterStartDay);

                    MessageBox.Show(this,
                        $"Ferdig. Oppdaterte {rows} rader.",
                        "OK", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Feil: " + ex.Message, "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    try { Cursor = Cursors.Default; } catch { }
                    if (_btnRecalcSeason != null) _btnRecalcSeason.Enabled = true;
                }
            }

        }


        // ---------------------------------------------
        // FAKTURA-ARKIV – PODGLĄD + WYSZUKIWARKA + EDYCJA
        // ---------------------------------------------
        public class FakturaArkivForm : Form
        {
            private readonly ExcelRepository _repo;

            private List<FakturaArkivPost> _alle;
            private BindingList<FakturaArkivPost> _binding;

            private DataGridView _grid;
            private TextBox _txtSok;
            private ComboBox _cmbKolonne;
            private Button _btnLagre;
            private Button _btnLukk;
            private Button _btnDetaljer;

            public FakturaArkivForm(ExcelRepository repo)
            {
                _repo = repo;
                Text = "Fakturaarkiv";
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(1300, 900);
                Font = new Font(FontFamily.GenericSansSerif, 11f);

                ByggUi();
                LastData();
                Filtrer();
            }




            private void ByggUi()
            {
                var lblSok = new Label
                {
                    Text = "Søk:",
                    AutoSize = true,
                    Location = new Point(20, 20)
                };

                _txtSok = new TextBox
                {
                    Location = new Point(60, 15),
                    Width = 250
                };
                _txtSok.TextChanged += (s, e) => Filtrer();

                _cmbKolonne = new ComboBox
                {
                    Location = new Point(330, 15),
                    Width = 150,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };
                _cmbKolonne.Items.Add("Alle");
                _cmbKolonne.Items.Add("Fakturanr");
                _cmbKolonne.Items.Add("Kunde");
                _cmbKolonne.Items.Add("Selskap");
                _cmbKolonne.SelectedIndex = 1;   // domyślnie: szukaj po numerze faktury
                _cmbKolonne.SelectedIndexChanged += (s, e) => Filtrer();

                _grid = new DataGridView
                {
                    Location = new Point(20, 50),
                    Size = new Size(1150, 560),
                    AutoGenerateColumns = false,
                    AllowUserToAddRows = false,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
                };

                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Fakturanr",
                    DataPropertyName = "FakturaNr",
                    Width = 100
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Dato",
                    DataPropertyName = "Dato",
                    Width = 90,
                    DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd" }
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Selskap",
                    DataPropertyName = "Selskap"
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Kunde",
                    DataPropertyName = "KundeNavn"
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Periode fra",
                    DataPropertyName = "PeriodeFra",
                    DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd" }
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Periode til",
                    DataPropertyName = "PeriodeTil",
                    DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd" }
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Antall vask",
                    DataPropertyName = "AntallVask",
                    Width = 80
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Netto",
                    DataPropertyName = "Netto",
                    DefaultCellStyle = new DataGridViewCellStyle { Format = "0.00" }
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Mva",
                    DataPropertyName = "Mva",
                    DefaultCellStyle = new DataGridViewCellStyle { Format = "0.00" }
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "Total",
                    DataPropertyName = "Total",
                    DefaultCellStyle = new DataGridViewCellStyle { Format = "0.00" }
                });
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = "PDF-fil",
                    DataPropertyName = "PdfFil"
                });
                _grid.Columns.Add(new DataGridViewCheckBoxColumn
                {
                    HeaderText = "Annullert",
                    DataPropertyName = "Annullert",
                    Width = 70
                });

                // podwójne kliknięcie -> szczegóły faktury
                _grid.CellDoubleClick += Grid_CellDoubleClick;

                _btnDetaljer = new Button
                {
                    Text = "Detaljer...",
                    Location = new Point(20, 630),
                    Width = 100,
                    Height = 35
                };
                _btnDetaljer.Click += BtnDetaljer_Click;

                _btnLagre = new Button
                {
                    Text = "Lagre",
                    Location = new Point(130, 630),
                    Width = 100,
                    Height = 35
                };
                _btnLagre.Click += BtnLagre_Click;

                _btnLukk = new Button
                {
                    Text = "Lukk",
                    Location = new Point(240, 630),
                    Width = 100,
                    Height = 35
                };
                _btnLukk.Click += (s, e) => Close();

                Controls.Add(lblSok);
                Controls.Add(_txtSok);
                Controls.Add(_cmbKolonne);
                Controls.Add(_grid);
                Controls.Add(_btnDetaljer);
                Controls.Add(_btnLagre);
                Controls.Add(_btnLukk);
            }

            private void LastData()
            {
                _alle = _repo.LastFakturaArkiv();
            }

            private void Filtrer()
            {
                if (_alle == null) return;

                string tekst = _txtSok.Text.Trim().ToLowerInvariant();
                string kol = _cmbKolonne.SelectedItem?.ToString() ?? "Alle";

                IEnumerable<FakturaArkivPost> query = _alle;

                if (!string.IsNullOrWhiteSpace(tekst))
                {
                    switch (kol)
                    {
                        case "Fakturanr":
                            query = query.Where(p => (p.FakturaNr ?? "").ToLowerInvariant().Contains(tekst));
                            break;
                        case "Kunde":
                            query = query.Where(p => (p.KundeNavn ?? "").ToLowerInvariant().Contains(tekst));
                            break;
                        case "Selskap":
                            query = query.Where(p => p.Selskap.ToString().ToLowerInvariant().Contains(tekst));
                            break;
                        default:    // Alle
                            query = query.Where(p =>
                                (p.FakturaNr ?? "").ToLowerInvariant().Contains(tekst) ||
                                (p.KundeNavn ?? "").ToLowerInvariant().Contains(tekst) ||
                                p.Selskap.ToString().ToLowerInvariant().Contains(tekst));
                            break;
                    }
                }

                _binding = new BindingList<FakturaArkivPost>(query.ToList());
                _grid.DataSource = _binding;
            }

            private void BtnLagre_Click(object sender, EventArgs e)
            {
                try
                {
                    // obiekty w _binding są tymi samymi referencjami co w _alle,
                    // więc wystarczy zapisać całą listę _alle.
                    _repo.LagreFakturaArkiv(_alle);
                    MessageBox.Show(this, "Fakturaarkiv lagret.", "Info",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Feil ved lagring: " + ex.Message,
                        "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            private void BtnDetaljer_Click(object sender, EventArgs e)
            {
                ÅpneDetaljerForValgtRad();
            }

            private void Grid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0) return;
                ÅpneDetaljerForValgtRad();
            }

            private void ÅpneDetaljerForValgtRad()
            {
                if (_grid.CurrentRow == null) return;
                if (!(_grid.CurrentRow.DataBoundItem is FakturaArkivPost post)) return;

                using (var dlg = new FakturaDetaljForm(post))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        // dane w obiekcie 'post' już zmienione – odśwież widok
                        _grid.Refresh();
                    }
                }
            }
        }
        // ---------------------------------------------
        // Szczegóły pojedynczej faktury – edycja
        // ---------------------------------------------
        public class FakturaDetaljForm : Form
        {
            private readonly FakturaArkivPost _post;

            private TextBox _txtFakturaNr;
            private DateTimePicker _dtpDato;
            private ComboBox _cmbSelskap;
            private TextBox _txtKunde;
            private DateTimePicker _dtpFra;
            private DateTimePicker _dtpTil;
            private NumericUpDown _numAntall;
            private NumericUpDown _numNetto;
            private NumericUpDown _numMva;
            private NumericUpDown _numTotal;
            private TextBox _txtPdf;
            private CheckBox _chkAnnullert;

            private Button _btnOk;
            private Button _btnAvbryt;

            public FakturaDetaljForm(FakturaArkivPost post)
            {
                _post = post ?? throw new ArgumentNullException(nameof(post));

                Text = "Fakturadetaljer";
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(1300, 900);
                Font = new Font(FontFamily.GenericSansSerif, 12f);
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;

                ByggUi();
                FyllFraPost();
            }




            private void ByggUi()
            {
                int xLabel = 20;
                int xVal = 160;
                int y = 20;
                int dy = 28;
                int wVal = 320;

                // Fakturanr (tylko do odczytu)
                Controls.Add(new Label { Text = "Fakturanr:", Location = new Point(xLabel, y), AutoSize = true });
                _txtFakturaNr = new TextBox
                {
                    Location = new Point(xVal, y - 3),
                    Width = 150,
                    ReadOnly = true
                };
                Controls.Add(_txtFakturaNr);
                y += dy;

                // Dato
                Controls.Add(new Label { Text = "Dato:", Location = new Point(xLabel, y), AutoSize = true });
                _dtpDato = new DateTimePicker
                {
                    Location = new Point(xVal, y - 3),
                    Width = 150,
                    Format = DateTimePickerFormat.Short
                };
                Controls.Add(_dtpDato);
                y += dy;

                // Selskap
                Controls.Add(new Label { Text = "Selskap:", Location = new Point(xLabel, y), AutoSize = true });
                _cmbSelskap = new ComboBox
                {
                    Location = new Point(xVal, y - 3),
                    Width = 200,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    DataSource = Enum.GetValues(typeof(SelskapType))
                };
                Controls.Add(_cmbSelskap);
                y += dy;

                // Kunde
                Controls.Add(new Label { Text = "Kunde:", Location = new Point(xLabel, y), AutoSize = true });
                _txtKunde = new TextBox
                {
                    Location = new Point(xVal, y - 3),
                    Width = wVal
                };
                Controls.Add(_txtKunde);
                y += dy;

                // Periode fra
                Controls.Add(new Label { Text = "Periode fra:", Location = new Point(xLabel, y), AutoSize = true });
                _dtpFra = new DateTimePicker
                {
                    Location = new Point(xVal, y - 3),
                    Width = 150,
                    Format = DateTimePickerFormat.Short
                };
                Controls.Add(_dtpFra);
                y += dy;

                // Periode til
                Controls.Add(new Label { Text = "Periode til:", Location = new Point(xLabel, y), AutoSize = true });
                _dtpTil = new DateTimePicker
                {
                    Location = new Point(xVal, y - 3),
                    Width = 150,
                    Format = DateTimePickerFormat.Short
                };
                Controls.Add(_dtpTil);
                y += dy;

                // Antall vask
                Controls.Add(new Label { Text = "Antall vask:", Location = new Point(xLabel, y), AutoSize = true });
                _numAntall = new NumericUpDown
                {
                    Location = new Point(xVal, y - 3),
                    Width = 100,
                    Minimum = 0,
                    Maximum = 100000
                };
                Controls.Add(_numAntall);
                y += dy;

                // Netto
                Controls.Add(new Label { Text = "Netto (kr):", Location = new Point(xLabel, y), AutoSize = true });
                _numNetto = new NumericUpDown
                {
                    Location = new Point(xVal, y - 3),
                    Width = 120,
                    DecimalPlaces = 2,
                    Minimum = -100000000,
                    Maximum = 100000000
                };
                Controls.Add(_numNetto);
                y += dy;

                // MVA
                Controls.Add(new Label { Text = "MVA (kr):", Location = new Point(xLabel, y), AutoSize = true });
                _numMva = new NumericUpDown
                {
                    Location = new Point(xVal, y - 3),
                    Width = 120,
                    DecimalPlaces = 2,
                    Minimum = -100000000,
                    Maximum = 100000000
                };
                Controls.Add(_numMva);
                y += dy;

                // Total
                Controls.Add(new Label { Text = "Total (kr):", Location = new Point(xLabel, y), AutoSize = true });
                _numTotal = new NumericUpDown
                {
                    Location = new Point(xVal, y - 3),
                    Width = 120,
                    DecimalPlaces = 2,
                    Minimum = -100000000,
                    Maximum = 100000000
                };
                Controls.Add(_numTotal);
                y += dy;

                // PDF
                Controls.Add(new Label { Text = "PDF-fil:", Location = new Point(xLabel, y), AutoSize = true });
                _txtPdf = new TextBox
                {
                    Location = new Point(xVal, y - 3),
                    Width = wVal
                };
                Controls.Add(_txtPdf);
                y += dy;

                // Annullert
                _chkAnnullert = new CheckBox
                {
                    Text = "Annullert",
                    AutoSize = true,
                    Location = new Point(xVal, y)
                };
                Controls.Add(_chkAnnullert);
                y += dy + 10;

                _btnOk = new Button
                {
                    Text = "Lagre",
                    Location = new Point(160, 360),
                    Width = 100,
                    Height = 35
                };
                _btnOk.Click += BtnOk_Click;

                _btnAvbryt = new Button
                {
                    Text = "Avbryt",
                    Location = new Point(270, 360),
                    Width = 100,
                    Height = 35
                };
                _btnAvbryt.Click += (s, e) =>
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                };

                Controls.Add(_btnOk);
                Controls.Add(_btnAvbryt);
            }

            private void FyllFraPost()
            {
                _txtFakturaNr.Text = _post.FakturaNr ?? "";
                _dtpDato.Value = _post.Dato == DateTime.MinValue ? DateTime.Today : _post.Dato;
                _cmbSelskap.SelectedItem = _post.Selskap;
                _txtKunde.Text = _post.KundeNavn ?? "";
                _dtpFra.Value = _post.PeriodeFra == DateTime.MinValue ? DateTime.Today : _post.PeriodeFra;
                _dtpTil.Value = _post.PeriodeTil == DateTime.MinValue ? DateTime.Today : _post.PeriodeTil;
                _numAntall.Value = Math.Min(_numAntall.Maximum, Math.Max(_numAntall.Minimum, _post.AntallVask));
                _numNetto.Value = Math.Min(_numNetto.Maximum, Math.Max(_numNetto.Minimum, _post.Netto));
                _numMva.Value = Math.Min(_numMva.Maximum, Math.Max(_numMva.Minimum, _post.Mva));
                _numTotal.Value = Math.Min(_numTotal.Maximum, Math.Max(_numTotal.Minimum, _post.Total));
                _txtPdf.Text = _post.PdfFil ?? "";
                _chkAnnullert.Checked = _post.Annullert;
            }

            private void BtnOk_Click(object sender, EventArgs e)
            {
                // zapis zmian do obiektu
                _post.Dato = _dtpDato.Value.Date;
                _post.Selskap = (SelskapType)_cmbSelskap.SelectedItem;
                _post.KundeNavn = _txtKunde.Text.Trim();
                _post.PeriodeFra = _dtpFra.Value.Date;
                _post.PeriodeTil = _dtpTil.Value.Date;
                _post.AntallVask = (int)_numAntall.Value;
                _post.Netto = _numNetto.Value;
                _post.Mva = _numMva.Value;
                _post.Total = _numTotal.Value;
                _post.PdfFil = _txtPdf.Text.Trim();
                _post.Annullert = _chkAnnullert.Checked;

                DialogResult = DialogResult.OK;
                Close();
            }
        }
        // ---------------------------------------------
        // PUNKT WEJŚCIA APLIKACJI
        // ---------------------------------------------


        internal static class PasswordPrompt
        {
            public static bool Require(IWin32Window owner, string title, string expectedPassword, string message = "Skriv passord:")
            {
                {
                    if (string.IsNullOrEmpty(expectedPassword))
                        return true;

                    using var dlg = new Form
                    {
                        Text = title,
                        FormBorderStyle = FormBorderStyle.FixedDialog,
                        StartPosition = FormStartPosition.CenterParent,
                        MinimizeBox = false,
                        MaximizeBox = false,
                        ShowInTaskbar = false,
                        ClientSize = new Size(520, 180)
                    };

                    var root = new TableLayoutPanel
                    {
                        Dock = DockStyle.Fill,
                        Padding = new Padding(16),
                        ColumnCount = 1,
                        RowCount = 3
                    };
                    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // label
                    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // input
                    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // buttons (bottom)

                    var lbl = new Label
                    {
                        Text = message,
                        AutoSize = true,
                        Dock = DockStyle.Top
                    };

                    // Centered input (fixed width)
                    var inputRow = new TableLayoutPanel
                    {
                        Dock = DockStyle.Top,
                        ColumnCount = 3,
                        RowCount = 1,
                        AutoSize = true,
                        Margin = new Padding(0, 10, 0, 0)
                    };
                    inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                    inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
                    inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

                    var txt = new TextBox
                    {
                        UseSystemPasswordChar = true,
                        Dock = DockStyle.Fill,
                        Font = new Font("Segoe UI", 11F, FontStyle.Regular)
                    };
                    inputRow.Controls.Add(txt, 1, 0);

                    // Centered buttons (same size)
                    var btnRow = new TableLayoutPanel
                    {
                        Dock = DockStyle.Bottom,
                        ColumnCount = 5,
                        RowCount = 1,
                        Height = 40,
                        Margin = new Padding(0, 18, 0, 0)
                    };
                    btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                    btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
                    btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12));
                    btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
                    btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

                    var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 120, Height = 32, Anchor = AnchorStyles.None };
                    var btnCancel = new Button { Text = "Avbryt", DialogResult = DialogResult.Cancel, Width = 120, Height = 32, Anchor = AnchorStyles.None };

                    btnRow.Controls.Add(btnOk, 1, 0);
                    btnRow.Controls.Add(btnCancel, 3, 0);

                    dlg.AcceptButton = btnOk;
                    dlg.CancelButton = btnCancel;

                    root.Controls.Add(lbl, 0, 0);
                    root.Controls.Add(inputRow, 0, 1);
                    root.Controls.Add(btnRow, 0, 2);

                    dlg.Controls.Add(root);

                    txt.Focus();

                    if (dlg.ShowDialog(owner) != DialogResult.OK)
                        return false;

                    if (!string.Equals(txt.Text, expectedPassword, StringComparison.Ordinal))
                    {
                        MessageBox.Show(owner, "Feil passord.", "Tilgang nektet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                    return true;
                }
            }
        }
    }
}