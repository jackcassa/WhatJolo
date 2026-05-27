using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Data.Common;

namespace WhatJolo;

internal sealed class AnnotationCropDbService
{
    private readonly ProjectWorkspaceService _workspaceService;

    public AnnotationCropDbService()
    {
        _workspaceService = new ProjectWorkspaceService();
    }

    public async Task SaveCropAsync(string projectName, string labelName, string sourceImagePath, string cropImagePath, Int32Rect bounds, bool isVariation = false)
    {
        EnsureProjectCropVariationColumn();
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
                    CreatedAtUtc,
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
                    CURRENT_TIMESTAMP,
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
            AddParameter(cropCommand, "@SourceImagePath", ConvertToStoredFileName(sourceImagePath));
            AddParameter(cropCommand, "@CropImagePath", ConvertToStoredFileName(cropImagePath));
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
                    CreatedAtUtc,
                    UpdatedAtUtc
                )
                VALUES
                (
                    @ProjectName,
                    @LabelName,
                    @CropAssetId,
                    @IsVariation,
                    CURRENT_TIMESTAMP,
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
        EnsureProjectCropVariationColumn();
        var storedCropPath = ConvertToStoredFileName(cropImagePath);

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
        EnsureProjectCropVariationColumn();
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
                ResolveSourceImagePath(reader.GetString(0), reader.GetString(3)),
                ResolveCropImagePath(reader.GetString(0), labelName, reader.GetString(4)),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }

        return result;
    }

    public async Task<IReadOnlyList<ProjectCropRecord>> GetAllProjectCropsAsync(string projectName)
    {
        EnsureProjectCropVariationColumn();
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
                ResolveSourceImagePath(reader.GetString(0), reader.GetString(3)),
                ResolveCropImagePath(reader.GetString(0), reader.GetString(1), reader.GetString(4)),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }

        return result;
    }

    public async Task<IReadOnlyList<ProjectCropRecord>> GetProjectCropsByLabelAsync(string projectName, string labelName, bool isVariation)
    {
        EnsureProjectCropVariationColumn();
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
                ResolveSourceImagePath(reader.GetString(0), reader.GetString(3)),
                ResolveCropImagePath(reader.GetString(0), reader.GetString(1), reader.GetString(4)),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }

        return result;
    }

    public async Task<ProjectCropRecord?> GetProjectCropByImagePathAsync(string projectName, string cropImagePath)
    {
        EnsureProjectCropVariationColumn();
        var storedCropPath = ConvertToStoredFileName(cropImagePath);

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
            ResolveSourceImagePath(reader.GetString(0), reader.GetString(3)),
            ResolveCropImagePath(reader.GetString(0), reader.GetString(1), reader.GetString(4)),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8));
    }

    public async Task<int> DeleteCropsBySourceImageAsync(string projectName, string sourceImagePath)
    {
        EnsureProjectCropVariationColumn();
        var storedSourcePath = ConvertToStoredFileName(sourceImagePath);
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
                itemsToDelete.Add((reader.GetString(0), ResolveCropImagePath(projectName, reader.GetString(0), reader.GetString(1))));
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

    private static string ConvertToStoredFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.GetFileName(path.Trim());
    }

    private string ResolveSourceImagePath(string projectName, string storedFileName)
    {
        var fileName = ConvertToStoredFileName(storedFileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var capturesPath = _workspaceService.GetCapturesPath(projectName);
        var variationPath = Path.Combine(capturesPath, "Variations", fileName);
        if (File.Exists(variationPath) || fileName.StartsWith("adb_capture_var_", StringComparison.OrdinalIgnoreCase))
        {
            return variationPath;
        }

        return Path.Combine(capturesPath, fileName);
    }

    private string ResolveCropImagePath(string projectName, string labelName, string storedFileName)
    {
        var fileName = ConvertToStoredFileName(storedFileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var safeClass = string.IsNullOrWhiteSpace(labelName) ? "crop" : labelName.Trim().ToLowerInvariant();
        return Path.Combine(_workspaceService.GetSavedCropsPath(projectName), safeClass, fileName);
    }

    private static async Task<string> ComputeFileSha256Async(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hashBytes);
    }

    private void EnsureProjectCropVariationColumn()
    {
        if (!SharedDatabase.IsDatabaseConnected())
        {
            return;
        }

        using var connection = SharedDatabase.CreateConnection();
        connection.Open();
        using var alterPgCommand = connection.CreateCommand();
        alterPgCommand.CommandText =
            """
            ALTER TABLE ProjectCropLink ADD COLUMN IF NOT EXISTS IsVariation INTEGER NOT NULL DEFAULT 0;

            UPDATE CropAsset
            SET
                SourceImagePath = regexp_replace(SourceImagePath, '^.*[\\\/]', ''),
                CropImagePath = regexp_replace(CropImagePath, '^.*[\\\/]', '')
            WHERE SourceImagePath LIKE '%\%' OR SourceImagePath LIKE '%/%'
               OR CropImagePath LIKE '%\%' OR CropImagePath LIKE '%/%';
            """;
        alterPgCommand.ExecuteNonQuery();
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
