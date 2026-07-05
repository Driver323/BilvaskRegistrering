using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BilvaskRegistrering.Worker;

internal sealed class AppConfig
{
    public string WorkerConnectionString { get; }
    public int RefreshSeconds { get; }
    public bool ShowOnlyUnconfirmed { get; }
    public bool ShowOnlyUnntak { get; }
    public IReadOnlyDictionary<string, string> TypeVaskColorMap { get; }
    public IReadOnlyDictionary<string, string> AnsattColorMap { get; }

    // UI passwords stored in settings.runtime.json (set by Admin app)
    public string UiAdminPassword { get; }
    public string UiWorkerPassword { get; }

    /// <summary>
    /// Folder containing CSV files used by the solution (e.g. EgenFlate.csv, Ansatte.csv).
    /// </summary>
    public string DokumentFolder { get; }

    // ITS listener (plate ingest via HTTP POST, no preview)
    public bool ItsListenerEnabled { get; }
    public int ItsListenerPort { get; }
    public string ItsListenerPath { get; }

    // Offline queue (local CSV)
    public string OfflineQueuePath { get; }
    public int PlateDebounceSeconds { get; }

    // Sesong-datoer (grenser) – standard: Sommer fra 01.04, Vinter fra 01.10
    public int SommerStartMonth { get; }
    public int SommerStartDay { get; }
    public int VinterStartMonth { get; }
    public int VinterStartDay { get; }

    public string ItsListenerPrefix
    {
        get
        {
            var path = ItsListenerPath ?? "/NotificationInfo/TollgateInfo";
            if (!path.StartsWith("/")) path = "/" + path;
            return $"http://+:{ItsListenerPort}{path.TrimEnd('/')}/";
        }
    }

    private AppConfig(
        string workerConn,
        int refreshSeconds,
        bool showOnlyUnconfirmed,
        bool showOnlyUnntak,
        bool itsEnabled,
        int itsPort,
        string itsPath,
        string offlineQueuePath,
        int plateDebounceSeconds,
        string uiAdminPassword,
        string uiWorkerPassword,
        int sommerStartMonth,
        int sommerStartDay,
        int vinterStartMonth,
        int vinterStartDay,
        IReadOnlyDictionary<string, string>? typeVaskColorMap,
        IReadOnlyDictionary<string, string>? ansattColorMap)
    {
        WorkerConnectionString = workerConn;
        RefreshSeconds = refreshSeconds <= 0 ? 5 : refreshSeconds;
        ShowOnlyUnconfirmed = showOnlyUnconfirmed;
        ShowOnlyUnntak = showOnlyUnntak;

        UiAdminPassword = string.IsNullOrWhiteSpace(uiAdminPassword) ? "admin" : uiAdminPassword;
        UiWorkerPassword = string.IsNullOrWhiteSpace(uiWorkerPassword) ? "worker" : uiWorkerPassword;
        TypeVaskColorMap = typeVaskColorMap is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(typeVaskColorMap, StringComparer.OrdinalIgnoreCase);
        AnsattColorMap = ansattColorMap is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(ansattColorMap, StringComparer.OrdinalIgnoreCase);

        ItsListenerEnabled = itsEnabled;
        ItsListenerPort = itsPort <= 0 ? 7070 : itsPort;
        ItsListenerPath = string.IsNullOrWhiteSpace(itsPath) ? "/NotificationInfo/TollgateInfo" : itsPath;

        // CSV/documents folder: prefer runtime DokumentFolder, else use Documents\BilvaskRegistrering
        DokumentFolder = LoadDokumentFolder();

        OfflineQueuePath = offlineQueuePath;
        PlateDebounceSeconds = plateDebounceSeconds <= 0 ? 3 : plateDebounceSeconds;

        SommerStartMonth = ClampMonth(sommerStartMonth, 4);
        SommerStartDay = ClampDay(SommerStartMonth, sommerStartDay, 1);
        VinterStartMonth = ClampMonth(vinterStartMonth, 10);
        VinterStartDay = ClampDay(VinterStartMonth, vinterStartDay, 1);
    }

