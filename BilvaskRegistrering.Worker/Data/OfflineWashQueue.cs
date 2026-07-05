using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BilvaskRegistrering.Worker.Data;

/// <summary>
/// Simple, append-only local queue for camera events when DB is unavailable.
/// Stores JSONL in DokumentFolder\offline_wash_queue.jsonl
/// </summary>
public sealed class OfflineWashQueue
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public OfflineWashQueue(string dokumentFolder)
    {
        Directory.CreateDirectory(dokumentFolder);
        _filePath = Path.Combine(dokumentFolder, "offline_wash_queue.jsonl");
    }

    public async Task EnqueueAsync(QueuedWashEvent ev, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(ev);
        await _ioLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_filePath, json + Environment.NewLine, ct);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<List<QueuedWashEvent>> DrainAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_filePath)) return new List<QueuedWashEvent>();

            var lines = await File.ReadAllLinesAsync(_filePath, ct);
            File.Delete(_filePath);

            var res = new List<QueuedWashEvent>(lines.Length);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var ev = JsonSerializer.Deserialize<QueuedWashEvent>(line);
                    if (ev != null) res.Add(ev);
                }
                catch
                {
                    // ignore malformed line
                }
            }
            return res;
        }
        finally
        {
            _ioLock.Release();
        }
    }
}

public sealed record QueuedWashEvent(
    string Regnr,
    DateTime OccurredAtLocal,
    string Source,
    string? VehicleType,
    string? Season,
    string? Status,
    bool Unntak
);
