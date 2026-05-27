using System.Data.Common;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace WhatJolo;

internal static class SharedDatabase
{
    private static readonly object InitLock = new();
    private static bool _initialized;
    private static bool _postgresActivated;
    private static PostgresConnectionSettings? _cachedSettings;

    public static string GetProjectDirectoryPath()
    {
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

    public static string GetSqliteDatabasePath()
    {
        return Path.GetFullPath(Path.Combine(
            GetHidRootPath(),
            "ScrcpyKeyboardClient",
            "AnnotationTemplates",
            "annotations.sqlite3"));
    }

    public static string GetDatabasePath()
    {
        return IsPostgresConfigured() ? GetConnectionDisplayString() : GetSqliteDatabasePath();
    }

    public static string GetConnectionString()
    {
        return IsPostgresConfigured()
            ? GetPostgresConnectionString()
            : GetSqliteConnectionString();
    }

    public static DbConnection CreateConnection()
    {
        EnsureDatabaseReady();
        return IsPostgresConfigured()
            ? new NpgsqlConnection(GetPostgresConnectionString())
            : new SqliteConnection(GetSqliteConnectionString());
    }

    public static bool IsPostgresConfigured()
    {
        return _postgresActivated && !string.IsNullOrWhiteSpace(GetPostgresConnectionString());
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
            return GetSqliteDatabasePath();
        }

        var builder = new NpgsqlConnectionStringBuilder(GetPostgresConnectionString());
        return $"PostgreSQL | Host={builder.Host} | Port={builder.Port} | Database={builder.Database} | User={builder.Username}";
    }

    public static void EnsureDatabaseReady()
    {
        if (_initialized)
        {
            return;
        }

        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            if (IsPostgresConfigured())
            {
                var migrator = new PostgresDatabaseMigrator(GetPostgresConnectionString(), GetSqliteConnectionString());
                migrator.EnsureReady();
            }

            _initialized = true;
        }
    }

    private static string GetSqliteConnectionString()
    {
        return $"Data Source={GetSqliteDatabasePath()};Cache=Shared;Mode=ReadWriteCreate;";
    }

    private static string GetPostgresConnectionString()
    {
        var envValue = Environment.GetEnvironmentVariable("WHATJOLO_POSTGRES_CONNECTION")?.Trim();
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return envValue;
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
            Pooling = true
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
