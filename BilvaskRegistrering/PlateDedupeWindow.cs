using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BilvaskRegistrering;

/// <summary>
/// Prosta deduplikacja w pamięci procesu: ten sam numer tylko raz w oknie czasu.
/// Używane do tłumienia duplikatów przed zapisem do CSV/logów (DB ma własny trigger).
/// </summary>
public sealed class PlateDedupeWindow
{
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, DateTime> _lastSeenUtc = new();
    private DateTime _lastCleanupUtc = DateTime.UtcNow;

    private static readonly Regex _ws = new(@"\s+", RegexOptions.Compiled);

    public PlateDedupeWindow(TimeSpan window)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _window = window;
    }

    public static string Normalize(string? plateRaw)
    {
        if (string.IsNullOrWhiteSpace(plateRaw)) return string.Empty;
        var s = plateRaw.Trim().ToUpperInvariant();
        s = _ws.Replace(s, ""); // usuń spacje
        s = s.Replace("-", "");
        return s;
    }

    /// <summary>
    /// Kompatybilna nazwa metody używana w kodzie (AdminDbEventSink).
    /// Zwraca false jeśli tablica była już widziana w oknie czasu.
    /// </summary>
    public bool ShouldProcess(string? plateRaw, DateTime utcNow)
    {
        return ShouldAccept(plateRaw, utcNow, out _);
    }

    /// <summary>
    /// Zwraca true jeśli wpis powinien przejść (nie jest duplikatem w oknie czasu).
    /// </summary>
    public bool ShouldAccept(string? plateRaw, DateTime utcNow, out string normalizedPlate)
    {
        normalizedPlate = Normalize(plateRaw);
        if (normalizedPlate.Length == 0) return true;

        CleanupOccasionally(utcNow);

        if (_lastSeenUtc.TryGetValue(normalizedPlate, out var last))
        {
            if (utcNow - last < _window)
                return false;
        }

        _lastSeenUtc[normalizedPlate] = utcNow;
        return true;
    }

    public void Clear() => _lastSeenUtc.Clear();

    private void CleanupOccasionally(DateTime utcNow)
    {
        // żeby słownik nie rósł w nieskończoność – czyść co kilka minut
        if (utcNow - _lastCleanupUtc < TimeSpan.FromMinutes(5))
            return;

        _lastCleanupUtc = utcNow;

        var cutoff = utcNow - (_window + TimeSpan.FromMinutes(5));
        foreach (var kv in _lastSeenUtc)
        {
            if (kv.Value < cutoff)
                _lastSeenUtc.TryRemove(kv.Key, out _);
        }
    }
}
