using BilvaskRegistrering.Database;
using Npgsql;
using System.Diagnostics;

public sealed class PostgresWriter
{
    private readonly string _cs;

    public PostgresWriter(string connectionString)
    {
        _cs = connectionString;
    }

    private static string NormalizePlate(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return "";
        return plate.Trim().Replace(" ", "").ToUpperInvariant();
    }

    // Strict fleet format: 2 letters + 5 digits (e.g. EB75707)
    private static bool IsValidNorwegianPlate(string plateNorm)
    {
        if (string.IsNullOrWhiteSpace(plateNorm)) return false;
        plateNorm = NormalizePlate(plateNorm);
        if (plateNorm.Length != 7) return false;
        return char.IsLetter(plateNorm[0]) &&
               char.IsLetter(plateNorm[1]) &&
               char.IsDigit(plateNorm[2]) &&
               char.IsDigit(plateNorm[3]) &&
               char.IsDigit(plateNorm[4]) &&
               char.IsDigit(plateNorm[5]) &&
               char.IsDigit(plateNorm[6]);
    }


    /// <summary>
    /// Inserts a wash event into Postgres.
    /// Returns an outcome:
    ///  - Inserted: row inserted
    ///  - Deduped: insert suppressed (e.g. by BEFORE INSERT trigger returning NULL)
    ///  - Failed: DB error
    /// </summary>
    public async Task<DbInsertOutcome> TryInsertWashEventAsync(
        DateTimeOffset occurredAt,
        string plate,
        string? internnr,
        string? vehicleType,
        string? season,
        string? status,
        string? note,
        string? selskap,
        CancellationToken ct = default)
    {
        try
        {
            var plateNorm = NormalizePlate(plate);
            if (!IsValidNorwegianPlate(plateNorm))
                return DbInsertOutcome.Deduped;

            // Extra safety: do not insert unknown vehicles (internnr missing)
            if (string.IsNullOrWhiteSpace(internnr))
                return DbInsertOutcome.Deduped;

            await using var conn = new NpgsqlConnection(_cs);
            await conn.OpenAsync(ct);

            const string sql = @"
INSERT INTO public.wash_events
(occurred_at, plate, internnr, vehicle_type, season, status, note, source, selskap)
VALUES
(@occurred_at, @plate, @internnr, @vehicle_type, @season, @status, @note, @source, @selskap);";

            await using var cmd = new NpgsqlCommand(sql, conn);

            cmd.Parameters.AddWithValue("occurred_at", occurredAt);
            cmd.Parameters.AddWithValue("plate", plateNorm);
            cmd.Parameters.AddWithValue("internnr", (object?)internnr ?? DBNull.Value);
            cmd.Parameters.AddWithValue("vehicle_type", (object?)vehicleType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("season", (object?)season ?? DBNull.Value);
            cmd.Parameters.AddWithValue("status", (object?)status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);

            // ⬇⬇⬇ TO JEST NOWE ⬇⬇⬇
            cmd.Parameters.AddWithValue("source", "kamera");
            cmd.Parameters.AddWithValue("selskap", (object?)selskap ?? DBNull.Value);

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0 ? DbInsertOutcome.Inserted : DbInsertOutcome.Deduped;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Postgres insert failed: " + ex);
            return DbInsertOutcome.Failed;
        }
    }


    /// <summary>
    /// Recalculates the season field for wash_events from a given local (Oslo) date.
    /// Uses the same rule as the apps:
    ///  Sommer: [SommerStart .. VinterStart), Vinter: otherwise.
    /// </summary>
    public async Task<int> RecalculateSeasonAsync(
        DateTime fromLocalDate,
        int sommerStartMonth, int sommerStartDay,
        int vinterStartMonth, int vinterStartDay,
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_cs);
            await conn.OpenAsync(ct);

            // Compare using local Oslo date (wash_events.occurred_at is timestamptz).
            const string sql = @"
WITH t AS (
    SELECT
        id,
        (occurred_at AT TIME ZONE 'Europe/Oslo')::date AS local_d,
        EXTRACT(YEAR FROM (occurred_at AT TIME ZONE 'Europe/Oslo'))::int AS y
    FROM public.wash_events
    WHERE (occurred_at AT TIME ZONE 'Europe/Oslo')::date >= @from_date
)
UPDATE public.wash_events we
SET season = CASE
    WHEN @sommer_before_vinter THEN
        CASE
            WHEN t.local_d >= make_date(t.y, @sm, @sd)
             AND t.local_d <  make_date(t.y, @vm, @vd)
            THEN 'Sommer' ELSE 'Vinter'
        END
    ELSE
        CASE
            WHEN t.local_d >= make_date(t.y, @sm, @sd)
             OR t.local_d <  make_date(t.y, @vm, @vd)
            THEN 'Sommer' ELSE 'Vinter'
        END
END
FROM t
WHERE we.id = t.id;";

            var sommerBeforeVinter = (sommerStartMonth < vinterStartMonth) ||
                                     (sommerStartMonth == vinterStartMonth && sommerStartDay <= vinterStartDay);

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("from_date", fromLocalDate.Date);
            cmd.Parameters.AddWithValue("sommer_before_vinter", sommerBeforeVinter);
            cmd.Parameters.AddWithValue("sm", sommerStartMonth);
            cmd.Parameters.AddWithValue("sd", sommerStartDay);
            cmd.Parameters.AddWithValue("vm", vinterStartMonth);
            cmd.Parameters.AddWithValue("vd", vinterStartDay);

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Postgres season recalculation failed: " + ex);
            throw;
        }
    }

}
