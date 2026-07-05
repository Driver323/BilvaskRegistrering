using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace BilvaskRegistrering.Database;

/// <summary>
/// Append-only audit CSV for wash events.
/// This is NOT the offline queue. It is a durable log/backup written even when DB is up.
/// </summary>
public sealed class WashAuditCsv
{
    private readonly string _path;
    private readonly object _gate = new();

    public WashAuditCsv(string path)
    {
        _path = path;
    }

    public void Append(
        DateTimeOffset occurredAtUtc,
        string app,
        string phase,
        string source,
        string plate,
        string? internnr,
        string? vehicleType,
        string? season,
        string? status,
        string? note,
        string? selskap,
        bool dbOk,
        bool queued,
        string? error)
    {
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var writeHeader = !File.Exists(_path) || new FileInfo(_path).Length == 0;

            using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            if (writeHeader)
            {
                sw.WriteLine("occurred_at_utc,app,phase,source,plate,internnr,vehicle_type,season,status,note,selskap,db_ok,queued,error");
            }

            static string Esc(string? s)
            {
                s ??= "";
                if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
                    return '"' + s.Replace("\"", "\"\"") + '"';
                return s;
            }

            var line = string.Join(",",
                occurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                Esc(app),
                Esc(phase),
                Esc(source),
                Esc(plate),
                Esc(internnr),
                Esc(vehicleType),
                Esc(season),
                Esc(status),
                Esc(note),
                Esc(selskap),
                dbOk ? "1" : "0",
                queued ? "1" : "0",
                Esc(error));

            sw.WriteLine(line);
        }
    }
}
