using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace BilvaskRegistrering.Worker.Services;

internal sealed class WorkerPlateIngestor
{
    private readonly string _connStr;
    private readonly OfflineWashQueue _queue;

    public WorkerPlateIngestor(string connStr, OfflineWashQueue queue)
    {
        _connStr = connStr;
        _queue = queue;
    }

    public async Task<bool> TryInsertAsync(DateTime ts, string plate, string source, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO wash_events(ts, plate, source) VALUES (@ts, @plate, @source);";
            cmd.Parameters.AddWithValue("ts", ts);
            cmd.Parameters.AddWithValue("plate", plate);
            cmd.Parameters.AddWithValue("source", source);
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch
        {
            _queue.Enqueue(new OfflineWashQueue.QueuedWashEvent(ts, plate, source));
            return false;
        }
    }

    public async Task<int> FlushQueueAsync(CancellationToken ct)
    {
        var items = _queue.DequeueAll();
        if (items.Count == 0) return 0;

        int ok = 0;
        foreach (var it in items)
        {
            if (await TryInsertAsync(it.Timestamp, it.Plate, it.Source, ct)) ok++;
        }
        return ok;
    }
}
