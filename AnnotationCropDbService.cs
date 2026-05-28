using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Data.Common;

namespace WhatJolo;

internal sealed class AnnotationCropDbService
{
    private readonly ProjectWorkspaceService _workspaceService;
    private readonly ProjectImageBlobService _projectImageBlobService;

    public AnnotationCropDbService()
    {
        _workspaceService = new ProjectWorkspaceService();
        _projectImageBlobService = new ProjectImageBlobService();
    }

    public async Task SaveCropAsync(string projectName, string labelName, string sourceImagePath, string cropImagePath, Int32Rect bounds, bool isVariation = false)
    {
        var cropHash = await ComputeFileSha256Async(cropImagePath);
        var sourceImageKey = ProjectAssetKey.BuildSourceImageKey(_workspaceService, projectName, sourceImagePath);
        var cropImageKey = ProjectAssetKey.BuildCropImageKey(_workspaceService, projectName, labelName, isVariation, cropImagePath);
        var sourceImageAsset = await _projectImageBlobService.EnsureImageAssetAsync(
            projectName,
            sourceImagePath,
            sourceImageKey.StartsWith("variation-source|", StringComparison.OrdinalIgnoreCase) ? "variation-source" : "capture");
        var cropImageAsset = await _projectImageBlobService.EnsureImageAssetAsync(
            projectName,
            cropImagePath,
            isVariation ? "variation-crop" : "crop");

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
                    SourceImageKey,
                    CropImageKey,
                    SourceImageBlobId,
                    CropImageBlobId,
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
                    @SourceImageKey,
                    @CropImageKey,
                    @SourceImageBlobId,
                    @CropImageBlobId,
                    @CropHash,
                    @X,
                    @Y,
                    @Width,
                    @Height,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                )
                ON CONFLICT(CropHash) DO UPDATE SET
                    SourceImageKey = excluded.SourceImageKey,
                    CropImageKey = excluded.CropImageKey,
                    SourceImageBlobId = excluded.SourceImageBlobId,
                    CropImageBlobId = excluded.CropImageBlobId,
                    X = excluded.X,
                    Y = excluded.Y,
                    Width = excluded.Width,
                    Height = excluded.Height,
                    UpdatedAtUtc = CURRENT_TIMESTAMP;

