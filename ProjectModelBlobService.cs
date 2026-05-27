using System.Data.Common;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace WhatJolo;

public sealed class ProjectModelBlobService
{
    private readonly ProjectWorkspaceService _workspaceService = new();

    public async Task<ProjectModelBlobSaveResult> SaveBestOnnxAsync(string projectName, string className)
    {
        var normalizedProjectName = _workspaceService.EnsureProject(projectName);
        var normalizedClassName = NormalizeClassName(className);
        var onnxPath = _workspaceService.FindLatestYoloOnnxPath(normalizedProjectName, normalizedClassName);
        if (string.IsNullOrWhiteSpace(onnxPath) || !File.Exists(onnxPath))
        {
            throw new FileNotFoundException($"best.onnx non trovato per il progetto {normalizedProjectName}, classe {normalizedClassName}.");
        }

        EnsureTable();

        var originalBytes = await File.ReadAllBytesAsync(onnxPath);
        var compressedBytes = Compress(originalBytes);
        var contentHash = Convert.ToHexString(SHA256.HashData(originalBytes));
        var fileName = Path.GetFileName(onnxPath);
        var runName = new DirectoryInfo(Path.GetDirectoryName(Path.GetDirectoryName(onnxPath)!)!).Name;

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ProjectModelBlob
            (
                ProjectName,
                ClassName,
                ModelFileName,
                ModelKind,
                RunName,
                ContentHash,
                ByteLength,
                CompressedBytes,
                CreatedAtUtc,
                UpdatedAtUtc
            )
            VALUES
            (
                @ProjectName,
                @ClassName,
                @ModelFileName,
                @ModelKind,
                @RunName,
                @ContentHash,
                @ByteLength,
                @CompressedBytes,
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            )
            ON CONFLICT(ProjectName, ClassName, ModelKind) DO UPDATE SET
                ModelFileName = excluded.ModelFileName,
                RunName = excluded.RunName,
                ContentHash = excluded.ContentHash,
                ByteLength = excluded.ByteLength,
                CompressedBytes = excluded.CompressedBytes,
                UpdatedAtUtc = CURRENT_TIMESTAMP;
            """;
        AddParameter(command, "@ProjectName", normalizedProjectName);
        AddParameter(command, "@ClassName", normalizedClassName);
        AddParameter(command, "@ModelFileName", fileName);
        AddParameter(command, "@ModelKind", "best.onnx");
        AddParameter(command, "@RunName", runName);
        AddParameter(command, "@ContentHash", contentHash);
        AddParameter(command, "@ByteLength", originalBytes.Length);
        AddParameter(command, "@CompressedBytes", compressedBytes);
        await command.ExecuteNonQueryAsync();

        return new ProjectModelBlobSaveResult(
            normalizedProjectName,
            normalizedClassName,
            onnxPath,
            fileName,
            runName,
            originalBytes.Length,
            compressedBytes.Length,
            contentHash);
    }

    public async Task<ProjectModelBlobRestoreResult> RestoreLatestBestOnnxToTempAsync(string projectName, string className)
    {
        var normalizedProjectName = _workspaceService.EnsureProject(projectName);
        var normalizedClassName = NormalizeClassName(className);
        EnsureTable();

        if (!SharedDatabase.IsDatabaseConnected())
        {
            throw new InvalidOperationException("Database non connesso. Apri la tab Istanza DB e premi Connetti.");
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ModelFileName, RunName, ContentHash, ByteLength, CompressedBytes
            FROM ProjectModelBlob
            WHERE ProjectName = @ProjectName
              AND ClassName = @ClassName
              AND ModelKind = @ModelKind
            ORDER BY UpdatedAtUtc DESC, Id DESC
            LIMIT 1;
            """;
        AddParameter(command, "@ProjectName", normalizedProjectName);
        AddParameter(command, "@ClassName", normalizedClassName);
        AddParameter(command, "@ModelKind", "best.onnx");

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new FileNotFoundException($"Nessun best.onnx salvato nel DB per il progetto {normalizedProjectName}, classe {normalizedClassName}.");
        }

        var modelFileName = reader.GetString(0);
        var runName = reader.GetString(1);
        var contentHash = reader.GetString(2);
        var byteLength = reader.GetInt32(3);
        var compressedBytes = (byte[])reader["CompressedBytes"];
        var originalBytes = Decompress(compressedBytes);
        if (originalBytes.Length != byteLength)
        {
            throw new InvalidDataException("La dimensione del modello ONNX salvato nel DB non corrisponde ai metadati.");
        }

