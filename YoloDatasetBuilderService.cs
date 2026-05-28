using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace WhatJolo;

internal sealed class YoloDatasetBuilderService
{
    private readonly AnnotationCropDbService _annotationCropDbService;
    private readonly ProjectImageBlobService _projectImageBlobService;
    private readonly ProjectWorkspaceService _workspaceService;

    public YoloDatasetBuilderService()
    {
        _annotationCropDbService = new AnnotationCropDbService();
        _projectImageBlobService = new ProjectImageBlobService();
        _workspaceService = new ProjectWorkspaceService();
    }

    public async Task<YoloDatasetBuildResult> BuildAsync(string projectName, IReadOnlyList<string> classNames)
    {
        var normalizedClasses = classNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedClasses.Length == 0)
        {
            throw new InvalidOperationException("Nessuna classe attiva selezionata per il progetto.");
        }

        if (normalizedClasses.Length != 1)
        {
            throw new InvalidOperationException("Il dataset YOLO ora viene creato per una sola classe alla volta.");
        }

        var targetClassName = normalizedClasses[0];

        var records = await _annotationCropDbService.GetProjectCropsAsync(projectName, normalizedClasses);

        var datasetFolder = _workspaceService.CreateYoloDatasetStructure(projectName, targetClassName);
        var imagesTrainFolder = Path.Combine(datasetFolder, "images", "train");
        var imagesValFolder = Path.Combine(datasetFolder, "images", "val");
        var labelsTrainFolder = Path.Combine(datasetFolder, "labels", "train");
        var labelsValFolder = Path.Combine(datasetFolder, "labels", "val");
        var imageMapPath = Path.Combine(datasetFolder, "image_map.tsv");

        await ClearFolderAsync(imagesTrainFolder, projectName);
        await ClearFolderAsync(imagesValFolder, projectName);
        ClearFolder(labelsTrainFolder);
        ClearFolder(labelsValFolder);

        var classMap = normalizedClasses
            .Select((label, index) => new { label, index })
            .ToDictionary(item => item.label, item => item.index, StringComparer.OrdinalIgnoreCase);

        File.WriteAllLines(Path.Combine(datasetFolder, "classes.txt"), normalizedClasses, Encoding.UTF8);
        var imageMapLines = new List<string> { "split\tdatasetImagePath\tsourceImageKey" };

