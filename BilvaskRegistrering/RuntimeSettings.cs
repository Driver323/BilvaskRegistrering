using System;

namespace BilvaskRegistrering;

public sealed class RuntimeSettings
{
    public AnprSettings Anpr { get; set; } = new();
    public DahuaSettings Dahua { get; set; } = new();
    public ItsApiSettings ItsApi { get; set; } = new();
    public DokumentSettings Dokument { get; set; } = new();
    // Extra preview-only cameras (Kamera 2 / Kamera 3)
    public PreviewCamerasSettings PreviewCameras { get; set; } = new();
    // Sesongpriser (brukes av UI / faktura)
    public SesongpriserSettings Sesongpriser { get; set; } = new();

    // Sesong-datoer (Sommer/Vinter grense). Brukes av både Admin/Worker.
    // Standard: Sommer fra 01.04, Vinter fra 01.10
    public SesongDatoerSettings SesongDatoer { get; set; } = new();
    public DatabaseSettings Database { get; set; } = new();
    public WorkerUiSettings WorkerUi { get; set; } = new();
    public ConnectionStringsSettings ConnectionStrings { get; set; } = new();
    public AuthSettings Auth { get; set; } = new();
    public InstallSettings Install { get; set; } = new();

    /// <summary>
    /// Creates a safe default settings object.
    /// LegacyUI relies on this when settings.runtime.json is missing/corrupt
    /// or when migrating old INI settings.
    /// </summary>
    public static RuntimeSettings CreateDefault()
    {
        return new RuntimeSettings
        {
            Anpr = new AnprSettings
            {
                RtspUrl = "",
                ApiToken = ""
            },
            Dahua = new DahuaSettings
            {
                Host = "0.0.0.0",
                Port = 37777,
                User = "",
                Password = ""
            },
            ItsApi = new ItsApiSettings
            {
                Host = "0.0.0.0",
                Port = 7070,
                Path = ""
            },
            Dokument = new DokumentSettings
            {
                Folder = "",
                AutoRegisterOnPlate = false,
                DisplaySeconds = 20
            },

            PreviewCameras = new PreviewCamerasSettings
            {
                Camera2 = new CameraPreviewSettings
                {
                    Enabled = true,
                    Host = "",
                    Port = 554,
                    Username = "",
                    Password = "",
                    Channel = 0,
                    Path = "",
                    RtspUrl = "",
                    AutoRefreshOnFreeze = true
                },
                Camera3 = new CameraPreviewSettings
                {
                    Enabled = true,
                    Host = "",
                    Port = 554,
                    Username = "",
                    Password = "",
                    Channel = 0,
                    Path = "",
                    RtspUrl = "",
                    AutoRefreshOnFreeze = false
                }
            },
            Sesongpriser = new SesongpriserSettings
            {
                StorSommer = 0,
                StorVinter = 0,
                LitenSommer = 0,
                LitenVinter = 0
            },

            SesongDatoer = new SesongDatoerSettings
            {
                SommerStartMonth = 4,
                SommerStartDay = 1,
                VinterStartMonth = 10,
                VinterStartDay = 1
            },
            Database = new DatabaseSettings
            {
                Enabled = false,
                Host = "",
                WorkerHost = "",
                Port = 5432,
                Database = "",
                AdminUser = "",
                AdminPassword = "",
                WorkerUser = "",
                WorkerPassword = "",
                SslMode = "",
                TrustServerCertificate = true
            },
            WorkerUi = new WorkerUiSettings
            {
                RefreshSeconds = 5,
                ShowOnlyUnconfirmed = true
            },
            ConnectionStrings = new ConnectionStringsSettings { Admin = "", Worker = "" },
            Auth = new AuthSettings { AdminPassword = "admin", WorkerPassword = "worker" },
            Install = new InstallSettings { ActivationCode = "", ActivatedAt = "" }

        };
    }
}

public sealed class PreviewCamerasSettings
{
    public CameraPreviewSettings Camera2 { get; set; } = new();
    public CameraPreviewSettings Camera3 { get; set; } = new();
}

public sealed class CameraPreviewSettings
{
    /// <summary>
    /// Stream protocol for preview cameras.
    /// Supported values:
    /// - "rtsp" (default)
    /// - "axis_http_mjpeg" (Axis MJPEG over HTTP)
    /// </summary>
    public string Protocol { get; set; } = "";

