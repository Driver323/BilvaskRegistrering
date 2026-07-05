using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using BilvaskRegistrering.Worker.Models;

namespace BilvaskRegistrering.Worker.Data;

internal static class WorkerDb
{
    /// <summary>
    /// Validates and normalizes the connection string.
    /// If parsing fails, throws a descriptive exception so the UI shows the real reason.
    /// </summary>
    private static string NormalizeOrThrow(string connStr)
    {
        if (string.IsNullOrWhiteSpace(connStr))
            throw new InvalidOperationException(
                "Connection string is empty. Check appsettings.json -> ConnectionStrings:Worker.");

        // Npgsql accepts many keywords, but we still normalize to avoid subtle typos (e.g. 'Ssl Mode' vs 'SSL Mode').
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connStr);

            // NOTE: Npgsql deprecated TrustServerCertificate (now a no-op), so we avoid touching it.

            // Make timeouts explicit so 'DB offline' happens quickly if it cannot authenticate.
            if (b.Timeout <= 0) b.Timeout = 5;
            if (b.CommandTimeout <= 0) b.CommandTimeout = 5;

            return b.ConnectionString;
        }
        catch (Exception ex)
        {
            // Very common: password contains ';' and is not quoted.
            var hint = connStr.Contains("Password=", StringComparison.OrdinalIgnoreCase) && connStr.Contains(";", StringComparison.Ordinal)
                ? " If your password contains ';' then wrap it in quotes: Password='...';"
                : string.Empty;
            throw new InvalidOperationException($"Invalid connection string: {ex.Message}.{hint}", ex);
        }
    }

    /// <summary>
    /// Normalizes registration number / plate for storage + matching:
    /// trims, removes spaces and uppercases.
    /// </summary>
    private static string NormalizePlate(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return "";
        return plate.Trim().Replace(" ", "").ToUpperInvariant();
    }


    /// <summary>
    /// Strict validation for Norwegian fleet plates in this solution:
    /// 2 letters + 5 digits (e.g. EB75707). Anything else is ignored to avoid SKYSS-logo OCR noise.
    /// </summary>
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
    /// Extra safety: only accept plates that exist in egen_flate (synced from EgenFlate.csv).
    /// If the table doesn't exist (older DB), we return true to avoid blocking the app.
    /// </summary>
    private static async Task<bool> PlateExistsInEgenFlateAsync(NpgsqlConnection conn, string plateNorm)
    {
        try
        {
            const string sql = "SELECT 1 FROM public.egen_flate WHERE registreringsnummer = @p LIMIT 1;";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("p", NormalizeRegnr(plateNorm));
            var res = await cmd.ExecuteScalarAsync();
            return res != null;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01" || ex.SqlState == "42703")
        {
            // Table/column missing in older schema -> don't block inserts
            return true;
        }
        catch
        {
            // If DB is flaky, be conservative and return false to avoid polluting wash_events.
            return false;
        }
    }

    

    private static string GuessVehicleType(string? internnr)
    {
        // If DB doesn't provide vehicle type, assume most common: bus/fleet vehicle.
        return string.IsNullOrWhiteSpace(internnr) ? "" : "Buss";
    }

    private static string GuessSeason(DateTime occurredAtLocal)
    {
        // Simple default rule (matches server default):
        // Sommer: 01.04 -> 30.09, Vinter: 01.10 -> 31.03
        var m = occurredAtLocal.Month;
        return (m >= 10 || m <= 3) ? "Vinter" : "Sommer";
    }
public static async Task<List<Ansatt>> GetAnsatterAsync(string connStr)
    {
        var list = new List<Ansatt>();
        connStr = NormalizeOrThrow(connStr);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        const string sql = "SELECT id, ansattnummer, navn FROM public.ansatter WHERE aktiv = true ORDER BY navn;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            list.Add(new Ansatt
            {
                Id = rdr.GetInt64(0),
                Ansattnummer = rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                Navn = rdr.GetString(2)
            });
        }

        return list;
    }

    public static async Task<List<WorkerWashRow>> GetWashesAsync(
        string connStr,
        bool onlyUnconfirmed,
        bool onlyUnntak,
        DateTime fromUtc,
        DateTime toUtc)
    {
        var list = new List<WorkerWashRow>();
        var cs = NormalizeOrThrow(connStr);

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        // NOTE:
        // We intentionally do NOT rely on the DB view (public.worker_active_washes) here.
        // In the field we've seen different values for wash_events.source ("kamera", "camera",
        // different casing, and even trailing spaces). If the view filter is too strict, the
        // Worker app ends up showing an empty list even though wash_events has rows.
        //
        // This query mirrors the view, but normalizes `source` using LOWER(TRIM()).
        string BuildSql(bool useNewDamageColumn)
        {
            var damageExpr = useNewDamageColumn ? "wc.uregistrert_skade" : "wc.kommentar";
            return @"
SELECT
  we.id,
  we.occurred_at AT TIME ZONE 'Europe/Oslo' AS dato,
  we.internnr,
  we.plate AS reg_nr,
  we.vehicle_type AS type_kjoretoy,
  COALESCE(wc.type_vask, 'Innvendig/uttvendig vask') AS type_vask,
  we.season AS season,
  we.status,
  " + damageExpr + @" AS uregistrert_skade,
  a.navn AS ansatt,
  wc.confirmed_at AT TIME ZONE 'Europe/Oslo' AS confirmed_at
FROM public.wash_events we
LEFT JOIN public.wash_confirmations wc ON wc.wash_event_id = we.id
LEFT JOIN public.ansatter a ON a.id = wc.ansatt_id
WHERE
  regexp_replace(COALESCE(we.plate, ''), '[\s\.-]', '', 'g') ~ '^[A-Z]{2}[0-9]{5}$'
  AND we.occurred_at >= @fromUtc
  AND we.occurred_at <= @toUtc
" + (onlyUnntak ? "AND LOWER(TRIM(COALESCE(we.status, '' ))) = 'unntak'" : "") +
(onlyUnconfirmed ? "AND wc.confirmed_at IS NULL" : "") + @"
ORDER BY we.occurred_at DESC
LIMIT 500;";
        }

        async Task ExecuteAndReadAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("fromUtc", DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc));
            cmd.Parameters.AddWithValue("toUtc", DateTime.SpecifyKind(toUtc, DateTimeKind.Utc));
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var datoLocal = rdr.GetFieldValue<DateTime>(1);
                var sh = BilvaskRegistrering.Worker.TurnusHelper.GetShift(datoLocal);
                list.Add(new WorkerWashRow
                {
                    Id = rdr.GetInt64(0),
                    Dato = datoLocal,
                    SkiftNr = sh.Nr,
                    Skift = sh.Display,
                    Internnr = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    RegNr = rdr.GetString(3),
                    TypeKjoretoy = rdr.IsDBNull(4) ? GuessVehicleType(rdr.IsDBNull(2) ? null : rdr.GetString(2)) : rdr.GetString(4),
                    TypeVask = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    Sesong = rdr.IsDBNull(6) ? GuessSeason(datoLocal) : rdr.GetString(6),
                    Status = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                    UregistrertSkade = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                    Ansatt = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                    ConfirmedAt = rdr.IsDBNull(10) ? null : rdr.GetFieldValue<DateTime>(10)
                });
            }
        }

        try
        {
            await ExecuteAndReadAsync(BuildSql(useNewDamageColumn: true));
        }
        catch (PostgresException ex) when (ex.SqlState == "42703")
        {
            list.Clear();
            await ExecuteAndReadAsync(BuildSql(useNewDamageColumn: false));
        }

        return list;
    }

    /// <summary>
    /// Syncs the vehicle lookup table (public.egen_flate) from the local CSV (EgenFlate.csv).
    /// This is needed because the worker UI relies on DB-side joins/views; if reg numbers in DB
    /// contain spaces ("EC 50316") but ITS provides them without spaces ("EC50316"), joins return 0 rows.
    /// We normalize and store registreringsnummer without whitespace and in uppercase.
    /// Returns number of rows inserted.
    /// </summary>
    public static async Task<int> SyncEgenFlateFromCsvAsync(string connStr, string dokumentFolder)
    {
        connStr = NormalizeOrThrow(connStr);
        if (string.IsNullOrWhiteSpace(dokumentFolder))
            return 0;

        var csvPath = Path.Combine(dokumentFolder, "EgenFlate.csv");
        if (!File.Exists(csvPath))
            return 0;

        var lines = await File.ReadAllLinesAsync(csvPath);
        if (lines.Length < 2)
            return 0;

        // delimiter in provided file is ';'
        var delim = lines[0].Contains(';') ? ';' : ',';
        var headers = lines[0].Split(delim).Select(h => h.Trim()).ToArray();

        int idxIntern = IndexOfHeader(headers, "internnr", "intern nr");
        int idxReg = IndexOfHeader(headers, "registreringsnummer", "regnr", "reg nr", "regnummer");
        int idxSelskap = IndexOfHeader(headers, "selskap", "firma", "company");
        int idxType = IndexOfHeader(headers, "typekjoretoy", "type kjøretøy", "type_kjoretoy", "vehicle_type", "type");
        int idxUnntak = IndexOfHeader(headers, "unntak", "exception");

        if (idxIntern < 0 || idxReg < 0)
            return 0;

        var rows = new List<(string Internnr, string Regnr, string Selskap, string VehicleType, bool Unntak)>();
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(delim);
            if (parts.Length <= Math.Max(idxIntern, idxReg)) continue;

            var intern = SafeGet(parts, idxIntern);
            var regRaw = SafeGet(parts, idxReg);
            if (string.IsNullOrWhiteSpace(regRaw)) continue;

            var reg = NormalizeRegnr(regRaw);
            var selskap = idxSelskap >= 0 ? SafeGet(parts, idxSelskap) : "";
            var vehicleType = idxType >= 0 ? SafeGet(parts, idxType) : "";
            var unntakStr = idxUnntak >= 0 ? SafeGet(parts, idxUnntak) : "";
            var unntak = ParseBoolLoose(unntakStr);

            rows.Add((intern, reg, selskap, vehicleType, unntak));
        }

        if (rows.Count == 0)
            return 0;

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Replace the table content; worker account should have rights.
        await using (var del = new NpgsqlCommand("DELETE FROM public.egen_flate;", conn, tx))
            await del.ExecuteNonQueryAsync();

        const string insSql = @"INSERT INTO public.egen_flate (internnr, registreringsnummer, selskap, vehicle_type, unntak)
                               VALUES (@internnr, @regnr, @selskap, @vehicle_type, @unntak);";

        foreach (var r in rows)
        {
            await using var cmd = new NpgsqlCommand(insSql, conn, tx);
            cmd.Parameters.AddWithValue("internnr", (object?)r.Internnr ?? "");
            cmd.Parameters.AddWithValue("regnr", (object?)r.Regnr ?? "");
            cmd.Parameters.AddWithValue("selskap", (object?)r.Selskap ?? "");
            cmd.Parameters.AddWithValue("vehicle_type", (object?)r.VehicleType ?? "");
            cmd.Parameters.AddWithValue("unntak", r.Unntak);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return rows.Count;
    }

    private static int IndexOfHeader(string[] headers, params string[] candidates)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            var h = headers[i].Trim().Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
            foreach (var c in candidates)
            {
                var cc = c.Trim().Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
                if (h == cc) return i;
            }
        }
        return -1;
    }

    private static string SafeGet(string[] parts, int idx)
        => idx >= 0 && idx < parts.Length ? parts[idx].Trim() : "";

    private static string NormalizeRegnr(string reg)
        => NormalizePlate(reg);

    private static bool ParseBoolLoose(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        v = v.Trim();
        if (bool.TryParse(v, out var b)) return b;
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n != 0;
        return v.Equals("ja", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> ConfirmWashAsync(string connStr, long washEventId, long ansattId, string typeVask, string? kommentar)
    {
        if (washEventId <= 0 || ansattId <= 0) return false;
        if (string.IsNullOrWhiteSpace(typeVask)) return false;

        var cs = NormalizeOrThrow(connStr);
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        // Upsert: if the confirmation already exists, update it (so entries always become visible in the list).
        // New schema uses uregistrert_skade; older schema may still use kommentar.
        var sqlNewWithConfirmedAt = @"
INSERT INTO public.wash_confirmations (wash_event_id, ansatt_id, type_vask, uregistrert_skade)
VALUES (@wash_event_id, @ansatt_id, @type_vask, @uregistrert_skade)
ON CONFLICT (wash_event_id) DO UPDATE
SET ansatt_id = EXCLUDED.ansatt_id,
    type_vask = EXCLUDED.type_vask,
    uregistrert_skade = EXCLUDED.uregistrert_skade,
    confirmed_at = COALESCE(public.wash_confirmations.confirmed_at, now());";

        var sqlNewNoConfirmedAt = @"
INSERT INTO public.wash_confirmations (wash_event_id, ansatt_id, type_vask, uregistrert_skade)
VALUES (@wash_event_id, @ansatt_id, @type_vask, @uregistrert_skade)
ON CONFLICT (wash_event_id) DO UPDATE
SET ansatt_id = EXCLUDED.ansatt_id,
    type_vask = EXCLUDED.type_vask,
    uregistrert_skade = EXCLUDED.uregistrert_skade;";

        var sqlOldWithConfirmedAt = @"
INSERT INTO public.wash_confirmations (wash_event_id, ansatt_id, type_vask, kommentar)
VALUES (@wash_event_id, @ansatt_id, @type_vask, @uregistrert_skade)
ON CONFLICT (wash_event_id) DO UPDATE
SET ansatt_id = EXCLUDED.ansatt_id,
    type_vask = EXCLUDED.type_vask,
    kommentar = EXCLUDED.kommentar,
    confirmed_at = COALESCE(public.wash_confirmations.confirmed_at, now());";

        var sqlOldNoConfirmedAt = @"
INSERT INTO public.wash_confirmations (wash_event_id, ansatt_id, type_vask, kommentar)
VALUES (@wash_event_id, @ansatt_id, @type_vask, @uregistrert_skade)
ON CONFLICT (wash_event_id) DO UPDATE
SET ansatt_id = EXCLUDED.ansatt_id,
    type_vask = EXCLUDED.type_vask,
    kommentar = EXCLUDED.kommentar;";

        async Task<int> Exec(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("wash_event_id", washEventId);
            cmd.Parameters.AddWithValue("ansatt_id", ansattId);
            cmd.Parameters.AddWithValue("type_vask", typeVask);
            cmd.Parameters.AddWithValue("uregistrert_skade", (object?)kommentar ?? DBNull.Value);
            return await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            var rows = await Exec(sqlNewWithConfirmedAt);
            return rows > 0;
        }
        catch (PostgresException ex) when (ex.SqlState == "42703")
        {
            try
            {
                var rows = await Exec(sqlNewNoConfirmedAt);
                return rows > 0;
            }
            catch (PostgresException ex2) when (ex2.SqlState == "42703")
            {
                try
                {
                    var rows = await Exec(sqlOldWithConfirmedAt);
                    return rows > 0;
                }
                catch (PostgresException ex3) when (ex3.SqlState == "42703")
                {
                    var rows = await Exec(sqlOldNoConfirmedAt);
                    return rows > 0;
                }
            }
        }
    }




    public static async Task<bool> CanConnectAsync(string connectionString)
    {
        var (ok, _) = await TryConnectAsync(connectionString);
        return ok;
    }

    /// <summary>
    /// Attempts to connect and returns a short, user-friendly error string on failure.
    /// </summary>
    public static async Task<(bool ok, string? error)> TryConnectAsync(string connectionString)
    {
        try
        {
            var cs = NormalizeOrThrow(connectionString);
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
            var r = await cmd.ExecuteScalarAsync();
            return (r != null, null);
        }
        catch (PostgresException pg)
        {
            // Keep it short for UI. SqlState helps a lot.
            var msg = string.IsNullOrWhiteSpace(pg.SqlState)
                ? pg.MessageText
                : $"{pg.SqlState}: {pg.MessageText}";
            return (false, msg);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

        

    /// <summary>
    /// Inserts a wash_event and returns the created id. Used for manual "Kun sjekket" registration.
    /// Returns -1 on failure.
    /// </summary>
    public static async Task<long> InsertWashEventReturningIdAsync(
        string connectionString,
        string plate,
        DateTime occurredAtUtc,
        string sourceApp = "worker_manual",
        string? season = null, bool requireEgenFlate = true)
    {
        try
        {
            var cs = NormalizeOrThrow(connectionString);
            var plateNorm = NormalizePlate(plate);

            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();

        // Reject obvious OCR noise (SKYSS logo etc.) and truncated plates (e.g. EB7571)
        if (!IsValidNorwegianPlate(plateNorm))
            return -1;

        // Accept only known fleet vehicles (present in egen_flate) unless caller opted out
        if (requireEgenFlate && !await PlateExistsInEgenFlateAsync(conn, plateNorm))
            return -1;


            // Prefer the NEW schema (occurred_at / plate / source / season).
            try
            {
                DateTime occurredAtLocal;
                if (occurredAtUtc.Kind == DateTimeKind.Utc) occurredAtLocal = occurredAtUtc.ToLocalTime();
                else if (occurredAtUtc.Kind == DateTimeKind.Local) occurredAtLocal = occurredAtUtc;
                else occurredAtLocal = DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Local);

                var seasonToStore = string.IsNullOrWhiteSpace(season) ? GuessSeason(occurredAtLocal) : season!;

                // try schema with season
                try
                {
                    await using var cmdNewSeason = new NpgsqlCommand(@"
INSERT INTO public.wash_events (occurred_at, plate, season, source)
VALUES (@occurred_at, @plate, @season, @source)
RETURNING id;", conn);

                    cmdNewSeason.Parameters.AddWithValue("occurred_at", occurredAtLocal);
                    cmdNewSeason.Parameters.AddWithValue("plate", plateNorm);
                    cmdNewSeason.Parameters.AddWithValue("season", seasonToStore);
                    cmdNewSeason.Parameters.AddWithValue("source", sourceApp);

                    var idObj = await cmdNewSeason.ExecuteScalarAsync();
                    if (idObj is not null && long.TryParse(idObj.ToString(), out var id1))
                        return id1;
                }
                catch (PostgresException ex) when (ex.SqlState == "42703" || ex.SqlState == "42P01")
                {
                    // fall back to schema without season
                }

                await using var cmdNew = new NpgsqlCommand(@"
INSERT INTO public.wash_events (occurred_at, plate, source)
VALUES (@occurred_at, @plate, @source)
RETURNING id;", conn);

                cmdNew.Parameters.AddWithValue("occurred_at", occurredAtLocal);
                cmdNew.Parameters.AddWithValue("plate", plateNorm);
                cmdNew.Parameters.AddWithValue("source", sourceApp);

                var idObj2 = await cmdNew.ExecuteScalarAsync();
                if (idObj2 is not null && long.TryParse(idObj2.ToString(), out var id2))
                    return id2;

                return -1;
            }
            catch (PostgresException ex) when (ex.SqlState == "42703" || ex.SqlState == "42P01")
            {
                // fall back to legacy schema
            }

            // Legacy schema fallback (occurred_at_utc / registreringsnummer / source_app).
            try
            {
                await using var cmdLegacy = new NpgsqlCommand(@"
INSERT INTO public.wash_events (occurred_at_utc, registreringsnummer, source_app)
VALUES (@occurred_at_utc, @registreringsnummer, @source_app)
RETURNING id;", conn);

                cmdLegacy.Parameters.AddWithValue("occurred_at_utc", occurredAtUtc);
                cmdLegacy.Parameters.AddWithValue("registreringsnummer", plateNorm);
                cmdLegacy.Parameters.AddWithValue("source_app", sourceApp);

                var idObj = await cmdLegacy.ExecuteScalarAsync();
                if (idObj is not null && long.TryParse(idObj.ToString(), out var id3))
                    return id3;
            }
            catch (PostgresException ex) when (ex.SqlState == "42703")
            {
                // Older legacy variants might not have source_app.
                await using var cmdLegacy2 = new NpgsqlCommand(@"
INSERT INTO public.wash_events (occurred_at_utc, registreringsnummer)
VALUES (@occurred_at_utc, @registreringsnummer)
RETURNING id;", conn);

                cmdLegacy2.Parameters.AddWithValue("occurred_at_utc", occurredAtUtc);
                cmdLegacy2.Parameters.AddWithValue("registreringsnummer", plateNorm);

                var idObj2 = await cmdLegacy2.ExecuteScalarAsync();
                if (idObj2 is not null && long.TryParse(idObj2.ToString(), out var id4))
                    return id4;
            }

            return -1;
        }
        catch
        {
            return -1;
        }
    }

public static async Task<DbInsertOutcome> TryInsertWashEventAsync(
        string connectionString,
        string plate,
        DateTime occurredAtUtc,
        string sourceApp = "worker",
        string? season = null, bool requireEgenFlate = true)
    {
        try
        {
            // Normalize and validate connection string
            var cs = NormalizeOrThrow(connectionString);

            var plateNorm = NormalizePlate(plate);

            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();

            // Reject obvious OCR noise (SKYSS logo etc.) and truncated plates (e.g. EB7571)
            if (!IsValidNorwegianPlate(plateNorm))
                return DbInsertOutcome.Deduped;

            // Accept only known fleet vehicles (present in egen_flate)
            if (!await PlateExistsInEgenFlateAsync(conn, plateNorm))
                return DbInsertOutcome.Deduped;


        // Prefer the NEW schema (occurred_at / plate / source).
        try
        {
            DateTime occurredAtLocal;
            if (occurredAtUtc.Kind == DateTimeKind.Utc) occurredAtLocal = occurredAtUtc.ToLocalTime();
            else if (occurredAtUtc.Kind == DateTimeKind.Local) occurredAtLocal = occurredAtUtc;
            else occurredAtLocal = DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Local);

            var seasonToStore = string.IsNullOrWhiteSpace(season) ? GuessSeason(occurredAtLocal) : season!;

            // NEW schema with explicit season column
            try
            {
                await using var cmdNewSeason = new NpgsqlCommand(@"
INSERT INTO public.wash_events (occurred_at, plate, season, source)
VALUES (@occurred_at, @plate, @season, @source);", conn);

                cmdNewSeason.Parameters.AddWithValue("occurred_at", occurredAtLocal);
                cmdNewSeason.Parameters.AddWithValue("plate", plateNorm);
                cmdNewSeason.Parameters.AddWithValue("season", seasonToStore);
                cmdNewSeason.Parameters.AddWithValue("source", sourceApp);

                var rows = await cmdNewSeason.ExecuteNonQueryAsync();
                return rows > 0 ? DbInsertOutcome.Inserted : DbInsertOutcome.Deduped;
            }
            catch (PostgresException ex) when (ex.SqlState == "42703" || ex.SqlState == "42P01")
            {
                // fall back to schema without season/source columns
            }

            await using var cmdNew = new NpgsqlCommand(@"
INSERT INTO public.wash_events (occurred_at, plate, source)
VALUES (@occurred_at, @plate, @source);", conn);

            cmdNew.Parameters.AddWithValue("occurred_at", occurredAtLocal);
            cmdNew.Parameters.AddWithValue("plate", plateNorm);
            cmdNew.Parameters.AddWithValue("source", sourceApp);

            var rows2 = await cmdNew.ExecuteNonQueryAsync();
            return rows2 > 0 ? DbInsertOutcome.Inserted : DbInsertOutcome.Deduped;
        }
        catch (PostgresException ex) when (ex.SqlState == "42703" || ex.SqlState == "42P01")
        {
            // 42703 = undefined_column, 42P01 = undefined_table -> fall back to legacy schema.
        }

        // Legacy schema fallback (occurred_at_utc / registreringsnummer / source_app).
        try
        {
            await using var cmdLegacy = new NpgsqlCommand(@"
INSERT INTO public.wash_events (occurred_at_utc, registreringsnummer, source_app)
VALUES (@occurred_at_utc, @registreringsnummer, @source_app);", conn);

            cmdLegacy.Parameters.AddWithValue("occurred_at_utc", occurredAtUtc);
            cmdLegacy.Parameters.AddWithValue("registreringsnummer", plateNorm);
            cmdLegacy.Parameters.AddWithValue("source_app", sourceApp);

            var rows = await cmdLegacy.ExecuteNonQueryAsync();
            return rows > 0 ? DbInsertOutcome.Inserted : DbInsertOutcome.Deduped;
        }
        catch (PostgresException ex) when (ex.SqlState == "42703")
        {
            // Older legacy variants might not have source_app.
            await using var cmdLegacy2 = new NpgsqlCommand(@"
INSERT INTO public.wash_events (occurred_at_utc, registreringsnummer)
VALUES (@occurred_at_utc, @registreringsnummer);", conn);

            cmdLegacy2.Parameters.AddWithValue("occurred_at_utc", occurredAtUtc);
            cmdLegacy2.Parameters.AddWithValue("registreringsnummer", plateNorm);

            var rows = await cmdLegacy2.ExecuteNonQueryAsync();
            return rows > 0 ? DbInsertOutcome.Inserted : DbInsertOutcome.Deduped;
        }

        }
        catch
        {
            return DbInsertOutcome.Failed;
        }
    }
}
