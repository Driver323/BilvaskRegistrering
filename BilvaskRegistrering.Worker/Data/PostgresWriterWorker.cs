using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace BilvaskRegistrering.Worker.Data;

internal sealed class PostgresWriterWorker
{
    private readonly string _cs;

    public PostgresWriterWorker(string connectionString)
    {
        _cs = connectionString;
    }

    public async Task<bool> TryInsertWashEventAsync(
        string regnr,
        string? internnr,
        string? selskap,
        string? typeKjoretoy,
        string sesong,
        string status,
        string kilde,
        string? note,
        DateTime? registeredAtUtc,
        CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_cs);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO public.wash_events
(regnr, internnr, selskap, type_kjoretoy, sesong, status, kilde, note, registered_at)
VALUES
(@regnr, @internnr, @selskap, @type_kjoretoy, @sesong, @status, @kilde, @note, @registered_at)
";

            cmd.Parameters.AddWithValue("regnr", regnr);
            cmd.Parameters.AddWithValue("internnr", (object?)internnr ?? DBNull.Value);
            cmd.Parameters.AddWithValue("selskap", (object?)selskap ?? DBNull.Value);
            cmd.Parameters.AddWithValue("type_kjoretoy", (object?)typeKjoretoy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("sesong", sesong);
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("kilde", kilde);
            cmd.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("registered_at", registeredAtUtc ?? DateTime.UtcNow);

            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
