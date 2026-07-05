using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BilvaskRegistrering.Worker.LocalData;

internal sealed class VehicleRegistry
{
    internal sealed record VehicleInfo(
        string? Internnr,
        string? Selskap,
        string? TypeKjoretoy,
        bool Unntak,
        string? Note);

    private readonly Dictionary<string, VehicleInfo> _byReg = new(StringComparer.OrdinalIgnoreCase);

    public static VehicleRegistry LoadFromFolder(string folder)
    {
        var reg = new VehicleRegistry();
        var path = Path.Combine(folder, "Kjoretoyregister.csv");
        if (!File.Exists(path)) return reg;

        // Expected header (from shipped sample):
        // Regnr;Internnr;Selskap;TypeKjoretoy;Sesong;Status;Unntak;Note
        var lines = File.ReadAllLines(path);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(';');
            if (parts.Length < 1) continue;

            var regnr = Normalize(parts[0]);
            if (string.IsNullOrWhiteSpace(regnr)) continue;

            string? internnr = parts.Length > 1 ? EmptyToNull(parts[1]) : null;
            string? selskap = parts.Length > 2 ? EmptyToNull(parts[2]) : null;
            string? type = parts.Length > 3 ? EmptyToNull(parts[3]) : null;

            // Unntak column can be 0/1 or true/false
            bool unntak = false;
            if (parts.Length > 6)
            {
                var u = (parts[6] ?? "").Trim();
                if (u == "1") unntak = true;
                else if (bool.TryParse(u, out var b)) unntak = b;
            }

            string? note = parts.Length > 7 ? EmptyToNull(parts[7]) : null;

            reg._byReg[regnr] = new VehicleInfo(internnr, selskap, type, unntak, note);
        }

        return reg;
    }

    public VehicleInfo? Lookup(string regnr)
    {
        if (string.IsNullOrWhiteSpace(regnr)) return null;
        _byReg.TryGetValue(Normalize(regnr), out var info);
        return info;
    }

    private static string Normalize(string? regnr)
    {
        if (string.IsNullOrWhiteSpace(regnr)) return "";
        return regnr.Trim().Replace(" ", "").ToUpperInvariant();
    }

    private static string? EmptyToNull(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Trim();
    }
}
