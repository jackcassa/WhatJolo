using System.IO;

namespace WhatJolo;

internal static class ProjectAssetKey
{
    public static bool IsLogicalKey(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Contains('|', StringComparison.Ordinal);
    }

    public static string BuildSourceImageKey(ProjectWorkspaceService workspaceService, string projectName, string localPath)
    {
        var fileName = NormalizeFileName(localPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var capturesPath = workspaceService.GetCapturesPath(projectName);
        var variationsPath = Path.Combine(capturesPath, "Variations");
        var fullPath = Path.GetFullPath(localPath);
        var fullVariationsPath = Path.GetFullPath(variationsPath) + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(fullVariationsPath, StringComparison.OrdinalIgnoreCase)
            ? $"variation-source|{fileName}"
            : $"capture|{fileName}";
    }

    public static string BuildCropImageKey(ProjectWorkspaceService workspaceService, string projectName, string labelName, bool isVariation, string localPath)
    {
        var safeLabel = NormalizeLabel(labelName);
        var fileName = NormalizeFileName(localPath);
        var kind = isVariation ? "variation-crop" : "crop";
        return $"{kind}|{safeLabel}|{fileName}";
    }

    public static string BuildProjectImageKey(ProjectWorkspaceService workspaceService, string projectName, string imageKind, string localPath)
    {
        var normalizedKind = string.IsNullOrWhiteSpace(imageKind) ? "file" : imageKind.Trim().ToLowerInvariant();
        var fileName = NormalizeFileName(localPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        return normalizedKind switch
        {
            "capture" => $"capture|{fileName}",
            "variation-source" => $"variation-source|{fileName}",
            "crop" => $"crop|{ExtractCropLabel(workspaceService, projectName, localPath)}|{fileName}",
            "variation-crop" => $"variation-crop|{ExtractCropLabel(workspaceService, projectName, localPath)}|{fileName}",
            "dataset-train" => $"dataset-train|{fileName}",
            "dataset-val" => $"dataset-val|{fileName}",
            "dataset-test" => $"dataset-test|{fileName}",
            "test" => $"test|{fileName}",
            _ => $"{normalizedKind}|{fileName}"
        };
    }

    public static string ResolveSourceImagePath(ProjectWorkspaceService workspaceService, string projectName, string sourceImageKey)
    {
        var parts = SplitKey(sourceImageKey, 2);
        if (parts.Length < 2)
        {
            return string.Empty;
        }

        return parts[0] switch
        {
            "capture" => Path.Combine(workspaceService.GetCapturesPath(projectName), parts[1]),
            "variation-source" => Path.Combine(workspaceService.GetCapturesPath(projectName), "Variations", parts[1]),
            _ => string.Empty
        };
    }

    public static string ResolveCropImagePath(ProjectWorkspaceService workspaceService, string projectName, string cropImageKey)
    {
        var parts = SplitKey(cropImageKey, 3);
        if (parts.Length < 3)
        {
            return string.Empty;
        }

        return parts[0] switch
        {
            "crop" or "variation-crop" => Path.Combine(workspaceService.GetSavedCropsPath(projectName), parts[1], parts[2]),
            _ => string.Empty
        };
    }

    public static string ResolveProjectImagePath(ProjectWorkspaceService workspaceService, string projectName, string imageKey, string imageKind)
    {
        var parts = SplitKey(imageKey, 2);
        if (parts.Length < 2)
        {
            return string.Empty;
        }

        return parts[0] switch
        {
            "capture" => Path.Combine(workspaceService.GetCapturesPath(projectName), parts[1]),
            "variation-source" => Path.Combine(workspaceService.GetCapturesPath(projectName), "Variations", parts[1]),
            "crop" or "variation-crop" when parts.Length >= 3 => Path.Combine(workspaceService.GetSavedCropsPath(projectName), parts[1], parts[2]),
            "dataset-train" => Path.Combine(workspaceService.GetYoloDatasetPath(projectName), "images", "train", parts[1]),
            "dataset-val" => Path.Combine(workspaceService.GetYoloDatasetPath(projectName), "images", "val", parts[1]),
            "dataset-test" => Path.Combine(workspaceService.GetYoloDatasetPath(projectName), "images", "test", parts[1]),
            "test" => Path.Combine(workspaceService.GetYoloTestPath(projectName), parts[1]),
            _ => Path.Combine(workspaceService.GetProjectPath(projectName), parts[^1])
        };
    }

    public static string NormalizeFileName(string pathOrFileName)
    {
        if (string.IsNullOrWhiteSpace(pathOrFileName))
        {
            return string.Empty;
        }

        return Path.GetFileName(pathOrFileName.Trim());
    }

    public static string NormalizeLabel(string? labelName)
    {
        return string.IsNullOrWhiteSpace(labelName)
            ? "crop"
            : labelName.Trim().ToLowerInvariant();
    }

    public static string BuildLegacySourceImageKey(string storedSourceImagePath)
    {
        var fileName = NormalizeFileName(storedSourceImagePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        return fileName.StartsWith("adb_capture_var_", StringComparison.OrdinalIgnoreCase)
            ? $"variation-source|{fileName}"
            : $"capture|{fileName}";
    }

    public static string BuildLegacyCropImageKey(string labelName, bool isVariation, string storedCropImagePath)
    {
        var safeLabel = NormalizeLabel(labelName);
        var fileName = NormalizeFileName(storedCropImagePath);
        var kind = isVariation ? "variation-crop" : "crop";
        return $"{kind}|{safeLabel}|{fileName}";
    }

    private static string ExtractCropLabel(ProjectWorkspaceService workspaceService, string projectName, string localPath)
    {
        var savedCropsRoot = Path.GetFullPath(workspaceService.GetSavedCropsPath(projectName));
        var fullPath = Path.GetFullPath(localPath);
        if (fullPath.StartsWith(savedCropsRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(savedCropsRoot, fullPath);
            var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                return NormalizeLabel(parts[0]);
            }
        }

        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "crop";
        }

        var variationIndex = fileName.IndexOf("_var_", StringComparison.OrdinalIgnoreCase);
        if (variationIndex > 0)
        {
            return NormalizeLabel(fileName[..variationIndex]);
        }

        var separatorIndex = fileName.IndexOf('_');
        return separatorIndex > 0
            ? NormalizeLabel(fileName[..separatorIndex])
            : "crop";
    }

    private static string[] SplitKey(string key, int minimumPartCount)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Array.Empty<string>();
        }

        var parts = key.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= minimumPartCount ? parts : Array.Empty<string>();
    }
}
