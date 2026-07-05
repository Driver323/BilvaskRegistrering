using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BilvaskRegistrering.Worker.Services;

internal sealed class OfflineWashQueue
{
    private readonly string _filePath;
    private readonly object _lock = new();

    internal sealed record QueuedWashEvent(DateTime Ts, string Plate, string Source)
    {
        // Back-compat with earlier code that expected a Timestamp property name.
        public DateTime Timestamp => Ts;
    }

    public OfflineWashQueue(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
    }

    public void Enqueue(QueuedWashEvent evt)
    {
        lock (_lock)
        {
            File.AppendAllText(_filePath, JsonSerializer.Serialize(evt) + Environment.NewLine);
        }
    }

    /// <summary>
    /// Returns all queued events and clears the queue.
    /// (Kept for compatibility with older ingestor code.)
    /// </summary>
    public IReadOnlyList<QueuedWashEvent> DequeueAll()
    {
        var items = ReadAll();
        if (items.Count > 0) Clear();
        return items;
    }

    public IReadOnlyList<QueuedWashEvent> ReadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath)) return Array.Empty<QueuedWashEvent>();

            var list = new List<QueuedWashEvent>();
            foreach (var line in File.ReadAllLines(_filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize<QueuedWashEvent>(line);
                    if (item is not null) list.Add(item);
                }
                catch { }
            }
            return list;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
        }
    }
}