    public string DetermineSeason(DateTime localDate)
    {
        var y = localDate.Year;
        var sommerStart = new DateTime(y, SommerStartMonth, ClampDay(SommerStartMonth, SommerStartDay, 1));
        var vinterStart = new DateTime(y, VinterStartMonth, ClampDay(VinterStartMonth, VinterStartDay, 1));

        if (sommerStart <= vinterStart)
            return (localDate >= sommerStart && localDate < vinterStart) ? "Sommer" : "Vinter";

        return (localDate >= sommerStart || localDate < vinterStart) ? "Sommer" : "Vinter";
    }

    private static string LoadDokumentFolder()
    {
        var runtime = TryLoadRuntime();
        return ResolveDokumentFolder(runtime?.DokumentFolder);
    }

    private static string ResolveDokumentFolder(string? configuredFolder)
    {
        var defaultFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "BilvaskRegistrering");

        var folder = string.IsNullOrWhiteSpace(configuredFolder)
            ? defaultFolder
            : configuredFolder;

        try
        {
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch
        {
            Directory.CreateDirectory(defaultFolder);
            return defaultFolder;
        }
    }

    public static AppConfig Load()
    {
        // 0) Try runtime settings saved by the Admin app (Documents\BilvaskRegistrering\settings.runtime.json)
        var runtime = TryLoadRuntime();

        // appsettings.json is copied to output dir by csproj
        var basePath = AppContext.BaseDirectory;
        var config = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables(prefix: "BILVASK_")
            .Build();

        // Connection string precedence:
        // 1) runtime settings (saved by Admin) ONLY if non-empty
        // 2) appsettings.json ConnectionStrings section
        // 3) backwards-compatible keys (older builds)
        var workerConnFromRuntime = (runtime != null && !string.IsNullOrWhiteSpace(runtime.WorkerConn))
            ? runtime.WorkerConn
            : null;

        var workerConnFromConfig =
            (config.GetConnectionString("worker") ??
             config.GetConnectionString("Worker") ??
             // legacy format used in the Admin app
             config.GetValue<string>("Postgres:Worker") ??
             config.GetValue<string>("Postgres:worker") ??
             "");

        var workerConn = workerConnFromRuntime ?? workerConnFromConfig;

        if (string.IsNullOrWhiteSpace(workerConn))
            workerConn = runtime?.WorkerConnBuiltFromDb ?? string.Empty;

        var refreshSeconds = runtime?.WorkerRefreshSeconds ?? config.GetValue("Worker:RefreshSeconds", 5);
        var showOnly = runtime?.WorkerShowOnlyUnconfirmed ?? config.GetValue("Worker:ShowOnlyUnconfirmed", true);
        var showOnlyUnntak = runtime?.WorkerShowOnlyUnntak ?? config.GetValue("Worker:ShowOnlyUnntak", true);

        // ITS listener settings
        var itsEnabled = runtime?.ItsEnabled ?? config.GetValue("ItsApi:Enabled", true);
        var itsPort = runtime?.ItsPort ?? config.GetValue("ItsApi:Port", 7070);
        var itsPath = runtime?.ItsPath ?? config.GetValue<string>("ItsApi:Path") ?? "/NotificationInfo/TollgateInfo";

        // Offline queue location: keep it next to the resolved CSV/doc folder.
        var docFolder = ResolveDokumentFolder(runtime?.DokumentFolder);
        var offlinePath = Path.Combine(docFolder, "worker_offline_queue.csv");

        var debounceSeconds = config.GetValue("Worker:PlateDebounceSeconds", 3);

        var uiAdminPassword = runtime?.UiAdminPassword ?? "admin";
        var uiWorkerPassword = runtime?.UiWorkerPassword ?? "worker";

        var ssM = runtime?.SommerStartMonth ?? 4;
        var ssD = runtime?.SommerStartDay ?? 1;
        var wsM = runtime?.VinterStartMonth ?? 10;
        var wsD = runtime?.VinterStartDay ?? 1;
        var typeColorMap = runtime?.TypeVaskColorMap ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ansattColorMap = runtime?.AnsattColorMap ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return new AppConfig(workerConn, refreshSeconds, showOnly, showOnlyUnntak, itsEnabled, itsPort, itsPath, offlinePath, debounceSeconds, uiAdminPassword, uiWorkerPassword, ssM, ssD, wsM, wsD, typeColorMap, ansattColorMap);
    }

