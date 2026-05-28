using System.Data.Common;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace WhatJolo;

internal sealed class ProjectImageBlobService
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
        await EnsureImageAssetAsync(projectName, imagePath, imageKind);
    }

    public async Task<ProjectImageAssetRecord?> GetImageAssetByKeyAsync(string projectName, string imageKey)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(imageKey))
        {
            return null;
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ProjectName, ImageKey, ImageKind
            FROM ProjectImageBlob
            WHERE ProjectName = @ProjectName
              AND ImageKey = @ImageKey
            LIMIT 1;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@ImageKey", imageKey);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ProjectImageAssetRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
    }

    public async Task<ProjectImageAssetRecord> EnsureImageAssetAsync(string projectName, string imagePath, string imageKind)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            throw new FileNotFoundException("Immagine progetto non trovata.", imagePath);
        }

        var fullPath = Path.GetFullPath(imagePath);
        var imageKey = ProjectAssetKey.BuildProjectImageKey(_workspaceService, projectName, imageKind, fullPath);
        var originalBytes = await File.ReadAllBytesAsync(fullPath);
        var compressedBytes = Compress(originalBytes);
        var contentHash = Convert.ToHexString(SHA256.HashData(originalBytes));

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ProjectImageBlob
            (
                ProjectName,
                ImageKey,
                ImageKind,
                ContentHash,
                ByteLength,
                CompressedBytes,
                CreatedAtUtc,
                UpdatedAtUtc
            )
            VALUES
            (
                @ProjectName,
                @ImageKey,
                @ImageKind,
                @ContentHash,
                @ByteLength,
                @CompressedBytes,
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            )
            ON CONFLICT(ProjectName, ImageKey) DO UPDATE SET
                ImageKind = excluded.ImageKind,
                ContentHash = excluded.ContentHash,
                ByteLength = excluded.ByteLength,
                CompressedBytes = excluded.CompressedBytes,
                UpdatedAtUtc = CURRENT_TIMESTAMP;
        """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@ImageKey", imageKey);
        AddParameter(command, "@ImageKind", imageKind);
        AddParameter(command, "@ContentHash", contentHash);
        AddParameter(command, "@ByteLength", originalBytes.Length);
        AddParameter(command, "@CompressedBytes", compressedBytes);
        await command.ExecuteNonQueryAsync();

        var record = await GetImageAssetByKeyAsync(projectName, imageKey);
        if (record == null)
        {
            throw new InvalidOperationException($"Asset immagine non trovato dopo il salvataggio: {imageKey}");
        }

        return record;
    }

    public async Task DeleteImageAsync(string projectName, string imagePathOrKey, string? imageKind = null)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(imagePathOrKey))
        {
            return;
        }

        var imageKey = ProjectAssetKey.IsLogicalKey(imagePathOrKey)
            ? imagePathOrKey
            : ProjectAssetKey.BuildProjectImageKey(
                _workspaceService,
                projectName,
                imageKind ?? InferImageKind(projectName, imagePathOrKey),
                imagePathOrKey);

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM ProjectImageBlob
            WHERE ProjectName = @ProjectName
              AND ImageKey = @ImageKey;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@ImageKey", imageKey);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> SyncProjectAsync(string projectName)
    {
        var normalizedProjectName = _workspaceService.EnsureProject(projectName);
        var expectedImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var capturePath in EnumerateProjectImages(_workspaceService.GetCapturesPath(normalizedProjectName)))
        {
            var imageKind = capturePath.Contains($"{Path.DirectorySeparatorChar}Variations{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                ? "variation-source"
                : "capture";
            expectedImages[Path.GetFullPath(capturePath)] = imageKind;
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

        var annotationCropDbService = new AnnotationCropDbService();
        var projectCrops = await annotationCropDbService.GetAllProjectCropsAsync(normalizedProjectName);
        foreach (var cropRecord in projectCrops)
        {
            if (File.Exists(cropRecord.CropImagePath))
            {
                expectedImages[Path.GetFullPath(cropRecord.CropImagePath)] = cropRecord.IsVariation ? "variation-crop" : "crop";
            }

            if (cropRecord.IsVariation && File.Exists(cropRecord.SourceImagePath))
            {
                expectedImages[Path.GetFullPath(cropRecord.SourceImagePath)] = "variation-source";
            }
        }

        foreach (var image in expectedImages)
        {
            await SaveImageAsync(normalizedProjectName, image.Key, image.Value);
        }

        var deletedCount = 0;
        var existingBlobPaths = await LoadProjectImagesAsync(normalizedProjectName);
        foreach (var storedImage in existingBlobPaths)
        {
            var resolvedPath = ProjectAssetKey.ResolveProjectImagePath(_workspaceService, normalizedProjectName, storedImage.ImageKey, storedImage.ImageKind);
            if (expectedImages.ContainsKey(resolvedPath))
            {
                continue;
            }

            await DeleteImageAsync(normalizedProjectName, resolvedPath);
            deletedCount++;
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
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ImageKey, ImageKind, CompressedBytes
            FROM ProjectImageBlob
            WHERE ProjectName = @ProjectName
            ORDER BY UpdatedAtUtc ASC, Id ASC;
            """;
        AddParameter(command, "@ProjectName", normalizedProjectName);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var storedKey = reader.GetString(0);
            var imageKind = reader.GetString(1);
            var resolvedPath = ProjectAssetKey.ResolveProjectImagePath(_workspaceService, normalizedProjectName, storedKey, imageKind);
            var compressedBytes = (byte[])reader.GetValue(2);
            var originalBytes = Decompress(compressedBytes);

            var directoryPath = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            await File.WriteAllBytesAsync(resolvedPath, originalBytes);
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

    private async Task<IReadOnlyList<ProjectImageReference>> LoadProjectImagesAsync(string projectName)
    {
        var items = new List<ProjectImageReference>();
        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ImageKey, ImageKind
            FROM ProjectImageBlob
            WHERE ProjectName = @ProjectName;
            """;
        AddParameter(command, "@ProjectName", projectName);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new ProjectImageReference(reader.GetString(0), reader.GetString(1)));
        }

        return items;
    }

    private string InferImageKind(string projectName, string imagePath)
    {
        var fullPath = Path.GetFullPath(imagePath);
        var capturesPath = Path.GetFullPath(_workspaceService.GetCapturesPath(projectName));
        var variationsPath = Path.Combine(capturesPath, "Variations");
        var savedCropsPath = Path.GetFullPath(_workspaceService.GetSavedCropsPath(projectName));
        var datasetPath = Path.GetFullPath(_workspaceService.GetYoloDatasetPath(projectName));
        var testPath = Path.GetFullPath(_workspaceService.GetYoloTestPath(projectName));

        if (fullPath.StartsWith(Path.GetFullPath(variationsPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return "variation-source";
        }

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

    private static byte[] Compress(byte[] inputBytes)
    {
        using var outputStream = new MemoryStream();
        using (var gzipStream = new GZipStream(outputStream, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzipStream.Write(inputBytes, 0, inputBytes.Length);
        }

        return outputStream.ToArray();
    }

    private static byte[] Decompress(byte[] compressedBytes)
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

    private static IEnumerable<string> EnumerateProjectImages(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
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
}

internal sealed record ProjectRestoreResult(string ProjectName, string ProjectPath, string YoloProjectPath, int RestoredImageCount, int RestoredModelCount);

internal sealed record ProjectImageReference(string ImageKey, string ImageKind);
internal sealed record ProjectImageAssetRecord(long Id, string ProjectName, string ImageKey, string ImageKind);
