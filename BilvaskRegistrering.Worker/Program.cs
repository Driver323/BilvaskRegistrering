using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace BilvaskRegistrering.Worker;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var activationCode = LoadActivationCode(out var configWarning);

        if (!BilvaskRegistrering.Worker.Security.InstallCodeVerifier.TryVerify(activationCode, out _, out var err))
        {
            MessageBox.Show(
                "Manglende eller ugyldig installasjonskode.\n\n" +
                err + "\n\n" +
                "Kjør installasjonsprogrammet og skriv inn en gyldig installasjonskode.",
                "Bilvask Worker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(configWarning))
        {
            MessageBox.Show(
                configWarning,
                "Bilvask Worker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        Application.Run(new WorkerForm());
    }

    private static string LoadActivationCode(out string warning)
    {
        warning = string.Empty;

        // Best for VS/debug:
        var env = Environment.GetEnvironmentVariable("BILVASK_INSTALL_CODE");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        // install_code.txt in Documents / ProgramData
        var fromDocsTxt = TryReadInstallCodeTxt(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(fromDocsTxt)) return fromDocsTxt;

        var fromPdTxt = TryReadInstallCodeTxt(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(fromPdTxt)) return fromPdTxt;

        // settings.runtime.json in Documents / ProgramData (salvage code even if JSON broken)
        var fromDocs = TryReadActivationFromRuntimeJson(Environment.SpecialFolder.MyDocuments, ref warning);
        if (!string.IsNullOrWhiteSpace(fromDocs)) return fromDocs;

        var fromPd = TryReadActivationFromRuntimeJson(Environment.SpecialFolder.CommonApplicationData, ref warning);
        return fromPd ?? string.Empty;
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

    private static string? TryReadActivationFromRuntimeJson(Environment.SpecialFolder folder, ref string warning)
    {
        var baseDir = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(baseDir)) return null;

        var path = Path.Combine(baseDir, "BilvaskRegistrering", "settings.runtime.json");
        if (!File.Exists(path)) return null;

        // First, try to salvage from raw text (works even if JSON broken)
        try
        {
            var raw = File.ReadAllText(path);
            var loose = ExtractActivationCodeFromRawText(raw);
            if (!string.IsNullOrWhiteSpace(loose)) return loose;
        }
        catch { }

        // Then, try strict JSON
        try
        {
            using var stream = File.OpenRead(path);
            using var json = JsonDocument.Parse(stream);

            var root = json.RootElement;
            if (root.TryGetProperty("Install", out var install) &&
                install.TryGetProperty("ActivationCode", out var ac) &&
                ac.ValueKind == JsonValueKind.String)
            {
                return ac.GetString();
            }
        }
        catch
        {
            try
            {
                var backup = path + ".invalid_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                File.Move(path, backup);
            }
            catch { }

            warning =
                "Konfigurasjonsfilen settings.runtime.json er ugyldig og ble ignorert.\n" +
                "Kjør installasjonsprogrammet på nytt eller åpne Innstillinger og lagre på nytt.";
        }

        return null;
    }

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
