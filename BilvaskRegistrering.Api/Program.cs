using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "BILVASK_");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSingleton<BilvaskDatabase>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BilvaskDatabase>();
    await db.EnsureSchemaAsync();
}

app.MapGet("/", async (BilvaskDatabase db) =>
{
    var status = await db.GetStatusAsync();
    var html = """
<!doctype html>
<html lang="no">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Bilvask API</title>
  <style>
    body{font-family:Arial,sans-serif;margin:32px;background:#f6f7f9;color:#1f2937}.card{max-width:880px;background:white;border-radius:16px;padding:24px;box-shadow:0 8px 26px #0001}code{background:#eef2f7;padding:2px 6px;border-radius:6px}.ok{color:#15803d}.bad{color:#b91c1c}table{border-collapse:collapse;width:100%;margin-top:16px}td,th{border-bottom:1px solid #e5e7eb;text-align:left;padding:8px}
  </style>
</head>
<body>
  <div class="card">
    <h1>Bilvask API działa</h1>
    <p>Status bazy: <b class="{{DB_CLASS}}">{{DB_TEXT}}</b></p>
    <p>Liczba zapisów kamery: <b>{{COUNT}}</b></p>
    <p>Endpoint kamery: <code>POST /api/camera/events</code></p>
    <p>Lista ostatnich zapisów: <a href="/api/washes">/api/washes</a></p>
  </div>
</body>
</html>
""";
    html = html.Replace("{{DB_CLASS}}", status.DatabaseOk ? "ok" : "bad")
               .Replace("{{DB_TEXT}}", status.DatabaseOk ? "OK" : WebUtility.HtmlEncode(status.Error ?? "Błąd"))
               .Replace("{{COUNT}}", status.WashEventsCount.ToString(CultureInfo.InvariantCulture));
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/health", async (BilvaskDatabase db) =>
{
    var status = await db.GetStatusAsync();
    return status.DatabaseOk ? Results.Ok(status) : Results.Problem(status.Error ?? "Database error", statusCode: 503);
});

app.MapGet("/api/status", async (BilvaskDatabase db) => Results.Ok(await db.GetStatusAsync()));

app.MapGet("/api/washes", async (int? limit, BilvaskDatabase db) =>
{
    var rows = await db.GetWashesAsync(Math.Clamp(limit ?? 100, 1, 500));
    return Results.Ok(rows);
});

app.MapPost("/api/camera/events", async (HttpContext http, CameraEventRequest request, BilvaskDatabase db, IConfiguration config) =>
{
    if (!ApiKeyIsValid(http, config))
        return Results.Unauthorized();

    var result = await db.InsertCameraEventAsync(request);
    if (!result.Accepted)
        return Results.BadRequest(result);

    return Results.Ok(result);
});

app.Run();

static bool ApiKeyIsValid(HttpContext http, IConfiguration config)
{
    var expected = config["BILVASK_API_KEY"] ?? config["ApiKey"] ?? config["Bilvask:ApiKey"];
    if (string.IsNullOrWhiteSpace(expected))
        return true; // Easy first test. Set BILVASK_API_KEY on Render before real use.

    if (http.Request.Headers.TryGetValue("X-API-Key", out var header) && header.ToString() == expected)
        return true;

    var auth = http.Request.Headers.Authorization.ToString();
    if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && auth[7..].Trim() == expected)
        return true;

    return false;
}

public sealed record CameraEventRequest(
    string? Plate,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset? OccurredAtLocal,
    string? Source,
    string? Season,
    string? Internnr,
    string? VehicleType,
    string? Status,
    JsonElement? Payload);

public sealed record CameraEventResponse(
    bool Accepted,
    bool Inserted,
    bool Deduped,
    long? Id,
    string Plate,
    DateTimeOffset OccurredAtUtc,
    string Message);

public sealed record WashRow(
    long Id,
    DateTimeOffset OccurredAt,
    string Plate,
    string? Internnr,
    string? VehicleType,
    string? Season,
    string? Status,
    string? Source,
    DateTimeOffset CreatedAt);

