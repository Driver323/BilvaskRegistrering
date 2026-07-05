using System;
using System.Threading.Tasks;
using Npgsql;
using BilvaskRegistrering.Models;

namespace BilvaskRegistrering.Data;

internal static class PostgresSink
{
    private static volatile bool _schemaEnsured;

    public static async Task EnsureSchemaAsync(string connStr)
    {
        if (_schemaEnsured) return;
        if (string.IsNullOrWhiteSpace(connStr)) return;

        using (var conn = new NpgsqlConnection(connStr))
        {
            await conn.OpenAsync();

            var sql = @"
CREATE TABLE IF NOT EXISTS wash_events (
  id BIGSERIAL PRIMARY KEY,
  occurred_at TIMESTAMPTZ NOT NULL,
  plate TEXT NOT NULL,
  selskap TEXT NULL,
  vehicle_type TEXT NULL,
  season TEXT NULL,
  status TEXT NULL,
  cost NUMERIC(12,2) NULL
);";

            using (var cmd = new NpgsqlCommand(sql, conn))
                await cmd.ExecuteNonQueryAsync();
        }

        _schemaEnsured = true;
    }

    public static async Task InsertWashAsync(string connStr, VaskeHendelse h)
    {
        if (h == null) return;
        if (string.IsNullOrWhiteSpace(connStr)) return;

        await EnsureSchemaAsync(connStr);

        using (var conn = new NpgsqlConnection(connStr))
        {
            await conn.OpenAsync();

            var sql = @"
INSERT INTO wash_events (occurred_at, plate, selskap, vehicle_type, season, status, cost)
VALUES (@t, @plate, @selskap, @type, @season, @status, @cost);";

            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("t", h.OccurredAtUtc);
                cmd.Parameters.AddWithValue("plate", h.Plate ?? "");
                cmd.Parameters.AddWithValue("selskap", (object?)h.Selskap ?? DBNull.Value);
                cmd.Parameters.AddWithValue("type", (object?)h.VehicleType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("season", (object?)h.Season ?? DBNull.Value);
                cmd.Parameters.AddWithValue("status", (object?)h.Status ?? DBNull.Value);
                cmd.Parameters.AddWithValue("cost", (object?)h.Cost ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}
