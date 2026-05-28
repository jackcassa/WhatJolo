using System.IO;
using System.Data.Common;

namespace WhatJolo;

public sealed class ProjectWorkspaceService
{
    public string ProjectsRootPath { get; }
    public string YoloProjectsRootPath { get; }

    public ProjectWorkspaceService()
    {
        ProjectsRootPath = Path.Combine(SharedDatabase.GetProjectDirectoryPath(), "Projects");
        YoloProjectsRootPath = SharedDatabase.GetProjectDirectoryPath();
        Directory.CreateDirectory(ProjectsRootPath);
    }

    public IReadOnlyList<string> GetProjectNames()
    {
        Directory.CreateDirectory(ProjectsRootPath);

        if (SharedDatabase.IsDatabaseConnected())
        {
            using var connection = SharedDatabase.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT ProjectName
                FROM ProjectInfo
                ORDER BY ProjectName;
                """;

            var databaseProjects = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var projectName = reader.GetString(0)?.Trim();
                if (!string.IsNullOrWhiteSpace(projectName))
                {
                    databaseProjects.Add(projectName);
                }
            }

            if (databaseProjects.Count > 0)
            {
                return databaseProjects;
            }
        }

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
        if (SharedDatabase.IsDatabaseConnected())
        {
            UpsertProjectInfo(normalizedName);
        }
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
        var classes = activeClasses
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (classes.Count != 1)
        {
            throw new InvalidOperationException("Il training YOLO ora supporta una sola classe alla volta.");
        }

        return CreateYoloDatasetStructure(projectName, classes[0]);
    }

    public string CreateYoloDatasetStructure(string projectName, string className)
    {
        var normalizedProjectName = EnsureProject(projectName);
        var normalizedClassName = NormalizeClassName(className);
        var projectPath = GetYoloClassProjectPath(normalizedProjectName, normalizedClassName);
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

        var yamlPath = Path.Combine(datasetPath, "data.yaml");
        var yamlLines = new List<string>
        {
            $"path: {datasetPath.Replace('\\', '/')}",
            "train: images/train",
            "val: images/val",
            "test: images/test",
            "nc: 1",
            $"names: ['{normalizedClassName}']"
        };
        File.WriteAllLines(yamlPath, yamlLines);

        return datasetPath;
    }

    public string GetYoloDatasetPath(string projectName)
    {
        return GetYoloDatasetPath(projectName, "cerca");
    }

    public string GetYoloDatasetPath(string projectName, string className)
    {
        return Path.Combine(GetYoloClassProjectPath(EnsureProject(projectName), NormalizeClassName(className)), "dataset");
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
        return GetYoloRunsPath(projectName, "cerca");
    }

    public string GetYoloRunsPath(string projectName, string className)
    {
        var projectPath = GetYoloClassProjectPath(projectName, className);
        var runsPath = Path.Combine(projectPath, "runs");
        Directory.CreateDirectory(runsPath);
        return runsPath;
    }

    public string? FindLatestYoloRunPath(string projectName)
    {
        return FindLatestYoloRunPath(projectName, "cerca");
    }

    public string? FindLatestYoloRunPath(string projectName, string className)
    {
        var safeProjectName = NormalizeProjectName(projectName);
        var safeClassName = NormalizeClassName(className);
        var runsPath = GetYoloRunsPath(projectName, safeClassName);
        if (!Directory.Exists(runsPath))
        {
            return null;
        }

        var preferredMatches = Directory
            .EnumerateDirectories(runsPath, $"{safeProjectName}_{safeClassName}*", SearchOption.TopDirectoryOnly)
            .ToList();

        if (preferredMatches.Count == 0)
        {
            // Backward compatibility for migrated class runs that still use the old
            // project-only naming, for example "runs\\primo\\weights\\best.onnx".
            preferredMatches = Directory
                .EnumerateDirectories(runsPath, safeProjectName, SearchOption.TopDirectoryOnly)
                .ToList();
        }

        return preferredMatches
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
        return FindLatestYoloCheckpointPath(projectName, "cerca");
    }

    public string? FindLatestYoloCheckpointPath(string projectName, string className)
    {
        var latestRunPath = FindLatestYoloRunPath(projectName, className);
        if (string.IsNullOrWhiteSpace(latestRunPath))
        {
            return null;
        }

        var checkpointPath = Path.Combine(latestRunPath, "weights", "last.pt");
        return File.Exists(checkpointPath) ? checkpointPath : null;
    }

    public string? FindLatestYoloOnnxPath(string projectName)
    {
        return FindLatestYoloOnnxPath(projectName, "cerca");
    }

    public string? FindLatestYoloOnnxPath(string projectName, string className)
    {
        var latestRunPath = FindLatestYoloRunPath(projectName, className);
        if (string.IsNullOrWhiteSpace(latestRunPath))
        {
            return null;
        }

        var onnxPath = Path.Combine(latestRunPath, "weights", "best.onnx");
        return File.Exists(onnxPath) ? onnxPath : null;
    }

    public string? ResetLatestYoloRun(string projectName)
    {
        return ResetLatestYoloRun(projectName, "cerca");
    }

    public string? ResetLatestYoloRun(string projectName, string className)
    {
        var latestRunPath = FindLatestYoloRunPath(projectName, className);
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

        if (!SharedDatabase.IsDatabaseConnected())
        {
            return safeAvailableClasses;
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

    public string? LoadSelectedCropClass(string projectName, IEnumerable<string> availableClasses)
    {
        var safeAvailableClasses = availableClasses
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (safeAvailableClasses.Length == 0 || !SharedDatabase.IsDatabaseConnected())
        {
            return null;
        }

        using var connection = SharedDatabase.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CurrentCropClass
            FROM ProjectInfo
            WHERE ProjectName = @ProjectName;
            """;
        AddParameter(command, "@ProjectName", EnsureProject(projectName));

        var result = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        var normalizedClass = result.Trim().ToLowerInvariant();
        return safeAvailableClasses.Contains(normalizedClass, StringComparer.OrdinalIgnoreCase)
            ? normalizedClass
            : null;
    }

    public void SaveSelectedCropClass(string projectName, string className)
    {
        if (!SharedDatabase.IsDatabaseConnected())
        {
            return;
        }

        var normalizedProjectName = EnsureProject(projectName);
        var normalizedClassName = NormalizeClassName(className);

        using var connection = SharedDatabase.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE ProjectInfo
            SET CurrentCropClass = @CurrentCropClass,
                UpdatedAtUtc = CURRENT_TIMESTAMP
            WHERE ProjectName = @ProjectName;
            """;
        AddParameter(command, "@ProjectName", normalizedProjectName);
        AddParameter(command, "@CurrentCropClass", normalizedClassName);
        command.ExecuteNonQuery();
    }

    public void SaveActiveClasses(string projectName, IEnumerable<string> activeClasses)
    {
        var normalizedProjectName = EnsureProject(projectName);
        if (!SharedDatabase.IsDatabaseConnected())
        {
            return;
        }

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
                    CreatedAtUtc,
                    UpdatedAtUtc
                )
                VALUES
                (
                    @ProjectName,
                    @ClassName,
                    CURRENT_TIMESTAMP,
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

    private static string NormalizeClassName(string? className)
    {
        var rawName = string.IsNullOrWhiteSpace(className) ? "cerca" : className.Trim().ToLowerInvariant();
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(rawName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "cerca" : sanitized;
    }

    private string GetYoloClassProjectPath(string projectName, string className)
    {
        var normalizedProjectName = NormalizeProjectName(projectName);
        var normalizedClassName = NormalizeClassName(className);
        var projectPath = Path.Combine(GetYoloProjectPath(normalizedProjectName), "classes", normalizedClassName);
        Directory.CreateDirectory(projectPath);
        return projectPath;
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

    private void UpsertProjectInfo(string projectName)
    {
        using var connection = SharedDatabase.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ProjectInfo
            (
                ProjectName,
                MachineName,
                CurrentCropClass,
                CreatedAtUtc,
                UpdatedAtUtc
            )
            VALUES
            (
                @ProjectName,
                @MachineName,
                @CurrentCropClass,
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            )
            ON CONFLICT(ProjectName) DO UPDATE SET
                MachineName = excluded.MachineName,
                UpdatedAtUtc = CURRENT_TIMESTAMP;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@MachineName", Environment.MachineName);
        AddParameter(command, "@CurrentCropClass", "cerca");
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
