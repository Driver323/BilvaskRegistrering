using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BilvaskRegistrering.Database;

/// <summary>
/// Simple local CSV queue for wash events when DB is offline.
/// Format (semicolon separated):
/// occurredAtO;plate;internnr;vehicleType;season;status;note;selskap
///
/// Safe for append-only; Drain reads file then truncates.
/// </summary>
public sealed class AdminOfflineEventQueue
{
    private readonly string _path;

    public AdminOfflineEventQueue(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
    }

    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        // keep file parseable
        return s.Replace(";", ",").Replace("\r", " ").Replace("\n", " ").Trim();
    }

    public void Enqueue(
        DateTimeOffset occurredAt,
        string plate,
        string? internnr,
        string? vehicleType,
        string? season,
        string? status,
        string? note,
        string? selskap)
    {
        var line = string.Join(";", new[]
        {
            occurredAt.ToString("O", CultureInfo.InvariantCulture),
            Clean(plate),
            Clean(internnr),
            Clean(vehicleType),
            Clean(season),
            Clean(status),
            Clean(note),
            Clean(selskap)
        });

        File.AppendAllLines(_path, new[] { line }, Encoding.UTF8);
    }

    public IReadOnlyList<(DateTimeOffset occurredAt, string plate, string? internnr, string? vehicleType, string? season, string? status, string? note, string? selskap)> DrainAll()
    {
        if (!File.Exists(_path)) return Array.Empty<(DateTimeOffset, string, string?, string?, string?, string?, string?, string?)>();

        var lines = File.ReadAllLines(_path, Encoding.UTF8)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        // truncate first (so we don't loop forever if insert crashes)
        File.WriteAllText(_path, "", Encoding.UTF8);

        var items = new List<(DateTimeOffset, string, string?, string?, string?, string?, string?, string?)>();
        foreach (var line in lines)
        {
            var parts = line.Split(';');
            if (parts.Length < 2) continue;

            if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                continue;

            var plate = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(plate)) continue;

            string? internnr = parts.Length > 2 ? parts[2].Trim() : null;
            string? vehicleType = parts.Length > 3 ? parts[3].Trim() : null;
            string? season = parts.Length > 4 ? parts[4].Trim() : null;
            string? status = parts.Length > 5 ? parts[5].Trim() : null;
            string? note = parts.Length > 6 ? parts[6].Trim() : null;
            string? selskap = parts.Length > 7 ? parts[7].Trim() : null;

            items.Add((dto, plate, internnr, vehicleType, season, status, note, selskap));
        }

        return items;
    }
}
