using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BilvaskRegistrering;
namespace BilvaskRegistrering
{
    /// <summary>
    /// Wynik próby zapisu do DB (żeby logika mogła rozróżnić Insert/Dedupe/Fail).
    /// </summary>
    public enum DbInsertOutcome
    {
        Inserted,   // zapisano do DB
        Deduped,    // DB odrzuciła jako duplikat (trigger)
        Failed,     // błąd DB (np. brak połączenia)
        Queued      // zapisano do kolejki offline
    }

    /// <summary>
    /// Prosta deduplikacja w pamięci procesu: ten sam numer tylko raz w oknie czasu.
    /// Działa przed zapisem do CSV/logów, żeby pliki nie rosły duplikatami.
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
            if (string.IsNullOrWhiteSpace(plateRaw)) return "";
            var s = plateRaw.Trim().ToUpperInvariant();
            s = _ws.Replace(s, ""); // usuń spacje
            return s;
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
}