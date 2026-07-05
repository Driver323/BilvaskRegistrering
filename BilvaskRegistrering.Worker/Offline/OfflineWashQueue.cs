using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BilvaskRegistrering.Worker.Offline;

/// <summary>
/// Very simple local CSV queue for plates when DB is offline.
/// Safe for "append only"; drain reads file then truncates.
/// </summary>
public sealed class OfflineWashQueue
{
    private readonly string _path;

    public OfflineWashQueue(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
    }

    public void Enqueue(string plate, DateTime occurredAtUtc)
    {
        var line = $"{occurredAtUtc.ToString("O", CultureInfo.InvariantCulture)};{plate}";
        File.AppendAllLines(_path, new[] { line }, Encoding.UTF8);
    }

    public IReadOnlyList<(DateTime occurredAtUtc, string plate)> DrainAll()
    {
        if (!File.Exists(_path)) return Array.Empty<(DateTime, string)>();

        var lines = File.ReadAllLines(_path, Encoding.UTF8)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        // truncate first (so if insert below crashes, we don't loop forever on same file)
        File.WriteAllText(_path, "", Encoding.UTF8);

        var items = new List<(DateTime, string)>();
        foreach (var line in lines)
        {
            var parts = line.Split(';');
            if (parts.Length < 2) continue;

            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                continue;

            var plate = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(plate)) continue;

            items.Add((dt, plate));
        }

        return items;
    }
}
