using System.Data.Common;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace WhatJolo;

public sealed class ProjectImageBlobService
{
    private readonly ProjectModelBlobService _projectModelBlobService;
    private readonly ProjectWorkspaceService _workspaceService;

    public ProjectImageBlobService()
    {
        _projectModelBlobService = new ProjectModelBlobService();
        _workspaceService = new ProjectWorkspaceService();
    }

    public async Task SaveImageAsync(string projectName, string imagePath, string imageKind)
    {
        var normalizedKind = NormalizeKind(imageKind);
        if (!IsBaseProjectImageKind(normalizedKind))
        {
            throw new InvalidOperationException($"Il salvataggio '{normalizedKind}' non passa da ProjectImage. Usa il flusso dedicato.");
        }

        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            throw new FileNotFoundException("Immagine progetto non trovata.", imagePath);
        }

        var originalBytes = await File.ReadAllBytesAsync(imagePath);
        await EnsureProjectImageAssetAsync(projectName, imagePath, normalizedKind, originalBytes);
    }

    public Task SaveVariationSourceImageAsync(string projectName, string imagePath, string originalImagePathOrKey, string originalCropImageKey)
    {
        throw new NotSupportedException("Le variazioni sono gestite dalla tabella ProjectVariation tramite AnnotationCropDbService.");
    }

    public async Task<ProjectImageAssetRecord?> GetImageAssetByKeyAsync(string projectName, string imageKey)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(imageKey))
        {
            return null;
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        return await GetImageAssetByKeyAsync(connection, projectName, imageKey);
    }

    public async Task<byte[]?> GetImageBytesByKeyAsync(string projectName, string imageKey)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(imageKey))
        {
            return null;
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();

        if (IsVariationSourceKey(imageKey) || IsVariationCropKey(imageKey))
        {
            await using var variationCommand = connection.CreateCommand();
            variationCommand.CommandText =
                """
                SELECT
                    CASE
                        WHEN @ReturnSource = 1 THEN pv.SourceCompressedBytes
                        ELSE pv.CropCompressedBytes
                    END
                FROM ProjectVariation pv
                INNER JOIN ProjectInfo pinfo ON pinfo.Id = pv.ProjectId
                WHERE pinfo.ProjectName = @ProjectName
                  AND (
                        pv.SourceImageKey = @ImageKey
                     OR pv.CropImageKey = @ImageKey
                  )
                LIMIT 1;
                """;
            AddParameter(variationCommand, "@ProjectName", projectName);
            AddParameter(variationCommand, "@ImageKey", imageKey);
            AddParameter(variationCommand, "@ReturnSource", IsVariationSourceKey(imageKey) ? 1 : 0);

            var variationBytes = await variationCommand.ExecuteScalarAsync();
            if (variationBytes is byte[] variationCompressedBytes)
            {
                return Decompress(variationCompressedBytes);
            }
        }

        if (IsNormalCropKey(imageKey))
        {
            await using var cropCommand = connection.CreateCommand();
            cropCommand.CommandText =
                """
                SELECT ca.CompressedBytes
                FROM CropAsset ca
                INNER JOIN ProjectCropLink pcl ON pcl.CropAssetId = ca.Id
                WHERE pcl.ProjectName = @ProjectName
                  AND ca.CropImageKey = @ImageKey
                LIMIT 1;
                """;
            AddParameter(cropCommand, "@ProjectName", projectName);
            AddParameter(cropCommand, "@ImageKey", imageKey);

            var cropBytes = await cropCommand.ExecuteScalarAsync();
            if (cropBytes is byte[] cropCompressedBytes)
            {
                return Decompress(cropCompressedBytes);
            }
        }

        await using var projectCommand = connection.CreateCommand();
        projectCommand.CommandText =
            """
            SELECT pi.CompressedBytes
            FROM ProjectImage pi
            INNER JOIN ProjectInfo pinfo ON pinfo.Id = pi.ProjectId
            WHERE pinfo.ProjectName = @ProjectName
              AND pi.ImageKey = @ImageKey
            LIMIT 1;
            """;
        AddParameter(projectCommand, "@ProjectName", projectName);
        AddParameter(projectCommand, "@ImageKey", imageKey);

        var projectBytes = await projectCommand.ExecuteScalarAsync();
        return projectBytes is byte[] projectCompressedBytes
            ? Decompress(projectCompressedBytes)
            : null;
    }

    public async Task SaveImageBytesAsync(string projectName, string fileName, string imageKind, byte[] imageBytes)
    {
        var normalizedKind = NormalizeKind(imageKind);
        if (!IsBaseProjectImageKind(normalizedKind))
        {
            throw new InvalidOperationException($"Il salvataggio '{normalizedKind}' non passa da ProjectImage. Usa il flusso dedicato.");
        }

        await EnsureProjectImageAssetAsync(projectName, fileName, normalizedKind, imageBytes);
    }

    public Task SaveVariationSourceImageBytesAsync(string projectName, string fileName, byte[] imageBytes, string originalImageKey, string originalCropImageKey)
    {
        throw new NotSupportedException("Le variazioni sono gestite dalla tabella ProjectVariation tramite AnnotationCropDbService.");
    }

    public async Task<IReadOnlyList<ProjectStoredImageRecord>> GetProjectImagesByKindsAsync(string projectName, params string[] imageKinds)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return Array.Empty<ProjectStoredImageRecord>();
        }

        var requestedKinds = imageKinds
            .Where(static kind => !string.IsNullOrWhiteSpace(kind))
            .Select(NormalizeKind)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requestedKinds.Length == 0)
        {
            return Array.Empty<ProjectStoredImageRecord>();
        }

        var items = new List<ProjectStoredImageRecord>();
        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();

        var basePrefixes = requestedKinds
            .Where(IsBaseProjectImageKind)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (basePrefixes.Length > 0)
        {
            items.AddRange(await LoadStoredBaseImagesAsync(connection, projectName, basePrefixes));
        }

        if (requestedKinds.Contains("variation-source", StringComparer.OrdinalIgnoreCase))
        {
            items.AddRange(await LoadStoredVariationImagesAsync(connection, projectName));
        }

        return items
            .OrderByDescending(static item => item.UpdatedAtUtc)
            .ThenBy(static item => item.ImageKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task DeleteImageAsync(string projectName, string imagePathOrKey, string? imageKind = null)
    {
        await DeleteImagesWithCleanupAsync(projectName, new[] { NormalizeImageKey(projectName, imagePathOrKey, imageKind) });
    }

    public async Task<int> DeleteImagesWithCleanupAsync(string projectName, IEnumerable<string> imagePathsOrKeys, string? imageKind = null)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return 0;
        }

        var imageKeys = imagePathsOrKeys
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizeImageKey(projectName, value!, imageKind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (imageKeys.Length == 0)
        {
            return 0;
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var deletedRows = 0;
        deletedRows += await DeleteVariationsByKeysAsync(connection, transaction, projectName, imageKeys.Where(IsVariationKey).ToArray());
        deletedRows += await DeleteCropAssetsByKeysAsync(connection, transaction, projectName, imageKeys.Where(IsNormalCropKey).ToArray());
        deletedRows += await DeleteProjectImagesByKeysAsync(connection, transaction, projectName, imageKeys.Where(IsBaseImageKey).ToArray());

        await transaction.CommitAsync();
        return deletedRows;
    }

    public Task<int> DeleteUnreferencedImagesAsync(string projectName)
    {
        return Task.FromResult(0);
    }

    public async Task<int> SyncProjectAsync(string projectName)
    {
        var normalizedProjectName = _workspaceService.EnsureProject(projectName);
        var expectedImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var capturePath in EnumerateProjectImages(_workspaceService.GetCapturesPath(normalizedProjectName), includeSubdirectories: false))
        {
            expectedImages[Path.GetFullPath(capturePath)] = "capture";
        }

        foreach (var testImagePath in EnumerateProjectImages(_workspaceService.GetYoloTestPath(normalizedProjectName)))
        {
            expectedImages[Path.GetFullPath(testImagePath)] = "test";
        }

        foreach (var trainImagePath in EnumerateProjectImages(Path.Combine(_workspaceService.GetYoloDatasetPath(normalizedProjectName), "images", "train")))
        {
            expectedImages[Path.GetFullPath(trainImagePath)] = "dataset-train";
        }

        foreach (var valImagePath in EnumerateProjectImages(Path.Combine(_workspaceService.GetYoloDatasetPath(normalizedProjectName), "images", "val")))
        {
            expectedImages[Path.GetFullPath(valImagePath)] = "dataset-val";
        }

        foreach (var testDatasetImagePath in EnumerateProjectImages(Path.Combine(_workspaceService.GetYoloDatasetPath(normalizedProjectName), "images", "test")))
        {
            expectedImages[Path.GetFullPath(testDatasetImagePath)] = "dataset-test";
        }

        foreach (var image in expectedImages)
        {
            await SaveImageAsync(normalizedProjectName, image.Key, image.Value);
        }

        var existingBlobPaths = await LoadProjectImagesAsync(normalizedProjectName);
        foreach (var storedImage in existingBlobPaths)
        {
            var resolvedPath = ProjectAssetKey.ResolveProjectImagePath(_workspaceService, normalizedProjectName, storedImage.ImageKey, storedImage.StorageKind);
            if (!expectedImages.ContainsKey(resolvedPath))
            {
                await DeleteImageAsync(normalizedProjectName, resolvedPath, storedImage.StorageKind);
            }
        }

        return expectedImages.Count;
    }

    public async Task<ProjectRestoreResult> RestoreProjectAsync(string projectName)
    {
        var normalizedProjectName = _workspaceService.EnsureProject(projectName);
        Directory.CreateDirectory(_workspaceService.GetYoloProjectPath(normalizedProjectName));

        var restoredCount = 0;
        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();

        foreach (var image in await LoadAllStoredImagesAsync(connection, normalizedProjectName))
        {
            var resolvedPath = ProjectAssetKey.ResolveProjectImagePath(_workspaceService, normalizedProjectName, image.ImageKey, image.StorageKind);
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                continue;
            }

            var directoryPath = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            await File.WriteAllBytesAsync(resolvedPath, image.OriginalBytes);
            restoredCount++;
        }

        foreach (var cropImage in await LoadStoredCropImagesAsync(connection, normalizedProjectName))
        {
            var resolvedPath = ProjectAssetKey.ResolveCropImagePath(_workspaceService, normalizedProjectName, cropImage.ImageKey);
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                continue;
            }

            var directoryPath = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            await File.WriteAllBytesAsync(resolvedPath, cropImage.OriginalBytes);
            restoredCount++;
        }

        foreach (var variation in await LoadStoredVariationCropImagesAsync(connection, normalizedProjectName))
        {
            var resolvedPath = ProjectAssetKey.ResolveCropImagePath(_workspaceService, normalizedProjectName, variation.ImageKey);
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                continue;
            }

            var directoryPath = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            await File.WriteAllBytesAsync(resolvedPath, variation.OriginalBytes);
            restoredCount++;
        }

        var restoredModels = await _projectModelBlobService.RestoreAllBestOnnxToProjectAsync(normalizedProjectName);

        return new ProjectRestoreResult(
            normalizedProjectName,
            _workspaceService.GetProjectPath(normalizedProjectName),
            _workspaceService.GetYoloProjectPath(normalizedProjectName),
            restoredCount,
            restoredModels.Count);
    }

    private async Task<ProjectImageAssetRecord> EnsureProjectImageAssetAsync(string projectName, string fileNameOrPath, string imageKind, byte[] originalBytes)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(fileNameOrPath) || originalBytes.Length == 0)
        {
            throw new InvalidOperationException("Immagine progetto non valida.");
        }

        var imageKey = ProjectAssetKey.BuildProjectImageKey(_workspaceService, projectName, imageKind, fileNameOrPath);
        var compressedBytes = Compress(originalBytes);
        var contentHash = Convert.ToHexString(SHA256.HashData(originalBytes));

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        var projectId = await ResolveProjectIdAsync(connection, projectName);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ProjectImage
            (
                ProjectId,
                ImageKey,
                ContentHash,
                ByteLength,
                CompressedBytes,
                CreatedAtUtc,
                UpdatedAtUtc
            )
            VALUES
            (
                @ProjectId,
                @ImageKey,
                @ContentHash,
                @ByteLength,
                @CompressedBytes,
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            )
            ON CONFLICT(ProjectId, ImageKey) DO UPDATE SET
                ContentHash = excluded.ContentHash,
                ByteLength = excluded.ByteLength,
                CompressedBytes = excluded.CompressedBytes,
                UpdatedAtUtc = CURRENT_TIMESTAMP;
            """;
        AddParameter(command, "@ProjectId", projectId);
        AddParameter(command, "@ImageKey", imageKey);
        AddParameter(command, "@ContentHash", contentHash);
        AddParameter(command, "@ByteLength", originalBytes.Length);
        AddParameter(command, "@CompressedBytes", compressedBytes);
        await command.ExecuteNonQueryAsync();

        return await GetImageAssetByKeyAsync(connection, projectName, imageKey)
            ?? throw new InvalidOperationException($"Asset immagine non trovato dopo il salvataggio: {imageKey}");
    }

    private async Task<ProjectImageAssetRecord?> GetImageAssetByKeyAsync(DbConnection connection, string projectName, string imageKey)
    {
        if (IsVariationKey(imageKey))
        {
            await using var variationCommand = connection.CreateCommand();
            variationCommand.CommandText =
                """
                SELECT pv.Id, pinfo.ProjectName, @ImageKey, CASE WHEN pv.SourceImageKey = @ImageKey THEN 'variation-source' ELSE 'variation-crop' END
                FROM ProjectVariation pv
                INNER JOIN ProjectInfo pinfo ON pinfo.Id = pv.ProjectId
                WHERE pinfo.ProjectName = @ProjectName
                  AND (pv.SourceImageKey = @ImageKey OR pv.CropImageKey = @ImageKey)
                LIMIT 1;
                """;
            AddParameter(variationCommand, "@ProjectName", projectName);
            AddParameter(variationCommand, "@ImageKey", imageKey);

            await using var variationReader = await variationCommand.ExecuteReaderAsync();
            if (await variationReader.ReadAsync())
            {
                return new ProjectImageAssetRecord(
                    variationReader.GetInt64(0),
                    variationReader.GetString(1),
                    variationReader.GetString(2),
                    variationReader.GetString(3));
            }
        }

        if (IsNormalCropKey(imageKey))
        {
            await using var cropCommand = connection.CreateCommand();
            cropCommand.CommandText =
                """
                SELECT ca.Id, pcl.ProjectName, ca.CropImageKey, 'crop'
                FROM CropAsset ca
                INNER JOIN ProjectCropLink pcl ON pcl.CropAssetId = ca.Id
                WHERE pcl.ProjectName = @ProjectName
                  AND ca.CropImageKey = @ImageKey
                LIMIT 1;
                """;
            AddParameter(cropCommand, "@ProjectName", projectName);
            AddParameter(cropCommand, "@ImageKey", imageKey);

            await using var cropReader = await cropCommand.ExecuteReaderAsync();
            if (await cropReader.ReadAsync())
            {
                return new ProjectImageAssetRecord(
                    cropReader.GetInt64(0),
                    cropReader.GetString(1),
                    cropReader.GetString(2),
                    cropReader.GetString(3));
            }
        }

        await using var projectCommand = connection.CreateCommand();
        projectCommand.CommandText =
            """
            SELECT pi.Id, pinfo.ProjectName, pi.ImageKey, substring(pi.ImageKey from '^[^|]+')
            FROM ProjectImage pi
            INNER JOIN ProjectInfo pinfo ON pinfo.Id = pi.ProjectId
            WHERE pinfo.ProjectName = @ProjectName
              AND pi.ImageKey = @ImageKey
            LIMIT 1;
            """;
        AddParameter(projectCommand, "@ProjectName", projectName);
        AddParameter(projectCommand, "@ImageKey", imageKey);

        await using var projectReader = await projectCommand.ExecuteReaderAsync();
        if (!await projectReader.ReadAsync())
        {
            return null;
        }

        return new ProjectImageAssetRecord(
            projectReader.GetInt64(0),
            projectReader.GetString(1),
            projectReader.GetString(2),
            projectReader.GetString(3));
    }

    private async Task<IReadOnlyList<ProjectStoredImageRecord>> LoadStoredBaseImagesAsync(DbConnection connection, string projectName, IReadOnlyList<string> prefixes)
    {
        var results = new List<ProjectStoredImageRecord>();
        await using var command = connection.CreateCommand();
        var likeClauses = new List<string>(prefixes.Count);
        for (var index = 0; index < prefixes.Count; index++)
        {
            var parameterName = $"@Prefix{index}";
            likeClauses.Add($"pi.ImageKey LIKE {parameterName}");
            AddParameter(command, parameterName, $"{prefixes[index]}|%");
        }

        command.CommandText =
            $"""
            SELECT pi.ImageKey, pi.CompressedBytes, pi.UpdatedAtUtc
            FROM ProjectImage pi
            INNER JOIN ProjectInfo pinfo ON pinfo.Id = pi.ProjectId
            WHERE pinfo.ProjectName = @ProjectName
              AND ({string.Join(" OR ", likeClauses)})
            ORDER BY pi.UpdatedAtUtc DESC, pi.Id DESC;
            """;
        AddParameter(command, "@ProjectName", projectName);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var imageKey = reader.GetString(0);
            results.Add(new ProjectStoredImageRecord(
                imageKey,
                ExtractKeyPrefix(imageKey),
                Decompress((byte[])reader.GetValue(1)),
                ReadDateTimeValue(reader, 2)));
        }

        return results;
    }

    private async Task<IReadOnlyList<ProjectStoredImageRecord>> LoadStoredVariationImagesAsync(DbConnection connection, string projectName)
    {
        var results = new List<ProjectStoredImageRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT pv.SourceImageKey, pv.SourceCompressedBytes, pv.UpdatedAtUtc
            FROM ProjectVariation pv
            INNER JOIN ProjectInfo pinfo ON pinfo.Id = pv.ProjectId
            WHERE pinfo.ProjectName = @ProjectName
            ORDER BY pv.UpdatedAtUtc DESC, pv.Id DESC;
            """;
        AddParameter(command, "@ProjectName", projectName);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ProjectStoredImageRecord(
                reader.GetString(0),
                "variation-source",
                Decompress((byte[])reader.GetValue(1)),
                ReadDateTimeValue(reader, 2)));
        }

        return results;
    }

    private async Task<IReadOnlyList<ProjectStoredImageRecord>> LoadStoredCropImagesAsync(DbConnection connection, string projectName)
    {
        var results = new List<ProjectStoredImageRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT ca.CropImageKey, ca.CompressedBytes, ca.UpdatedAtUtc
            FROM CropAsset ca
            INNER JOIN ProjectCropLink pcl ON pcl.CropAssetId = ca.Id
            WHERE pcl.ProjectName = @ProjectName
            ORDER BY ca.UpdatedAtUtc DESC, ca.Id DESC;
            """;
        AddParameter(command, "@ProjectName", projectName);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var imageKey = reader.GetString(0);
            results.Add(new ProjectStoredImageRecord(
                imageKey,
                ExtractKeyPrefix(imageKey),
                Decompress((byte[])reader.GetValue(1)),
                ReadDateTimeValue(reader, 2)));
        }

        return results;
    }

    private async Task<IReadOnlyList<ProjectStoredImageRecord>> LoadStoredVariationCropImagesAsync(DbConnection connection, string projectName)
    {
        var results = new List<ProjectStoredImageRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT pv.CropImageKey, pv.CropCompressedBytes, pv.UpdatedAtUtc
            FROM ProjectVariation pv
            INNER JOIN ProjectInfo pinfo ON pinfo.Id = pv.ProjectId
            WHERE pinfo.ProjectName = @ProjectName
            ORDER BY pv.UpdatedAtUtc DESC, pv.Id DESC;
            """;
        AddParameter(command, "@ProjectName", projectName);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var imageKey = reader.GetString(0);
            results.Add(new ProjectStoredImageRecord(
                imageKey,
                ExtractKeyPrefix(imageKey),
                Decompress((byte[])reader.GetValue(1)),
                ReadDateTimeValue(reader, 2)));
        }

        return results;
    }

    private async Task<IReadOnlyList<ProjectStoredImageRecord>> LoadAllStoredImagesAsync(DbConnection connection, string projectName)
    {
        var results = new List<ProjectStoredImageRecord>();
        results.AddRange(await LoadStoredBaseImagesAsync(connection, projectName, new[] { "capture", "dataset-train", "dataset-val", "dataset-test", "test" }));
        results.AddRange(await LoadStoredVariationImagesAsync(connection, projectName));
        return results;
    }

    private async Task<IReadOnlyList<ProjectImageReference>> LoadProjectImagesAsync(string projectName)
    {
        var items = new List<ProjectImageReference>();
        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();

        await using var projectCommand = connection.CreateCommand();
        projectCommand.CommandText =
            """
            SELECT pi.ImageKey
            FROM ProjectImage pi
            INNER JOIN ProjectInfo pinfo ON pinfo.Id = pi.ProjectId
            WHERE pinfo.ProjectName = @ProjectName;
            """;
        AddParameter(projectCommand, "@ProjectName", projectName);

        await using var projectReader = await projectCommand.ExecuteReaderAsync();
        while (await projectReader.ReadAsync())
        {
            var imageKey = projectReader.GetString(0);
            items.Add(new ProjectImageReference(imageKey, ExtractKeyPrefix(imageKey)));
        }

        return items;
    }

    private async Task<long> ResolveProjectIdAsync(DbConnection connection, string projectName)
    {
        _workspaceService.EnsureProject(projectName);

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

    private async Task<int> DeleteCropAssetsByKeysAsync(DbConnection connection, DbTransaction transaction, string projectName, IReadOnlyList<string> cropKeys)
    {
        if (cropKeys.Count == 0)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameterNames = new List<string>(cropKeys.Count);
        for (var index = 0; index < cropKeys.Count; index++)
        {
            var parameterName = $"@CropImageKey{index}";
            parameterNames.Add(parameterName);
            AddParameter(command, parameterName, cropKeys[index]);
        }

        command.CommandText =
            $"""
            DELETE FROM CropAsset ca
            USING ProjectCropLink pcl
            WHERE pcl.CropAssetId = ca.Id
              AND pcl.ProjectName = @ProjectName
              AND ca.CropImageKey IN ({string.Join(", ", parameterNames)});
            """;
        AddParameter(command, "@ProjectName", projectName);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> DeleteVariationsByKeysAsync(DbConnection connection, DbTransaction transaction, string projectName, IReadOnlyList<string> imageKeys)
    {
        if (imageKeys.Count == 0)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameterNames = new List<string>(imageKeys.Count);
        for (var index = 0; index < imageKeys.Count; index++)
        {
            var parameterName = $"@VariationImageKey{index}";
            parameterNames.Add(parameterName);
            AddParameter(command, parameterName, imageKeys[index]);
        }

        command.CommandText =
            $"""
            DELETE FROM ProjectVariation pv
            USING ProjectInfo pinfo
            WHERE pv.ProjectId = pinfo.Id
              AND pinfo.ProjectName = @ProjectName
              AND (
                    pv.SourceImageKey IN ({string.Join(", ", parameterNames)})
                 OR pv.CropImageKey IN ({string.Join(", ", parameterNames)})
              );
            """;
        AddParameter(command, "@ProjectName", projectName);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> DeleteProjectImagesByKeysAsync(DbConnection connection, DbTransaction transaction, string projectName, IReadOnlyList<string> imageKeys)
    {
        if (imageKeys.Count == 0)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameterNames = new List<string>(imageKeys.Count);
        for (var index = 0; index < imageKeys.Count; index++)
        {
            var parameterName = $"@ProjectImageKey{index}";
            parameterNames.Add(parameterName);
            AddParameter(command, parameterName, imageKeys[index]);
        }

        command.CommandText =
            $"""
            DELETE FROM ProjectImage pi
            USING ProjectInfo pinfo
            WHERE pi.ProjectId = pinfo.Id
              AND pinfo.ProjectName = @ProjectName
              AND pi.ImageKey IN ({string.Join(", ", parameterNames)});
            """;
        AddParameter(command, "@ProjectName", projectName);
        return await command.ExecuteNonQueryAsync();
    }

    private string InferImageKind(string projectName, string imagePath)
    {
        var fullPath = Path.GetFullPath(imagePath);
        var capturesPath = Path.GetFullPath(_workspaceService.GetCapturesPath(projectName));
        var savedCropsPath = Path.GetFullPath(_workspaceService.GetSavedCropsPath(projectName));
        var datasetPath = Path.GetFullPath(_workspaceService.GetYoloDatasetPath(projectName));
        var testPath = Path.GetFullPath(_workspaceService.GetYoloTestPath(projectName));

        if (fullPath.StartsWith(capturesPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return "capture";
        }

        if (fullPath.StartsWith(savedCropsPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(imagePath).Contains("_var_", StringComparison.OrdinalIgnoreCase)
                ? "variation-crop"
                : "crop";
        }

        if (fullPath.StartsWith(Path.Combine(datasetPath, "images", "train") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return "dataset-train";
        }

        if (fullPath.StartsWith(Path.Combine(datasetPath, "images", "val") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return "dataset-val";
        }

        if (fullPath.StartsWith(Path.Combine(datasetPath, "images", "test") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return "dataset-test";
        }

        if (fullPath.StartsWith(testPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return "test";
        }

        return "file";
    }

    private string NormalizeImageKey(string projectName, string imagePathOrKey, string? imageKind)
    {
        if (ProjectAssetKey.IsLogicalKey(imagePathOrKey))
        {
            return imagePathOrKey;
        }

        return ProjectAssetKey.BuildProjectImageKey(
            _workspaceService,
            projectName,
            imageKind ?? InferImageKind(projectName, imagePathOrKey),
            imagePathOrKey);
    }

    private static string NormalizeKind(string imageKind)
    {
        return string.IsNullOrWhiteSpace(imageKind) ? "file" : imageKind.Trim().ToLowerInvariant();
    }

    private static bool IsBaseProjectImageKind(string imageKind)
    {
        return imageKind is "capture" or "dataset-train" or "dataset-val" or "dataset-test" or "test";
    }

    private static string ExtractKeyPrefix(string imageKey)
    {
        if (string.IsNullOrWhiteSpace(imageKey))
        {
            return string.Empty;
        }

        var separatorIndex = imageKey.IndexOf('|');
        return separatorIndex > 0 ? imageKey[..separatorIndex] : imageKey;
    }

    private static bool IsVariationSourceKey(string imageKey)
    {
        return imageKey.StartsWith("variation-source|", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVariationCropKey(string imageKey)
    {
        return imageKey.StartsWith("variation-crop|", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVariationKey(string imageKey)
    {
        return IsVariationSourceKey(imageKey) || IsVariationCropKey(imageKey);
    }

    private static bool IsNormalCropKey(string imageKey)
    {
        return imageKey.StartsWith("crop|", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBaseImageKey(string imageKey)
    {
        return !IsVariationKey(imageKey) && !IsNormalCropKey(imageKey);
    }

    public static byte[] Compress(byte[] inputBytes)
    {
        using var outputStream = new MemoryStream();
        using (var gzipStream = new GZipStream(outputStream, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzipStream.Write(inputBytes, 0, inputBytes.Length);
        }

        return outputStream.ToArray();
    }

    public static byte[] Decompress(byte[] compressedBytes)
    {
        using var inputStream = new MemoryStream(compressedBytes);
        using var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress);
        using var outputStream = new MemoryStream();
        gzipStream.CopyTo(outputStream);
        return outputStream.ToArray();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static IEnumerable<string> EnumerateProjectImages(string folderPath, bool includeSubdirectories = true)
    {
        if (!Directory.Exists(folderPath))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateFiles(folderPath, "*.*", includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(IsSupportedImagePath)
            .ToArray();
    }

    private static bool IsSupportedImagePath(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime ReadDateTimeValue(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return DateTime.MinValue;
        }

        var rawValue = reader.GetValue(ordinal);
        if (rawValue is DateTime dateTime)
        {
            return dateTime;
        }

        if (rawValue is DateTimeOffset dateTimeOffset)
        {
            return dateTimeOffset.UtcDateTime;
        }

        var text = Convert.ToString(rawValue, CultureInfo.InvariantCulture);
        if (DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedUtc))
        {
            return parsedUtc;
        }

        if (DateTime.TryParse(text, out var parsedLocal))
        {
            return parsedLocal;
        }

        throw new InvalidCastException($"Impossibile leggere un DateTime dal valore '{text}'.");
    }
}

public sealed record ProjectRestoreResult(string ProjectName, string ProjectPath, string YoloProjectPath, int RestoredImageCount, int RestoredModelCount);

internal sealed record ProjectImageReference(string ImageKey, string StorageKind);
public sealed record ProjectImageAssetRecord(long Id, string ProjectName, string ImageKey, string StorageKind);
public sealed record ProjectStoredImageRecord(string ImageKey, string StorageKind, byte[] OriginalBytes, DateTime UpdatedAtUtc);
