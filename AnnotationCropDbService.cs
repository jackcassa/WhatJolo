using System.Data.Common;
using System.Security.Cryptography;
using System.Windows;

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

    public Task SaveCropAsync(string projectName, string labelName, string sourceImagePath, string cropImagePath, Int32Rect bounds, bool isVariation = false, string? originalCropImageKey = null)
    {
        throw new NotSupportedException("Il salvataggio path-based delle crop non e' consentito. Usa il flusso DB-driven con chiavi logiche e byte in memoria.");
    }

    public Task SaveCropAsync(string projectName, string labelName, string sourceImageKey, string cropImageKey, byte[] cropBytes, Int32Rect bounds, bool isVariation = false, string? originalCropImageKey = null)
    {
        if (isVariation)
        {
            throw new InvalidOperationException("Le variazioni devono essere salvate con SaveVariationAsync.");
        }

        return SaveNormalCropAsync(projectName, labelName, sourceImageKey, cropImageKey, cropBytes, bounds);
    }

    public async Task SaveVariationAsync(
        string projectName,
        string labelName,
        string originalCropImageKey,
        string sourceImageKey,
        string cropImageKey,
        byte[] sourceImageBytes,
        byte[] cropBytes,
        Int32Rect bounds)
    {
        if (string.IsNullOrWhiteSpace(projectName) ||
            string.IsNullOrWhiteSpace(labelName) ||
            string.IsNullOrWhiteSpace(originalCropImageKey) ||
            string.IsNullOrWhiteSpace(sourceImageKey) ||
            string.IsNullOrWhiteSpace(cropImageKey))
        {
            throw new InvalidOperationException("Dati variazione non validi.");
        }

        var safeLabel = ProjectAssetKey.NormalizeLabel(labelName);
        var sourceContentHash = Convert.ToHexString(SHA256.HashData(sourceImageBytes));
        var cropContentHash = Convert.ToHexString(SHA256.HashData(cropBytes));
        var sourceCompressedBytes = ProjectImageBlobService.Compress(sourceImageBytes);
        var cropCompressedBytes = ProjectImageBlobService.Compress(cropBytes);
        var projectId = await ResolveProjectIdAsync(projectName);
        var originalCropAssetId = await GetCropAssetIdByImageKeyAsync(projectName, originalCropImageKey)
            ?? throw new InvalidOperationException("Una variazione richiede una crop originale valida.");

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ProjectVariation
            (
                ProjectId,
                LabelName,
                OriginalCropAssetId,
                SourceImageKey,
                CropImageKey,
                SourceContentHash,
                SourceByteLength,
                SourceCompressedBytes,
                CropContentHash,
                CropByteLength,
                CropCompressedBytes,
                X,
                Y,
                Width,
                Height,
                CreatedAtUtc,
                UpdatedAtUtc
            )
            VALUES
            (
                @ProjectId,
                @LabelName,
                @OriginalCropAssetId,
                @SourceImageKey,
                @CropImageKey,
                @SourceContentHash,
                @SourceByteLength,
                @SourceCompressedBytes,
                @CropContentHash,
                @CropByteLength,
                @CropCompressedBytes,
                @X,
                @Y,
                @Width,
                @Height,
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            )
            ON CONFLICT(CropImageKey) DO UPDATE SET
                LabelName = excluded.LabelName,
                OriginalCropAssetId = excluded.OriginalCropAssetId,
                SourceImageKey = excluded.SourceImageKey,
                SourceContentHash = excluded.SourceContentHash,
                SourceByteLength = excluded.SourceByteLength,
                SourceCompressedBytes = excluded.SourceCompressedBytes,
                CropContentHash = excluded.CropContentHash,
                CropByteLength = excluded.CropByteLength,
                CropCompressedBytes = excluded.CropCompressedBytes,
                X = excluded.X,
                Y = excluded.Y,
                Width = excluded.Width,
                Height = excluded.Height,
                UpdatedAtUtc = CURRENT_TIMESTAMP;
            """;
        AddParameter(command, "@ProjectId", projectId);
        AddParameter(command, "@LabelName", safeLabel);
        AddParameter(command, "@OriginalCropAssetId", originalCropAssetId);
        AddParameter(command, "@SourceImageKey", sourceImageKey);
        AddParameter(command, "@CropImageKey", cropImageKey);
        AddParameter(command, "@SourceContentHash", sourceContentHash);
        AddParameter(command, "@SourceByteLength", sourceImageBytes.Length);
        AddParameter(command, "@SourceCompressedBytes", sourceCompressedBytes);
        AddParameter(command, "@CropContentHash", cropContentHash);
        AddParameter(command, "@CropByteLength", cropBytes.Length);
        AddParameter(command, "@CropCompressedBytes", cropCompressedBytes);
        AddParameter(command, "@X", bounds.X);
        AddParameter(command, "@Y", bounds.Y);
        AddParameter(command, "@Width", bounds.Width);
        AddParameter(command, "@Height", bounds.Height);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> DeleteCropAsync(string projectName, string labelName, string cropImageKey)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(cropImageKey))
        {
            return false;
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM CropAsset ca
            USING ProjectCropLink pcl
            WHERE pcl.CropAssetId = ca.Id
              AND pcl.ProjectName = @ProjectName
              AND pcl.LabelName = @LabelName
              AND ca.CropImageKey = @CropImageKey;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@LabelName", ProjectAssetKey.NormalizeLabel(labelName));
        AddParameter(command, "@CropImageKey", cropImageKey);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteVariationAsync(string projectName, string variationCropImageKey)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(variationCropImageKey))
        {
            return false;
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM ProjectVariation pv
            USING ProjectInfo pinfo
            WHERE pv.ProjectId = pinfo.Id
              AND pinfo.ProjectName = @ProjectName
              AND pv.CropImageKey = @CropImageKey;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@CropImageKey", variationCropImageKey);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> DeleteVariationsByOriginalCropImageKeyAsync(string projectName, string originalCropImageKey)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(originalCropImageKey))
        {
            return 0;
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM ProjectVariation pv
            USING CropAsset ca, ProjectInfo pinfo
            WHERE pv.ProjectId = pinfo.Id
              AND pv.OriginalCropAssetId = ca.Id
              AND pinfo.ProjectName = @ProjectName
              AND ca.CropImageKey = @OriginalCropImageKey;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@OriginalCropImageKey", originalCropImageKey);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ProjectCropRecord>> GetProjectCropsAsync(string projectName, IEnumerable<string> labelNames)
    {
        var normalizedLabels = labelNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(ProjectAssetKey.NormalizeLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedLabels.Length == 0)
        {
            return Array.Empty<ProjectCropRecord>();
        }

        var results = new List<ProjectCropRecord>();
        foreach (var label in normalizedLabels)
        {
            results.AddRange(await GetProjectNormalCropsByLabelAsync(projectName, label));
            results.AddRange(await GetProjectVariationsByLabelAsync(projectName, label));
        }

        return results
            .OrderBy(record => record.SourceImageKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.LabelName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.CropImageKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<ProjectCropRecord>> GetAllProjectCropsAsync(string projectName)
    {
        var labels = await GetProjectLabelsAsync(projectName);
        return await GetProjectCropsAsync(projectName, labels);
    }

    public async Task<IReadOnlyList<ProjectCropRecord>> GetProjectCropsByLabelAsync(string projectName, string labelName, bool isVariation)
    {
        return isVariation
            ? await GetProjectVariationsByLabelAsync(projectName, labelName)
            : await GetProjectNormalCropsByLabelAsync(projectName, labelName);
    }

    public async Task<IReadOnlyList<ProjectCropRecord>> GetProjectCropsByLabelAndSourceImageAsync(string projectName, string labelName, string sourceImageKey)
    {
        var normalizedLabelName = ProjectAssetKey.NormalizeLabel(labelName);
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(sourceImageKey))
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
                0 AS IsVariation,
                ca.SourceImageKey,
                ca.CropImageKey,
                NULL AS OriginalCropImageKey,
                ca.CompressedBytes,
                ca.X,
                ca.Y,
                ca.Width,
                ca.Height
            FROM ProjectCropLink pcl
            INNER JOIN CropAsset ca ON ca.Id = pcl.CropAssetId
            WHERE pcl.ProjectName = @ProjectName
              AND lower(trim(pcl.LabelName)) = @LabelName
              AND ca.SourceImageKey = @SourceImageKey
            ORDER BY pcl.UpdatedAtUtc DESC, ca.Id DESC;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@LabelName", normalizedLabelName);
        AddParameter(command, "@SourceImageKey", sourceImageKey);
        return await ReadProjectCropRecordsAsync(command);
    }

    public async Task<IReadOnlySet<string>> GetSourceImageKeysForLabelAsync(string projectName, string labelName)
    {
        var normalizedLabelName = ProjectAssetKey.NormalizeLabel(labelName);
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT ca.SourceImageKey
            FROM ProjectCropLink pcl
            INNER JOIN CropAsset ca ON ca.Id = pcl.CropAssetId
            WHERE pcl.ProjectName = @ProjectName
              AND lower(trim(pcl.LabelName)) = @LabelName
              AND ca.SourceImageKey IS NOT NULL
              AND ca.SourceImageKey <> '';
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@LabelName", normalizedLabelName);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    public async Task<IReadOnlySet<string>> GetAnnotatedSourceImageKeysAsync(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT ca.SourceImageKey
            FROM ProjectCropLink pcl
            INNER JOIN CropAsset ca ON ca.Id = pcl.CropAssetId
            WHERE pcl.ProjectName = @ProjectName
              AND ca.SourceImageKey IS NOT NULL
              AND ca.SourceImageKey <> '';
            """;
        AddParameter(command, "@ProjectName", projectName);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    public async Task<ProjectCropRecord?> GetProjectCropByImageKeyAsync(string projectName, string cropImageKey)
    {
        if (string.IsNullOrWhiteSpace(cropImageKey))
        {
            return null;
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();

        await using (var normalCommand = connection.CreateCommand())
        {
            normalCommand.CommandText =
                """
                SELECT
                    pcl.ProjectName,
                    pcl.LabelName,
                    0 AS IsVariation,
                    ca.SourceImageKey,
                    ca.CropImageKey,
                    NULL AS OriginalCropImageKey,
                    ca.CompressedBytes,
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
            AddParameter(normalCommand, "@ProjectName", projectName);
            AddParameter(normalCommand, "@CropImageKey", cropImageKey);

            var normalRecords = await ReadProjectCropRecordsAsync(normalCommand);
            if (normalRecords.Count > 0)
            {
                return normalRecords[0];
            }
        }

        await using var variationCommand = connection.CreateCommand();
        variationCommand.CommandText =
            """
            SELECT
                pinfo.ProjectName,
                pv.LabelName,
                1 AS IsVariation,
                pv.SourceImageKey,
                pv.CropImageKey,
                ca.CropImageKey AS OriginalCropImageKey,
                pv.CropCompressedBytes,
                pv.X,
                pv.Y,
                pv.Width,
                pv.Height
            FROM ProjectVariation pv
            INNER JOIN ProjectInfo pinfo ON pinfo.Id = pv.ProjectId
            INNER JOIN CropAsset ca ON ca.Id = pv.OriginalCropAssetId
            WHERE pinfo.ProjectName = @ProjectName
              AND pv.CropImageKey = @CropImageKey
            ORDER BY pv.UpdatedAtUtc DESC
            LIMIT 1;
            """;
        AddParameter(variationCommand, "@ProjectName", projectName);
        AddParameter(variationCommand, "@CropImageKey", cropImageKey);

        var variationRecords = await ReadProjectCropRecordsAsync(variationCommand);
        return variationRecords.FirstOrDefault();
    }

    public async Task<IReadOnlyList<ProjectCropRecord>> GetVariationsByOriginalCropImageKeyAsync(string projectName, string originalCropImageKey)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(originalCropImageKey))
        {
            return Array.Empty<ProjectCropRecord>();
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                pinfo.ProjectName,
                pv.LabelName,
                1 AS IsVariation,
                pv.SourceImageKey,
                pv.CropImageKey,
                ca.CropImageKey AS OriginalCropImageKey,
                pv.CropCompressedBytes,
                pv.X,
                pv.Y,
                pv.Width,
                pv.Height
            FROM ProjectVariation pv
            INNER JOIN ProjectInfo pinfo ON pinfo.Id = pv.ProjectId
            INNER JOIN CropAsset ca ON ca.Id = pv.OriginalCropAssetId
            WHERE pinfo.ProjectName = @ProjectName
              AND ca.CropImageKey = @OriginalCropImageKey
            ORDER BY pv.UpdatedAtUtc DESC, pv.Id DESC;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@OriginalCropImageKey", originalCropImageKey);
        return await ReadProjectCropRecordsAsync(command);
    }

    private async Task SaveNormalCropAsync(string projectName, string labelName, string sourceImageKey, string cropImageKey, byte[] cropBytes, Int32Rect bounds)
    {
        var cropHash = Convert.ToHexString(SHA256.HashData(cropBytes));
        var compressedCropBytes = ProjectImageBlobService.Compress(cropBytes);
        var cropContentHash = Convert.ToHexString(SHA256.HashData(cropBytes));
        var sourceImageAsset = await _projectImageBlobService.GetImageAssetByKeyAsync(projectName, sourceImageKey);
        if (sourceImageAsset == null || sourceImageAsset.StorageKind != "capture")
        {
            throw new InvalidOperationException($"Immagine sorgente non valida per una crop normale: {sourceImageKey}");
        }

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
                    SourceProjectImageId,
                    CropHash,
                    ContentHash,
                    ByteLength,
                    CompressedBytes,
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
                    @SourceProjectImageId,
                    @CropHash,
                    @ContentHash,
                    @ByteLength,
                    @CompressedBytes,
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
                    SourceProjectImageId = excluded.SourceProjectImageId,
                    ContentHash = excluded.ContentHash,
                    ByteLength = excluded.ByteLength,
                    CompressedBytes = excluded.CompressedBytes,
                    X = excluded.X,
                    Y = excluded.Y,
                    Width = excluded.Width,
                    Height = excluded.Height,
                    UpdatedAtUtc = CURRENT_TIMESTAMP;

                SELECT Id FROM CropAsset WHERE CropHash = @CropHash;
                """;
            AddParameter(cropCommand, "@SourceImageKey", sourceImageKey);
            AddParameter(cropCommand, "@CropImageKey", cropImageKey);
            AddParameter(cropCommand, "@SourceProjectImageId", sourceImageAsset.Id);
            AddParameter(cropCommand, "@CropHash", cropHash);
            AddParameter(cropCommand, "@ContentHash", cropContentHash);
            AddParameter(cropCommand, "@ByteLength", cropBytes.Length);
            AddParameter(cropCommand, "@CompressedBytes", compressedCropBytes);
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
                    0,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                )
                ON CONFLICT(ProjectName, LabelName, CropAssetId) DO UPDATE SET
                    UpdatedAtUtc = CURRENT_TIMESTAMP;
                """;
            AddParameter(linkCommand, "@ProjectName", projectName);
            AddParameter(linkCommand, "@LabelName", ProjectAssetKey.NormalizeLabel(labelName));
            AddParameter(linkCommand, "@CropAssetId", cropAssetId);
            await linkCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private async Task<IReadOnlyList<ProjectCropRecord>> GetProjectNormalCropsByLabelAsync(string projectName, string labelName)
    {
        var normalizedLabelName = ProjectAssetKey.NormalizeLabel(labelName);
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
                0 AS IsVariation,
                ca.SourceImageKey,
                ca.CropImageKey,
                NULL AS OriginalCropImageKey,
                ca.CompressedBytes,
                ca.X,
                ca.Y,
                ca.Width,
                ca.Height
            FROM ProjectCropLink pcl
            INNER JOIN CropAsset ca ON ca.Id = pcl.CropAssetId
            WHERE pcl.ProjectName = @ProjectName
              AND lower(trim(pcl.LabelName)) = @LabelName
            ORDER BY pcl.UpdatedAtUtc DESC, ca.Id DESC;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@LabelName", normalizedLabelName);
        return await ReadProjectCropRecordsAsync(command);
    }

    private async Task<IReadOnlyList<ProjectCropRecord>> GetProjectVariationsByLabelAsync(string projectName, string labelName)
    {
        var normalizedLabelName = ProjectAssetKey.NormalizeLabel(labelName);
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
                pinfo.ProjectName,
                pv.LabelName,
                1 AS IsVariation,
                pv.SourceImageKey,
                pv.CropImageKey,
                ca.CropImageKey AS OriginalCropImageKey,
                pv.CropCompressedBytes,
                pv.X,
                pv.Y,
                pv.Width,
                pv.Height
            FROM ProjectVariation pv
            INNER JOIN ProjectInfo pinfo ON pinfo.Id = pv.ProjectId
            INNER JOIN CropAsset ca ON ca.Id = pv.OriginalCropAssetId
            WHERE pinfo.ProjectName = @ProjectName
              AND lower(trim(pv.LabelName)) = @LabelName
            ORDER BY pv.UpdatedAtUtc DESC, pv.Id DESC;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@LabelName", normalizedLabelName);
        return await ReadProjectCropRecordsAsync(command);
    }

    private async Task<IReadOnlyList<string>> GetProjectLabelsAsync(string projectName)
    {
        var labels = new List<string>();
        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT lower(trim(LabelName))
            FROM ProjectCropLink
            WHERE ProjectName = @ProjectName
            ORDER BY lower(trim(LabelName));
            """;
        AddParameter(command, "@ProjectName", projectName);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0))
            {
                labels.Add(reader.GetString(0));
            }
        }

        return labels;
    }

    private async Task<long?> GetCropAssetIdByImageKeyAsync(string projectName, string? cropImageKey)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(cropImageKey))
        {
            return null;
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ca.Id
            FROM CropAsset ca
            INNER JOIN ProjectCropLink pcl ON pcl.CropAssetId = ca.Id
            WHERE pcl.ProjectName = @ProjectName
              AND ca.CropImageKey = @CropImageKey
            ORDER BY pcl.UpdatedAtUtc DESC
            LIMIT 1;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@CropImageKey", cropImageKey);

        var result = await command.ExecuteScalarAsync();
        return result == null || result == DBNull.Value
            ? null
            : Convert.ToInt64(result);
    }

    private async Task<long> ResolveProjectIdAsync(string projectName)
    {
        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id
            FROM ProjectInfo
            WHERE ProjectName = @ProjectName
            LIMIT 1;
            """;
        AddParameter(command, "@ProjectName", projectName);
        var result = await command.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value)
        {
            throw new InvalidOperationException($"ProjectInfo non trovato per il progetto '{projectName}'.");
        }

        return Convert.ToInt64(result);
    }

    private async Task<IReadOnlyList<ProjectCropRecord>> ReadProjectCropRecordsAsync(DbCommand command)
    {
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
                reader.IsDBNull(5) ? null : reader.GetString(5),
                ProjectImageBlobService.Decompress((byte[])reader.GetValue(6)),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10)));
        }

        return result;
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
    string? OriginalCropImageKey,
    byte[] CropImageBytes,
    int X,
    int Y,
    int Width,
    int Height);
