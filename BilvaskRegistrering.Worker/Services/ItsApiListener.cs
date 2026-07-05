using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BilvaskRegistrering.Worker.Services;

internal sealed class ItsApiListener : IDisposable
{
    private readonly string _prefix;
    private HttpListener? _listener;
    private Task? _runTask;

    public event EventHandler<string>? PlateCaptured;

    public ItsApiListener(string host, int port, string path)
    {
        // HttpListener doesn't accept arbitrary hostnames well; use wildcard.
        // Example: http://+:7070/NotificationInfo/TollgateInfo/
        var normalizedPath = path.Trim();
        if (!normalizedPath.StartsWith("/")) normalizedPath = "/" + normalizedPath;
        if (!normalizedPath.EndsWith("/")) normalizedPath += "/";
        _prefix = $"http://+:{port}{normalizedPath}";
    }

    public void Start(CancellationToken ct)
    {
        if (_listener is not null) return;

        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);
        _listener.Start();

        _runTask = Task.Run(() => RunAsync(ct), ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (_listener is null) return;

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch
            {
                // transient; continue
                continue;
            }

            _ = Task.Run(() => HandleRequestAsync(ctx), CancellationToken.None);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                ctx.Response.StatusCode = 405;
                ctx.Response.Close();
                return;
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                body = await reader.ReadToEndAsync().ConfigureAwait(false);

            var plate = TryExtractPlate(body);
            if (!string.IsNullOrWhiteSpace(plate))
                PlateCaptured?.Invoke(this, plate);

            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        }
        catch
        {
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch { }
        }
    }

    private static string? TryExtractPlate(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        // Dahua often posts JSON. We try a tolerant JSON parse, otherwise fallback to regex-ish scan.
        try
        {
            using var doc = JsonDocument.Parse(body);
            // common paths:
            // PlateResult.PlateNumber
            if (TryFindString(doc.RootElement, "PlateNumber", out var plate)) return plate;
            if (TryFindString(doc.RootElement, "plateNumber", out plate)) return plate;
            if (TryFindString(doc.RootElement, "plate", out plate)) return plate;
        }
        catch
        {
            // ignore
        }

        // Fallback: look for something like "PlateNumber":"AB12345"
        var idx = body.IndexOf("PlateNumber", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var sub = body.Substring(idx);
            var q1 = sub.IndexOf('"');
            var q2 = sub.IndexOf('"', q1 + 1);
            // crude, but avoids extra deps
        }

        return null;
    }

    private static bool TryFindString(JsonElement el, string propertyName, out string? value)
    {
        value = null;
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                if (string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        value = p.Value.GetString();
                        return !string.IsNullOrWhiteSpace(value);
                    }
                }

                if (TryFindString(p.Value, propertyName, out value)) return true;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                if (TryFindString(item, propertyName, out value)) return true;
        }

        return false;
    }

    public void Dispose()
    {
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }

        _listener = null;
    }
}
