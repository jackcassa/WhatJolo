using System.IO;
using System.Data.Common;

namespace WhatJolo;

internal sealed class ProjectWorkspaceService
{
    private readonly string _connectionString;

    public string ProjectsRootPath { get; }
    public string YoloProjectsRootPath { get; }

    public ProjectWorkspaceService()
    {
        ProjectsRootPath = Path.Combine(SharedDatabase.GetProjectDirectoryPath(), "Projects");
        YoloProjectsRootPath = SharedDatabase.GetProjectDirectoryPath();
        _connectionString = SharedDatabase.GetConnectionString();
        SharedDatabase.EnsureDatabaseReady();
        Directory.CreateDirectory(ProjectsRootPath);
        EnsureProjectActiveClassTable();
        EnsureProjectInfoTable();
    }

    public IReadOnlyList<string> GetProjectNames()
    {
        Directory.CreateDirectory(ProjectsRootPath);

        return Directory
            .GetDirectories(ProjectsRootPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    public string EnsureProject(string projectName)
    {
        var normalizedName = NormalizeProjectName(projectName);
        var projectPath = GetProjectPath(normalizedName);
        Directory.CreateDirectory(projectPath);
        Directory.CreateDirectory(Path.Combine(projectPath, "Captures"));
        Directory.CreateDirectory(Path.Combine(projectPath, "SavedCrops"));
        UpsertProjectInfo(normalizedName, projectPath);
        return normalizedName;
    }

    public string GetProjectPath(string projectName)
    {
        return Path.Combine(ProjectsRootPath, NormalizeProjectName(projectName));
    }

    public string GetCapturesPath(string projectName)
    {
        return Path.Combine(GetProjectPath(projectName), "Captures");
    }

    public string GetSavedCropsPath(string projectName)
    {
        return Path.Combine(GetProjectPath(projectName), "SavedCrops");
    }

    public string CreateYoloDatasetStructure(string projectName, IEnumerable<string> activeClasses)
    {
        var normalizedProjectName = EnsureProject(projectName);
        var projectPath = GetYoloProjectPath(normalizedProjectName);
        var datasetPath = Path.Combine(projectPath, "dataset");
        var imagesTrainPath = Path.Combine(datasetPath, "images", "train");
        var imagesValPath = Path.Combine(datasetPath, "images", "val");
        var imagesTestPath = Path.Combine(datasetPath, "images", "test");
        var labelsTrainPath = Path.Combine(datasetPath, "labels", "train");
        var labelsValPath = Path.Combine(datasetPath, "labels", "val");
        var labelsTestPath = Path.Combine(datasetPath, "labels", "test");

        Directory.CreateDirectory(imagesTrainPath);
        Directory.CreateDirectory(imagesValPath);
        Directory.CreateDirectory(imagesTestPath);
        Directory.CreateDirectory(labelsTrainPath);
        Directory.CreateDirectory(labelsValPath);
        Directory.CreateDirectory(labelsTestPath);

        var classes = activeClasses
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var yamlPath = Path.Combine(datasetPath, "data.yaml");
        var yamlLines = new List<string>
        {
            $"path: {datasetPath.Replace('\\', '/')}",
            "train: images/train",
            "val: images/val",
            "test: images/test",
            $"nc: {classes.Count}",
            $"names: [{string.Join(", ", classes.Select(static name => $"'{name}'"))}]"
        };
        File.WriteAllLines(yamlPath, yamlLines);

        return datasetPath;
    }

    public string GetYoloDatasetPath(string projectName)
    {
        return Path.Combine(GetYoloProjectPath(EnsureProject(projectName)), "dataset");
    }

    public string GetYoloProjectPath(string projectName)
    {
        var normalizedProjectName = NormalizeProjectName(projectName);
        var projectPath = Path.Combine(YoloProjectsRootPath, normalizedProjectName);
        Directory.CreateDirectory(projectPath);
        return projectPath;
    }

    public string GetYoloTestPath(string projectName)
    {
        var projectPath = GetYoloProjectPath(projectName);
        var testPath = Path.Combine(projectPath, "test");
        Directory.CreateDirectory(testPath);
        return testPath;
    }

    public string GetYoloRunsPath(string projectName)
    {
        var projectPath = GetYoloProjectPath(projectName);
        var runsPath = Path.Combine(projectPath, "runs");
        Directory.CreateDirectory(runsPath);
        return runsPath;
    }

    public string? FindLatestYoloRunPath(string projectName)
    {
        var safeProjectName = NormalizeProjectName(projectName);
        var runsPath = GetYoloRunsPath(projectName);
        if (!Directory.Exists(runsPath))
        {
            return null;
        }

        return Directory
            .EnumerateDirectories(runsPath, safeProjectName + "*", SearchOption.TopDirectoryOnly)
            .Select(path => new
            {
                Path = path,
                Stamp = GetRunTimestamp(path)
            })
            .OrderByDescending(item => item.Stamp)
            .Select(item => item.Path)
            .FirstOrDefault();
    }

    public string? FindLatestYoloCheckpointPath(string projectName)
    {
        var latestRunPath = FindLatestYoloRunPath(projectName);
        if (string.IsNullOrWhiteSpace(latestRunPath))
        {
            return null;
        }

        var checkpointPath = Path.Combine(latestRunPath, "weights", "last.pt");
        return File.Exists(checkpointPath) ? checkpointPath : null;
    }

    public string? FindLatestYoloOnnxPath(string projectName)
    {
        var latestRunPath = FindLatestYoloRunPath(projectName);
        if (string.IsNullOrWhiteSpace(latestRunPath))
        {
            return null;
        }

        var onnxPath = Path.Combine(latestRunPath, "weights", "best.onnx");
        return File.Exists(onnxPath) ? onnxPath : null;
    }

    public string? ResetLatestYoloRun(string projectName)
    {
        var latestRunPath = FindLatestYoloRunPath(projectName);
        if (string.IsNullOrWhiteSpace(latestRunPath) || !Directory.Exists(latestRunPath))
        {
            return null;
        }

        Directory.Delete(latestRunPath, recursive: true);
        return latestRunPath;
    }

    public IReadOnlyList<string> LoadActiveClasses(string projectName, IEnumerable<string> availableClasses)
    {
        var safeAvailableClasses = availableClasses
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (safeAvailableClasses.Count == 0)
        {
            return Array.Empty<string>();
        }

        using var connection = SharedDatabase.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ClassName
            FROM ProjectActiveClass
            WHERE ProjectName = @ProjectName
            ORDER BY ClassName;
            """;
        AddParameter(command, "@ProjectName", EnsureProject(projectName));

        var selectedClasses = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var className = reader.GetString(0).Trim().ToLowerInvariant();
            if (safeAvailableClasses.Contains(className, StringComparer.OrdinalIgnoreCase))
            {
                selectedClasses.Add(className);
            }
        }

        return selectedClasses.Count > 0
            ? selectedClasses
            : safeAvailableClasses;
    }

    public void SaveActiveClasses(string projectName, IEnumerable<string> activeClasses)
    {
        var normalizedProjectName = EnsureProject(projectName);
        var distinctClasses = activeClasses
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var connection = SharedDatabase.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM ProjectActiveClass WHERE ProjectName = @ProjectName;";
            AddParameter(deleteCommand, "@ProjectName", normalizedProjectName);
            deleteCommand.ExecuteNonQuery();
        }

        foreach (var className in distinctClasses)
        {
            using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                INSERT INTO ProjectActiveClass
                (
                    ProjectName,
                    ClassName,
                    UpdatedAtUtc
                )
                VALUES
                (
                    @ProjectName,
                    @ClassName,
                    CURRENT_TIMESTAMP
                );
                """;
            AddParameter(insertCommand, "@ProjectName", normalizedProjectName);
            AddParameter(insertCommand, "@ClassName", className);
            insertCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static string NormalizeProjectName(string? projectName)
    {
        var rawName = string.IsNullOrWhiteSpace(projectName) ? "Default" : projectName.Trim();
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(rawName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Default" : sanitized;
    }

    private static DateTime GetRunTimestamp(string runPath)
    {
        var weightsFolder = Path.Combine(runPath, "weights");
        var lastCheckpoint = Path.Combine(weightsFolder, "last.pt");
        var bestCheckpoint = Path.Combine(weightsFolder, "best.pt");

        if (File.Exists(lastCheckpoint))
        {
            return File.GetLastWriteTimeUtc(lastCheckpoint);
        }

        if (File.Exists(bestCheckpoint))
        {
            return File.GetLastWriteTimeUtc(bestCheckpoint);
        }

        return Directory.GetLastWriteTimeUtc(runPath);
    }

    private void EnsureProjectActiveClassTable()
    {
        using var connection = SharedDatabase.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SharedDatabase.IsPostgresConfigured()
            ? """
              CREATE TABLE IF NOT EXISTS ProjectActiveClass
              (
                  Id BIGSERIAL PRIMARY KEY,
                  ProjectName TEXT NOT NULL,
                  ClassName TEXT NOT NULL,
                  CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                  UpdatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                  UNIQUE(ProjectName, ClassName)
              );

              CREATE INDEX IF NOT EXISTS IX_ProjectActiveClass_ProjectName
                  ON ProjectActiveClass(ProjectName, ClassName);
              """
            : """
              CREATE TABLE IF NOT EXISTS ProjectActiveClass
              (
                  Id INTEGER PRIMARY KEY AUTOINCREMENT,
                  ProjectName TEXT NOT NULL,
                  ClassName TEXT NOT NULL,
                  CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                  UpdatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                  UNIQUE(ProjectName, ClassName)
              );

              CREATE INDEX IF NOT EXISTS IX_ProjectActiveClass_ProjectName
                  ON ProjectActiveClass(ProjectName, ClassName);
              """;
        command.ExecuteNonQuery();
    }

    private void EnsureProjectInfoTable()
    {
        using var connection = SharedDatabase.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SharedDatabase.IsPostgresConfigured()
            ? """
              CREATE TABLE IF NOT EXISTS ProjectInfo
              (
                  Id BIGSERIAL PRIMARY KEY,
                  ProjectName TEXT NOT NULL,
                  ProjectRootPath TEXT NOT NULL,
                  MachineName TEXT NOT NULL,
                  CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                  UpdatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                  UNIQUE(ProjectName)
              );

              CREATE INDEX IF NOT EXISTS IX_ProjectInfo_ProjectName
                  ON ProjectInfo(ProjectName);
              """
            : """
              CREATE TABLE IF NOT EXISTS ProjectInfo
              (
                  Id INTEGER PRIMARY KEY AUTOINCREMENT,
                  ProjectName TEXT NOT NULL,
                  ProjectRootPath TEXT NOT NULL,
                  MachineName TEXT NOT NULL,
                  CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                  UpdatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                  UNIQUE(ProjectName)
              );

              CREATE INDEX IF NOT EXISTS IX_ProjectInfo_ProjectName
                  ON ProjectInfo(ProjectName);
              """;
        command.ExecuteNonQuery();
    }

    private void UpsertProjectInfo(string projectName, string projectRootPath)
    {
        using var connection = SharedDatabase.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ProjectInfo
            (
                ProjectName,
                ProjectRootPath,
                MachineName,
                UpdatedAtUtc
            )
            VALUES
            (
                @ProjectName,
                @ProjectRootPath,
                @MachineName,
                CURRENT_TIMESTAMP
            )
            ON CONFLICT(ProjectName) DO UPDATE SET
                ProjectRootPath = excluded.ProjectRootPath,
                MachineName = excluded.MachineName,
                UpdatedAtUtc = CURRENT_TIMESTAMP;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@ProjectRootPath", projectRootPath);
        AddParameter(command, "@MachineName", Environment.MachineName);
        command.ExecuteNonQuery();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
