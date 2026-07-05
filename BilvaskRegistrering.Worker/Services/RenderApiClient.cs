using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace BilvaskRegistrering.Worker.Services;

internal static class RenderApiClient
{
    internal sealed record Result(bool Success, bool Deduped, bool Invalid, string Message);

    private sealed record Settings(bool Enabled, string BaseUrl, string ApiKey, int TimeoutSeconds)
    {
        public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BaseUrl);
    }

    public static bool IsConfigured()
    {
        try { return LoadSettings().IsConfigured; }
        catch { return false; }
    }

    public static async Task<Result> TrySendWashEventAsync(
        string plate,
        DateTime occurredAtUtc,
        string? season,
        string source,
        CancellationToken cancellationToken = default)
    {
        var settings = LoadSettings();
        if (!settings.IsConfigured)
            return new Result(false, false, false, "RenderApi is not configured");

        try
        {
            var baseUrl = settings.BaseUrl.Trim().TrimEnd('/');
            var url = baseUrl + "/api/camera/events";

            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds <= 0 ? 8 : settings.TimeoutSeconds)
            };

            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", settings.ApiKey.Trim());

            var dto = new
            {
                plate,
                occurredAtUtc = DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc),
                source,
                season,
                status = "unntak"
            };

            using var response = await client.PostAsJsonAsync(url, dto, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return new Result(false, false, false, $"HTTP {(int)response.StatusCode}: {raw}");

            var deduped = false;
            var inserted = false;
            var accepted = true;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.TryGetProperty("accepted", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    accepted = a.GetBoolean();
                if (root.TryGetProperty("inserted", out var i) && i.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    inserted = i.GetBoolean();
                if (root.TryGetProperty("deduped", out var d) && d.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    deduped = d.GetBoolean();
            }
            catch
            {
                inserted = true;
            }

            if (!accepted)
                return new Result(false, deduped, true, raw);

            return new Result(inserted || deduped || response.IsSuccessStatusCode, deduped, false, raw);
        }
        catch (Exception ex)
        {
            return new Result(false, false, false, ex.Message);
        }
    }

    private static Settings LoadSettings()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables(prefix: "BILVASK_")
            .Build();

        var enabled = config.GetValue("RenderApi:Enabled", false);
        var baseUrl = config.GetValue<string>("RenderApi:BaseUrl") ?? string.Empty;
        var apiKey = config.GetValue<string>("RenderApi:ApiKey") ?? string.Empty;
        var timeout = config.GetValue("RenderApi:TimeoutSeconds", 8);

        // Environment variable fallback without prefix, useful for quick tests.
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = Environment.GetEnvironmentVariable("BILVASK_RENDER_API_BASE_URL") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable("BILVASK_API_KEY") ?? string.Empty;
        if (!enabled && !string.IsNullOrWhiteSpace(baseUrl))
            enabled = true;

        return new Settings(enabled, baseUrl, apiKey, timeout);
    }
}
