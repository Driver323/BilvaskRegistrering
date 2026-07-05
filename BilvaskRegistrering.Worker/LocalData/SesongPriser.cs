using System;
using System.Globalization;
using System.IO;

namespace BilvaskRegistrering.Worker.LocalData;

internal sealed class SesongPriser
{
    public decimal StorSommer { get; init; }
    public decimal StorVinter { get; init; }
    public decimal LitenSommer { get; init; }
    public decimal LitenVinter { get; init; }

    public static SesongPriser LoadFromFolder(string folder)
    {
        var p = new SesongPriser();
        var path = Path.Combine(folder, "SesongPriser.csv");
        if (!File.Exists(path)) return p;

        // Format: Key;Value
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(';');
            if (parts.Length < 2) continue;

            var key = (parts[0] ?? "").Trim();
            var valRaw = (parts[1] ?? "").Trim();
            if (!decimal.TryParse(valRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) &&
                !decimal.TryParse(valRaw, NumberStyles.Any, CultureInfo.CurrentCulture, out val))
            {
                continue;
            }

            switch (key)
            {
                case "StorSommer": p = new SesongPriser { StorSommer = val, StorVinter = p.StorVinter, LitenSommer = p.LitenSommer, LitenVinter = p.LitenVinter }; break;
                case "StorVinter": p = new SesongPriser { StorSommer = p.StorSommer, StorVinter = val, LitenSommer = p.LitenSommer, LitenVinter = p.LitenVinter }; break;
                case "LitenSommer": p = new SesongPriser { StorSommer = p.StorSommer, StorVinter = p.StorVinter, LitenSommer = val, LitenVinter = p.LitenVinter }; break;
                case "LitenVinter": p = new SesongPriser { StorSommer = p.StorSommer, StorVinter = p.StorVinter, LitenSommer = p.LitenSommer, LitenVinter = val }; break;
            }
        }

        return p;
    }

    public decimal GetPrice(string? typeKjoretoy, string? sesong)
    {
        var type = (typeKjoretoy ?? "").Trim().ToLowerInvariant();
        var isStor = type.Contains("stor");
        var isVinter = (sesong ?? "").Trim().Equals("Vinter", StringComparison.OrdinalIgnoreCase);

        if (isStor && isVinter) return StorVinter;
        if (isStor) return StorSommer;
        if (isVinter) return LitenVinter;
        return LitenSommer;
    }
}
