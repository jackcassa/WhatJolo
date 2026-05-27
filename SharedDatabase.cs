using System.Data.Common;
using System.IO;
using System.Text.Json;
using Npgsql;

namespace WhatJolo;

public static class SharedDatabase
{
    private static readonly object InitLock = new();
    private static bool _initialized;
    private static bool _postgresActivated;
    private static PostgresConnectionSettings? _cachedSettings;

    public static string GetProjectDirectoryPath()
    {
        var current = new DirectoryInfo(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..")));

        while (current != null)
        {
            var solutionPath = Path.Combine(current.FullName, "WhatJolo.slnx");
            if (File.Exists(solutionPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            ".."));
    }

    public static string GetHidRootPath()
    {
        return Path.GetFullPath(Path.Combine(
            GetProjectDirectoryPath(),
            ".."));
    }

    public static string GetDatabasePath()
    {
        return GetConnectionDisplayString();
    }

    public static string GetConnectionString()
    {
        return GetPostgresConnectionString();
    }

    public static DbConnection CreateConnection()
    {
        if (!IsPostgresConfigured())
        {
            throw new InvalidOperationException("Database PostgreSQL non connesso. Premi Connetti nella tab Istanza DB.");
        }

        EnsureDatabaseReady();
        return new NpgsqlConnection(GetPostgresConnectionString());
    }

    public static bool IsPostgresConfigured()
    {
        return _postgresActivated && !string.IsNullOrWhiteSpace(GetPostgresConnectionString());
    }

    public static bool IsDatabaseConnected()
    {
        return IsPostgresConfigured();
    }

    public static void ActivateConfiguredPostgres()
    {
        _postgresActivated = true;
        ResetInitialization();
    }

    public static void DeactivatePostgres()
    {
        _postgresActivated = false;
        ResetInitialization();
    }

    public static string GetSettingsFilePath()
    {
        return Path.Combine(GetProjectDirectoryPath(), "db.instance.json");
    }

    public static PostgresConnectionSettings LoadPostgresSettings()
    {
        if (_cachedSettings != null)
        {
            return CloneSettings(_cachedSettings);
        }

        var envConnection = Environment.GetEnvironmentVariable("WHATJOLO_POSTGRES_CONNECTION")?.Trim();
        if (!string.IsNullOrWhiteSpace(envConnection))
        {
            var envBuilder = new NpgsqlConnectionStringBuilder(envConnection);
            _cachedSettings = new PostgresConnectionSettings
            {
                Enabled = true,
                Host = envBuilder.Host ?? string.Empty,
                Port = envBuilder.Port,
                Database = envBuilder.Database ?? string.Empty,
                Username = envBuilder.Username ?? string.Empty,
                Password = envBuilder.Password ?? string.Empty
            };
            return CloneSettings(_cachedSettings);
        }

        var settingsPath = GetSettingsFilePath();
        if (!File.Exists(settingsPath))
        {
            _cachedSettings = new PostgresConnectionSettings();
            return CloneSettings(_cachedSettings);
        }

        var json = File.ReadAllText(settingsPath);
        _cachedSettings = JsonSerializer.Deserialize<PostgresConnectionSettings>(json) ?? new PostgresConnectionSettings();
        return CloneSettings(_cachedSettings);
    }

    public static void SavePostgresSettings(PostgresConnectionSettings settings)
    {
        var normalizedSettings = CloneSettings(settings);
        var json = JsonSerializer.Serialize(normalizedSettings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(GetSettingsFilePath(), json);
        _cachedSettings = normalizedSettings;
        if (!normalizedSettings.Enabled)
        {
            _postgresActivated = false;
        }
        ResetInitialization();
    }

    public static void ResetInitialization()
    {
        lock (InitLock)
        {
            _initialized = false;
        }
    }

    public static string GetConnectionDisplayString()
    {
        if (!IsPostgresConfigured())
        {
            return "PostgreSQL non connesso";
        }

        var builder = new NpgsqlConnectionStringBuilder(GetPostgresConnectionString());
        return $"PostgreSQL | Host={builder.Host} | Port={builder.Port} | Database={builder.Database} | User={builder.Username}";
    }

    public static string GetConnectionPreview()
    {
        var settings = LoadPostgresSettings();
        return $"Host={settings.Host};Port={settings.Port};Database={settings.Database};Username={settings.Username};Password={settings.Password}";
    }

    public static void EnsureDatabaseReady(Action<string>? progress = null)
    {
        if (!IsPostgresConfigured() || _initialized)
        {
            return;
        }

        lock (InitLock)
        {
            if (!IsPostgresConfigured() || _initialized)
            {
                return;
            }

            progress?.Invoke("Creazione/verifica schema PostgreSQL...");
            var migrator = new PostgresDatabaseMigrator(GetPostgresConnectionString());
            migrator.EnsureReady();
            _initialized = true;
            progress?.Invoke("Schema PostgreSQL pronto.");
        }
    }

    private static string GetPostgresConnectionString()
    {
        var envValue = Environment.GetEnvironmentVariable("WHATJOLO_POSTGRES_CONNECTION")?.Trim();
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            var envBuilder = new NpgsqlConnectionStringBuilder(envValue);
            if (envBuilder.Timeout <= 0)
            {
                envBuilder.Timeout = 5;
            }

            if (envBuilder.CommandTimeout <= 0)
            {
                envBuilder.CommandTimeout = 15;
            }

            if (envBuilder.KeepAlive <= 0)
            {
                envBuilder.KeepAlive = 15;
            }

            return envBuilder.ConnectionString;
        }

        var settings = LoadPostgresSettings();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database))
        {
            return string.Empty;
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = settings.Host,
            Port = settings.Port <= 0 ? 5432 : settings.Port,
            Database = settings.Database,
            Username = settings.Username ?? string.Empty,
            Password = settings.Password ?? string.Empty,
            Pooling = true,
            Timeout = 5,
            CommandTimeout = 15,
            KeepAlive = 15
        };
        return builder.ConnectionString;
    }

    private static PostgresConnectionSettings CloneSettings(PostgresConnectionSettings settings)
    {
        return new PostgresConnectionSettings
        {
            Enabled = settings.Enabled,
            Host = settings.Host,
            Port = settings.Port,
            Database = settings.Database,
            Username = settings.Username,
            Password = settings.Password
        };
    }
}
