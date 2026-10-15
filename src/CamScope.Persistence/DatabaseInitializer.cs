using Microsoft.Data.Sqlite;

namespace CamScope.Persistence;

public sealed class DatabaseInitializer
{
    private readonly string _connectionString;

    public DatabaseInitializer(string connectionString)
    {
        _connectionString = connectionString;
    }

    public static string GetDefaultDbPath()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                var camScopeDir = Path.Combine(appData, "CamScope");
                Directory.CreateDirectory(camScopeDir);
                return Path.Combine(camScopeDir, "camscope.db");
            }
        }
        catch
        {
            // Fallback when OS application data directory access is restricted
        }

        var localDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(localDir);
        return Path.Combine(localDir, "camscope.db");
    }

    public static string CreateDefaultConnectionString()
    {
        var path = GetDefaultDbPath();
        return $"Data Source={path}";
    }

    public void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Devices (
    Id TEXT PRIMARY KEY,
    EndpointId TEXT NOT NULL UNIQUE,
    DisplayName TEXT NOT NULL,
    Vendor TEXT NOT NULL,
    Model TEXT NOT NULL,
    FirmwareVersion TEXT,
    SerialNumber TEXT,
    Host TEXT NOT NULL,
    Port INTEGER NOT NULL,
    PrimaryServiceAddress TEXT,
    ServiceAddressesJson TEXT NOT NULL,
    ScopesJson TEXT NOT NULL,
    ConnectionState TEXT NOT NULL,
    AuthState TEXT NOT NULL,
    CapabilitiesJson TEXT NOT NULL,
    Protocol TEXT NOT NULL,
    FirstSeenUtc TEXT NOT NULL,
    LastSeenUtc TEXT NOT NULL,
    LastError TEXT,
    IsDemoDevice INTEGER NOT NULL,
    Location TEXT,
    TagsJson TEXT NOT NULL,
    RtspStreamUri TEXT
);

CREATE TABLE IF NOT EXISTS AuditLogs (
    Id TEXT PRIMARY KEY,
    TimestampUtc TEXT NOT NULL,
    Action TEXT NOT NULL,
    TargetEndpoint TEXT,
    UserOrSource TEXT,
    Succeeded INTEGER NOT NULL,
    Details TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Settings (
    Key TEXT PRIMARY KEY,
    ValueJson TEXT NOT NULL
);
";
        cmd.ExecuteNonQuery();
    }
}
