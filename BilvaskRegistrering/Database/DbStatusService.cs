using Npgsql;

public sealed class DbStatusService
{
    private readonly string _cs;

    public DbStatusService(string connectionString)
    {
        _cs = connectionString;
    }

    public async Task<(bool ok, string message)> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_cs);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
            await cmd.ExecuteScalarAsync(ct);

            return (true, "Connected");
        }
        catch (Exception ex)
        {
            // krótka wiadomość, bez ściany tekstu
            return (false, ex.GetBaseException().Message);
        }
    }
}
