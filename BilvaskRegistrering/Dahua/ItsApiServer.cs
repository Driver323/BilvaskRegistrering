using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace BilvaskRegistrering.Dahua;

/// <summary>
/// In-process HTTP server for Dahua "Platform Access / ITSAPI".
/// 
/// Important: We intentionally use Kestrel (ASP.NET Core) instead of HttpListener,
/// so you DON'T need netsh urlacl (and you avoid Error 1332 issues on Windows).
/// </summary>
public sealed class ItsApiServer
{
    public event EventHandler<string>? PlateDetected;
    public event EventHandler<string>? Debug;

    private readonly int _port;
    private readonly string _path;

    private IHost? _host;

    public ItsApiServer(int port, string path)
    {
        _port = port <= 0 ? 7070 : port;
        _path = NormalizePath(path);
    }

    public void Start()
    {
        if (_host != null) return;

        var listenUrl = $"http://0.0.0.0:{_port}";

        _host = Host.CreateDefaultBuilder()
            // Avoid noisy logging windows console
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseKestrel();
                webBuilder.UseUrls(listenUrl);
                webBuilder.Configure(app =>
                {
                    app.UseRouting();

                    app.UseEndpoints(endpoints =>
                    {
                        // Dahua will POST to the exact path configured in Platform Access.
                        endpoints.MapPost(_path, HandlePostAsync);

                        // Simple health check / sanity test
                        endpoints.MapGet("/", async ctx =>
                        {
                            ctx.Response.ContentType = "text/plain";
                            await ctx.Response.WriteAsync($"ITSAPI server OK. POST to {_path}\n");
                        });
                    });
                });
            })
            .Build();

        _ = _host.StartAsync();
        EmitDebug($"ITSAPI server listening on {listenUrl}{_path}");
    }

    public void Stop()
    {
        try
        {
            _host?.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch { }

        try
        {
            _host?.Dispose();
        }
        catch { }

        _host = null;
    }

    private async Task HandlePostAsync(HttpContext ctx)
    {
        try
        {
            var raw = await ReadBodyAsStringAsync(ctx);
            if (string.IsNullOrWhiteSpace(raw))
            {
                EmitDebug("ITSAPI POST received but body is empty");
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("empty body");
                return;
            }

            // Try JSON first, fallback to heuristic regex.
            var plate = TryParsePlateFromJson(raw) ?? TryParsePlateFromText(raw);

            if (!string.IsNullOrWhiteSpace(plate))
            {
                plate = plate.Trim().ToUpperInvariant();
                EmitDebug($"ITSAPI plate: {plate}");
                try { PlateDetected?.Invoke(this, plate); } catch { }
            }
            else
            {
                // Keep debug light (do NOT spam huge payloads)
                EmitDebug("ITSAPI POST received (no plate found)");
            }

            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("OK");
        }
        catch (Exception ex)
        {
            EmitDebug("ITSAPI error: " + ex.Message);
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsync("ERR");
        }
    }

    private static async Task<string> ReadBodyAsStringAsync(HttpContext ctx)
    {
        // Dahua typically sends JSON with UTF-8. Sometimes it uses form-encoded.
        // We keep it simple and just read raw body.
        ctx.Request.EnableBuffering();
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        ms.Position = 0;

        var bytes = ms.ToArray();
        if (bytes.Length == 0) return "";

        // Prefer UTF-8; fallback to ANSI if needed.
        try { return Encoding.UTF8.GetString(bytes); }
        catch { return Encoding.Default.GetString(bytes); }
    }

    private static string? TryParsePlateFromJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // Common Dahua payload patterns (varies by firmware/model)
            // We try a few known paths:
            // - ..."PlateNumber":"ABC123"
            // - ..."plateNo":"ABC123"
            // - ..."PlateNo":"ABC123"
            // - ..."Plate": { "PlateNumber": ... }

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

                        // Some firmwares nest it
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
        // Typical patterns seen in raw payloads / form fields
        // Keep permissive but not too greedy.
        // Use verbatim strings so regex escapes like \s and \b don't turn into invalid C# escape sequences.
        // NOTE: In verbatim strings (@""), quotes are escaped by doubling them (""), not with backslash.
        // Using \" inside a verbatim string breaks compilation (the quote terminates the string).
        // We therefore match optional quotes with ""? instead of \"?.
        var m = Regex.Match(raw,
            @"(?:PlateNumber|plateNo|PlateNo)""?\s*[:=]\s*""?([A-Z0-9-]{3,12})",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        // Last resort: look for something that resembles a plate in the body.
        // (Adjust if your country format is different.)
        m = Regex.Match(raw, @"\b([A-Z]{1,3}[0-9]{1,5}[A-Z]{0,2})\b");
        if (m.Success) return m.Groups[1].Value;

        return null;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/NotificationInfo/TollgateInfo";
        path = path.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        return path;
    }

    private void EmitDebug(string msg)
    {
        try { Debug?.Invoke(this, msg); } catch { }
    }
}
