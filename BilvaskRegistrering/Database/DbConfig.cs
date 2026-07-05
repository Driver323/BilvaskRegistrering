using Microsoft.Extensions.Configuration;
using BilvaskRegistrering;

public static class DbConfig
{
    public static bool Enabled { get; private set; }
    public static string ConnectionString { get; private set; } = "";
    public static bool WorkerOnlyUnntak { get; private set; }
    public static bool QueueWhenDown { get; private set; }
    public static string QueueFile { get; private set; } = "AdminDbQueue.csv";

    public static void Load(IConfiguration config)
    {
        // 1) Default from appsettings.json (still supported)
        Enabled = config.GetValue("Postgres:Enabled", false);
        ConnectionString = config.GetValue<string>("Postgres:ConnectionString") ?? "";
        WorkerOnlyUnntak = config.GetValue("Postgres:WorkerOnlyUnntak", false);
        QueueWhenDown = config.GetValue("Postgres:QueueWhenDown", true);
        QueueFile = config.GetValue<string>("Postgres:QueueFile") ?? "AdminDbQueue.csv";

        // 2) Override from runtime JSON (settings.runtime.json), if present.
        // This enables changing DB connection from the application settings UI.
        try
        {
            // Only override appsettings when runtime DB settings are explicitly enabled and complete.
            // If runtime says "disabled", we keep the appsettings.json values so production deployments
            // can be controlled by appsettings without being accidentally overridden by an old runtime file.
            if (AppConfig.DbEnabled &&
                !string.IsNullOrWhiteSpace(AppConfig.DbHost) &&
                !string.IsNullOrWhiteSpace(AppConfig.DbName) &&
                !string.IsNullOrWhiteSpace(AppConfig.DbAdminUser))
            {
                Enabled = true;
                ConnectionString = AppConfig.BuildAdminConnectionString();
            }
        }
        catch
        {
            // ignore – keep values from appsettings
        }
    }
}
