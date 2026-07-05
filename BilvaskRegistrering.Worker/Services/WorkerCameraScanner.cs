using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BilvaskRegistrering.Worker.Services;

/// <summary>
/// Lightweight ITS API listener (HTTP POST) that extracts plate numbers from payloads.
/// This is the "same" ingest method as the admin app, but without any camera preview.
/// </summary>
public sealed class WorkerCameraScanner : IDisposable
{
    private readonly string _prefix; // must end with /
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event EventHandler<string>? PlateRecognized;
    public event EventHandler<string>? Debug;

    public WorkerCameraScanner(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("prefix is empty", nameof(prefix));

        // HttpListener requires trailing slash.
        _prefix = prefix.EndsWith("/") ? prefix : (prefix + "/");
    }

    public void Start()
    {
        if (_listener != null) return;

        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);
        _listener.Start();

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        EmitDebug($"ITS listener started: {_prefix}");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }

        _cts = null;
        _listener = null;
        _loop = null;

        EmitDebug("ITS listener stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                EmitDebug("ITS listener error: " + ex.Message);
                await Task.Delay(250, ct).ContinueWith(_ => { }, TaskScheduler.Default);
                continue;
            }

            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 405;
                await WriteTextAsync(ctx, "Only POST");
                return;
            }

            var raw = await ReadBodyAsStringAsync(ctx.Request);
            if (string.IsNullOrWhiteSpace(raw))
            {
                ctx.Response.StatusCode = 400;
                await WriteTextAsync(ctx, "empty body");
                return;
            }

            var plate = TryParsePlateFromJson(raw) ?? TryParsePlateFromText(raw);
            if (!string.IsNullOrWhiteSpace(plate))
            {
                plate = plate.Trim().ToUpperInvariant();
                EmitDebug("ITS plate: " + plate);
                try { PlateRecognized?.Invoke(this, plate); } catch { }
            }
            else
            {
                EmitDebug("ITS POST received (no plate found)");
            }

            ctx.Response.StatusCode = 200;
            await WriteTextAsync(ctx, "OK");
        }
        catch (Exception ex)
        {
            EmitDebug("ITS handle error: " + ex.Message);
            try
            {
                ctx.Response.StatusCode = 500;
                await WriteTextAsync(ctx, "ERR");
            }
            catch { }
        }
        finally
        {
            try { ctx.Response.OutputStream.Close(); } catch { }
        }
    }

    private static async Task<string> ReadBodyAsStringAsync(HttpListenerRequest req)
    {
        using var ms = new System.IO.MemoryStream();
        await req.InputStream.CopyToAsync(ms);
        var bytes = ms.ToArray();
        if (bytes.Length == 0) return "";

        try { return Encoding.UTF8.GetString(bytes); }
        catch { return Encoding.Default.GetString(bytes); }
    }

    private static async Task WriteTextAsync(HttpListenerContext ctx, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    private static string? TryParsePlateFromJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var candidates = new[] { "PlateNumber", "plateNo", "PlateNo", "PlateNo1", "Plate" };
            foreach (var key in candidates)
            {
                if (TryFindJsonString(root, key, out var value) && !string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        catch
        {
            // not JSON
        }

        return null;
    }

    private static bool TryFindJsonString(JsonElement element, string key, out string? value)
    {
        value = null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            value = prop.Value.GetString();
                            return true;
                        }

                        if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            if (TryFindJsonString(prop.Value, "PlateNumber", out value) ||
                                TryFindJsonString(prop.Value, "plateNo", out value) ||
                                TryFindJsonString(prop.Value, "PlateNo", out value))
                                return true;
                        }
                    }

                    if (TryFindJsonString(prop.Value, key, out value) && !string.IsNullOrWhiteSpace(value))
                        return true;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindJsonString(item, key, out value) && !string.IsNullOrWhiteSpace(value))
                        return true;
                }
                break;
        }

        return false;
    }

    private static string? TryParsePlateFromText(string raw)
    {
        var m = Regex.Match(raw,
            @"(?:PlateNumber|plateNo|PlateNo)""?\s*[:=]\s*""?([A-Z0-9-]{3,12})",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(raw, @"\b([A-Z]{1,3}[0-9]{1,5}[A-Z]{0,2})\b");
        if (m.Success) return m.Groups[1].Value;

        return null;
    }

    private void EmitDebug(string msg)
    {
        try { Debug?.Invoke(this, msg); } catch { }
    }

    public void Dispose()
    {
        Stop();
    }
}