    private sealed record RuntimeSettings(
        string? DokumentFolder,
        string? WorkerConn,
        int? WorkerRefreshSeconds,
        bool? WorkerShowOnlyUnconfirmed,
        bool? WorkerShowOnlyUnntak,
        bool? ItsEnabled,
        int? ItsPort,
        string? ItsPath,
        string? UiAdminPassword,
        string? UiWorkerPassword,
        string? WorkerConnBuiltFromDb,
        int? SommerStartMonth,
        int? SommerStartDay,
        int? VinterStartMonth,
        int? VinterStartDay,
        Dictionary<string, string>? TypeVaskColorMap,
        Dictionary<string, string>? AnsattColorMap);

private static RuntimeSettings? TryLoadRuntime()
    {
        try
        {
            // There can be TWO runtime files:
            // - ProgramData (written by installer / shared for all users)
            // - Documents (user-writable)
            //
            // In the field we've seen cases where ProgramData still contains an old default (127.0.0.1),
            // while the user saved the correct DB host in Documents. If we always prefer ProgramData,
            // Worker will keep trying to connect to 127.0.0.1.
            //
            // Strategy:
            //  - Parse BOTH if they exist
            //  - Prefer the one that points to a non-local host
            //  - Otherwise prefer the newest file
            //  - Otherwise prefer Documents (user-writable)

            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var pdPath = Path.Combine(common, "BilvaskRegistrering", "settings.runtime.json");

            var doc = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var docPath = Path.Combine(doc, "BilvaskRegistrering", "settings.runtime.json");

            var pd = TryParseRuntime(pdPath);
            var docs = TryParseRuntime(docPath);

            if (pd is null) return docs;
            if (docs is null) return pd;

            bool pdLocal = LooksLikeLocalDb(pd);
            bool docsLocal = LooksLikeLocalDb(docs);

            if (!docsLocal && pdLocal) return docs;
            if (!pdLocal && docsLocal) return pd;

            DateTime pdWrite = SafeLastWrite(pdPath);
            DateTime docWrite = SafeLastWrite(docPath);

            if (docWrite > pdWrite) return docs;
            if (pdWrite > docWrite) return pd;

            return docs; // fallback: prefer user-writable
        }
        catch
        {
            return null;
        }

        static DateTime SafeLastWrite(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        static bool LooksLikeLocalDb(RuntimeSettings s)
        {
            string? conn = null;
            if (!string.IsNullOrWhiteSpace(s.WorkerConn)) conn = s.WorkerConn;
            else if (!string.IsNullOrWhiteSpace(s.WorkerConnBuiltFromDb)) conn = s.WorkerConnBuiltFromDb;

            if (string.IsNullOrWhiteSpace(conn))
                return true;

            try
            {
                var b = new NpgsqlConnectionStringBuilder(conn);
                var host = (b.Host ?? "").Trim();
                if (string.IsNullOrWhiteSpace(host)) return true;
                if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
                if (host.StartsWith("127.")) return true;
                return false;
            }
            catch
            {
                // If parsing fails, don't block loading; treat as local to prefer the other file.
                return true;
            }
        }

        static RuntimeSettings? TryParseRuntime(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;

                using var stream = File.OpenRead(path);
                using var docJson = JsonDocument.Parse(stream);
                var root = docJson.RootElement;

                string? dokumentFolder = null;
                if (root.TryGetProperty("Dokument", out var dokument) && dokument.TryGetProperty("Folder", out var folderEl) && folderEl.ValueKind == JsonValueKind.String)
                    dokumentFolder = folderEl.GetString();

                string? workerConn = null;
                if (root.TryGetProperty("ConnectionStrings", out var cs) && cs.TryGetProperty("worker", out var worker) && worker.ValueKind == JsonValueKind.String)
                    workerConn = worker.GetString();
                else if (root.TryGetProperty("ConnectionStrings", out cs) && cs.TryGetProperty("Worker", out worker) && worker.ValueKind == JsonValueKind.String)
                    workerConn = worker.GetString();

                int? refresh = null;
                bool? only = null;
                bool? onlyUnntak = null;
                if (root.TryGetProperty("WorkerUi", out var workerUi))
                {
                    if (workerUi.TryGetProperty("RefreshSeconds", out var rs) && rs.TryGetInt32(out var v)) refresh = v;
                    if (workerUi.TryGetProperty("ShowOnlyUnconfirmed", out var su) && su.ValueKind is JsonValueKind.True or JsonValueKind.False) only = su.GetBoolean();
                    if (workerUi.TryGetProperty("ShowOnlyUnntak", out var un) && un.ValueKind is JsonValueKind.True or JsonValueKind.False) onlyUnntak = un.GetBoolean();
                }
                if (root.TryGetProperty("Worker", out var worker2))
                {
                    if (worker2.TryGetProperty("RefreshSeconds", out var rs) && rs.TryGetInt32(out var v)) refresh ??= v;
                    if (worker2.TryGetProperty("ShowOnlyUnconfirmed", out var su) && su.ValueKind is JsonValueKind.True or JsonValueKind.False) only ??= su.GetBoolean();
                    if (worker2.TryGetProperty("ShowOnlyUnntak", out var un) && un.ValueKind is JsonValueKind.True or JsonValueKind.False) onlyUnntak ??= un.GetBoolean();
                }

                bool? itsEnabled = null;
                int? itsPort = null;
                string? itsPath = null;
                if (root.TryGetProperty("ItsApi", out var its))
                {
                    if (its.TryGetProperty("Enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False) itsEnabled = en.GetBoolean();
                    if (its.TryGetProperty("Port", out var po) && po.TryGetInt32(out var pv)) itsPort = pv;
                    if (its.TryGetProperty("Path", out var pa) && pa.ValueKind == JsonValueKind.String) itsPath = pa.GetString();
                }

                // UI passwords
                string? uiAdminPassword = null;
                string? uiWorkerPassword = null;
                if (root.TryGetProperty("Auth", out var auth))
                {
                    if (auth.TryGetProperty("AdminPassword", out var ap) && ap.ValueKind == JsonValueKind.String) uiAdminPassword = ap.GetString();
                    if (auth.TryGetProperty("WorkerPassword", out var wp) && wp.ValueKind == JsonValueKind.String) uiWorkerPassword = wp.GetString();

                    // tolerate older/lowercase property names
                    if (string.IsNullOrWhiteSpace(uiAdminPassword) && auth.TryGetProperty("adminPassword", out ap) && ap.ValueKind == JsonValueKind.String) uiAdminPassword = ap.GetString();
                    if (string.IsNullOrWhiteSpace(uiWorkerPassword) && auth.TryGetProperty("workerPassword", out wp) && wp.ValueKind == JsonValueKind.String) uiWorkerPassword = wp.GetString();
                }

                // Season dates
                int? ssM = null, ssD = null, wsM = null, wsD = null;
                if (root.TryGetProperty("SesongDatoer", out var sd))
                {
                    if (sd.TryGetProperty("SommerStartMonth", out var m) && m.TryGetInt32(out var mv)) ssM = mv;
                    if (sd.TryGetProperty("SommerStartDay", out var d) && d.TryGetInt32(out var dv)) ssD = dv;
                    if (sd.TryGetProperty("VinterStartMonth", out var m2) && m2.TryGetInt32(out var mv2)) wsM = mv2;
                    if (sd.TryGetProperty("VinterStartDay", out var d2) && d2.TryGetInt32(out var dv2)) wsD = dv2;
                }

                Dictionary<string, string>? typeVaskColorMap = null;
                Dictionary<string, string>? ansattColorMap = null;
                if (root.TryGetProperty("WorkerColors", out var wc))
                {
                    if (wc.TryGetProperty("TypeVask", out var tv) && tv.ValueKind == JsonValueKind.Object)
                        typeVaskColorMap = ParseStringMap(tv);
                    if (wc.TryGetProperty("Ansatt", out var an) && an.ValueKind == JsonValueKind.Object)
                        ansattColorMap = ParseStringMap(an);
                }

                // Build a worker connection string from Database section (if ConnectionStrings is missing)
                string? workerConnBuiltFromDb = null;
                try
                {
                    if (root.TryGetProperty("Database", out var db) &&
                        db.TryGetProperty("Enabled", out var en) &&
                        en.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                        en.GetBoolean())
                    {
                        string? host = null;
                        string? workerHost = null;
                        int port = 5432;
                        string? name = null;
                        string? user = null;
                        string? pass = null;
                        string? sslMode = null;
                        bool trust = true;

                        if (db.TryGetProperty("Host", out var h) && h.ValueKind == JsonValueKind.String) host = h.GetString();
                        if (db.TryGetProperty("WorkerHost", out var wh) && wh.ValueKind == JsonValueKind.String) workerHost = wh.GetString();
                        if (db.TryGetProperty("Port", out var p) && p.TryGetInt32(out var pv)) port = pv;

                        // Accept both "Name" (older drafts) and "Database" (current)
                        if (db.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
                        if (string.IsNullOrWhiteSpace(name) && db.TryGetProperty("Database", out var dbn) && dbn.ValueKind == JsonValueKind.String) name = dbn.GetString();

                        if (db.TryGetProperty("WorkerUser", out var u) && u.ValueKind == JsonValueKind.String) user = u.GetString();
                        if (db.TryGetProperty("WorkerPassword", out var pw) && pw.ValueKind == JsonValueKind.String) pass = pw.GetString();
                        if (db.TryGetProperty("SslMode", out var sm) && sm.ValueKind == JsonValueKind.String) sslMode = sm.GetString();
                        if (db.TryGetProperty("TrustServerCertificate", out var t) && t.ValueKind is JsonValueKind.True or JsonValueKind.False) trust = t.GetBoolean();

                        var finalHost = string.IsNullOrWhiteSpace(workerHost) ? host : workerHost;
                        if (!string.IsNullOrWhiteSpace(finalHost) &&
                            !string.IsNullOrWhiteSpace(name) &&
                            !string.IsNullOrWhiteSpace(user))
                        {
                            var ssl = string.IsNullOrWhiteSpace(sslMode) ? "Prefer" : sslMode;
                            workerConnBuiltFromDb =
                                $"Host={finalHost};Port={port};Database={name};Username={user};Password={pass};Ssl Mode={ssl};Trust Server Certificate={trust};";
                        }
                    }
                }
                catch { /* ignore */ }

                return new RuntimeSettings(dokumentFolder, workerConn, refresh, only, onlyUnntak, itsEnabled, itsPort, itsPath, uiAdminPassword, uiWorkerPassword, workerConnBuiltFromDb, ssM, ssD, wsM, wsD, typeVaskColorMap, ansattColorMap);
            }
            catch
            {
                return null;
            }
        }
    }


    public static bool TryParseColor(string? value, out Color color)
    {
        color = Color.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var s = value.Trim();
        try
        {
            color = ColorTranslator.FromHtml(s);
            return true;
        }
        catch { }

        try
        {
            color = Color.FromName(s);
            return color.ToArgb() != 0 || string.Equals(s, "Black", StringComparison.OrdinalIgnoreCase);
        }
        catch { }

        return false;
    }

    public static string ColorToSetting(Color color)
        => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Dictionary<string, string> ParseStringMap(JsonElement el)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var val = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(prop.Name) && !string.IsNullOrWhiteSpace(val))
                        map[prop.Name.Trim()] = val!.Trim();
                }
            }
        }
        catch { }
        return map;
    }

    private static int ClampMonth(int m, int fallback)
    {
        if (m < 1 || m > 12) return fallback;
        return m;
    }

    private static int ClampDay(int month, int day, int fallback)
    {
        try
        {
            var max = DateTime.DaysInMonth(2024, ClampMonth(month, 1));
            if (day < 1) return fallback;
            if (day > max) return max;
            return day;
        }
        catch
        {
            return fallback;
        }
    }
}
