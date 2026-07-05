using System;
using System.Collections.Generic;

namespace BilvaskRegistrering.Worker.Services;

/// <summary>
/// In-memory dedupe helper to suppress repeated registrations of the same plate
/// within a time window (e.g. 30 minutes).
/// </summary>
internal sealed class PlateDedupeWindow
{
    private readonly TimeSpan _window;
    private readonly Dictionary<string, DateTime> _lastSeenUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public PlateDedupeWindow(TimeSpan window)
    {
        _window = window;
    }

    public static string Normalize(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return string.Empty;
        Span<char> buf = stackalloc char[plate.Length];
        int n = 0;
        foreach (var c in plate)
        {
            if (char.IsWhiteSpace(c) || c == '-') continue;
            buf[n++] = char.ToUpperInvariant(c);
        }
        return new string(buf.Slice(0, n));
    }

    public bool ShouldProcess(string? plate, DateTime utcNow)
    {
        var key = Normalize(plate);
        if (string.IsNullOrEmpty(key)) return true;
        lock (_gate)
        {
            if (_lastSeenUtc.TryGetValue(key, out var last) && (utcNow - last) < _window)
                return false;
            _lastSeenUtc[key] = utcNow;

            if (_lastSeenUtc.Count > 5000)
            {
                var cutoff = utcNow - TimeSpan.FromMinutes(Math.Max(60, _window.TotalMinutes * 2));
                var remove = new List<string>();
                foreach (var kv in _lastSeenUtc)
                    if (kv.Value < cutoff) remove.Add(kv.Key);
                foreach (var k in remove) _lastSeenUtc.Remove(k);
            }
            return true;
        }
    }
}
