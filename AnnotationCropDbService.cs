using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace WhatJolo;

internal sealed class AnnotationCropDbService
{
    private readonly string _connectionString;
    private readonly string _storageRootPath;

    public AnnotationCropDbService()
    {
        _connectionString = SharedDatabase.GetConnectionString();
        _storageRootPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SharedDatabase.GetSqliteDatabasePath())!, ".."));
        SharedDatabase.EnsureDatabaseReady();
        EnsureProjectCropVariationColumn();
    }

    public async Task SaveCropAsync(string projectName, string labelName, string sourceImagePath, string cropImagePath, Int32Rect bounds, bool isVariation = false)
    {
        var cropHash = await ComputeFileSha256Async(cropImagePath);

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        long cropAssetId;
        await using (var cropCommand = connection.CreateCommand())
        {
            cropCommand.Transaction = transaction;
            cropCommand.CommandText =
                """
                INSERT INTO CropAsset
                (
                    SourceImagePath,
                    CropImagePath,
                    CropHash,
                    X,
                    Y,
                    Width,
                    Height,
                    UpdatedAtUtc
                )
                VALUES
                (
                    @SourceImagePath,
                    @CropImagePath,
                    @CropHash,
                    @X,
                    @Y,
                    @Width,
                    @Height,
                    CURRENT_TIMESTAMP
                )
                ON CONFLICT(CropHash) DO UPDATE SET
                    SourceImagePath = excluded.SourceImagePath,
                    CropImagePath = excluded.CropImagePath,
                    X = excluded.X,
                    Y = excluded.Y,
                    Width = excluded.Width,
                    Height = excluded.Height,
                    UpdatedAtUtc = CURRENT_TIMESTAMP;

                SELECT Id FROM CropAsset WHERE CropHash = @CropHash;
                """;
            AddParameter(cropCommand, "@SourceImagePath", ConvertToStoredPath(sourceImagePath));
            AddParameter(cropCommand, "@CropImagePath", ConvertToStoredPath(cropImagePath));
            AddParameter(cropCommand, "@CropHash", cropHash);
            AddParameter(cropCommand, "@X", bounds.X);
            AddParameter(cropCommand, "@Y", bounds.Y);
            AddParameter(cropCommand, "@Width", bounds.Width);
            AddParameter(cropCommand, "@Height", bounds.Height);
            cropAssetId = Convert.ToInt64(await cropCommand.ExecuteScalarAsync());
        }

        await using (var linkCommand = connection.CreateCommand())
        {
            linkCommand.Transaction = transaction;
            linkCommand.CommandText =
                """
                INSERT INTO ProjectCropLink
                (
                    ProjectName,
                    LabelName,
                    CropAssetId,
                    IsVariation,
                    UpdatedAtUtc
                )
                VALUES
                (
                    @ProjectName,
                    @LabelName,
                    @CropAssetId,
                    @IsVariation,
                    CURRENT_TIMESTAMP
                )
                ON CONFLICT(ProjectName, LabelName, CropAssetId) DO UPDATE SET
                    IsVariation = excluded.IsVariation,
                    UpdatedAtUtc = CURRENT_TIMESTAMP;
                """;
            AddParameter(linkCommand, "@ProjectName", projectName);
            AddParameter(linkCommand, "@LabelName", labelName);
            AddParameter(linkCommand, "@CropAssetId", cropAssetId);
            AddParameter(linkCommand, "@IsVariation", isVariation ? 1 : 0);
            await linkCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<bool> DeleteCropAsync(string projectName, string labelName, string cropImagePath)
    {
        var storedCropPath = ConvertToStoredPath(cropImagePath);

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        long linkId = 0;
        long cropAssetId = 0;

        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandText =
                """
                SELECT
                    pcl.Id,
                    pcl.CropAssetId
                FROM ProjectCropLink pcl
                INNER JOIN CropAsset ca ON ca.Id = pcl.CropAssetId
                WHERE pcl.ProjectName = @ProjectName
                  AND pcl.LabelName = @LabelName
                  AND ca.CropImagePath = @CropImagePath
                LIMIT 1;
                """;
            AddParameter(selectCommand, "@ProjectName", projectName);
            AddParameter(selectCommand, "@LabelName", labelName);
            AddParameter(selectCommand, "@CropImagePath", storedCropPath);

            await using var reader = await selectCommand.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return false;
            }

            linkId = reader.GetInt64(0);
            cropAssetId = reader.GetInt64(1);
        }

        await using (var deleteLinkCommand = connection.CreateCommand())
        {
            deleteLinkCommand.Transaction = transaction;
            deleteLinkCommand.CommandText = "DELETE FROM ProjectCropLink WHERE Id = @Id;";
            AddParameter(deleteLinkCommand, "@Id", linkId);
            await deleteLinkCommand.ExecuteNonQueryAsync();
        }

        long remainingLinks;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM ProjectCropLink WHERE CropAssetId = @CropAssetId;";
            AddParameter(countCommand, "@CropAssetId", cropAssetId);
            remainingLinks = Convert.ToInt64(await countCommand.ExecuteScalarAsync());
        }

        if (remainingLinks == 0)
        {
            await using var deleteAssetCommand = connection.CreateCommand();
            deleteAssetCommand.Transaction = transaction;
            deleteAssetCommand.CommandText = "DELETE FROM CropAsset WHERE Id = @CropAssetId;";
            AddParameter(deleteAssetCommand, "@CropAssetId", cropAssetId);
            await deleteAssetCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return true;
    }

    public async Task<IReadOnlyList<ProjectCropRecord>> GetProjectCropsAsync(string projectName, IEnumerable<string> labelNames)
    {
        var normalizedLabels = labelNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedLabels.Length == 0)
        {
            return Array.Empty<ProjectCropRecord>();
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        var labelParameterNames = new List<string>(normalizedLabels.Length);
        for (var index = 0; index < normalizedLabels.Length; index++)
        {
            var parameterName = $"@Label{index}";
            labelParameterNames.Add(parameterName);
            AddParameter(command, parameterName, normalizedLabels[index]);
        }

        command.CommandText =
            $"""
            SELECT
                pcl.ProjectName,
                pcl.LabelName,
                pcl.IsVariation,
                ca.SourceImagePath,
                ca.CropImagePath,
                ca.X,
                ca.Y,
                ca.Width,
                ca.Height
            FROM ProjectCropLink pcl
            INNER JOIN CropAsset ca ON ca.Id = pcl.CropAssetId
            WHERE pcl.ProjectName = @ProjectName
              AND lower(trim(pcl.LabelName)) IN ({string.Join(", ", labelParameterNames)})
            ORDER BY ca.SourceImagePath, pcl.LabelName, ca.Id;
            """;
        AddParameter(command, "@ProjectName", projectName);

        var result = new List<ProjectCropRecord>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var labelName = reader.GetString(1).Trim().ToLowerInvariant();
            result.Add(new ProjectCropRecord(
                reader.GetString(0),
                labelName,
                reader.GetInt32(2) != 0,
                ResolveStoredPath(reader.GetString(3)),
                ResolveStoredPath(reader.GetString(4)),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }

        return result;
    }

    public async Task<IReadOnlyList<ProjectCropRecord>> GetAllProjectCropsAsync(string projectName)
    {
        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                pcl.ProjectName,
                pcl.LabelName,
                pcl.IsVariation,
                ca.SourceImagePath,
                ca.CropImagePath,
                ca.X,
                ca.Y,
                ca.Width,
                ca.Height
            FROM ProjectCropLink pcl
            INNER JOIN CropAsset ca ON ca.Id = pcl.CropAssetId
            WHERE pcl.ProjectName = @ProjectName
            ORDER BY pcl.UpdatedAtUtc DESC, ca.Id DESC;
            """;
        AddParameter(command, "@ProjectName", projectName);

        var result = new List<ProjectCropRecord>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new ProjectCropRecord(
                reader.GetString(0),
                reader.GetString(1).Trim().ToLowerInvariant(),
                reader.GetInt32(2) != 0,
                ResolveStoredPath(reader.GetString(3)),
                ResolveStoredPath(reader.GetString(4)),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }

        return result;
    }

    public async Task<IReadOnlyList<ProjectCropRecord>> GetProjectCropsByLabelAsync(string projectName, string labelName, bool isVariation)
    {
        var normalizedLabelName = string.IsNullOrWhiteSpace(labelName)
            ? string.Empty
            : labelName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedLabelName))
        {
            return Array.Empty<ProjectCropRecord>();
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                pcl.ProjectName,
                pcl.LabelName,
                pcl.IsVariation,
                ca.SourceImagePath,
                ca.CropImagePath,
                ca.X,
                ca.Y,
                ca.Width,
                ca.Height
            FROM ProjectCropLink pcl
            INNER JOIN CropAsset ca ON ca.Id = pcl.CropAssetId
            WHERE pcl.ProjectName = @ProjectName
              AND lower(trim(pcl.LabelName)) = @LabelName
              AND pcl.IsVariation = @IsVariation
            ORDER BY pcl.UpdatedAtUtc DESC, ca.Id DESC;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@LabelName", normalizedLabelName);
        AddParameter(command, "@IsVariation", isVariation ? 1 : 0);

        var result = new List<ProjectCropRecord>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new ProjectCropRecord(
                reader.GetString(0),
                reader.GetString(1).Trim().ToLowerInvariant(),
                reader.GetInt32(2) != 0,
                ResolveStoredPath(reader.GetString(3)),
                ResolveStoredPath(reader.GetString(4)),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }

        return result;
    }

    public async Task<ProjectCropRecord?> GetProjectCropByImagePathAsync(string projectName, string cropImagePath)
    {
        var storedCropPath = ConvertToStoredPath(cropImagePath);

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                pcl.ProjectName,
                pcl.LabelName,
                pcl.IsVariation,
                ca.SourceImagePath,
                ca.CropImagePath,
                ca.X,
                ca.Y,
                ca.Width,
                ca.Height
            FROM ProjectCropLink pcl
            INNER JOIN CropAsset ca ON ca.Id = pcl.CropAssetId
            WHERE pcl.ProjectName = @ProjectName
              AND ca.CropImagePath = @CropImagePath
            ORDER BY pcl.UpdatedAtUtc DESC
            LIMIT 1;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@CropImagePath", storedCropPath);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ProjectCropRecord(
            reader.GetString(0),
            reader.GetString(1).Trim().ToLowerInvariant(),
            reader.GetInt32(2) != 0,
            ResolveStoredPath(reader.GetString(3)),
            ResolveStoredPath(reader.GetString(4)),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8));
    }

    public async Task<int> DeleteCropsBySourceImageAsync(string projectName, string sourceImagePath)
    {
        var normalizedSourcePath = Path.GetFullPath(sourceImagePath);
        var storedSourcePath = ConvertToStoredPath(normalizedSourcePath);
        var itemsToDelete = new List<(string LabelName, string CropImagePath)>();
        var projectImageBlobService = new ProjectImageBlobService();

        await using (var connection = SharedDatabase.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT pcl.LabelName, ca.CropImagePath
                FROM ProjectCropLink pcl
                INNER JOIN CropAsset ca ON ca.Id = pcl.CropAssetId
                WHERE pcl.ProjectName = @ProjectName
                  AND ca.SourceImagePath = @SourceImagePath
                ORDER BY pcl.Id;
                """;
            AddParameter(command, "@ProjectName", projectName);
            AddParameter(command, "@SourceImagePath", storedSourcePath);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                itemsToDelete.Add((reader.GetString(0), ResolveStoredPath(reader.GetString(1))));
            }
        }

        var deletedCount = 0;
        foreach (var item in itemsToDelete)
        {
            if (await DeleteCropAsync(projectName, item.LabelName, item.CropImagePath))
            {
                if (File.Exists(item.CropImagePath))
                {
                    File.Delete(item.CropImagePath);
                }

                await projectImageBlobService.DeleteImageAsync(projectName, item.CropImagePath);
                deletedCount++;
            }
        }

        return deletedCount;
    }

    private string ConvertToStoredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_storageRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return Path.GetRelativePath(_storageRootPath, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }

    private string ResolveStoredPath(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(storedPath))
        {
            return Path.GetFullPath(storedPath);
        }

        var normalizedRelativePath = storedPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(_storageRootPath, normalizedRelativePath));
    }

    private static async Task<string> ComputeFileSha256Async(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hashBytes);
    }

    private void EnsureProjectCropVariationColumn()
    {
        using var connection = SharedDatabase.CreateConnection();
        connection.Open();
        if (SharedDatabase.IsPostgresConfigured())
        {
            using var alterPgCommand = connection.CreateCommand();
            alterPgCommand.CommandText = "ALTER TABLE ProjectCropLink ADD COLUMN IF NOT EXISTS IsVariation INTEGER NOT NULL DEFAULT 0;";
            alterPgCommand.ExecuteNonQuery();
            return;
        }

        using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "PRAGMA table_info(ProjectCropLink);";
        using var reader = checkCommand.ExecuteReader();

        var hasVariationColumn = false;
        while (reader.Read())
        {
            if (string.Equals(Convert.ToString(reader["name"]), "IsVariation", StringComparison.OrdinalIgnoreCase))
            {
                hasVariationColumn = true;
                break;
            }
        }

        if (!hasVariationColumn)
        {
            using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE ProjectCropLink ADD COLUMN IsVariation INTEGER NOT NULL DEFAULT 0;";
            alterCommand.ExecuteNonQuery();
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

internal sealed record ProjectCropRecord(
    string ProjectName,
    string LabelName,
    bool IsVariation,
    string SourceImagePath,
    string CropImagePath,
    int X,
    int Y,
    int Width,
    int Height);
