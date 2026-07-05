using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Extensions.Configuration;

namespace BilvaskRegistrering;

internal static class Program
{
    public static IConfigurationRoot AppConfig { get; private set; } = null!;

    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppConfig = BuildConfigSafe(out var configWarning, out var looseCodeFromRuntimeJson);

        // --- Install code verification (RSA-signed, offline) ---
        var activationCode =
            Environment.GetEnvironmentVariable("BILVASK_INSTALL_CODE")?.Trim()
            ?? TryReadInstallCodeTxt(Environment.SpecialFolder.MyDocuments)
            ?? TryReadInstallCodeTxt(Environment.SpecialFolder.CommonApplicationData)
            ?? (string.IsNullOrWhiteSpace(looseCodeFromRuntimeJson) ? null : looseCodeFromRuntimeJson)
            ?? AppConfig.GetValue<string>("Install:ActivationCode")
            ?? "";

        if (!BilvaskRegistrering.Security.InstallCodeVerifier.TryVerify(activationCode, out _, out var licErr))
        {
            MessageBox.Show(
                "Manglende eller ugyldig installasjonskode.\n\n" +
                licErr + "\n\n" +
                "Kjør installasjonsprogrammet og skriv inn en gyldig installasjonskode.",
                "BilvaskRegistrering",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(configWarning))
        {
            MessageBox.Show(
                configWarning,
                "BilvaskRegistrering",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        DbConfig.Load(AppConfig);

        // Ensure DB schema exists (non-fatal on failure)
        try
        {
            if (DbConfig.Enabled && !string.IsNullOrWhiteSpace(DbConfig.ConnectionString))
            {
                DbSchemaMigrator.EnsureAsync(DbConfig.ConnectionString).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // ignore
        }

        Application.Run(new HovedForm());
    }

    private static IConfigurationRoot BuildConfigSafe(out string warning, out string looseActivationCode)
{
    warning = string.Empty;
    looseActivationCode = string.Empty;

    // We'll retry once if a runtime JSON file crashes the configuration builder (e.g. duplicate keys).
    for (var attempt = 0; attempt < 2; attempt++)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

        // Runtime settings (Documents)
        var docsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "BilvaskRegistrering", "settings.runtime.json");

        TryAddJsonFileSafe(builder, docsPath, ref warning, ref looseActivationCode);

        // Fallback (ProgramData)
        var programDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "BilvaskRegistrering", "settings.runtime.json");

        TryAddJsonFileSafe(builder, programDataPath, ref warning, ref looseActivationCode);

        try
        {
            return builder.Build();
        }
        catch (Exception ex) when (attempt == 0)
        {
            var badPath = ExtractJsonPathFromException(ex);
            if (!string.IsNullOrWhiteSpace(badPath))
            {
                HandleBadRuntimeJson(badPath!, ref warning, ref looseActivationCode,
                    "Konfigurasjonsfilen inneholdt duplikate eller ugyldige nøkler og ble reparert/ignorert.");
                continue; // retry with fresh builder
            }
            throw;
        }
    }

    // Fallback (should never happen)
    return new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .Build();
}

private static void TryAddJsonFileSafe(IConfigurationBuilder builder, string path, ref string warning, ref string looseActivationCode)
{
    if (!File.Exists(path)) return;

    // 1) Try to parse & normalize duplicate keys (case-insensitive), which can crash ConfigurationBuilder
    // (e.g., ConnectionStrings.Worker + ConnectionStrings.worker).
    try
    {
        NormalizeDuplicateKeysInPlace(path);
    }
    catch
    {
        // ignore; handled below
    }

    // 2) Validate JSON can be parsed and does not have case-insensitive duplicate keys.
    try
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        ThrowIfCaseInsensitiveDuplicateKeys(doc.RootElement, "");
    }
    catch
    {
        HandleBadRuntimeJson(path, ref warning, ref looseActivationCode,
            "Konfigurasjonsfilen settings.runtime.json er ugyldig og ble ignorert.");
        return;
    }

    builder.AddJsonFile(path, optional: true, reloadOnChange: true);
}

private static void HandleBadRuntimeJson(string path, ref string warning, ref string looseActivationCode, string reason)
{
    // Try to salvage activation code from raw text even if JSON is broken
    try
    {
        var raw = File.ReadAllText(path);
        var extracted = ExtractActivationCodeFromRawText(raw);
        if (!string.IsNullOrWhiteSpace(extracted))
        {
            looseActivationCode = extracted;
        }
    }
    catch { /* ignore */ }

    // If JSON is parseable but contains duplicates, try to normalize it before giving up.
    try
    {
        NormalizeDuplicateKeysInPlace(path);
        AppendWarning(ref warning,
            "Konfigurasjonsfilen settings.runtime.json inneholdt duplikate nøkler og ble automatisk reparert.\n" +
            "Hvis du fortsatt får problemer: åpne Innstillinger og trykk Lagre.");
        return;
    }
    catch
    {
        // ignore; will rename below
    }

    // Rename invalid JSON so app can start and Settings/Installer can recreate
    try
    {
        var backup = path + ".invalid_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        File.Move(path, backup);
    }
    catch { /* ignore */ }

    AppendWarning(ref warning,
        reason + "\n" +
        "Kjør installasjonsprogrammet på nytt eller åpne Innstillinger og lagre på nytt.");
}

