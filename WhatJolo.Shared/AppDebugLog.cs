using System.Text;

namespace WhatJolo;

public enum AppDebugLevel
{
    Debug,
    Info,
    Warn,
    Error
}

public static class AppDebugLog
{
    private static readonly object SyncRoot = new();

    public static string GetLogFilePath()
    {
        var logsPath = Path.Combine(SharedDatabase.GetProjectDirectoryPath(), "Logs");
        Directory.CreateDirectory(logsPath);
        return Path.Combine(logsPath, "whatjolo-debug.log");
    }

    public static void Debug(string scope, string message)
    {
        Write(AppDebugLevel.Debug, scope, message);
    }

    public static void Info(string scope, string message)
    {
        Write(AppDebugLevel.Info, scope, message);
    }

    public static void Warn(string scope, string message)
    {
        Write(AppDebugLevel.Warn, scope, message);
    }

    public static void Error(string scope, string message, Exception? exception = null)
    {
        Write(AppDebugLevel.Error, scope, message, exception);
    }

    public static void Write(AppDebugLevel level, string scope, string message, Exception? exception = null)
    {
        var builder = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [")
            .Append(level.ToString().ToUpperInvariant())
            .Append("] ")
            .Append(scope)
            .Append(" | ")
            .Append(message);

        if (exception is not null)
        {
            builder.AppendLine()
                .Append(exception.GetType().FullName)
                .Append(": ")
                .Append(exception.Message);

            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                builder.AppendLine()
                    .Append(exception.StackTrace);
            }
        }

        builder.AppendLine();

        lock (SyncRoot)
        {
            File.AppendAllText(GetLogFilePath(), builder.ToString());
        }
    }
}
