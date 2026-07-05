using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BilvaskRegistrering.Worker.Data;

/// <summary>
/// Lightweight local lookup for vehicle metadata (type + unntak) stored in DokumentFolder.
/// Expected file: DokumentFolder\Kjoretoyregister.csv (semicolon-separated).
/// Columns: Regnr;TypeKjoretoy;Unntak;Kommentar
/// </summary>
internal sealed class VehicleRegistry
{
    private readonly string _csvPath;
    private readonly object _reloadLock = new();
    private DateTime _lastWriteUtc;
    private ConcurrentDictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);

    public VehicleRegistry(string dokumentFolder)
    {
        _csvPath = Path.Combine(dokumentFolder ?? string.Empty, "Kjoretoyregister.csv");
    }

    public bool TryGet(string regnr, out Entry entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(regnr)) return false;

        EnsureLoaded();
        return _map.TryGetValue(Normalize(regnr), out entry);
    }

    private void EnsureLoaded()
    {
        try
        {
            if (!File.Exists(_csvPath)) return;

            var writeUtc = File.GetLastWriteTimeUtc(_csvPath);
            if (writeUtc == _lastWriteUtc && _map.Count > 0) return;

            lock (_reloadLock)
            {
                writeUtc = File.GetLastWriteTimeUtc(_csvPath);
                if (writeUtc == _lastWriteUtc && _map.Count > 0) return;

                var next = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadLines(_csvPath))
                {
                    // Skip empty + header
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("Regnr", StringComparison.OrdinalIgnoreCase)) continue;

                    var parts = line.Split(';');
                    if (parts.Length < 1) continue;

                    var reg = Normalize(parts[0]);
                    if (string.IsNullOrWhiteSpace(reg)) continue;

                    var type = parts.Length > 1 ? parts[1]?.Trim() : null;
                    var unntakStr = parts.Length > 2 ? parts[2]?.Trim() : null;
                    var kommentar = parts.Length > 3 ? parts[3]?.Trim() : null;

                    bool? unntak = null;
                    if (!string.IsNullOrWhiteSpace(unntakStr))
                    {
                        // Accept: 1/0, true/false, ja/nei
                        if (bool.TryParse(unntakStr, out var b)) unntak = b;
                        else if (unntakStr == "1") unntak = true;
                        else if (unntakStr == "0") unntak = false;
                        else if (unntakStr.Equals("ja", StringComparison.OrdinalIgnoreCase)) unntak = true;
                        else if (unntakStr.Equals("nei", StringComparison.OrdinalIgnoreCase)) unntak = false;
                    }

                    next[reg] = new Entry(type, unntak, kommentar);
                }

                _map = next;
                _lastWriteUtc = writeUtc;
            }
        }
        catch
        {
            // registry is best-effort; ignore failures
        }
    }

    private static string Normalize(string regnr) => regnr.Trim().ToUpperInvariant();

    public readonly record struct Entry(string? VehicleType, bool? Unntak, string? Kommentar);
}