private static void AppendWarning(ref string warning, string msg)
{
    if (string.IsNullOrWhiteSpace(msg)) return;
    if (string.IsNullOrWhiteSpace(warning))
        warning = msg;
    else
        warning = warning + "\n\n" + msg;
}

private static string? ExtractJsonPathFromException(Exception ex)
{
    // Typical message:
    // "Failed to load configuration from file 'C:\...\settings.runtime.json'."
    var cur = ex;
    while (cur != null)
    {
        var msg = cur.Message ?? "";
        var m = Regex.Match(msg, "file\\s+'([^']+settings\\.runtime\\.json)'", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        cur = cur.InnerException;
    }
    return null;
}

private static void ThrowIfCaseInsensitiveDuplicateKeys(JsonElement el, string pathPrefix)
{
    if (el.ValueKind == JsonValueKind.Object)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in el.EnumerateObject())
        {
            if (!seen.Add(prop.Name))
                throw new InvalidDataException($"Duplicate JSON key '{pathPrefix}{prop.Name}'");
            ThrowIfCaseInsensitiveDuplicateKeys(prop.Value, pathPrefix + prop.Name + ":");
        }
    }
    else if (el.ValueKind == JsonValueKind.Array)
    {
        var i = 0;
        foreach (var item in el.EnumerateArray())
        {
            ThrowIfCaseInsensitiveDuplicateKeys(item, pathPrefix + i + ":");
            i++;
        }
    }
}

private static void NormalizeDuplicateKeysInPlace(string path)
{
    var raw = File.ReadAllText(path);
    if (string.IsNullOrWhiteSpace(raw)) return;

    using var doc = JsonDocument.Parse(raw);

    // If no duplicates, do nothing.
    try
    {
        ThrowIfCaseInsensitiveDuplicateKeys(doc.RootElement, "");
        return;
    }
    catch
    {
        // has duplicates -> normalize
    }

    var normalized = NormalizeElement(doc.RootElement, parentPath: "");
    var json = normalized.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(path, json);
}

private static JsonNode NormalizeElement(JsonElement el, string parentPath)
{
    switch (el.ValueKind)
    {
        case JsonValueKind.Object:
        {
            // keep last value for each key (case-insensitive)
            var last = new Dictionary<string, (string OutName, JsonNode? Node)>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in el.EnumerateObject())
            {
                var outName = CanonicalizeKey(parentPath, prop.Name);
                last[prop.Name] = (outName, NormalizeElement(prop.Value, NextPath(parentPath, outName)));
            }

            // Emit keys only once (canonical casing), in the order of first appearance
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var obj = new JsonObject();
            foreach (var prop in el.EnumerateObject())
            {
                if (!last.TryGetValue(prop.Name, out var entry)) continue;
                if (!emitted.Add(entry.OutName)) continue;
                obj[entry.OutName] = entry.Node;
            }
            return obj;
        }
        case JsonValueKind.Array:
        {
            var arr = new JsonArray();
            foreach (var item in el.EnumerateArray())
                arr.Add(NormalizeElement(item, parentPath));
            return arr;
        }
        case JsonValueKind.String:
            return JsonValue.Create(el.GetString());
        case JsonValueKind.Number:
            if (el.TryGetInt64(out var l)) return JsonValue.Create(l);
            if (el.TryGetDouble(out var d)) return JsonValue.Create(d);
            return JsonValue.Create(el.GetRawText());
        case JsonValueKind.True:
        case JsonValueKind.False:
            return JsonValue.Create(el.GetBoolean());
        case JsonValueKind.Null:
        case JsonValueKind.Undefined:
        default:
            return JsonValue.Create((string?)null);
    }
}

private static string NextPath(string parentPath, string key)
    => string.IsNullOrWhiteSpace(parentPath) ? key : parentPath + ":" + key;

private static string CanonicalizeKey(string parentPath, string key)
{
    if (parentPath.Equals("ConnectionStrings", StringComparison.OrdinalIgnoreCase))
    {
        if (key.Equals("worker", StringComparison.OrdinalIgnoreCase)) return "Worker";
        if (key.Equals("admin", StringComparison.OrdinalIgnoreCase)) return "Admin";
    }
    return key;
}

private static string? TryReadInstallCodeTxt(Environment.SpecialFolder folder)
    {
        try
        {
            var dir = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(dir)) return null;

            var path = Path.Combine(dir, "BilvaskRegistrering", "install_code.txt");
            if (!File.Exists(path)) return null;

            var raw = File.ReadAllText(path);
            var code = ExtractActivationCodeFromRawText(raw);
            return string.IsNullOrWhiteSpace(code) ? null : code;
        }
        catch
        {
            return null;
        }
    }

    // Robust extraction:
    // - Accepts pasted emails / wrapped lines
    // - Ignores whitespace
    // - Returns the LAST BVR1.<...>.<...> token found
    private static string ExtractActivationCodeFromRawText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return "";
        var compact = Regex.Replace(rawText, "\\s+", "");
        var matches = Regex.Matches(compact, "BVR1\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+");
        if (matches.Count > 0)
        {
            return matches[matches.Count - 1].Value;
        }
        return "";
    }
}
