using System.Data.Common;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace WhatJolo;

internal sealed class ProjectImageBlobService
{
    private readonly AnnotationCropDbService _annotationCropDbService;
    private readonly ProjectWorkspaceService _workspaceService;

    public ProjectImageBlobService()
    {
        _annotationCropDbService = new AnnotationCropDbService();
        _workspaceService = new ProjectWorkspaceService();
    }

    public async Task SaveImageAsync(string projectName, string imagePath, string imageKind)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return;
        }

        EnsureTable();

        var fullPath = Path.GetFullPath(imagePath);
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
                ImagePath,
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
                @ImagePath,
                @ImageKind,
                @ContentHash,
                @ByteLength,
                @CompressedBytes,
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            )
            ON CONFLICT(ProjectName, ImagePath) DO UPDATE SET
                ImageKind = excluded.ImageKind,
                ContentHash = excluded.ContentHash,
                ByteLength = excluded.ByteLength,
                CompressedBytes = excluded.CompressedBytes,
                UpdatedAtUtc = CURRENT_TIMESTAMP;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@ImagePath", ConvertToStoredFileName(fullPath));
        AddParameter(command, "@ImageKind", imageKind);
        AddParameter(command, "@ContentHash", contentHash);
        AddParameter(command, "@ByteLength", originalBytes.Length);
        AddParameter(command, "@CompressedBytes", compressedBytes);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteImageAsync(string projectName, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        EnsureTable();

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM ProjectImageBlob
            WHERE ProjectName = @ProjectName
              AND ImagePath = @ImagePath;
            """;
        AddParameter(command, "@ProjectName", projectName);
        AddParameter(command, "@ImagePath", ConvertToStoredFileName(imagePath));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> SyncProjectAsync(string projectName)
    {
        EnsureTable();
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

        var projectCrops = await _annotationCropDbService.GetAllProjectCropsAsync(normalizedProjectName);
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
            var resolvedPath = ResolveStoredPath(normalizedProjectName, storedImage.ImagePath, storedImage.ImageKind);
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
        EnsureTable();
        var normalizedProjectName = _workspaceService.EnsureProject(projectName);
        Directory.CreateDirectory(_workspaceService.GetYoloProjectPath(normalizedProjectName));

        var restoredCount = 0;
        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ImagePath, ImageKind, CompressedBytes
            FROM ProjectImageBlob
            WHERE ProjectName = @ProjectName
            ORDER BY UpdatedAtUtc ASC, Id ASC;
            """;
        AddParameter(command, "@ProjectName", normalizedProjectName);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var storedPath = reader.GetString(0);
            var imageKind = reader.GetString(1);
            var resolvedPath = ResolveStoredPath(normalizedProjectName, storedPath, imageKind);
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

        return new ProjectRestoreResult(
            normalizedProjectName,
            _workspaceService.GetProjectPath(normalizedProjectName),
            _workspaceService.GetYoloProjectPath(normalizedProjectName),
            restoredCount);
    }

    private void EnsureTable()
    {
        if (!SharedDatabase.IsDatabaseConnected())
        {
            return;
        }

        using var connection = SharedDatabase.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ProjectImageBlob
            (
                Id BIGSERIAL PRIMARY KEY,
                ProjectName TEXT NOT NULL,
                ImagePath TEXT NOT NULL,
                ImageKind TEXT NOT NULL,
                ContentHash TEXT NOT NULL,
                ByteLength INTEGER NOT NULL,
                CompressedBytes BYTEA NOT NULL,
                CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UpdatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(ProjectName, ImagePath)
            );

            CREATE INDEX IF NOT EXISTS IX_ProjectImageBlob_ProjectName
                ON ProjectImageBlob(ProjectName, ImageKind, UpdatedAtUtc DESC);

            UPDATE ProjectImageBlob
            SET ImagePath = regexp_replace(ImagePath, '^.*[\\\/]', '')
            WHERE ImagePath LIKE '%\%' OR ImagePath LIKE '%/%';
            """;
        command.ExecuteNonQuery();
    }

    private async Task<IReadOnlyList<ProjectImageReference>> LoadProjectImagesAsync(string projectName)
    {
        var items = new List<ProjectImageReference>();
        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ImagePath, ImageKind
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

    private static string ConvertToStoredFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.GetFileName(path.Trim());
    }

    private string ResolveStoredPath(string projectName, string storedPath, string imageKind)
    {
        var fileName = ConvertToStoredFileName(storedPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        return imageKind switch
        {
            "capture" => Path.Combine(_workspaceService.GetCapturesPath(projectName), fileName),
            "variation-source" => Path.Combine(_workspaceService.GetCapturesPath(projectName), "Variations", fileName),
            "crop" or "variation-crop" => Path.Combine(
                _workspaceService.GetSavedCropsPath(projectName),
                ExtractLabelFromCropFileName(fileName),
                fileName),
            "dataset-train" => Path.Combine(_workspaceService.GetYoloDatasetPath(projectName), "images", "train", fileName),
            "dataset-val" => Path.Combine(_workspaceService.GetYoloDatasetPath(projectName), "images", "val", fileName),
            "dataset-test" => Path.Combine(_workspaceService.GetYoloDatasetPath(projectName), "images", "test", fileName),
            "test" => Path.Combine(_workspaceService.GetYoloTestPath(projectName), fileName),
            _ => Path.Combine(_workspaceService.GetProjectPath(projectName), fileName)
        };
    }

    private static string ExtractLabelFromCropFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var variationIndex = name.IndexOf("_var_", StringComparison.OrdinalIgnoreCase);
        if (variationIndex > 0)
        {
            return name[..variationIndex].Trim().ToLowerInvariant();
        }

        var timestampSeparatorIndex = name.IndexOf('_');
        return timestampSeparatorIndex > 0
            ? name[..timestampSeparatorIndex].Trim().ToLowerInvariant()
            : "crop";
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

internal sealed record ProjectRestoreResult(string ProjectName, string ProjectPath, string YoloProjectPath, int RestoredImageCount);

internal sealed record ProjectImageReference(string ImagePath, string ImageKind);