        var projectCaptureImages = await _projectImageBlobService.GetProjectImagesByKindsAsync(projectName, "capture");
        var allSourceImages = projectCaptureImages
            .Select(static image => image.ImageKey)
            .Concat(records.Select(static record => record.SourceImageKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allSourceImages.Length == 0)
        {
            throw new InvalidOperationException("Nessuna immagine disponibile nelle captures o nelle annotazioni del progetto.");
        }

        var recordsBySourceImage = records
            .GroupBy(record => record.SourceImageKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var imageCount = allSourceImages.Length;
        var validationStartIndex = imageCount > 1 ? Math.Max(1, imageCount - Math.Max(1, (int)Math.Round(imageCount * 0.2d))) : imageCount;

        for (var index = 0; index < allSourceImages.Length; index++)
        {
            var sourceImageKey = allSourceImages[index];
            var isValidation = imageCount > 1 && index >= validationStartIndex;
            var imageTargetFolder = isValidation ? imagesValFolder : imagesTrainFolder;
            var labelTargetFolder = isValidation ? labelsValFolder : labelsTrainFolder;

            var groupRecords = recordsBySourceImage.TryGetValue(sourceImageKey, out var matchedRecords)
                ? matchedRecords
                : Array.Empty<ProjectCropRecord>();

            var sourceImageBytes = await _projectImageBlobService.GetImageBytesByKeyAsync(projectName, sourceImageKey);
            if (sourceImageBytes == null || sourceImageBytes.Length == 0)
            {
                continue;
            }

            using var imageStream = new MemoryStream(sourceImageBytes);
            var bitmapFrame = BitmapFrame.Create(imageStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var imageWidth = bitmapFrame.PixelWidth;
            var imageHeight = bitmapFrame.PixelHeight;
            if (imageWidth <= 0 || imageHeight <= 0)
            {
                continue;
            }

            var sourceFileName = BuildSourceFileName(sourceImageKey);
            var fileStem = BuildDatasetImageStem(index, sourceFileName);
            var extension = Path.GetExtension(sourceFileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            var targetImagePath = Path.Combine(imageTargetFolder, fileStem + extension.ToLowerInvariant());
            await File.WriteAllBytesAsync(targetImagePath, sourceImageBytes);
            await _projectImageBlobService.SaveImageAsync(projectName, targetImagePath, isValidation ? "dataset-val" : "dataset-train");
            imageMapLines.Add($"{(isValidation ? "val" : "train")}\t{targetImagePath}\t{sourceImageKey}");

            var yoloLines = new List<string>();
            foreach (var record in groupRecords)
            {
                if (!classMap.TryGetValue(record.LabelName.Trim(), out var classId))
                {
                    continue;
                }

                var clippedLeft = Math.Max(0, record.X);
                var clippedTop = Math.Max(0, record.Y);
                var clippedRight = Math.Min(imageWidth, record.X + record.Width);
                var clippedBottom = Math.Min(imageHeight, record.Y + record.Height);
                var clippedWidth = clippedRight - clippedLeft;
                var clippedHeight = clippedBottom - clippedTop;

                if (clippedWidth <= 0 || clippedHeight <= 0)
                {
                    continue;
                }

                var xCenter = (clippedLeft + (clippedWidth / 2d)) / imageWidth;
                var yCenter = (clippedTop + (clippedHeight / 2d)) / imageHeight;
                var width = clippedWidth / (double)imageWidth;
                var height = clippedHeight / (double)imageHeight;

                yoloLines.Add(
                    classId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " +
                    xCenter.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " " +
                    yCenter.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " " +
                    width.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " " +
                    height.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
            }

            var labelPath = Path.Combine(labelTargetFolder, fileStem + ".txt");
            File.WriteAllLines(labelPath, yoloLines, Encoding.ASCII);
        }

        var dataYamlPath = Path.Combine(datasetFolder, "data.yaml");
        var yaml = new StringBuilder();
        yaml.AppendLine("path: " + datasetFolder.Replace("\\", "/"));
        yaml.AppendLine("train: images/train");
        yaml.AppendLine("val: images/val");
        yaml.AppendLine("names:");
        for (var index = 0; index < normalizedClasses.Length; index++)
        {
            yaml.AppendLine($"  {index}: {normalizedClasses[index]}");
        }

        File.WriteAllText(dataYamlPath, yaml.ToString(), new UTF8Encoding(false));
        File.WriteAllLines(imageMapPath, imageMapLines, new UTF8Encoding(false));

        return new YoloDatasetBuildResult(datasetFolder, imageCount, normalizedClasses.Length);
    }

    public async Task<YoloDatasetBuildResult> BuildTestFromDatabaseAsync(string projectName, IReadOnlyList<string> classNames)
    {
        var normalizedClasses = classNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedClasses.Length == 0)
        {
            throw new InvalidOperationException("Nessuna classe attiva selezionata per il progetto.");
        }

        if (normalizedClasses.Length != 1)
        {
            throw new InvalidOperationException("Il dataset test YOLO ora viene creato per una sola classe alla volta.");
        }

        var targetClassName = normalizedClasses[0];

        var records = await _annotationCropDbService.GetProjectCropsAsync(projectName, normalizedClasses);
        if (records.Count == 0)
        {
            throw new InvalidOperationException("Nessuna annotazione trovata nel DB per il progetto corrente.");
        }

        var datasetFolder = _workspaceService.CreateYoloDatasetStructure(projectName, targetClassName);
        var imagesTestFolder = Path.Combine(datasetFolder, "images", "test");
        var labelsTestFolder = Path.Combine(datasetFolder, "labels", "test");
        var imageMapPath = Path.Combine(datasetFolder, "image_map.tsv");

        await ClearFolderAsync(imagesTestFolder, projectName);
        ClearFolder(labelsTestFolder);

        var classMap = normalizedClasses
            .Select((label, index) => new { label, index })
            .ToDictionary(item => item.label, item => item.index, StringComparer.OrdinalIgnoreCase);

        var recordsBySourceImage = records
            .GroupBy(record => record.SourceImageKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var allSourceImages = recordsBySourceImage.Keys
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allSourceImages.Length == 0)
        {
            throw new InvalidOperationException("Le immagini sorgente annotate del progetto non sono disponibili nel DB.");
        }

        var imageMapLines = File.Exists(imageMapPath)
            ? File.ReadAllLines(imageMapPath, Encoding.UTF8)
                .Where(static line => !line.StartsWith("test\t", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : new List<string>();

        if (imageMapLines.Count == 0)
        {
            imageMapLines.Add("split\tdatasetImagePath\tsourceImageKey");
        }

        for (var index = 0; index < allSourceImages.Length; index++)
        {
            var sourceImageKey = allSourceImages[index];
            var groupRecords = recordsBySourceImage[sourceImageKey];

            var sourceImageBytes = await _projectImageBlobService.GetImageBytesByKeyAsync(projectName, sourceImageKey);
            if (sourceImageBytes == null || sourceImageBytes.Length == 0)
            {
                continue;
            }

            using var imageStream = new MemoryStream(sourceImageBytes);
            var bitmapFrame = BitmapFrame.Create(imageStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var imageWidth = bitmapFrame.PixelWidth;
            var imageHeight = bitmapFrame.PixelHeight;
            if (imageWidth <= 0 || imageHeight <= 0)
            {
                continue;
            }

            var sourceFileName = BuildSourceFileName(sourceImageKey);
            var fileStem = BuildDatasetImageStem(index, sourceFileName);
            var extension = Path.GetExtension(sourceFileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            var targetImagePath = Path.Combine(imagesTestFolder, fileStem + extension.ToLowerInvariant());
            await File.WriteAllBytesAsync(targetImagePath, sourceImageBytes);
            await _projectImageBlobService.SaveImageAsync(projectName, targetImagePath, "dataset-test");
            imageMapLines.Add($"test\t{targetImagePath}\t{sourceImageKey}");

            var yoloLines = new List<string>();
            foreach (var record in groupRecords)
            {
                if (!classMap.TryGetValue(record.LabelName.Trim(), out var classId))
                {
                    continue;
                }

                var clippedLeft = Math.Max(0, record.X);
                var clippedTop = Math.Max(0, record.Y);
                var clippedRight = Math.Min(imageWidth, record.X + record.Width);
                var clippedBottom = Math.Min(imageHeight, record.Y + record.Height);
                var clippedWidth = clippedRight - clippedLeft;
                var clippedHeight = clippedBottom - clippedTop;

                if (clippedWidth <= 0 || clippedHeight <= 0)
                {
                    continue;
                }

                var xCenter = (clippedLeft + (clippedWidth / 2d)) / imageWidth;
                var yCenter = (clippedTop + (clippedHeight / 2d)) / imageHeight;
                var width = clippedWidth / (double)imageWidth;
                var height = clippedHeight / (double)imageHeight;

                yoloLines.Add(
                    classId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " +
                    xCenter.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " " +
                    yCenter.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " " +
                    width.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " " +
                    height.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
            }

            var labelPath = Path.Combine(labelsTestFolder, fileStem + ".txt");
            File.WriteAllLines(labelPath, yoloLines, Encoding.ASCII);
        }

        File.WriteAllLines(imageMapPath, imageMapLines, new UTF8Encoding(false));
        return new YoloDatasetBuildResult(datasetFolder, allSourceImages.Length, normalizedClasses.Length);
    }

    private async Task ClearFolderAsync(string folderPath, string projectName)
    {
        Directory.CreateDirectory(folderPath);
        foreach (var filePath in Directory.GetFiles(folderPath))
        {
            File.Delete(filePath);
            await _projectImageBlobService.DeleteImageAsync(projectName, filePath);
        }
    }

    private static void ClearFolder(string folderPath)
    {
        Directory.CreateDirectory(folderPath);
        foreach (var filePath in Directory.GetFiles(folderPath))
        {
            File.Delete(filePath);
        }
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

    private static string BuildDatasetImageStem(int index, string sourceImagePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourceImagePath);
        var safeName = Regex.Replace(baseName, "[^a-zA-Z0-9_-]+", "_").Trim('_');
        if (safeName.Length == 0)
        {
            safeName = "image";
        }

        return $"{index + 1:D4}_{safeName}";
    }

    private static string BuildSourceFileName(string imageKey)
    {
        var parts = imageKey.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            >= 3 => parts[2],
            2 => parts[1],
            _ => imageKey
        };
    }
}

internal sealed record YoloDatasetBuildResult(string DatasetFolder, int ImageCount, int ClassCount);
