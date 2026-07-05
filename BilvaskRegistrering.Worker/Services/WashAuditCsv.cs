using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace BilvaskRegistrering.Worker.Services;

/// <summary>
/// Append-only audit CSV log for wash events recognized/handled by Worker.
/// This is NOT the offline queue (which is drained/truncated).
/// </summary>
internal sealed class WashAuditCsv
{
    private readonly string _path;
    private static readonly Mutex _mutex = new(false, "Local\\BilvaskRegistrering_WashAuditCsv");

    public WashAuditCsv(string dokumentFolder)
    {
        var folder = dokumentFolder;

        try
        {
            if (string.IsNullOrWhiteSpace(folder))
                throw new InvalidOperationException("DokumentFolder empty");

            Directory.CreateDirectory(folder);
        }
        catch
        {
            folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "BilvaskRegistrering");
            Directory.CreateDirectory(folder);
        }

        _path = Path.Combine(folder, "WashEvents_Audit.csv");
    }

    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return s.Replace(";", ",").Replace("\r", " ").Replace("\n", " ").Trim();
    }

    public void Append(
        DateTime occurredAtUtc,
        string phase,
        string source,
        string plate,
        bool dbOk,
        bool queued,
        string? error)
    {
        var header = "ts_utc;app;phase;source;plate;internnr;vehicle_type;season;status;note;selskap;db_ok;queued;error";
        var line = string.Join(";", new[]
        {
            DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
            "worker",
            Clean(phase),
            Clean(source),
            Clean(plate),
            "", "", "", "", "", "", // optional fields not known in Worker ingest
            dbOk ? "1" : "0",
            queued ? "1" : "0",
            Clean(error)
        });

        bool locked = false;
        try { locked = _mutex.WaitOne(750); } catch { }

        try
        {
            using var fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var isEmpty = fs.Length == 0;
            fs.Seek(0, SeekOrigin.End);
            using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (isEmpty)
                sw.WriteLine(header);
            sw.WriteLine(line);
            sw.Flush();
        }
        finally
        {
            if (locked)
            {
                try { _mutex.ReleaseMutex(); } catch { }
            }
        }
    }
}