    // If false, the app will not try to connect.
    public bool Enabled { get; set; } = false;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 554;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    // For Axis encoders / multi-camera devices. 0 means "not used".
    public int Channel { get; set; } = 0;
    // Default Axis path. User can override.
    public string Path { get; set; } = "";
    // Optional full RTSP override. If set, Host/Port/Channel/Path are ignored.
    public string RtspUrl { get; set; } = "";

    /// <summary>
    /// When true, Kamera 2 may auto-restart the preview if the picture freezes.
    /// The toggle is exposed only for Kamera 2 in the UI, but keeping it on the
    /// per-camera settings object keeps settings.runtime.json backwards compatible.
    /// </summary>
    public bool AutoRefreshOnFreeze { get; set; } = true;
}

public sealed class AnprSettings
{
    // New canonical property names (used by LegacyUI/AppConfig)
    public string RtspUrl { get; set; } = "";
    public string ApiToken { get; set; } = "";

    // Backwards-compatible aliases (older drafts)
    public string CameraRtspUrl
    {
        get => RtspUrl;
        set => RtspUrl = value ?? "";
    }

    public string PlateRecognizerApiToken
    {
        get => ApiToken;
        set => ApiToken = value ?? "";
    }
}

public sealed class DahuaSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 37777;
    // New canonical property names (used by LegacyUI/AppConfig)
    public string User { get; set; } = "";
    public string Password { get; set; } = "";

    // Backwards-compatible alias
    public string Username
    {
        get => User;
        set => User = value ?? "";
    }
}

public sealed class ItsApiSettings
{
    // New canonical property names (used by LegacyUI/AppConfig)
    public string Host { get; set; } = "";
    public int Port { get; set; } = 7070;
    public string Path { get; set; } = "";

    // Backwards-compatible alias
    public string HostIp
    {
        get => Host;
        set => Host = value ?? "";
    }
}

public sealed class DokumentSettings
{
    public string Folder { get; set; } = "";
    public bool AutoRegisterOnPlate { get; set; } = false;
    public int DisplaySeconds { get; set; } = 10;
}

public sealed class DatabaseSettings
{
    public bool Enabled { get; set; } = false;
    /// <summary>
    /// Admin app DB host (management / office PC).
    /// Kept as <c>Host</c> for backwards compatibility.
    /// </summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// Worker app DB host (wash team / worker PC).
    /// If empty, the worker will fall back to <see cref="Host" />.
    /// </summary>
    public string WorkerHost { get; set; } = "";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "";

    // roles
    public string AdminUser { get; set; } = "";
    public string AdminPassword { get; set; } = "";

    public string WorkerUser { get; set; } = "";
    public string WorkerPassword { get; set; } = "";

    // fixed defaults for Azure (we may override to Disable for local/Tailscale hosts)
    public string SslMode { get; set; } = "Prefer";
    public bool TrustServerCertificate { get; set; } = true;
}

public sealed class WorkerUiSettings
{
    public int RefreshSeconds { get; set; } = 5;
    public bool ShowOnlyUnconfirmed { get; set; } = true;
}



public sealed class ConnectionStringsSettings
{
    public string Admin { get; set; } = "";
    public string Worker { get; set; } = "";
}

public sealed class AuthSettings
{
    public string AdminPassword { get; set; } = "admin";
    public string WorkerPassword { get; set; } = "worker";
}

public sealed class SesongpriserSettings
{
    // Stored as doubles in JSON for simplicity; UI casts to decimal where needed.
    public double StorSommer { get; set; } = 0;
    public double StorVinter { get; set; } = 0;
    public double LitenSommer { get; set; } = 0;
    public double LitenVinter { get; set; } = 0;
}

public sealed class SesongDatoerSettings
{
    // Stored as integers in JSON.
    // If values are invalid, the app will clamp to a valid day in the selected month.
    public int SommerStartMonth { get; set; } = 4;
    public int SommerStartDay { get; set; } = 1;
    public int VinterStartMonth { get; set; } = 10;
    public int VinterStartDay { get; set; } = 1;
}


public sealed class InstallSettings
{
    public string ActivationCode { get; set; } = "";
    public string ActivatedAt { get; set; } = "";
}