public sealed record ApiStatus(bool DatabaseOk, long WashEventsCount, DateTimeOffset ServerTimeUtc, string? Error);

public sealed class BilvaskDatabase
{
    private readonly IConfiguration _config;
    private readonly ILogger<BilvaskDatabase> _logger;

    public BilvaskDatabase(IConfiguration config, ILogger<BilvaskDatabase> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync()
    {
        await using var conn = new NpgsqlConnection(GetConnectionString());
        await conn.OpenAsync();

        var sql = """
CREATE TABLE IF NOT EXISTS public.wash_events (
  id BIGSERIAL PRIMARY KEY,
  occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  plate TEXT NOT NULL,
  internnr TEXT NULL,
  selskap TEXT NULL,
  vehicle_type TEXT NULL,
  season TEXT NULL,
  status TEXT NULL,
  cost NUMERIC(12,2) NULL,
  note TEXT NULL,
  source TEXT NULL,
  payload JSONB NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS internnr TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS selskap TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS vehicle_type TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS season TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS status TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS cost NUMERIC(12,2) NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS note TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS source TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS payload JSONB NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT now();

CREATE INDEX IF NOT EXISTS ix_wash_events_occurred_at ON public.wash_events (occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_wash_events_plate_time ON public.wash_events (plate, occurred_at DESC);

CREATE TABLE IF NOT EXISTS public.ansatter (
  id BIGSERIAL PRIMARY KEY,
  ansattnummer TEXT NULL,
  navn TEXT NOT NULL,
  pin TEXT NULL,
  aktiv BOOLEAN NOT NULL DEFAULT TRUE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS public.wash_confirmations (
  id BIGSERIAL PRIMARY KEY,
  wash_event_id BIGINT NOT NULL REFERENCES public.wash_events(id) ON DELETE CASCADE,
  ansatt_id BIGINT NOT NULL REFERENCES public.ansatter(id),
  type_vask TEXT NOT NULL DEFAULT 'Innvendig/uttvendig vask',
  uregistrert_skade TEXT NULL,
  confirmed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT uq_wash_confirmations UNIQUE (wash_event_id)
);

CREATE OR REPLACE VIEW public.worker_active_washes AS
SELECT
  we.id,
  we.occurred_at AS dato,
  we.internnr,
  we.plate AS reg_nr,
  we.vehicle_type AS type_kjoretoy,
  wc.type_vask,
  we.season AS season,
  we.status,
  wc.uregistrert_skade,
  a.navn AS ansatt,
  wc.confirmed_at
FROM public.wash_events we
LEFT JOIN public.wash_confirmations wc ON wc.wash_event_id = we.id
LEFT JOIN public.ansatter a ON a.id = wc.ansatt_id
ORDER BY we.occurred_at DESC;
""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<ApiStatus> GetStatusAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(GetConnectionString());
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM public.wash_events;", conn);
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            return new ApiStatus(true, count, DateTimeOffset.UtcNow, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database status check failed");
            return new ApiStatus(false, 0, DateTimeOffset.UtcNow, ex.Message);
        }
    }

    public async Task<IReadOnlyList<WashRow>> GetWashesAsync(int limit)
    {
        var rows = new List<WashRow>();
        await using var conn = new NpgsqlConnection(GetConnectionString());
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("""
SELECT id, occurred_at, plate, internnr, vehicle_type, season, status, source, created_at
FROM public.wash_events
ORDER BY occurred_at DESC
LIMIT @limit;
""", conn);
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var occurredAt = DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(1), DateTimeKind.Utc);
            var createdAt = DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(8), DateTimeKind.Utc);
            rows.Add(new WashRow(
                reader.GetInt64(0),
                new DateTimeOffset(occurredAt),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                new DateTimeOffset(createdAt)));
        }

        return rows;
    }

    public async Task<CameraEventResponse> InsertCameraEventAsync(CameraEventRequest request)
    {
        var plate = NormalizePlate(request.Plate);
        var occurredAt = request.OccurredAtUtc ?? request.OccurredAtLocal?.ToUniversalTime() ?? DateTimeOffset.UtcNow;

        if (!IsValidNorwegianPlate(plate))
            return new CameraEventResponse(false, false, false, null, plate, occurredAt, "Invalid plate");

        await using var conn = new NpgsqlConnection(GetConnectionString());
        await conn.OpenAsync();

        var duplicateFrom = occurredAt.AddMinutes(-30);
        var duplicateTo = occurredAt.AddMinutes(30);

        await using (var dupCmd = new NpgsqlCommand("""
SELECT id
FROM public.wash_events
WHERE plate = @plate
  AND occurred_at BETWEEN @from AND @to
ORDER BY occurred_at DESC
LIMIT 1;
""", conn))
        {
            dupCmd.Parameters.AddWithValue("plate", plate);
            dupCmd.Parameters.AddWithValue("from", duplicateFrom.UtcDateTime);
            dupCmd.Parameters.AddWithValue("to", duplicateTo.UtcDateTime);
            var duplicateId = await dupCmd.ExecuteScalarAsync();
            if (duplicateId is not null)
            {
                var id = Convert.ToInt64(duplicateId, CultureInfo.InvariantCulture);
                return new CameraEventResponse(true, false, true, id, plate, occurredAt, "Duplicate within 30 minutes");
            }
        }

        var payloadJson = JsonSerializer.Serialize(request, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        await using var cmd = new NpgsqlCommand("""
INSERT INTO public.wash_events
  (occurred_at, plate, internnr, vehicle_type, season, status, source, payload)
VALUES
  (@occurred_at, @plate, @internnr, @vehicle_type, @season, @status, @source, @payload)
RETURNING id;
""", conn);
        cmd.Parameters.AddWithValue("occurred_at", occurredAt.UtcDateTime);
        cmd.Parameters.AddWithValue("plate", plate);
        cmd.Parameters.AddWithValue("internnr", (object?)request.Internnr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("vehicle_type", (object?)request.VehicleType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("season", (object?)request.Season ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", string.IsNullOrWhiteSpace(request.Status) ? "unntak" : request.Status!);
        cmd.Parameters.AddWithValue("source", string.IsNullOrWhiteSpace(request.Source) ? "render_api" : request.Source!);
        cmd.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payloadJson;

        var newId = Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        return new CameraEventResponse(true, true, false, newId, plate, occurredAt, "Inserted");
    }

    private string GetConnectionString()
    {
        var raw = _config.GetConnectionString("DefaultConnection")
                  ?? _config.GetConnectionString("ConnectionString")
                  ?? _config["DATABASE_URL"]
                  ?? _config["DatabaseUrl"]
                  ?? _config["Postgres:ConnectionString"];

        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Missing database connection. Set DATABASE_URL or ConnectionStrings__DefaultConnection.");

        return raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
               raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
            ? ConvertDatabaseUrlToNpgsql(raw)
            : raw;
    }

    private static string ConvertDatabaseUrlToNpgsql(string databaseUrl)
    {
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':', 2);
        var username = userInfo.Length > 0 ? WebUtility.UrlDecode(userInfo[0]) : "";
        var password = userInfo.Length > 1 ? WebUtility.UrlDecode(userInfo[1]) : "";
        var database = uri.AbsolutePath.TrimStart('/');

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = database,
            Username = username,
            Password = password,
            SslMode = SslMode.Require,
            TrustServerCertificate = true,
            Timeout = 15,
            CommandTimeout = 30
        };
        return builder.ConnectionString;
    }

    private static string NormalizePlate(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return string.Empty;
        var chars = plate.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }

    private static bool IsValidNorwegianPlate(string plate)
    {
        if (plate.Length != 7) return false;
        return char.IsLetter(plate[0]) && char.IsLetter(plate[1]) &&
               char.IsDigit(plate[2]) && char.IsDigit(plate[3]) && char.IsDigit(plate[4]) &&
               char.IsDigit(plate[5]) && char.IsDigit(plate[6]);
    }
}
