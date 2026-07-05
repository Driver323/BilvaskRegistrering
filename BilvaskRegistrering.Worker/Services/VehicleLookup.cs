using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BilvaskRegistrering.Worker.Services;

public sealed class VehicleLookup
{
    private readonly string _documentsFolder;
    private readonly object _gate = new();
    private DateTime _lastLoadUtc = DateTime.MinValue;
    private Dictionary<string, VehicleInfo> _byReg = new(StringComparer.OrdinalIgnoreCase);

    public VehicleLookup(string documentsFolder)
    {
        _documentsFolder = documentsFolder ?? throw new ArgumentNullException(nameof(documentsFolder));
    }

    public VehicleInfo? TryGetByRegNr(string regNr)
    {
        if (string.IsNullOrWhiteSpace(regNr)) return null;
        EnsureLoaded();

        var key = NormalizeReg(regNr);
        lock (_gate)
        {
            return _byReg.TryGetValue(key, out var info) ? info : null;
        }
    }

    public bool IsUnntak(string regNr)
    {
        var info = TryGetByRegNr(regNr);
        return info?.Unntak == true;
    }

    public void ForceReload() => LoadNow();

    private void EnsureLoaded()
    {
        // cheap: reload at most every 30 seconds to pick up file edits
        if (DateTime.UtcNow - _lastLoadUtc < TimeSpan.FromSeconds(30)) return;
        LoadNow();
    }

    private void LoadNow()
    {
        lock (_gate)
        {
            var map = new Dictionary<string, VehicleInfo>(StringComparer.OrdinalIgnoreCase);

            // Prefer EgenFlate, then fall back to Kjoretoyregister
            var egenFlate = Path.Combine(_documentsFolder, "EgenFlate.csv");
            var kjoretoy = Path.Combine(_documentsFolder, "Kjoretoyregister.csv");

            if (File.Exists(egenFlate))
                LoadCsv(egenFlate, map, isEgenFlate: true);
            if (File.Exists(kjoretoy))
                LoadCsv(kjoretoy, map, isEgenFlate: false);

            _byReg = map;
            _lastLoadUtc = DateTime.UtcNow;
        }
    }

    private static void LoadCsv(string path, Dictionary<string, VehicleInfo> map, bool isEgenFlate)
    {
        foreach (var (line, idx) in File.ReadLines(path).Select((l, i) => (l, i)))
        {
            if (idx == 0) continue; // header
            if (string.IsNullOrWhiteSpace(line)) continue;

            // ';' separated
            var parts = line.Split(';');
            if (parts.Length < 2) continue;

            var internnr = parts[0].Trim();
            var regnr = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(regnr)) continue;

            var type = parts.Length > 2 ? parts[2].Trim() : "";
            var unntakRaw = parts.Length > 3 ? parts[3].Trim() : "";
            var unntak = ParseBool(unntakRaw);

            // Additional columns differ between files, we keep best-effort metadata
            string? selskap = null;
            string? merke = null;

            if (isEgenFlate)
            {
                // Internnr;Regnr;TypeKjoretoy;Unntak;Selskap;KjoretoyT...;Merke;... (varies)
                selskap = parts.Length > 4 ? parts[4].Trim() : null;
                merke = parts.Length > 6 ? parts[6].Trim() : null;
            }
            else
            {
                // Internnr;Regnr;TypeKjoretoy;Unntak;Selskap;...
                selskap = parts.Length > 4 ? parts[4].Trim() : null;
                merke = parts.Length > 7 ? parts[7].Trim() : null;
            }

            var key = NormalizeReg(regnr);

            // Keep existing entry from EgenFlate (preferred). If not present, add.
            if (!map.ContainsKey(key) || isEgenFlate)
            {
                map[key] = new VehicleInfo
                {
                    RegNr = key,
                    Internnr = string.IsNullOrWhiteSpace(internnr) ? null : internnr,
                    TypeKjoretoy = string.IsNullOrWhiteSpace(type) ? null : type,
                    Unntak = unntak,
                    Selskap = string.IsNullOrWhiteSpace(selskap) ? null : selskap,
                    Merke = string.IsNullOrWhiteSpace(merke) ? null : merke,
                };
            }
        }
    }

    public static string NormalizeReg(string regNr)
    {
        // Same normalization approach as the Admin UI: trim/upper/remove whitespace
        return new string(regNr
            .Trim()
            .ToUpperInvariant()
            .Where(c => !char.IsWhiteSpace(c))
            .ToArray());
    }

    private static bool ParseBool(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        if (bool.TryParse(value, out var b)) return b;
        if (value == "1") return true;
        if (value == "0") return false;
        return value.Equals("ja", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class VehicleInfo
{
    public string RegNr { get; set; } = "";
    public string? Internnr { get; set; }
    public string? TypeKjoretoy { get; set; }
    public bool Unntak { get; set; }
    public string? Selskap { get; set; }
    public string? Merke { get; set; }
}