                SELECT Id FROM CropAsset WHERE CropHash = @CropHash;
                """;
            AddParameter(cropCommand, "@SourceImageKey", sourceImageKey);
            AddParameter(cropCommand, "@CropImageKey", cropImageKey);
            AddParameter(cropCommand, "@SourceImageBlobId", sourceImageAsset.Id);
            AddParameter(cropCommand, "@CropImageBlobId", cropImageAsset.Id);
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

    public async Task<bool> DeleteCropAsync(string projectName, string labelName, string cropImageKey)
    {
        if (string.IsNullOrWhiteSpace(cropImageKey))
        {
            return false;
        }

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
                  AND ca.CropImageKey = @CropImageKey
                LIMIT 1;
                """;
            AddParameter(selectCommand, "@ProjectName", projectName);
            AddParameter(selectCommand, "@LabelName", labelName);
            AddParameter(selectCommand, "@CropImageKey", cropImageKey);

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
                ca.SourceImageKey,
                ca.CropImageKey,
                ca.X,
                ca.Y,
                ca.Width,
                ca.Height
            FROM ProjectCropLink pcl
            INNER JOIN CropAsset ca ON ca.Id = pcl.CropAssetId
            WHERE pcl.ProjectName = @ProjectName
              AND lower(trim(pcl.LabelName)) IN ({string.Join(", ", labelParameterNames)})
            ORDER BY ca.SourceImageKey, pcl.LabelName, ca.Id;
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
                reader.GetString(3),
                reader.GetString(4),
                ProjectAssetKey.ResolveSourceImagePath(_workspaceService, reader.GetString(0), reader.GetString(3)),
                ProjectAssetKey.ResolveCropImagePath(_workspaceService, reader.GetString(0), reader.GetString(4)),
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
                ca.SourceImageKey,
                ca.CropImageKey,
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
                reader.GetString(3),
                reader.GetString(4),
                ProjectAssetKey.ResolveSourceImagePath(_workspaceService, reader.GetString(0), reader.GetString(3)),
                ProjectAssetKey.ResolveCropImagePath(_workspaceService, reader.GetString(0), reader.GetString(4)),
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
                ca.SourceImageKey,
                ca.CropImageKey,
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
                reader.GetString(3),
                reader.GetString(4),
                ProjectAssetKey.ResolveSourceImagePath(_workspaceService, reader.GetString(0), reader.GetString(3)),
                ProjectAssetKey.ResolveCropImagePath(_workspaceService, reader.GetString(0), reader.GetString(4)),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }

        return result;
    }

    public async Task<ProjectCropRecord?> GetProjectCropByImageKeyAsync(string projectName, string cropImageKey)
    {
        if (string.IsNullOrWhiteSpace(cropImageKey))
        {
            return null;
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
                ca.SourceImageKey,
                ca.CropImageKey,
                ca.X,
                ca.Y,
                ca.Width,
                ca.Height
            FROM ProjectCropLink pcl
            INNER JOIN CropAsset ca ON ca.Id = pcl.CropAssetId
            WHERE pcl.ProjectName = @ProjectName
              AND ca.CropImageKey = @CropImageKey
            ORDER BY pcl.UpdatedAtUtc DESC
            LIMIT 1;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@CropImageKey", cropImageKey);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ProjectCropRecord(
            reader.GetString(0),
            reader.GetString(1).Trim().ToLowerInvariant(),
            reader.GetInt32(2) != 0,
            reader.GetString(3),
            reader.GetString(4),
            ProjectAssetKey.ResolveSourceImagePath(_workspaceService, reader.GetString(0), reader.GetString(3)),
            ProjectAssetKey.ResolveCropImagePath(_workspaceService, reader.GetString(0), reader.GetString(4)),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8));
    }

    public async Task<int> DeleteCropsBySourceImageKeyAsync(string projectName, string sourceImageKey)
    {
        if (string.IsNullOrWhiteSpace(sourceImageKey))
        {
            return 0;
        }

        var itemsToDelete = new List<(string LabelName, string CropImageKey, string CropImagePath, bool IsVariation)>();
        var projectImageBlobService = new ProjectImageBlobService();

        await using (var connection = SharedDatabase.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT pcl.LabelName, pcl.IsVariation, ca.CropImageKey
                FROM ProjectCropLink pcl
                INNER JOIN CropAsset ca ON ca.Id = pcl.CropAssetId
                WHERE pcl.ProjectName = @ProjectName
                  AND ca.SourceImageKey = @SourceImageKey
                ORDER BY pcl.Id;
                """;
            AddParameter(command, "@ProjectName", projectName);
            AddParameter(command, "@SourceImageKey", sourceImageKey);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var labelName = reader.GetString(0);
                var isVariation = reader.GetInt32(1) != 0;
                var cropKey = reader.GetString(2);
                itemsToDelete.Add((
                    labelName,
                    cropKey,
                    ProjectAssetKey.ResolveCropImagePath(_workspaceService, projectName, cropKey),
                    isVariation));
            }
        }

        var deletedCount = 0;
        foreach (var item in itemsToDelete)
        {
            if (await DeleteCropAsync(projectName, item.LabelName, item.CropImageKey))
            {
                if (File.Exists(item.CropImagePath))
                {
                    File.Delete(item.CropImagePath);
                }

                await projectImageBlobService.DeleteImageAsync(projectName, item.CropImagePath, item.IsVariation ? "variation-crop" : "crop");
                deletedCount++;
            }
        }

        return deletedCount;
    }

    private static async Task<string> ComputeFileSha256Async(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hashBytes);
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
    string SourceImageKey,
    string CropImageKey,
    string SourceImagePath,
    string CropImagePath,
    int X,
    int Y,
    int Width,
    int Height);
