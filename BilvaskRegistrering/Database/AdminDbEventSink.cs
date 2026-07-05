using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BilvaskRegistrering;
using BilvaskRegistrering.Database;

/// <summary>
/// Centralized helper for writing Admin (camera) events to Postgres.
/// - Inserts directly when DB is available
/// - Optionally queues events to local CSV when DB is down
/// - Drains queue opportunistically when DB is back
///
/// Fix (01.03.2026):
/// - Keep one cached sink instance (some call sites create a new sink per event)
/// - Flush queue on a background timer (so DB updates even without the next vehicle)
/// - Quick retry ~1.5s after a DB failure
/// </summary>
public sealed class AdminDbEventSink
{
    private readonly PostgresWriter _writer;
    private readonly AdminOfflineEventQueue? _queue;
    private readonly WashAuditCsv _audit;

    // Static so it works even if callers create a new sink per event.
    private static readonly PlateDedupeWindow _dedupe30 = new(TimeSpan.FromMinutes(30));

    // Singleton cache (prevents multiple timers + avoids rebuilding writer/queue each event)
    private static readonly object _cacheLock = new();
    private static AdminDbEventSink? _cached;
    private static string _cachedKey = "";

    // Background flush timer (threading timer - no UI dependency)
    private static System.Threading.Timer? _flushTimer;
    private static int _flushInProgress;

    // Per-instance guard (avoid overlapping drains for same queue)
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    private AdminDbEventSink(string connectionString, string queuePath, bool queueWhenDown)
    {
        _writer = new PostgresWriter(connectionString);
        _queue = queueWhenDown ? new AdminOfflineEventQueue(queuePath) : null;
        _audit = new WashAuditCsv(Path.Combine(AppConfig.DocFolder, "WashEvents_Audit.csv"));
    }

    public static AdminDbEventSink? TryCreateFromConfig()
    {
        if (!DbConfig.Enabled) return null;
        if (string.IsNullOrWhiteSpace(DbConfig.ConnectionString)) return null;

        var queueFile = DbConfig.QueueFile;
        var queuePath = Path.IsPathRooted(queueFile)
            ? queueFile
            : Path.Combine(AppConfig.DocFolder, queueFile);

        var key = $"{DbConfig.ConnectionString}|{queuePath}|{DbConfig.QueueWhenDown}";

        lock (_cacheLock)
        {
            if (_cached != null && string.Equals(_cachedKey, key, StringComparison.Ordinal))
                return _cached;

            _cached = new AdminDbEventSink(DbConfig.ConnectionString, queuePath, DbConfig.QueueWhenDown);
            _cachedKey = key;

            // (Re)start timer to flush queue every 5 seconds.
            try
            {
                _flushTimer?.Dispose();
            }
            catch { /* ignore */ }

            _flushTimer = new System.Threading.Timer(_ =>
            {
                var sink = _cached;
                if (sink == null) return;

                // Don't overlap flush runs.
                if (Interlocked.Exchange(ref _flushInProgress, 1) == 1) return;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await sink.TryFlushQueueAsync(CancellationToken.None);
                    }
                    catch { /* ignore */ }
                    finally
                    {
                        Interlocked.Exchange(ref _flushInProgress, 0);
                    }
                });
            }, null, dueTime: 5000, period: 5000);

            return _cached;
        }
    }

    public async Task<bool> TryInsertAsync(
        DateTimeOffset occurredAt,
        string plate,
        string? internnr,
        string? vehicleType,
        string? season,
        string? status,
        string? note,
        string? selskap,
        CancellationToken ct = default)
    {
        // Suppress duplicates (same plate within 30 minutes) across DB/CSV/queue.
        var plateNorm = PlateDedupeWindow.Normalize(plate);
        if (!_dedupe30.ShouldProcess(plateNorm, occurredAt.ToUniversalTime().UtcDateTime))
            return true; // treat as already handled

        // 1) Try to drain any queued events first.
        if (_queue != null)
            await TryFlushQueueAsync(ct);

        // 2) Try insert current event.
        var outcome = await _writer.TryInsertWashEventAsync(occurredAt, plateNorm, internnr, vehicleType, season, status, note, selskap, ct);

        if (outcome == DbInsertOutcome.Inserted)
        {
            // Audit only real inserts (avoid duplicates in audit CSV)
            try
            {
                _audit.Append(
                    occurredAtUtc: occurredAt.ToUniversalTime(),
                    app: "admin",
                    phase: "insert",
                    source: "kamera",
                    plate: plateNorm,
                    internnr: internnr,
                    vehicleType: vehicleType,
                    season: season,
                    status: status,
                    note: note,
                    selskap: selskap,
                    dbOk: true,
                    queued: false,
                    error: null);
            }
            catch { }
            return true;
        }

        if (outcome == DbInsertOutcome.Deduped)
        {
            // Already registered in DB (trigger dedupe) — do not audit/queue.
            return true;
        }

        // 3) If DB failed, audit and enqueue as fallback.
        try
        {
            _audit.Append(
                occurredAtUtc: occurredAt.ToUniversalTime(),
                app: "admin",
                phase: "db_fail",
                source: "kamera",
                plate: plateNorm,
                internnr: internnr,
                vehicleType: vehicleType,
                season: season,
                status: status,
                note: note,
                selskap: selskap,
                dbOk: false,
                queued: _queue != null,
                error: null);
        }
        catch { }

        _queue?.Enqueue(occurredAt, plateNorm, internnr, vehicleType, season, status, note, selskap);

        // Quick retry (so DB can catch up even if the next vehicle doesn't arrive)
        if (_queue != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500, CancellationToken.None);
                    await TryFlushQueueAsync(CancellationToken.None);
                }
                catch { /* ignore */ }
            });
        }

        return false;
    }

    private async Task TryFlushQueueAsync(CancellationToken ct)
    {
        if (_queue == null) return;

        // prevent concurrent drains
        if (!await _flushGate.WaitAsync(0, ct))
            return;

        try
        {
            var items = _queue.DrainAll();
            if (items.Count == 0) return;

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var ok = await _writer.TryInsertWashEventAsync(
                    it.occurredAt,
                    it.plate,
                    string.IsNullOrWhiteSpace(it.internnr) ? null : it.internnr,
                    string.IsNullOrWhiteSpace(it.vehicleType) ? null : it.vehicleType,
                    string.IsNullOrWhiteSpace(it.season) ? null : it.season,
                    string.IsNullOrWhiteSpace(it.status) ? null : it.status,
                    string.IsNullOrWhiteSpace(it.note) ? null : it.note,
                    string.IsNullOrWhiteSpace(it.selskap) ? null : it.selskap,
                    ct);

                if (ok == DbInsertOutcome.Inserted || ok == DbInsertOutcome.Deduped) continue;

                // DB still down → re-enqueue current + remaining and stop.
                for (int j = i; j < items.Count; j++)
                {
                    var r = items[j];
                    _queue.Enqueue(r.occurredAt, r.plate, r.internnr, r.vehicleType, r.season, r.status, r.note, r.selskap);
                }
                break;
            }
        }
        finally
        {
            try { _flushGate.Release(); } catch { /* ignore */ }
        }
    }
}