        var restoredHash = Convert.ToHexString(SHA256.HashData(originalBytes));
        if (!string.Equals(restoredHash, contentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Il modello ONNX salvato nel DB non supera il controllo hash.");
        }

        var tempFolder = Path.Combine(
            Path.GetTempPath(),
            "WhatJolo",
            "ProjectModelBlob",
            SanitizePathSegment(normalizedProjectName),
            SanitizePathSegment(normalizedClassName),
            contentHash[..Math.Min(contentHash.Length, 16)]);
        Directory.CreateDirectory(tempFolder);

        var outputPath = Path.Combine(tempFolder, string.IsNullOrWhiteSpace(modelFileName) ? "best.onnx" : modelFileName);
        await File.WriteAllBytesAsync(outputPath, originalBytes);
        WriteProjectLabelsBesideModel(normalizedProjectName, normalizedClassName, outputPath);

        return new ProjectModelBlobRestoreResult(
            normalizedProjectName,
            normalizedClassName,
            outputPath,
            modelFileName,
            runName,
            byteLength,
            compressedBytes.Length,
            contentHash);
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

    private static byte[] Decompress(byte[] inputBytes)
    {
        using var inputStream = new MemoryStream(inputBytes);
        using var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress);
        using var outputStream = new MemoryStream();
        gzipStream.CopyTo(outputStream);
        return outputStream.ToArray();
    }

    private void WriteProjectLabelsBesideModel(string projectName, string className, string modelPath)
    {
        var classesPath = Path.Combine(_workspaceService.GetYoloDatasetPath(projectName, className), "classes.txt");
        if (!File.Exists(classesPath))
        {
            return;
        }

        var labelsPath = Path.ChangeExtension(modelPath, ".txt");
        File.Copy(classesPath, labelsPath, overwrite: true);
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeChars = value
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray();
        var safeValue = new string(safeChars).Trim();
        return string.IsNullOrWhiteSpace(safeValue) ? "Project" : safeValue;
    }

    private static string NormalizeClassName(string className)
    {
        var normalizedClassName = className.Trim();
        if (string.IsNullOrWhiteSpace(normalizedClassName))
        {
            throw new ArgumentException("Classe modello non valida.", nameof(className));
        }

        return normalizedClassName;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void EnsureTable()
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
            CREATE TABLE IF NOT EXISTS ProjectModelBlob
            (
                Id BIGSERIAL PRIMARY KEY,
                ProjectName TEXT NOT NULL,
                ClassName TEXT NOT NULL DEFAULT '',
                ModelFileName TEXT NOT NULL,
                ModelKind TEXT NOT NULL,
                RunName TEXT NOT NULL,
                ContentHash TEXT NOT NULL,
                ByteLength INTEGER NOT NULL,
                CompressedBytes BYTEA NOT NULL,
                CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UpdatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(ProjectName, ModelKind)
            );

            ALTER TABLE ProjectModelBlob
                ADD COLUMN IF NOT EXISTS ClassName TEXT NOT NULL DEFAULT '';

            ALTER TABLE ProjectModelBlob
                DROP CONSTRAINT IF EXISTS projectmodelblob_projectname_modelkind_key;

            CREATE UNIQUE INDEX IF NOT EXISTS IX_ProjectModelBlob_ProjectName_ClassName_ModelKind
                ON ProjectModelBlob(ProjectName, ClassName, ModelKind);

            CREATE INDEX IF NOT EXISTS IX_ProjectModelBlob_ProjectName
                ON ProjectModelBlob(ProjectName, ClassName, UpdatedAtUtc DESC);
            """;
        command.ExecuteNonQuery();
    }
}

public sealed record ProjectModelBlobSaveResult(
    string ProjectName,
    string ClassName,
    string ModelPath,
    string ModelFileName,
    string RunName,
    int ByteLength,
    int CompressedLength,
    string ContentHash);

public sealed record ProjectModelBlobRestoreResult(
    string ProjectName,
    string ClassName,
    string ModelPath,
    string ModelFileName,
    string RunName,
    int ByteLength,
    int CompressedLength,
    string ContentHash);
